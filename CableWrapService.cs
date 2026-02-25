using System;
using System.Collections.ObjectModel;
using System.ComponentModel.Composition;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using System.Windows;
using Newtonsoft.Json;
using NINA.Astrometry;
using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces.Mediator;

namespace CableWrapMonitor {

    /// <summary>
    /// Describes what the tracker is currently doing, used by the UI to show
    /// a coloured status indicator.
    /// </summary>
    public enum TrackingStatus {
        NotConnected,
        Stopped,
        Tracking,
        Warning
    }

    /// <summary>
    /// Core business-logic service for the cable wrap plugin.
    ///
    /// This class is a MEF-shared singleton: NINA constructs it once and injects it
    /// into both the dockable panel ViewModel and the sequence instruction.
    ///
    /// Responsibilities:
    ///   - Poll telescope RA every 5 seconds via ITelescopeMediator
    ///   - Accumulate signed RA rotation in degrees, handling the 0h/24h wraparound
    ///   - Detect and ignore slews (large RA changes in a single poll cycle)
    ///   - Log 360° crossings to a history list
    ///   - Fire a NINA warning notification when the threshold is exceeded
    ///   - Persist and restore all state via JSON so remote sessions survive a restart
    /// </summary>
    [Export]
    [PartCreationPolicy(CreationPolicy.Shared)]
    public class CableWrapService : BaseINPC, IDisposable {

        // ── Dependencies ──────────────────────────────────────────────────────────

        private readonly ITelescopeMediator telescopeMediator;

        // ── Persistence paths ─────────────────────────────────────────────────────

        private static readonly string DataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NINA", "Plugins", "CableWrapMonitor");

        private static readonly string StatePath    = Path.Combine(DataDirectory, "state.json");
        private static readonly string SettingsPath = Path.Combine(DataDirectory, "settings.json");

        // ── Internal state ────────────────────────────────────────────────────────

        private CableWrapState    state;
        private CableWrapSettings settings;
        private readonly System.Timers.Timer pollTimer;
        private bool              disposed           = false;
        private int               _pollTickCount     = 0;
        private int               _trackingTickCount = 0;
        private bool              _isUnwinding       = false;

        // ── Observable properties (bound to the dockable panel UI) ────────────────

        private TrackingStatus _trackingStatus = TrackingStatus.NotConnected;
        public TrackingStatus TrackingStatus {
            get => _trackingStatus;
            private set { _trackingStatus = value; RaisePropertyChanged(); }
        }

        private double _totalDegreesRotated = 0;
        public double TotalDegreesRotated {
            get => _totalDegreesRotated;
            private set {
                _totalDegreesRotated = value;
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(WrapCount));
            }
        }

        /// <summary>
        /// Total rotation expressed as a fraction of full rotations.
        /// e.g. 247.3° → 0.69 wraps.
        /// </summary>
        public double WrapCount => TotalDegreesRotated / 360.0;

        private double _warningThresholdRotations = 1.0;
        /// <summary>
        /// User-configurable threshold. Setting it saves to disk immediately.
        /// </summary>
        public double WarningThresholdRotations {
            get => _warningThresholdRotations;
            set {
                _warningThresholdRotations = Math.Clamp(value, 0.5, 3.0);
                settings.WarningThresholdRotations = _warningThresholdRotations;
                SaveSettings();
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(WarningThresholdDegrees));
            }
        }

        public double WarningThresholdDegrees => WarningThresholdRotations * 360.0;

        /// <summary>
        /// True while an automated unwind is in progress. Used by the UI to disable
        /// the Unwind and Reset buttons during the operation.
        /// </summary>
        public bool IsUnwinding {
            get => _isUnwinding;
            private set { _isUnwinding = value; RaisePropertyChanged(); }
        }

        /// <summary>
        /// Observable list of history entries shown in the panel's history section.
        /// Updated on the UI thread whenever a new entry is appended.
        /// </summary>
        public ObservableCollection<WrapHistoryEntry> WrapHistory { get; }
            = new ObservableCollection<WrapHistoryEntry>();

        // ── Static singleton (used by CheckCableWrapInstruction to avoid MEF issues) ──

        /// <summary>
        /// Set when the service is constructed by MEF. Sequence items access the
        /// service through this property rather than via MEF injection, because NINA
        /// may compose sequence items in a context where plugin-internal exports are
        /// not available.
        /// </summary>
        public static CableWrapService? Instance { get; private set; }

        // ── Constructor ───────────────────────────────────────────────────────────

        [ImportingConstructor]
        public CableWrapService(ITelescopeMediator telescopeMediator) {
            this.telescopeMediator = telescopeMediator;
            Instance = this;

            Directory.CreateDirectory(DataDirectory);

            // Restore persisted state and settings from disk
            state    = LoadState();
            settings = LoadSettings();

            _warningThresholdRotations = settings.WarningThresholdRotations;
            _totalDegreesRotated       = state.TotalDegreesRotated;

            foreach (var entry in state.WrapHistory)
                WrapHistory.Add(entry);

            // Start the polling timer (1 second interval, state saved every 10 ticks)
            pollTimer           = new System.Timers.Timer(TimeSpan.FromSeconds(1).TotalMilliseconds);
            pollTimer.Elapsed  += OnPollTick;
            pollTimer.AutoReset = true;
            pollTimer.Start();

            Logger.Info("CableWrapMonitor: Service started. " +
                        $"Resuming from {state.TotalDegreesRotated:F1}° total rotation.");
        }

        // ── Poll tick ─────────────────────────────────────────────────────────────

        private void OnPollTick(object? sender, ElapsedEventArgs e) {
            try {
                var info = telescopeMediator.GetInfo();

                if (!info.Connected) {
                    TrackingStatus         = TrackingStatus.NotConnected;
                    state.LastKnownRA      = null;
                    state.LastKnownAzimuth = null;
                    return;
                }

                if (!info.TrackingEnabled && !info.Slewing) {
                    TrackingStatus     = TrackingStatus.Stopped;
                    _trackingTickCount = 0;
                    return;
                }

                if (info.Slewing) {
                    // ── SLEW MODE: use RA (azimuth is unreliable during slews) ──────────
                    // Reset azimuth baseline so tracking picks up cleanly after the slew.
                    _trackingTickCount     = 0;
                    state.LastKnownAzimuth = null;

                    double currentRA = info.RightAscension; // hours, 0.0 – 24.0
                    if (state.LastKnownRA.HasValue) {
                        double delta = currentRA - state.LastKnownRA.Value;
                        if (delta >  12.0) delta -= 24.0;
                        if (delta < -12.0) delta += 24.0;
                        Accumulate(delta * 15.0);
                    } else {
                        TrackingStatus = TrackingStatus.Tracking;
                    }
                    state.LastKnownRA = currentRA;

                } else {
                    // ── TRACKING MODE: use Azimuth (RA is held constant during tracking) ─
                    // Reset RA baseline so the next slew picks up cleanly.
                    state.LastKnownRA = null;

                    _trackingTickCount++;
                    if (_trackingTickCount < 5) return; // sample every 5 seconds
                    _trackingTickCount = 0;

                    double currentAzimuth = info.Azimuth; // degrees, 0.0 – 360.0
                    if (state.LastKnownAzimuth.HasValue) {
                        double delta = currentAzimuth - state.LastKnownAzimuth.Value;
                        if (delta >  180.0) delta -= 360.0;
                        if (delta < -180.0) delta += 360.0;
                        Accumulate(delta);
                    } else {
                        TrackingStatus = TrackingStatus.Tracking;
                    }
                    state.LastKnownAzimuth = currentAzimuth;
                }

                // Save to disk every 10 seconds rather than every tick
                _pollTickCount++;
                if (_pollTickCount % 10 == 0)
                    SaveState();

            } catch (Exception ex) {
                Logger.Error($"CableWrapMonitor: Error during poll tick: {ex.Message}");
            }
        }

        // Adds a delta to the accumulator and updates the UI
        private void Accumulate(double deltaInDegrees) {
            state.TotalDegreesRotated += deltaInDegrees;
            _totalDegreesRotated       = state.TotalDegreesRotated;
            RaisePropertyChanged(nameof(TotalDegreesRotated));
            RaisePropertyChanged(nameof(WrapCount));
            CheckForWrapCrossing();
            CheckForWarningThreshold();
        }

        // Logs a new entry every time the accumulator crosses another 360° multiple
        private void CheckForWrapCrossing() {
            int newWrapCount = (int)Math.Floor(Math.Abs(state.TotalDegreesRotated) / 360.0);
            if (newWrapCount > state.LastLoggedWrapCount) {
                state.LastLoggedWrapCount = newWrapCount;
                string sign = state.TotalDegreesRotated >= 0 ? "+" : "-";
                AppendHistory(state.TotalDegreesRotated,
                    $"Crossed {sign}{newWrapCount * 360}°");
                Logger.Warning($"CableWrapMonitor: Wrap crossing #{newWrapCount} at " +
                               $"{state.TotalDegreesRotated:F1}°");
            }
        }

        // Fires the alert once when the warning threshold is first exceeded
        private void CheckForWarningThreshold() {
            if (Math.Abs(state.TotalDegreesRotated) >= WarningThresholdDegrees
                && !state.AlertFired) {
                state.AlertFired = true;
                TrackingStatus   = TrackingStatus.Warning;
                FireAlert();
            } else if (TrackingStatus != TrackingStatus.Warning) {
                TrackingStatus = TrackingStatus.Tracking;
            }
        }

        private void FireAlert() {
            double wraps = Math.Abs(TotalDegreesRotated) / 360.0;
            string msg =
                $"CABLE WRAP WARNING: The Seestar USB cable has rotated " +
                $"{wraps:F1} times ({Math.Abs(TotalDegreesRotated):F0}°). " +
                "Check the telescope and unwind the cable, then click " +
                "'Reset — Cable Unwound' in the Cable Wrap Monitor panel.";

            Logger.Warning($"CableWrapMonitor: {msg}");
        }

        // ── Public reset (called by the panel's Reset button) ─────────────────────

        /// <summary>
        /// Resets the accumulator to zero, recording a manual-reset entry in the history.
        /// Call this after physically unwinding the cable.
        /// </summary>
        public void Reset() {
            Logger.Info($"CableWrapMonitor: Manual reset at {state.TotalDegreesRotated:F1}°.");

            AppendHistory(state.TotalDegreesRotated, "Manual reset — cable physically unwound");

            state.TotalDegreesRotated = 0;
            state.ZeroSetTimestamp    = DateTime.UtcNow;
            state.LastLoggedWrapCount = 0;
            state.AlertFired          = false;
            state.LastKnownRA         = null; // re-establish baselines on next tick
            state.LastKnownAzimuth    = null;

            _totalDegreesRotated = 0;
            RaisePropertyChanged(nameof(TotalDegreesRotated));
            RaisePropertyChanged(nameof(WrapCount));

            TrackingStatus = TrackingStatus.Tracking;
            SaveState();
        }

        // ── Automated unwind ──────────────────────────────────────────────────────

        /// <summary>
        /// Unwinds the USB cable by:
        ///   1. Slewing straight up to a safe high-altitude position (near north celestial
        ///      pole) so that no subsequent RA slew can point below the horizon.
        ///   2. Stepping RA in the opposite direction of the wind in ≤60° increments until
        ///      the accumulator is within 10° of zero.
        ///   3. Sending the scope home (FindHome) to land on a clean mechanical zero.
        ///   4. Resetting the accumulator to zero.
        /// </summary>
        public async Task UnwindAsync(IProgress<ApplicationStatus>? progress, CancellationToken token) {
            if (_isUnwinding) return;
            try {
                IsUnwinding = true;

                var info = telescopeMediator.GetInfo();
                if (!info.Connected)
                    throw new Exception("Telescope not connected — cannot auto-unwind.");

                double remaining = TotalDegreesRotated;
                if (Math.Abs(remaining) < 10.0) {
                    Reset();
                    progress?.Report(new ApplicationStatus { Status = "Cable wrap already near zero. Counter reset." });
                    return;
                }

                Logger.Info($"CableWrapMonitor: Auto-unwind started. {remaining:+0.0;-0.0}° to unwind.");
                AppendHistory(state.TotalDegreesRotated,
                    $"Auto-unwind started — {remaining:+0.0;-0.0}° to unwind");

                // ── Step 1: Slew straight up to a safe starting position ───────────
                // Pointing near Dec=80° (close to the north celestial pole) keeps the
                // scope at high altitude for every RA value we'll visit during the
                // unwind loop, so we can never hit "below horizon".
                // Using SiderealTime as the RA puts us on the meridian (peak altitude).
                progress?.Report(new ApplicationStatus {
                    Status = "Pointing straight up to safe position before unwinding..."
                });

                double safeRA  = info.SiderealTime; // meridian = maximum altitude
                double safeDec = 80.0;              // near pole, circumpolar everywhere

                Logger.Info($"CableWrapMonitor: Slewing to safe position " +
                            $"RA {safeRA:F2}h Dec {safeDec:F0}° before unwind.");

                var safeTarget = new Coordinates(safeRA, safeDec, Epoch.JNOW, Coordinates.RAType.Hours);
                bool safeOk = await telescopeMediator.SlewToCoordinatesAsync(safeTarget, token);
                if (!safeOk)
                    throw new Exception(
                        "Could not slew to safe starting position (RA=LST, Dec=80°). " +
                        "Make sure the telescope is connected and the mount limits allow pointing north.");

                await Task.Delay(2000, token);
                info = telescopeMediator.GetInfo();
                double currentRA  = info.RightAscension;
                double currentDec = info.Declination; // should be ≈80°
                remaining = TotalDegreesRotated;

                // ── Step 2: Unwind loop ────────────────────────────────────────────
                // At Dec≈80°, all RA positions are circumpolar and well above the
                // horizon, so slews of up to 60° of RA are always safe.
                const int    maxSteps  = 20;
                const double maxStepDeg = 60.0; // safe step size at high declination
                int step = 0;

                while (Math.Abs(remaining) >= 10.0 && step < maxSteps && !token.IsCancellationRequested) {
                    step++;

                    // On the Seestar (alt-az mount), increasing RA causes the azimuth
                    // axis to rotate in the direction that unwinds positive wrap, and
                    // decreasing RA unwinds negative wrap.  stepHours carries the sign
                    // of remaining, so adding it moves RA in the correct direction.
                    double stepDeg   = Math.Sign(remaining) * Math.Min(Math.Abs(remaining), maxStepDeg);
                    double stepHours = stepDeg / 15.0;
                    double targetRA  = ((currentRA + stepHours) % 24.0 + 24.0) % 24.0;

                    progress?.Report(new ApplicationStatus {
                        Status = $"Unwinding step {step}/{maxSteps}: {remaining:+0.0;-0.0}° remaining..."
                    });

                    Logger.Info($"CableWrapMonitor: Unwind step {step} — " +
                                $"RA {currentRA:F3}h → {targetRA:F3}h " +
                                $"(step {stepDeg:+0.0;-0.0}°, {remaining:+0.0;-0.0}° remaining).");

                    var target = new Coordinates(targetRA, currentDec, Epoch.JNOW, Coordinates.RAType.Hours);
                    bool slewOk = await telescopeMediator.SlewToCoordinatesAsync(target, token);
                    if (!slewOk)
                        throw new Exception(
                            $"Slew failed during unwind step {step}. " +
                            "Please check the mount and try again.");

                    await Task.Delay(2000, token);
                    info      = telescopeMediator.GetInfo();
                    currentRA = info.RightAscension;
                    remaining = TotalDegreesRotated;
                }

                if (token.IsCancellationRequested) {
                    AppendHistory(state.TotalDegreesRotated, "Auto-unwind cancelled by user");
                    progress?.Report(new ApplicationStatus { Status = "Unwind cancelled." });
                    return;
                }

                // ── Step 3: Home the scope to land on a clean mechanical zero ──────
                progress?.Report(new ApplicationStatus {
                    Status = $"Unwind steps done ({remaining:+0.0;-0.0}° residual) — homing scope..."
                });
                Logger.Info($"CableWrapMonitor: Unwind steps complete (residual {remaining:+0.0;-0.0}°). " +
                            "Sending scope home.");

                await telescopeMediator.FindHome(progress, token);
                await Task.Delay(2000, token);

                // ── Step 4: Reset counter ──────────────────────────────────────────
                Logger.Info("CableWrapMonitor: Auto-unwind complete. Resetting counter.");
                AppendHistory(state.TotalDegreesRotated, "Auto-unwind complete — scope homed, counter reset");
                Reset();
                progress?.Report(new ApplicationStatus {
                    Status = "Cable unwound. Scope homed. Counter reset to zero."
                });

            } catch (OperationCanceledException) {
                AppendHistory(state.TotalDegreesRotated, "Auto-unwind cancelled");
                progress?.Report(new ApplicationStatus { Status = "Unwind cancelled." });
            } catch (Exception ex) {
                Logger.Error($"CableWrapMonitor: Unwind failed: {ex.Message}");
                progress?.Report(new ApplicationStatus { Status = $"Unwind failed: {ex.Message}" });
                throw;
            } finally {
                IsUnwinding = false;
            }
        }

        // ── History helper ────────────────────────────────────────────────────────

        private void AppendHistory(double cumulativeDegrees, string note) {
            var entry = new WrapHistoryEntry {
                Timestamp         = DateTime.Now,
                CumulativeDegrees = cumulativeDegrees,
                Note              = note
            };

            state.WrapHistory.Add(entry);

            // Trim entries older than 1 hour to keep the JSON file small
            DateTime cutoff = DateTime.Now.AddHours(-1);
            while (state.WrapHistory.Count > 0 && state.WrapHistory[0].Timestamp < cutoff)
                state.WrapHistory.RemoveAt(0);

            // Update the observable collection on the UI thread
            Application.Current?.Dispatcher.Invoke(() => {
                WrapHistory.Add(entry);
                while (WrapHistory.Count > 0 && WrapHistory[0].Timestamp < cutoff)
                    WrapHistory.RemoveAt(0);
            });
        }

        // ── Persistence ───────────────────────────────────────────────────────────

        private CableWrapState LoadState() {
            try {
                if (File.Exists(StatePath)) {
                    string json = File.ReadAllText(StatePath);
                    return JsonConvert.DeserializeObject<CableWrapState>(json)
                           ?? new CableWrapState();
                }
            } catch (Exception ex) {
                Logger.Error($"CableWrapMonitor: Could not load state, starting fresh. {ex.Message}");
            }
            return new CableWrapState();
        }

        private void SaveState() {
            try {
                File.WriteAllText(StatePath,
                    JsonConvert.SerializeObject(state, Formatting.Indented));
            } catch (Exception ex) {
                Logger.Error($"CableWrapMonitor: Could not save state. {ex.Message}");
            }
        }

        private CableWrapSettings LoadSettings() {
            try {
                if (File.Exists(SettingsPath)) {
                    string json = File.ReadAllText(SettingsPath);
                    return JsonConvert.DeserializeObject<CableWrapSettings>(json)
                           ?? new CableWrapSettings();
                }
            } catch (Exception ex) {
                Logger.Error($"CableWrapMonitor: Could not load settings. {ex.Message}");
            }
            return new CableWrapSettings();
        }

        private void SaveSettings() {
            try {
                File.WriteAllText(SettingsPath,
                    JsonConvert.SerializeObject(settings, Formatting.Indented));
            } catch (Exception ex) {
                Logger.Error($"CableWrapMonitor: Could not save settings. {ex.Message}");
            }
        }

        // ── Disposal ──────────────────────────────────────────────────────────────

        public void Dispose() {
            if (!disposed) {
                pollTimer.Stop();
                pollTimer.Dispose();
                SaveState();
                disposed = true;
            }
        }
    }
}
