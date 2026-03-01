using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.ViewModel;
using OxyPlot;
using OxyPlot.Annotations;
using OxyPlot.Axes;
using OxyPlot.Series;

namespace CableWrapMonitor.Dockable {

    /// <summary>
    /// ViewModel for the Cable Wrap Monitor dockable panel.
    ///
    /// NINA discovers this class via MEF and adds it to the dockable panel list.
    /// The matching DataTemplate (in Resources.xaml) renders it as CableWrapDockable.xaml.
    ///
    /// This VM is intentionally thin — it delegates all logic to CableWrapService
    /// and exposes it directly to the XAML via the Service property.
    /// </summary>
    [Export(typeof(IDockableVM))]
    [PartCreationPolicy(CreationPolicy.Shared)]
    public class CableWrapDockableVM : DockableVM {

        public CableWrapService Service { get; }

        [ImportingConstructor]
        public CableWrapDockableVM(IProfileService profileService, CableWrapService service)
                : base(profileService) {

            Service = service;

            Title = "Cable Wrap Monitor";

            ResetCommand  = new RelayCommand(ExecuteReset);
            UnwindCommand = new AsyncRelayCommand(ExecuteUnwindAsync);

            // Rebuild charts whenever a new graph sample is recorded.
            service.PropertyChanged += (_, e) => {
                if (e.PropertyName == nameof(CableWrapService.GraphSamples))
                    Application.Current?.Dispatcher.Invoke(BuildCharts);
            };
            BuildCharts();
        }

        // ContentId and IsTool are read-only in NINA 3.2 — override as properties
        public override string ContentId => "CableWrapMonitor_Dockable";
        public override bool IsTool => true;

        public ICommand ResetCommand { get; }
        public IAsyncRelayCommand UnwindCommand { get; }

        // ── OxyPlot chart models ───────────────────────────────────────────────

        private PlotModel _timeSeriesModel = new PlotModel();
        public  PlotModel  TimeSeriesModel {
            get => _timeSeriesModel;
            private set { _timeSeriesModel = value; RaisePropertyChanged(); }
        }

        private PlotModel _spiralModel = new PlotModel();
        public  PlotModel  SpiralModel {
            get => _spiralModel;
            private set { _spiralModel = value; RaisePropertyChanged(); }
        }

        /// <summary>
        /// Rebuilds both chart models from the current sample list.
        /// Must be called on the UI thread.
        /// </summary>
        private void BuildCharts() {
            IReadOnlyList<RotationSample> samples = Service.GraphSamples;
            double thresh = Service.WarningThresholdDegrees;

            // ── Shared colours ───────────────────────────────────────────────
            var axisColor = OxyColor.FromArgb(180, 170, 170, 170);
            var gridColor = OxyColor.FromArgb(40,  255, 255, 255);
            var ninaBlue  = OxyColor.Parse("#4A90D9");
            var warnRed   = OxyColor.Parse("#FF4444");
            var faintGrey = OxyColor.FromArgb(60,  200, 200, 200);

            // ── Time-series model ────────────────────────────────────────────
            var ts = new PlotModel {
                Background          = OxyColors.Transparent,
                PlotAreaBorderColor = OxyColor.FromArgb(40, 255, 255, 255),
            };

            ts.Axes.Add(new DateTimeAxis {
                Position           = AxisPosition.Bottom,
                StringFormat       = "HH:mm",
                AxislineColor      = axisColor,
                TicklineColor      = axisColor,
                TextColor          = axisColor,
                MajorGridlineStyle = LineStyle.Dot,
                MajorGridlineColor = gridColor,
                MinorGridlineStyle = LineStyle.None,
            });
            ts.Axes.Add(new LinearAxis {
                Position           = AxisPosition.Left,
                Title              = "°",
                TitleColor         = axisColor,
                AxislineColor      = axisColor,
                TicklineColor      = axisColor,
                TextColor          = axisColor,
                MajorGridlineStyle = LineStyle.Dot,
                MajorGridlineColor = gridColor,
                MinorGridlineStyle = LineStyle.None,
            });

            // Home line at Y=0
            ts.Annotations.Add(new LineAnnotation {
                Type      = LineAnnotationType.Horizontal,
                Y         = 0,
                Color     = OxyColor.FromArgb(80, 200, 200, 200),
                LineStyle = LineStyle.Dash,
                Text      = "home",
                TextColor = axisColor,
                FontSize  = 9,
            });
            // Warning threshold lines
            ts.Annotations.Add(new LineAnnotation {
                Type      = LineAnnotationType.Horizontal,
                Y         = thresh,
                Color     = warnRed,
                LineStyle = LineStyle.Dash,
            });
            ts.Annotations.Add(new LineAnnotation {
                Type      = LineAnnotationType.Horizontal,
                Y         = -thresh,
                Color     = warnRed,
                LineStyle = LineStyle.Dash,
            });

            var line = new LineSeries {
                Color           = ninaBlue,
                StrokeThickness = 1.5,
                MarkerType      = MarkerType.None,
            };
            foreach (var s in samples)
                line.Points.Add(DateTimeAxis.CreateDataPoint(s.Timestamp, s.Degrees));
            ts.Series.Add(line);

            ts.InvalidatePlot(false);
            TimeSeriesModel = ts;

            // ── Spiral model ─────────────────────────────────────────────────
            double maxDeg = samples.Count > 0
                ? samples.Max(s => Math.Abs(s.Degrees))
                : 0;
            double maxR = Math.Max(1.2, maxDeg / 360.0 + 0.2);

            var sp = new PlotModel {
                Background          = OxyColors.Transparent,
                PlotAreaBorderColor = OxyColors.Transparent,
                IsLegendVisible     = false,
            };

            sp.Axes.Add(new LinearAxis {
                Position      = AxisPosition.Bottom,
                Minimum       = -maxR - 0.2,
                Maximum       =  maxR + 0.2,
                IsAxisVisible = false,
            });
            sp.Axes.Add(new LinearAxis {
                Position      = AxisPosition.Left,
                Minimum       = -maxR - 0.2,
                Maximum       =  maxR + 0.2,
                IsAxisVisible = false,
            });

            // Crosshairs
            sp.Annotations.Add(new LineAnnotation {
                Type      = LineAnnotationType.Horizontal,
                Y         = 0,
                Color     = OxyColor.FromArgb(40, 200, 200, 200),
                LineStyle = LineStyle.Solid,
            });
            sp.Annotations.Add(new LineAnnotation {
                Type      = LineAnnotationType.Vertical,
                X         = 0,
                Color     = OxyColor.FromArgb(40, 200, 200, 200),
                LineStyle = LineStyle.Solid,
            });

            // Wrap rings — one faint circle per full rotation
            int numRings = (int)Math.Ceiling(maxR);
            for (int i = 1; i <= numRings; i++) {
                var ring = new LineSeries {
                    Color           = faintGrey,
                    StrokeThickness = 0.5,
                    MarkerType      = MarkerType.None,
                };
                for (int j = 0; j <= 72; j++) {
                    double theta = j * 2.0 * Math.PI / 72;
                    ring.Points.Add(new DataPoint(i * Math.Cos(theta), i * Math.Sin(theta)));
                }
                sp.Series.Add(ring);
            }

            // Threshold ring (red dashed)
            double threshR = thresh / 360.0;
            var threshRing = new LineSeries {
                Color           = warnRed,
                StrokeThickness = 1.0,
                LineStyle       = LineStyle.Dash,
                MarkerType      = MarkerType.None,
            };
            for (int j = 0; j <= 72; j++) {
                double theta = j * 2.0 * Math.PI / 72;
                threshRing.Points.Add(new DataPoint(threshR * Math.Cos(theta), threshR * Math.Sin(theta)));
            }
            sp.Series.Add(threshRing);

            // Spiral path (cable wound up as seen end-on)
            var path = new LineSeries {
                Color           = ninaBlue,
                StrokeThickness = 1.5,
                MarkerType      = MarkerType.None,
            };
            foreach (var s in samples) {
                double theta = s.Degrees * Math.PI / 180.0;
                double r     = Math.Abs(s.Degrees) / 360.0;
                path.Points.Add(new DataPoint(r * Math.Cos(theta), r * Math.Sin(theta)));
            }
            sp.Series.Add(path);

            // Current position — filled circle dot
            if (samples.Count > 0) {
                var last  = samples[samples.Count - 1];
                double theta = last.Degrees * Math.PI / 180.0;
                double r     = Math.Abs(last.Degrees) / 360.0;
                var dot = new ScatterSeries {
                    MarkerType            = MarkerType.Circle,
                    MarkerSize            = 5,
                    MarkerFill            = ninaBlue,
                    MarkerStroke          = OxyColors.White,
                    MarkerStrokeThickness = 1,
                };
                dot.Points.Add(new ScatterPoint(r * Math.Cos(theta), r * Math.Sin(theta)));
                sp.Series.Add(dot);
            }

            sp.InvalidatePlot(false);
            SpiralModel = sp;
        }

        // ── Commands ──────────────────────────────────────────────────────────

        /// <summary>
        /// Bound to the "Reset — Cable Unwound" button in the panel.
        /// Shows a confirmation dialog before zeroing the accumulator.
        /// </summary>
        private void ExecuteReset() {
            var result = MessageBox.Show(
                "Have you physically unwound the USB cable?\n\n" +
                "Clicking Yes will reset the wrap counter to zero.\n" +
                "Only do this after the cable has actually been unwound.",
                "Reset — Cable Unwound",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.No);

            if (result == MessageBoxResult.Yes) {
                Service.Reset();
            }
        }

        private async Task ExecuteUnwindAsync() {
            try {
                await Service.UnwindAsync(null, CancellationToken.None);
            } catch (OperationCanceledException) {
                // cancelled — no message needed
            } catch (Exception ex) {
                MessageBox.Show(
                    $"Auto-unwind failed:\n\n{ex.Message}\n\n" +
                    "Please unwind the cable manually and click 'Reset — Cable Unwound'.",
                    "Unwind Failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
    }
}
