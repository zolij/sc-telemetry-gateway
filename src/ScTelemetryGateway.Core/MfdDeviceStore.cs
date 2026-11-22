using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ScTelemetryGateway.Core;

internal sealed record MfdStoredDevice(Guid Id, string Label, string TokenHash, DateTimeOffset ApprovedAt);

public sealed class MfdDeviceStore(string directory)
{
    private const int MaxStoreBytes = 1024 * 1024;
    private sealed class Document
    {
        public int SchemaVersion { get; set; }
        public StoredDevice[]? Devices { get; set; }
    }

    private sealed class StoredDevice
    {
        public Guid Id { get; set; }
        public string? Label { get; set; }
        public string? TokenHash { get; set; }
        public DateTimeOffset ApprovedAt { get; set; }
    }

    private readonly object gate = new();
    private readonly string path = Path.Combine(directory, "mfd-devices.json");
    private MfdStoredDevice[]? devices;

    internal MfdStoredDevice[] List()
    {
        lock (gate) return Load().ToArray();
    }

    internal void Add(MfdStoredDevice device)
    {
        Validate(device);
        lock (gate)
        {
            var current = Load();
            if (current.Any(existing => existing.Id == device.Id))
                throw new InvalidDataException("Duplicate MFD device identifier.");
            Persist(current.Append(device).ToArray());
        }
    }

    internal bool Remove(Guid id)
    {
        lock (gate)
        {
            var current = Load();
            if (!current.Any(device => device.Id == id)) return false;
            Persist(current.Where(device => device.Id != id).ToArray());
            return true;
        }
    }

    internal static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    internal static bool TokenMatches(string token, string expectedHash)
    {
        var actual = Encoding.ASCII.GetBytes(HashToken(token));
        var expected = Encoding.ASCII.GetBytes(expectedHash);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private MfdStoredDevice[] Load()
    {
        if (devices is not null) return devices;
        FileStream stream;
        try { stream = File.OpenRead(path); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            devices = [];
            return devices;
        }
        using (stream)
        {
            if (stream.Length > MaxStoreBytes)
                throw new InvalidDataException("MFD device store exceeds its size limit.");
            var document = JsonSerializer.Deserialize<Document>(stream, WireJson.Options)
                ?? throw new JsonException("MFD device store cannot be null.");
            if (document.SchemaVersion != 1 || document.Devices is null)
                throw new InvalidDataException("Invalid MFD device store schema.");
            var loaded = new Dictionary<Guid, MfdStoredDevice>();
            foreach (var item in document.Devices)
            {
                if (item is null || item.Label is null || item.TokenHash is null)
                    throw new InvalidDataException("Invalid MFD device record.");
                var device = new MfdStoredDevice(item.Id, item.Label, item.TokenHash, item.ApprovedAt);
                Validate(device);
                if (!loaded.TryAdd(device.Id, device))
                    throw new InvalidDataException("Duplicate MFD device identifier.");
            }
            devices = loaded.Values.ToArray();
            return devices;
        }
    }

    private void Persist(MfdStoredDevice[] next)
    {
        var document = new Document
        {
            SchemaVersion = 1,
            Devices = next.Select(device => new StoredDevice
            {
                Id = device.Id,
                Label = device.Label,
                TokenHash = device.TokenHash,
                ApprovedAt = device.ApprovedAt
            }).ToArray()
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, WireJson.Options);
        if (bytes.Length > MaxStoreBytes)
            throw new InvalidDataException("MFD device store exceeds its size limit.");
        Directory.CreateDirectory(directory);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
            devices = next;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void Validate(MfdStoredDevice device)
    {
        if (device.Id == Guid.Empty || device.Label.Length is < 1 or > 48 ||
            device.Label != device.Label.Trim() || device.TokenHash.Length != 64 ||
            device.TokenHash.Any(character => !char.IsAsciiDigit(character) && character is not (>= 'A' and <= 'F')) ||
            device.ApprovedAt == default)
            throw new InvalidDataException("Invalid MFD device record.");
    }
}
