using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.LiveTv;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;
using TVHeadEnd.DataHelper;
using TVHeadEnd.HTSP;


namespace TVHeadEnd
{
    public class HTSConnectionHandler : HTSConnectionListener
    {
        private static volatile HTSConnectionHandler _instance;
        private static object _syncRoot = new Object();

        private readonly object _lock = new Object();

        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger<HTSConnectionHandler> _logger;
        private readonly IHttpClientFactory _httpClientFactory;

        private volatile Boolean _initialLoadFinished = false;
        private volatile Boolean _connected = false;
        private volatile Boolean _configured = false;
        private volatile Boolean _firstConnectAttemptCompleted = false;

        // Give up a single connection attempt after this time (an unreachable
        // host would otherwise block for the full kernel TCP timeout).
        private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

        // How long callers may wait for the initial sync while a connection
        // attempt is in flight or the initial data is still being received.
        // When the server is known to be unreachable callers fail immediately
        // and never wait.
        private static readonly TimeSpan InitialLoadTimeout = TimeSpan.FromMinutes(5);

        private const int MaxRetryDelaySeconds = 60;

        private Task _connectionTask;

        private HTSConnectionAsync _htsConnection;
        private int _priority;
        private string _profile;
        private string _httpBaseUrl;
        private string _channelType;
        private string _tvhServerName;
        private int _httpPort;
        private int _htspPort;
        private string _webRoot;
        private string _userName;
        private string _password;
        private bool _enableSubsMaudios;
        private bool _forceDeinterlace;

        // Data helpers
        private readonly ChannelDataHelper _channelDataHelper;
        private readonly DvrDataHelper _dvrDataHelper;
        private readonly AutorecDataHelper _autorecDataHelper;

        private LiveTvService _liveTvService;

        private Dictionary<string, string> _headers = new Dictionary<string, string>();

        public HTSConnectionHandler(ILoggerFactory loggerFactory, IHttpClientFactory httpClientFactory)
        {
            _loggerFactory = loggerFactory;
            _logger = loggerFactory.CreateLogger<HTSConnectionHandler>();
            _httpClientFactory = httpClientFactory;
            _liveTvService = null;

            //System.Diagnostics.StackTrace t = new System.Diagnostics.StackTrace();
            _logger.LogDebug("[TVHclient] HTSConnectionHandler");

            _channelDataHelper = new ChannelDataHelper(loggerFactory.CreateLogger<ChannelDataHelper>());
            _dvrDataHelper = new DvrDataHelper(loggerFactory.CreateLogger<DvrDataHelper>());
            _autorecDataHelper = new AutorecDataHelper(loggerFactory.CreateLogger<AutorecDataHelper>());

            _channelDataHelper.SetChannelType4Other(_channelType);
        }

        public static HTSConnectionHandler GetInstance(ILoggerFactory loggerFactory, IHttpClientFactory httpClientFactory)
        {
            if (_instance == null)
            {
                lock (_syncRoot)
                {
                    if (_instance == null)
                    {
                        _instance = new HTSConnectionHandler(loggerFactory, httpClientFactory);
                    }
                }
            }
            return _instance;
        }

        public void setLiveTvService(LiveTvService liveTvService)
        {
            _liveTvService = liveTvService;
        }

        public LiveTvService getLiveTvService()
        {
            return _liveTvService;
        }

        public int WaitForInitialLoad(CancellationToken cancellationToken)
        {
            StartConnectionLoop();
            DateTime deadline = DateTime.UtcNow + InitialLoadTimeout;
            while (!cancellationToken.IsCancellationRequested)
            {
                if (_initialLoadFinished)
                {
                    return 0;
                }

                // Fail fast while the server is unreachable: the background
                // loop keeps reconnecting, callers must not block on it.
                if (_firstConnectAttemptCompleted && !_connected)
                {
                    return -1;
                }

                if (DateTime.UtcNow > deadline)
                {
                    return -1;
                }

                Thread.Sleep(100);
            }
            return -1;
        }

        private void init()
        {
            if(_configured == true)
            {
                return ;
            }
            _logger.LogDebug("[TVHclient] HTSConnectionHandler - Init()");

            var config = Plugin.Instance.Configuration;

            _logger.LogDebug("[TVHclient] HTSConnectionHandler - Config initialized");

            if (string.IsNullOrEmpty(config.TVH_ServerName))
            {
                string message = "[TVHclient] HTSConnectionHandler.ensureConnection: TVH server name must be configured";
                _logger.LogError(message);
                throw new InvalidOperationException(message);
            }

            if (string.IsNullOrEmpty(config.Username))
            {
                string message = "[TVHclient] HTSConnectionHandler.ensureConnection: username must be configured";
                _logger.LogError(message);
                throw new InvalidOperationException(message);
            }

            if (string.IsNullOrEmpty(config.Password))
            {
                string message = "[TVHclient] HTSConnectionHandler.ensureConnection: password must be configured";
                _logger.LogError(message);
                throw new InvalidOperationException(message);
            }

            _priority = config.Priority;
            _profile = config.Profile.Trim();
            _channelType = config.ChannelType.Trim();
            _enableSubsMaudios = config.EnableSubsMaudios;
            _forceDeinterlace = config.ForceDeinterlace;

            if (_priority < 0 || _priority > 4)
            {
                _priority = 2;
                _logger.LogDebug("[TVHclient] HTSConnectionHandler.ensureConnection: priority was out of range [0-4] - set to 2");
            }

            _tvhServerName = config.TVH_ServerName.Trim();
            _httpPort = config.HTTP_Port;
            _htspPort = config.HTSP_Port;
            _webRoot = config.WebRoot;
            if (_webRoot.EndsWith("/"))
            {
                _webRoot = _webRoot.Substring(0, _webRoot.Length - 1);
            }
            _userName = config.Username.Trim();
            _password = config.Password.Trim();

            if (_enableSubsMaudios)
            {
                // Use HTTP basic auth instead of TVH ticketing system for authentication to allow the users to switch subs or audio tracks at any time
                _httpBaseUrl = "http://" + _userName + ":" + _password + "@" + _tvhServerName + ":" + _httpPort + _webRoot;
            }
            else
            {
                _httpBaseUrl = "http://" + _tvhServerName + ":" + _httpPort + _webRoot;
            }

            string authInfo = _userName + ":" + _password;
            authInfo = Convert.ToBase64String(Encoding.Default.GetBytes(authInfo));
            _headers["Authorization"] = "Basic " + authInfo;
            _configured = true;
        }

        public string GetChannelImageUrl(string channelId)
        {
            init();

            _logger.LogDebug("[TVHclient] HTSConnectionHandler.GetChannelImage: channelId: {id}", channelId);

            String channelIcon = _channelDataHelper.GetChannelIcon4ChannelId(channelId);

            if (string.IsNullOrEmpty(channelIcon))
            {
                return null;
            }

            if (channelIcon.StartsWith("http"))
            {
                return _channelDataHelper.GetChannelIcon4ChannelId(channelId);
            }
            else
            {
                return "http://" + _userName + ":" + _password + "@" +_tvhServerName + ":" + _httpPort + _webRoot + "/" + channelIcon;
            }
        }

        public Dictionary<string, string> GetHeaders()
        {
            return new Dictionary<string, string>(_headers);
        }

        //private static Stream ImageToPNGStream(Image image)
        //{
        //    Stream stream = new System.IO.MemoryStream();
        //    image.Save(stream, ImageFormat.Png);
        //    stream.Position = 0;
        //    return stream;
        //}

        private void StartConnectionLoop()
        {
            init();

            lock (_lock)
            {
                if (_connected || (_connectionTask != null && !_connectionTask.IsCompleted))
                {
                    return;
                }

                _connectionTask = Task.Run(() => ConnectionLoop());
            }
        }

        private async Task ConnectionLoop()
        {
            int attempt = 0;
            while (!_connected)
            {
                try
                {
                    HTSConnectionAsync connection;
                    lock (_lock)
                    {
                        if (_htsConnection == null || _htsConnection.needsRestart())
                        {
                            _logger.LogDebug("[TVHclient] HTSConnectionHandler.ConnectionLoop: create new HTS connection");
                            Version version = Assembly.GetEntryAssembly().GetName().Version;
                            _htsConnection = new HTSConnectionAsync(this, "TVHclient4Emby-" + version.ToString(), "" + HTSMessage.HTSP_VERSION, _loggerFactory);
                        }
                        connection = _htsConnection;
                    }

                    _logger.LogDebug("[TVHclient] HTSConnectionHandler.ConnectionLoop: used connection parameters: " +
                        "TVH Server = '{servername}'; HTTP Port = '{httpport}'; HTSP Port = '{htspport}'; Web-Root = '{webroot}'; " +
                        "User = '{user}'; Password set = '{passexists}'",
                        _tvhServerName, _httpPort, _htspPort, _webRoot, _userName, (_password.Length > 0));

                    connection.open(_tvhServerName, _htspPort, ConnectTimeout);

                    if (connection.authenticate(_userName, _password))
                    {
                        _connected = true;
                        _logger.LogInformation("[TVHclient] HTSConnectionHandler.ConnectionLoop: connection to {servername}:{htspport} established",
                            _tvhServerName, _htspPort);
                        return;
                    }

                    _logger.LogError("[TVHclient] HTSConnectionHandler.ConnectionLoop: authentication failed");
                    connection.stop();
                }
                catch (Exception ex)
                {
                    _logger.LogError("[TVHclient] HTSConnectionHandler.ConnectionLoop: can't connect to {servername}:{htspport} - {message}",
                        _tvhServerName, _htspPort, ex.Message);
                }
                finally
                {
                    _firstConnectAttemptCompleted = true;
                }

                attempt++;
                int delaySeconds = Math.Min(MaxRetryDelaySeconds, 5 << Math.Min(attempt - 1, 4));
                _logger.LogDebug("[TVHclient] HTSConnectionHandler.ConnectionLoop: next connection attempt in {delay}s", delaySeconds);
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds)).ConfigureAwait(false);
            }
        }

        public void SendMessage(HTSMessage message, HTSResponseHandler responseHandler)
        {
            StartConnectionLoop();

            HTSConnectionAsync connection = _htsConnection;
            if (!_connected || connection == null)
            {
                throw new InvalidOperationException("[TVHclient] HTSConnectionHandler.SendMessage: not connected to TVH server '"
                    + _tvhServerName + ":" + _htspPort + "'");
            }

            connection.sendMessage(message, responseHandler);
        }

        public String GetServername()
        {
            StartConnectionLoop();
            HTSConnectionAsync connection = _htsConnection;
            return (_connected && connection != null) ? connection.getServername() : null;
        }

        public String GetServerVersion()
        {
            StartConnectionLoop();
            HTSConnectionAsync connection = _htsConnection;
            return (_connected && connection != null) ? connection.getServerversion() : null;
        }

        public int GetServerProtocolVersion()
        {
            StartConnectionLoop();
            HTSConnectionAsync connection = _htsConnection;
            return (_connected && connection != null) ? connection.getServerProtocolVersion() : -1;
        }

        public String GetDiskSpace()
        {
            StartConnectionLoop();
            HTSConnectionAsync connection = _htsConnection;
            return (_connected && connection != null) ? connection.getDiskspace() : null;
        }

        public Task<IEnumerable<ChannelInfo>> BuildChannelInfos(CancellationToken cancellationToken)
        {
            return _channelDataHelper.BuildChannelInfos(cancellationToken);
        }

        public int GetPriority()
        {
            init();
            return _priority;
        }

        public String GetProfile()
        {
            init();
            return _profile;
        }

        public String GetHttpBaseUrl()
        {
            init();
            return _httpBaseUrl;
        }

        public bool GetEnableSubsMaudios()
        {
            init();
            return _enableSubsMaudios;
        }

        public bool GetForceDeinterlace()
        {
            init();
            return _forceDeinterlace;
        }

        public Task<IEnumerable<MyRecordingInfo>> BuildDvrInfos(CancellationToken cancellationToken)
        {
            return _dvrDataHelper.buildDvrInfos(cancellationToken);
        }

        public Task<IEnumerable<SeriesTimerInfo>> BuildAutorecInfos(CancellationToken cancellationToken)
        {
            return _autorecDataHelper.buildAutorecInfos(cancellationToken);
        }

        public Task<IEnumerable<TimerInfo>> BuildPendingTimersInfos(CancellationToken cancellationToken)
        {
            return _dvrDataHelper.buildPendingTimersInfos(cancellationToken);
        }

        public void onError(Exception ex)
        {
            _logger.LogError(ex, "[TVHclient] HTSConnectionHandler: HTSP error");
            lock (_lock)
            {
                if (_htsConnection != null)
                {
                    _htsConnection.stop();
                    _htsConnection = null;
                }
                _connected = false;
                _initialLoadFinished = false;
            }
            //_liveTvService.sendDataSourceChanged();
            StartConnectionLoop();
        }

        public void onMessage(HTSMessage response)
        {
            if (response != null)
            {
                switch (response.Method)
                {
                    case "tagAdd":
                    case "tagUpdate":
                    case "tagDelete":
                        //_logger.LogCritical("[TVHclient] tad add/update/delete {resp}", response.ToString());
                        break;

                    case "channelAdd":
                    case "channelUpdate":
                        _channelDataHelper.Add(response);
                        break;

                    case "dvrEntryAdd":
                        _dvrDataHelper.dvrEntryAdd(response);
                        break;
                    case "dvrEntryUpdate":
                        _dvrDataHelper.dvrEntryUpdate(response);
                        break;
                    case "dvrEntryDelete":
                        _dvrDataHelper.dvrEntryDelete(response);
                        break;

                    case "autorecEntryAdd":
                        _autorecDataHelper.autorecEntryAdd(response);
                        break;
                    case "autorecEntryUpdate":
                        _autorecDataHelper.autorecEntryUpdate(response);
                        break;
                    case "autorecEntryDelete":
                        _autorecDataHelper.autorecEntryDelete(response);
                        break;

                    case "eventAdd":
                    case "eventUpdate":
                    case "eventDelete":
                        // should not happen as we don't subscribe for this events.
                        break;

                    //case "subscriptionStart":
                    //case "subscriptionGrace":
                    //case "subscriptionStop":
                    //case "subscriptionSkip":
                    //case "subscriptionSpeed":
                    //case "subscriptionStatus":
                    //    _logger.LogCritical("[TVHclient] subscription events {resp}", response.ToString());
                    //    break;

                    //case "queueStatus":
                    //    _logger.LogCritical("[TVHclient] queueStatus event {resp}", response.ToString());
                    //    break;

                    //case "signalStatus":
                    //    _logger.LogCritical("[TVHclient] signalStatus event {resp}", response.ToString());
                    //    break;

                    //case "timeshiftStatus":
                    //    _logger.LogCritical("[TVHclient] timeshiftStatus event {resp}", response.ToString());
                    //    break;

                    //case "muxpkt": // streaming data
                    //    _logger.LogCritical("[TVHclient] muxpkt event {resp}", response.ToString());
                    //    break;

                    case "initialSyncCompleted":
                        _initialLoadFinished = true;
                        break;

                    default:
                        //_logger.LogCritical("[TVHclient] Method '{method}' not handled in LiveTvService.cs", response.Method);
                        break;
                }
            }
        }
    }
}
