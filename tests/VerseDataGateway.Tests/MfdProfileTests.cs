using VerseDataGateway.Core;
using System.Text.Json;

namespace VerseDataGateway.Tests;

public sealed class MfdProfileTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "vdg-profiles-" + Guid.NewGuid().ToString("N"));
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
    public void ActionTilesPersistAndRejectInvalidBindingsAndHolds()
    {
        var draft = Draft();
        var tile = draft.Pages[0].Tiles[0];
        var control = tile with { Id = Guid.NewGuid(), Field = "", Label = "Radar ping",
            Widget = "action", Action = "v_invoke_ping", HoldMs = 1000 };
        var light = tile with { Field = "vehicle.state.lights_enabled" };
        var configured = draft with { Pages = [draft.Pages[0] with { Tiles = [light, control] }] };
        var saved = Store.Create(configured);
        var restored = Store.Get(saved.Id)!;
        Assert.Null(restored.Pages[0].Tiles[0].Action);
        Assert.Equal((uint)1000, restored.Pages[0].Tiles[1].HoldMs);
        foreach (var invalid in new[]
        {
            control with { HoldMs = 0 },
            control with { HoldMs = 5001 },
            control with { Action = null },
            control with { Action = "bad action" },
            light with { Action = "v_invoke_ping" },
            light with { Action = null, HoldMs = 100 }
        })
            Assert.Throws<ArgumentException>(() => (draft with { Pages = [draft.Pages[0] with { Tiles = [invalid] }] }).Validate());
        Assert.Throws<ArgumentException>(() => Store.Create(draft with { Pages = [draft.Pages[0] with
        {
            Tiles = [light with { Action = "v_lights_on" }]
        }] }));
        var legacy = JsonSerializer.Serialize(draft, WireJson.Options);
        Assert.DoesNotContain("\"action\"", legacy);
        Assert.DoesNotContain("\"hold_ms\"", legacy);
        JsonSerializer.Deserialize<MfdProfile>(legacy, WireJson.Options)!.Validate();
    }

    [Fact]
    public void LegacyControlGroupsAreRejectedForNewProfiles()
    {
        var draft = Draft();
        var tile = draft.Pages[0].Tiles[0] with
        {
            Field = "vehicle.state.master_mode.mode",
            Controls = [new() { Action = "v_master_mode_set_nav" }, new() { Action = "v_master_mode_set_scm" }]
        };
        var combined = draft with { Pages = [draft.Pages[0] with { Tiles = [tile] }] };
        combined.ValidateLegacy();
        Assert.Throws<ArgumentException>(() => Store.Create(combined));
        foreach (var invalid in new[]
        {
            tile with { Controls = [] },
            tile with { Action = "v_master_mode_cycle" },
            tile with { Controls = [new() { Action = "v_master_mode_set_nav" }, new() { Action = "v_master_mode_set_nav" }] },
            tile with { Controls = [new() { Action = "v_invoke_ping" }] },
            tile with { Controls = [new() { Action = "v_master_mode_set_nav", HoldMs = 5001 }] },
            tile with { Controls = Enumerable.Range(0, 65).Select(index => new MfdControl { Action = $"v_{index}" }).ToArray() }
        })
            Assert.Throws<ArgumentException>(() => (draft with { Pages = [draft.Pages[0] with { Tiles = [invalid] }] }).ValidateLegacy());
        var standalone = tile with { Field = "", Widget = "action", Controls =
            [new() { Action = "emote_wave" }, new() { Action = "v_invoke_ping", HoldMs = 500 }] };
        (draft with { Pages = [draft.Pages[0] with { Tiles = [standalone] }] }).ValidateLegacy();
        Assert.Throws<ArgumentException>(() => Store.Create(draft with { Pages = [draft.Pages[0] with { Tiles = [standalone] }] }));
    }

    [Fact]
    public void SavedLegacyProfilesCanBeListedAndDeletedWithoutBlockingAtomicProfiles()
    {
        var legacy = Draft() with { Id = Guid.NewGuid(), Revision = 1 };
        legacy = legacy with { Pages = [legacy.Pages[0] with { Tiles =
            [legacy.Pages[0].Tiles[0] with { Field = "vehicle.state.lights_enabled", Action = "v_lights" }] }] };
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "mfd-profiles.json"),
            JsonSerializer.Serialize(new { schema_version = 1, profiles = new[] { legacy } }, WireJson.Options));
        var store = Store;
        var atomic = store.Create(Draft());
        Assert.False(store.Summaries().Single(item => item.Id == legacy.Id).Atomic);
        Assert.True(store.Summaries().Single(item => item.Id == atomic.Id).Atomic);
        Assert.Throws<ArgumentException>(() => store.Get(legacy.Id));
        Assert.NotNull(store.Get(atomic.Id));
        store.Delete(legacy.Id, legacy.Revision);
        Assert.Single(store.Summaries());
    }

    [Theory]
    [InlineData("v_toggle_landing_system", "vehicle.state.flight.state.gear")]
    [InlineData("v_open_all_doors", "vehicle.state.doors.open_state")]
    [InlineData("v_ifcs_gsafe_on", "vehicle.state.flight.settings.ifcs_g_safe.active")]
    [InlineData("v_master_mode_set_nav", "vehicle.state.master_mode.mode")]
    [InlineData("v_power_toggle_weapons", "vehicle.state.power.weapons")]
    public void NameBasedActionsMapToExistingTelemetryFields(string action, string field)
    {
        Assert.Contains(field, MfdActions.FieldsForAction(action));
        Assert.True(FieldCatalog.All.ContainsKey(field));
    }

    [Theory]
    [InlineData("vehicle.state.power.shield_generators")]
    [InlineData("vehicle.state.power.shield_emitters")]
    [InlineData("vehicle.state.shields.level")]
    [InlineData("vehicle.state.shields.faces")]
    public void ShieldControlMapsToRelatedFields(string field)
    {
        Assert.Contains(field, MfdActions.FieldsForAction("v_power_toggle_shields"));
        Assert.True(FieldCatalog.All.ContainsKey(field));
    }

    [Fact]
    public void PageTriggersPersistAndLegacyProfilesDefaultToManualPages()
    {
        var draft = Draft();
        string[] triggers = ["GAME_EVENT_TYPE_ACTOR_ENTER_SEAT", "GAME_EVENT_TYPE_ACTOR_RESPAWN"];
        var saved = Store.Create(draft with { Pages = [draft.Pages[0] with { TriggerEvents = triggers }] });
        triggers[0] = "GAME_EVENT_TYPE_ACTOR_DEAD";
        Assert.Equal("GAME_EVENT_TYPE_ACTOR_ENTER_SEAT", Store.Get(saved.Id)!.Pages[0].TriggerEvents[0]);
        Assert.Equal(2, Store.Get(saved.Id)!.Pages[0].TriggerEvents.Length);
        var legacyJson = System.Text.Json.JsonSerializer.Serialize(draft, WireJson.Options).Replace(",\"trigger_events\":[]", "");
        Assert.DoesNotContain("\"trigger_events\"", legacyJson);
        var legacy = System.Text.Json.JsonSerializer.Deserialize<MfdProfile>(legacyJson, WireJson.Options)!;
        legacy.Validate();
        Assert.Empty(legacy.Pages[0].TriggerEvents);
    }

    [Fact]
    public void PageTriggersRejectUnknownAndDuplicateEventsButAreUniqueOnlyWithinAProfile()
    {
        var draft = Draft();
        var page = draft.Pages[0] with { TriggerEvents = ["GAME_EVENT_TYPE_ACTOR_ENTER_SEAT"] };
        Store.Create(draft with { Pages = [page] });
        Store.Create(draft with { Pages = [page] });
        Assert.Throws<ArgumentException>(() => (draft with {
            Pages = [page, page with { Id = Guid.NewGuid(), Tiles = [] }]
        }).Validate());
        foreach (var invalid in new string[][] {
            ["GAME_EVENT_TYPE_ACTOR_ENTER_SEAT", "GAME_EVENT_TYPE_ACTOR_ENTER_SEAT"],
            ["GAME_EVENT_TYPE_UNSPECIFIED"], ["unknown"], null!
        })
            Assert.Throws<ArgumentException>(() => (draft with { Pages = [page with { TriggerEvents = invalid }] }).Validate());
    }

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
        var error = Record.Exception(() => store.Update(saved.Id, saved.Revision, saved with { Name = "Not saved" }));
        Assert.True(error is IOException or UnauthorizedAccessException, error?.ToString() ?? "Persistence unexpectedly succeeded.");
        Assert.Equal(saved.Name, store.Get(saved.Id)!.Name);
        Assert.Equal(saved.Revision, store.Get(saved.Id)!.Revision);
        Assert.Empty(Directory.GetFiles(root, "*.tmp"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RevisionGrowthCannotPersistAProfileAboveTheReloadableByteLimit(bool oversizedAfterUpdate)
    {
        var store = Store;
        var saved = store.Create(Draft());
        while (saved.Revision < 9) saved = store.Update(saved.Id, saved.Revision, saved);
        var tile = saved.Pages[0].Tiles[0] with { Label = "x" };
        var draft = saved with
        {
            Pages = Enumerable.Range(0, MfdProfile.MaxPages).Select(_ => saved.Pages[0] with
            {
                Id = Guid.NewGuid(),
                Tiles = Enumerable.Range(0, MfdProfile.MaxTiles).Select(_ => tile with { Id = Guid.NewGuid() }).ToArray()
            }).ToArray()
        };
        var remaining = MfdProfile.MaxRequestBytes - JsonSerializer.SerializeToUtf8Bytes(draft, WireJson.Options).Length;
        foreach (var page in draft.Pages)
            for (var index = 0; index < page.Tiles.Length; index++)
            {
                var escaped = Math.Min(79, remaining / 6);
                var label = "x" + new string('\u00e9', escaped);
                remaining -= escaped * 6;
                var ascii = Math.Min(80 - label.Length, remaining);
                label += new string('x', ascii);
                remaining -= ascii;
                page.Tiles[index] = page.Tiles[index] with { Label = label };
            }
        Assert.Equal(0, remaining);
        if (!oversizedAfterUpdate) draft = draft with { Name = draft.Name[..^1] };
        Assert.Equal(MfdProfile.MaxRequestBytes - (oversizedAfterUpdate ? 0 : 1),
            JsonSerializer.SerializeToUtf8Bytes(draft, WireJson.Options).Length);
        draft.Validate();
        var path = Path.Combine(root, "mfd-profiles.json");
        var previousBytes = File.ReadAllBytes(path);
        if (oversizedAfterUpdate)
        {
            Assert.Throws<ArgumentException>(() => store.Update(saved.Id, saved.Revision, draft));
            Assert.Equal(previousBytes, File.ReadAllBytes(path));
            Assert.Equal(9, store.Get(saved.Id)!.Revision);
            Assert.Equal(9, Store.Get(saved.Id)!.Revision);
        }
        else
        {
            var updated = store.Update(saved.Id, saved.Revision, draft);
            Assert.Equal(10, updated.Revision);
            Assert.Equal(MfdProfile.MaxRequestBytes, JsonSerializer.SerializeToUtf8Bytes(updated, WireJson.Options).Length);
            Store.Get(saved.Id)!.Validate();
            Assert.Equal(10, Store.Get(saved.Id)!.Revision);
        }
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
