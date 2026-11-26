using Sc.Game.Telemetry.V1;

namespace VerseDataGateway.TelemetryMock;

public static class ActionCatalog
{
    private static readonly IReadOnlyDictionary<string, TriggerableAction> Actions = Create();

    public static ListTriggerableActionsResponse List()
    {
        var result = new ListTriggerableActionsResponse();
        result.Actions.Add(Actions.Values.Select(action => action.Clone()));
        return result;
    }

    public static bool TryGet(string name, out TriggerableAction? action) => Actions.TryGetValue(name, out action);

    private static IReadOnlyDictionary<string, TriggerableAction> Create()
    {
        var actions = new Dictionary<string, TriggerableAction>(StringComparer.Ordinal);
        void Add(string category, string names)
        {
            foreach (var name in names.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                actions.Add(name, new TriggerableAction
                {
                    ActionName = name, Category = category, Holdable = name == "v_invoke_ping"
                });
        }

        Add("lights_controller", "v_lights v_lights_off v_lights_on");
        Add("player", "mobiglas toggle_flashlight v_starmap visor_wipe");
        Add("player_emotes", """
            emote_agree emote_angry emote_atease emote_attention emote_blah emote_bored emote_bow
            emote_burp emote_cheer emote_chicken emote_clap emote_come emote_cry emote_dance
            emote_disagree emote_failure emote_flex emote_flirt emote_gasp emote_gloat emote_greet
            emote_laugh emote_launch emote_point emote_rude emote_salute emote_sit emote_sleep
            emote_smell emote_taunt emote_threaten emote_wait emote_wave emote_whistle
            """.Replace('\n', ' ').Replace('\r', ' '));
        Add("seat_general", """
            v_eject v_emergency_exit v_enter_remote_turret_1 v_enter_remote_turret_2 v_enter_remote_turret_3
            v_light_amplification_off v_light_amplification_on v_light_amplification_toggle
            v_operator_mode_cycle_back v_operator_mode_cycle_forward v_set_flight_mode v_set_guns_mode
            v_set_mining_mode v_set_quantum_mode v_set_refuel_mode v_set_salvage_mode v_set_scan_mode
            """.Replace('\n', ' ').Replace('\r', ' '));
        Add("spaceship_docking", "v_toggle_docking_request");
        Add("spaceship_general", """
            v_close_all_doors v_flightready v_lock_all_doors v_lock_all_ports v_open_all_doors
            v_self_destruct v_toggle_all_doorlocks v_toggle_all_doors v_toggle_all_portlocks
            v_unlock_all_doors v_unlock_all_ports
            """.Replace('\n', ' ').Replace('\r', ' '));
        Add("spaceship_movement", """
            v_atc_request v_toggle_landing_system v_deploy_landing_system v_retract_landing_system
            v_flight_advanced_hud_off v_flight_advanced_hud_on v_flight_advanced_hud_toggle
            v_ifcs_toggle_gforce_safety v_ifcs_gsafe_on v_ifcs_gsafe_off v_ifcs_proximity_assist_off
            v_ifcs_proximity_assist_on v_ifcs_proximity_assist_toggle v_ifcs_reset_gmeter_max
            v_ifcs_toggle_esp v_ifcs_vector_decoupling_off v_ifcs_vector_decoupling_on
            v_ifcs_vector_decoupling_toggle v_master_mode_cycle v_master_mode_set_nav v_master_mode_set_scm
            v_transform_cycle v_transform_deploy v_transform_retract v_vtol_off v_vtol_on v_vtol_toggle
            """.Replace('\n', ' ').Replace('\r', ' '));
        Add("spaceship_power", """
            v_power_set_off v_power_set_on v_power_set_shields_off v_power_set_shields_on
            v_power_set_thrusters_off v_power_set_thrusters_on v_power_set_weapons_off v_power_set_weapons_on
            v_power_toggle v_power_toggle_shields v_power_toggle_thrusters v_power_toggle_weapons
            """.Replace('\n', ' ').Replace('\r', ' '));
        Add("spaceship_quantum", "v_toggle_qdrive_engagement");
        Add("spaceship_radar", "v_invoke_ping");
        Add("spaceship_target_hailing", "v_target_hail");
        return actions;
    }
}
