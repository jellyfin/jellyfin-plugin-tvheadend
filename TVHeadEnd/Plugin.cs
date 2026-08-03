using System;
using System.Collections.Generic;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using TVHeadEnd.Configuration;

namespace TVHeadEnd
{
    /// <summary>
    /// Class Plugin.
    /// </summary>
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
    {
        private static readonly Guid _pluginId = new Guid("3fd018e5-5e78-4e58-b280-a0c068febee0");

        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
        }

        /// <summary>
        /// Gets the instance.
        /// </summary>
        /// <value>The instance.</value>
        public static Plugin Instance { get; private set; } = null!;

        /// <summary>
        /// Gets the name of the plugin.
        /// </summary>
        /// <value>The name.</value>
        public override string Name => "TVHeadend";

        /// <summary>
        /// Gets the description.
        /// </summary>
        /// <value>The description.</value>
        public override string Description => "Provides live TV using TVHeadend as the source.";

        /// <inheritdoc />
        public override Guid Id => _pluginId;

        public IEnumerable<PluginPageInfo> GetPages()
        {
            return new[]
            {
                new PluginPageInfo
                {
                    Name = "tvheadend",
                    EmbeddedResourcePath = GetType().Namespace + ".Web.tvheadend.html",
                },
                new PluginPageInfo
                {
                    Name = "tvheadendjs",
                    EmbeddedResourcePath = GetType().Namespace + ".Web.tvheadend.js"
                }
            };
        }
    }
}
