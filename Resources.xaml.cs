using System.ComponentModel.Composition;
using System.Windows;

namespace CableWrapMonitor {

    /// <summary>
    /// Code-behind for Resources.xaml.
    ///
    /// The [Export(typeof(ResourceDictionary))] attribute tells NINA's MEF container to
    /// merge this dictionary into the application's global resources. That makes the
    /// DataTemplates and icons defined in Resources.xaml available throughout NINA,
    /// including the docking manager and sequencer panel.
    /// </summary>
    [Export(typeof(ResourceDictionary))]
    public partial class Resources : ResourceDictionary {
        public Resources() {
            InitializeComponent();
        }
    }
}
