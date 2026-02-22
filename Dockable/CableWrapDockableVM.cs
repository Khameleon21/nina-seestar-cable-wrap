using System.ComponentModel.Composition;
using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.ViewModel;

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

            ResetCommand = new RelayCommand(ExecuteReset);
        }

        /// <summary>
        /// Bound to the "Reset — Cable Unwound" button in the panel.
        /// Shows a confirmation dialog before zeroing the accumulator.
        /// </summary>
        // ContentId and IsTool are read-only in NINA 3.2 — override as properties
        public override string ContentId => "CableWrapMonitor_Dockable";
        public override bool IsTool => true;

        public ICommand ResetCommand { get; }

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
    }
}
