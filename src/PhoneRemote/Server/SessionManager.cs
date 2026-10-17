using System.Security.Cryptography;

namespace PhoneRemote.Server;

public enum PinResult { Ok, Wrong, Locked, Busy }

/// <summary>
/// Holds the secret link token, the PIN, the one-time QR key and the single authorised phone session.
/// The link token and QR key are new on each launch and whenever the link is reset. The PIN is the one the
/// user chose (kept between launches by <see cref="PinStore"/>); until they choose one, a random PIN is used.
/// </summary>
public sealed class SessionManager
{
    public const int MaxAttempts = 5;

    private readonly object _gate = new();
    private string? _sessionId;

    public string Token { get; private set; } = "";
    public string Pin { get; private set; } = "";
    /// <summary>The user chose this PIN (otherwise it is random and changes on each launch).</summary>
    public bool PinIsPersonal { get; private set; }
    /// <summary>Secret carried by the QR code: scanning it connects without typing the PIN. Single use.</summary>
    public string QrKey { get; private set; } = "";
    public int FailedAttempts { get; private set; }
    public bool PhoneConnected { get; private set; }
    public DateTime? ConnectedSince { get; private set; }
    public string? PhoneName { get; private set; }

    /// <summary>Raised whenever anything the desktop UI displays changes.</summary>
    public event Action? Changed;

    /// <summary>Raised when the current phone must be kicked out (link reset or "Déconnecter").</summary>
    public event Action? SessionRevoked;

    public SessionManager()
    {
        var saved = PinStore.Load();
        Pin = saved ?? NewRandomPin();
        PinIsPersonal = saved != null;
        Reset(notify: false);
    }

    public void Reset() => Reset(notify: true);

    private void Reset(bool notify)
    {
        bool hadSession;
        lock (_gate)
        {
            hadSession = _sessionId != null;
            Token = NewSecret(16);
            QrKey = NewSecret(16);
            if (!PinIsPersonal) Pin = NewRandomPin();
            FailedAttempts = 0;
            _sessionId = null;
            PhoneConnected = false;
            ConnectedSince = null;
            PhoneName = null;
        }
        if (hadSession) SessionRevoked?.Invoke();
        if (notify) Changed?.Invoke();
    }

    /// <summary>Saves the code chosen by the user. Returns an error message, or null when it is saved.</summary>
    public string? SetPersonalPin(string pin)
    {
        if (PinStore.Weakness(pin) is { } why) return why;
        try { PinStore.Save(pin); }
        catch (Exception ex) { return "Impossible d'enregistrer le code : " + ex.Message; }
        lock (_gate)
        {
            Pin = pin;
            PinIsPersonal = true;
            FailedAttempts = 0;
        }
        Changed?.Invoke();
        return null;
    }

    public bool IsToken(string token) => SameSecret(token, Token);

    public bool IsSession(string? sessionId)
    {
        lock (_gate) return sessionId != null && _sessionId != null && SameSecret(sessionId, _sessionId);
    }

    public PinResult TryPin(string pin, out string? sessionId) =>
        TryOpen(pin.Length == 6 && SameSecret(pin, Pin), rotateQr: false, out sessionId);

    /// <summary>Connection from the QR code: its key replaces the PIN, then a new key is made (single use).</summary>
    public PinResult TryQrKey(string key, out string? sessionId) =>
        TryOpen(key.Length == QrKey.Length && SameSecret(key, QrKey), rotateQr: true, out sessionId);

    private PinResult TryOpen(bool secretOk, bool rotateQr, out string? sessionId)
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
            else if (secretOk)
            {
                _sessionId = NewSecret(24);
                sessionId = _sessionId;
                FailedAttempts = 0;
                if (rotateQr) QrKey = NewSecret(16);
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

    private static string NewSecret(int bytes) => Convert.ToHexString(RandomNumberGenerator.GetBytes(bytes)).ToLowerInvariant();

    private static string NewRandomPin() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");

    private static bool SameSecret(string a, string b) =>
        a.Length == b.Length &&
        CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(a), System.Text.Encoding.ASCII.GetBytes(b));
}
