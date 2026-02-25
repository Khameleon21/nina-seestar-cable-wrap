using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.Validations;

namespace CableWrapMonitor.SequenceItems {

    /// <summary>
    /// A NINA sequence instruction that automatically unwinds the cable by slewing
    /// the mount in the opposite direction of the accumulated wrap, then resets the
    /// accumulator to zero so the sequence can continue.
    ///
    /// Recommended usage: insert this instruction when the cable wrap check fails,
    /// or at the start of a recovery sequence after a cable wrap alert.
    /// </summary>
    [Export(typeof(ISequenceItem))]
    [ExportMetadata("Name",        "Unwind Cable")]
    [ExportMetadata("Description", "Slews the Seestar in reverse to physically unwind the USB cable, " +
                                   "then resets the wrap counter to zero.")]
    [ExportMetadata("Icon",        "UnwindCableSequenceIcon")]
    [ExportMetadata("Category",    "Cable Wrap Monitor")]
    [JsonObject(MemberSerialization.OptIn)]
    public class UnwindCableInstruction : SequenceItem, IValidatable {

        private static CableWrapService? Service => CableWrapService.Instance;

        [ImportingConstructor]
        public UnwindCableInstruction() {
            Name = "Unwind Cable";
        }

        public UnwindCableInstruction(UnwindCableInstruction other) : base(other) { }

        public override object Clone() => new UnwindCableInstruction(this);

        // ── Properties shown in the sequencer row ─────────────────────────────────

        public string TotalDegreesRotatedDisplay {
            get {
                double deg = Service?.TotalDegreesRotated ?? 0.0;
                return $"{deg:+0.0;-0.0;0.0}°";
            }
        }

        // ── Execution ─────────────────────────────────────────────────────────────

        public override async Task Execute(
                IProgress<ApplicationStatus> progress,
                CancellationToken token) {

            progress?.Report(new ApplicationStatus { Status = "Starting cable unwind..." });

            if (Service == null) {
                progress?.Report(new ApplicationStatus { Status = "Cable wrap: service not running, skipping." });
                return;
            }

            double totalDeg = Service.TotalDegreesRotated;

            if (Math.Abs(totalDeg) < 10.0) {
                // Already near zero
                progress?.Report(new ApplicationStatus {
                    Status = $"Cable wrap near zero ({totalDeg:+0.0;-0.0}°) — nothing to unwind."
                });
                return;
            }

            Logger.Info($"CableWrapMonitor (sequence): Starting auto-unwind from {totalDeg:+0.0;-0.0}°.");
            await Service.UnwindAsync(progress, token);
        }

        // ── Validation (IValidatable) ─────────────────────────────────────────────

        public IList<string> Issues { get; set; } = new List<string>();

        public bool Validate() {
            var issues = new List<string>();

            if (Service == null) {
                issues.Add("Cable Wrap Monitor service is not running.");
            } else if (Service.TrackingStatus == TrackingStatus.NotConnected) {
                issues.Add("Telescope not connected — unwind will be skipped.");
            }

            if (issues != Issues) {
                Issues = issues;
                RaisePropertyChanged(nameof(Issues));
            }

            return true;
        }
    }
}
