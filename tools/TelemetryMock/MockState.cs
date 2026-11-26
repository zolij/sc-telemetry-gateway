using System.Diagnostics;
using Sc.Game.Telemetry.V1;

namespace VerseDataGateway.TelemetryMock;

public sealed class MockState
{
    private readonly object gate = new();
    private readonly long started = Stopwatch.GetTimestamp();
    private bool seated = true, lights, gSafe = true, esp = true, proximity = true, coupled = true;
    private bool hud, lightAmplification, power = true, weaponsPower = true, enginesPower = true, shieldsPower = true;
    private bool doorsOpen, doorsLocked;
    private bool gear, vtol, transformation;
    private VehicleMasterMode mode = VehicleMasterMode.Scm;

    public TelemetryStreamResponse Frame()
    {
        lock (gate)
        {
            var elapsed = Stopwatch.GetElapsedTime(started).TotalSeconds;
            var wave = (float)Math.Sin(elapsed / 5);
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
                        State = new() { Gear = Deployment(gear), Vtol = Deployment(vtol), Transformation = Deployment(transformation) },
                        Settings = new()
                        {
                            IfcsGSafe = Available(gSafe), IfcsEsp = Available(esp), IfcsProximityAssist = Available(proximity),
                            IfcsCoupled = Available(coupled), AdvancedHudEnabledScm = hud, AdvancedHudEnabledNav = hud,
                            FlightLightAmplification = lightAmplification
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
                        ShieldGenerators = shieldsPower, ShieldEmitters = shieldsPower
                    },
                    Shields = new() { Level = shieldsPower ? 0.75f : 0, Faces = { 0.8f, 0.7f, 0.75f, 0.75f } },
                    MasterMode = new() { Mode = mode }
                }
            };
            result.Weapons = new()
            {
                PresetIndex = 1, PresetName = "Synthetic preset",
                Weapons = { new WeaponState { Name = "Synthetic weapon", IsPowered = weaponsPower, ReadyToFire = weaponsPower } },
                Countermeasures =
                {
                    new Countermeasures { Type = Countermeasures.Types.CountermeasureType.Decoy, Amount = 24 },
                    new Countermeasures { Type = Countermeasures.Types.CountermeasureType.Noise, Amount = 5 }
                }
            };
            result.Turret = new() { LightAmplification = lightAmplification };
            result.ForceFeedback = new()
            {
                Angular = new() { X = new() { Resistance = 0.2f, Damping = 0.1f } },
                Linear = new() { Z = new() { Resistance = 0.1f, Damping = 0.1f } },
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
            if (!seated) return Response(TriggerGameActionStatus.MapInactive, "The mock vehicle action map is inactive on foot.");
            switch (request.ActionName)
            {
                case "v_lights": lights = !lights; break;
                case "v_lights_on": lights = true; break;
                case "v_lights_off": lights = false; break;
                case "v_toggle_landing_system": gear = !gear; break;
                case "v_deploy_landing_system": gear = true; break;
                case "v_retract_landing_system": gear = false; break;
                case "v_vtol_toggle": vtol = !vtol; break;
                case "v_vtol_on": vtol = true; break;
                case "v_vtol_off": vtol = false; break;
                case "v_transform_cycle": transformation = !transformation; break;
                case "v_transform_deploy": transformation = true; break;
                case "v_transform_retract": transformation = false; break;
                case "v_open_all_doors": doorsOpen = true; break;
                case "v_close_all_doors": doorsOpen = false; break;
                case "v_toggle_all_doors": doorsOpen = !doorsOpen; break;
                case "v_lock_all_doors": doorsLocked = true; break;
                case "v_unlock_all_doors": doorsLocked = false; break;
                case "v_toggle_all_doorlocks": doorsLocked = !doorsLocked; break;
                case "v_ifcs_toggle_gforce_safety": gSafe = !gSafe; break;
                case "v_ifcs_gsafe_on": gSafe = true; break;
                case "v_ifcs_gsafe_off": gSafe = false; break;
                case "v_ifcs_toggle_esp": esp = !esp; break;
                case "v_ifcs_proximity_assist_toggle": proximity = !proximity; break;
                case "v_ifcs_proximity_assist_on": proximity = true; break;
                case "v_ifcs_proximity_assist_off": proximity = false; break;
                case "v_ifcs_vector_decoupling_toggle": coupled = !coupled; break;
                case "v_ifcs_vector_decoupling_on": coupled = true; break;
                case "v_ifcs_vector_decoupling_off": coupled = false; break;
                case "v_master_mode_cycle": mode = mode == VehicleMasterMode.Scm ? VehicleMasterMode.Navigation : VehicleMasterMode.Scm; break;
                case "v_master_mode_set_nav": mode = VehicleMasterMode.Navigation; break;
                case "v_master_mode_set_scm": mode = VehicleMasterMode.Scm; break;
                case "v_flight_advanced_hud_toggle": hud = !hud; break;
                case "v_flight_advanced_hud_on": hud = true; break;
                case "v_flight_advanced_hud_off": hud = false; break;
                case "v_light_amplification_toggle": lightAmplification = !lightAmplification; break;
                case "v_light_amplification_on": lightAmplification = true; break;
                case "v_light_amplification_off": lightAmplification = false; break;
                case "v_power_toggle": SetAllPower(!power); break;
                case "v_power_set_on": SetAllPower(true); break;
                case "v_power_set_off": SetAllPower(false); break;
                case "v_power_toggle_weapons": weaponsPower = !weaponsPower; break;
                case "v_power_set_weapons_on": weaponsPower = true; break;
                case "v_power_set_weapons_off": weaponsPower = false; break;
                case "v_power_toggle_thrusters": enginesPower = !enginesPower; break;
                case "v_power_set_thrusters_on": enginesPower = true; break;
                case "v_power_set_thrusters_off": enginesPower = false; break;
                case "v_power_toggle_shields": shieldsPower = !shieldsPower; break;
                case "v_power_set_shields_on": shieldsPower = true; break;
                case "v_power_set_shields_off": shieldsPower = false; break;
                default: return Response(TriggerGameActionStatus.Failed, "This action is not simulated by the mock.");
            }
            return Response(TriggerGameActionStatus.Ok, "Simulated action.");
        }
    }

    private void SetAllPower(bool value) => power = weaponsPower = enginesPower = shieldsPower = value;
    private static Availability Available(bool enabled) => new() { Enabled = enabled, Active = enabled };
    private static VehicleComponentDeploymentState Deployment(bool value) => value
        ? VehicleComponentDeploymentState.Deployed : VehicleComponentDeploymentState.Retracted;
    private static TriggerGameActionResponse Response(TriggerGameActionStatus status, string message) =>
        new() { Status = status, Message = message };
}
