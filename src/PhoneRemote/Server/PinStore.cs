using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PhoneRemote.Server;

/// <summary>
/// The personal code chosen by the user, kept between launches in %LOCALAPPDATA%\se7en Vision\pin.json.
/// It is encrypted with Windows DPAPI (current user only), so the file is useless on another account or PC.
/// </summary>
internal static class PinStore
{
    private static string FilePath => Path.Combine(AppInfo.DataDir, "pin.json");

    public static string? Load()
    {
        try
        {
            var doc = JsonSerializer.Deserialize(File.ReadAllText(FilePath), PinJson.Default.PinFile);
            if (doc?.Data is not { Length: > 0 } data) return null;
            var pin = Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(data), Entropy, DataProtectionScope.CurrentUser));
            return IsValid(pin) ? pin : null;
        }
        catch (Exception)
        {
            return null; // pas encore de code choisi, ou fichier illisible
        }
    }

    public static void Save(string pin)
    {
        Directory.CreateDirectory(AppInfo.DataDir);
        var data = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(pin), Entropy, DataProtectionScope.CurrentUser));
        File.WriteAllText(FilePath, JsonSerializer.Serialize(new PinFile(data), PinJson.Default.PinFile));
    }

    public static bool IsValid(string pin) => pin.Length == 6 && pin.All(char.IsAsciiDigit);

    /// <summary>Refuse les codes trop faciles à deviner (000000, 123456…). Retourne le message d'erreur, ou null.</summary>
    public static string? Weakness(string pin)
    {
        if (!IsValid(pin)) return "Le code doit faire 6 chiffres.";
        if (pin.Distinct().Count() == 1) return "Six fois le même chiffre, c'est trop facile à deviner.";
        bool up = true, down = true;
        for (int i = 1; i < 6; i++)
        {
            if (pin[i] != pin[i - 1] + 1) up = false;
            if (pin[i] != pin[i - 1] - 1) down = false;
        }
        return up || down ? "Une suite de chiffres, c'est trop facile à deviner." : null;
    }

    private static readonly byte[] Entropy = "se7en Vision code personnel"u8.ToArray();
}

internal sealed record PinFile(string Data);

[System.Text.Json.Serialization.JsonSerializable(typeof(PinFile))]
internal partial class PinJson : System.Text.Json.Serialization.JsonSerializerContext;
