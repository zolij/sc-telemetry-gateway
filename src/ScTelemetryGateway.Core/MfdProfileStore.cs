using System.Text.Json;

namespace ScTelemetryGateway.Core;

public sealed class MfdProfileConflictException() : Exception("This profile was changed or deleted by another display.");
public sealed class MfdProfileLimitException(string message) : Exception(message);

public sealed class MfdProfileStore(string directory)
{
    private sealed record Document(int SchemaVersion, MfdProfile[] Profiles);
    private readonly object gate = new();
    private readonly string path = Path.Combine(directory, "mfd-profiles.json");
    private Dictionary<Guid, MfdProfile>? profiles;

    public MfdProfile[] List()
    {
        lock (gate) return Copy(Load().Values.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    public MfdProfile? Get(Guid id)
    {
        lock (gate) return Load().TryGetValue(id, out var profile) ? Copy(profile) : null;
    }

    public MfdProfile Create(MfdProfile draft)
    {
        draft.Validate();
        lock (gate)
        {
            var current = Load();
            if (current.Count >= MfdProfile.MaxProfiles)
                throw new MfdProfileLimitException($"At most {MfdProfile.MaxProfiles} profiles may be stored.");
            var profile = Copy(draft) with
            {
                Id = Guid.NewGuid(), Revision = 1,
                Pages = draft.Pages.Select(page => page with
                {
                    Id = Guid.NewGuid(),
                    Tiles = page.Tiles.Select(tile => tile with { Id = Guid.NewGuid() }).ToArray()
                }).ToArray()
            };
            var next = new Dictionary<Guid, MfdProfile>(current) { [profile.Id] = Copy(profile) };
            Persist(next);
            return Copy(profile);
        }
    }

    public MfdProfile Update(Guid id, long expectedRevision, MfdProfile draft)
    {
        draft.Validate();
        lock (gate)
        {
            var current = Load();
            if (!current.TryGetValue(id, out var existing) || existing.Revision != expectedRevision)
                throw new MfdProfileConflictException();
            if (draft.Id != id || draft.Revision != expectedRevision)
                throw new ArgumentException("Profile identity and revision must match the saved profile.");
            var profile = Copy(draft) with { Revision = checked(existing.Revision + 1) };
            var next = new Dictionary<Guid, MfdProfile>(current) { [id] = profile };
            Persist(next);
            return Copy(profile);
        }
    }

    public void Delete(Guid id, long expectedRevision)
    {
        lock (gate)
        {
            var current = Load();
            if (!current.TryGetValue(id, out var profile) || profile.Revision != expectedRevision)
                throw new MfdProfileConflictException();
            var next = new Dictionary<Guid, MfdProfile>(current);
            next.Remove(id);
            Persist(next);
        }
    }

    private Dictionary<Guid, MfdProfile> Load()
    {
        if (profiles is not null) return profiles;
        if (!File.Exists(path))
        {
            profiles = [];
            return profiles;
        }
        using var stream = File.OpenRead(path);
        if (stream.Length > MfdProfile.MaxStoreBytes) throw new InvalidDataException("Profile store exceeds its size limit.");
        var document = JsonSerializer.Deserialize<Document>(stream, WireJson.Options)
            ?? throw new JsonException("Profile store cannot be null.");
        if (document.SchemaVersion != 1 || document.Profiles is null || document.Profiles.Length > MfdProfile.MaxProfiles)
            throw new InvalidDataException("Invalid profile store schema or profile count.");
        var loaded = new Dictionary<Guid, MfdProfile>();
        foreach (var profile in document.Profiles)
        {
            if (profile is null || profile.Id == Guid.Empty || profile.Revision < 1 || !loaded.TryAdd(profile.Id, profile))
                throw new InvalidDataException("Invalid or duplicate saved profile identity.");
            try { profile.Validate(); }
            catch (ArgumentException ex) { throw new InvalidDataException("Invalid saved profile.", ex); }
        }
        profiles = loaded;
        return loaded;
    }

    private void Persist(Dictionary<Guid, MfdProfile> next)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Document(1, next.Values.ToArray()), WireJson.Options);
        if (bytes.Length > MfdProfile.MaxStoreBytes)
            throw new MfdProfileLimitException("Profile store exceeds the 16 MiB size limit.");
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
            profiles = next;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static T Copy<T>(T value) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.SerializeToUtf8Bytes(value, WireJson.Options), WireJson.Options)
        ?? throw new JsonException("Profile copy cannot be null.");
}
