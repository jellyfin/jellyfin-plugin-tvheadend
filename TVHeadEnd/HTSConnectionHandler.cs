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
    class HTSConnectionHandler : HTSConnectionListener
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

        public int WaitForInitialLoad(CancellationToken cancellationToken)
        {
            ensureConnection();
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

        private void ensureConnection()
        {
            init();

            //_logger.LogDebug("[TVHclient] HTSConnectionHandler.ensureConnection");
            if (_htsConnection == null || _htsConnection.needsRestart())
            {
                _logger.LogDebug("[TVHclient] HTSConnectionHandler.ensureConnection: create new HTS connection");
                Version version = Assembly.GetEntryAssembly().GetName().Version;
                _htsConnection = new HTSConnectionAsync(this, "TVHclient4Emby-" + version.ToString(), "" + HTSMessage.HTSP_VERSION, _loggerFactory);
                _connected = false;
            }

            lock (_lock)
            {
                if (!_connected)
                {
                    _logger.LogDebug("[TVHclient] HTSConnectionHandler.ensureConnection: used connection parameters: " +
                        "TVH Server = '{servername}'; HTTP Port = '{httpport}'; HTSP Port = '{htspport}'; Web-Root = '{webroot}'; " +
                        "User = '{user}'; Password set = '{passexists}'",
                        _tvhServerName, _httpPort, _htspPort, _webRoot, _userName, (_password.Length > 0));

                    _htsConnection.open(_tvhServerName, _htspPort);
                    _connected = _htsConnection.authenticate(_userName, _password);

                    _logger.LogDebug("[TVHclient] HTSConnectionHandler.ensureConnection: connection established {c}", _connected);
                }
            }
        }

        public void SendMessage(HTSMessage message, HTSResponseHandler responseHandler)
        {
            ensureConnection();
            _htsConnection.sendMessage(message, responseHandler);
        }

        public String GetServername()
        {
            ensureConnection();
            return _htsConnection.getServername();
        }

        public String GetServerVersion()
        {
            ensureConnection();
            return _htsConnection.getServerversion();
        }

        public int GetServerProtocolVersion()
        {
            ensureConnection();
            return _htsConnection.getServerProtocolVersion();
        }

        public String GetDiskSpace()
        {
            ensureConnection();
            return _htsConnection.getDiskspace();
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
            _htsConnection.stop();
            _htsConnection = null;
            _connected = false;
            //_liveTvService.sendDataSourceChanged();
            ensureConnection();
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
