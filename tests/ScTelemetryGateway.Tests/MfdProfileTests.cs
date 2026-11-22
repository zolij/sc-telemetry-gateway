using ScTelemetryGateway.Core;

namespace ScTelemetryGateway.Tests;

public sealed class MfdProfileTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "sctg-profiles-" + Guid.NewGuid().ToString("N"));
    private MfdProfileStore Store => new(root);

    public static MfdProfile Draft() => new()
    {
        Name = "My flight deck",
        Pages = [new() { Id = Guid.NewGuid(), Name = "Flight", Tiles = [new()
        {
            Id = Guid.NewGuid(), Field = "actor.vitals.heart_rate", Label = "Heart rate",
            Widget = "value", Size = "normal"
        }] }]
    };

    [Fact]
    public void ProfilesSurviveReopeningAndRejectStaleWritesAndDeletes()
    {
        var first = Store.Create(Draft());
        var reopened = Store;
        Assert.Equal("Heart rate", reopened.Get(first.Id)!.Pages[0].Tiles[0].Label);
        var updated = reopened.Update(first.Id, first.Revision, first with { Name = "Updated" });
        Assert.Equal(2, updated.Revision);
        Assert.Throws<MfdProfileConflictException>(() => reopened.Update(first.Id, first.Revision, first));
        Assert.Throws<MfdProfileConflictException>(() => reopened.Delete(first.Id, first.Revision));
        Assert.Equal("Updated", Store.Get(first.Id)!.Name);
        reopened.Delete(updated.Id, updated.Revision);
        Assert.Empty(Store.List());
    }

    [Fact]
    public void ReturnedAndSuppliedArraysCannotChangeStoredProfiles()
    {
        var store = Store;
        var draft = Draft();
        var saved = store.Create(draft);
        draft.Pages[0].Tiles[0] = draft.Pages[0].Tiles[0] with { Label = "Changed input" };
        saved.Pages[0].Tiles[0] = saved.Pages[0].Tiles[0] with { Label = "Changed output" };
        Assert.Equal("Heart rate", store.Get(saved.Id)!.Pages[0].Tiles[0].Label);
    }

    [Fact]
    public void FailedPersistenceDoesNotPublishSuccessOrReplaceTheStoredProfile()
    {
        var store = Store;
        var saved = store.Create(Draft());
        File.Delete(Path.Combine(root, "mfd-profiles.json"));
        Directory.CreateDirectory(Path.Combine(root, "mfd-profiles.json"));
        Assert.ThrowsAny<IOException>(() => store.Update(saved.Id, saved.Revision, saved with { Name = "Not saved" }));
        Assert.Equal(saved.Name, store.Get(saved.Id)!.Name);
        Assert.Equal(saved.Revision, store.Get(saved.Id)!.Revision);
        Assert.Empty(Directory.GetFiles(root, "*.tmp"));
    }

    [Fact]
    public void CorruptStoreIsNotReplacedWithAnEmptyStore()
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "mfd-profiles.json");
        File.WriteAllText(path, "{broken");
        Assert.Throws<System.Text.Json.JsonException>(() => Store.Create(Draft()));
        Assert.Equal("{broken", File.ReadAllText(path));
    }

    [Fact]
    public void StoreByteLimitAcceptsTheBoundaryAndRejectsOneMoreByte()
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "mfd-profiles.json");
        const string empty = "{\"schema_version\":1,\"profiles\":[]}";
        File.WriteAllText(path, empty + new string(' ', MfdProfile.MaxStoreBytes - empty.Length));
        Assert.Empty(Store.List());
        File.AppendAllText(path, " ");
        Assert.Throws<InvalidDataException>(() => Store.List());
        Assert.Equal(MfdProfile.MaxStoreBytes + 1L, new FileInfo(path).Length);
    }

    [Fact]
    public void ListsAndStatusRulesPreserveTheirConfigurationAndUnknownVersionsFail()
    {
        var draft = Draft();
        var tile = draft.Pages[0].Tiles[0];
        var configured = draft with
        {
            Pages = [draft.Pages[0] with { Tiles =
            [
                tile with { Widget = "status", Rules = [new() { Value = "72", Label = "Normal", Color = "green" }] },
                tile with { Id = Guid.NewGuid(), Field = "events.events.type", Widget = "list" }
            ] }]
        };
        var saved = Store.Create(configured);
        Assert.Equal("Normal", Store.Get(saved.Id)!.Pages[0].Tiles[0].Rules[0].Label);
        Assert.Equal("list", Store.Get(saved.Id)!.Pages[0].Tiles[1].Widget);
        Assert.Throws<ArgumentException>(() => (configured with { SchemaVersion = 2 }).Validate());
        Assert.Throws<ArgumentException>(() => (configured with { Pages = [configured.Pages[0] with
        {
            Tiles = [tile with { Widget = "status", Rules =
                [new() { Value = "72", Label = "Normal", Color = "arbitrary-css" }] }]
        }] }).Validate());
    }

    [Fact]
    public async Task ConcurrentCreationCannotExceedTheProfileLimit()
    {
        var store = Store;
        var results = await Task.WhenAll(Enumerable.Range(0, MfdProfile.MaxProfiles + 1).Select(_ => Task.Run(() =>
        {
            try { store.Create(Draft()); return true; }
            catch (MfdProfileLimitException) { return false; }
        })));
        Assert.Equal(MfdProfile.MaxProfiles, results.Count(result => result));
        Assert.Single(results, result => !result);
        Assert.Equal(MfdProfile.MaxProfiles, Store.List().Length);
    }

    [Fact]
    public void PageAndTileBoundariesAndIncompatibleWidgetsAreValidated()
    {
        var draft = Draft();
        var tile = draft.Pages[0].Tiles[0];
        var page = draft.Pages[0] with
        {
            Tiles = Enumerable.Range(0, MfdProfile.MaxTiles).Select(_ => tile with { Id = Guid.NewGuid() }).ToArray()
        };
        var maximum = draft with
        {
            Pages = Enumerable.Range(0, MfdProfile.MaxPages).Select(_ => page with
            {
                Id = Guid.NewGuid(), Tiles = page.Tiles.Select(t => t with { Id = Guid.NewGuid() }).ToArray()
            }).ToArray()
        };
        maximum.Validate();
        Assert.Throws<ArgumentException>(() => (maximum with { Pages = [.. maximum.Pages, page] }).Validate());
        Assert.Throws<ArgumentException>(() => (draft with { Pages = [page with { Tiles = [.. page.Tiles, tile] }] }).Validate());
        Assert.Throws<ArgumentException>(() => (draft with
        {
            Pages = [page with { Tiles = [tile with { Widget = "list" }] }]
        }).Validate());
        Assert.Throws<ArgumentException>(() => (draft with
        {
            Pages = [page with { Tiles = [tile with { Field = "unknown" }] }]
        }).Validate());
    }

    [Fact]
    public void PercentageOverridesPersistAndOldProfilesUseAutomaticFormatting()
    {
        var draft = Draft();
        var profile = Store.Create(draft with
        {
            Pages = [draft.Pages[0] with { Tiles =
            [
                draft.Pages[0].Tiles[0] with { Field = "vehicle.state.shields.level", Format = "percent_fraction" },
                draft.Pages[0].Tiles[0] with { Id = Guid.NewGuid(), Field = "actor.vitals.health.current", Format = "percent_max" }
            ] }]
        });
        Assert.Equal("percent_fraction", Store.Get(profile.Id)!.Pages[0].Tiles[0].Format);
        Assert.Equal("percent_max", Store.Get(profile.Id)!.Pages[0].Tiles[1].Format);
        var oldJson = System.Text.Json.JsonSerializer.Serialize(draft, WireJson.Options).Replace("\"format\":\"auto\",", "");
        Assert.DoesNotContain("\"format\"", oldJson);
        var old = System.Text.Json.JsonSerializer.Deserialize<MfdProfile>(oldJson, WireJson.Options)!;
        Assert.Equal("auto", old.Pages[0].Tiles[0].Format);
        old.Validate();
    }

    [Theory]
    [InlineData("vehicle.state.shields.level", "percent_fraction", null)]
    [InlineData("vehicle.state.shields.faces", "percent_fraction", null)]
    [InlineData("vehicle.state.flight.instruments.hydrogen_fuel", "percent_fraction", null)]
    [InlineData("vehicle.state.flight.instruments.quantum_fuel", "percent", null)]
    [InlineData("actor.vitals.health.current", "percent_max", "actor.vitals.health.max")]
    [InlineData("actor.vitals.oxygen.current", "percent_max", "actor.vitals.oxygen.max")]
    [InlineData("actor.vitals.hunger.current", "percent_max", "actor.vitals.hunger.max")]
    [InlineData("actor.vitals.thirst.current", "percent_max", "actor.vitals.thirst.max")]
    [InlineData("actor.vitals.body_temperature.current", "number", "actor.vitals.body_temperature.max")]
    [InlineData("vehicle.state.health", "number", null)]
    public void CatalogDistinguishesPercentageScalesFromUnverifiedFields(string path, string format, string? maximum)
    {
        var field = MfdCatalogField.From(FieldCatalog.All[path]);
        Assert.Equal(format, field.DisplayFormat);
        Assert.Equal(maximum, field.MaximumField);
    }

    [Theory]
    [InlineData("actor.state.locomotion", "percent")]
    [InlineData("actor.state.uec_balance", "percent_fraction")]
    [InlineData("vehicle.state.flight.instruments.speed", "percent_max")]
    [InlineData("actor.vitals.heart_rate", "unknown")]
    public void InvalidPercentageFormatsAreRejected(string field, string format)
    {
        var draft = Draft();
        Assert.Throws<ArgumentException>(() => (draft with
        {
            Pages = [draft.Pages[0] with { Tiles = [draft.Pages[0].Tiles[0] with { Field = field, Format = format }] }]
        }).Validate());
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
