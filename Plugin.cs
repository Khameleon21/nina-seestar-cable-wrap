using System.ComponentModel.Composition;
using NINA.Plugin;
using NINA.Plugin.Interfaces;

namespace CableWrapMonitor {

    /// <summary>
    /// Main plugin registration class. NINA discovers this via MEF (Managed Extensibility
    /// Framework). All metadata (name, version, author, etc.) is read automatically from
    /// Properties/AssemblyInfo.cs — no need to set anything here manually.
    /// </summary>
    [Export(typeof(IPluginManifest))]
    public class CableWrapMonitorPlugin : PluginBase {

        [ImportingConstructor]
        public CableWrapMonitorPlugin() {
            // Nothing to do here. The CableWrapService is a separate MEF export and is
            // constructed when it is first imported by the dockable panel ViewModel.
        }
    }
}
