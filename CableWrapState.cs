using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace CableWrapMonitor {

    /// <summary>
    /// All persistent state written to disk on every poll cycle.
    /// Loaded on startup so the plugin resumes seamlessly after a NINA restart.
    /// </summary>
    public class CableWrapState {

        /// <summary>
        /// Running total of RA rotation in degrees.
        /// Positive = clockwise (cable winding up), negative = counter-clockwise (unwinding).
        /// </summary>
        [JsonProperty]
        public double TotalDegreesRotated { get; set; } = 0.0;

        /// <summary>
        /// The telescope azimuth (in degrees, 0–360) read during the previous poll cycle.
        /// Null means we haven't established a baseline yet.
        /// </summary>
        [JsonProperty]
        public double? LastKnownAzimuth { get; set; } = null;

        /// <summary>
        /// The UTC time at which the user last reset the counter.
        /// </summary>
        [JsonProperty]
        public DateTime ZeroSetTimestamp { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// The highest integer wrap count (floor(|totalDeg| / 360)) that has already been
        /// logged. Prevents the same crossing being added to history multiple times.
        /// </summary>
        [JsonProperty]
        public int LastLoggedWrapCount { get; set; } = 0;

        /// <summary>
        /// True once the warning notification has been shown for the current threshold
        /// crossing. Cleared when the user resets the counter, so it fires again next time.
        /// </summary>
        [JsonProperty]
        public bool AlertFired { get; set; } = false;

        /// <summary>
        /// Timestamped log of each 360° crossing and each manual reset.
        /// </summary>
        [JsonProperty]
        public List<WrapHistoryEntry> WrapHistory { get; set; } = new List<WrapHistoryEntry>();
    }

    /// <summary>
    /// One line in the wrap history list shown in the panel.
    /// </summary>
    public class WrapHistoryEntry {

        [JsonProperty]
        public DateTime Timestamp { get; set; }

        [JsonProperty]
        public double CumulativeDegrees { get; set; }

        /// <summary>
        /// Human-readable description, e.g. "Crossed +360°" or "Manual reset".
        /// </summary>
        [JsonProperty]
        public string Note { get; set; } = string.Empty;
    }

    /// <summary>
    /// User-configurable settings. Stored in a separate JSON file so they survive
    /// a full state reset without being lost.
    /// </summary>
    public class CableWrapSettings {

        /// <summary>
        /// Number of full 360° rotations before the warning fires.
        /// Allowed range: 0.5 to 3.0 rotations.
        /// </summary>
        [JsonProperty]
        public double WarningThresholdRotations { get; set; } = 1.0;

    }
}
