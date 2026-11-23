using Grpc.Core;
using Grpc.Net.Client;
using Sc.Game.Telemetry.V1;

namespace VerseDataGateway.Core;

internal sealed class MfdActions(int port, MfdSnapshotStore snapshot) : IDisposable
{
    public const uint MaxHoldMilliseconds = 5000;
    private readonly GrpcChannel channel = GrpcChannel.ForAddress($"http://127.0.0.1:{port}",
        new GrpcChannelOptions { MaxReceiveMessageSize = 1024 * 1024 });
    private readonly SemaphoreSlim triggerLock = new(1, 1);
    private long lastTrigger;

    private static readonly IReadOnlyDictionary<string, string> FieldByAction =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["v_lights"] = "vehicle.state.lights_enabled",
            ["v_lights_on"] = "vehicle.state.lights_enabled",
            ["v_lights_off"] = "vehicle.state.lights_enabled",
            ["v_toggle_landing_system"] = "vehicle.state.flight.state.gear",
            ["v_deploy_landing_system"] = "vehicle.state.flight.state.gear",
            ["v_retract_landing_system"] = "vehicle.state.flight.state.gear",
            ["v_vtol_off"] = "vehicle.state.flight.state.vtol",
            ["v_vtol_on"] = "vehicle.state.flight.state.vtol",
            ["v_vtol_toggle"] = "vehicle.state.flight.state.vtol",
            ["v_transform_cycle"] = "vehicle.state.flight.state.transformation",
            ["v_transform_deploy"] = "vehicle.state.flight.state.transformation",
            ["v_transform_retract"] = "vehicle.state.flight.state.transformation",
            ["v_close_all_doors"] = "vehicle.state.doors.open_state",
            ["v_open_all_doors"] = "vehicle.state.doors.open_state",
            ["v_toggle_all_doors"] = "vehicle.state.doors.open_state",
            ["v_lock_all_doors"] = "vehicle.state.doors.lock_state",
            ["v_unlock_all_doors"] = "vehicle.state.doors.lock_state",
            ["v_toggle_all_doorlocks"] = "vehicle.state.doors.lock_state",
            ["v_ifcs_toggle_gforce_safety"] = "vehicle.state.flight.settings.ifcs_g_safe.active",
            ["v_ifcs_gsafe_on"] = "vehicle.state.flight.settings.ifcs_g_safe.active",
            ["v_ifcs_gsafe_off"] = "vehicle.state.flight.settings.ifcs_g_safe.active",
            ["v_ifcs_toggle_esp"] = "vehicle.state.flight.settings.ifcs_esp.active",
            ["v_ifcs_proximity_assist_off"] = "vehicle.state.flight.settings.ifcs_proximity_assist.active",
            ["v_ifcs_proximity_assist_on"] = "vehicle.state.flight.settings.ifcs_proximity_assist.active",
            ["v_ifcs_proximity_assist_toggle"] = "vehicle.state.flight.settings.ifcs_proximity_assist.active",
            ["v_ifcs_vector_decoupling_off"] = "vehicle.state.flight.settings.ifcs_coupled.active",
            ["v_ifcs_vector_decoupling_on"] = "vehicle.state.flight.settings.ifcs_coupled.active",
            ["v_ifcs_vector_decoupling_toggle"] = "vehicle.state.flight.settings.ifcs_coupled.active",
            ["v_master_mode_cycle"] = "vehicle.state.master_mode.mode",
            ["v_master_mode_set_nav"] = "vehicle.state.master_mode.mode",
            ["v_master_mode_set_scm"] = "vehicle.state.master_mode.mode",
            ["v_power_set_weapons_off"] = "vehicle.state.power.weapons",
            ["v_power_set_weapons_on"] = "vehicle.state.power.weapons",
            ["v_power_toggle_weapons"] = "vehicle.state.power.weapons"
        };

    private static readonly IReadOnlyDictionary<string, string[]> FieldsByAction = BuildAdditionalMappings();

    private static IReadOnlyDictionary<string, string[]> BuildAdditionalMappings()
    {
        var mappings = new Dictionary<string, string[]>(StringComparer.Ordinal);
        void Add(string[] fields, params string[] actions)
        {
            foreach (var action in actions) mappings.Add(action, fields);
        }

        Add(["vehicle.state.power.shield_generators", "vehicle.state.power.shield_emitters",
                "vehicle.state.shields.level", "vehicle.state.shields.faces"],
            "v_power_toggle_shields", "v_power_set_shields_on", "v_power_set_shields_off");
        Add(["vehicle.state.power.engines"],
            "v_power_toggle_thrusters", "v_power_set_thrusters_on", "v_power_set_thrusters_off");
        Add(["vehicle.state.power.powerplants", "vehicle.state.power.engines", "vehicle.state.power.weapons",
                "vehicle.state.power.coolers", "vehicle.state.power.life_support", "vehicle.state.power.gravity_generators",
                "vehicle.state.power.sensors", "vehicle.state.power.shield_generators", "vehicle.state.power.shield_emitters"],
            "v_power_toggle", "v_power_set_on", "v_power_set_off");
        Add(["vehicle.state.flight.settings.advanced_hud_enabled_scm", "vehicle.state.flight.settings.advanced_hud_enabled_nav"],
            "v_flight_advanced_hud_toggle", "v_flight_advanced_hud_on", "v_flight_advanced_hud_off");
        Add(["vehicle.state.flight.settings.flight_light_amplification", "turret.light_amplification"],
            "v_light_amplification_toggle", "v_light_amplification_on", "v_light_amplification_off");
        return mappings;
    }

    public async Task<IReadOnlyList<MfdAction>> ListAsync(CancellationToken cancellationToken)
    {
        var client = new GameTelemetryService.GameTelemetryServiceClient(channel);
        var response = await client.ListTriggerableActionsAsync(new(),
            deadline: DateTime.UtcNow.AddSeconds(5), cancellationToken: cancellationToken);
        if (response.Actions.Count > 512)
            throw new InvalidDataException("The game returned too many actions.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<MfdAction>();
        foreach (var action in response.Actions)
        {
            if (!ValidName(action.ActionName) || !ValidName(action.Category) || !seen.Add(action.ActionName))
                throw new InvalidDataException("The game returned an invalid action list.");
            var fields = FieldsForAction(action.ActionName);
            var field = fields.FirstOrDefault();
            result.Add(new(action.ActionName, action.Category, action.Holdable, field,
                fields.Count == 0 || fields.Any(item => snapshot.SelectedFields.Contains(item, StringComparer.Ordinal)),
                field is null ? null : action.ActionName is "v_lights_on" or "v_lights_off", fields));
        }
        return result;
    }

    public async Task<TriggerGameActionResponse> TriggerAsync(string name, uint? holdMs,
        Func<Action, bool> dispatchIfAuthorized, CancellationToken cancellationToken)
    {
        if (!ValidName(name)) throw new ArgumentException("Invalid action name.");
        if (holdMs is 0 or > MaxHoldMilliseconds)
            throw new ArgumentException($"Hold duration must be between 1 and {MaxHoldMilliseconds} milliseconds.");
        if (!await triggerLock.WaitAsync(0, cancellationToken))
            throw new MfdActionBusyException();
        try
        {
            var actions = await ListAsync(cancellationToken);
            var action = actions.SingleOrDefault(item => item.ActionName == name)
                ?? throw new ArgumentException("This action is not currently listed by the game.");
            if (!action.Enabled) throw new MfdActionForbiddenException();
            if (holdMs.HasValue && !action.Holdable)
                throw new ArgumentException("This action does not support a hold duration.");
            if (snapshot.Current().ConnectionStatus != "connected")
                throw new MfdActionForbiddenException();
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (lastTrigger != 0 && System.Diagnostics.Stopwatch.GetElapsedTime(lastTrigger, now) < TimeSpan.FromMilliseconds(250))
                throw new MfdActionBusyException();
            var request = new TriggerGameActionRequest { ActionName = name };
            if (holdMs.HasValue) request.HoldMs = holdMs.Value;
            var client = new GameTelemetryService.GameTelemetryServiceClient(channel);
            AsyncUnaryCall<TriggerGameActionResponse>? call = null;
            if (!dispatchIfAuthorized(() => call = client.TriggerGameActionAsync(request,
                deadline: DateTime.UtcNow.AddSeconds(5), cancellationToken: cancellationToken)))
                throw new MfdActionForbiddenException();
            lastTrigger = now;
            using (call!)
                return await call!.ResponseAsync;
        }
        finally { triggerLock.Release(); }
    }

    internal static IReadOnlyList<string> FieldsForAction(string name) =>
        FieldsByAction.TryGetValue(name, out var fields) ? fields :
        FieldByAction.TryGetValue(name, out var field) ? [field] : [];

    internal static bool ValidName(string? value) => value is { Length: > 0 and <= 128 } &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    public void Dispose()
    {
        channel.Dispose();
        triggerLock.Dispose();
    }
}

internal sealed record MfdAction(string ActionName, string Category, bool Holdable, string? Field, bool Enabled,
    bool? MappingVerified, IReadOnlyList<string> Fields);
internal sealed class MfdActionBusyException : Exception;
internal sealed class MfdActionForbiddenException : Exception;
