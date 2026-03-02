# NINA Plugin: Seestar Cable Wrap Monitor

## Status: Active development — v0.3.9

---

## The Problem

The ZWO Seestar S50 is an all-in-one smart telescope. When used with NINA via the ZWO ALPACA driver, the mount rotates on its azimuth/RA axis to track the sky. The USB cable that connects the Seestar to the PC plugs into the rotating base of the scope — meaning over time, the cable winds around the mount.

When the user is **physically present**, this is a minor inconvenience. However, many Seestar users run their scopes **remotely** (e.g. at a dark sky site in another city or state). In a remote scenario, a jammed or disconnected cable can ruin an entire imaging session with no way to intervene.

There is currently **no logic in NINA or the ZWO ALPACA driver** to track or warn about this condition.

---

## The Solution

A NINA plugin that:
- Tracks azimuth axis rotation in real time using the ASCOM telescope interface exposed by the ZWO ALPACA driver
- Counts **all** rotation — both sidereal tracking and slews (both wind the cable)
- Accumulates total rotation in degrees, bidirectionally (unwinding counts back toward zero)
- Warns when the cable has wrapped past the configured threshold (default: 1 full rotation / 360°)
- Displays a **time-series chart** of rotation over the session
- Displays a **cable position arc chart** showing current wrap position on a clock face
- Provides a **"Check Cable Wrap"** sequence instruction that halts the sequence if the threshold is exceeded
- Provides an **"Unwind Cable"** sequence instruction that automatically slews the mount to unwind
- Offers an **auto-unwind button** in the panel — slews the mount in reverse to unwind, then resets the counter
- Displays a running count and timestamped history of each 360° crossing in a dockable panel
- Persists state across NINA restarts (critical for remote use)
- Snaps the accumulator to the nearest whole wrap when the scope returns home (crash recovery)

---

## Target Environment

| Item | Value |
|---|---|
| NINA Version | 3.2 (latest stable) |
| Telescope | ZWO Seestar S50 |
| Driver | ZWO ALPACA Driver (exposes Seestar as ASCOM telescope) |
| Mount type | Alt-Az, tracking via azimuth/RA axis |
| Language | C# 12 / .NET 8 |
| UI Framework | WPF (MVVM, consistent with NINA) |
| NuGet packages | `NINA.Plugin` 3.2.0.9001, `OxyPlot.Wpf` 2.2.0 |

---

## Project Structure

```
CableWrapMonitor/
├── CableWrapMonitor.csproj          # SDK-style project, net8.0-windows, UseWPF
├── Properties/
│   └── AssemblyInfo.cs              # Plugin name, version, NINA metadata attributes
├── Plugin.cs                        # MEF export for IPluginManifest — NINA entry point
├── CableWrapState.cs                # CableWrapState, WrapHistoryEntry, CableWrapSettings,
│                                    # RotationSample — JSON-serializable models + graph data
├── CableWrapService.cs              # Core logic: ITelescopeConsumer, rotation accumulation,
│                                    # slew/track/stopped state machine, auto-unwind, snap-to-home
├── Dockable/
│   ├── CableWrapDockableVM.cs       # Panel ViewModel — MEF [Export(typeof(IDockableVM))]
│   │                                # OxyPlot chart models (time-series + arc), Reset/Unwind commands
│   ├── CableWrapDockable.xaml       # Panel UI: status, rotation, wrap count, threshold,
│   │                                # time-series chart, cable position arc chart, history, buttons
│   └── CableWrapDockable.xaml.cs   # Code-behind (InitializeComponent only)
├── SequenceItems/
│   ├── CheckCableWrapInstruction.cs # Sequence item — fails if wrap > threshold
│   ├── CheckCableWrapInstructionView.xaml
│   ├── UnwindCableInstruction.cs    # Sequence item — auto-unwinds cable via slews
│   └── UnwindCableInstructionView.xaml
├── Resources.xaml                   # DataTemplates + sequence icons, merged into NINA
└── Resources.xaml.cs                # MEF [Export(typeof(ResourceDictionary))]
```

---

## Architecture Notes

### MEF Composition
NINA uses traditional MEF (`System.ComponentModel.Composition`) for plugin discovery. The following parts are exported:

| Class | Export type | Role |
|---|---|---|
| `CableWrapMonitorPlugin` | `IPluginManifest` | Registers the plugin with NINA |
| `CableWrapService` | `ITelescopeConsumer` (shared) | Receives telescope data pushes from NINA |
| `CableWrapDockableVM` | `IDockableVM` | Discovered by NINA's docking manager |
| `CheckCableWrapInstruction` | `ISequenceItem` | Appears in the sequencer's instruction list |
| `UnwindCableInstruction` | `ISequenceItem` | Appears in the sequencer's instruction list |
| `Resources` | `ResourceDictionary` | DataTemplates merged into NINA's app resources |

### CableWrapService — Data Source
Rather than polling on a timer, the service implements `ITelescopeConsumer`. NINA calls `UpdateDeviceInfo()` at its native rate (several Hz), pushing a `TelescopeInfo` snapshot each tick. This eliminates polling overhead and ensures the plugin reacts at the same rate as the rest of NINA.

### State Machine
On each `UpdateDeviceInfo()` call the service branches into one of three states:

| State | Condition | What happens |
|---|---|---|
| **Not Connected** | `info.Connected == false` | Resets all baselines, sets gray status |
| **Slewing** | `info.Slewing == true` | Accumulates `GetComputedAzimuth()` delta per tick, drives live display |
| **Tracking** | `info.TrackingEnabled == true` | Samples azimuth every 5 seconds, accumulates delta |
| **Stopped** | All other | Updates `LastKnownAzimuth` baseline; fires at-home snap if scope just returned home |

### ALPACA Driver Quirks
The ZWO ALPACA driver has two known behaviours the plugin works around:

1. **`info.Azimuth = 0°` when at home** — the driver reports `0°` as a placeholder when the scope is at its home position. The real home azimuth for the Seestar S50 is approximately `180°` (scope points south). The plugin uses `GetComputedAzimuth()` (calculated from RA/Dec + observer location) instead of `info.Azimuth` for all baseline and stopped-state reads.

2. **Azimuth unreliable during slews** — `info.Azimuth` reports erratic values mid-slew (observed: `-440°`). The plugin uses `GetComputedAzimuth()` throughout slews as well.

### At-Home Snap
When the scope returns to home after a slew or park, the service fires `SnapToHomePosition()`:
- Rounds `TotalDegreesRotated` to the nearest whole number of full rotations (`Math.Round(total / 360.0) * 360.0`)
- This corrects minor accumulator drift and is the primary crash-recovery mechanism — if NINA crashed mid-session, the stale value in `state.json` gets corrected the next time the scope parks at home

### Auto-Unwind
The "Unwind Cable Automatically" button (and `UnwindCableInstruction` sequence item) calls `UnwindAsync()`:
1. Slews to a safe position near the meridian at Dec +80°
2. Steps through the accumulated rotation in ≤60° increments, slewing in reverse
3. Sends the scope home via `FindHome()`
4. Calls `Reset()` to zero the counter and record a history entry

### Charts
Both charts update whenever a new graph sample is recorded:

| Chart | What it shows |
|---|---|
| **Rotation Tonight** (time-series) | Total degrees rotated vs time, with threshold lines |
| **Cable Position** (arc) | Current position on a clock face — home = 9 o'clock, CW = positive rotation; each 360° lap changes colour (blue → gold → orange → red) |

### Tracking Status

| Status | Dot colour | Meaning |
|---|---|---|
| Not Connected | Gray | Telescope not connected in NINA |
| Not Tracking | Blue | Connected but not tracking or slewing (at home / parked) |
| Tracking | Green | Connected and actively tracking or slewing |
| ⚠ WRAP WARNING | Red | Accumulated rotation exceeds the configured threshold |

### State Persistence
Two JSON files under `%LOCALAPPDATA%\NINA\Plugins\CableWrapMonitor\`:
- `state.json` — accumulator, last known azimuth, wrap history (last 1 hour), alert-fired flag
- `settings.json` — warning threshold in rotations

State is loaded on service construction, so tracking resumes seamlessly after a NINA restart.

### Sequence Instructions

**Check Cable Wrap** — insert between imaging targets in an Advanced Sequence:
- `|TotalDegreesRotated| < threshold` → succeeds silently, sequence continues
- Threshold exceeded → throws `Exception`, NINA marks the item Failed and halts the sequence

**Unwind Cable** — insert in a sequence to automatically unwind before a critical phase:
- If `|TotalDegreesRotated| < 10°` → skips silently (nothing to unwind)
- Otherwise calls `UnwindAsync()` — slews the mount in reverse to unwind, then resets counter

---

## Version History

### v0.3.x — Charts and auto-unwind

| Version | Change |
|---|---|
| **v0.3.9** | Move arc chart legend outside OxyPlot into a XAML StackPanel — no more overlap with arc |
| **v0.3.8** | Always-visible legend using ghost series — was hidden until 2+ laps accumulated |
| **v0.3.7** | Fix OxyPlot 2.2.0 Legend API — `sp.Legends.Add(new Legend {...})` replaces removed PlotModel properties |
| **v0.3.6** | Multi-lap arc colouring — each 360° lap changes colour (blue → gold → orange → red) |
| **v0.3.5** | Replace outward spiral with arc on unit circle — home at 9 o'clock, CW positive, arc retraces on unwind |
| **v0.3.4** | Revert zenith fix — tick accumulator was correct; scope physically loops 400°+ in high-altitude slews |
| **v0.3.3** | Attempted zenith-singularity fix via pre/post Az diff (reverted — wrong for full-loop slews) |
| **v0.3.2** | Fix tracking over-accumulation; live chart updates during slews |
| **v0.3.1** | Remove explicit OxyPlot.Wpf pin — NINA already provides 2.2.0 transitively |
| **v0.3.0** | Add OxyPlot time-series and spiral cable-wrap charts |

### v0.2.x — ITelescopeConsumer architecture

| Version | Change |
|---|---|
| **v0.2.27** | Drop separate display accumulator — single `GetComputedAzimuth()` accumulator drives both commit and live display |
| **v0.2.26** | Fix home-position Az baseline — ALPACA reports `0°` at home (lie); use `GetComputedAzimuth()` (≈180°) instead |
| **v0.2.25** | Use driver Az for live slew display; add periodic slew log entries |
| **v0.2.24** | Delay live slew display until direction confirmed — skip waypoint spike on tick 1 |
| **v0.2.23** | Freeze display during slews — driver waypoint injection was corrupting live total |
| **v0.2.22** | Re-enable live display during slews via ITelescopeConsumer data |
| **v0.2.21** | Switch to `ITelescopeConsumer` — receive same live telescope data as NINA UI panels; remove poll timer |
| **v0.2.20** | Remove live slew display — driver waypoints made it unreliable (later restored in v0.2.22) |
| **v0.2.19** | Add per-tick slew logging for diagnostics |
| **v0.2.18** | Fix slew tracking — incremental Az accumulation, remove direction-sign math |
| **v0.2.17** | Live total rotation display during slews using computed Az |
| **v0.2.16** | Use `GetComputedAzimuth()` everywhere; lower snap threshold to 0.001° |
| **v0.2.15** | Use `AtHome=0°` override for post-slew Az when scope arrives home |
| **v0.2.14** | Use RA for slew direction indicator instead of early Az samples |
| **v0.2.13** | Catch-up sample on stop; settle delay before snap; movement direction indicator (↻/↺) |
| **v0.2.12** | Add early-azimuth direction detection for slew tracking |
| **v0.2.11** | Fix slew tracking — use before/after azimuth instead of RA delta |
| **v0.2.10** | Fix snap not firing on sub-1° residuals at home |
| **v0.2.9** | Add dedicated rolling log file (`cwm-YYYY-MM-DD.log`) |
| **v0.2.8** | Fix snap firing too early during FindHome |
| **v0.2.7** | Track park motion; snap wrap to nearest whole rotation on home arrival |
| **v0.2.5** | Reduce settle delays from 2000ms to 500ms during unwind |
| **v0.2.4** | Fix unwind direction |
| **v0.2.3** | Suppress poll timer during unwind; manually track unwind progress |
| **v0.2.2** | Fix unwind direction (increase RA to unwind positive wrap) |
| **v0.2.1** | Fix unwind below-horizon error |
| **v0.2.0** | Add auto-unwind feature (`UnwindAsync()`); remove AtHome snap (restored in v0.2.7) |

### v0.1.x — Initial beta

| Version | Change |
|---|---|
| **v0.1.11** | Auto-unwind feature first attempt |
| **v0.1.7** | Fix sequence item category — was not appearing in NINA Advanced Sequencer |
| **v0.1.6** | Fix drift display rounding error in snap message |
| **v0.1.5** | Snap accumulator to nearest whole wrap when scope returns home |
| **v0.1.4** | Hybrid tracking: RA for slews, Azimuth for sidereal tracking |
| **v0.1.3** | Switch from `RightAscension` to `Azimuth` for rotation tracking |
| **v0.1.2** | Variable poll rate: 1s during slews, 5s during tracking |
| **v0.1.1** | Initial versioning and readme |
| **v0.1.0** | Initial beta release |

---

## Build Instructions

1. Clone the repo on the Windows PC running NINA
2. Open a terminal in the project folder
3. Run: `dotnet build -c Release`
4. Close NINA
5. Copy `bin\Release\CableWrapMonitor.dll` to `%LOCALAPPDATA%\NINA\Plugins\3.0.0\`
6. Restart NINA — the plugin loads automatically via MEF

To update after a `git pull`: repeat steps 3–6.

---

## Versioning

| Version range | Meaning |
|---|---|
| `0.1.x` | Initial beta |
| `0.2.x` | ITelescopeConsumer architecture, slew/track/stopped state machine |
| `0.3.x` | OxyPlot charts, auto-unwind, Unwind sequence item |
| `1.0.0` | Stable release |

Version is set in `Properties/AssemblyInfo.cs` (both `AssemblyVersion` and `AssemblyFileVersion`). Bump the patch digit on every change.

---

## Known Limitations

- **No toast notifications**: The NINA 3.2 plugin API does not expose a public notification class. Alerts appear in the NINA log and as a red indicator in the panel. Use the "Check Cable Wrap" sequence instruction for automated halting.
- **No direct sequence pause from background service**: Use the "Check Cable Wrap" sequence instruction between imaging targets instead.
- **FindHome motion not accumulated**: The Seestar ALPACA driver does not set `Slewing=true` during `FindHome()` or `Park()` motion. The at-home snap corrects any residual drift when the scope arrives at home.
- **Seestar-specific**: The ALPACA quirks worked around here (`info.Azimuth = 0°` at home, erratic mid-slew azimuth) may not apply to other telescopes. The plugin has only been tested against the ZWO Seestar S50 ALPACA driver.

---

## Functional Checklist

- [x] Azimuth axis rotation tracking using ITelescopeConsumer
- [x] All rotation counted — tracking and slews
- [x] Signed accumulator (clockwise positive, counter-clockwise negative)
- [x] Wrap count display as decimal fraction of full rotations
- [x] Timestamped wrap history (last 1 hour)
- [x] Warning threshold configurable from 0.5 to 3.0 rotations (default 1.0)
- [x] Alert fires once per threshold crossing, not every poll cycle
- [x] State persisted to JSON; loaded on startup — survives NINA restarts and crashes
- [x] At-home snap for crash recovery
- [x] "Reset — Cable Unwound" button with confirmation dialog
- [x] "Unwind Cable Automatically" button — reverses mount to unwind cable
- [x] Dockable panel with status, rotation, wrap count, threshold, charts, history, buttons
- [x] Time-series chart (rotation over session)
- [x] Cable position arc chart (clock-face, colour-coded by lap)
- [x] Movement direction indicator (↻ CW / ↺ CCW) while slewing
- [x] "Check Cable Wrap" sequence instruction — halts sequence on threshold breach
- [x] "Unwind Cable" sequence instruction — auto-unwinds in a running sequence
- [ ] Toast notifications (NINA 3.2 API limitation)
- [ ] Direct sequence pause from background service (NINA API limitation)
