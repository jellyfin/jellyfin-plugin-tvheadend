using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.LiveTv;
using Microsoft.Extensions.Logging;
using TVHeadEnd.DataHelper;
using TVHeadEnd.HTSP;


namespace TVHeadEnd
{
    class HTSConnectionHandler : HTSConnectionListener
    {
        private static volatile HTSConnectionHandler _instance;
        private static object _syncRoot = new Object();

        private readonly object _lock = new Object();

        private readonly ILogger _logger;

        private volatile Boolean _initialLoadFinished = false;
        private volatile Boolean _connected = false;

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

        private HTSConnectionHandler(ILogger logger)
        {
            _logger = logger;

            //System.Diagnostics.StackTrace t = new System.Diagnostics.StackTrace();
            _logger.LogInformation("[TVHclient] HTSConnectionHandler()");

            _channelDataHelper = new ChannelDataHelper(logger);
            _dvrDataHelper = new DvrDataHelper(logger);
            _autorecDataHelper = new AutorecDataHelper(logger);

            init();

            _channelDataHelper.SetChannelType4Other(_channelType);
        }

        public static HTSConnectionHandler GetInstance(ILogger logger)
        {
            if (_instance == null)
            {
                lock (_syncRoot)
                {
                    if (_instance == null)
                    {
                        _instance = new HTSConnectionHandler(logger);
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
            var config = Plugin.Instance.Configuration;

            if (string.IsNullOrEmpty(config.TVH_ServerName))
            {
                string message = "[TVHclient] HTSConnectionHandler.ensureConnection: TVH-Server name must be configured.";
                _logger.LogError(message);
                throw new InvalidOperationException(message);
            }

            if (string.IsNullOrEmpty(config.Username))
            {
                string message = "[TVHclient] HTSConnectionHandler.ensureConnection: Username must be configured.";
                _logger.LogError(message);
                throw new InvalidOperationException(message);
            }

            if (string.IsNullOrEmpty(config.Password))
            {
                string message = "[TVHclient] HTSConnectionHandler.ensureConnection: Password must be configured.";
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
                _logger.LogInformation("[TVHclient] HTSConnectionHandler.ensureConnection: Priority was out of range [0-4] - set to 2");
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
        }

        public ImageStream GetChannelImage(string channelId, CancellationToken cancellationToken)
        {
            try
            {
                _logger.LogInformation("[TVHclient] HTSConnectionHandler.GetChannelImage() channelId: {id}", channelId);

                String channelIcon = _channelDataHelper.GetChannelIcon4ChannelId(channelId);

                _logger.LogInformation("[TVHclient] HTSConnectionHandler.GetChannelImage() channelIcon: {ico}", channelIcon);

                WebRequest request = null;

                if (channelIcon.StartsWith("http"))
                {
                    request = WebRequest.Create(channelIcon);

                    _logger.LogInformation("[TVHclient] HTSConnectionHandler.GetChannelImage() WebRequest: {ico}", channelIcon);
                }
                else
                {
                    string requestStr = "http://" + _tvhServerName + ":" + _httpPort + _webRoot + "/" + channelIcon;
                    request = WebRequest.Create(requestStr);
                    request.Headers["Authorization"] = _headers["Authorization"];

                    _logger.LogInformation("[TVHclient] HTSConnectionHandler.GetChannelImage() WebRequest: {req}", requestStr);
                }


                HttpWebResponse httpWebReponse = (HttpWebResponse)request.GetResponse();
                Stream stream = httpWebReponse.GetResponseStream();

                ImageStream imageStream = new ImageStream();

                int lastDot = channelIcon.LastIndexOf('.');
                if (lastDot > -1)
                {
                    String suffix = channelIcon.Substring(lastDot + 1);
                    suffix = suffix.ToLower();

                    _logger.LogInformation("[TVHclient] HTSConnectionHandler.GetChannelImage() image suffix: {sfx}", suffix);

                    switch (suffix)
                    {
                        case "bmp":
                            imageStream.Stream = stream;
                            imageStream.Format = MediaBrowser.Model.Drawing.ImageFormat.Bmp;
                            _logger.LogInformation("[TVHclient] HTSConnectionHandler.GetChannelImage() using fix image type BMP.");
                            break;

                        case "gif":
                            imageStream.Stream = stream;
                            imageStream.Format = MediaBrowser.Model.Drawing.ImageFormat.Gif;
                            _logger.LogInformation("[TVHclient] HTSConnectionHandler.GetChannelImage() using fix image type GIF.");
                            break;

                        case "jpg":
                            imageStream.Stream = stream;
                            imageStream.Format = MediaBrowser.Model.Drawing.ImageFormat.Jpg;
                            _logger.LogInformation("[TVHclient] HTSConnectionHandler.GetChannelImage() using fix image type JPG.");
                            break;

                        case "png":
                            imageStream.Stream = stream;
                            imageStream.Format = MediaBrowser.Model.Drawing.ImageFormat.Png;
                            _logger.LogInformation("[TVHclient] HTSConnectionHandler.GetChannelImage() using fix image type PNG.");
                            break;

                        case "webp":
                            imageStream.Stream = stream;
                            imageStream.Format = MediaBrowser.Model.Drawing.ImageFormat.Webp;
                            _logger.LogInformation("[TVHclient] HTSConnectionHandler.GetChannelImage() using fix image type WEBP.");
                            break;

                        default:
                            _logger.LogInformation("[TVHclient] HTSConnectionHandler.GetChannelImage() unkown image type '{sfx}' - return as PNG", suffix);
                            //Image image = Image.FromStream(stream);
                            //imageStream.Stream = ImageToPNGStream(image);
                            //imageStream.Format = MediaBrowser.Model.Drawing.ImageFormat.Png;
                            imageStream.Stream = stream;
                            imageStream.Format = MediaBrowser.Model.Drawing.ImageFormat.Png;
                            break;
                    }
                }
                else
                {
                    _logger.LogInformation("[TVHclient] HTSConnectionHandler.GetChannelImage() no image type in suffix of channelImage name '{ico}' found - return as PNG.", channelIcon);
                    //Image image = Image.FromStream(stream);
                    //imageStream.Stream = ImageToPNGStream(image);
                    //imageStream.Format = MediaBrowser.Model.Drawing.ImageFormat.Png;
                    imageStream.Stream = stream;
                    imageStream.Format = MediaBrowser.Model.Drawing.ImageFormat.Png;
                }

                return imageStream;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[TVHclient] HTSConnectionHandler.GetChannelImage() caught exception");
                return null;
            }
        }

        public string GetChannelImageUrl(string channelId)
        {
            _logger.LogInformation("[TVHclient] HTSConnectionHandler.GetChannelImage() channelId: {id}", channelId);

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
            //_logger.LogInformation("[TVHclient] HTSConnectionHandler.ensureConnection()");
            if (_htsConnection == null || _htsConnection.needsRestart())
            {
                _logger.LogInformation("[TVHclient] HTSConnectionHandler.ensureConnection() : create new HTS-Connection");
                Version version = Assembly.GetEntryAssembly().GetName().Version;
                _htsConnection = new HTSConnectionAsync(this, "TVHclient4Emby-" + version.ToString(), "" + HTSMessage.HTSP_VERSION, _logger);
                _connected = false;
            }

            lock (_lock)
            {
                if (!_connected)
                {
                    _logger.LogInformation("[TVHclient] HTSConnectionHandler.ensureConnection: Used connection parameters: " +
                        "TVH Server = '{servername}'; HTTP Port = '{httpport}'; HTSP Port = '{htspport}'; Web-Root = '{webroot}'; " +
                        "User = '{user}'; Password set = '{passexists}'",
                        _tvhServerName, _httpPort, _htspPort, _webRoot, _userName, (_password.Length > 0));

                    _htsConnection.open(_tvhServerName, _htspPort);
                    _connected = _htsConnection.authenticate(_userName, _password);

                    _logger.LogInformation("[TVHclient] HTSConnectionHandler.ensureConnection: connection established {c}", _connected);
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
            return _priority;
        }

        public String GetProfile()
        {
            return _profile;
        }

        public String GetHttpBaseUrl()
        {
            return _httpBaseUrl;
        }

        public bool GetEnableSubsMaudios()
        {
            return _enableSubsMaudios;
        }

        public bool GetForceDeinterlace()
        {
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
            _logger.LogError(ex, "[TVHclient] HTSConnectionHandler recorded a HTSP error");
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
