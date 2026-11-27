namespace VerseDataGateway.Core;

internal static class MfdPresetMigration
{
    private const string Master = "vehicle.state.master_mode.mode";
    private const string HudScm = "vehicle.state.flight.settings.advanced_hud_enabled_scm";
    private const string HudNav = "vehicle.state.flight.settings.advanced_hud_enabled_nav";

    internal static MfdTile Convert(MfdTile tile)
    {
        if (tile.Widget != "combined" || tile.Components is null || tile.Size != "wide" ||
            tile.Decimals != 2 || tile.Format != "auto" || tile.Rules.Length != 0) return tile;
        var (field, allowed, label, fields, actions) = tile.Label switch
        {
            "Vehicle lights" => ("vehicle.state.lights_enabled", new[] { "v_lights", "v_lights_on", "v_lights_off" },
                "Vehicle lights", new[] { ("vehicle.state.lights_enabled", "Vehicle lights") }, new[] { ("v_lights", "Lights") }),
            "Landing gear" => ("vehicle.state.flight.state.gear", new[] { "v_toggle_landing_system", "v_deploy_landing_system", "v_retract_landing_system" },
                "Landing gear", new[] { ("vehicle.state.flight.state.gear", "Landing gear") }, new[] { ("v_toggle_landing_system", "Toggle Landing System") }),
            "G-safe" => ("vehicle.state.flight.settings.ifcs_g_safe.active", new[] { "v_ifcs_toggle_gforce_safety", "v_ifcs_gsafe_on", "v_ifcs_gsafe_off" },
                "G-safe", new[] { ("vehicle.state.flight.settings.ifcs_g_safe.active", "G-safe") }, new[] { ("v_ifcs_toggle_gforce_safety", "Ifcs Toggle Gforce Safety") }),
            "Coupled flight" => ("vehicle.state.flight.settings.ifcs_coupled.active", new[] { "v_ifcs_vector_decoupling_toggle", "v_ifcs_vector_decoupling_on", "v_ifcs_vector_decoupling_off" },
                "Coupled flight", new[] { ("vehicle.state.flight.settings.ifcs_coupled.active", "Coupled flight") }, new[] { ("v_ifcs_vector_decoupling_toggle", "Ifcs Vector Decoupling Toggle") }),
            "Master mode" => (Master, new[] { "v_master_mode_cycle", "v_master_mode_set_scm" },
                "Master mode", new[] { (Master, "Master mode") }, new[] { ("v_master_mode_cycle", "Master Mode Cycle") }),
            "SCM advanced HUD" => (HudScm, new[] { "v_flight_advanced_hud_toggle", "v_flight_advanced_hud_on" },
                "Advanced HUD", new[] { (Master, "Master mode"), (HudScm, "SCM"), (HudNav, "NAV") }, new[] { ("v_flight_advanced_hud_toggle", "Flight Advanced Hud Toggle") }),
            "Weapons power" => ("vehicle.state.power.weapons", new[] { "v_power_toggle_weapons", "v_power_set_weapons_on", "v_power_set_weapons_off" },
                "System power", new[] { ("vehicle.state.power.weapons", "Weapons"), ("vehicle.state.power.shield_generators", "Shield generator"),
                    ("vehicle.state.power.shield_emitters", "Shield emitter"), ("vehicle.state.power.engines", "Engines") },
                new[] { ("v_power_toggle_weapons", "Power Toggle Weapons"), ("v_power_toggle_shields", "Power Toggle Shields"), ("v_power_toggle_thrusters", "Power Toggle Thrusters") }),
            _ => (null, Array.Empty<string>(), null, Array.Empty<(string, string)>(), Array.Empty<(string, string)>())
        };
        if (field is null || tile.Components.Length < 2 || tile.Components[0].Widget != "value" ||
            tile.Components[0].Field != field || tile.Components[0].Label != tile.Label ||
            tile.Components.Any(item => item.Size != "normal" || item.Decimals != 2 || item.Format != "auto" ||
                item.HoldMs is not null || item.Rules.Length != 0) ||
            tile.Components.Skip(1).Any(item => item.Widget != "action" || item.Action is null ||
                item.Field != "" || !allowed.Contains(item.Action, StringComparer.Ordinal) ||
                item.Label != ActionLabel(item.Action))) return tile;
        var oldActions = tile.Components.Skip(1).Select(item => item.Action).ToArray();
        var validActions = tile.Label switch
        {
            "Vehicle lights" => new[] { "v_lights", "v_lights_on", "v_lights_off" },
            "Landing gear" => new[] { "v_deploy_landing_system", "v_retract_landing_system" },
            "G-safe" => new[] { "v_ifcs_gsafe_on", "v_ifcs_gsafe_off", "v_ifcs_toggle_gforce_safety" },
            "Coupled flight" => new[] { "v_ifcs_vector_decoupling_on", "v_ifcs_vector_decoupling_off", "v_ifcs_vector_decoupling_toggle" },
            "Master mode" => new[] { "v_master_mode_cycle", "v_master_mode_set_scm" },
            "SCM advanced HUD" => oldActions.Length == 1 ? new[] { "v_flight_advanced_hud_toggle" } :
                new[] { "v_flight_advanced_hud_on", "v_flight_advanced_hud_toggle" },
            "Weapons power" => oldActions.Length == 1 ? new[] { "v_power_toggle_weapons" } :
                new[] { "v_power_set_weapons_on", "v_power_set_weapons_off", "v_power_toggle_weapons" },
            _ => Array.Empty<string>()
        };
        if (!oldActions.SequenceEqual(validActions)) return tile;

        var components = fields.Select(item =>
        {
            var existing = tile.Components.SingleOrDefault(component => component.Widget == "value" && component.Field == item.Item1);
            return existing is null ? Field(item.Item1, item.Item2) : existing with { Label = item.Item2 };
        }).Concat(actions.Select(item =>
        {
            var existing = tile.Components.FirstOrDefault(component => component.Widget == "action" && component.Action == item.Item1);
            return existing ?? Action(item.Item1, item.Item2);
        })).ToArray();
        if (tile.Label == label && components.SequenceEqual(tile.Components)) return tile;
        return tile with { Label = label!, Components = components };
    }

    private static MfdTile Field(string field, string label) => new()
    {
        Id = Guid.NewGuid(), Field = field, Label = label, Widget = "value", Size = "normal"
    };

    private static MfdTile Action(string action, string label) => new()
    {
        Id = Guid.NewGuid(), Field = "", Label = label, Widget = "action", Size = "normal", Action = action
    };

    private static string ActionLabel(string action) => string.Join(" ",
        action["v_".Length..].Split('_').Select(word =>
            word.Length == 0 ? "" : char.ToUpperInvariant(word[0]) + word[1..]));
}
