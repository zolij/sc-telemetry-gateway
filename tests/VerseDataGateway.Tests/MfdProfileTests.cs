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
    public void ActionLibraryIncludesBaselineAndPersistsNewLiveActions()
    {
        var snapshot = new MfdSnapshotStore([]);
        var library = new MfdActionLibrary(root);
        var baseline = library.List(snapshot);
        Assert.Equal(112, baseline.Count);
        Assert.True(baseline.Single(action => action.ActionName == "v_invoke_ping").Holdable);
        Assert.False(baseline.Single(action => action.ActionName == "v_lights_on").Enabled);
        library.Remember([MfdActions.Describe("future_control", "future_category", true, snapshot)]);
        var reopened = new MfdActionLibrary(root).List(snapshot);
        Assert.Equal(113, reopened.Count);
        Assert.True(reopened.Single(action => action.ActionName == "future_control").Holdable);
        library.Remember([]);
        Assert.Equal(113, new MfdActionLibrary(root).List(snapshot).Count);
    }

    [Fact]
    public void ActionLibraryReportsCorruptStorageWithoutReplacingIt()
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "mfd-actions.json");
        File.WriteAllText(path, "{invalid");
        var library = new MfdActionLibrary(root);
        Assert.Throws<InvalidDataException>(() => library.List(new MfdSnapshotStore([])));
        Assert.Equal("{invalid", File.ReadAllText(path));
    }

    [Fact]
    public void CombinedTilesRoundTripAndRejectInvalidComponents()
    {
        var draft = Draft();
        var value = draft.Pages[0].Tiles[0];
        var action = value with { Id = Guid.NewGuid(), Field = "", Widget = "action", Action = "v_lights_on" };
        var combined = value with { Id = Guid.NewGuid(), Field = "", Label = "Controls", Widget = "combined",
            Components = [value, action] };
        var saved = Store.Create(draft with { Pages = [draft.Pages[0] with { Tiles = [combined] }] });
        var restored = Store.Get(saved.Id)!.Pages[0].Tiles[0];
        Assert.Equal(2, restored.Components!.Length);
        Assert.NotEqual(value.Id, restored.Components[0].Id);
        Assert.NotEqual(action.Id, restored.Components[1].Id);
        Assert.Equal("v_lights_on", restored.Components[1].Action);
        foreach (var invalid in new[]
        {
            combined with { Components = [] },
            combined with { Components = [combined] },
            combined with { Components = [value, value] },
            combined with { Components = Enumerable.Range(0, 17).Select(_ => value with { Id = Guid.NewGuid() }).ToArray() },
            combined with { Field = value.Field },
            value with { Components = [action] }
        })
            Assert.Throws<ArgumentException>(() => (draft with { Pages = [draft.Pages[0] with { Tiles = [invalid] }] }).Validate());
    }

    [Fact]
    public void EarlierTemplatesMigrateAtomicallyOnceWithRevisionAndLeaveCustomCards()
    {
        MfdTile Value(string field, string label) => new() {
            Id = Guid.NewGuid(), Field = field, Label = label, Widget = "value", Size = "normal"
        };
        MfdTile Action(string name) => new() {
            Id = Guid.NewGuid(), Field = "", Label = name, Widget = "action", Size = "normal", Action = name
        };
        MfdTile Group(string label, string field, params string[] actions) => new() {
            Id = Guid.NewGuid(), Field = "", Label = label, Widget = "combined", Size = "wide",
            Components = new[] { Value(field, label) }.Concat(actions.Select(Action)).ToArray()
        };
        var old = new[] {
            Group("Landing gear", "vehicle.state.flight.state.gear", "v_deploy_landing_system", "v_retract_landing_system"),
            Group("Master mode", "vehicle.state.master_mode.mode", "v_master_mode_cycle", "v_master_mode_set_scm"),
            Group("SCM advanced HUD", "vehicle.state.flight.settings.advanced_hud_enabled_scm", "v_flight_advanced_hud_toggle"),
            Group("Weapons power", "vehicle.state.power.weapons", "v_power_toggle_weapons"),
            Group("Landing gear", "vehicle.state.flight.state.gear", "v_deploy_landing_system", "v_invoke_ping")
        };
        var draft = Draft();
        var saved = Store.Create(draft with { Pages = [draft.Pages[0] with { Tiles = old }] });
        var migrated = Store.Get(saved.Id)!;
        Assert.Equal(saved.Revision + 1, migrated.Revision);
        var tiles = migrated.Pages[0].Tiles;
        Assert.Equal("v_toggle_landing_system", Assert.Single(tiles[0].Components!, component => component.Widget == "action").Action);
        Assert.Equal("v_master_mode_cycle", Assert.Single(tiles[1].Components!, component => component.Widget == "action").Action);
        Assert.Equal("Advanced HUD", tiles[2].Label);
        Assert.Equal(new[] { "vehicle.state.master_mode.mode", "vehicle.state.flight.settings.advanced_hud_enabled_scm",
            "vehicle.state.flight.settings.advanced_hud_enabled_nav" }, tiles[2].Components!.Where(item => item.Widget == "value").Select(item => item.Field));
        Assert.Equal("System power", tiles[3].Label);
        Assert.Equal(4, tiles[3].Components!.Count(item => item.Widget == "value"));
        Assert.Equal(new[] { "v_power_toggle_weapons", "v_power_toggle_shields", "v_power_toggle_thrusters" },
            tiles[3].Components!.Where(item => item.Widget == "action").Select(item => item.Action));
        Assert.Contains(tiles[4].Components!, item => item.Action == "v_invoke_ping");
        var reopened = Store.Get(saved.Id)!;
        Assert.Equal(migrated.Revision, reopened.Revision);
        Assert.Equal(JsonSerializer.Serialize(migrated, WireJson.Options), JsonSerializer.Serialize(reopened, WireJson.Options));
    }

    [Theory]
    [InlineData("vehicle.state.lights_enabled", "v_lights_on")]
    [InlineData("vehicle.state.flight.state.gear", "v_deploy_landing_system")]
    [InlineData("vehicle.state.flight.settings.ifcs_g_safe.active", "v_ifcs_gsafe_on")]
    [InlineData("vehicle.state.flight.settings.ifcs_coupled.active", "v_ifcs_vector_decoupling_on")]
    [InlineData("vehicle.state.master_mode.mode", "v_master_mode_set_scm")]
    [InlineData("vehicle.state.flight.settings.advanced_hud_enabled_scm", "v_flight_advanced_hud_on")]
    [InlineData("vehicle.state.flight.settings.flight_light_amplification", "v_light_amplification_on")]
    [InlineData("vehicle.state.power.weapons", "v_power_set_weapons_on")]
    public void DocumentedTemplateFieldsAndActionsExist(string field, string action)
    {
        Assert.True(FieldCatalog.All.ContainsKey(field));
        Assert.Contains(field, MfdActions.FieldsForAction(action));
        Assert.Contains(new MfdActionLibrary(root).List(new MfdSnapshotStore([])), item => item.ActionName == action);
    }

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
