using System.Diagnostics;
using System.Threading.Channels;
using Grpc.Core;
using Sc.Game.Telemetry.V1;

namespace VerseDataGateway.TelemetryMock;

public sealed class MockState(TimeProvider? timeProvider = null)
{
    private readonly object gate = new();
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly long started = Stopwatch.GetTimestamp();
    private bool seated = true, lights, gSafe = true, esp = true, proximity = true, coupled = true;
    private bool hudScm, hudNav, lightAmplification, power = true, weaponsPower = true, enginesPower = true;
    private bool shieldGenerators = true, shieldEmitters = true;
    private bool doorsOpen, doorsLocked;
    private bool gear, vtol, transformation;
    private long? gearTransitionStarted;
    private VehicleMasterMode mode = VehicleMasterMode.Scm;
    private readonly HashSet<Channel<GameEvent.Types.GameEventType>> subscribers = [];

    public Subscription Subscribe()
    {
        lock (gate)
        {
            var channel = Channel.CreateBounded<GameEvent.Types.GameEventType>(256);
            subscribers.Add(channel);
            return new Subscription(this, channel);
        }
    }

    public void SetSeated(bool value)
    {
        lock (gate)
        {
            if (seated == value) return;
            seated = value;
            Publish(value ? GameEvent.Types.GameEventType.ActorEnterSeat : GameEvent.Types.GameEventType.ActorLeaveSeat);
        }
    }

    public void SetShields(bool generators, bool emitters)
    {
        lock (gate) { shieldGenerators = generators; shieldEmitters = emitters; }
    }

    public void Emit(GameEvent.Types.GameEventType type)
    {
        if (type is GameEvent.Types.GameEventType.Unspecified || !Enum.IsDefined(type))
            throw new ArgumentOutOfRangeException(nameof(type));
        lock (gate) Publish(type);
    }

    private void Publish(GameEvent.Types.GameEventType type)
    {
        foreach (var channel in subscribers.ToArray())
        {
            if (channel.Writer.TryWrite(type)) continue;
            channel.Writer.TryComplete(new RpcException(new Status(StatusCode.ResourceExhausted,
                "Mock event queue overflowed; reconnect the stream.")));
            subscribers.Remove(channel);
        }
    }

    public sealed class Subscription : IDisposable
    {
        private readonly MockState owner;
        private readonly Channel<GameEvent.Types.GameEventType> channel;

        internal Subscription(MockState owner, Channel<GameEvent.Types.GameEventType> channel)
        {
            this.owner = owner;
            this.channel = channel;
        }

        public ChannelReader<GameEvent.Types.GameEventType> Events => channel.Reader;

        public void Dispose()
        {
            lock (owner.gate)
            {
                owner.subscribers.Remove(channel);
                channel.Writer.TryComplete();
            }
        }
    }

    public TelemetryStreamResponse FrameWithEvents(Subscription subscription)
    {
        lock (gate)
        {
            var frame = Frame();
            while (subscription.Events.TryRead(out var type))
            {
                frame.Events ??= new EventsFrame();
                frame.Events.Events.Add(new GameEvent { Type = type });
            }
            return frame;
        }
    }

    public TelemetryStreamResponse Frame()
    {
        lock (gate)
        {
            var elapsed = Stopwatch.GetElapsedTime(started).TotalSeconds;
            var wave = (float)Math.Sin(elapsed / 5);
            var shieldsOperational = shieldGenerators && shieldEmitters;
            var result = new TelemetryStreamResponse
            {
                Actor = new()
                {
                    Vitals = new()
                    {
                        Health = new() { Current = 95, Max = 100 },
                        Oxygen = new() { Current = 98, Max = 100 },
                        Hunger = new() { Current = 75, Max = 100 },
                        Thirst = new() { Current = 70, Max = 100 },
                        BodyTemperature = new() { Current = 36.8f, Max = 42 },
                        HeartRate = 72 + 5 * wave, Gravity = 1, Pressure = 1
                    },
                    State = new()
                    {
                        Locomotion = seated ? ActorLocomotionState.Seated : ActorLocomotionState.OnFoot,
                        CrewRole = seated ? ActorCrewRole.Pilot : ActorCrewRole.Unspecified,
                        SeatName = seated ? "Mock pilot seat" : "", UecBalance = 123456
                    },
                    Environment = new() { ExternalTemperature = 21, BreathableAtmosphere = true }
                }
            };
            if (!seated) return result;

            result.Vehicle = new()
            {
                State = new()
                {
                    Name = "Synthetic ship", ClassName = "mock_ship", Manufacturer = "Mock",
                    Type = VehicleType.Spaceship, LightsEnabled = lights, Health = 0.95f,
                    Doors = new()
                    {
                        OpenState = doorsOpen ? VehicleDoorState.Types.DoorOpenState.Opened : VehicleDoorState.Types.DoorOpenState.Closed,
                        LockState = doorsLocked ? VehicleDoorState.Types.DoorLockState.Locked : VehicleDoorState.Types.DoorLockState.Unlocked
                    },
                    Flight = new()
                    {
                        State = new() { Gear = GearDeployment(), Vtol = Deployment(vtol), Transformation = Deployment(transformation) },
                        Settings = new()
                        {
                            IfcsGSafe = Available(gSafe), IfcsEsp = Available(esp), IfcsProximityAssist = Available(proximity),
                            IfcsCoupled = Available(coupled), AdvancedHudEnabledScm = hudScm, AdvancedHudEnabledNav = hudNav,
                            FlightLightAmplification = lightAmplification, IfcsGravityCompensation = Available(true),
                            IfcsCruiseMode = Available(false), IfcsSpeedLimiter = Available(true),
                            IfcsProximityAssistLanding = Available(true), IfcsStability = Available(true),
                            IfcsCore = Available(true), IfcsWindCompensation = Available(true),
                            IfcsCommandBehaviour = Available(true), AdvancedHudLabels = true, CoursePrediction = true
                        },
                        Instruments = new()
                        {
                            Speed = 150 + 30 * wave, HydrogenFuel = 0.8f, QuantumFuel = 75,
                            GMeter = new() { Current = 1.1f, Max = 3.5f },
                            Altitude = 1200 + 100 * wave, RadarAltitude = 1100 + 100 * wave,
                            VerticalSpeedIndicator = 10 * wave, AtmosphericDensity = 0.6f,
                            Heading = (float)(elapsed * 5 % 360),
                            Boost = new() { Output = 0.4f, Reserves = 0.8f }
                        }
                    },
                    Power = new()
                    {
                        Powerplants = power, Engines = enginesPower, Weapons = weaponsPower,
                        Coolers = power, LifeSupport = power, GravityGenerators = power, Sensors = power,
                        ShieldGenerators = shieldGenerators, ShieldEmitters = shieldEmitters
                    },
                    Annunciators = new()
                    {
                        Pwr = Annunciation(power), Wpn = Annunciation(weaponsPower), Thr = Annunciation(enginesPower),
                        Shld = Annunciation(shieldsOperational), Cool = Annunciation(power), Qdrv = Annunciation(power),
                        Radr = Annunciation(power), Prox = Annunciation(proximity),
                        Hit = VehicleAnnunciator.Types.AnnunciatorState.Nominal,
                        Misl = VehicleAnnunciator.Types.AnnunciatorState.Nominal
                    },
                    Shields = new()
                    {
                        Level = shieldsOperational ? 0.75f : 0,
                        Faces = { shieldsOperational ? 0.8f : 0, shieldsOperational ? 0.7f : 0,
                            shieldsOperational ? 0.75f : 0, shieldsOperational ? 0.75f : 0 }
                    },
                    MasterMode = new() { Mode = mode }
                }
            };
            result.Weapons = new()
            {
                Settings = new() { PrecisionLines = true, UseLagPips = true },
                PresetIndex = 1, PresetName = "Synthetic preset",
                Weapons = { new WeaponState { Name = "Synthetic weapon", IsPowered = weaponsPower,
                    ReadyToFire = weaponsPower, ContainedInPreset = true } },
                Countermeasures =
                {
                    new Countermeasures { Type = Countermeasures.Types.CountermeasureType.Decoy, Amount = 24 },
                    new Countermeasures { Type = Countermeasures.Types.CountermeasureType.Noise, Amount = 5 }
                }
            };
            result.Turret = new() { LightAmplification = lightAmplification };
            result.ForceFeedback = new()
            {
                Angular = Feedback(), Linear = Feedback(),
                StickShaker = 0.1f
            };
            return result;
        }
    }

    public TriggerGameActionResponse Trigger(TriggerGameActionRequest request)
    {
        if (!ActionCatalog.TryGet(request.ActionName, out var action))
            return Response(TriggerGameActionStatus.UnknownAction, "Unknown mock action.");
        if (request.HasHoldMs && !action!.Holdable)
            return Response(TriggerGameActionStatus.HoldNotAllowed, "This mock action does not allow hold.");
        if (request.HasHoldMs && request.HoldMs is 0 or > 5000)
            return Response(TriggerGameActionStatus.Failed, "Hold duration must be between 1 and 5000 ms.");

        lock (gate)
        {
            Action? apply = request.ActionName switch
            {
                "v_lights" => () => lights = !lights,
                "v_lights_on" => () => lights = true,
                "v_lights_off" => () => lights = false,
                "v_toggle_landing_system" => () => SetGear(!gear),
                "v_deploy_landing_system" => () => SetGear(true),
                "v_retract_landing_system" => () => SetGear(false),
                "v_vtol_toggle" => () => vtol = !vtol,
                "v_vtol_on" => () => vtol = true,
                "v_vtol_off" => () => vtol = false,
                "v_transform_cycle" => () => transformation = !transformation,
                "v_transform_deploy" => () => transformation = true,
                "v_transform_retract" => () => transformation = false,
                "v_open_all_doors" => () => doorsOpen = true,
                "v_close_all_doors" => () => doorsOpen = false,
                "v_toggle_all_doors" => () => doorsOpen = !doorsOpen,
                "v_lock_all_doors" => () => doorsLocked = true,
                "v_unlock_all_doors" => () => doorsLocked = false,
                "v_toggle_all_doorlocks" => () => doorsLocked = !doorsLocked,
                "v_ifcs_toggle_gforce_safety" => () => gSafe = !gSafe,
                "v_ifcs_gsafe_on" => () => gSafe = true,
                "v_ifcs_gsafe_off" => () => gSafe = false,
                "v_ifcs_toggle_esp" => () => esp = !esp,
                "v_ifcs_proximity_assist_toggle" => () => proximity = !proximity,
                "v_ifcs_proximity_assist_on" => () => proximity = true,
                "v_ifcs_proximity_assist_off" => () => proximity = false,
                "v_ifcs_vector_decoupling_toggle" => () => coupled = !coupled,
                "v_ifcs_vector_decoupling_on" => () => coupled = false,
                "v_ifcs_vector_decoupling_off" => () => coupled = true,
                "v_master_mode_cycle" => () => mode = mode == VehicleMasterMode.Scm ? VehicleMasterMode.Navigation : VehicleMasterMode.Scm,
                "v_master_mode_set_nav" => () => mode = VehicleMasterMode.Navigation,
                "v_master_mode_set_scm" => () => mode = VehicleMasterMode.Scm,
                "v_flight_advanced_hud_toggle" => () => SetHud(!(mode == VehicleMasterMode.Scm ? hudScm : hudNav)),
                "v_flight_advanced_hud_on" => () => SetHud(true),
                "v_flight_advanced_hud_off" => () => SetHud(false),
                "v_light_amplification_toggle" => () => lightAmplification = !lightAmplification,
                "v_light_amplification_on" => () => lightAmplification = true,
                "v_light_amplification_off" => () => lightAmplification = false,
                "v_power_toggle" => () => SetAllPower(!power),
                "v_power_set_on" => () => SetAllPower(true),
                "v_power_set_off" => () => SetAllPower(false),
                "v_power_toggle_weapons" => () => weaponsPower = !weaponsPower,
                "v_power_set_weapons_on" => () => weaponsPower = true,
                "v_power_set_weapons_off" => () => weaponsPower = false,
                "v_power_toggle_thrusters" => () => enginesPower = !enginesPower,
                "v_power_set_thrusters_on" => () => enginesPower = true,
                "v_power_set_thrusters_off" => () => enginesPower = false,
                "v_power_toggle_shields" => () => SetShields(!(shieldGenerators && shieldEmitters), !(shieldGenerators && shieldEmitters)),
                "v_power_set_shields_on" => () => SetShields(true, true),
                "v_power_set_shields_off" => () => SetShields(false, false),
                _ => null
            };
            if (apply is null) return Response(TriggerGameActionStatus.Failed, "This action is not simulated by the mock.");
            if (!seated) return Response(TriggerGameActionStatus.MapInactive, "The mock vehicle action map is inactive on foot.");
            apply();
            return Response(TriggerGameActionStatus.Ok, "Simulated action.");
        }
    }

    private void SetAllPower(bool value) => power = weaponsPower = enginesPower = shieldGenerators = shieldEmitters = value;
    private void SetHud(bool value)
    {
        if (mode == VehicleMasterMode.Scm) hudScm = value;
        else hudNav = value;
    }
    private void SetGear(bool value)
    {
        if (gear == value) return;
        gear = value;
        gearTransitionStarted = clock.GetTimestamp();
    }
    private VehicleComponentDeploymentState GearDeployment()
    {
        if (gearTransitionStarted is long started && clock.GetElapsedTime(started) < TimeSpan.FromSeconds(2))
            return gear ? VehicleComponentDeploymentState.Deploying : VehicleComponentDeploymentState.Retracting;
        gearTransitionStarted = null;
        return Deployment(gear);
    }
    private static VehicleAnnunciator.Types.AnnunciatorState Annunciation(bool enabled) => enabled
        ? VehicleAnnunciator.Types.AnnunciatorState.Nominal : VehicleAnnunciator.Types.AnnunciatorState.Off;
    private static FeedbackAxes Feedback() => new()
    {
        X = new() { Resistance = 0.2f, Damping = 0.1f },
        Y = new() { Resistance = 0.15f, Damping = 0.1f },
        Z = new() { Resistance = 0.1f, Damping = 0.1f }
    };
    private static Availability Available(bool enabled) => new() { Enabled = enabled, Active = enabled };
    private static VehicleComponentDeploymentState Deployment(bool value) => value
        ? VehicleComponentDeploymentState.Deployed : VehicleComponentDeploymentState.Retracted;
    private static TriggerGameActionResponse Response(TriggerGameActionStatus status, string message) =>
        new() { Status = status, Message = message };
}
