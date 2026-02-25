using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using NINA.Core.Model;
using NINA.Core.Utility;   // Logger
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.Validations;

namespace CableWrapMonitor.SequenceItems {

    /// <summary>
    /// A NINA sequence instruction that checks the cable wrap status at the point
    /// in the sequence where it is inserted.
    ///
    /// If the accumulated rotation exceeds the warning threshold, the instruction
    /// fails with a clear error message, which halts the sequence (or triggers
    /// whatever error-handling strategy the user has configured in the sequencer).
    ///
    /// Recommended usage: insert this instruction between imaging targets, or at
    /// the start of each sequence, as a safety gate.
    /// </summary>
    [Export(typeof(ISequenceItem))]
    [ExportMetadata("Name",        "Check Cable Wrap")]
    [ExportMetadata("Description", "Fails the sequence if the Seestar USB cable has " +
                                   "wound beyond the configured warning threshold.")]
    [ExportMetadata("Icon",        "CableWrapSequenceIcon")]
    [ExportMetadata("Category",    "Cable Wrap Monitor")]
    [JsonObject(MemberSerialization.OptIn)]
    public class CheckCableWrapInstruction : SequenceItem, IValidatable {

        private readonly CableWrapService service;

        [ImportingConstructor]
        public CheckCableWrapInstruction(CableWrapService service) {
            this.service = service;
            Name = "Check Cable Wrap";
        }

        /// <summary>
        /// Copy constructor used by NINA's sequencer when cloning instructions.
        /// </summary>
        public CheckCableWrapInstruction(CheckCableWrapInstruction other) : base(other) {
            service = other.service;
        }

        public override object Clone() => new CheckCableWrapInstruction(this);

        // ── Properties exposed to the sequence item view (CheckCableWrapInstructionView.xaml) ──

        /// <summary>
        /// Current warning threshold, shown in the sequencer row for this instruction.
        /// </summary>
        public double WarningThresholdRotations => service.WarningThresholdRotations;

        /// <summary>
        /// Formatted string of current total rotation, shown next to the threshold.
        /// </summary>
        public string TotalDegreesRotatedDisplay {
            get {
                double deg = service.TotalDegreesRotated;
                return $"{deg:+0.1°;-0.1°;0.0°}";
            }
        }

        // ── Execution ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Called by the NINA sequencer when this instruction's turn comes.
        ///
        /// If the telescope is not connected, or the wrap is within limits, the
        /// instruction succeeds silently. If the wrap is over the limit it throws,
        /// which fails this instruction and stops the sequence.
        /// </summary>
        public override async Task Execute(
                IProgress<ApplicationStatus> progress,
                CancellationToken token) {

            progress?.Report(new ApplicationStatus { Status = "Checking cable wrap..." });
            await Task.Delay(100, token); // yield to keep the UI responsive

            double totalDeg  = service.TotalDegreesRotated;
            double threshold = service.WarningThresholdDegrees;
            double absTotal  = Math.Abs(totalDeg);

            if (absTotal >= threshold) {
                double wraps = absTotal / 360.0;
                string msg =
                    $"Cable Wrap Check FAILED: accumulated rotation is " +
                    $"{totalDeg:+0.1;-0.1}° ({wraps:F2} wraps), which exceeds " +
                    $"the {threshold:F0}° ({service.WarningThresholdRotations:F1} wrap) threshold. " +
                    "Please unwind the USB cable and click 'Reset — Cable Unwound' " +
                    "in the Cable Wrap Monitor panel before continuing.";

                Logger.Error($"CableWrapMonitor (sequence): {msg}");

                // Throwing any exception causes NINA to mark this item as Failed and
                // halt the sequence (or trigger the user's configured error handler).
                throw new Exception(msg);
            }

            // Within limits — report success and continue
            progress?.Report(new ApplicationStatus {
                Status = $"Cable wrap OK ({totalDeg:+0.1;-0.1}° / {threshold:F0}° limit)"
            });
        }

        // ── Validation (IValidatable) ─────────────────────────────────────────────

        /// <summary>
        /// Validate is called by NINA before the sequence runs.
        /// Returning true always allows the sequence to run; Execute() handles the real logic.
        /// </summary>
        public bool Validate() {
            var issues = new List<string>();

            if (service.TrackingStatus == TrackingStatus.NotConnected) {
                issues.Add("Telescope not connected — cable wrap check will be skipped.");
            }

            if (issues != Issues) {
                Issues = issues;
                RaisePropertyChanged(nameof(Issues));
            }

            return true; // Always allow the sequence to run; Execute() handles the real logic.
        }

        public IList<string> Issues { get; set; } = new List<string>();
    }
}

