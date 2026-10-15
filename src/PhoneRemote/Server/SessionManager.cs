using System.Security.Cryptography;

namespace PhoneRemote.Server;

public enum PinResult { Ok, Wrong, Locked, Busy }

/// <summary>
/// Holds the secret link token, the PIN shown on the PC, and the single authorised phone session.
/// Everything is regenerated on each launch and whenever the link is reset.
/// </summary>
public sealed class SessionManager
{
    public const int MaxAttempts = 5;

    private readonly object _gate = new();
    private string? _sessionId;

    public string Token { get; private set; } = "";
    public string Pin { get; private set; } = "";
    public int FailedAttempts { get; private set; }
    public bool PhoneConnected { get; private set; }
    public DateTime? ConnectedSince { get; private set; }
    public string? PhoneName { get; private set; }

    /// <summary>Raised whenever anything the desktop UI displays changes.</summary>
    public event Action? Changed;

    /// <summary>Raised when the current phone must be kicked out (link reset or "Déconnecter").</summary>
    public event Action? SessionRevoked;

    public SessionManager() => Reset(notify: false);

    public void Reset() => Reset(notify: true);

    private void Reset(bool notify)
    {
        bool hadSession;
        lock (_gate)
        {
            hadSession = _sessionId != null;
            Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            Pin = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
            FailedAttempts = 0;
            _sessionId = null;
            PhoneConnected = false;
            ConnectedSince = null;
            PhoneName = null;
        }
        if (hadSession) SessionRevoked?.Invoke();
        if (notify) Changed?.Invoke();
    }

    public bool IsToken(string token) =>
        token.Length == Token.Length &&
        CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.ASCII.GetBytes(token), System.Text.Encoding.ASCII.GetBytes(Token));

    public bool IsSession(string? sessionId)
    {
        lock (_gate)
        {
            return sessionId != null && _sessionId != null && sessionId.Length == _sessionId.Length &&
                   CryptographicOperations.FixedTimeEquals(
                       System.Text.Encoding.ASCII.GetBytes(sessionId), System.Text.Encoding.ASCII.GetBytes(_sessionId));
        }
    }

    public PinResult TryPin(string pin, out string? sessionId)
    {
        sessionId = null;
        bool locked = false;
        PinResult result;
        lock (_gate)
        {
            if (PhoneConnected)
            {
                result = PinResult.Busy;
            }
            else if (pin.Length == 6 && CryptographicOperations.FixedTimeEquals(
                         System.Text.Encoding.ASCII.GetBytes(pin), System.Text.Encoding.ASCII.GetBytes(Pin)))
            {
                _sessionId = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
                sessionId = _sessionId;
                FailedAttempts = 0;
                result = PinResult.Ok;
            }
            else
            {
                FailedAttempts++;
                locked = FailedAttempts >= MaxAttempts;
                result = locked ? PinResult.Locked : PinResult.Wrong;
            }
        }
        if (locked) Reset();
        else Changed?.Invoke();
        return result;
    }

    public void SetConnected(bool connected, string? phoneName = null)
    {
        lock (_gate)
        {
            PhoneConnected = connected;
            ConnectedSince = connected ? DateTime.Now : null;
            PhoneName = connected ? phoneName : null;
        }
        Changed?.Invoke();
    }
}
