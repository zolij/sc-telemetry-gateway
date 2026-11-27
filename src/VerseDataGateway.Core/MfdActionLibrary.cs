using System.Text.Json;

namespace VerseDataGateway.Core;

public sealed class MfdActionLibrary(string directory)
{
    private const int MaxActions = 4096;
    private const int MaxBytes = 1024 * 1024;
    private sealed record Entry(string ActionName, string Category, bool Holdable);
    private sealed record Document(int SchemaVersion, Entry[] Actions);
    private readonly object gate = new();
    private readonly string path = Path.Combine(directory, "mfd-actions.json");
    private Dictionary<string, Entry>? known;

    // Observed PTU 5.0.202.10025; see docs/GAME-TELEMETRY.md.
    private const string Baseline = """
        lights_controller: v_lights v_lights_off v_lights_on
        player: mobiglas toggle_flashlight v_starmap visor_wipe
        player_emotes: emote_agree emote_angry emote_atease emote_attention emote_blah emote_bored emote_bow emote_burp emote_cheer emote_chicken emote_clap emote_come emote_cry emote_dance emote_disagree emote_failure emote_flex emote_flirt emote_gasp emote_gloat emote_greet emote_laugh emote_launch emote_point emote_rude emote_salute emote_sit emote_sleep emote_smell emote_taunt emote_threaten emote_wait emote_wave emote_whistle
        seat_general: v_eject v_emergency_exit v_enter_remote_turret_1 v_enter_remote_turret_2 v_enter_remote_turret_3 v_light_amplification_off v_light_amplification_on v_light_amplification_toggle v_operator_mode_cycle_back v_operator_mode_cycle_forward v_set_flight_mode v_set_guns_mode v_set_mining_mode v_set_quantum_mode v_set_refuel_mode v_set_salvage_mode v_set_scan_mode
        spaceship_docking: v_toggle_docking_request
        spaceship_general: v_close_all_doors v_flightready v_lock_all_doors v_lock_all_ports v_open_all_doors v_self_destruct v_toggle_all_doorlocks v_toggle_all_doors v_toggle_all_portlocks v_unlock_all_doors v_unlock_all_ports
        spaceship_movement: v_atc_request v_toggle_landing_system v_deploy_landing_system v_retract_landing_system v_flight_advanced_hud_off v_flight_advanced_hud_on v_flight_advanced_hud_toggle v_ifcs_toggle_gforce_safety v_ifcs_gsafe_on v_ifcs_gsafe_off v_ifcs_proximity_assist_off v_ifcs_proximity_assist_on v_ifcs_proximity_assist_toggle v_ifcs_reset_gmeter_max v_ifcs_toggle_esp v_ifcs_vector_decoupling_off v_ifcs_vector_decoupling_on v_ifcs_vector_decoupling_toggle v_master_mode_cycle v_master_mode_set_nav v_master_mode_set_scm v_transform_cycle v_transform_deploy v_transform_retract v_vtol_off v_vtol_on v_vtol_toggle
        spaceship_power: v_power_set_off v_power_set_on v_power_set_shields_off v_power_set_shields_on v_power_set_thrusters_off v_power_set_thrusters_on v_power_set_weapons_off v_power_set_weapons_on v_power_toggle v_power_toggle_shields v_power_toggle_thrusters v_power_toggle_weapons
        spaceship_quantum: v_toggle_qdrive_engagement
        spaceship_radar: v_invoke_ping
        spaceship_target_hailing: v_target_hail
        """;

    internal IReadOnlyList<MfdAction> List(MfdSnapshotStore snapshot)
    {
        lock (gate)
            return Load().Values.Select(entry => MfdActions.Describe(entry.ActionName, entry.Category, entry.Holdable, snapshot)).ToArray();
    }

    internal void Remember(IReadOnlyList<MfdAction> actions)
    {
        lock (gate)
        {
            var next = new Dictionary<string, Entry>(Load(), StringComparer.Ordinal);
            foreach (var action in actions)
                next[action.ActionName] = new(action.ActionName, action.Category, action.Holdable);
            if (next.Count > MaxActions) throw new InvalidDataException("MFD action library exceeds its action limit.");
            if (next.Count == known!.Count && next.All(item => known.TryGetValue(item.Key, out var previous) && previous == item.Value))
                return;
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new Document(1, next.Values.ToArray()), WireJson.Options);
            if (bytes.Length > MaxBytes) throw new InvalidDataException("MFD action library exceeds its size limit.");
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
                known = next;
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
    }

    private Dictionary<string, Entry> Load()
    {
        if (known is not null) return known;
        var result = new Dictionary<string, Entry>(StringComparer.Ordinal);
        foreach (var line in Baseline.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(':', 2);
            foreach (var name in parts[1].Split(' ', StringSplitOptions.RemoveEmptyEntries))
                result.Add(name, new(name, parts[0], name == "v_invoke_ping"));
        }
        FileStream stream;
        try { stream = File.OpenRead(path); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return known = result;
        }
        using (stream)
        {
            if (stream.Length > MaxBytes) throw new InvalidDataException("MFD action library exceeds its size limit.");
            Document document;
            try
            {
                document = JsonSerializer.Deserialize<Document>(stream, WireJson.Options)
                    ?? throw new InvalidDataException("MFD action library cannot be null.");
            }
            catch (JsonException ex) { throw new InvalidDataException("Invalid MFD action library.", ex); }
            if (document.SchemaVersion != 1 || document.Actions is null || document.Actions.Length > MaxActions)
                throw new InvalidDataException("Invalid MFD action library.");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in document.Actions)
            {
                if (entry is null || !MfdActions.ValidName(entry.ActionName) || !MfdActions.ValidName(entry.Category) || !seen.Add(entry.ActionName))
                    throw new InvalidDataException("Invalid MFD action library entry.");
                result[entry.ActionName] = entry;
            }
        }
        if (result.Count > MaxActions) throw new InvalidDataException("MFD action library exceeds its action limit.");
        return known = result;
    }
}
