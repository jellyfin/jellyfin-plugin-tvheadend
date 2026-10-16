using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.LiveTv;
using Microsoft.Extensions.Logging;
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

        private const int MaxRetryDelaySeconds = 60;

        // Give up a single connection attempt after this time (an unreachable
        // host would otherwise block for the full kernel TCP timeout).
        private static readonly TimeSpan _connectTimeout = TimeSpan.FromSeconds(10);

        // How long callers may wait for the initial sync while a connection
        // attempt is in flight or the initial data is still being received.
        // When the server is known to be unreachable callers fail immediately
        // and never wait.
        private static readonly TimeSpan _initialLoadTimeout = TimeSpan.FromMinutes(5);

        private static readonly TimeSpan _syncProgressTimeout = TimeSpan.FromSeconds(20);

        private readonly object _lock = new object();
        private readonly CancellationTokenSource _connectionLoopCts = new CancellationTokenSource();

        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger<HTSConnectionHandler> _logger;

        // Data helpers
        private readonly ChannelDataHelper _channelDataHelper;
        private readonly DvrDataHelper _dvrDataHelper;
        private readonly AutorecDataHelper _autorecDataHelper;

        private readonly Dictionary<string, string> _headers = new Dictionary<string, string>();

        private bool _initialLoadFinished;
        private bool _connected;
        private bool _configured;
        // True while there is no connection attempt callers should wait for: only the
        // first attempt after startup and the first one after a working session dropped
        // are awaited, retries of a failed attempt fail fast.
        private bool _firstConnectAttemptCompleted;

        // Set when TVHeadend rejected the credentials; cleared once the connection
        // settings that were rejected have been changed in the configuration.
        private bool _authenticationFailed;
        private string? _rejectedConfiguration;

        private Task? _connectionTask;
        private bool _disposed;
        private bool _initialSyncReceived;

        // The current session completed the initial sync at some point; decides whether a
        // drop is followed by an immediate, awaited reconnect or by the backoff.
        private bool _sessionSynced;
        private long _lastSyncProgress;

        [SuppressMessage("Usage", "CA2213:Disposable fields should be disposed", Justification = "ConnectionLoop owns and disposes each attempt in its finally block; Dispose stops and joins that loop.")]
        private HTSConnectionAsync? _htsConnection;
        private int _priority;
        private string _profile = string.Empty;
        private string _httpBaseUrl = string.Empty;
        private string _channelType = string.Empty;
        private string _tvhServerName = string.Empty;
        private int _httpPort;
        private int _htspPort;
        private string _webRoot = string.Empty;
        private string _userName = string.Empty;
        private string _password = string.Empty;
        private bool _enableSubsMaudios;
        private bool _forceDeinterlace;

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

            // The channel type is applied in Init(), once the configuration has been read.
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
            StartConnectionLoop();
            long started = Stopwatch.GetTimestamp();
            lock (_lock)
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    if (_disposed || _authenticationFailed)
                    {
                        return -1;
                    }

                    bool connected = _connected && _htsConnection != null && !_htsConnection.NeedsRestart();
                    if (connected && _initialLoadFinished)
                    {
                        return 0;
                    }

                    if ((_firstConnectAttemptCompleted && !connected)
                        || Stopwatch.GetElapsedTime(started) >= _initialLoadTimeout)
                    {
                        return -1;
                    }

                    Monitor.Wait(_lock, TimeSpan.FromMilliseconds(100));
                }

                return -1;
            }
        }

        /// <summary>
        /// Waits for the initial sync and fails when the server is unavailable.
        /// </summary>
        /// <remarks>
        /// Every Jellyfin-facing operation goes through here so that an outage always surfaces
        /// as an error. An empty result would be taken as "nothing exists" (Jellyfin deletes
        /// channels, programs and recordings that are missing from a refresh), and a silent
        /// return would pretend a timer or recording change succeeded.
        /// </remarks>
        /// <param name="operation">The calling operation, for the error message.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task that completes once the server is usable.</returns>
        /// <exception cref="InvalidOperationException">The server is unreachable, rejected the credentials or has not finished the initial sync.</exception>
        public async Task EnsureAvailableAsync(string operation, CancellationToken cancellationToken)
        {
            int result = await Task.Run(() => WaitForInitialLoad(cancellationToken), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (result == -1)
            {
                throw new InvalidOperationException("[TVHclient] " + operation + " is not possible: " + GetUnavailableReason());
            }
        }

        private void Init()
        {
            lock (_lock)
            {
                InitConfiguration();
            }
        }

        private void InitConfiguration()
        {
            if (_configured == true)
            {
                return;
            }

            _logger.LogDebug("[TVHclient] HTSConnectionHandler - Init()");

            var config = Plugin.Instance.Configuration;

            _logger.LogDebug("[TVHclient] HTSConnectionHandler - Config initialized");

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

            _priority = config.Priority;
            _profile = config.Profile.Trim();
            _channelType = config.ChannelType.Trim();
            _enableSubsMaudios = config.EnableSubsMaudios;
            _forceDeinterlace = config.ForceDeinterlace;

            if (_priority < DvrPriorityImportant || _priority > DvrPriorityNotSet)
            {
                _priority = DvrPriorityNormal;
                _logger.LogWarning(
                    "[TVHclient] HTSConnectionHandler.Init: priority {ConfiguredPriority} is out of range [{Lowest}-{Highest}] - using {Fallback} (normal)",
                    config.Priority,
                    DvrPriorityImportant,
                    DvrPriorityNotSet,
                    DvrPriorityNormal);
            }

            _tvhServerName = config.TVH_ServerName.Trim();
            _httpPort = config.HTTP_Port;
            _htspPort = config.HTSP_Port;

            _userName = config.Username.Trim();
            _password = config.Password.Trim();

            _httpBaseUrl = BuildHttpBaseUrl();

            string authInfo = _userName + ":" + _password;
            authInfo = Convert.ToBase64String(Encoding.Default.GetBytes(authInfo));
            _headers["Authorization"] = "Basic " + authInfo;

            // The constructor runs before any configuration is available, so the channel type
            // has to be handed to the data helper here, once it has actually been read.
            _channelDataHelper.SetChannelType4Other(_channelType);
            _channelDataHelper.SetIncludeUnnumberedChannels(config.IncludeUnnumberedChannels);

            _configured = true;
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
        /// absent field means TVHeadend is served from the root. The HTTP URLs are rebuilt
        /// because they are assembled in Init(), before a connection exists.
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
            _httpBaseUrl = BuildHttpBaseUrl();
        }

        /// <summary>
        /// Builds the TVHeadend HTTP base URL from the current settings.
        /// </summary>
        /// <returns>The HTTP base URL.</returns>
        private string BuildHttpBaseUrl()
        {
            if (_enableSubsMaudios)
            {
                // Use HTTP basic auth instead of TVH ticketing system for authentication to allow the users to switch subs or audio tracks at any time
                return "http://" + _userName + ":" + _password + "@" + _tvhServerName + ":" + _httpPort + _webRoot;
            }

            return "http://" + _tvhServerName + ":" + _httpPort + _webRoot;
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
        /// The web root is the one reported by the server, so it is only final once connected.
        /// </remarks>
        /// <param name="relativePath">The path below the web root, with or without a leading slash.</param>
        /// <returns>An absolute URL including the configured credentials.</returns>
        public string GetAuthenticatedUrl(string relativePath)
        {
            StartConnectionLoop();

            lock (_lock)
            {
                return "http://" + _userName + ":" + _password + "@" + _tvhServerName + ":" + _httpPort + _webRoot
                    + "/" + relativePath.TrimStart('/');
            }
        }

        public string? GetChannelImageUrl(string channelId)
        {
            Init();

            _logger.LogDebug("[TVHclient] HTSConnectionHandler.GetChannelImage: channelId: {Id}", channelId);

            return ResolveImageUrl(_channelDataHelper.GetChannelIcon4ChannelId(channelId));
        }

        public Dictionary<string, string> GetHeaders()
        {
            lock (_lock)
            {
                return new Dictionary<string, string>(_headers);
            }
        }

        // private static Stream ImageToPNGStream(Image image)
        // {
        //    Stream stream = new System.IO.MemoryStream();
        //    image.Save(stream, ImageFormat.Png);
        //    stream.Position = 0;
        //    return stream;
        // }

        /// <summary>
        /// Describes why the server cannot be used right now, for exceptions raised towards Jellyfin.
        /// </summary>
        /// <returns>A human readable reason.</returns>
        public string GetUnavailableReason()
        {
            lock (_lock)
            {
                if (_authenticationFailed)
                {
                    return "authentication with TVHeadend server '" + _tvhServerName + ":" + _htspPort
                        + "' failed; the plugin retries once the connection settings in the plugin configuration are changed";
                }

                return "TVHeadend server '" + _tvhServerName + ":" + _htspPort + "' is unreachable or has not finished the initial sync";
            }
        }

        private void StartConnectionLoop()
        {
            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }

                if (_authenticationFailed)
                {
                    // Rejected credentials do not fix themselves: retry only once the
                    // connection settings differ from the ones the server rejected.
                    _configured = false;
                    InitConfiguration();
                    if (string.Equals(ConfigurationKey(_tvhServerName, _htspPort, _userName, _password), _rejectedConfiguration, StringComparison.Ordinal))
                    {
                        return;
                    }

                    _authenticationFailed = false;
                    _rejectedConfiguration = null;
                    _logger.LogInformation("[TVHclient] HTSConnectionHandler: connection settings changed, retrying authentication with TVHeadend");
                }

                InitConfiguration();
                if (_connectionTask == null || _connectionTask.IsCompleted)
                {
                    // One owner remains alive through connection, sync, service and backoff.
                    // A disconnect cannot race a successful task's completion anymore. The
                    // loop only ends on shutdown or rejected credentials, both gated above,
                    // so a completed task here is unexpected and simply gets replaced.
                    _firstConnectAttemptCompleted = false;
                    CancellationToken token = _connectionLoopCts.Token;
                    _connectionTask = Task.Run(() => ConnectionLoop(token), CancellationToken.None);
                }
            }
        }

        private static string ConfigurationKey(string hostname, int port, string username, string password)
        {
            return string.Join('\n', hostname, port, username, password);
        }

        private async Task ConnectionLoop(CancellationToken cancellationToken)
        {
            // Consecutive failed attempts since startup or since the last session that
            // completed the initial sync; drives the backoff and decides who waits.
            int attempt = 0;
            while (!cancellationToken.IsCancellationRequested)
            {
                HTSConnectionAsync? connection = null;
                bool synced = false;
                string hostname = _tvhServerName;
                int port = _htspPort;
                try
                {
                    string username;
                    string password;
                    lock (_lock)
                    {
                        if (_disposed || _authenticationFailed)
                        {
                            return;
                        }

                        // Read a single configuration snapshot for each attempt. A saved
                        // correction during backoff takes effect without mixing credentials.
                        _configured = false;
                        InitConfiguration();
                        hostname = _tvhServerName;
                        port = _htspPort;
                        username = _userName;
                        password = _password;
                        Version? version = typeof(HTSConnectionHandler).Assembly.GetName().Version;
                        connection = new HTSConnectionAsync(this, "Jellyfin-TVHeadend", version?.ToString() ?? "unknown", _loggerFactory);
                        _htsConnection = connection;
                        _connected = false;
                        _initialLoadFinished = false;
                        _initialSyncReceived = false;
                        _sessionSynced = false;

                        // The server sends its complete state again after enableAsyncMetadata.
                        // Start from an empty snapshot so entries that were deleted or changed
                        // during the outage are not kept (adds for known ids are ignored).
                        // Callers are gated until this session's sync completes.
                        _channelDataHelper.Clear();
                        _dvrDataHelper.Clear();
                        _autorecDataHelper.Clear();
                        if (attempt == 0)
                        {
                            // Callers wait for this attempt: it is the first one after startup
                            // or the reconnect right after a working session dropped. Retries
                            // of a failed attempt are not awaited, so a dead server never
                            // blocks the UI beyond the first attempt.
                            _firstConnectAttemptCompleted = false;
                        }

                        _lastSyncProgress = Stopwatch.GetTimestamp();
                        Monitor.PulseAll(_lock);
                    }

                    _logger.LogDebug("[TVHclient] HTSConnectionHandler.ConnectionLoop: opening HTSP connection to {ServerAddress}:{Htspport}", hostname, port);
                    connection.Open(hostname, port, _connectTimeout);
                    if (!connection.Authenticate(username, password))
                    {
                        lock (_lock)
                        {
                            _authenticationFailed = true;
                            _rejectedConfiguration = ConfigurationKey(hostname, port, username, password);
                            Monitor.PulseAll(_lock);
                        }

                        _logger.LogError(
                            "[TVHclient] HTSConnectionHandler.ConnectionLoop: TVHeadend server {ServerAddress}:{Htspport} rejected the credentials of user '{User}'. "
                            + "Giving up: Live TV stays unavailable until the server, username or password in the plugin configuration are changed",
                            hostname,
                            port,
                            username);
                        return;
                    }

                    lock (_lock)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!ReferenceEquals(connection, _htsConnection) || connection.NeedsRestart())
                        {
                            throw new IOException("Connection lost while completing the HTSP handshake");
                        }

                        ApplyServerWebRoot(connection.GetWebRoot());
                        _connected = true;
                        _initialLoadFinished = _initialSyncReceived;
                        _sessionSynced = _initialSyncReceived;
                        _lastSyncProgress = Stopwatch.GetTimestamp();
                        Monitor.PulseAll(_lock);
                    }

                    _logger.LogInformation(
                        "[TVHclient] HTSConnectionHandler.ConnectionLoop: connection to {ServerAddress}:{Htspport} established; "
                        + "TVH server = '{ServerName}' {ServerVersion}; HTSP version negotiated = {NegotiatedHtspVersion} "
                        + "(server supports up to {ServerHtspVersion}, client up to {ClientHtspVersion})",
                        hostname,
                        port,
                        connection.GetServername(),
                        connection.GetServerversion(),
                        connection.GetNegotiatedProtocolVersion(),
                        connection.GetServerProtocolVersion(),
                        HTSMessage.HtspVersion);

                    while (!cancellationToken.IsCancellationRequested && !connection.NeedsRestart())
                    {
                        lock (_lock)
                        {
                            if (!_initialLoadFinished && Stopwatch.GetElapsedTime(_lastSyncProgress) >= _syncProgressTimeout)
                            {
                                // Stalled, as opposed to merely large: a big dump keeps
                                // arriving and refreshes the progress timestamp.
                                throw new TimeoutException("TVHeadend did not complete initial metadata sync");
                            }
                        }

                        if (connection.HasTimedOutResponse())
                        {
                            throw new TimeoutException("TVHeadend stopped answering HTSP requests");
                        }

                        await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception) when (cancellationToken.IsCancellationRequested)
                {
                    // Shutting down; the finally block releases the attempt.
                }
                catch (Exception ex) when (ex is TimeoutException or IOException or SocketException or OperationCanceledException)
                {
                    // Expected while the server is down or unresponsive: one line per
                    // attempt, no stack trace, the root cause instead of the buffer wrapper.
                    _logger.LogError(
                        "[TVHclient] HTSConnectionHandler.ConnectionLoop: can't connect to {ServerAddress}:{Htspport} - {Message}",
                        hostname,
                        port,
                        ex.GetBaseException().Message);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[TVHclient] HTSConnectionHandler.ConnectionLoop: connection attempt to {ServerAddress}:{Htspport} failed", hostname, port);
                }
                finally
                {
                    lock (_lock)
                    {
                        if (ReferenceEquals(connection, _htsConnection))
                        {
                            _htsConnection = null;
                            _connected = false;
                            _initialLoadFinished = false;
                            _initialSyncReceived = false;
                        }

                        // After a working session the reconnect is immediate and awaited, so
                        // callers keep waiting; after a failed attempt they fail fast.
                        synced = _sessionSynced;
                        _firstConnectAttemptCompleted = !synced;
                        Monitor.PulseAll(_lock);
                    }

                    // Only the loop disposes its attempts, including rejected credentials
                    // and shutdown. Never join workers while holding the handler lock.
                    connection?.Dispose();
                }

                if (synced)
                {
                    // A working session dropped: reconnect right away, and awaited, so a
                    // brief server restart stays invisible to callers.
                    attempt = 0;
                    continue;
                }

                attempt++;
                int delaySeconds = Math.Min(MaxRetryDelaySeconds, 5 << Math.Min(attempt - 1, 4));
                _logger.LogDebug("[TVHclient] HTSConnectionHandler.ConnectionLoop: next connection attempt in {Delay}s", delaySeconds);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        public void SendMessage(HTSMessage message, IHTSResponseHandler responseHandler)
        {
            StartConnectionLoop();
            lock (_lock)
            {
                if (_disposed || !_connected || _htsConnection == null || _htsConnection.NeedsRestart())
                {
                    throw new InvalidOperationException("[TVHclient] HTSConnectionHandler.SendMessage: " + GetUnavailableReason());
                }

                _htsConnection.SendMessage(message, responseHandler);
            }
        }

        /// <summary>
        /// Gets the HTSP version in effect for the current connection.
        /// </summary>
        /// <returns>The negotiated HTSP version, or -1 while not connected.</returns>
        public int GetNegotiatedProtocolVersion()
        {
            StartConnectionLoop();
            lock (_lock)
            {
                return (!_disposed && _connected && _htsConnection != null && !_htsConnection.NeedsRestart())
                    ? _htsConnection.GetNegotiatedProtocolVersion() : -1;
            }
        }

        public Task<IEnumerable<ChannelInfo>> BuildChannelInfos(CancellationToken cancellationToken)
        {
            return BuildSnapshotAsync(_channelDataHelper.BuildChannelInfos, cancellationToken);
        }

        private async Task<IEnumerable<T>> BuildSnapshotAsync<T>(Func<CancellationToken, Task<IEnumerable<T>>> build, CancellationToken cancellationToken)
        {
            HTSConnectionAsync connection;
            Task<IEnumerable<T>> building;
            lock (_lock)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_disposed || !_connected || !_initialLoadFinished || _htsConnection == null || _htsConnection.NeedsRestart())
                {
                    throw new InvalidOperationException("[TVHclient] Cannot build a metadata snapshot: " + GetUnavailableReason());
                }

                connection = _htsConnection;
                building = build(cancellationToken);
            }

            // The reconnect loop clears the helpers before receiving the next dump. A call
            // admitted by the availability gate must not publish that incomplete snapshot,
            // even if the replacement has already finished syncing by the time we return.
            var result = await building.ConfigureAwait(false);
            lock (_lock)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_disposed || !ReferenceEquals(connection, _htsConnection) || !_connected || !_initialLoadFinished || connection.NeedsRestart())
                {
                    throw new InvalidOperationException("[TVHclient] The TVHeadend connection changed while building a metadata snapshot");
                }

                return result;
            }
        }

        public int GetPriority()
        {
            Init();
            return _priority;
        }

        public string GetProfile()
        {
            Init();
            return _profile;
        }

        public string GetHttpBaseUrl()
        {
            // The web root is taken from the HTSP handshake, so the base URL is only
            // final once connected; never block on an unreachable server here.
            StartConnectionLoop();
            lock (_lock)
            {
                return _httpBaseUrl;
            }
        }

        public bool GetEnableSubsMaudios()
        {
            Init();
            return _enableSubsMaudios;
        }

        public bool GetForceDeinterlace()
        {
            Init();
            return _forceDeinterlace;
        }

        public Task<IEnumerable<MyRecordingInfo>> BuildDvrInfos(CancellationToken cancellationToken)
        {
            return BuildSnapshotAsync(_dvrDataHelper.BuildDvrInfos, cancellationToken);
        }

        public Task<IEnumerable<SeriesTimerInfo>> BuildAutorecInfos(CancellationToken cancellationToken)
        {
            return BuildSnapshotAsync(_autorecDataHelper.BuildAutorecInfos, cancellationToken);
        }

        public Task<IEnumerable<TimerInfo>> BuildPendingTimersInfos(CancellationToken cancellationToken)
        {
            return BuildSnapshotAsync(_dvrDataHelper.BuildPendingTimersInfos, cancellationToken);
        }

        public void OnError(HTSConnectionAsync connection, Exception ex)
        {
            bool wasConnected;
            lock (_lock)
            {
                if (_disposed || !ReferenceEquals(connection, _htsConnection))
                {
                    return;
                }

                wasConnected = _connected;

                // A synced session that dies is reconnected immediately and callers wait for
                // that attempt; a failure before the sync completed fails fast instead.
                _firstConnectAttemptCompleted = !_sessionSynced;
                _connected = false;
                _initialLoadFinished = false;
                _initialSyncReceived = false;
                Monitor.PulseAll(_lock);
            }

            connection.Stop();
            if (wasConnected)
            {
                _logger.LogError(ex, "[TVHclient] HTSConnectionHandler: HTSP error");
            }
        }

        public void OnMessage(HTSConnectionAsync connection, HTSMessage? response)
        {
            lock (_lock)
            {
                if (_disposed || !ReferenceEquals(connection, _htsConnection) || connection.NeedsRestart() || response == null)
                {
                    return;
                }

                _lastSyncProgress = Stopwatch.GetTimestamp();
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
                        _initialSyncReceived = true;
                        _initialLoadFinished = _connected;
                        _sessionSynced |= _connected;
                        Monitor.PulseAll(_lock);
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
        /// Stops the reconnect loop and releases the HTSP connection held by this handler.
        /// </summary>
        /// <param name="disposing"><c>true</c> to release managed resources.</param>
        protected virtual void Dispose(bool disposing)
        {
            if (!disposing)
            {
                return;
            }

            Task? loop;
            HTSConnectionAsync? connection;
            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _connected = false;
                _initialLoadFinished = false;
                _firstConnectAttemptCompleted = true;
                loop = _connectionTask;
                connection = _htsConnection;
                Monitor.PulseAll(_lock);
            }

            _connectionLoopCts.Cancel();
            connection?.Stop();
            loop?.GetAwaiter().GetResult();
            _connectionLoopCts.Dispose();
        }
    }
}
