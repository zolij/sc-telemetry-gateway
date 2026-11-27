using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Sc.Game.Telemetry.V1;
using VerseDataGateway.Core;
using VerseDataGateway.TelemetryMock;

namespace VerseDataGateway.Tests;

public sealed class TelemetryMockTests
{
    [Fact]
    public async Task GameProtocolStreamsChangingStateAndValidatesActions()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var app = MockHost.Build(0);
        await app.StartAsync(deadline.Token);
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!;
        using var channel = GrpcChannel.ForAddress(Assert.Single(addresses.Addresses));
        var client = new GameTelemetryService.GameTelemetryServiceClient(channel);
        var catalog = await client.ListTriggerableActionsAsync(new(), cancellationToken: deadline.Token);
        Assert.Equal(112, catalog.Actions.Count);
        Assert.Equal(112, catalog.Actions.Select(action => action.ActionName).Distinct().Count());
        var documentation = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "GAME-TELEMETRY.md"), deadline.Token);
        var catalogSection = documentation.Split("## Observed action catalogue", StringSplitOptions.None)[1];
        var lines = catalogSection.Split("```text", StringSplitOptions.None)[1]
            .Split("```", StringSplitOptions.None)[0].Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var documented = new Dictionary<string, string>(StringComparer.Ordinal);
        var category = "";
        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (!char.IsWhiteSpace(line[0])) category = line.Split(' ')[0];
            else documented.Add(line.Trim(), category);
        }
        Assert.Equal(documented.Count, catalog.Actions.Count);
        foreach (var action in catalog.Actions)
            Assert.Equal(documented[action.ActionName], action.Category);
        var ping = Assert.Single(catalog.Actions, action => action.Holdable);
        Assert.Equal("v_invoke_ping", ping.ActionName);
        Assert.Contains(catalog.Actions, action => action.ActionName == "v_lights_on" && action.Category == "lights_controller");

        using var stream = client.TelemetryStream(new(), cancellationToken: deadline.Token);
        Assert.True(await stream.ResponseStream.MoveNext(deadline.Token));
        var first = stream.ResponseStream.Current;
        Assert.NotNull(first.Vehicle);
        Assert.NotNull(first.Weapons);
        Assert.False(first.Vehicle.State.LightsEnabled);

        var unknown = await client.TriggerGameActionAsync(new() { ActionName = "not_a_game_action" }, cancellationToken: deadline.Token);
        Assert.Equal(TriggerGameActionStatus.UnknownAction, unknown.Status);
        var hold = await client.TriggerGameActionAsync(new() { ActionName = "v_lights_on", HoldMs = 100 }, cancellationToken: deadline.Token);
        Assert.Equal(TriggerGameActionStatus.HoldNotAllowed, hold.Status);
        var invalidHold = await client.TriggerGameActionAsync(new() { ActionName = "v_invoke_ping", HoldMs = 0 }, cancellationToken: deadline.Token);
        Assert.Equal(TriggerGameActionStatus.Failed, invalidHold.Status);
        var unsupported = await client.TriggerGameActionAsync(new() { ActionName = "v_invoke_ping", HoldMs = 10 }, cancellationToken: deadline.Token);
        Assert.Equal(TriggerGameActionStatus.Failed, unsupported.Status);
        Assert.Contains("not simulated", unsupported.Message);
        var changed = await client.TriggerGameActionAsync(new() { ActionName = "v_lights_on" }, cancellationToken: deadline.Token);
        Assert.Equal(TriggerGameActionStatus.Ok, changed.Status);
        var lit = await NextMatching(stream.ResponseStream, frame => frame.Vehicle?.State.LightsEnabled == true, deadline.Token);
        Assert.True(lit.Vehicle.State.LightsEnabled);
        var later = await NextMatching(stream.ResponseStream,
            frame => frame.Actor.Vitals.HeartRate != first.Actor.Vitals.HeartRate, deadline.Token);
        Assert.NotEqual(first.Actor.Vitals.HeartRate, later.Actor.Vitals.HeartRate);

        var off = await client.TriggerGameActionAsync(new() { ActionName = "v_power_set_weapons_off" }, cancellationToken: deadline.Token);
        Assert.Equal(TriggerGameActionStatus.Ok, off.Status);
        var noWeaponsPower = await NextMatching(stream.ResponseStream, frame => frame.Vehicle?.State.Power.Weapons == false, deadline.Token);
        Assert.False(Assert.Single(noWeaponsPower.Weapons.Weapons).IsPowered);
    }

    [Fact]
    public async Task EventsReachActiveStreamsOnceAndSeatChangesAreVisible()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var app = MockHost.Build(0);
        await app.StartAsync(deadline.Token);
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!;
        using var channel = GrpcChannel.ForAddress(Assert.Single(addresses.Addresses));
        var client = new GameTelemetryService.GameTelemetryServiceClient(channel);
        var state = app.Services.GetRequiredService<MockState>();
        state.Emit(GameEvent.Types.GameEventType.ActorSpawn);
        using var first = client.TelemetryStream(new(), cancellationToken: deadline.Token);
        using var second = client.TelemetryStream(new(), cancellationToken: deadline.Token);
        Assert.True(await first.ResponseStream.MoveNext(deadline.Token));
        Assert.True(await second.ResponseStream.MoveNext(deadline.Token));
        Assert.Null(first.ResponseStream.Current.Events);
        Assert.Null(second.ResponseStream.Current.Events);
        Assert.Contains("on foot", MockConsole.Execute(state, "foot"));
        foreach (var reader in new[] { first.ResponseStream, second.ResponseStream })
        {
            var frame = await NextMatching(reader,
                item => item.Events?.Events.Any(e => e.Type == GameEvent.Types.GameEventType.ActorLeaveSeat) == true, deadline.Token);
            Assert.Null(frame.Vehicle);
            Assert.Equal(ActorLocomotionState.OnFoot, frame.Actor.State.Locomotion);
            Assert.Single(frame.Events.Events);
            for (var i = 0; i < 3; i++)
            {
                Assert.True(await reader.MoveNext(deadline.Token));
                Assert.Null(reader.Current.Events);
            }
        }
        var inactive = await client.TriggerGameActionAsync(new() { ActionName = "v_lights_on" }, cancellationToken: deadline.Token);
        Assert.Equal(TriggerGameActionStatus.MapInactive, inactive.Status);
        var unsupported = await client.TriggerGameActionAsync(new() { ActionName = "emote_wave" }, cancellationToken: deadline.Token);
        Assert.Equal(TriggerGameActionStatus.Failed, unsupported.Status);
        Assert.Contains("not simulated", unsupported.Message);
        Assert.Contains("Invalid", MockConsole.Execute(state, "event no-such-event"));
        Assert.Contains("Emitted", MockConsole.Execute(state, "event downed"));
        var downed = await NextMatching(first.ResponseStream,
            item => item.Events?.Events.Any(e => e.Type == GameEvent.Types.GameEventType.ActorDowned) == true, deadline.Token);
        Assert.Single(downed.Events.Events);
        using var newStream = client.TelemetryStream(new(), cancellationToken: deadline.Token);
        Assert.True(await newStream.ResponseStream.MoveNext(deadline.Token));
        Assert.Null(newStream.ResponseStream.Current.Events);
        MockConsole.Execute(state, "seat");
        var seated = await NextMatching(newStream.ResponseStream,
            item => item.Events?.Events.Any(e => e.Type == GameEvent.Types.GameEventType.ActorEnterSeat) == true, deadline.Token);
        Assert.Equal(ActorLocomotionState.Seated, seated.Actor.State.Locomotion);
        Assert.NotNull(seated.Vehicle);
    }

    [Fact]
    public void VehicleControlsChangeTheAssociatedSyntheticFields()
    {
        var state = new MockState();
        void Trigger(string name) =>
            Assert.Equal(TriggerGameActionStatus.Ok, state.Trigger(new() { ActionName = name }).Status);

        Trigger("v_deploy_landing_system");
        Trigger("v_vtol_on");
        Trigger("v_transform_deploy");
        Trigger("v_ifcs_gsafe_off");
        Trigger("v_master_mode_set_nav");
        Trigger("v_power_set_shields_off");
        var ship = state.Frame().Vehicle.State;
        Assert.Equal(VehicleComponentDeploymentState.Deploying, ship.Flight.State.Gear);
        Assert.Equal(VehicleComponentDeploymentState.Deployed, ship.Flight.State.Vtol);
        Assert.Equal(VehicleComponentDeploymentState.Deployed, ship.Flight.State.Transformation);
        Assert.False(ship.Flight.Settings.IfcsGSafe.Active);
        Assert.Equal(VehicleMasterMode.Navigation, ship.MasterMode.Mode);
        Assert.False(ship.Power.ShieldGenerators);
        Assert.All(ship.Shields.Faces, face => Assert.Equal(0, face));
        Trigger("v_power_set_shields_on");
        Assert.True(state.Frame().Vehicle.State.Power.ShieldEmitters);
    }

    [Fact]
    public void CompositeCardStatesCanBeExercisedIndependently()
    {
        var clock = new ManualTimeProvider();
        var state = new MockState(clock);
        void Trigger(string name) =>
            Assert.Equal(TriggerGameActionStatus.Ok, state.Trigger(new() { ActionName = name }).Status);

        Trigger("v_toggle_landing_system");
        Assert.Equal(VehicleComponentDeploymentState.Deploying, state.Frame().Vehicle.State.Flight.State.Gear);
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(VehicleComponentDeploymentState.Deployed, state.Frame().Vehicle.State.Flight.State.Gear);
        Trigger("v_toggle_landing_system");
        Assert.Equal(VehicleComponentDeploymentState.Retracting, state.Frame().Vehicle.State.Flight.State.Gear);
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(VehicleComponentDeploymentState.Retracted, state.Frame().Vehicle.State.Flight.State.Gear);

        Trigger("v_flight_advanced_hud_toggle");
        Trigger("v_master_mode_cycle");
        var settings = state.Frame().Vehicle.State.Flight.Settings;
        Assert.True(settings.AdvancedHudEnabledScm);
        Assert.False(settings.AdvancedHudEnabledNav);
        Trigger("v_flight_advanced_hud_toggle");
        Trigger("v_master_mode_cycle");
        Trigger("v_flight_advanced_hud_toggle");
        settings = state.Frame().Vehicle.State.Flight.Settings;
        Assert.False(settings.AdvancedHudEnabledScm);
        Assert.True(settings.AdvancedHudEnabledNav);

        Assert.Contains("mixed", MockConsole.Execute(state, "shields mixed"));
        var power = state.Frame().Vehicle.State.Power;
        Assert.True(power.ShieldGenerators);
        Assert.False(power.ShieldEmitters);
        Trigger("v_power_toggle_shields");
        power = state.Frame().Vehicle.State.Power;
        Assert.True(power.ShieldGenerators);
        Assert.True(power.ShieldEmitters);
        Trigger("v_power_toggle_shields");
        power = state.Frame().Vehicle.State.Power;
        Assert.False(power.ShieldGenerators);
        Assert.False(power.ShieldEmitters);
        Assert.Contains("Invalid", MockConsole.Execute(state, "shields invalid"));
        Assert.Contains("on", MockConsole.Execute(state, "shields on"));
        Assert.True(state.Frame().Vehicle.State.Power.ShieldEmitters);
        Assert.Contains("off", MockConsole.Execute(state, "shields off"));
        Assert.False(state.Frame().Vehicle.State.Power.ShieldGenerators);

        Trigger("v_ifcs_vector_decoupling_on");
        Assert.False(state.Frame().Vehicle.State.Flight.Settings.IfcsCoupled.Active);
        Trigger("v_ifcs_vector_decoupling_off");
        Assert.True(state.Frame().Vehicle.State.Flight.Settings.IfcsCoupled.Active);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        public void Advance(TimeSpan duration) => ticks += duration.Ticks;
    }

    [Fact]
    public async Task SlowEventSubscriberFailsExplicitlyInsteadOfDroppingEvents()
    {
        var state = new MockState();
        using var subscription = state.Subscribe();
        for (var i = 0; i < 257; i++)
            state.Emit(GameEvent.Types.GameEventType.ActorDowned);
        while (subscription.Events.TryRead(out _)) { }
        var error = await Assert.ThrowsAsync<RpcException>(async () => await subscription.Events.Completion);
        Assert.Equal(StatusCode.ResourceExhausted, error.StatusCode);
    }

    [Fact]
    public async Task GatewayReaderConsumesMockStreamAndStops()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var app = MockHost.Build(0);
        await app.StartAsync(deadline.Token);
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!;
        var port = new Uri(Assert.Single(addresses.Addresses)).Port;
        var snapshot = new MfdSnapshotStore(["vehicle.state.lights_enabled", "actor.vitals.heart_rate",
            "vehicle.state.flight.state.gear", "vehicle.state.power.shield_generators",
            "vehicle.state.power.shield_emitters", "vehicle.state.flight.settings.advanced_hud_enabled_scm",
            "vehicle.state.flight.settings.advanced_hud_enabled_nav", "vehicle.state.master_mode.mode"]);
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var reader = new GameTelemetryReader(port);
        var run = reader.RunAsync([], _ => { }, stop.Token, snapshot.Disconnected, snapshot.StartStream, (frame, payload) =>
        {
            snapshot.Ingest(frame, payload);
            received.TrySetResult();
        });
        try
        {
            await received.Task.WaitAsync(deadline.Token);
            Assert.Equal("connected", snapshot.Current().ConnectionStatus);
            Assert.True(snapshot.Current().Fields.ContainsKey("actor.vitals.heart_rate"));
            app.Services.GetRequiredService<MockState>().Trigger(new() { ActionName = "v_lights_on" });
            while (!snapshot.Current().Fields.TryGetValue("vehicle.state.lights_enabled", out var value) ||
                   value.Value?.GetValue<bool>() != true)
                await Task.Delay(20, deadline.Token);
            var state = app.Services.GetRequiredService<MockState>();
            state.SetShields(true, false);
            Assert.Equal(TriggerGameActionStatus.Ok, state.Trigger(new() { ActionName = "v_flight_advanced_hud_toggle" }).Status);
            Assert.Equal(TriggerGameActionStatus.Ok, state.Trigger(new() { ActionName = "v_master_mode_cycle" }).Status);
            Assert.Equal(TriggerGameActionStatus.Ok, state.Trigger(new() { ActionName = "v_toggle_landing_system" }).Status);
            while (true)
            {
                var fields = snapshot.Current().Fields;
                if (fields.TryGetValue("vehicle.state.flight.state.gear", out var gear) &&
                    gear.Value?.GetValue<string>() == "VEHICLE_COMPONENT_DEPLOYMENT_STATE_DEPLOYING" &&
                    fields["vehicle.state.power.shield_generators"].Value?.GetValue<bool>() == true &&
                    fields["vehicle.state.power.shield_emitters"].Value?.GetValue<bool>() == false &&
                    fields["vehicle.state.flight.settings.advanced_hud_enabled_scm"].Value?.GetValue<bool>() == true &&
                    fields["vehicle.state.flight.settings.advanced_hud_enabled_nav"].Value?.GetValue<bool>() == false &&
                    fields["vehicle.state.master_mode.mode"].Value?.GetValue<string>() == "VEHICLE_MASTER_MODE_NAVIGATION") break;
                await Task.Delay(20, deadline.Token);
            }
        }
        finally
        {
            await stop.CancelAsync();
            try { await run; }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }
    }

    private static async Task<TelemetryStreamResponse> NextMatching(
        IAsyncStreamReader<TelemetryStreamResponse> stream, Func<TelemetryStreamResponse, bool> predicate,
        CancellationToken cancellationToken)
    {
        while (await stream.MoveNext(cancellationToken))
            if (predicate(stream.Current)) return stream.Current;
        throw new InvalidOperationException("The mock stream ended before the expected frame.");
    }
}
