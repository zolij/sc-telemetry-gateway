# PTU game telemetry protocol

This is a development reference for the **game's local gRPC interface**, not
the gateway's [MFD and receiver APIs](PROTOCOL.md). The repository's
[`api.proto`](../proto/sc/game/telemetry/v1/api.proto) and
[`types.proto`](../proto/sc/game/telemetry/v1/types.proto) are reconstructed
from descriptors embedded in the game executable, not official source files
or an SDK. They are the field-by-field reference for this
project; this document is a navigable summary, not an independent schema.

The installed PTU manifest reports `5.0.202.10025` (changelist `13248297`,
build date 2026-11-20). On 2026-11-23 both repository definitions were checked
against the corresponding descriptors embedded in that build's
`Bin64/StarCitizen.exe`. Package, syntax, imports, message and nested enum
definitions, field names, numbers, types, labels and explicit presence,
reserved ranges and names, enum values, and service method input/output and
streaming flags matched. This comparison does not establish runtime semantics
or compatibility with other builds. Recheck descriptors and test against the
running game after an update. See [NOTICE](../NOTICE).

## Service and wire contract

The proto3 package is `sc.game.telemetry.v1`; the service is
`GameTelemetryService`. The gRPC paths are:

| Method | Request | Response | Shape |
|---|---|---|---|
| `/sc.game.telemetry.v1.GameTelemetryService/TelemetryStream` | `TelemetryStreamRequest` (empty) | `TelemetryStreamResponse` | unary request, server stream |
| `/sc.game.telemetry.v1.GameTelemetryService/ListTriggerableActions` | `ListTriggerableActionsRequest` (empty) | `ListTriggerableActionsResponse` | unary |
| `/sc.game.telemetry.v1.GameTelemetryService/TriggerGameAction` | `TriggerGameActionRequest` | `TriggerGameActionResponse` | unary |

Numbers below are **protobuf field tags**, not values or indexes. Message fields
have presence. `optional` scalars and enums have explicit presence; ordinary
proto3 scalars do not distinguish an absent value from the default at the
generated API level. `repeated` fields are lists. A wire-omitted ordinary
scalar is not proof that the game measured a default value. Units, ranges,
freshness, ordering semantics, server timestamps and action-to-field mappings
are not specified by these descriptors.

### Telemetry envelope

`TelemetryStreamResponse` reserves tags 1 and 2 and has message fields:

| Tag | Field | Type |
|---:|---|---|
| 3 | `vehicle` | `VehicleFrame` |
| 4 | `weapons` | `WeaponsFrame` |
| 5 | `events` | `EventsFrame` |
| 6 | `actor` | `ActorFrame` |
| 7 | `force_feedback` | `ForceFeedbackFrame` |
| 8 | `turret` | `TurretFrame` |

The schema does not require every section in every response. `VehicleFrame`
reserves tag 1 and has `state: VehicleState` at tag 2.

### Actions

`ListTriggerableActionsResponse` has `repeated TriggerableAction actions = 1`.
Each `TriggerableAction` contains `string action_name = 1`, `string category
= 2`, and `bool holdable = 3`. The descriptor does **not** enumerate valid
action names or categories, bind actions to telemetry fields, or guarantee that
a listed action can be executed in the current game context.

`TriggerGameActionRequest` has `string action_name = 1` and `optional uint32
hold_ms = 2`. The optional duration can be absent, distinct from present zero;
the proto does not specify a permitted range. `TriggerGameActionResponse` has
`TriggerGameActionStatus status = 1` and `string message = 2`. Status values:

| Number | Full enum name |
|---:|---|
| 0 | `TRIGGER_GAME_ACTION_STATUS_UNSPECIFIED` |
| 1 | `TRIGGER_GAME_ACTION_STATUS_OK` |
| 2 | `TRIGGER_GAME_ACTION_STATUS_UNKNOWN_ACTION` |
| 3 | `TRIGGER_GAME_ACTION_STATUS_MAP_INACTIVE` |
| 4 | `TRIGGER_GAME_ACTION_STATUS_RATE_LIMITED` |
| 5 | `TRIGGER_GAME_ACTION_STATUS_FAILED` |
| 6 | `TRIGGER_GAME_ACTION_STATUS_HOLD_NOT_ALLOWED` |
| 7 | `TRIGGER_GAME_ACTION_STATUS_FILTERED` |

These are names, not a complete specification of their causes. A response
status must be inspected; a successful RPC transport alone does not establish a
game-state change. The gateway's duration, authorization and dispatch limits
are separate from the game schema and are documented in
[PROTOCOL.md](PROTOCOL.md#game-controls).

## Telemetry message inventory

In the tables, `?` denotes a field explicitly marked `optional` in the
reconstructed source; all message fields also have protobuf presence. Names,
tags and type declarations remain definitive in
[`types.proto`](../proto/sc/game/telemetry/v1/types.proto).

### Vehicle

| Message | Fields: `tag name: type` |
|---|---|
| `VehicleState` | `1 name: string`, `2 class_name: string`, `3 manufacturer: string`, `4 type: VehicleType`, `5 lights_enabled: bool`, `6 doors: VehicleDoorState?`, `7 annunciators: VehicleAnnunciator?`, `8 flight: VehicleFlight?`, `9 power: VehiclePowerState?`, `10 shields: VehicleShields?`, `11 health: float`, `12 master_mode: VehicleMasterModeState?` |
| `VehicleDoorState` | `1 open_state: DoorOpenState`, `2 lock_state: DoorLockState` |
| `VehicleFlight` | `1 state: VehicleFlightState`, `2 settings: VehicleFlightSettings`, `3 instruments: VehicleFlightInstruments` |
| `VehicleFlightState` | `1 is_landed: bool`, `2 gear: VehicleComponentDeploymentState?`, `3 vtol: VehicleComponentDeploymentState?`, `4 transformation: VehicleComponentDeploymentState?` |
| `VehicleFlightSettings` | `1 ifcs_coupled: Availability`, `2 ifcs_esp: Availability`, `3 ifcs_gravity_compensation: Availability`, `4 ifcs_g_safe: Availability`, `5 ifcs_cruise_mode: Availability`, `6 ifcs_spacebreak_enables_boost: bool`, `7 ifcs_speed_limiter: Availability`, `8 ifcs_proximity_assist: Availability`, `9 ifcs_proximity_assist_landing: Availability`, `10 ifcs_stability: Availability`, `11 ifcs_core: Availability`, `12 advanced_hud_enabled_scm: bool`, `13 advanced_hud_enabled_nav: bool`, `14 advanced_hud_labels: bool`, `15 course_prediction: bool`, `16 ifcs_wind_compensation: Availability`, `17 ifcs_g_safe_disabled_by_boost: bool`, `18 ifcs_command_behaviour: Availability`, `19 flight_light_amplification: bool` |
| `VehicleFlightInstruments` | `1 speed: float`, `2 hydrogen_fuel: float`, `3 quantum_fuel: float`, `4 g_meter: Gauge?`, `5 boost: VehicleThrusterBoost`, `6 altitude: float`, `7 radar_altitude: float`, `8 vertical_speed_indicator: float`, `9 atmospheric_density: float`, `10 heading: float` |
| `VehicleThrusterBoost` | `1 engaged: bool`, `2 output: float`, `3 reserves: float`, `4 critical: bool` |
| `VehiclePowerState` | `1 powerplants: bool`, `2 engines: bool`, `3 weapons: bool`, `4 coolers: bool`, `5 life_support: bool`, `6 gravity_generators: bool`, `7 sensors: bool`, `8 shield_generators: bool`, `9 shield_emitters: bool` |
| `VehicleAnnunciator` | `1 pwr`, `2 wpn`, `3 thr`, `4 shld`, `5 cool`, `6 qdrv`, `7 radr`, `8 prox`, `9 hit`, `10 misl`: all `AnnunciatorState` |
| `VehicleShields` | `1 level: float`, `2 faces: repeated float` |
| `VehicleMasterModeState` | `1 mode: VehicleMasterMode`, `2 spooled: bool` |
| `Availability` | `1 enabled: bool`, `2 active: bool` |
| `Gauge` | `1 current: float`, `2 max: float` |

### Weapons, events, actor, force feedback and turret

| Message | Fields: `tag name: type` |
|---|---|
| `WeaponsFrame` | `1 settings: WeaponsSettings`, `2 preset_index: uint32`, `3 preset_name: string`, `4 weapons: repeated WeaponState`, `5 countermeasures: repeated Countermeasures` |
| `WeaponsSettings` | `1 fallback_convergence_distance: bool`, `2 magnified_ui: bool`, `3 precision_lines: bool`, `4 staggered_fire: bool`, `5 use_lag_pips: bool`, `6 use_single_pips: bool`, `7 use_fading_pips: bool`, `8 gimbals_locked: bool` |
| `WeaponState` | `1 name: string`, `2 is_powered: bool`, `3 ready_to_fire: bool`, `4 contained_in_preset: bool` |
| `Countermeasures` | `1 type: CountermeasureType`, `2 amount: uint32` |
| `EventsFrame` | `1 events: repeated GameEvent` |
| `GameEvent` | `1 type: GameEventType` |
| `ActorFrame` | `1 vitals: ActorVitals`, `2 state: ActorState`, `3 environment: ActorEnvironment` |
| `ActorVitals` | `1 health: Gauge`, `2 oxygen: Gauge`, `3 hunger: Gauge`, `4 thirst: Gauge`, `5 body_temperature: Gauge`, tag 6 reserved, `7 heart_rate: float`, `8 gravity: float`, `9 pressure: float`, `10 intoxication_level: int32` |
| `ActorState` | `1 locomotion: ActorLocomotionState`, `2 crew_role: ActorCrewRole`, `3 seat_name: string`, `4 uec_balance: int64` |
| `ActorEnvironment` | `1 external_temperature: float`, `2 ambient_radiation_rate: float`, `3 breathable_atmosphere: bool` |
| `ForceFeedbackFrame` | tag 1 reserved, `2 angular: FeedbackAxes`, `3 linear: FeedbackAxes`, `4 gunfire: float`, `5 stick_shaker: float` |
| `FeedbackAxes` | `1 x: AxisFeedback`, `2 y: AxisFeedback`, `3 z: AxisFeedback` |
| `AxisFeedback` | `1 resistance: float`, `2 damping: float` |
| `TurretFrame` | `1 light_amplification: bool` |

### Telemetry enums

Enum names below omit the common prefix shown in each row; numbers are wire
values. Nested enum names are qualified by their containing message.

| Enum | Prefix | Suffixes and numbers |
|---|---|---|
| `VehicleComponentDeploymentState` | `VEHICLE_COMPONENT_DEPLOYMENT_STATE_` | `UNSPECIFIED=0`, `RETRACTED=1`, `DEPLOYING=2`, `DEPLOYED=3`, `RETRACTING=4` |
| `VehicleType` | `VEHICLE_TYPE_` | `UNSPECIFIED=0`, `SPACESHIP=1`, `GROUND=2`, `GRAVLEV=3`, `BOAT=4` |
| `VehicleMasterMode` | `VEHICLE_MASTER_MODE_` | `UNSPECIFIED=0`, `NAVIGATION=1`, `SCM=2`, `STEALTH=3` |
| `ActorLocomotionState` | `ACTOR_LOCOMOTION_STATE_` | `UNSPECIFIED=0`, `ON_FOOT=1`, `EVA=2`, `SEATED=3` |
| `ActorCrewRole` | `ACTOR_CREW_ROLE_` | `UNSPECIFIED=0`, `PILOT=1`, `GUNNER=2`, `CREW=3` |
| `VehicleDoorState.DoorOpenState` | `DOOR_OPEN_STATE_` | `UNSPECIFIED=0`, `OPENED=1`, `CLOSED=2` |
| `VehicleDoorState.DoorLockState` | `DOOR_LOCK_STATE_` | `UNSPECIFIED=0`, `UNLOCKED=1`, `LOCKED=2` |
| `VehicleAnnunciator.AnnunciatorState` | `ANNUNCIATOR_STATE_` | `UNSPECIFIED=0`, `NOMINAL=1`, `OFF=2`, `MINOR_FLASHING=3`, `MINOR_ON=4`, `MAJOR_FLASHING=5`, `MAJOR_ON=6` |
| `Countermeasures.CountermeasureType` | `COUNTERMEASURE_TYPE_` | `UNSPECIFIED=0`, `DECOY=1`, `NOISE=2` |
| `GameEvent.GameEventType` | `GAME_EVENT_TYPE_` | `UNSPECIFIED=0`, `ACTOR_SPAWN=1`, `ACTOR_DEAD=2`, `ACTOR_ENTER_SEAT=3`, `ACTOR_LEAVE_SEAT=4`, `ACTOR_RESPAWN=5`, `ACTOR_DOWNED=6` |

In this reconstructed telemetry schema `VehicleMasterModeState` exposes a
master mode, but there is no separate operator-mode state field. The action
API does not establish that an action and a similarly named telemetry field
represent the same state.

## Observed action catalogue (not part of the proto)

The following is a snapshot from `ListTriggerableActions` on PTU
`5.0.202.10025`, captured on 2026-11-23. It contains 112 actions in 11
categories. This is a runtime observation, **not an enum or allowlist defined
by the descriptor**, and may change with the build or game context. Query the
running game rather than relying on this snapshot. Only `v_invoke_ping` was
reported as `holdable: true`; all other entries were reported as `false`.

```text
lights_controller (3)
  v_lights
  v_lights_off
  v_lights_on

player (4)
  mobiglas
  toggle_flashlight
  v_starmap
  visor_wipe

player_emotes (34)
  emote_agree
  emote_angry
  emote_atease
  emote_attention
  emote_blah
  emote_bored
  emote_bow
  emote_burp
  emote_cheer
  emote_chicken
  emote_clap
  emote_come
  emote_cry
  emote_dance
  emote_disagree
  emote_failure
  emote_flex
  emote_flirt
  emote_gasp
  emote_gloat
  emote_greet
  emote_laugh
  emote_launch
  emote_point
  emote_rude
  emote_salute
  emote_sit
  emote_sleep
  emote_smell
  emote_taunt
  emote_threaten
  emote_wait
  emote_wave
  emote_whistle

seat_general (17)
  v_eject
  v_emergency_exit
  v_enter_remote_turret_1
  v_enter_remote_turret_2
  v_enter_remote_turret_3
  v_light_amplification_off
  v_light_amplification_on
  v_light_amplification_toggle
  v_operator_mode_cycle_back
  v_operator_mode_cycle_forward
  v_set_flight_mode
  v_set_guns_mode
  v_set_mining_mode
  v_set_quantum_mode
  v_set_refuel_mode
  v_set_salvage_mode
  v_set_scan_mode

spaceship_docking (1)
  v_toggle_docking_request

spaceship_general (11)
  v_close_all_doors
  v_flightready
  v_lock_all_doors
  v_lock_all_ports
  v_open_all_doors
  v_self_destruct
  v_toggle_all_doorlocks
  v_toggle_all_doors
  v_toggle_all_portlocks
  v_unlock_all_doors
  v_unlock_all_ports

spaceship_movement (27)
  v_atc_request
  v_toggle_landing_system
  v_deploy_landing_system
  v_retract_landing_system
  v_flight_advanced_hud_off
  v_flight_advanced_hud_on
  v_flight_advanced_hud_toggle
  v_ifcs_toggle_gforce_safety
  v_ifcs_gsafe_on
  v_ifcs_gsafe_off
  v_ifcs_proximity_assist_off
  v_ifcs_proximity_assist_on
  v_ifcs_proximity_assist_toggle
  v_ifcs_reset_gmeter_max
  v_ifcs_toggle_esp
  v_ifcs_vector_decoupling_off
  v_ifcs_vector_decoupling_on
  v_ifcs_vector_decoupling_toggle
  v_master_mode_cycle
  v_master_mode_set_nav
  v_master_mode_set_scm
  v_transform_cycle
  v_transform_deploy
  v_transform_retract
  v_vtol_off
  v_vtol_on
  v_vtol_toggle

spaceship_power (12)
  v_power_set_off
  v_power_set_on
  v_power_set_shields_off
  v_power_set_shields_on
  v_power_set_thrusters_off
  v_power_set_thrusters_on
  v_power_set_weapons_off
  v_power_set_weapons_on
  v_power_toggle
  v_power_toggle_shields
  v_power_toggle_thrusters
  v_power_toggle_weapons

spaceship_quantum (1)
  v_toggle_qdrive_engagement

spaceship_radar (1)
  v_invoke_ping

spaceship_target_hailing (1)
  v_target_hail
```

In live tests, vehicle lights on/off and a 500 ms radar ping were exercised;
listing the other entries does not verify their behavior. The game controls
permission and other operational constraints are described in the
[setup guide](../README.md#windows-quick-start) and
[gateway protocol](PROTOCOL.md#game-controls).
