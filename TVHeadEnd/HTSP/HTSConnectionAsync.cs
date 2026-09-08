using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TVHeadEnd.Helper;
using TVHeadEnd.HTSP.Responses;

namespace TVHeadEnd.HTSP
{
    public sealed class HTSConnectionAsync : IDisposable
    {
        private const long BytesPerGiga = 1024 * 1024 * 1024;

        private readonly object _lock;
        private readonly IHTSConnectionListener _listener;
        private readonly string _clientName;
        private readonly string _clientVersion;
        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger<HTSConnectionAsync> _logger;

        private readonly ByteList _buffer;
        private readonly BlockingBuffer<HTSMessage> _receivedMessagesQueue;
        private readonly BlockingBuffer<HTSMessage> _messagesForSendQueue;
        private readonly Dictionary<int, IHTSResponseHandler?> _responseHandlers;

        private readonly CancellationTokenSource _receiveHandlerThreadTokenSource;
        private readonly CancellationTokenSource _messageBuilderThreadTokenSource;
        private readonly CancellationTokenSource _sendingHandlerThreadTokenSource;
        private readonly CancellationTokenSource _messageDistributorThreadTokenSource;

        private volatile bool _needsRestart;
        private volatile bool _connected;
        private volatile int _seq;

        private int _serverProtocolVersion;
        private string? _servername;
        private string? _serverversion;
        private string? _diskSpace;

        private Thread? _receiveHandlerThread;
        private Thread? _messageBuilderThread;
        private Thread? _sendingHandlerThread;
        private Thread? _messageDistributorThread;

        private Socket? _socket;

        public HTSConnectionAsync(IHTSConnectionListener listener, string clientName, string clientVersion, ILoggerFactory loggerFactory)
        {
            _loggerFactory = loggerFactory;
            _logger = loggerFactory.CreateLogger<HTSConnectionAsync>();

            _connected = false;
            _lock = new object();

            _listener = listener;
            _clientName = clientName;
            _clientVersion = clientVersion;

            _buffer = new ByteList();
            _receivedMessagesQueue = new BlockingBuffer<HTSMessage>(int.MaxValue);
            _messagesForSendQueue = new BlockingBuffer<HTSMessage>(int.MaxValue);
            _responseHandlers = new Dictionary<int, IHTSResponseHandler?>();

            _receiveHandlerThreadTokenSource = new CancellationTokenSource();
            _messageBuilderThreadTokenSource = new CancellationTokenSource();
            _sendingHandlerThreadTokenSource = new CancellationTokenSource();
            _messageDistributorThreadTokenSource = new CancellationTokenSource();
        }

        public void Stop()
        {
            try
            {
                if (_receiveHandlerThread != null && _receiveHandlerThread.IsAlive)
                {
                    _receiveHandlerThreadTokenSource.Cancel();
                }

                if (_messageBuilderThread != null && _messageBuilderThread.IsAlive)
                {
                    _messageBuilderThreadTokenSource.Cancel();
                }

                if (_sendingHandlerThread != null && _sendingHandlerThread.IsAlive)
                {
                    _sendingHandlerThreadTokenSource.Cancel();
                }

                if (_messageDistributorThread != null && _messageDistributorThread.IsAlive)
                {
                    _messageDistributorThreadTokenSource.Cancel();
                }
            }
            catch
            {
            }

            try
            {
                if (_socket != null && _socket.Connected)
                {
                    _socket.Close();
                }
            }
            catch
            {
            }

            _needsRestart = true;
            _connected = false;
        }

        public bool NeedsRestart()
        {
            return _needsRestart;
        }

        public void Open(string hostname, int port)
        {
            if (_connected)
            {
                return;
            }

            lock (_lock)
            {
                while (!_connected)
                {
                    try
                    {
                        // Establish the remote endpoint for the socket.
                        if (!IPAddress.TryParse(hostname, out IPAddress? ipAddress))
                        {
                            // no IP --> ask DNS
                            IPHostEntry ipHostInfo = Dns.GetHostEntry(hostname);
                            ipAddress = ipHostInfo.AddressList[0];
                        }

                        IPEndPoint remoteEP = new IPEndPoint(ipAddress, port);

                        _logger.LogDebug(
                            "[TVHclient] HTSConnectionAsync.Open: IPEndPoint = '{IP}'; AddressFamily = '{AF}'",
                            remoteEP.ToString(),
                            ipAddress.AddressFamily);

                        // Create a TCP/IP socket.
                        _socket = new Socket(ipAddress.AddressFamily, SocketType.Stream, ProtocolType.Tcp);

                        // connect to server
                        _socket.Connect(remoteEP);

                        _connected = true;
                        _logger.LogDebug("[TVHclient] HTSConnectionAsync.Open: socket connected");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[TVHclient] HTSConnectionAsync.Open: exception caught");

                        Thread.Sleep(2000);
                    }
                }

                _receiveHandlerThread = StartBackgroundThread(ReceiveHandler);
                _messageBuilderThread = StartBackgroundThread(MessageBuilder);
                _sendingHandlerThread = StartBackgroundThread(SendingHandler);
                _messageDistributorThread = StartBackgroundThread(MessageDistributor);
            }
        }

        private static Thread StartBackgroundThread(ThreadStart threadStart)
        {
            Thread thread = new Thread(threadStart)
            {
                IsBackground = true
            };
            thread.Start();
            return thread;
        }

        public bool Authenticate(string username, string password)
        {
            _logger.LogDebug("[TVHclient] HTSConnectionAsync.authenticate: start");

            HTSMessage helloMessage = new HTSMessage();
            helloMessage.Method = "hello";
            helloMessage.PutField("clientname", _clientName);
            helloMessage.PutField("clientversion", _clientVersion);
            helloMessage.PutField("htspversion", HTSMessage.HtspVersion);
            helloMessage.PutField("username", username);

            LoopBackResponseHandler loopBackResponseHandler = new LoopBackResponseHandler();
            SendMessage(helloMessage, loopBackResponseHandler);
            HTSMessage helloResponse = loopBackResponseHandler.GetResponse();
            if (helloResponse != null)
            {
                if (helloResponse.ContainsField("htspversion"))
                {
                    _serverProtocolVersion = helloResponse.GetInt("htspversion");
                }
                else
                {
                    _serverProtocolVersion = -1;
                    _logger.LogDebug("[TVHclient] HTSConnectionAsync.authenticate: hello didn't include required field 'htspversion' - htsp incorrectly implemented by tvheadend");
                }

                if (helloResponse.ContainsField("servername"))
                {
                    _servername = helloResponse.GetString("servername");
                }
                else
                {
                    _servername = "n/a";
                    _logger.LogDebug("[TVHclient] HTSConnectionAsync.authenticate: hello didn't include required field 'servername' - htsp incorrectly implemented by tvheadend");
                }

                if (helloResponse.ContainsField("serverversion"))
                {
                    _serverversion = helloResponse.GetString("serverversion");
                }
                else
                {
                    _serverversion = "n/a";
                    _logger.LogDebug("[TVHclient] HTSConnectionAsync.authenticate: hello didn't include required field 'serverversion' - htsp incorrectly implemented by tvheadend");
                }

                byte[] salt;
                if (helloResponse.ContainsField("challenge"))
                {
                    salt = helloResponse.GetByteArray("challenge");
                }
                else
                {
                    salt = Array.Empty<byte>();
                    _logger.LogInformation("[TVHclient] HTSConnectionAsync.authenticate: hello didn't include required field 'challenge' - htsp incorrectly implemented by tvheadend");
                }

                byte[] digest = SHA1Helper.GenerateSaltedSHA1(password, salt);
                HTSMessage authMessage = new HTSMessage();
                authMessage.Method = "authenticate";
                authMessage.PutField("username", username);
                authMessage.PutField("digest", digest);
                SendMessage(authMessage, loopBackResponseHandler);
                HTSMessage authResponse = loopBackResponseHandler.GetResponse();
                if (authResponse != null)
                {
                    bool auth = authResponse.GetInt("noaccess", 0) != 1;
                    if (auth)
                    {
                        HTSMessage getDiskSpaceMessage = new HTSMessage();
                        getDiskSpaceMessage.Method = "getDiskSpace";
                        SendMessage(getDiskSpaceMessage, loopBackResponseHandler);
                        HTSMessage diskSpaceResponse = loopBackResponseHandler.GetResponse();
                        if (diskSpaceResponse != null)
                        {
                            long freeDiskSpace = -1;
                            long totalDiskSpace = -1;
                            if (diskSpaceResponse.ContainsField("freediskspace"))
                            {
                                freeDiskSpace = diskSpaceResponse.GetLong("freediskspace") / BytesPerGiga;
                            }
                            else
                            {
                                _logger.LogDebug("[TVHclient] HTSConnectionAsync.authenticate: getDiskSpace didn't include required field 'freediskspace' - htsp incorrectly implemented by tvheadend");
                            }

                            if (diskSpaceResponse.ContainsField("totaldiskspace"))
                            {
                                totalDiskSpace = diskSpaceResponse.GetLong("totaldiskspace") / BytesPerGiga;
                            }
                            else
                            {
                                _logger.LogDebug("[TVHclient] HTSConnectionAsync.authenticate: getDiskSpace didn't include required field 'totaldiskspace' - htsp incorrectly implemented by tvheadend");
                            }

                            _diskSpace = freeDiskSpace + "GB / " + totalDiskSpace + "GB";
                        }

                        HTSMessage enableAsyncMetadataMessage = new HTSMessage();
                        enableAsyncMetadataMessage.Method = "enableAsyncMetadata";
                        SendMessage(enableAsyncMetadataMessage, null);
                    }

                    _logger.LogDebug("[TVHclient] HTSConnectionAsync.authenticate: authenticated = {M}", auth);
                    return auth;
                }
            }

            _logger.LogError("[TVHclient] HTSConnectionAsync.authenticate: no hello response");
            return false;
        }

        public int GetServerProtocolVersion()
        {
            return _serverProtocolVersion;
        }

        public string? GetServername()
        {
            return _servername;
        }

        public string? GetServerversion()
        {
            return _serverversion;
        }

        public string? GetDiskspace()
        {
            return _diskSpace;
        }

        public void SendMessage(HTSMessage message, IHTSResponseHandler? responseHandler)
        {
            // loop the sequence number
            if (_seq == int.MaxValue)
            {
                _seq = int.MinValue;
            }
            else
            {
                _seq++;
            }

            // housekeeping very old response handlers
            _responseHandlers.Remove(_seq);

            message.PutField("seq", _seq);
            _messagesForSendQueue.Enqueue(message);
            _responseHandlers.Add(_seq, responseHandler);
        }

        private void SendingHandler()
        {
            bool threadOk = true;
            while (_connected && threadOk)
            {
                if (_sendingHandlerThreadTokenSource.IsCancellationRequested)
                {
                    return;
                }

                try
                {
                    HTSMessage message = _messagesForSendQueue.Dequeue();
                    byte[] data2send = message.BuildBytes();
                    int bytesSent = _socket!.Send(data2send);
                    if (bytesSent != data2send.Length)
                    {
                        _logger.LogError(
                            "[TVHclient] HTSConnectionAsync.SendingHandler: sending data not completed\nBytes sent: {Txbytes}\nMessage bytes: " +
                            "{Msgbytes}\nMessage: {Msg}",
                            bytesSent,
                            data2send.Length,
                            message.ToString());
                    }
                }
                catch (Exception ex)
                {
                    threadOk = false;
                    _logger.LogError(ex, "[TVHclient] HTSConnectionAsync.SendingHandler: exception caught");
                    if (_listener != null)
                    {
                        _listener.OnError(ex);
                    }
                    else
                    {
                        _logger.LogError(ex, "[TVHclient] HTSConnectionAsync.SendingHandler: exception caught, but no error listener is configured");
                    }
                }
            }
        }

        private void ReceiveHandler()
        {
            bool threadOk = true;
            byte[] readBuffer = new byte[1024];
            while (_connected && threadOk)
            {
                if (_receiveHandlerThreadTokenSource.IsCancellationRequested)
                {
                    return;
                }

                try
                {
                    int bytesReceived = _socket!.Receive(readBuffer);
                    if (bytesReceived == 0)
                    {
                        Stop();
                        return;
                    }

                    _buffer.AppendCount(readBuffer, bytesReceived);
                }
                catch (Exception ex)
                {
                    threadOk = false;
                    if (_listener != null)
                    {
                        Task.Run(() => _listener.OnError(ex));
                    }
                    else
                    {
                        _logger.LogError(ex, "[TVHclient] HTSConnectionAsync.ReceiveHandler: exception caught, but no error listener is configured");
                    }
                }
            }
        }

        private void MessageBuilder()
        {
            bool threadOk = true;
            while (_connected && threadOk)
            {
                if (_messageBuilderThreadTokenSource.IsCancellationRequested)
                {
                    return;
                }

                try
                {
                    byte[] lengthInformation = _buffer.GetFromStart(4);
                    long messageDataLength = HTSMessage.UIntToLong(lengthInformation[0], lengthInformation[1], lengthInformation[2], lengthInformation[3]);
                    byte[] messageData = _buffer.ExtractFromStart((int)messageDataLength + 4); // should be long !!!
                    HTSMessage? response = HTSMessage.Parse(messageData, _loggerFactory.CreateLogger<HTSMessage>());
                    if (response != null)
                    {
                        _receivedMessagesQueue.Enqueue(response);
                    }
                }
                catch (Exception ex)
                {
                    threadOk = false;
                    if (_listener != null)
                    {
                        _listener.OnError(ex);
                    }
                    else
                    {
                        _logger.LogError(ex, "[TVHclient] HTSConnectionAsync.MessageBuilder: exception caught, but no error listener is configured");
                    }
                }
            }
        }

        private void MessageDistributor()
        {
            bool threadOk = true;
            while (_connected && threadOk)
            {
                if (_messageDistributorThreadTokenSource.IsCancellationRequested)
                {
                    return;
                }

                try
                {
                    HTSMessage response = _receivedMessagesQueue.Dequeue();
                    if (response.ContainsField("seq"))
                    {
                        int seqNo = response.GetInt("seq");
                        if (_responseHandlers.TryGetValue(seqNo, out var currHTSResponseHandler))
                        {
                            if (currHTSResponseHandler != null)
                            {
                                _responseHandlers.Remove(seqNo);
                                currHTSResponseHandler.HandleResponse(response);
                            }
                        }
                        else
                        {
                            _logger.LogCritical("[TVHclient] HTSConnectionAsync.MessageDistributor: HTSResponseHandler for seq = '{Seq}' not found", seqNo);
                        }
                    }
                    else
                    {
                        // auto update messages
                        if (_listener != null)
                        {
                            _listener.OnMessage(response);
                        }
                    }
                }
                catch (Exception ex)
                {
                    threadOk = false;
                    if (_listener != null)
                    {
                        _listener.OnError(ex);
                    }
                    else
                    {
                        _logger.LogError(ex, "[TVHclient] HTSConnectionAsync.MessageBuilder: exception caught, but no error listener is configured");
                    }
                }
            }
        }

        public void Dispose()
        {
            Stop();

            _receiveHandlerThreadTokenSource.Dispose();
            _messageBuilderThreadTokenSource.Dispose();
            _sendingHandlerThreadTokenSource.Dispose();
            _messageDistributorThreadTokenSource.Dispose();
            _socket?.Dispose();
        }
    }
}
