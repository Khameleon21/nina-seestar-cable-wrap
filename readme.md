# NINA Plugin: Seestar Cable Wrap Monitor

## Status: Beta — v0.1.x, actively testing

---

## The Problem

The ZWO Seestar S50 is an all-in-one smart telescope. When used with NINA via the ZWO ALPACA driver, the mount rotates on its azimuth/RA axis to track the sky. The USB cable that connects the Seestar to the PC plugs into the rotating base of the scope — meaning over time, the cable winds around the mount.

When the user is **physically present**, this is a minor inconvenience. However, many Seestar users run their scopes **remotely** (e.g. at a dark sky site in another city or state). In a remote scenario, a jammed or disconnected cable can ruin an entire imaging session with no way to intervene.

There is currently **no logic in NINA or the ZWO ALPACA driver** to track or warn about this condition.

---

## The Solution

A NINA plugin that:
- Tracks RA axis rotation over time using the standard ASCOM telescope interface exposed by the ZWO ALPACA driver
- Counts **all** RA movement — both sidereal tracking and slews (both wind the cable)
- Accumulates total rotation in degrees, bidirectionally (unwinding counts back toward zero)
- Warns the user when the cable has wrapped past the configured threshold (default: 1 full rotation / 360°)
- Provides a "Check Cable Wrap" sequence instruction that fails the sequence if the threshold is exceeded
- Displays a running count and timestamped history of each 360° crossing in a dockable panel
- Persists state across NINA restarts (critical for remote use)
- Provides a reset button for when the user has physically unwound the cable

---

## Target Environment

| Item | Value |
|---|---|
| NINA Version | 3.2 (latest stable) |
| Telescope | ZWO Seestar S50 |
| Driver | ZWO ALPACA Driver (exposes Seestar as ASCOM telescope) |
| Mount type | Alt-Az, polar aligned by user, tracking via RA axis |
| Language | C# 12 / .NET 8 |
| UI Framework | WPF (MVVM, consistent with NINA) |
| NuGet package | `NINA.Plugin` 3.2.0.9001 |

---

## Project Structure

```
CableWrapMonitor/
├── CableWrapMonitor.csproj          # SDK-style project, net8.0-windows, UseWPF
├── Properties/
│   └── AssemblyInfo.cs              # Plugin name, version, NINA metadata attributes
├── Plugin.cs                        # MEF export for IPluginManifest — NINA entry point
├── CableWrapState.cs                # CableWrapState, WrapHistoryEntry, CableWrapSettings
│                                    # (JSON-serializable models for disk persistence)
├── CableWrapService.cs              # Core logic: polls RA every 1 s, accumulates delta,
│                                    # saves state every 10 s, fires alerts
├── Dockable/
│   ├── CableWrapDockableVM.cs       # Panel ViewModel — MEF [Export(typeof(IDockableVM))]
│   ├── CableWrapDockable.xaml       # Panel UI (status, rotation, wrap count, history, reset)
│   └── CableWrapDockable.xaml.cs   # Code-behind (InitializeComponent only)
├── SequenceItems/
│   ├── CheckCableWrapInstruction.cs # Sequence item — fails if wrap > threshold
│   ├── CheckCableWrapInstructionView.xaml
│   └── CheckCableWrapInstructionView.xaml.cs
├── Resources.xaml                   # DataTemplates + sequence icon, merged into NINA
└── Resources.xaml.cs                # MEF [Export(typeof(ResourceDictionary))]
```

---

## Architecture Notes

### MEF Composition
NINA uses traditional MEF (`System.ComponentModel.Composition`) for plugin discovery. Three parts are exported:

| Class | Export type | Role |
|---|---|---|
| `CableWrapMonitorPlugin` | `IPluginManifest` | Registers the plugin with NINA |
| `CableWrapService` | (self, shared) | Singleton injected into the VM and instruction |
| `CableWrapDockableVM` | `IDockableVM` | Discovered by NINA's docking manager |
| `CheckCableWrapInstruction` | `ISequenceItem` | Appears in the sequencer's instruction list |
| `Resources` | `ResourceDictionary` | DataTemplates merged into NINA's app resources |

### CableWrapService
The heart of the plugin. Injected with `ITelescopeMediator` by MEF. Runs a 1-second timer. On each tick:
1. Calls `telescopeMediator.GetInfo()` → `TelescopeInfo.RightAscension` (hours, 0–24)
2. Checks `Connected` — if false, sets NotConnected status, clears RA baseline, returns
3. Checks `TrackingEnabled || Slewing` — if neither, sets Stopped status, returns
4. Computes signed RA delta, corrects for 0h/24h wraparound
5. Accumulates delta into `TotalDegreesRotated` (all movement — tracking and slews)
6. Logs each 360° crossing to history
7. Saves full state to JSON every 10 ticks (every ~10 seconds)

### What Gets Counted
**All physical RA axis rotation is counted**, including:
- Sidereal tracking (slow, ~15°/hour)
- Slews between targets (fast, potentially hundreds of degrees)

Slews are not excluded because they physically rotate the mount axis and wind the cable just as much as tracking does. The mount sometimes takes the long way around when slewing, which can add 300+ degrees in a single target change.

### Tracking Status
| Status | Dot colour | Meaning |
|---|---|---|
| Not Connected | Gray | Telescope not connected in NINA |
| Not Tracking | Blue | Connected but not tracking or slewing (at home/park) |
| Tracking | Green | Connected and actively tracking or slewing |
| ⚠ WRAP WARNING | Red | Accumulated rotation exceeds the threshold |

### State Persistence
Two JSON files under `%LOCALAPPDATA%\NINA\Plugins\CableWrapMonitor\`:
- `state.json` — accumulator, last known RA, wrap history (last 1 hour), alert-fired flag
- `settings.json` — warning threshold (rotations)

State is loaded on service construction, so tracking resumes seamlessly after a NINA restart.

### Sequence Instruction
`CheckCableWrapInstruction` is inserted into a NINA Advanced Sequence. When executed:
- If `|TotalDegreesRotated| < threshold` → succeeds silently, sequence continues
- If threshold exceeded → throws `Exception`, sequence stops

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

| Version | Meaning |
|---|---|
| `0.1.x` | Beta — bug fixes and tuning, no new features |
| `0.2.0` | First new feature added after beta |
| `1.0.0` | Stable release |

Version is set in `Properties/AssemblyInfo.cs`. Bump the patch digit (`x`) on every change.

---

## Known Limitations

- **Slew path variance**: The mount occasionally takes the long way around when slewing between targets (e.g. 300+ degrees instead of 60). This is real cable movement and is correctly counted, but means a single unlucky slew can consume most of your threshold budget. Set the threshold to 1.5–2.0 rotations if this happens frequently.
- **Sidereal accumulation precision**: The ALPACA driver may cache the RA value and not update at exactly 1Hz. Sidereal drift accumulation (15°/hr) is approximate.
- **No toast notifications**: The NINA 3.2 plugin API does not expose a public notification class. Alerts appear in the NINA log and as a red indicator in the panel. Use the sequence instruction for automated halting.
- **No direct sequence pause**: NINA's plugin API does not allow a background service to cancel a running sequence. Use the "Check Cable Wrap" sequence instruction between imaging targets instead.

---

## Functional Requirements

- [x] RA axis rotation tracking with 0h/24h wraparound handling
- [x] All rotation counted — tracking and slews
- [x] Signed accumulator (clockwise positive, counter-clockwise negative)
- [x] Wrap count display as decimal fraction of full rotations
- [x] Timestamped wrap history (last 1 hour)
- [x] Warning threshold configurable from 0.5 to 3.0 rotations (default 1.0)
- [x] Alert fires once per threshold crossing, not every poll cycle
- [x] State persisted to JSON every 10 seconds
- [x] State loaded on startup — survives NINA restarts
- [x] "Reset — Cable Unwound" button with Yes/No confirmation
- [x] Reset records a manual-reset entry in the history
- [x] Dockable panel with status indicator, rotation, wrap count, editable threshold, history list
- [x] Not Connected / Not Tracking / Tracking / Warning status indicator
- [x] "Check Cable Wrap" sequence instruction that fails sequence on threshold breach
- [ ] Toast notifications (NINA 3.2 API limitation)
- [ ] Direct sequence pause from background service (NINA API limitation)

---

## Notes for Claude Code (future sessions)

- User has no coding experience — write all code in full, never ask user to modify manually
- Solo project — no Co-Authored-By lines in any commits
- Prioritize robustness over cleverness; this runs unattended at remote dark sites
- NINA 3.2 is `.net8.0-windows`. One NuGet package (`NINA.Plugin 3.2.0.9001`) covers all dependencies
- MEF uses `System.ComponentModel.Composition` (the traditional/legacy MEF), not `System.Composition`
- `IDockableVM` is in `NINA.Equipment.Interfaces.ViewModel` (not WPF.Base)
- `TelescopeInfo.TrackingEnabled` (bool) — NOT `.Tracking`
- `Notification` class does not exist in NINA 3.2 — use Logger only
- `ContentId` and `IsTool` on DockableVM must be property overrides, not constructor assignments
- `SequenceItem.Validate()` is not virtual — no `override` keyword
- The dockable panel DataTemplate key naming convention is critical: `{FullNamespace}.{ClassName}_Dockable`
- DLL installs in `%LOCALAPPDATA%\NINA\Plugins\3.0.0\` alongside other plugins
- Bump version (`0.1.x`) in `Properties/AssemblyInfo.cs` on every change
