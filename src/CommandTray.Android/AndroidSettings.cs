using Android.Content;
using Android.Security.Keystore;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;
using System.Security.Cryptography;
using System.Text;

namespace CommandTray.Android;

internal sealed class AndroidSettings(Context context)
{
    private const string KeyAlias = "commandtray.android.secrets";
    private readonly ISharedPreferences _preferences = context.GetSharedPreferences("commandtray", FileCreationMode.Private)
        ?? throw new IOException("Android preferences are unavailable.");

    public const int McpPort = 58629;
    public const string McpEndpoint = "http://127.0.0.1:58629/mcp";

    public string Server => Get("server");
    public string DeviceName => Get("name", "Android");
    public string DeviceId => Get("deviceId");
    public string ActiveSpace => Get("activeSpace");
    public string Language => Get("language", "auto");
    public bool Paired => Server.Length != 0 && _preferences.Contains("relayToken");
    public bool McpEnabled => _preferences.GetBoolean("mcpEnabled", false);

    public void SavePairing(string server, string name, string deviceId, string token)
    {
        if (deviceId.Length == 0 || token.Length == 0)
            throw new InvalidDataException("The relay returned incomplete pairing credentials.");

        using var edit = Edit();
        if (!server.Equals(Server, StringComparison.Ordinal))
            edit.Remove("activeSpace");
        edit.PutString("server", server);
        edit.PutString("name", name);
        edit.PutString("deviceId", deviceId);
        edit.PutString("relayToken", Encrypt(token));
        Commit(edit);
    }

    public void ForgetPairing()
    {
        using var edit = Edit();
        edit.Remove("deviceId");
        edit.Remove("relayToken");
        Commit(edit);
    }

    public void SelectSpace(string id)
    {
        using var edit = Edit();
        edit.PutString("activeSpace", id);
        Commit(edit);
    }

    public void SetLanguage(string language)
    {
        using var edit = Edit();
        edit.PutString("language", language);
        Commit(edit);
    }

    public void SetMcpEnabled(bool enabled)
    {
        using var edit = Edit();
        edit.PutBoolean("mcpEnabled", enabled);
        if (enabled && !_preferences.Contains("mcpToken"))
        {
            var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');
            edit.PutString("mcpToken", Encrypt(token));
        }
        Commit(edit);
    }

    public string RelayToken() => Secret("relayToken", "Pair this device first.");
    public string McpToken() => Secret("mcpToken", "Enable local MCP first.");

    public static string NormalizeServer(string text)
    {
        var normalized = text.Trim().TrimEnd('/');
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri) ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            (uri.Scheme != Uri.UriSchemeHttps &&
                !(uri.Scheme == Uri.UriSchemeHttp && uri.Host is ("localhost" or "127.0.0.1"))))
            throw new ArgumentException("Use an HTTPS relay address or an HTTP localhost address.");
        // Java used URI.toASCIIString. Avoid normalizing unrelated parts of an existing
        // address, since the original spelling is part of the workspace filename.
        return normalized.All(char.IsAscii) ? normalized : uri.AbsoluteUri.TrimEnd('/');
    }

    private string Secret(string name, string unavailable)
    {
        var protectedValue = Get(name);
        if (protectedValue.Length == 0)
            throw new InvalidOperationException(unavailable);
        var parts = protectedValue.Split('.', 2);
        if (parts.Length != 2)
            throw new InvalidDataException("The saved credential is invalid.");
        using var key = Key(generate: false);
        using var cipher = Cipher.GetInstance("AES/GCM/NoPadding") ?? throw new CryptographicException("AES-GCM is unavailable.");
        using var parameters = new GCMParameterSpec(128, Convert.FromBase64String(parts[0]));
        cipher.Init(Javax.Crypto.CipherMode.DecryptMode, key, parameters);
        return Encoding.UTF8.GetString(cipher.DoFinal(Convert.FromBase64String(parts[1]))
            ?? throw new CryptographicException("The saved credential could not be decrypted."));
    }

    private static string Encrypt(string text)
    {
        using var key = Key(generate: true);
        using var cipher = Cipher.GetInstance("AES/GCM/NoPadding") ?? throw new CryptographicException("AES-GCM is unavailable.");
        cipher.Init(Javax.Crypto.CipherMode.EncryptMode, key);
        var nonce = cipher.GetIV() ?? throw new CryptographicException("Keystore did not generate a nonce.");
        var bytes = cipher.DoFinal(Encoding.UTF8.GetBytes(text)) ?? throw new CryptographicException("Credential encryption failed.");
        return Convert.ToBase64String(nonce) + "." + Convert.ToBase64String(bytes);
    }

    private static IKey Key(bool generate)
    {
        const string provider = "AndroidKeyStore";
        using var store = KeyStore.GetInstance(provider) ?? throw new CryptographicException("Android Keystore is unavailable.");
        store.Load(null);
        if (store.GetKey(KeyAlias, null) is { } existing)
            return existing;
        if (!generate)
            throw new InvalidOperationException("The saved Android Keystore key is unavailable. Pair this device again.");

        using var generator = KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmAes, provider)
            ?? throw new CryptographicException("Android Keystore AES is unavailable.");
        using var settings = new KeyGenParameterSpec.Builder(KeyAlias, KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt);
        using var parameters = settings.SetBlockModes(KeyProperties.BlockModeGcm)
            .SetEncryptionPaddings(KeyProperties.EncryptionPaddingNone).Build();
        generator.Init(parameters);
        return generator.GenerateKey() ?? throw new CryptographicException("Could not create the Android Keystore key.");
    }

    private string Get(string name, string fallback = "") => _preferences.GetString(name, fallback) ?? fallback;
    private ISharedPreferencesEditor Edit() => _preferences.Edit() ?? throw new IOException("Android preferences are unavailable.");
    private static void Commit(ISharedPreferencesEditor edit)
    {
        if (!edit.Commit())
            throw new IOException("Could not save Android settings.");
    }
}