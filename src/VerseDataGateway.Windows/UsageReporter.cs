using System.Net.Http.Json;
using VerseDataGateway.Core;

namespace VerseDataGateway.Windows;

public sealed class UsageIdentityStore(string? directory = null)
{
    private readonly string root = directory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VerseDataGateway");

    public Guid GetOrCreate()
    {
        var path = Path.Combine(root, "usage-id");
        if (File.Exists(path))
        {
            var value = File.ReadAllText(path);
            if (!Guid.TryParseExact(value, "D", out var existing) || existing.Version != 4)
                throw new InvalidDataException("The usage statistics installation ID is invalid.");
            return existing;
        }

        Directory.CreateDirectory(root);
        var id = Guid.NewGuid();
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream);
        writer.Write(id.ToString("D"));
        return id;
    }

    public void Delete()
    {
        if (Directory.Exists(root)) File.Delete(Path.Combine(root, "usage-id"));
    }
}

public sealed class UsageReporter(HttpClient client, UsageIdentityStore identity, Uri endpoint)
{
    public static readonly Uri Endpoint = new("https://sc-telemetry-usage.zolij.info/v1/activity");

    public async Task ReportAsync(bool collectionRunning, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var id = identity.GetOrCreate();
        using var response = await client.PostAsJsonAsync(endpoint, new
        {
            installationId = id.ToString("D"),
            version = BuildInfo.Version,
            collection = collectionRunning
        }, cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}
