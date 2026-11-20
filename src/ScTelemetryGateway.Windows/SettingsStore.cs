using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ScTelemetryGateway.Core;

namespace ScTelemetryGateway.Windows;

[SupportedOSPlatform("windows")]
public sealed class SettingsStore(string? directory = null)
{
    private readonly string root = directory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StarCitizenTelemetryGateway");

    public (GatewayOptions Options, string Key) Load()
    {
        var path = Path.Combine(root, "settings.json");
        var options = File.Exists(path)
            ? JsonSerializer.Deserialize<GatewayOptions>(File.ReadAllText(path), WireJson.Options)
                ?? throw new JsonException("Settings cannot be null.")
            : new GatewayOptions();
        options.Validate(requireEndpoint: false);
        var keyPath = Path.Combine(root, "api-key.bin");
        if (!File.Exists(keyPath)) return (options, "");
        var clear = ProtectedData.Unprotect(File.ReadAllBytes(keyPath), null, DataProtectionScope.CurrentUser);
        try { return (options, Encoding.UTF8.GetString(clear)); }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }

    public void Save(GatewayOptions options, string key)
    {
        options.Validate(requireEndpoint: false);
        if (key.Any(char.IsControl)) throw new ArgumentException("API key cannot contain control characters.");
        Directory.CreateDirectory(root);
        var clear = Encoding.UTF8.GetBytes(key);
        try
        {
            var protectedKey = ProtectedData.Protect(clear, null, DataProtectionScope.CurrentUser);
            AtomicWrite(Path.Combine(root, "api-key.bin"), protectedKey);
            AtomicWrite(Path.Combine(root, "settings.json"), JsonSerializer.SerializeToUtf8Bytes(options, WireJson.Options));
        }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }

    private static void AtomicWrite(string path, byte[] contents)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(contents);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
