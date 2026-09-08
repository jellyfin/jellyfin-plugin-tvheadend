using System.Diagnostics.CodeAnalysis;
using MediaBrowser.Model.Plugins;

namespace TVHeadEnd.Configuration
{
    /// <summary>
    /// Class PluginConfiguration.
    /// </summary>
    [SuppressMessage(
        "Naming",
        "CA1707:Identifiers should not contain underscores",
        Justification = "These property names are the element names Jellyfin persists this configuration under. Renaming them would silently reset every existing user's settings on upgrade, and would also break Web/tvheadend.js.")]
    public class PluginConfiguration : BasePluginConfiguration
    {
        public PluginConfiguration()
        {
            TVH_ServerName = "localhost";
            HTTP_Port = 9981;
            HTSP_Port = 9982;
            WebRoot = "/";
            Username = string.Empty;
            Password = string.Empty;
            Priority = 5;
            Profile = string.Empty;
            Pre_Padding = 0;
            Post_Padding = 0;
            ChannelType = "Ignore";
            HideRecordingsChannel = false;
            EnableSubsMaudios = false;
            ForceDeinterlace = false;
        }

        public string TVH_ServerName { get; set; }

        public int HTTP_Port { get; set; }

        public int HTSP_Port { get; set; }

        public string WebRoot { get; set; }

        public string Username { get; set; }

        public string Password { get; set; }

        public int Priority { get; set; }

        public string Profile { get; set; }

        public int Pre_Padding { get; set; }

        public int Post_Padding { get; set; }

        public string ChannelType { get; set; }

        public bool HideRecordingsChannel { get; set; }

        public bool EnableSubsMaudios { get; set; }

        public bool ForceDeinterlace { get; set; }
    }
}
