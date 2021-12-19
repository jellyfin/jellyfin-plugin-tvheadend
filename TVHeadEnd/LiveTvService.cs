using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;
using TVHeadEnd.Helper;
using TVHeadEnd.HTSP;
using TVHeadEnd.HTSP_Responses;
using TVHeadEnd.TimeoutHelper;
using static TVHeadEnd.AccessTicketHandler.TicketType;

namespace TVHeadEnd
{
    public class LiveTvService : ILiveTvService
    {
        public event EventHandler DataSourceChanged;
        public event EventHandler<RecordingStatusChangedEventArgs> RecordingStatusChanged;

        //Added for stream probing
        private readonly IMediaEncoder _mediaEncoder;

        private readonly TimeSpan TIMEOUT = TimeSpan.FromMinutes(5);

        private HTSConnectionHandler _htsConnectionHandler;
        private readonly AccessTicketHandler _channelTicketHandler, _recordingTicketHandler;

        private readonly ILogger<LiveTvService> _logger;
        public DateTime LastRecordingChange = DateTime.MinValue;

        public LiveTvService(ILoggerFactory loggerFactory, IMediaEncoder mediaEncoder, IHttpClientFactory httpClientFactory)
        {
            //System.Diagnostics.StackTrace t = new System.Diagnostics.StackTrace();
            _logger = loggerFactory.CreateLogger<LiveTvService>();
            _logger.LogDebug("[TVHclient] LiveTvService()");

            _htsConnectionHandler = HTSConnectionHandler.GetInstance(loggerFactory, httpClientFactory);
            _htsConnectionHandler.setLiveTvService(this);

            var ticketLifeSpan = TimeSpan.FromSeconds(30); // TVH built-in minimum ticket-TTL.
            _channelTicketHandler = new AccessTicketHandler(loggerFactory, _htsConnectionHandler, TIMEOUT, ticketLifeSpan, Channel);
            _recordingTicketHandler = new AccessTicketHandler(loggerFactory, _htsConnectionHandler, TIMEOUT, ticketLifeSpan, Recording);

            //Added for stream probing
            _mediaEncoder = mediaEncoder;
        }

        public string HomePageUrl { get { return "http://tvheadend.org/"; } }

        public string Name { get { return "TVHclient LiveTvService"; } }

        public void sendDataSourceChanged()
        {
            try
            {
                if (DataSourceChanged != null)
                {
                    _logger.LogDebug("[TVHclient] LiveTvService.sendDataSourceChanged: calling EventHandler 'DataSourceChanged'");
                    DataSourceChanged(this, EventArgs.Empty);
                }
                else
                {
                    _logger.LogDebug("[TVHclient] LiveTvService.sendDataSourceChanged: EventHandler 'DataSourceChanged' was not set by Jellyfin");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[TVHclient] LiveTvService.sendDataSourceChanged: exception caught");
            }
        }

        public void sendRecordingStatusChanged(RecordingStatusChangedEventArgs recordingStatusChangedEventArgs)
        {
            try
            {
                if (RecordingStatusChanged != null)
                {
                    _logger.LogDebug("[TVHclient] LiveTvService.sendRecordingStatusChanged: recording status changed");
                    RecordingStatusChanged(this, recordingStatusChangedEventArgs);
                }
                else
                {
                    _logger.LogCritical("[TVHclient] LiveTvServicesendRecordingStatusChanged: EventHandler 'RecordingStatusChanged' was not set by Jellyfin");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[TVHclient] LiveTvService.sendRecordingStatusChanged: exception caught");
            }
        }

        public async Task CancelSeriesTimerAsync(string timerId, CancellationToken cancellationToken)
        {
            int timeOut = await WaitForInitialLoadTask(cancellationToken);
            if (timeOut == -1 || cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug("[TVHclient] LiveTvService.CancelSeriesTimerAsync: call cancelled or timed out");
                return;
            }

            HTSMessage deleteAutorecMessage = new HTSMessage();
            deleteAutorecMessage.Method = "deleteAutorecEntry";
            deleteAutorecMessage.putField("id", timerId);

            TaskWithTimeoutRunner<HTSMessage> twtr = new TaskWithTimeoutRunner<HTSMessage>(TIMEOUT);
            TaskWithTimeoutResult<HTSMessage> twtRes = await twtr.RunWithTimeout(Task.Factory.StartNew<HTSMessage>(() =>
            {
                LoopBackResponseHandler lbrh = new LoopBackResponseHandler();
                _htsConnectionHandler.SendMessage(deleteAutorecMessage, lbrh);
                LastRecordingChange = DateTime.UtcNow;
                return lbrh.getResponse();
            }));

            if (twtRes.HasTimeout)
            {
                _logger.LogError("[TVHclient] LiveTvService.CancelSeriesTimerAsync: can't delete recording because the timeout was reached");
            }
            else
            {
                HTSMessage deleteAutorecResponse = twtRes.Result;
                Boolean success = deleteAutorecResponse.getInt("success", 0) == 1;
                if (!success)
                {
                    if (deleteAutorecResponse.containsField("error"))
                    {
                        _logger.LogError("[TVHclient] LiveTvService.CancelSeriesTimerAsync: can't delete recording: '{why}'", deleteAutorecResponse.getString("error"));
                    }
                    else if (deleteAutorecResponse.containsField("noaccess"))
                    {
                        _logger.LogError("[TVHclient] LiveTvService.CancelSeriesTimerAsync: can't delete recording: '{why}'", deleteAutorecResponse.getString("noaccess"));
                    }
                }
            }
        }

        public async Task CancelTimerAsync(string timerId, CancellationToken cancellationToken)
        {
            int timeOut = await WaitForInitialLoadTask(cancellationToken);
            if (timeOut == -1 || cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug("[TVHclient] LiveTvService.CancelTimerAsync: call cancelled or timed out");
                return;
            }

            HTSMessage cancelTimerMessage = new HTSMessage();
            cancelTimerMessage.Method = "cancelDvrEntry";
            cancelTimerMessage.putField("id", timerId);

            TaskWithTimeoutRunner<HTSMessage> twtr = new TaskWithTimeoutRunner<HTSMessage>(TIMEOUT);
            TaskWithTimeoutResult<HTSMessage> twtRes = await twtr.RunWithTimeout(Task.Factory.StartNew<HTSMessage>(() =>
            {
                LoopBackResponseHandler lbrh = new LoopBackResponseHandler();
                _htsConnectionHandler.SendMessage(cancelTimerMessage, lbrh);
                LastRecordingChange = DateTime.UtcNow;
                return lbrh.getResponse();
            }));

            if (twtRes.HasTimeout)
            {
                _logger.LogError("[TVHclient] LiveTvService.CancelTimerAsync: can't cancel timer because the timeout was reached");
            }
            else
            {
                HTSMessage cancelTimerResponse = twtRes.Result;
                Boolean success = cancelTimerResponse.getInt("success", 0) == 1;
                if (!success)
                {
                    if (cancelTimerResponse.containsField("error"))
                    {
                        _logger.LogError("[TVHclient] LiveTvService.CancelTimerAsync: can't cancel timer: '{why}'", cancelTimerResponse.getString("error"));
                    }
                    else if (cancelTimerResponse.containsField("noaccess"))
                    {
                        _logger.LogError("[TVHclient] LiveTvService.CancelTimerAsync: can't cancel timer: '{why}'", cancelTimerResponse.getString("noaccess"));
                    }
                }
            }
        }

        public async Task CloseLiveStream(string subscriptionId, CancellationToken cancellationToken)
        {
            await Task.Factory.StartNew<string>(() =>
            {
                _logger.LogDebug("[TVHclient] LiveTvService.CloseLiveStream: closed stream for subscriptionId: {id}", subscriptionId);
                return subscriptionId;
            });
        }

        public async Task CreateSeriesTimerAsync(SeriesTimerInfo info, CancellationToken cancellationToken)
        {
            // Dummy method to avoid warnings
            await Task.Factory.StartNew<int>(() => { return 0; });

            throw new NotImplementedException();


            //int timeOut = await WaitForInitialLoadTask(cancellationToken);
            //if (timeOut == -1 || cancellationToken.IsCancellationRequested)
            //{
            //    _logger.LogDebug("[TVHclient] LiveTvService.CreateSeriesTimerAsync: call cancelled or timed out - returning empty list");
            //    return;
            //}

            ////_logger.LogDebug("[TVHclient] LiveTvService.CreateSeriesTimerAsync: got SeriesTimerInfo: {spam}", dump(info));

            //HTSMessage createSeriesTimerMessage = new HTSMessage();
            //createSeriesTimerMessage.Method = "addAutorecEntry";
            //createSeriesTimerMessage.putField("title", info.Name);
            //if (!info.RecordAnyChannel)
            //{
            //    createSeriesTimerMessage.putField("channelId", info.ChannelId);
            //}
            //createSeriesTimerMessage.putField("minDuration", 0);
            //createSeriesTimerMessage.putField("maxDuration", 0);

            //int tempPriority = info.Priority;
            //if (tempPriority == 0)
            //{
            //    tempPriority = _priority; // info.Priority delivers 0 if timers is newly created - no GUI
            //}
            //createSeriesTimerMessage.putField("priority", tempPriority);
            //createSeriesTimerMessage.putField("configName", _profile);
            //createSeriesTimerMessage.putField("daysOfWeek", AutorecDataHelper.getDaysOfWeekFromList(info.Days));

            //if (!info.RecordAnyTime)
            //{
            //    createSeriesTimerMessage.putField("approxTime", AutorecDataHelper.getMinutesFromMidnight(info.StartDate));
            //}
            //createSeriesTimerMessage.putField("startExtra", (long)(info.PrePaddingSeconds / 60L));
            //createSeriesTimerMessage.putField("stopExtra", (long)(info.PostPaddingSeconds / 60L));
            //createSeriesTimerMessage.putField("comment", info.Overview);


            ////_logger.LogDebug("[TVHclient] LiveTvService.CreateSeriesTimerAsync: created HTSP message: {msg}", createSeriesTimerMessage.ToString());


            ///*
            //        public DateTime EndDate { get; set; }
            //        public string ProgramId { get; set; }
            //        public bool RecordNewOnly { get; set; }
            // */

            ////HTSMessage createSeriesTimerResponse = await Task.Factory.StartNew<HTSMessage>(() =>
            ////{
            ////    LoopBackResponseHandler lbrh = new LoopBackResponseHandler();
            ////    _htsConnection.sendMessage(createSeriesTimerMessage, lbrh);
            ////    return lbrh.getResponse();
            ////});

            //TaskWithTimeoutRunner<HTSMessage> twtr = new TaskWithTimeoutRunner<HTSMessage>(TIMEOUT);
            //TaskWithTimeoutResult<HTSMessage> twtRes = await  twtr.RunWithTimeout(Task.Factory.StartNew<HTSMessage>(() =>
            //{
            //    LoopBackResponseHandler lbrh = new LoopBackResponseHandler();
            //    _htsConnection.sendMessage(createSeriesTimerMessage, lbrh);
            //    return lbrh.getResponse();
            //}));

            //if (twtRes.HasTimeout)
            //{
            //    _logger.LogError("[TVHclient] LiveTvService.CreateSeriesTimerAsync: can't create series because the timeout was reached");
            //}
            //else
            //{
            //    HTSMessage createSeriesTimerResponse = twtRes.Result;
            //    Boolean success = createSeriesTimerResponse.getInt("success", 0) == 1;
            //    if (!success)
            //    {
            //        _logger.LogError("[TVHclient] LiveTvService.CreateSeriesTimerAsync: can't create series timer: '{why}'", createSeriesTimerResponse.getString("error"));
            //    }
            //}
        }

        public async Task CreateTimerAsync(TimerInfo info, CancellationToken cancellationToken)
        {
            int timeOut = await WaitForInitialLoadTask(cancellationToken);
            if (timeOut == -1 || cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug("[TVHclient] LiveTvService.CreateTimerAsync: call cancelled or timed out");
                return;
            }

            HTSMessage createTimerMessage = new HTSMessage();
            createTimerMessage.Method = "addDvrEntry";
            createTimerMessage.putField("channelId", info.ChannelId);
            createTimerMessage.putField("start", DateTimeHelper.getUnixUTCTimeFromUtcDateTime(info.StartDate));
            createTimerMessage.putField("stop", DateTimeHelper.getUnixUTCTimeFromUtcDateTime(info.EndDate));
            createTimerMessage.putField("startExtra", (long)(info.PrePaddingSeconds / 60));
            createTimerMessage.putField("stopExtra", (long)(info.PostPaddingSeconds / 60));
            createTimerMessage.putField("priority", _htsConnectionHandler.GetPriority()); // info.Priority delivers always 0 - no GUI
            createTimerMessage.putField("configName", _htsConnectionHandler.GetProfile());
            createTimerMessage.putField("description", info.Overview);
            createTimerMessage.putField("title", info.Name);
            createTimerMessage.putField("creator", Plugin.Instance.Configuration.Username);

            TaskWithTimeoutRunner<HTSMessage> twtr = new TaskWithTimeoutRunner<HTSMessage>(TIMEOUT);
            TaskWithTimeoutResult<HTSMessage> twtRes = await twtr.RunWithTimeout(Task.Factory.StartNew<HTSMessage>(() =>
            {
                LoopBackResponseHandler lbrh = new LoopBackResponseHandler();
                _htsConnectionHandler.SendMessage(createTimerMessage, lbrh);
                return lbrh.getResponse();
            }));

            if (twtRes.HasTimeout)
            {
                _logger.LogError("[TVHclient] LiveTvService.CreateTimerAsync: can't create timer because the timeout was reached");
            }
            else
            {
                HTSMessage createTimerResponse = twtRes.Result;
                Boolean success = createTimerResponse.getInt("success", 0) == 1;
                if (!success)
                {
                    if (createTimerResponse.containsField("error"))
                    {
                        _logger.LogError("[TVHclient] LiveTvService.CreateTimerAsync: can't create timer: '{why}'", createTimerResponse.getString("error"));
                    }
                    else if (createTimerResponse.containsField("noaccess"))
                    {
                        _logger.LogError("[TVHclient] LiveTvService.CreateTimerAsync: can't create timer: '{why}'", createTimerResponse.getString("noaccess"));
                    }
                }
            }
        }

        public async Task DeleteRecordingAsync(string recordingId, CancellationToken cancellationToken)
        {
            int timeOut = await WaitForInitialLoadTask(cancellationToken);
            if (timeOut == -1 || cancellationToken.IsCancellationRequested)
            {
                _logger.LogError("[TVHclient] LiveTvService.DeleteRecordingAsync: call cancelled or timed out");
                return;
            }

            HTSMessage deleteRecordingMessage = new HTSMessage();
            deleteRecordingMessage.Method = "deleteDvrEntry";
            deleteRecordingMessage.putField("id", recordingId);

            TaskWithTimeoutRunner<HTSMessage> twtr = new TaskWithTimeoutRunner<HTSMessage>(TIMEOUT);
            TaskWithTimeoutResult<HTSMessage> twtRes = await twtr.RunWithTimeout(Task.Factory.StartNew<HTSMessage>(() =>
            {
                LoopBackResponseHandler lbrh = new LoopBackResponseHandler();
                _htsConnectionHandler.SendMessage(deleteRecordingMessage, lbrh);
                LastRecordingChange = DateTime.UtcNow;
                return lbrh.getResponse();
            }));

            if (twtRes.HasTimeout)
            {
                _logger.LogError("[TVHclient] LiveTvService.DeleteRecordingAsync: can't delete recording because the timeout was reached");
            }
            else
            {
                HTSMessage deleteRecordingResponse = twtRes.Result;
                Boolean success = deleteRecordingResponse.getInt("success", 0) == 1;
                if (!success)
                {
                    if (deleteRecordingResponse.containsField("error"))
                    {
                        _logger.LogError("[TVHclient] LiveTvService.DeleteRecordingAsync: can't delete recording: '{why}'", deleteRecordingResponse.getString("error"));
                    }
                    else if (deleteRecordingResponse.containsField("noaccess"))
                    {
                        _logger.LogError("[TVHclient] LiveTvService.DeleteRecordingAsync: can't delete recording: '{why}'", deleteRecordingResponse.getString("noaccess"));
                    }
                }
            }
        }

        public async Task<IEnumerable<ChannelInfo>> GetChannelsAsync(CancellationToken cancellationToken)
        {
            int timeOut = await WaitForInitialLoadTask(cancellationToken);
            if (timeOut == -1 || cancellationToken.IsCancellationRequested)
            {
                _logger.LogError("[TVHclient] LiveTvService.GetChannelsAsync: call cancelled or timed out - returning empty list");
                return new List<ChannelInfo>();
            }

            TaskWithTimeoutRunner<IEnumerable<ChannelInfo>> twtr = new TaskWithTimeoutRunner<IEnumerable<ChannelInfo>>(TIMEOUT);
            TaskWithTimeoutResult<IEnumerable<ChannelInfo>> twtRes = await
                twtr.RunWithTimeout(_htsConnectionHandler.BuildChannelInfos(cancellationToken));

            if (twtRes.HasTimeout)
            {
                return new List<ChannelInfo>();
            }

            var list = twtRes.Result.ToList();

            foreach (var channel in list)
            {
                if (string.IsNullOrEmpty(channel.ImageUrl))
                {
                    channel.ImageUrl = _htsConnectionHandler.GetChannelImageUrl(channel.Id);
                }
            }

            return list;
        }

        public async Task<MediaSourceInfo> GetChannelStream(string channelId, string mediaSourceId, CancellationToken cancellationToken)
        {
            var ticket = await _channelTicketHandler.GetTicket(channelId, cancellationToken);

            if (_htsConnectionHandler.GetEnableSubsMaudios())
            {
                _logger.LogInformation("[TVHclient] LiveTvService.GetChannelStream: support for live TV subtitles and multiple audio tracks is enabled");

                MediaSourceInfo livetvasset = new MediaSourceInfo();

                livetvasset.Id = ticket.Id;

                // Use HTTP basic auth in HTTP header instead of TVH ticketing system for authentication to allow the users to switch subs or audio tracks at any time
                livetvasset.Path = _htsConnectionHandler.GetHttpBaseUrl() + ticket.Path;
                livetvasset.Protocol = MediaProtocol.Http;
                livetvasset.RequiredHttpHeaders = _htsConnectionHandler.GetHeaders();

                // Probe the asset stream to determine available sub-streams
                string livetvasset_probeUrl = "" + livetvasset.Path;

                // If enabled, force video deinterlacing for channels
                if (_htsConnectionHandler.GetForceDeinterlace())
                {
                    _logger.LogInformation("[TVHclient] LiveTvService.GetChannelStream: force video deinterlacing for all channels and recordings is enabled");

                    foreach (MediaStream i in livetvasset.MediaStreams)
                    {
                        if (i.Type == MediaStreamType.Video && i.IsInterlaced == false)
                        {
                            i.IsInterlaced = true;
                        }
                    }
                }

                return livetvasset;
            }
            else
            {
                return new MediaSourceInfo
                {
                    Id = ticket.Id,
                    Path = _htsConnectionHandler.GetHttpBaseUrl() + ticket.Path + "?ticket=" + ticket.TicketParam,
                    Protocol = MediaProtocol.Http,
                    MediaStreams = new List<MediaStream>
                    {
                        new MediaStream
                        {
                            Type = MediaStreamType.Video,
                            // Set the index to -1 because we don't know the exact index of the video stream within the container
                            Index = -1,
                            // Set to true if unknown to enable deinterlacing
                            IsInterlaced = true
                        },
                        new MediaStream
                        {
                            Type = MediaStreamType.Audio,
                            // Set the index to -1 because we don't know the exact index of the audio stream within the container
                            Index = -1
                        }
                    }
                };
            }
        }

        public async Task<List<MediaSourceInfo>> GetChannelStreamMediaSources(string channelId, CancellationToken cancellationToken)
        {
            var source = await GetChannelStream(channelId, string.Empty, cancellationToken);
            return new List<MediaSourceInfo>() { source };
        }

        public async Task<SeriesTimerInfo> GetNewTimerDefaultsAsync(CancellationToken cancellationToken, ProgramInfo program = null)
        {
            return await Task.Factory.StartNew<SeriesTimerInfo>(() =>
            {
                return new SeriesTimerInfo
                {
                    PostPaddingSeconds = 0,
                    PrePaddingSeconds = 0,
                    RecordAnyChannel = true,
                    RecordAnyTime = true,
                    RecordNewOnly = false
                };
            });
        }

        public Task<ImageStream> GetProgramImageAsync(string programId, string channelId, CancellationToken cancellationToken)
        {
            // Leave as is. This is handled by supplying image url to ProgramInfo
            throw new NotImplementedException();
        }

        public async Task<IEnumerable<ProgramInfo>> GetProgramsAsync(string channelId, DateTime startDateUtc, DateTime endDateUtc, CancellationToken cancellationToken)
        {
            int timeOut = await WaitForInitialLoadTask(cancellationToken);
            if (timeOut == -1 || cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug("[TVHclient] LiveTvService.GetProgramsAsync: call cancelled or timed out - returning empty list");
                return new List<ProgramInfo>();
            }

            GetEventsResponseHandler currGetEventsResponseHandler = new GetEventsResponseHandler(startDateUtc, endDateUtc, _logger, cancellationToken);

            HTSMessage queryEvents = new HTSMessage();
            queryEvents.Method = "getEvents";
            queryEvents.putField("channelId", Convert.ToInt32(channelId));
            queryEvents.putField("maxTime", ((DateTimeOffset)endDateUtc).ToUnixTimeSeconds());
            _htsConnectionHandler.SendMessage(queryEvents, currGetEventsResponseHandler);

            _logger.LogDebug("[TVHclient] LiveTvService.GetProgramsAsync: ask TVH for events of channel '{chanid}'", channelId);

            TaskWithTimeoutRunner<IEnumerable<ProgramInfo>> twtr = new TaskWithTimeoutRunner<IEnumerable<ProgramInfo>>(TIMEOUT);
            TaskWithTimeoutResult<IEnumerable<ProgramInfo>> twtRes = await
                twtr.RunWithTimeout(currGetEventsResponseHandler.GetEvents(cancellationToken, channelId));

            if (twtRes.HasTimeout)
            {
                _logger.LogDebug("[TVHclient] LiveTvService.GetProgramsAsync: timeout reached while calling for events of channel '{chanid}'", channelId);
                return new List<ProgramInfo>();
            }

            return twtRes.Result;
        }

        public Task<ImageStream> GetRecordingImageAsync(string recordingId, CancellationToken cancellationToken)
        {
            // Leave as is. This is handled by supplying image url to RecordingInfo
            throw new NotImplementedException();
        }

        public async Task<IEnumerable<MyRecordingInfo>> GetAllRecordingsAsync(CancellationToken cancellationToken)
        {
            // retrieve all 'Pending', 'Inprogress' and 'Completed' recordings
            // we don't deliver the 'Pending' recordings

            int timeOut = await WaitForInitialLoadTask(cancellationToken);
            if (timeOut == -1 || cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug("[TVHclient] LiveTvService.GetRecordingsAsync: call cancelled or timed out - returning empty list");
                return new List<MyRecordingInfo>();
            }

            TaskWithTimeoutRunner<IEnumerable<MyRecordingInfo>> twtr = new TaskWithTimeoutRunner<IEnumerable<MyRecordingInfo>>(TIMEOUT);
            TaskWithTimeoutResult<IEnumerable<MyRecordingInfo>> twtRes = await
                twtr.RunWithTimeout(_htsConnectionHandler.BuildDvrInfos(cancellationToken));

            if (twtRes.HasTimeout)
            {
                return new List<MyRecordingInfo>();
            }

            return twtRes.Result;
        }

        private void LogStringList(List<String> theList, String prefix)
        {
            // TODO: Really? doublecheck that this is the way to do it, or if a single call to the logger with everything would be better.
            theList.ForEach(delegate(String s) { _logger.LogDebug("{pfx}{string}", prefix, s); });
        }

        public async Task<MediaSourceInfo> GetRecordingStream(string recordingId, string mediaSourceId, CancellationToken cancellationToken)
        {
            var ticket = await _recordingTicketHandler.GetTicket(recordingId, cancellationToken);

            if (_htsConnectionHandler.GetEnableSubsMaudios())
            {
                _logger.LogInformation("[TVHclient] LiveTvService.GetRecordingStream: support for live TV subtitles and multiple audio tracks is enabled");

                MediaSourceInfo recordingasset = new MediaSourceInfo();

                recordingasset.Id = ticket.Id;

                // Use HTTP basic auth instead of TVH ticketing system for authentication to allow the users to switch subs or audio tracks at any time
                recordingasset.Path = _htsConnectionHandler.GetHttpBaseUrl() + ticket.Path;
                recordingasset.Protocol = MediaProtocol.Http;

                // Set asset source and type for stream probing and logging
                string recordingasset_probeUrl = "" + recordingasset.Path;

                // If enabled, force video deinterlacing for recordings
                if (_htsConnectionHandler.GetForceDeinterlace())
                {
                    _logger.LogInformation("[TVHclient] LiveTvService.GetRecordingStream: force video deinterlacing for all channels and recordings is enabled");

                    foreach (MediaStream i in recordingasset.MediaStreams)
                    {
                        if (i.Type == MediaStreamType.Video && i.IsInterlaced == false)
                        {
                            i.IsInterlaced = true;
                        }
                    }
                }

                return recordingasset;
            }
            else
            {
                return new MediaSourceInfo
                {
                    Id = ticket.Id,
                    Path = _htsConnectionHandler.GetHttpBaseUrl() + ticket.Path + "?ticket=" + ticket.TicketParam,
                    Protocol = MediaProtocol.Http,
                    MediaStreams = new List<MediaStream>
                    {
                        new MediaStream
                        {
                            Type = MediaStreamType.Video,
                            // Set the index to -1 because we don't know the exact index of the video stream within the container
                            Index = -1,
                            // Set to true if unknown to enable deinterlacing
                            IsInterlaced = true
                        },
                        new MediaStream
                        {
                            Type = MediaStreamType.Audio,
                            // Set the index to -1 because we don't know the exact index of the audio stream within the container
                            Index = -1
                        }
                    }
                };
            }
        }

        public async Task<List<MediaSourceInfo>> GetRecordingStreamMediaSources(string recordingId, CancellationToken cancellationToken)
        {
            var source = await GetRecordingStream(recordingId, string.Empty, cancellationToken);
            return new List<MediaSourceInfo>() { source };
        }

        public async Task<IEnumerable<SeriesTimerInfo>> GetSeriesTimersAsync(CancellationToken cancellationToken)
        {
            int timeOut = await WaitForInitialLoadTask(cancellationToken);
            if (timeOut == -1 || cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug("[TVHclient] LiveTvService.GetSeriesTimersAsync: call cancelled ot timed out - returning empty list");
                return new List<SeriesTimerInfo>();
            }

            TaskWithTimeoutRunner<IEnumerable<SeriesTimerInfo>> twtr = new TaskWithTimeoutRunner<IEnumerable<SeriesTimerInfo>>(TIMEOUT);
            TaskWithTimeoutResult<IEnumerable<SeriesTimerInfo>> twtRes = await
                twtr.RunWithTimeout(_htsConnectionHandler.BuildAutorecInfos(cancellationToken));

            if (twtRes.HasTimeout)
            {
                return new List<SeriesTimerInfo>();
            }

            return twtRes.Result;
        }

        public async Task<LiveTvServiceStatusInfo> GetStatusInfoAsync(CancellationToken cancellationToken)
        {
            int timeOut = await WaitForInitialLoadTask(cancellationToken);
            if (timeOut == -1 || cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug("[TVHclient] LiveTvService.GetStatusInfoAsync: call cancelled or timed out");
                return new LiveTvServiceStatusInfo
                {
                    Status = LiveTvServiceStatus.Unavailable
                };
            }

            string serverName = _htsConnectionHandler.GetServername();
            string serverVersion = _htsConnectionHandler.GetServerVersion();
            int serverProtocolVersion = _htsConnectionHandler.GetServerProtocolVersion();
            string diskSpace = _htsConnectionHandler.GetDiskSpace();

            int usedHTSPversion = (serverProtocolVersion < (int)HTSMessage.HTSP_VERSION) ? serverProtocolVersion : (int)HTSMessage.HTSP_VERSION;

            string serverVersionMessage = "<p>" + serverName + " " + serverVersion + "</p>"
                + "<p>HTSP protocol version: " + usedHTSPversion + "</p>"
                + "<p>Free diskspace: " + diskSpace + "</p>";

            //TaskWithTimeoutRunner<List<LiveTvTunerInfo>> twtr = new TaskWithTimeoutRunner<List<LiveTvTunerInfo>>(TIMEOUT);
            //TaskWithTimeoutResult<List<LiveTvTunerInfo>> twtRes = await
            //    twtr.RunWithTimeout(_tunerDataHelper.buildTunerInfos(cancellationToken));

            List<LiveTvTunerInfo> tvTunerInfos;
            //if (twtRes.HasTimeout)
            //{
            tvTunerInfos = new List<LiveTvTunerInfo>();
            //} else
            //{
            //    tvTunerInfos = twtRes.Result;
            //}

            return new LiveTvServiceStatusInfo
            {
                Version = serverVersionMessage,
                Tuners = tvTunerInfos,
                Status = LiveTvServiceStatus.Ok,
            };
        }

        public async Task<IEnumerable<TimerInfo>> GetTimersAsync(CancellationToken cancellationToken)
        {
            //  retrieve the 'Pending' recordings");

            int timeOut = await WaitForInitialLoadTask(cancellationToken);
            if (timeOut == -1 || cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug("[TVHclient] LiveTvService.GetTimersAsync: call cancelled or timed out - returning empty list");
                return new List<TimerInfo>();
            }

            TaskWithTimeoutRunner<IEnumerable<TimerInfo>> twtr = new TaskWithTimeoutRunner<IEnumerable<TimerInfo>>(TIMEOUT);
            TaskWithTimeoutResult<IEnumerable<TimerInfo>> twtRes = await
                twtr.RunWithTimeout(_htsConnectionHandler.BuildPendingTimersInfos(cancellationToken));

            if (twtRes.HasTimeout)
            {
                return new List<TimerInfo>();
            }

            return twtRes.Result;
        }

        public Task RecordLiveStream(string id, CancellationToken cancellationToken)
        {
            _logger.LogDebug("[TVHclient] LiveTvService.RecordLiveStream: id {id}", id);

            throw new NotImplementedException();
        }

        public Task ResetTuner(string id, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public async Task UpdateSeriesTimerAsync(SeriesTimerInfo info, CancellationToken cancellationToken)
        {
            await CancelSeriesTimerAsync(info.Id, cancellationToken);
            LastRecordingChange = DateTime.UtcNow;
            // TODO add if method is implemented
            // await CreateSeriesTimerAsync(info, cancellationToken);
        }

        public async Task UpdateTimerAsync(TimerInfo info, CancellationToken cancellationToken)
        {
            int timeOut = await WaitForInitialLoadTask(cancellationToken);
            if (timeOut == -1 || cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug("[TVHclient] LiveTvService.UpdateTimerAsync: call cancelled or timed out");
                return;
            }

            HTSMessage updateTimerMessage = new HTSMessage();
            updateTimerMessage.Method = "updateDvrEntry";
            updateTimerMessage.putField("id", info.Id);
            updateTimerMessage.putField("startExtra", (long)(info.PrePaddingSeconds / 60));
            updateTimerMessage.putField("stopExtra", (long)(info.PostPaddingSeconds / 60));

            TaskWithTimeoutRunner<HTSMessage> twtr = new TaskWithTimeoutRunner<HTSMessage>(TIMEOUT);
            TaskWithTimeoutResult<HTSMessage> twtRes = await twtr.RunWithTimeout(Task.Factory.StartNew<HTSMessage>(() =>
            {
                LoopBackResponseHandler lbrh = new LoopBackResponseHandler();
                _htsConnectionHandler.SendMessage(updateTimerMessage, lbrh);
                LastRecordingChange = DateTime.UtcNow;
                return lbrh.getResponse();
            }));

            if (twtRes.HasTimeout)
            {
                _logger.LogError("[TVHclient] LiveTvService.UpdateTimerAsync: can't update timer because the timeout was reached");
            }
            else
            {
                HTSMessage updateTimerResponse = twtRes.Result;
                Boolean success = updateTimerResponse.getInt("success", 0) == 1;
                if (!success)
                {
                    if (updateTimerResponse.containsField("error"))
                    {
                        _logger.LogError("[TVHclient] LiveTvService.UpdateTimerAsync: can't update timer: '{why}'", updateTimerResponse.getString("error"));
                    }
                    else if (updateTimerResponse.containsField("noaccess"))
                    {
                        _logger.LogError("[TVHclient] LiveTvService.UpdateTimerAsync: can't update timer: '{why}'", updateTimerResponse.getString("noaccess"));
                    }
                }
            }
        }

        /***********/
        /* Helpers */
        /***********/

        private Task<int> WaitForInitialLoadTask(CancellationToken cancellationToken)
        {
            return Task.Factory.StartNew<int>(() => _htsConnectionHandler.WaitForInitialLoad(cancellationToken));
        }

        private string dump(SeriesTimerInfo sti)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("\n<SeriesTimerInfo>\n");
            sb.Append("  Id:                    " + sti.Id + "\n");
            sb.Append("  Name:                  " + sti.Name + "\n");
            sb.Append("  Overview:              " + sti.Overview + "\n");
            sb.Append("  Priority:              " + sti.Priority + "\n");
            sb.Append("  ChannelId:             " + sti.ChannelId + "\n");
            sb.Append("  ProgramId:             " + sti.ProgramId + "\n");
            sb.Append("  Days:                  " + dump(sti.Days) + "\n");
            sb.Append("  StartDate:             " + sti.StartDate + "\n");
            sb.Append("  EndDate:               " + sti.EndDate + "\n");
            sb.Append("  IsPrePaddingRequired:  " + sti.IsPrePaddingRequired + "\n");
            sb.Append("  PrePaddingSeconds:     " + sti.PrePaddingSeconds + "\n");
            sb.Append("  IsPostPaddingRequired: " + sti.IsPrePaddingRequired + "\n");
            sb.Append("  PostPaddingSeconds:    " + sti.PostPaddingSeconds + "\n");
            sb.Append("  RecordAnyChannel:      " + sti.RecordAnyChannel + "\n");
            sb.Append("  RecordAnyTime:         " + sti.RecordAnyTime + "\n");
            sb.Append("  RecordNewOnly:         " + sti.RecordNewOnly + "\n");
            sb.Append("</SeriesTimerInfo>\n");
            return sb.ToString();
        }

        private string dump(List<DayOfWeek> days)
        {
            StringBuilder sb = new StringBuilder();
            foreach (DayOfWeek dow in days)
            {
                sb.Append(dow + ", ");
            }
            string tmpResult = sb.ToString();
            if (tmpResult.EndsWith(","))
            {
                tmpResult = tmpResult.Substring(0, tmpResult.Length - 2);
            }
            return tmpResult;
        }

        /*
        public async Task CopyFilesAsync(StreamReader source, StreamWriter destination)
        {
            char[] buffer = new char[0x1000];
            int numRead;
            while ((numRead = await source.ReadAsync(buffer, 0, buffer.Length)) != 0)
            {
                await destination.WriteAsync(buffer, 0, numRead);
            }
        }
        */
    }

}
