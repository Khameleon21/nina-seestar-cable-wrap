using System;
using System.Collections.ObjectModel;
using System.ComponentModel.Composition;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Newtonsoft.Json;
using NINA.Astrometry;
using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Equipment.Equipment.MyTelescope;
using NINA.Equipment.Interfaces;
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
    ///   - Subscribe to NINA's telescope mediator as a consumer so we receive the
    ///     same fresh position data that NINA's own UI panels receive (no separate
    ///     poll timer — NINA pushes data to UpdateDeviceInfo at its native rate).
    ///   - Accumulate signed azimuth rotation in degrees, handling wrap-around.
    ///   - Track both slews (RA-based accumulator) and sidereal tracking (Az-based).
    ///   - Log 360° crossings to a history list.
    ///   - Fire a warning notification when the threshold is exceeded.
    ///   - Persist and restore all state via JSON so remote sessions survive a restart.
    /// </summary>
    [Export]
    [PartCreationPolicy(CreationPolicy.Shared)]
    public class CableWrapService : BaseINPC, ITelescopeConsumer, IDisposable {

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
        private bool              disposed            = false;
        private bool              _isUnwinding        = false;
        private bool              _wasAtHome          = false;
        private bool              _slewInProgress     = false;
        private int               _slewDirectionSign  = 0;   // +1=CW, -1=CCW, 0=unknown
        private double            _preSlewRA          = 0;   // RA at slew start
        private double            _preSlewTotal       = 0;   // TotalDegreesRotated at slew start
        private double            _slewLiveAzAccum    = 0;   // cumulative Az delta during current slew (for slew-end commit)
        private double            _prevSlewAz         = double.NaN; // computed Az at previous slew call
        private double            _slewDisplayAccum   = 0;   // display-only accumulator (driver raw Az)
        private double            _prevSlewDisplayAz  = double.NaN; // driver Az at previous slew call
        private DateTime          _lastSlewLog        = DateTime.MinValue;
        private bool              _isMoving           = false;
        private string            _movementIndicator  = "";

        // Time-based rate limiters (replaces tick counters — UpdateDeviceInfo rate is variable)
        private DateTime          _lastTrackingSample = DateTime.MinValue;
        private DateTime          _atHomeArrivalTime  = DateTime.MinValue;
        private DateTime          _lastStateSave      = DateTime.MinValue;
        private static readonly TimeSpan TrackingSampleInterval = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan AtHomeSettleDelay      = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan StateSaveInterval      = TimeSpan.FromSeconds(10);

        private string            _lastBranch         = "";   // transition detection
        private readonly object   _logLock            = new object();
        private string            _logDate            = "";
        private string            _logPath            = "";

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
        /// True while the scope is actively slewing or sidereal-tracking.
        /// Used by the UI to drive a pulsing animation on the movement indicator.
        /// </summary>
        public bool IsMoving {
            get => _isMoving;
            private set { _isMoving = value; RaisePropertyChanged(); }
        }

        /// <summary>
        /// Short text label shown next to Total Rotation while the scope is moving.
        /// Values: "↻ CW", "↺ CCW", "↻ slewing", "→ tracking", or "".
        /// </summary>
        public string MovementIndicator {
            get => _movementIndicator;
            private set { _movementIndicator = value; RaisePropertyChanged(); }
        }

        /// <summary>
        /// Observable list of history entries shown in the panel's history section.
        /// Updated on the UI thread whenever a new entry is appended.
        /// </summary>
        public ObservableCollection<WrapHistoryEntry> WrapHistory { get; }
            = new ObservableCollection<WrapHistoryEntry>();

        // ── Static singleton (used by sequence items to avoid MEF issues) ──────────

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
            CleanupOldLogs();

            // Restore persisted state and settings from disk
            state    = LoadState();
            settings = LoadSettings();

            _warningThresholdRotations = settings.WarningThresholdRotations;
            _totalDegreesRotated       = state.TotalDegreesRotated;

            foreach (var entry in state.WrapHistory)
                WrapHistory.Add(entry);

            // Register as a telescope consumer — NINA will push fresh TelescopeInfo to us
            // at its native poll rate (typically several times per second), giving us the
            // same real-time position data that NINA's own UI panels receive.
            telescopeMediator.RegisterConsumer(this);

            Logger.Info("CableWrapMonitor: Service started. " +
                        $"Resuming from {state.TotalDegreesRotated:F1}° total rotation.");
            CwmLog($"[START] Service started. Resuming from {state.TotalDegreesRotated:F1}°. " +
                   $"Threshold: {settings.WarningThresholdRotations:F1} wraps.");
        }

        // ── ITelescopeConsumer ────────────────────────────────────────────────────

        /// <summary>
        /// Called by NINA's telescope mediator every time fresh telescope data is
        /// available. This replaces the old 1-second poll timer — we now receive the
        /// same data at the same rate as NINA's own display panels.
        /// </summary>
        public void UpdateDeviceInfo(TelescopeInfo info) {
            try {
                // Do not accumulate while an automated unwind is in progress —
                // the unwind routine manages the counter manually.
                if (_isUnwinding) return;

                if (!info.Connected) {
                    if (_lastBranch != "DISC") {
                        CwmLog("[DISCONNECT] Telescope disconnected.");
                        _lastBranch = "DISC";
                    }
                    TrackingStatus         = TrackingStatus.NotConnected;
                    state.LastKnownRA      = null;
                    state.LastKnownAzimuth = null;
                    _wasAtHome             = false;
                    _slewInProgress        = false;
                    _slewDirectionSign     = 0;
                    _slewLiveAzAccum       = 0.0;
                    _prevSlewAz            = double.NaN;
                    _slewDisplayAccum      = 0.0;
                    _prevSlewDisplayAz     = double.NaN;
                    _atHomeArrivalTime     = DateTime.MinValue;
                    IsMoving               = false;
                    MovementIndicator      = "";
                    return;
                }

                if (info.Slewing) {
                    // ── SLEW MODE ────────────────────────────────────────────────────────
                    // Azimuth is unreliable mid-slew (-440° errors observed).
                    // We accumulate incremental Az tick-by-tick during the slew and commit
                    // the total at slew-end. No live display update during the slew — the
                    // Seestar ALPACA driver reports planned waypoints, not real-time position,
                    // so intermediate values are unreliable for display purposes.
                    _wasAtHome         = false;
                    _atHomeArrivalTime = DateTime.MinValue;

                    if (!_slewInProgress) {
                        _slewInProgress      = true;
                        _slewDirectionSign   = 0;
                        _preSlewRA           = info.RightAscension;
                        _preSlewTotal        = state.TotalDegreesRotated;
                        _slewLiveAzAccum     = 0.0;
                        _slewDisplayAccum    = 0.0;
                        _lastSlewLog         = DateTime.UtcNow;
                        // Seed the real accumulator from the last known stationary Az.
                        _prevSlewAz          = state.LastKnownAzimuth.HasValue
                                               ? state.LastKnownAzimuth.Value
                                               : GetComputedAzimuth(info);
                        // Seed the display accumulator from the driver's raw Az.
                        // info.Azimuth tracks actual mechanical position (same source as
                        // NINA's telescope panel) without the RA/Dec waypoint spike.
                        _prevSlewDisplayAz   = ((info.Azimuth % 360.0) + 360.0) % 360.0;
                        CwmLog($"[→SLEW] Slew started. Pre-slew Az={state.LastKnownAzimuth?.ToString("F2") ?? "unknown"}° driverAz={_prevSlewDisplayAz:F2}° RA={_preSlewRA:F3}h");
                        _lastBranch = "SLEW";
                    }

                    // Detect rotation direction from RA movement until confirmed.
                    // RA is reliable during slews even when Azimuth is not.
                    // Northern hemisphere: RA decreasing → CW, RA increasing → CCW.
                    if (_slewDirectionSign == 0) {
                        double raDelta = info.RightAscension - _preSlewRA;
                        if (raDelta >  12.0) raDelta -= 24.0;   // 0h/24h wraparound
                        if (raDelta < -12.0) raDelta += 24.0;
                        if (Math.Abs(raDelta) >= 0.05) {         // ~0.75° of travel — enough to be sure
                            _slewDirectionSign = raDelta < 0 ? +1 : -1;
                            CwmLog($"[SLEW-DIR] RA {_preSlewRA:F3}h→{info.RightAscension:F3}h " +
                                   $"Δ={raDelta:+0.00;-0.00}h → sign={_slewDirectionSign:+0;-0;0} " +
                                   $"({(_slewDirectionSign > 0 ? "CW" : "CCW")})");
                        }
                    }

                    // ── Real accumulator (GetComputedAzimuth) ─────────────────────────
                    // Used for the slew-end commit. Proven correct despite intermediate
                    // spikes (the driver's RA/Dec waypoint jump self-cancels over 2 ticks).
                    {
                        double liveAz    = GetComputedAzimuth(info);
                        double tickDelta = liveAz - _prevSlewAz;
                        if (tickDelta >  180.0) tickDelta -= 360.0;
                        if (tickDelta < -180.0) tickDelta += 360.0;
                        _slewLiveAzAccum += tickDelta;
                        _prevSlewAz       = liveAz;
                    }

                    // ── Display accumulator (driver raw Az) ────────────────────────────
                    // info.Azimuth is what NINA's own telescope panel displays — it tracks
                    // actual mechanical position smoothly without the RA/Dec waypoint spike.
                    // Incremental tick accumulation, separate from the real accumulator.
                    {
                        double driverAz = ((info.Azimuth % 360.0) + 360.0) % 360.0;
                        if (!double.IsNaN(_prevSlewDisplayAz)) {
                            double displayDelta = driverAz - _prevSlewDisplayAz;
                            if (displayDelta >  180.0) displayDelta -= 360.0;
                            if (displayDelta < -180.0) displayDelta += 360.0;
                            _slewDisplayAccum    += displayDelta;
                            _totalDegreesRotated  = _preSlewTotal + _slewDisplayAccum;
                            RaisePropertyChanged(nameof(TotalDegreesRotated));
                            RaisePropertyChanged(nameof(WrapCount));
                        }
                        _prevSlewDisplayAz = driverAz;
                    }

                    // Periodic slew log entry (every 5 seconds)
                    if ((DateTime.UtcNow - _lastSlewLog).TotalSeconds >= 5.0) {
                        _lastSlewLog = DateTime.UtcNow;
                        CwmLog($"[SLEW] driverAz={((info.Azimuth%360+360)%360):F2}° " +
                               $"display={_slewDisplayAccum:+0.0;-0.0}° " +
                               $"calcAccum={_slewLiveAzAccum:+0.0;-0.0}°");
                    }

                    IsMoving = true;
                    MovementIndicator = _slewDirectionSign == +1 ? "↻ CW"  :
                                        _slewDirectionSign == -1 ? "↺ CCW" :
                                                                   "↻ slewing";
                    TrackingStatus = TrackingStatus.Tracking;

                } else {
                    // ── Slew just ended — commit the incremental accumulator ──────────────
                    if (_slewInProgress) {
                        _slewInProgress     = false;
                        _lastTrackingSample = DateTime.MinValue; // reset so tracking immediately baselines
                        _lastBranch         = "";                // reset so transition logs fire below

                        // Final tick: capture any movement between last slew tick and Slewing→false.
                        double postSlewAz = info.AtHome ? 0.0 : GetComputedAzimuth(info);
                        if (!double.IsNaN(_prevSlewAz)) {
                            double finalDelta = postSlewAz - _prevSlewAz;
                            if (finalDelta >  180.0) finalDelta -= 360.0;
                            if (finalDelta < -180.0) finalDelta += 360.0;
                            _slewLiveAzAccum += finalDelta;
                        }

                        CwmLog($"[SLEW-END] Az {state.LastKnownAzimuth?.ToString("F2") ?? "?"}°→{postSlewAz:F2}° " +
                               $"(AtHome={info.AtHome}) accum={_slewLiveAzAccum:+0.00;-0.00}° " +
                               $"total={_preSlewTotal + _slewLiveAzAccum:+0.0;-0.0}°");

                        Accumulate(_slewLiveAzAccum);
                        state.LastKnownAzimuth = postSlewAz;
                    }

                    if (info.TrackingEnabled) {
                        // ── TRACKING MODE: Azimuth (RA constant during sidereal tracking) ──
                        _wasAtHome         = false;
                        _atHomeArrivalTime = DateTime.MinValue;
                        state.LastKnownRA  = null;
                        IsMoving           = true;
                        MovementIndicator  = "→ tracking";

                        if (_lastBranch != "TRACK") {
                            CwmLog("[→TRACK] Sidereal tracking started.");
                            _lastBranch = "TRACK";
                        }

                        // Rate-limit to every 5 seconds — NINA may push data many times/sec
                        DateTime now = DateTime.UtcNow;
                        if ((now - _lastTrackingSample) >= TrackingSampleInterval) {
                            _lastTrackingSample = now;

                            double currentAzimuth = GetComputedAzimuth(info);
                            if (state.LastKnownAzimuth.HasValue) {
                                double delta = currentAzimuth - state.LastKnownAzimuth.Value;
                                if (delta >  180.0) delta -= 360.0;
                                if (delta < -180.0) delta += 360.0;
                                Accumulate(delta);
                                CwmLog($"[TRACK] Az delta={delta:+0.00;-0.00}° total={TotalDegreesRotated:+0.0;-0.0}° (calcAz={currentAzimuth:F2}°)");
                            } else {
                                CwmLog($"[TRACK] Baseline set. calcAz={currentAzimuth:F2}°");
                                TrackingStatus = TrackingStatus.Tracking;
                            }
                            state.LastKnownAzimuth = currentAzimuth;
                        }

                    } else {
                        // ── STOPPED ───────────────────────────────────────────────────────
                        // Not tracking, not slewing. Keep azimuth fresh so the next slew
                        // has an accurate pre-slew baseline. Snap fires here after FindHome.
                        state.LastKnownRA  = null;
                        TrackingStatus     = TrackingStatus.Stopped;
                        IsMoving           = false;
                        MovementIndicator  = "";

                        if (_lastBranch != "STOP") {
                            // First STOPPED call — immediately catch any azimuth motion that
                            // occurred since the last tracking sample (e.g., FindHome completing).
                            if (state.LastKnownAzimuth.HasValue) {
                                double immediateAz = info.AtHome ? 0.0 : GetComputedAzimuth(info);
                                double catchDelta  = immediateAz - state.LastKnownAzimuth.Value;
                                if (catchDelta >  180.0) catchDelta -= 360.0;
                                if (catchDelta < -180.0) catchDelta += 360.0;
                                if (Math.Abs(catchDelta) >= 0.5) {
                                    Accumulate(catchDelta);
                                    CwmLog($"[→STOP] Catch-up: Az {state.LastKnownAzimuth.Value:F2}°→{immediateAz:F2}° " +
                                           $"delta={catchDelta:+0.00;-0.00}° total={TotalDegreesRotated:+0.0;-0.0}°");
                                }
                                state.LastKnownAzimuth = immediateAz;
                            }
                            CwmLog($"[→STOP] Scope stopped. AtHome={info.AtHome} total={TotalDegreesRotated:+0.0;-0.0}°");
                            _lastBranch = "STOP";
                        } else {
                            state.LastKnownAzimuth = info.AtHome ? 0.0 : GetComputedAzimuth(info);
                        }

                        // At-home snap: wait for position to fully settle after arriving home,
                        // then round off any residual drift to the nearest whole-wrap multiple.
                        if (info.AtHome) {
                            if (!_wasAtHome) {
                                // Scope just arrived at home — start the settle timer.
                                _atHomeArrivalTime = DateTime.UtcNow;
                                CwmLog($"[STOP] Arrived home. Waiting {AtHomeSettleDelay.TotalSeconds}s before snap. " +
                                       $"total={TotalDegreesRotated:+0.0;-0.0}°");
                            } else if (_atHomeArrivalTime != DateTime.MinValue &&
                                       (DateTime.UtcNow - _atHomeArrivalTime) >= AtHomeSettleDelay) {
                                _atHomeArrivalTime = DateTime.MinValue; // prevent repeat firing
                                SnapToHomePosition();
                            }
                        } else {
                            _atHomeArrivalTime = DateTime.MinValue;
                        }
                        _wasAtHome = info.AtHome;
                    }
                }

                // Time-based state save — runs at most every 10 seconds regardless of
                // how frequently UpdateDeviceInfo is called.
                DateTime saveNow = DateTime.UtcNow;
                if ((saveNow - _lastStateSave) >= StateSaveInterval) {
                    _lastStateSave = saveNow;
                    SaveState();
                }

            } catch (Exception ex) {
                Logger.Error($"CableWrapMonitor: Error in UpdateDeviceInfo: {ex.Message}");
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
                CwmLog($"[WRAP] Crossed {sign}{newWrapCount * 360}° — total now {state.TotalDegreesRotated:F1}°");
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

        // ── Computed azimuth from RA/Dec ──────────────────────────────────────────

        /// <summary>
        /// Computes azimuth from RA, Dec, sidereal time and site latitude — the same
        /// way NINA's own UI does. This is smooth and stable even during slews,
        /// whereas the ALPACA driver's Azimuth property can report garbage mid-slew.
        ///
        /// Az convention: N=0°, E=90°, S=180°, W=270°.
        /// Falls back to info.Azimuth if site latitude/longitude are not configured.
        /// </summary>
        private static double GetComputedAzimuth(TelescopeInfo info) {
            // Guard: if site coordinates are not set (both zero), fall back to driver
            if (Math.Abs(info.SiteLatitude) < 0.001 && Math.Abs(info.SiteLongitude) < 0.001)
                return info.Azimuth;

            // Hour angle in radians: HA = LST - RA  (both in hours → multiply by π/12)
            double haRad  = (info.SiderealTime - info.RightAscension) * Math.PI / 12.0;
            double decRad = info.Declination   * Math.PI / 180.0;
            double latRad = info.SiteLatitude  * Math.PI / 180.0;

            // Az = atan2(-cos(dec)·sin(ha),  sin(dec)·cos(lat) - cos(dec)·cos(ha)·sin(lat))
            // Gives N=0°, E=90°, S=180°, W=270° (standard astronomical Az).
            double x = -Math.Cos(decRad) * Math.Sin(haRad);
            double y  =  Math.Sin(decRad) * Math.Cos(latRad)
                       - Math.Cos(decRad) * Math.Cos(haRad) * Math.Sin(latRad);

            double az = Math.Atan2(x, y) * 180.0 / Math.PI;
            if (az < 0.0) az += 360.0;
            return az;
        }

        private void FireAlert() {
            double wraps = Math.Abs(TotalDegreesRotated) / 360.0;
            string msg =
                $"CABLE WRAP WARNING: The Seestar USB cable has rotated " +
                $"{wraps:F1} times ({Math.Abs(TotalDegreesRotated):F0}°). " +
                "Check the telescope and unwind the cable, then click " +
                "'Reset — Cable Unwound' in the Cable Wrap Monitor panel.";

            Logger.Warning($"CableWrapMonitor: {msg}");
            CwmLog($"[ALERT] Wrap warning! {Math.Abs(TotalDegreesRotated):F1}° " +
                   $"({Math.Abs(TotalDegreesRotated) / 360.0:F2} wraps). " +
                   $"Threshold: {WarningThresholdDegrees:F0}°");
        }

        // ── Public reset (called by the panel's Reset button) ─────────────────────

        /// <summary>
        /// Resets the accumulator to zero, recording a manual-reset entry in the history.
        /// Call this after physically unwinding the cable.
        /// </summary>
        public void Reset() {
            Logger.Info($"CableWrapMonitor: Manual reset at {state.TotalDegreesRotated:F1}°.");
            CwmLog($"[RESET] Counter reset from {state.TotalDegreesRotated:+0.0;-0.0}° to 0°.");

            AppendHistory(state.TotalDegreesRotated, "Manual reset — cable physically unwound");

            state.TotalDegreesRotated = 0;
            state.ZeroSetTimestamp    = DateTime.UtcNow;
            state.LastLoggedWrapCount = 0;
            state.AlertFired          = false;
            state.LastKnownRA         = null; // re-establish baselines on next update
            state.LastKnownAzimuth    = null;

            _totalDegreesRotated = 0;
            RaisePropertyChanged(nameof(TotalDegreesRotated));
            RaisePropertyChanged(nameof(WrapCount));

            TrackingStatus = TrackingStatus.Tracking;
            SaveState();
        }

        // ── At-home snap ──────────────────────────────────────────────────────────

        /// <summary>
        /// Called once when the scope has settled at its home position.
        /// Rounds the accumulated total to the nearest whole-wrap multiple to correct
        /// any residual drift. e.g. 355° → 360°, 5° → 0°, 722° → 720°.
        /// </summary>
        private void SnapToHomePosition() {
            double total   = state.TotalDegreesRotated;
            double snapped = Math.Round(total / 360.0) * 360.0;
            if (Math.Abs(snapped - total) < 0.001) return; // already at a whole wrap

            Logger.Info($"CableWrapMonitor: Scope returned home — snapping wrap " +
                        $"{total:F1}° → {snapped:F1}°.");
            CwmLog($"[SNAP] {total:+0.0;-0.0}° → {snapped:F1}° (corrected {snapped - total:+0.0;-0.0}°)");
            AppendHistory(total,
                $"Scope returned home — drift corrected {total:F1}° → {snapped:F1}°");

            state.TotalDegreesRotated = snapped;
            _totalDegreesRotated      = snapped;
            state.LastLoggedWrapCount = (int)Math.Abs(Math.Truncate(snapped / 360.0));
            if (Math.Abs(snapped) < WarningThresholdDegrees)
                state.AlertFired = false;

            RaisePropertyChanged(nameof(TotalDegreesRotated));
            RaisePropertyChanged(nameof(WrapCount));
            SaveState();
        }

        // ── Automated unwind ──────────────────────────────────────────────────────

        /// <summary>
        /// Unwinds the USB cable by:
        ///   1. Slewing straight up to a safe high-altitude position (near north celestial
        ///      pole) so that no subsequent RA slew can go below the horizon.
        ///   2. Stepping RA in ≤60° increments to physically rotate the azimuth axis
        ///      backward. UpdateDeviceInfo is suppressed during this time (_isUnwinding=true)
        ///      and the counter is decremented manually after each step so the UI shows
        ///      the number going down.
        ///   3. Sending the scope home to land on a clean mechanical zero.
        ///   4. Resetting the accumulator to zero.
        /// </summary>
        public async Task UnwindAsync(IProgress<ApplicationStatus>? progress, CancellationToken token) {
            if (_isUnwinding) return;
            try {
                // Set _isUnwinding = true BEFORE any slews so that UpdateDeviceInfo skips
                // accumulation for the entire operation — otherwise the mediator push
                // fights the unwind and the counter keeps climbing.
                IsUnwinding = true;

                var info = telescopeMediator.GetInfo();
                if (!info.Connected)
                    throw new Exception("Telescope not connected — cannot auto-unwind.");

                // Snapshot the total before we do anything. We will manually decrement
                // this value after each step so the UI reflects progress.
                double startTotal = TotalDegreesRotated;
                if (Math.Abs(startTotal) < 10.0) {
                    Reset();
                    progress?.Report(new ApplicationStatus { Status = "Cable wrap already near zero. Counter reset." });
                    return;
                }

                Logger.Info($"CableWrapMonitor: Auto-unwind started. {startTotal:+0.0;-0.0}° to unwind.");
                CwmLog($"[UNWIND-START] Auto-unwind started from {startTotal:+0.0;-0.0}°.");
                AppendHistory(state.TotalDegreesRotated,
                    $"Auto-unwind started — {startTotal:+0.0;-0.0}° to unwind");

                // ── Step 1: Slew straight up to a safe starting position ───────────
                // Pointing near Dec=80° keeps the scope well above the horizon for
                // every RA value we visit in the loop. SiderealTime as the RA puts us
                // on the meridian (highest altitude for that declination).
                progress?.Report(new ApplicationStatus {
                    Status = "Pointing straight up to safe position before unwinding..."
                });

                double safeRA  = info.SiderealTime;
                double safeDec = 80.0;
                Logger.Info($"CableWrapMonitor: Slewing to safe position RA {safeRA:F2}h Dec {safeDec:F0}°.");

                var safeTarget = new Coordinates(safeRA, safeDec, Epoch.JNOW, Coordinates.RAType.Hours);
                bool safeOk = await telescopeMediator.SlewToCoordinatesAsync(safeTarget, token);
                if (!safeOk)
                    throw new Exception(
                        "Could not slew to safe starting position (RA=LST, Dec=80°). " +
                        "Check the mount is connected and can point north.");

                await Task.Delay(500, token);
                info = telescopeMediator.GetInfo();
                double currentRA  = info.RightAscension;
                double currentDec = info.Declination;

                // ── Step 2: Unwind loop ────────────────────────────────────────────
                // The mediator push is suppressed, so the counter will NOT change on its
                // own. We decrement it manually after each slew so the display shows
                // the wrap count going down toward zero.
                //
                // Direction: to unwind positive wrap, we decrease RA (slew west).
                const int    maxSteps   = 20;
                const double maxStepDeg = 60.0;
                int    step      = 0;
                double remaining = startTotal;

                while (Math.Abs(remaining) >= 10.0 && step < maxSteps && !token.IsCancellationRequested) {
                    step++;

                    double stepDeg   = Math.Sign(remaining) * Math.Min(Math.Abs(remaining), maxStepDeg);
                    double stepHours = stepDeg / 15.0;
                    double targetRA  = ((currentRA - stepHours) % 24.0 + 24.0) % 24.0;

                    progress?.Report(new ApplicationStatus {
                        Status = $"Unwinding step {step}/{maxSteps}: {remaining:+0.0;-0.0}° remaining..."
                    });
                    Logger.Info($"CableWrapMonitor: Unwind step {step} — " +
                                $"RA {currentRA:F3}h → {targetRA:F3}h " +
                                $"({stepDeg:+0.0;-0.0}°, {remaining:+0.0;-0.0}° remaining).");
                    CwmLog($"[UNWIND-STEP {step}/{maxSteps}] {stepDeg:+0.0;-0.0}° — remaining={remaining:+0.0;-0.0}°");

                    var target = new Coordinates(targetRA, currentDec, Epoch.JNOW, Coordinates.RAType.Hours);
                    bool slewOk = await telescopeMediator.SlewToCoordinatesAsync(target, token);
                    if (!slewOk)
                        throw new Exception($"Slew failed during unwind step {step}. Please check the mount.");

                    await Task.Delay(500, token);
                    info      = telescopeMediator.GetInfo();
                    currentRA = info.RightAscension;

                    // Manually decrement the counter so the UI shows progress.
                    remaining -= stepDeg;
                    state.TotalDegreesRotated = remaining;
                    _totalDegreesRotated      = remaining;
                    RaisePropertyChanged(nameof(TotalDegreesRotated));
                    RaisePropertyChanged(nameof(WrapCount));
                }

                if (token.IsCancellationRequested) {
                    AppendHistory(state.TotalDegreesRotated, "Auto-unwind cancelled by user");
                    progress?.Report(new ApplicationStatus { Status = "Unwind cancelled." });
                    return;
                }

                // ── Step 3: Home the scope ─────────────────────────────────────────
                progress?.Report(new ApplicationStatus {
                    Status = $"Unwind steps done — homing scope to finish..."
                });
                Logger.Info("CableWrapMonitor: Unwind steps complete. Sending scope home.");

                await telescopeMediator.FindHome(progress, token);
                await Task.Delay(500, token);

                // ── Step 4: Reset counter ──────────────────────────────────────────
                Logger.Info("CableWrapMonitor: Auto-unwind complete. Resetting counter.");
                CwmLog("[UNWIND-DONE] Auto-unwind complete. Scope homed.");
                AppendHistory(state.TotalDegreesRotated, "Auto-unwind complete — scope homed, counter reset");
                Reset();
                progress?.Report(new ApplicationStatus {
                    Status = "Cable unwound. Scope homed. Counter reset to zero."
                });

            } catch (OperationCanceledException) {
                CwmLog($"[UNWIND-CANCEL] Cancelled at {TotalDegreesRotated:+0.0;-0.0}° remaining.");
                AppendHistory(state.TotalDegreesRotated, "Auto-unwind cancelled");
                progress?.Report(new ApplicationStatus { Status = "Unwind cancelled." });
            } catch (Exception ex) {
                CwmLog($"[UNWIND-FAIL] {ex.Message}");
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

        // ── Dedicated log file ────────────────────────────────────────────────────

        // Returns today's log path, refreshing the cached value on date rollover.
        private string LogPath {
            get {
                string today = DateTime.Now.ToString("yyyy-MM-dd");
                if (today != _logDate) {
                    _logDate = today;
                    _logPath = Path.Combine(DataDirectory, $"cwm-{today}.log");
                }
                return _logPath;
            }
        }

        private void CwmLog(string message) {
            try {
                string line = $"{DateTime.Now:HH:mm:ss} {message}";
                lock (_logLock)
                    File.AppendAllText(LogPath, line + Environment.NewLine);
            } catch { }
        }

        private void CleanupOldLogs() {
            try {
                DateTime cutoff = DateTime.Now.AddDays(-7);
                foreach (string file in Directory.GetFiles(DataDirectory, "cwm-*.log"))
                    if (File.GetLastWriteTime(file) < cutoff)
                        File.Delete(file);
            } catch { }
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
                telescopeMediator.RemoveConsumer(this);
                SaveState();
                CwmLog($"[STOP] Service stopped. Final total: {TotalDegreesRotated:+0.0;-0.0}°.");
                disposed = true;
            }
        }
    }
}
