using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Playnite.VRMods
{
    /// <summary>
    /// Registry that manages all registered VR mod data sources.
    /// Acts as a container and factory for source instances.
    ///
    /// Sources are registered once during plugin initialization (in VrModTagPlugin's constructor),
    /// then queried during scans to determine which sources to fetch.
    ///
    /// The registry uses ObservableCollection to support WPF data binding for the settings UI,
    /// which displays the list of available sources and their enabled state.
    /// </summary>
    public class VrModSourceRegistry
    {
        /// <summary>
        /// Internal collection of registered sources.
        /// Made private to prevent external code from bypassing the Register() method.
        /// Wrapped by a public read-only property that exposes only enumeration.
        /// </summary>
        private readonly ObservableCollection<IVrModSource> _sources = new();

        /// <summary>
        /// Read-only access to the list of registered sources.
        /// Used by the settings UI to display sources and by the service to iterate during scans.
        /// Note: This returns all sources regardless of enabled state.
        /// Use GetEnabledSources() to get only active sources.
        /// </summary>
        public ObservableCollection<IVrModSource> Sources => _sources;

        /// <summary>
        /// Registers a data source with the registry.
        /// Sources are typically registered during plugin initialization.
        ///
        /// Example: _registry.Register(new RototRobotVrModParser());
        /// </summary>
        /// <param name="source">The VR mod source to register.</param>
        public void Register(IVrModSource source)
        {
            _sources.Add(source);
        }

        /// <summary>
        /// Returns all sources that are currently enabled by the user.
        /// This is the method used during scans to determine which sources to fetch.
        ///
        /// Note: IsEnabled is set by the settings ViewModel's SyncSettingsToRegistry() method,
        /// which maps settings toggles (EnableRototRobot, EnableFlat2VR, EnableUeVR) to
        /// individual source instances.
        /// </summary>
        /// <returns>Enumerable of enabled sources. Returns empty if no sources are enabled.</returns>
        public IEnumerable<IVrModSource> GetEnabledSources()
        {
            return _sources.Where(s => s.IsEnabled);
        }
    }
}
