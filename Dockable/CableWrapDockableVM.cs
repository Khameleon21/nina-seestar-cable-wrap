using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
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

            // ── Arc model (cable position on unit circle) ─────────────────────
            // Home = 9 o'clock (−1, 0). CW = positive total, arc grows CW.
            // Each 360° lap gets a new colour drawn on top of the previous.
            double total    = Service.TotalDegreesRotated;
            double absTotal = Math.Abs(total);
            double sign     = total >= 0 ? 1.0 : -1.0;

            var lapColors = new OxyColor[] {
                ninaBlue,                      // Wrap 1: blue
                OxyColor.Parse("#FFD700"),      // Wrap 2: gold
                OxyColor.Parse("#FF8C00"),      // Wrap 3: orange
                warnRed,                        // Wrap 4+: red
            };

            var sp = new PlotModel {
                Background          = OxyColors.Transparent,
                PlotAreaBorderColor = OxyColors.Transparent,
                IsLegendVisible     = false,
            };

            sp.Axes.Add(new LinearAxis {
                Position      = AxisPosition.Bottom,
                Minimum       = -1.3,
                Maximum       =  1.3,
                IsAxisVisible = false,
            });
            sp.Axes.Add(new LinearAxis {
                Position      = AxisPosition.Left,
                Minimum       = -1.3,
                Maximum       =  1.3,
                IsAxisVisible = false,
            });

            // Background track ring
            var track = new LineSeries {
                Color           = faintGrey,
                StrokeThickness = 1.5,
                MarkerType      = MarkerType.None,
            };
            for (int j = 0; j <= 360; j++) {
                double a = j * Math.PI / 180.0;
                track.Points.Add(new DataPoint(Math.Cos(a), Math.Sin(a)));
            }
            sp.Series.Add(track);

            // Home marker at 9 o'clock (−1, 0)
            var homeDot = new ScatterSeries {
                MarkerType            = MarkerType.Circle,
                MarkerSize            = 5,
                MarkerFill            = OxyColor.FromArgb(150, 200, 200, 200),
                MarkerStroke          = OxyColors.Transparent,
                MarkerStrokeThickness = 0,
            };
            homeDot.Points.Add(new ScatterPoint(-1.0, 0.0));
            sp.Series.Add(homeDot);

            if (absTotal >= 0.5) {
                double homeAngle    = Math.PI;
                double totalRad     = total * Math.PI / 180.0;
                double currentAngle = homeAngle - totalRad;

                // Draw each 360° lap as a separate untitled LineSeries (no legend
                // entry — the ghost series above already define the colour key).
                double drawnDeg = 0.0;
                int    lapIdx   = 0;
                while (absTotal - drawnDeg >= 0.5 && lapIdx < lapColors.Length * 2) {
                    double lapDeg   = Math.Min(absTotal - drawnDeg, 360.0);
                    int    numPts   = (int)Math.Ceiling(lapDeg);
                    double startRad = drawnDeg * Math.PI / 180.0;
                    double lapRad   = lapDeg   * Math.PI / 180.0;
                    OxyColor lc     = lapColors[Math.Min(lapIdx, lapColors.Length - 1)];

                    var lapSeries = new LineSeries {
                        Color           = lc,
                        StrokeThickness = 3.5,
                        MarkerType      = MarkerType.None,
                    };
                    for (int i = 0; i <= numPts; i++) {
                        double t     = (double)i / numPts;
                        double angle = homeAngle - sign * (startRad + lapRad * t);
                        lapSeries.Points.Add(new DataPoint(Math.Cos(angle), Math.Sin(angle)));
                    }
                    sp.Series.Add(lapSeries);
                    drawnDeg += lapDeg;
                    lapIdx++;
                }

                // Current position dot — colour of the last lap drawn
                OxyColor dotColor = lapColors[Math.Min(lapIdx - 1, lapColors.Length - 1)];
                var dot = new ScatterSeries {
                    MarkerType            = MarkerType.Circle,
                    MarkerSize            = 7,
                    MarkerFill            = dotColor,
                    MarkerStroke          = OxyColors.White,
                    MarkerStrokeThickness = 1.5,
                };
                dot.Points.Add(new ScatterPoint(Math.Cos(currentAngle), Math.Sin(currentAngle)));
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
