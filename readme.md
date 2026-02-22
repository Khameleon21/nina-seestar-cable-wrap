# NINA Plugin: Seestar Cable Wrap Monitor

## Status: Initial code complete — not yet compiled or tested

---

## The Problem

The ZWO Seestar S50 is an all-in-one smart telescope. When used with NINA via the ZWO ALPACA driver, the mount rotates on its azimuth/RA axis to track the sky. The USB cable that connects the Seestar to the PC plugs into the rotating base of the scope — meaning over time, the cable winds around the mount.

When the user is **physically present**, this is a minor inconvenience. However, many Seestar users run their scopes **remotely** (e.g. at a dark sky site in another city or state). In a remote scenario, a jammed or disconnected cable can ruin an entire imaging session with no way to intervene.

There is currently **no logic in NINA or the ZWO ALPACA driver** to track or warn about this condition.

---

## The Solution

A NINA plugin that:
- Tracks RA axis rotation over time using the standard ASCOM telescope interface exposed by the ZWO ALPACA driver
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
├── CableWrapService.cs              # Core logic: polls RA every 5 s, accumulates delta,
│                                    # detects slews, fires alerts, saves/loads state
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
The heart of the plugin. Injected with `ITelescopeMediator` by MEF. Runs a 5-second timer. On each tick:
1. Calls `telescopeMediator.GetInfo()` → `TelescopeInfo.RightAscension` (hours, 0–24)
2. Computes signed RA delta, corrects for 0h/24h wraparound
3. Ignores deltas > 5° (slew detection threshold, configurable)
4. Accumulates delta into `TotalDegreesRotated`
5. Logs each 360° crossing to history
6. Fires `Notification.ShowWarning()` once per threshold crossing
7. Saves full state to JSON on every tick

### State Persistence
Two JSON files under `%LOCALAPPDATA%\NINA\Plugins\CableWrapMonitor\`:
- `state.json` — accumulator, last known RA, wrap history, alert-fired flag
- `settings.json` — warning threshold (rotations), slew detection threshold (degrees)

State is loaded on service construction, so tracking resumes seamlessly after a NINA restart.

### Sequence Instruction
`CheckCableWrapInstruction` is inserted into a NINA Advanced Sequence. When executed:
- If `|TotalDegreesRotated| < threshold` → succeeds silently, sequence continues
- If threshold exceeded → fires `Notification.ShowError()`, throws `Exception`, sequence stops

### Direct Sequence Pause
NINA's plugin API does not expose a way to externally cancel a running sequence from a background service. The dockable panel shows a red warning indicator and fires a toast notification. The **sequence instruction** is the correct mechanism for automated sequence halting. Users should add "Check Cable Wrap" between imaging targets.

---

## Build Instructions

1. Install **Visual Studio 2022** (Community is free) with the **.NET Desktop Development** workload
2. Open this folder in Visual Studio (File → Open → Folder)
3. Visual Studio will restore the `NINA.Plugin 3.2.0.9001` NuGet package automatically
4. Build → Build Solution (Ctrl+Shift+B) using **Release** configuration
5. Copy `bin\Release\CableWrapMonitor.dll` to `%LOCALAPPDATA%\NINA\Plugins\`
6. Restart NINA — the plugin loads automatically via MEF

---

## Known Potential Compilation Issues

These were identified as risks during code generation but could not be verified without building. Each has a straightforward fix:

### 1. `DockableVM` constructor signature
**Symptom:** Error about no matching constructor `DockableVM(IProfileService)`
**Fix:** In `CableWrapDockableVM.cs`, remove the `IProfileService profileService` parameter and change `base(profileService)` to `base()`. Also remove the `IProfileService` import.

### 2. `IDockableVM` namespace
**Symptom:** `IDockableVM` could not be found in `NINA.WPF.Base.ViewModel`
**Fix:** Add `using NINA.WPF.Base.Interfaces;` to `CableWrapDockableVM.cs`

### 3. `BaseINPC` not found
**Symptom:** `BaseINPC` does not exist in `NINA.Core.Utility`
**Fix:** In `CableWrapService.cs`:
- Add `using CommunityToolkit.Mvvm.ComponentModel;`
- Change `: BaseINPC` to `: ObservableObject`
- Change all `RaisePropertyChanged(nameof(X))` → `OnPropertyChanged(nameof(X))`
- Change `RaisePropertyChanged()` (no arg, in property setters) → `OnPropertyChanged()`

### 4. `Issues` property assignment
**Symptom:** `Issues` has no setter, or setter is inaccessible
**Fix:** In `CheckCableWrapInstruction.Validate()`, replace `Issues = issues;` with:
```csharp
Issues.Clear();
foreach (var item in issues) Issues.Add(item);
```

### 5. `SequenceItem` namespace ambiguity
**Symptom:** `SequenceItem is a namespace, not a type`
**Fix:** In `CheckCableWrapInstruction.cs`, change the `using` to an alias:
```csharp
using NINASequenceItem = NINA.Sequencer.SequenceItem.SequenceItem;
```
Then change the class declaration to `: NINASequenceItem` and the copy constructor call to `base(other)`.

---

## Functional Requirements (all implemented)

- [x] RA axis rotation tracking with 0h/24h wraparound handling
- [x] Signed accumulator (clockwise positive, counter-clockwise negative)
- [x] Slew detection — deltas > 5° in one tick are ignored and logged
- [x] Wrap count display as decimal fraction of full rotations
- [x] Timestamped wrap history (last 100 entries kept)
- [x] Warning threshold configurable from 0.5 to 3.0 rotations (default 1.0)
- [x] Alert fires once per threshold crossing, not every poll cycle
- [x] NINA toast notification on threshold crossing
- [x] State persisted to JSON on every poll tick
- [x] State loaded on startup — survives NINA restarts
- [x] "Reset — Cable Unwound" button with Yes/No confirmation
- [x] Reset records a manual-reset entry in the history
- [x] Dockable panel with status indicator, rotation, wrap count, editable threshold, history list
- [x] Graceful "Not Connected" state when telescope is disconnected
- [x] "Check Cable Wrap" sequence instruction that fails sequence on threshold breach
- [ ] Direct sequence pause from background service (not possible via NINA's plugin API — use sequence instruction instead)

---

## Edge Cases Handled

- **Slews / meridian flips**: RA changes > 5° in one 5-second tick are logged as slews and excluded from the accumulator
- **NINA restart mid-session**: State is fully restored from disk; tracking continues from last known position
- **Telescope disconnects during session**: Timer continues running; each tick sets status to "Not Connected" and skips accumulation until reconnected
- **Threshold changed after alert fires**: New threshold takes effect immediately on next tick; alert re-arms only after a reset

---

## Notes for Claude Code (future sessions)

- User has no coding experience — write all code in full, never ask user to modify code manually
- Prioritize robustness over cleverness; this runs unattended at remote dark sites
- NINA 3.2 is `.net8.0-windows`. One NuGet package (`NINA.Plugin 3.2.0.9001`) covers all dependencies
- MEF uses `System.ComponentModel.Composition` (the traditional/legacy MEF), not `System.Composition`
- The dockable panel DataTemplate key naming convention is critical: `{FullNamespace}.{ClassName}_Dockable`
- `Resources.xaml` must have `x:Class="CableWrapMonitor.Resources"` to connect to its code-behind
- State file path: `%LOCALAPPDATA%\NINA\Plugins\CableWrapMonitor\state.json`
- Slew detection threshold default: 5°/tick. Normal sidereal rate is ~0.1°/tick, so this is safely above noise
