namespace PhoneRemote.Core;

/// <summary>What the controller learnt this second (every field optional: missing = unknown).</summary>
public sealed record LinkSample(
    double? RembBps = null,      // receiver's own estimate (WebRTC REMB)
    double? LossPercent = null,  // packets lost (RTCP RR / TWCC) or frames dropped on the WebSocket queue
    double? DelayDeltaMs = null, // one-way delay growth (TWCC) or RTT growth (pong)
    int? ViewerFps = null,       // frames the viewer decoded
    int SentKbps = 0,            // what we actually sent
    int SentFps = 0);

public enum BandwidthAction { None, Down, Up, FpsDown, FpsUp }

/// <summary>
/// Picks the bitrate (and, at the floor, the frame rate) from what the link tells us. AIMD with hysteresis:
/// down at once when the estimate falls or loss appears, up slowly after five clean seconds; never more than
/// one change every 10 s except an emergency drop under heavy loss. Pure: one call per second.
/// </summary>
public sealed class BandwidthController
{
    private readonly int _floor, _ceiling, _maxFps;
    private int _kbps, _fps;
    private int _cleanSeconds, _downsAtFloor, _cleanSinceFpsDown;
    private double _lastChangeAt = -100, _now;
    private double _lossEma, _delayEma, _lastRemb = double.NaN, _rembAt = -100;

    public int Kbps => _kbps;
    public int Fps => _fps;
    public double EstimateKbps { get; private set; }
    public string EstimateSource { get; private set; } = "none";

    public BandwidthController(EncodePlan plan)
    {
        _floor = plan.MinKbps; _ceiling = Math.Max(plan.MinKbps, plan.MaxKbps);
        _kbps = Math.Clamp(plan.Kbps, _floor, _ceiling);
        _fps = _maxFps = plan.Fps;
    }

    /// <summary>One second of observations. Returns what to do; <see cref="Kbps"/> / <see cref="Fps"/> hold the new values.</summary>
    public BandwidthAction Feed(LinkSample s, double nowSeconds)
    {
        _now = nowSeconds;
        if (s.RembBps is { } remb && remb > 0) { _lastRemb = remb / 1000.0; _rembAt = nowSeconds; }
        if (s.LossPercent is { } loss) _lossEma = _lossEma * 0.6 + loss * 0.4;
        if (s.DelayDeltaMs is { } d) _delayEma = _delayEma * 0.7 + d * 0.3;
        bool congested = _lossEma > 3 || _delayEma > 50;
        bool heavyLoss = _lossEma > 10;
        bool starving = s.ViewerFps is { } vf && s.SentFps > 0 && vf < s.SentFps * 0.6 && s.SentKbps > 0;

        double? est = null;
        if (nowSeconds - _rembAt < 3 && !double.IsNaN(_lastRemb)) { est = _lastRemb; EstimateSource = "remb"; }
        else if (congested && s.SentKbps > 0) { est = s.SentKbps * 0.8; EstimateSource = _lossEma > 3 ? "loss" : "delay"; }
        else if (starving) { est = s.SentKbps * 0.7; EstimateSource = "viewer"; }
        else EstimateSource = "none";
        if (est is { } e) EstimateKbps = e;

        double sinceChange = nowSeconds - _lastChangeAt;
        int target = est is { } t ? (int)Math.Clamp(t * 0.85, _floor, _ceiling) : _kbps;

        // a receiver estimate wobbles: only a clear drop counts; loss or delay growth: any drop counts
        bool wantDown = est != null && (EstimateSource == "remb" ? target < _kbps * 0.8 : target < _kbps);
        bool starvingAtFloor = est is { } e3 && _kbps == _floor && e3 * 0.85 < _floor;
        if ((wantDown || starvingAtFloor) && (sinceChange >= 10 || (heavyLoss && sinceChange >= 2)))
        {
            _cleanSeconds = 0;
            _lastChangeAt = nowSeconds;
            if (wantDown) _kbps = Math.Max(_floor, target);
            if (_kbps == _floor) _downsAtFloor++;
            if (_downsAtFloor >= 3 && _fps > 20) { _fps = _fps > 30 ? 30 : 20; _downsAtFloor = 0; _cleanSinceFpsDown = 0; return BandwidthAction.FpsDown; }
            return wantDown ? BandwidthAction.Down : BandwidthAction.None;
        }
        if (est == null || target >= _kbps) _cleanSeconds++; else _cleanSeconds = 0;
        if (!congested && !starving) _cleanSinceFpsDown++; else _cleanSinceFpsDown = 0;

        if (_fps < _maxFps && _cleanSinceFpsDown >= 20 && sinceChange >= 10)
        {
            _fps = _maxFps;
            _lastChangeAt = nowSeconds;
            _cleanSinceFpsDown = 0;
            return BandwidthAction.FpsUp;
        }
        if (_cleanSeconds >= 5 && sinceChange >= 10 && _kbps < _ceiling && (est == null ? _lossEma < 1 && _delayEma < 20 : target > _kbps * 1.25))
        {
            int up = (int)Math.Min(_ceiling, Math.Max(_kbps * 1.25, _kbps + 250));
            if (est is { } e2) up = Math.Min(up, Math.Max(_kbps, (int)(e2 * 0.85)));
            if (up > _kbps)
            {
                _kbps = up;
                _cleanSeconds = 0;
                _lastChangeAt = nowSeconds;
                _downsAtFloor = 0;
                return BandwidthAction.Up;
            }
        }
        return BandwidthAction.None;
    }
}
