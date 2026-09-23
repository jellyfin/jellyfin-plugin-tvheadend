using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.LiveTv;
using Microsoft.Extensions.Logging;
using TVHeadEnd.Configuration;
using TVHeadEnd.DataHelper;
using TVHeadEnd.HTSP;

namespace TVHeadEnd
{
    public class HTSConnectionHandler : IHTSConnectionListener, IDisposable
    {
        /// <summary>
        /// DVR_PRIO_IMPORTANT - the lowest value TVHeadend accepts for a recording priority.
        /// </summary>
        private const int DvrPriorityImportant = 0;

        /// <summary>
        /// DVR_PRIO_NORMAL - the fallback used when the configured priority is out of range.
        /// </summary>
        private const int DvrPriorityNormal = 2;

        /// <summary>
        /// DVR_PRIO_NOTSET - leaves the priority to the TVHeadend DVR configuration.
        /// </summary>
        private const int DvrPriorityNotSet = 5;

        private readonly object _lock = new object();

        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger<HTSConnectionHandler> _logger;

        // Data helpers
        private readonly ChannelDataHelper _channelDataHelper;
        private readonly DvrDataHelper _dvrDataHelper;
        private readonly AutorecDataHelper _autorecDataHelper;

        private volatile bool _initialLoadFinished;
        private volatile bool _connected;

        // Jellyfin replaces the configuration instance on every save, so its identity says whether these are current.
        private volatile Settings? _settings;

        // Connection state, written under _lock.
        private HTSConnectionAsync? _htsConnection;
        private Settings? _connectedWith;
        private string _webRoot = string.Empty;

        private LiveTvService? _liveTvService;

        public HTSConnectionHandler(ILoggerFactory loggerFactory)
        {
            _loggerFactory = loggerFactory;
            _logger = loggerFactory.CreateLogger<HTSConnectionHandler>();

            // System.Diagnostics.StackTrace t = new System.Diagnostics.StackTrace();
            _logger.LogDebug("[TVHclient] HTSConnectionHandler");

            _channelDataHelper = new ChannelDataHelper(loggerFactory.CreateLogger<ChannelDataHelper>());
            _dvrDataHelper = new DvrDataHelper(loggerFactory.CreateLogger<DvrDataHelper>());
            _autorecDataHelper = new AutorecDataHelper(loggerFactory.CreateLogger<AutorecDataHelper>());

            // The channel type is applied once the configuration has been read.
            // ChannelDataHelper defaults to "Ignore" until then.
        }

        public void SetLiveTvService(LiveTvService liveTvService)
        {
            _liveTvService = liveTvService;
        }

        public LiveTvService? GetLiveTvService()
        {
            return _liveTvService;
        }

        public int WaitForInitialLoad(CancellationToken cancellationToken)
        {
            EnsureConnection();
            DateTime start = DateTime.Now;
            while (!_initialLoadFinished || cancellationToken.IsCancellationRequested)
            {
                Thread.Sleep(500);
                TimeSpan duration = DateTime.Now - start;
                long durationInSec = duration.Ticks / TimeSpan.TicksPerSecond;
                if (durationInSec > 60 * 15) // 15 Min timeout, should be enough to load huge data count
                {
                    return -1;
                }
            }

            return 0;
        }

        private Settings CurrentSettings()
        {
            var config = Plugin.Instance.Configuration;
            var settings = _settings;

            if (settings is not null && ReferenceEquals(settings.Source, config))
            {
                return settings;
            }

            settings = ReadSettings(config);
            _settings = settings;

            // The constructor runs before any configuration exists, so it is handed over here.
            _channelDataHelper.SetChannelType4Other(settings.ChannelType);

            return settings;
        }

        private Settings ReadSettings(PluginConfiguration config)
        {
            _logger.LogDebug("[TVHclient] HTSConnectionHandler: reading the configuration");

            if (string.IsNullOrEmpty(config.TVH_ServerName))
            {
                const string Message = "[TVHclient] HTSConnectionHandler.EnsureConnection: TVH server name must be configured";
                _logger.LogError(Message);
                throw new InvalidOperationException(Message);
            }

            if (string.IsNullOrEmpty(config.Username))
            {
                const string Message = "[TVHclient] HTSConnectionHandler.EnsureConnection: username must be configured";
                _logger.LogError(Message);
                throw new InvalidOperationException(Message);
            }

            if (string.IsNullOrEmpty(config.Password))
            {
                const string Message = "[TVHclient] HTSConnectionHandler.EnsureConnection: password must be configured";
                _logger.LogError(Message);
                throw new InvalidOperationException(Message);
            }

            var priority = config.Priority;

            if (priority < DvrPriorityImportant || priority > DvrPriorityNotSet)
            {
                priority = DvrPriorityNormal;
                _logger.LogWarning(
                    "[TVHclient] HTSConnectionHandler: priority {ConfiguredPriority} is out of range [{Lowest}-{Highest}] - using {Fallback} (normal)",
                    config.Priority,
                    DvrPriorityImportant,
                    DvrPriorityNotSet,
                    DvrPriorityNormal);
            }

            return new Settings
            {
                Source = config,
                ServerName = config.TVH_ServerName.Trim(),
                HttpPort = config.HTTP_Port,
                HtspPort = config.HTSP_Port,
                UserName = config.Username.Trim(),
                Password = config.Password.Trim(),
                Priority = priority,
                Profile = config.Profile.Trim(),
                ChannelType = config.ChannelType.Trim(),
                EnableSubsMaudios = config.EnableSubsMaudios,
                ForceDeinterlace = config.ForceDeinterlace,
            };
        }

        /// <summary>
        /// Trims a web root into the '' or '/prefix' form used when building URLs.
        /// </summary>
        /// <param name="webRoot">The raw web root.</param>
        /// <returns>The normalized web root.</returns>
        private static string NormalizeWebRoot(string? webRoot)
        {
            if (string.IsNullOrWhiteSpace(webRoot))
            {
                return string.Empty;
            }

            string trimmed = webRoot.Trim().TrimEnd('/');

            if (trimmed.Length == 0)
            {
                return string.Empty;
            }

            return trimmed.StartsWith('/') ? trimmed : "/" + trimmed;
        }

        /// <summary>
        /// Adopts the web root TVHeadend reported during the handshake.
        /// </summary>
        /// <remarks>
        /// The server knows its own path prefix, so it is the only source for this value; an
        /// absent field means TVHeadend is served from the root.
        /// </remarks>
        /// <param name="reportedWebRoot">The web root from the hello response.</param>
        private void ApplyServerWebRoot(string? reportedWebRoot)
        {
            string resolved = NormalizeWebRoot(reportedWebRoot);

            if (string.Equals(resolved, _webRoot, StringComparison.Ordinal))
            {
                return;
            }

            _logger.LogInformation(
                "[TVHclient] HTSConnectionHandler: TVHeadend reported web root '{ReportedWebRoot}'",
                resolved);

            _webRoot = resolved;
        }

        /// <summary>
        /// Builds the TVHeadend HTTP base URL from the current settings.
        /// </summary>
        /// <returns>The HTTP base URL.</returns>
        private string BuildHttpBaseUrl(Settings settings)
        {
            if (settings.EnableSubsMaudios)
            {
                // Use HTTP basic auth instead of TVH ticketing system for authentication to allow the users to switch subs or audio tracks at any time
                return "http://" + settings.UserName + ":" + settings.Password + "@" + settings.ServerName + ":" + settings.HttpPort + _webRoot;
            }

            return "http://" + settings.ServerName + ":" + settings.HttpPort + _webRoot;
        }

        /// <summary>
        /// Turns an image reference from an HTSP message into an absolute URL.
        /// </summary>
        /// <remarks>
        /// TVHeadend's imagecache references are version dependent: below the per-field
        /// threshold the server sends an absolute <c>http://</c> URL, between HTSP v8 and v14
        /// a root-relative <c>/imagecache/N</c> path, and from v15 on a relative
        /// <c>imagecache/N</c> path. EPG providers may also supply an absolute URL directly.
        /// Anything that is not already absolute is resolved against the configured TVHeadend
        /// HTTP endpoint, so every negotiated protocol version yields a usable URL.
        /// </remarks>
        /// <param name="image">The raw image value from an HTSP message.</param>
        /// <returns>An absolute URL, or <c>null</c> when no image was supplied.</returns>
        public string? ResolveImageUrl(string? image)
        {
            if (string.IsNullOrEmpty(image))
            {
                return null;
            }

            if (image.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || image.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return image;
            }

            return GetAuthenticatedUrl(image);
        }

        /// <summary>
        /// Builds an absolute, credentialed URL for a resource served by TVHeadend over HTTP.
        /// </summary>
        /// <remarks>
        /// The web root is the one reported by the server, so a connection is established first.
        /// </remarks>
        /// <param name="relativePath">The path below the web root, with or without a leading slash.</param>
        /// <returns>An absolute URL including the configured credentials.</returns>
        public string GetAuthenticatedUrl(string relativePath)
        {
            EnsureConnection();

            var settings = CurrentSettings();

            return "http://" + settings.UserName + ":" + settings.Password + "@" + settings.ServerName + ":" + settings.HttpPort + _webRoot
                + "/" + relativePath.TrimStart('/');
        }

        public string? GetChannelImageUrl(string channelId)
        {
            _logger.LogDebug("[TVHclient] HTSConnectionHandler.GetChannelImage: channelId: {Id}", channelId);

            return ResolveImageUrl(_channelDataHelper.GetChannelIcon4ChannelId(channelId));
        }

        public Dictionary<string, string> GetHeaders()
        {
            var settings = CurrentSettings();
            var authInfo = Convert.ToBase64String(Encoding.Default.GetBytes(settings.UserName + ":" + settings.Password));

            return new Dictionary<string, string> { { "Authorization", "Basic " + authInfo } };
        }

        // private static Stream ImageToPNGStream(Image image)
        // {
        //    Stream stream = new System.IO.MemoryStream();
        //    image.Save(stream, ImageFormat.Png);
        //    stream.Position = 0;
        //    return stream;
        // }

        private void EnsureConnection()
        {
            var settings = CurrentSettings();

            lock (_lock)
            {
                // A connection opened with settings that no longer apply has to go.
                if (_htsConnection is not null
                    && (_htsConnection.NeedsRestart() || !DescribesSameEndpoint(_connectedWith, settings)))
                {
                    _logger.LogInformation("[TVHclient] HTSConnectionHandler: dropping the connection, it no longer matches the settings");
                    _htsConnection.Stop();
                    _htsConnection.Dispose();
                    _htsConnection = null;
                    _connected = false;

                    // The helpers hold data from that connection, so callers must wait for the new one.
                    _initialLoadFinished = false;
                }

                if (_htsConnection is null)
                {
                    _logger.LogDebug("[TVHclient] HTSConnectionHandler.ensureConnection: create new HTS connection");
                    // "clientversion" is the client's own version, not the protocol version -
                    // TVHeadend only reports it, but sending the HTSP number here was misleading.
                    Version? version = typeof(HTSConnectionHandler).Assembly.GetName().Version;
                    _htsConnection = new HTSConnectionAsync(
                        this,
                        "Jellyfin-TVHeadend",
                        version?.ToString() ?? "unknown",
                        _loggerFactory);
                    _connected = false;
                }

                if (!_connected)
                {
                    _logger.LogDebug(
                        "[TVHclient] HTSConnectionHandler.ensureConnection: used connection parameters: " +
                        "TVH Server = '{Servername}'; HTTP Port = '{Httpport}'; HTSP Port = '{Htspport}'; Web-Root = '{Webroot}'; " +
                        "User = '{User}'; Password set = '{Passexists}'",
                        settings.ServerName,
                        settings.HttpPort,
                        settings.HtspPort,
                        _webRoot,
                        settings.UserName,
                        settings.Password.Length > 0);

                    _htsConnection.Open(settings.ServerName, settings.HtspPort);
                    _connected = _htsConnection.Authenticate(settings.UserName, settings.Password);
                    _connectedWith = settings;

                    if (_connected)
                    {
                        ApplyServerWebRoot(_htsConnection.GetWebRoot());
                    }

                    _logger.LogInformation(
                        "[TVHclient] HTSConnectionHandler.EnsureConnection: connection established = {Connected}; "
                        + "TVH server = '{ServerName}' {ServerVersion}; HTSP version negotiated = {NegotiatedHtspVersion} "
                        + "(server supports up to {ServerHtspVersion}, client up to {ClientHtspVersion})",
                        _connected,
                        _htsConnection.GetServername(),
                        _htsConnection.GetServerversion(),
                        _htsConnection.GetNegotiatedProtocolVersion(),
                        _htsConnection.GetServerProtocolVersion(),
                        HTSMessage.HtspVersion);
                }
            }
        }

        private static bool DescribesSameEndpoint(Settings? left, Settings? right)
        {
            if (left is null || right is null)
            {
                return false;
            }

            return string.Equals(left.ServerName, right.ServerName, StringComparison.Ordinal)
                && left.HttpPort == right.HttpPort
                && left.HtspPort == right.HtspPort
                && string.Equals(left.UserName, right.UserName, StringComparison.Ordinal)
                && string.Equals(left.Password, right.Password, StringComparison.Ordinal);
        }

        public void SendMessage(HTSMessage message, IHTSResponseHandler responseHandler)
        {
            EnsureConnection();
            _htsConnection!.SendMessage(message, responseHandler);
        }

        /// <summary>
        /// Gets the HTSP version in effect for the current connection.
        /// </summary>
        /// <returns>The negotiated HTSP version.</returns>
        public int GetNegotiatedProtocolVersion()
        {
            EnsureConnection();
            return _htsConnection!.GetNegotiatedProtocolVersion();
        }

        public Task<IEnumerable<ChannelInfo>> BuildChannelInfos(CancellationToken cancellationToken)
        {
            return _channelDataHelper.BuildChannelInfos(cancellationToken);
        }

        public int GetPriority()
        {
            return CurrentSettings().Priority;
        }

        public string GetProfile()
        {
            return CurrentSettings().Profile;
        }

        public string GetHttpBaseUrl()
        {
            // The web root is taken from the HTSP handshake, so a connection is required
            // before the base URL is known to be correct.
            EnsureConnection();
            return BuildHttpBaseUrl(CurrentSettings());
        }

        public bool GetEnableSubsMaudios()
        {
            return CurrentSettings().EnableSubsMaudios;
        }

        public bool GetForceDeinterlace()
        {
            return CurrentSettings().ForceDeinterlace;
        }

        public Task<IEnumerable<MyRecordingInfo>> BuildDvrInfos(CancellationToken cancellationToken)
        {
            return _dvrDataHelper.BuildDvrInfos(cancellationToken);
        }

        public Task<IEnumerable<SeriesTimerInfo>> BuildAutorecInfos(CancellationToken cancellationToken)
        {
            return _autorecDataHelper.BuildAutorecInfos(cancellationToken);
        }

        public Task<IEnumerable<TimerInfo>> BuildPendingTimersInfos(CancellationToken cancellationToken)
        {
            return _dvrDataHelper.BuildPendingTimersInfos(cancellationToken);
        }

        public void OnError(Exception ex)
        {
            // The failed connection stopped itself, so EnsureConnection rebuilds on the next call.
            _logger.LogError(ex, "[TVHclient] HTSConnectionHandler: HTSP error");
        }

        public void OnMessage(HTSMessage? response)
        {
            if (response != null)
            {
                switch (response.Method)
                {
                    case "tagAdd":
                    case "tagUpdate":
                    case "tagDelete":
                        // _logger.LogCritical("[TVHclient] tad add/update/delete {Resp}", response.ToString());
                        break;

                    case "channelAdd":
                    case "channelUpdate":
                        _channelDataHelper.Add(response);
                        break;

                    case "dvrEntryAdd":
                        _dvrDataHelper.DvrEntryAdd(response);
                        break;
                    case "dvrEntryUpdate":
                        _dvrDataHelper.DvrEntryUpdate(response);
                        break;
                    case "dvrEntryDelete":
                        _dvrDataHelper.DvrEntryDelete(response);
                        break;

                    case "autorecEntryAdd":
                        _autorecDataHelper.AutorecEntryAdd(response);
                        break;
                    case "autorecEntryUpdate":
                        _autorecDataHelper.AutorecEntryUpdate(response);
                        break;
                    case "autorecEntryDelete":
                        _autorecDataHelper.AutorecEntryDelete(response);
                        break;

                    case "eventAdd":
                    case "eventUpdate":
                    case "eventDelete":
                        // should not happen as we don't subscribe for this events.
                        break;

                    // case "subscriptionStart":
                    // case "subscriptionGrace":
                    // case "subscriptionStop":
                    // case "subscriptionSkip":
                    // case "subscriptionSpeed":
                    // case "subscriptionStatus":
                    //    _logger.LogCritical("[TVHclient] subscription events {Resp}", response.ToString());
                    //    break;

                    // case "queueStatus":
                    //    _logger.LogCritical("[TVHclient] queueStatus event {Resp}", response.ToString());
                    //    break;

                    // case "signalStatus":
                    //    _logger.LogCritical("[TVHclient] signalStatus event {Resp}", response.ToString());
                    //    break;

                    // case "timeshiftStatus":
                    //    _logger.LogCritical("[TVHclient] timeshiftStatus event {Resp}", response.ToString());
                    //    break;

                    // case "muxpkt": // streaming data
                    //    _logger.LogCritical("[TVHclient] muxpkt event {Resp}", response.ToString());
                    //    break;

                    case "initialSyncCompleted":
                        _initialLoadFinished = true;
                        break;

                    default:
                        // _logger.LogCritical("[TVHclient] Method '{Method}' not handled in LiveTvService.cs", response.Method);
                        break;
                }
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Releases the HTSP connection held by this handler.
        /// </summary>
        /// <param name="disposing"><c>true</c> to release managed resources.</param>
        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                _htsConnection?.Dispose();
                _htsConnection = null;
            }
        }

        private sealed class Settings
        {
            // The configuration instance these values were read from.
            public required object Source { get; init; }

            public required string ServerName { get; init; }

            public required int HttpPort { get; init; }

            public required int HtspPort { get; init; }

            public required string UserName { get; init; }

            public required string Password { get; init; }

            public required int Priority { get; init; }

            public required string Profile { get; init; }

            public required string ChannelType { get; init; }

            public required bool EnableSubsMaudios { get; init; }

            public required bool ForceDeinterlace { get; init; }
        }
    }
}
