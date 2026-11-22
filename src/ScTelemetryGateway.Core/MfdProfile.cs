using System.Text.Json;
using System.Text.Json.Serialization;
using Google.Protobuf.Reflection;

namespace ScTelemetryGateway.Core;

public sealed record MfdStatusRule
{
    public required string Value { get; init; }
    public required string Label { get; init; }
    public required string Color { get; init; }
}

public sealed record MfdTile
{
    public required Guid Id { get; init; }
    public required string Field { get; init; }
    public required string Label { get; init; }
    public required string Widget { get; init; }
    public required string Size { get; init; }
    public int Decimals { get; init; } = 2;
    public MfdStatusRule[] Rules { get; init; } = [];
}

public sealed record MfdPage
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required MfdTile[] Tiles { get; init; }
}

public sealed record MfdProfile
{
    [JsonRequired]
    public int SchemaVersion { get; init; } = 1;
    public Guid Id { get; init; }
    public long Revision { get; init; }
    public required string Name { get; init; }
    public required MfdPage[] Pages { get; init; }

    public const int MaxProfiles = 32;
    public const int MaxPages = 16;
    public const int MaxTiles = 64;
    public const int MaxRequestBytes = 512 * 1024;
    public const int MaxStoreBytes = 16 * 1024 * 1024;

    public void Validate()
    {
        if (SchemaVersion != 1) throw new ArgumentException("Unsupported profile schema version.");
        Text(Name, 80, "Profile name");
        if (Pages is null || Pages.Length is < 1 or > MaxPages)
            throw new ArgumentException($"Profiles must contain between 1 and {MaxPages} pages.");
        var ids = new HashSet<Guid>();
        foreach (var page in Pages)
        {
            if (page is null) throw new ArgumentException("Pages cannot be null.");
            Identity(page.Id, ids);
            Text(page.Name, 80, "Page name");
            if (page.Tiles is null || page.Tiles.Length > MaxTiles)
                throw new ArgumentException($"Pages may contain at most {MaxTiles} tiles.");
            foreach (var tile in page.Tiles)
            {
                if (tile is null) throw new ArgumentException("Tiles cannot be null.");
                Identity(tile.Id, ids);
                Text(tile.Label, 80, "Tile label");
                if (tile.Field is null || !FieldCatalog.All.TryGetValue(tile.Field, out var field))
                    throw new ArgumentException("Unknown telemetry field.");
                if (tile.Widget is not ("value" or "status" or "list") ||
                    field.Repeated != (tile.Widget == "list"))
                    throw new ArgumentException("Widget is incompatible with the telemetry field.");
                if (tile.Size is not ("normal" or "wide" or "large") || tile.Decimals is < 0 or > 6)
                    throw new ArgumentException("Invalid tile size or decimal precision.");
                if (tile.Rules is null || tile.Rules.Length > 32 || tile.Widget != "status" && tile.Rules.Length != 0)
                    throw new ArgumentException("Invalid status rules.");
                var values = new HashSet<string>(StringComparer.Ordinal);
                foreach (var rule in tile.Rules)
                {
                    if (rule is null) throw new ArgumentException("Status rules cannot be null.");
                    Text(rule.Value, 128, "Status value");
                    Text(rule.Label, 80, "Status label");
                    if (!values.Add(rule.Value) || rule.Color is not ("neutral" or "green" or "amber" or "red" or "blue"))
                        throw new ArgumentException("Status rules need unique values and a supported color.");
                }
            }
        }
        if (JsonSerializer.SerializeToUtf8Bytes(this, WireJson.Options).Length > MaxRequestBytes)
            throw new ArgumentException("Profile exceeds the 512 KiB size limit.");
    }

    private static void Text(string? value, int maximum, string label)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum || value.Any(char.IsControl))
            throw new ArgumentException($"{label} must contain 1-{maximum} characters without control characters.");
    }

    private static void Identity(Guid id, HashSet<Guid> ids)
    {
        if (id == Guid.Empty || !ids.Add(id))
            throw new ArgumentException("Page and tile identifiers must be nonempty and unique.");
    }
}

public sealed record MfdCatalogField(string Path, string Type, bool Repeated, string[] EnumValues)
{
    public static MfdCatalogField From(TelemetryField field)
    {
        var descriptor = field.Chain[^1];
        return new(field.Path, descriptor.FieldType.ToString().ToLowerInvariant(), field.Repeated,
            descriptor.FieldType == FieldType.Enum
                ? descriptor.EnumType.Values.Select(value => value.Name).ToArray() : []);
    }

}

public sealed record MfdProfileSummary(Guid Id, string Name, long Revision);
