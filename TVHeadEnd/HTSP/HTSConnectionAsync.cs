using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
        // Bounded wait for handshake responses so that a connection dying
        // during the handshake can't block the caller forever.
        private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(20);

        // A server that stopped answering altogether is detected through the age of its
        // oldest unanswered request. Requests after the handshake can legitimately take far
        // longer than the handshake itself (a week of EPG for one channel over a slow link),
        // so this is deliberately generous; the service layer bounds individual calls.
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(2);

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

        private readonly CancellationTokenSource _stopCts = new CancellationTokenSource();
        private readonly Dictionary<int, long> _responseStarted = new Dictionary<int, long>();
        private bool _opening;
        private bool _opened;
        private bool _disposed;

        private volatile bool _needsRestart;
        private volatile bool _connected;
        private int _seq;

        private int _serverProtocolVersion;
        private string? _servername;
        private string? _serverversion;
        private string? _webRoot;

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
        }

        public void Stop()
        {
            Stop(new IOException("The HTSP connection was stopped"));
        }

        private bool Stop(Exception error)
        {
            lock (_lock)
            {
                if (_needsRestart)
                {
                    return false;
                }

                _needsRestart = true;
                _connected = false;
                _stopCts.Cancel();
                _buffer.Close();
                _receivedMessagesQueue.Close(error);
                _messagesForSendQueue.Close(error);

                // Wake socket operations before joining the workers. Dispose the socket
                // only after Open and all workers have finished using it.
                try
                {
                    _socket?.Shutdown(SocketShutdown.Both);
                }
                catch (SocketException)
                {
                    // Connecting and already-disconnected sockets cannot be shut down.
                }

                foreach (IHTSResponseHandler? handler in _responseHandlers.Values)
                {
                    handler?.HandleError(error);
                }

                _responseHandlers.Clear();
                _responseStarted.Clear();
                return true;
            }
        }

        private void ReportError(Exception error)
        {
            if (Stop(error))
            {
                // Never call the listener while holding the connection lock: API calls
                // acquire the handler lock before accepting a request on this connection.
                _listener.OnError(this, error);
            }
        }

        public bool NeedsRestart()
        {
            return _needsRestart;
        }

        public bool HasTimedOutResponse()
        {
            lock (_lock)
            {
                foreach (long started in _responseStarted.Values)
                {
                    if (Stopwatch.GetElapsedTime(started) >= RequestTimeout)
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        public void Open(string hostname, int port, TimeSpan connectTimeout)
        {
            lock (_lock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_needsRestart || _opened)
                {
                    throw new InvalidOperationException("An HTSP connection can only be opened once");
                }

                _opened = true;
                _opening = true;
            }

            try
            {
                long started = Stopwatch.GetTimestamp();
                if (!IPAddress.TryParse(hostname, out IPAddress? ipAddress))
                {
                    // DNS is part of the attempt deadline and must not hold up shutdown.
                    IPAddress[] addresses = Dns.GetHostAddressesAsync(hostname, _stopCts.Token)
                        .WaitAsync(connectTimeout, _stopCts.Token).GetAwaiter().GetResult();
                    ipAddress = addresses[0];
                }

                IPEndPoint remoteEP = new IPEndPoint(ipAddress, port);
                Socket socket;
                lock (_lock)
                {
                    _stopCts.Token.ThrowIfCancellationRequested();
                    socket = new Socket(ipAddress.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                    _socket = socket;
                }

                try
                {
                    socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                    socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 60);
                    socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 10);
                    socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3);
                }
                catch (SocketException ex)
                {
                    _logger.LogWarning(ex, "[TVHclient] HTSConnectionAsync.Open: TCP keepalive not available");
                }

                IAsyncResult connectResult = socket.BeginConnect(remoteEP, null, null);
                using (WaitHandle connectWait = connectResult.AsyncWaitHandle)
                {
                    while (!connectWait.WaitOne(TimeSpan.FromMilliseconds(100)))
                    {
                        _stopCts.Token.ThrowIfCancellationRequested();
                        if (Stopwatch.GetElapsedTime(started) >= connectTimeout)
                        {
                            throw new TimeoutException("No response from '" + remoteEP + "' within " + connectTimeout.TotalSeconds + "s");
                        }
                    }
                }

                socket.EndConnect(connectResult);
                lock (_lock)
                {
                    _stopCts.Token.ThrowIfCancellationRequested();
                    _connected = true;
                    _receiveHandlerThread = StartBackgroundThread(ReceiveHandler);
                    _messageBuilderThread = StartBackgroundThread(MessageBuilder);
                    _sendingHandlerThread = StartBackgroundThread(SendingHandler);
                    _messageDistributorThread = StartBackgroundThread(MessageDistributor);
                }
            }
            finally
            {
                lock (_lock)
                {
                    _opening = false;
                    Monitor.PulseAll(_lock);
                }
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

        /// <summary>
        /// Performs the HTSP hello/authenticate handshake.
        /// </summary>
        /// <param name="username">The TVHeadend user name.</param>
        /// <param name="password">The TVHeadend password.</param>
        /// <returns><c>true</c> if the server accepted the credentials, <c>false</c> if it rejected them.</returns>
        /// <exception cref="TimeoutException">The server did not answer a handshake message in time.</exception>
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
            HTSMessage? helloResponse = loopBackResponseHandler.GetResponse(ResponseTimeout);
            if (helloResponse == null)
            {
                throw new TimeoutException("No response to 'hello' within " + ResponseTimeout.TotalSeconds + "s");
            }

            if (helloResponse.ContainsField("htspversion"))
            {
                _serverProtocolVersion = helloResponse.GetInt("htspversion");
            }
            else
            {
                _serverProtocolVersion = -1;
                _logger.LogDebug("[TVHclient] HTSConnectionAsync.authenticate: hello didn't include required field 'htspversion' - htsp incorrectly implemented by tvheadend");
            }

            // TVHeadend only sends "webroot" when it is actually configured behind a
            // path prefix; its absence means the server is served from the root.
            _webRoot = helloResponse.GetString("webroot", null);

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
            HTSMessage? authResponse = loopBackResponseHandler.GetResponse(ResponseTimeout);
            if (authResponse == null)
            {
                throw new TimeoutException("No response to 'authenticate' within " + ResponseTimeout.TotalSeconds + "s");
            }

            bool auth = authResponse.GetInt("noaccess", 0) != 1;
            if (auth)
            {
                HTSMessage enableAsyncMetadataMessage = new HTSMessage();
                enableAsyncMetadataMessage.Method = "enableAsyncMetadata";
                SendMessage(enableAsyncMetadataMessage, null);
            }

            _logger.LogDebug("[TVHclient] HTSConnectionAsync.authenticate: authenticated = {M}", auth);
            return auth;
        }

        /// <summary>
        /// Gets the highest HTSP version the server itself supports.
        /// </summary>
        /// <remarks>
        /// This is the raw <c>htspversion</c> from the hello response, which reports the server's
        /// own maximum rather than the agreed version. Use <see cref="GetNegotiatedProtocolVersion"/>
        /// to decide which fields the connection will actually carry.
        /// </remarks>
        /// <returns>The server's maximum supported HTSP version.</returns>
        public int GetServerProtocolVersion()
        {
            return _serverProtocolVersion;
        }

        /// <summary>
        /// Gets the HTSP version actually in effect for this connection.
        /// </summary>
        /// <remarks>
        /// TVHeadend applies <c>min(server version, requested version)</c> internally and does not
        /// report the result, so the same minimum is computed here.
        /// </remarks>
        /// <returns>The negotiated HTSP version.</returns>
        public int GetNegotiatedProtocolVersion()
        {
            return Math.Min(_serverProtocolVersion, (int)HTSMessage.HtspVersion);
        }

        public string? GetServername()
        {
            return _servername;
        }

        public string? GetServerversion()
        {
            return _serverversion;
        }

        /// <summary>
        /// Gets the web root TVHeadend reported during the HTSP handshake.
        /// </summary>
        /// <returns>The server's web root, or <c>null</c> if it serves from the root.</returns>
        public string? GetWebRoot()
        {
            return _webRoot;
        }

        public void SendMessage(HTSMessage message, IHTSResponseHandler? responseHandler)
        {
            lock (_lock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (!_connected || _needsRestart)
                {
                    throw new IOException("The HTSP connection is not available");
                }

                int sequence;
                do
                {
                    _seq = _seq == int.MaxValue ? int.MinValue : _seq + 1;
                    sequence = _seq;
                }
                while (_responseHandlers.ContainsKey(sequence));

                message.PutField("seq", sequence);
                _responseHandlers.Add(sequence, responseHandler);
                _responseStarted.Add(sequence, Stopwatch.GetTimestamp());
                // Registration precedes publication to the sending thread.
                _messagesForSendQueue.Enqueue(message);
            }
        }

        private void SendingHandler()
        {
            try
            {
                while (_connected)
                {
                    HTSMessage message = _messagesForSendQueue.Dequeue();
                    byte[] data = message.BuildBytes();
                    int offset = 0;
                    while (offset < data.Length && _connected)
                    {
                        int sent = _socket!.Send(data, offset, data.Length - offset, SocketFlags.None);
                        if (sent == 0)
                        {
                            throw new IOException("The HTSP socket stopped accepting data");
                        }

                        offset += sent;
                    }
                }
            }
            catch (Exception ex)
            {
                ReportError(ex);
            }
        }

        private void ReceiveHandler()
        {
            try
            {
                byte[] readBuffer = new byte[1024];
                while (_connected)
                {
                    int bytesReceived = _socket!.Receive(readBuffer);
                    if (bytesReceived == 0)
                    {
                        throw new IOException("The HTSP peer closed the connection");
                    }

                    _buffer.AppendCount(readBuffer, bytesReceived);
                }
            }
            catch (Exception ex)
            {
                ReportError(ex);
            }
        }

        private void MessageBuilder()
        {
            try
            {
                while (_connected)
                {
                    byte[] lengthInformation = _buffer.GetFromStart(4);
                    long messageDataLength = HTSMessage.UIntToLong(lengthInformation[0], lengthInformation[1], lengthInformation[2], lengthInformation[3]);
                    byte[] messageData = _buffer.ExtractFromStart(checked((int)messageDataLength + 4));
                    HTSMessage? response = HTSMessage.Parse(messageData, _loggerFactory.CreateLogger<HTSMessage>());
                    if (response != null)
                    {
                        _receivedMessagesQueue.Enqueue(response);
                    }
                }
            }
            catch (Exception ex)
            {
                ReportError(ex);
            }
        }

        private void MessageDistributor()
        {
            try
            {
                while (_connected)
                {
                    HTSMessage response = _receivedMessagesQueue.Dequeue();
                    if (response.ContainsField("seq"))
                    {
                        int sequence = response.GetInt("seq");
                        IHTSResponseHandler? handler;
                        lock (_lock)
                        {
                            if (!_connected || !_responseHandlers.TryGetValue(sequence, out handler))
                            {
                                continue;
                            }

                            _responseStarted.Remove(sequence);
                        }

                        // Keep the handler registered until processing finishes so Stop
                        // can fail even an in-flight response. Callbacks run without _lock.
                        handler?.HandleResponse(response);
                        lock (_lock)
                        {
                            _responseHandlers.Remove(sequence);
                        }
                    }
                    else
                    {
                        _listener.OnMessage(this, response);
                    }
                }
            }
            catch (Exception ex)
            {
                ReportError(ex);
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
            }

            Stop();
            lock (_lock)
            {
                while (_opening)
                {
                    Monitor.Wait(_lock);
                }
            }

            JoinWorker(_receiveHandlerThread);
            JoinWorker(_messageBuilderThread);
            JoinWorker(_sendingHandlerThread);
            JoinWorker(_messageDistributorThread);
            _socket?.Dispose();
            _stopCts.Dispose();
        }

        private static void JoinWorker(Thread? worker)
        {
            if (worker != null && worker != Thread.CurrentThread)
            {
                worker.Join();
            }
        }
    }
}
