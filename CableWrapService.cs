using System;
using System.Collections.ObjectModel;
using System.ComponentModel.Composition;
using System.IO;
using System.Timers;
using System.Windows;
using Newtonsoft.Json;
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
        private readonly Timer    pollTimer;
        private bool              disposed      = false;
        private int               _pollTickCount = 0;

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
        /// Observable list of history entries shown in the panel's history section.
        /// Updated on the UI thread whenever a new entry is appended.
        /// </summary>
        public ObservableCollection<WrapHistoryEntry> WrapHistory { get; }
            = new ObservableCollection<WrapHistoryEntry>();

        // ── Constructor ───────────────────────────────────────────────────────────

        [ImportingConstructor]
        public CableWrapService(ITelescopeMediator telescopeMediator) {
            this.telescopeMediator = telescopeMediator;

            Directory.CreateDirectory(DataDirectory);

            // Restore persisted state and settings from disk
            state    = LoadState();
            settings = LoadSettings();

            _warningThresholdRotations = settings.WarningThresholdRotations;
            _totalDegreesRotated       = state.TotalDegreesRotated;

            foreach (var entry in state.WrapHistory)
                WrapHistory.Add(entry);

            // Start the polling timer (1 second interval, state saved every 10 ticks)
            pollTimer           = new Timer(TimeSpan.FromSeconds(1).TotalMilliseconds);
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
                    TrackingStatus = TrackingStatus.NotConnected;
                    return;
                }

                if (!info.TrackingEnabled && !info.Slewing) {
                    TrackingStatus    = TrackingStatus.Stopped;
                    state.LastKnownRA = null; // re-establish baseline when tracking resumes
                    return;
                }

                double currentRA = info.RightAscension; // hours, 0.0 – 24.0

                if (state.LastKnownRA.HasValue) {
                    double delta = currentRA - state.LastKnownRA.Value;

                    // Correct for the 0h/24h wraparound in the RA coordinate system.
                    // A jump from 23.9h to 0.1h is a +0.2h forward step, not a -23.8h
                    // backward jump.
                    if (delta >  12.0) delta -= 24.0;
                    if (delta < -12.0) delta += 24.0;

                    double deltaInDegrees = delta * 15.0; // 15°/hour

                    // Count all RA movement — both sidereal tracking and slews physically
                    // rotate the mount axis and wind the cable.
                    state.TotalDegreesRotated += deltaInDegrees;
                    _totalDegreesRotated       = state.TotalDegreesRotated;

                    RaisePropertyChanged(nameof(TotalDegreesRotated));
                    RaisePropertyChanged(nameof(WrapCount));

                    CheckForWrapCrossing();
                    CheckForWarningThreshold();
                } else {
                    // First tick after connection — establish baseline, no delta yet
                    TrackingStatus = TrackingStatus.Tracking;
                }

                state.LastKnownRA = currentRA;

                // Save to disk every 10 seconds rather than every tick
                _pollTickCount++;
                if (_pollTickCount % 10 == 0)
                    SaveState();

            } catch (Exception ex) {
                Logger.Error($"CableWrapMonitor: Error during poll tick: {ex.Message}");
            }
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
            state.LastKnownRA         = null; // re-establish baseline on next tick

            _totalDegreesRotated = 0;
            RaisePropertyChanged(nameof(TotalDegreesRotated));
            RaisePropertyChanged(nameof(WrapCount));

            TrackingStatus = TrackingStatus.Tracking;
            SaveState();
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
