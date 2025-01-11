using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
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
        private readonly IMediaEncoder _mediaEncoder;

        private readonly TimeSpan _timeout = TimeSpan.FromMinutes(5);

        private readonly HTSConnectionHandler _htsConnectionHandler;
        private readonly AccessTicketHandler _channelTicketHandler;

        private readonly ILogger<LiveTvService> _logger;
        public DateTime _lastRecordingChange = DateTime.MinValue;

        public LiveTvService(ILoggerFactory loggerFactory, IMediaEncoder mediaEncoder, HTSConnectionHandler connectionHandler)
        {
            //System.Diagnostics.StackTrace t = new System.Diagnostics.StackTrace();
            _logger = loggerFactory.CreateLogger<LiveTvService>();
            _logger.LogDebug("LiveTvService()");

            _htsConnectionHandler = connectionHandler;
            _htsConnectionHandler.setLiveTvService(this);

            {
                var lifeSpan = TimeSpan.FromSeconds(15);       // Revalidate tickets every 15 seconds
                var requestTimeout = TimeSpan.FromSeconds(10); // First request retry after 10 seconds
                var retries = 2;                               // Number of times to retry getting tickets
                _channelTicketHandler = new AccessTicketHandler(loggerFactory, _htsConnectionHandler, requestTimeout, retries, lifeSpan, Channel);
            }

            //Added for stream probing
            _mediaEncoder = mediaEncoder;
        }

        public string HomePageUrl { get { return "http://tvheadend.org/"; } }

        public string Name { get { return "TVHclient LiveTvService"; } }

        public async Task CancelSeriesTimerAsync(string timerId, CancellationToken cancellationToken)
        {
            int timeOut = await WaitForInitialLoadTask(cancellationToken);
            if (timeOut == -1 || cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug("LiveTvService.CancelSeriesTimerAsync: call cancelled or timed out");
                return;
            }

            HTSMessage deleteAutorecMessage = new HTSMessage();
            deleteAutorecMessage.Method = "deleteAutorecEntry";
            deleteAutorecMessage.putField("id", timerId);

            TaskWithTimeoutRunner<HTSMessage> twtr = new TaskWithTimeoutRunner<HTSMessage>(_timeout);
            TaskWithTimeoutResult<HTSMessage> twtRes = await twtr.RunWithTimeout(Task.Factory.StartNew(() =>
            {
                LoopBackResponseHandler lbrh = new LoopBackResponseHandler();
                _htsConnectionHandler.SendMessage(deleteAutorecMessage, lbrh);
                _lastRecordingChange = DateTime.UtcNow;
                return lbrh.getResponse();
            }, cancellationToken));

            if (twtRes.HasTimeout)
            {
                _logger.LogError("LiveTvService.CancelSeriesTimerAsync: can't delete recording because the timeout was reached");
            }
            else
            {
                HTSMessage deleteAutorecResponse = twtRes.Result;
                Boolean success = deleteAutorecResponse.getInt("success", 0) == 1;
                if (!success)
                {
                    if (deleteAutorecResponse.containsField("error"))
                    {
                        _logger.LogError("LiveTvService.CancelSeriesTimerAsync: can't delete recording: '{why}'", deleteAutorecResponse.getString("error"));
                    }
                    else if (deleteAutorecResponse.containsField("noaccess"))
                    {
                        _logger.LogError("LiveTvService.CancelSeriesTimerAsync: can't delete recording: '{why}'", deleteAutorecResponse.getString("noaccess"));
                    }
                }
            }
        }

        public async Task CancelTimerAsync(string timerId, CancellationToken cancellationToken)
        {
            int timeOut = await WaitForInitialLoadTask(cancellationToken);
            if (timeOut == -1 || cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug("LiveTvService.CancelTimerAsync: call cancelled or timed out");
                return;
            }

            HTSMessage cancelTimerMessage = new HTSMessage();
            cancelTimerMessage.Method = "cancelDvrEntry";
            cancelTimerMessage.putField("id", timerId);

            TaskWithTimeoutRunner<HTSMessage> twtr = new TaskWithTimeoutRunner<HTSMessage>(_timeout);
            TaskWithTimeoutResult<HTSMessage> twtRes = await twtr.RunWithTimeout(Task.Factory.StartNew(() =>
            {
                LoopBackResponseHandler lbrh = new LoopBackResponseHandler();
                _htsConnectionHandler.SendMessage(cancelTimerMessage, lbrh);
                _lastRecordingChange = DateTime.UtcNow;
                return lbrh.getResponse();
            }, cancellationToken));

            if (twtRes.HasTimeout)
            {
                _logger.LogError("LiveTvService.CancelTimerAsync: can't cancel timer because the timeout was reached");
            }
            else
            {
                HTSMessage cancelTimerResponse = twtRes.Result;
                Boolean success = cancelTimerResponse.getInt("success", 0) == 1;
                if (!success)
                {
                    if (cancelTimerResponse.containsField("error"))
                    {
                        _logger.LogError("LiveTvService.CancelTimerAsync: can't cancel timer: '{why}'", cancelTimerResponse.getString("error"));
                    }
                    else if (cancelTimerResponse.containsField("noaccess"))
                    {
                        _logger.LogError("LiveTvService.CancelTimerAsync: can't cancel timer: '{why}'", cancelTimerResponse.getString("noaccess"));
                    }
                }
            }
        }

        public async Task CloseLiveStream(string subscriptionId, CancellationToken cancellationToken)
        {
            await Task.Factory.StartNew(() =>
            {
                _logger.LogDebug("LiveTvService.CloseLiveStream: closed stream for subscriptionId: {id}", subscriptionId);
                return subscriptionId;
            }, cancellationToken);
        }

        public async Task CreateSeriesTimerAsync(SeriesTimerInfo info, CancellationToken cancellationToken)
        {
            // Dummy method to avoid warnings
            await Task.Factory.StartNew(() => 0, cancellationToken);

            throw new NotImplementedException();
        }

        public async Task CreateTimerAsync(TimerInfo info, CancellationToken cancellationToken)
        {
            int timeOut = await WaitForInitialLoadTask(cancellationToken);
            if (timeOut == -1 || cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug("LiveTvService.CreateTimerAsync: call cancelled or timed out");
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

            TaskWithTimeoutRunner<HTSMessage> twtr = new TaskWithTimeoutRunner<HTSMessage>(_timeout);
            TaskWithTimeoutResult<HTSMessage> twtRes = await twtr.RunWithTimeout(Task.Factory.StartNew(() =>
            {
                LoopBackResponseHandler lbrh = new LoopBackResponseHandler();
                _htsConnectionHandler.SendMessage(createTimerMessage, lbrh);
                return lbrh.getResponse();
            }, cancellationToken));

            if (twtRes.HasTimeout)
            {
                _logger.LogError("LiveTvService.CreateTimerAsync: can't create timer because the timeout was reached");
            }
            else
            {
                HTSMessage createTimerResponse = twtRes.Result;
                Boolean success = createTimerResponse.getInt("success", 0) == 1;
                if (!success)
                {
                    if (createTimerResponse.containsField("error"))
                    {
                        _logger.LogError("LiveTvService.CreateTimerAsync: can't create timer: '{why}'", createTimerResponse.getString("error"));
                    }
                    else if (createTimerResponse.containsField("noaccess"))
                    {
                        _logger.LogError("LiveTvService.CreateTimerAsync: can't create timer: '{why}'", createTimerResponse.getString("noaccess"));
                    }
                }
            }
        }

        public async Task DeleteRecordingAsync(string recordingId, CancellationToken cancellationToken)
        {
            int timeOut = await WaitForInitialLoadTask(cancellationToken);
            if (timeOut == -1 || cancellationToken.IsCancellationRequested)
            {
                _logger.LogError("LiveTvService.DeleteRecordingAsync: call cancelled or timed out");
                return;
            }

            HTSMessage deleteRecordingMessage = new HTSMessage();
            deleteRecordingMessage.Method = "deleteDvrEntry";
            deleteRecordingMessage.putField("id", recordingId);

            TaskWithTimeoutRunner<HTSMessage> twtr = new TaskWithTimeoutRunner<HTSMessage>(_timeout);
            TaskWithTimeoutResult<HTSMessage> twtRes = await twtr.RunWithTimeout(Task.Factory.StartNew(() =>
            {
                LoopBackResponseHandler lbrh = new LoopBackResponseHandler();
                _htsConnectionHandler.SendMessage(deleteRecordingMessage, lbrh);
                _lastRecordingChange = DateTime.UtcNow;
                return lbrh.getResponse();
            }, cancellationToken));

            if (twtRes.HasTimeout)
            {
                _logger.LogError("LiveTvService.DeleteRecordingAsync: can't delete recording because the timeout was reached");
            }
            else
            {
                HTSMessage deleteRecordingResponse = twtRes.Result;
                Boolean success = deleteRecordingResponse.getInt("success", 0) == 1;
                if (!success)
                {
                    if (deleteRecordingResponse.containsField("error"))
                    {
                        _logger.LogError("LiveTvService.DeleteRecordingAsync: can't delete recording: '{why}'", deleteRecordingResponse.getString("error"));
                    }
                    else if (deleteRecordingResponse.containsField("noaccess"))
                    {
                        _logger.LogError("LiveTvService.DeleteRecordingAsync: can't delete recording: '{why}'", deleteRecordingResponse.getString("noaccess"));
                    }
                }
            }
        }

        public async Task<IEnumerable<ChannelInfo>> GetChannelsAsync(CancellationToken cancellationToken)
        {
            int timeOut = await WaitForInitialLoadTask(cancellationToken);
            if (timeOut == -1 || cancellationToken.IsCancellationRequested)
            {
                _logger.LogError("LiveTvService.GetChannelsAsync: call cancelled or timed out - returning empty list");
                return new List<ChannelInfo>();
            }

            TaskWithTimeoutRunner<IEnumerable<ChannelInfo>> twtr = new TaskWithTimeoutRunner<IEnumerable<ChannelInfo>>(_timeout);
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
                _logger.LogInformation("LiveTvService.GetChannelStream: support for live TV subtitles and multiple audio tracks is enabled");

                MediaSourceInfo livetvasset = new MediaSourceInfo();

                livetvasset.Id = channelId;

                // Use HTTP basic auth in HTTP header instead of TVH ticketing system for authentication to allow the users to switch subs or audio tracks at any time
                livetvasset.Path = _htsConnectionHandler.GetHttpBaseUrl() + ticket.Path;
                livetvasset.Protocol = MediaProtocol.Http;
                livetvasset.RequiredHttpHeaders = _htsConnectionHandler.GetHeaders();
                livetvasset.AnalyzeDurationMs = 2000;
                livetvasset.SupportsDirectStream = false;
                livetvasset.RequiresClosing = true;
                livetvasset.SupportsProbing = false;
                livetvasset.Container = "mpegts";
                livetvasset.RequiresOpening = true;
                livetvasset.IsInfiniteStream  = true;

                // Probe the asset stream to determine available sub-streams
                string livetvasset_probeUrl = "" + livetvasset.Path;
                string livetvasset_source = "LiveTV";
                await ProbeStream(livetvasset, livetvasset_probeUrl, livetvasset_source, cancellationToken);

                // If enabled, force video deinterlacing for channels
                if (_htsConnectionHandler.GetForceDeinterlace())
                {
                    _logger.LogInformation("LiveTvService.GetChannelStream: force video deinterlacing for all channels and recordings is enabled");

                    foreach (MediaStream i in livetvasset.MediaStreams)
                    {
                        if (i.Type == MediaStreamType.Video && i.IsInterlaced == false)
                        {
                            i.IsInterlaced = true;
                        }
                        i.RealFrameRate = 50.0F;
                    }
                }

                return livetvasset;
            }
            else
            {
                return new MediaSourceInfo
                {
                    Id = channelId,
                    Path = _htsConnectionHandler.GetHttpBaseUrl() + ticket.Url,
                    Protocol = MediaProtocol.Http,
                    AnalyzeDurationMs = 2000,
                    SupportsDirectStream = false,
                    SupportsProbing = false,
                    Container = "mpegts",
                    MediaStreams = new List<MediaStream>
                    {
                        new MediaStream
                        {
                            Type = MediaStreamType.Video,
                            // Set the index to -1 because we don't know the exact index of the video stream within the container
                            Index = -1,
                            // Set to true if unknown to enable deinterlacing
                            IsInterlaced = true,
                            RealFrameRate = 50.0F
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

        private async Task ProbeStream(MediaSourceInfo mediaSourceInfo, string probeUrl, string source, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Probe stream for {source}", source);
            _logger.LogInformation("Probe URL: {probeUrl}", probeUrl);

            MediaInfoRequest req = new MediaInfoRequest
            {
                MediaType = MediaBrowser.Model.Dlna.DlnaProfileType.Video,
                MediaSource = mediaSourceInfo,
                ExtractChapters = false,
            };

            var originalRuntime = mediaSourceInfo.RunTimeTicks;
            Stopwatch stopWatch = new Stopwatch();
            stopWatch.Start();
            MediaInfo info = await _mediaEncoder.GetMediaInfo(req, cancellationToken).ConfigureAwait(false);
            stopWatch.Stop();
            TimeSpan ts = stopWatch.Elapsed;
            string elapsedTime = $"{ts.Hours:00}:{ts.Minutes:00}:{ts.Seconds:00}.{ts.Milliseconds / 10:00}";
            _logger.LogDebug("Probe RunTime {ElapsedTime}", elapsedTime);

            if (info != null)
            {
                _logger.LogDebug("Probe returned:");

                mediaSourceInfo.Bitrate = info.Bitrate;
                _logger.LogDebug("        BitRate:                    {BitRate}", info.Bitrate);

                mediaSourceInfo.Container = info.Container;
                _logger.LogDebug("        Container:                  {Container}", info.Container);

                mediaSourceInfo.MediaStreams = info.MediaStreams;
                _logger.LogDebug("        MediaStreams:               ");
                LogMediaStreamList(info.MediaStreams, "                       ");

                mediaSourceInfo.RunTimeTicks = info.RunTimeTicks;
                _logger.LogDebug("        RunTimeTicks:               {RunTimeTicks}", info.RunTimeTicks);

                mediaSourceInfo.Size = info.Size;
                _logger.LogDebug("        Size:                       {Size}", info.Size);

                mediaSourceInfo.Timestamp = info.Timestamp;
                _logger.LogDebug("        Timestamp:                  {Timestamp}", info.Timestamp);

                mediaSourceInfo.Video3DFormat = info.Video3DFormat;
                _logger.LogDebug("        Video3DFormat:              {Video3DFormat}", info.Video3DFormat);

                mediaSourceInfo.VideoType = info.VideoType;
                _logger.LogDebug("        VideoType:                  {VideoType}", info.VideoType);

                mediaSourceInfo.RequiresClosing = true;
                _logger.LogDebug("        RequiresClosing:            {RequiresClosing}", info.RequiresClosing);

                mediaSourceInfo.RequiresOpening = true;
                _logger.LogDebug("        RequiresOpening:            {RequiresOpening}", info.RequiresOpening);

                mediaSourceInfo.SupportsDirectPlay = true;
                _logger.LogDebug("        SupportsDirectPlay:         {SupportsDirectPlay}", info.SupportsDirectPlay);

                mediaSourceInfo.SupportsDirectStream = true;
                _logger.LogDebug("        SupportsDirectStream:       {SupportsDirectStream}", info.SupportsDirectStream);

                mediaSourceInfo.SupportsTranscoding = true;
                _logger.LogDebug("        SupportsTranscoding:        {SupportsTranscoding}", info.SupportsTranscoding);

                mediaSourceInfo.DefaultSubtitleStreamIndex = null;
                _logger.LogDebug("        DefaultSubtitleStreamIndex: n/a");

                if (!originalRuntime.HasValue)
                {
                    mediaSourceInfo.RunTimeTicks = null;
                    _logger.LogDebug("        Original runtime:           n/a");
                }

                var audioStream = mediaSourceInfo.MediaStreams.FirstOrDefault(i => i.Type == MediaStreamType.Audio);
                if (audioStream == null || audioStream.Index == -1)
                {
                    mediaSourceInfo.DefaultAudioStreamIndex = null;
                    _logger.LogDebug("        DefaultAudioStreamIndex:    n/a");
                }
                else
                {
                    mediaSourceInfo.DefaultAudioStreamIndex = audioStream.Index;
                    _logger.LogDebug("        DefaultAudioStreamIndex:    '{DefaultAudioStreamIndex}'", info.DefaultAudioStreamIndex);
                }
            }
            else
            {
                _logger.LogError("Cannot probe {source} stream", source);
            }
        }

        private void LogMediaStreamList(IReadOnlyList<MediaStream> theList, string prefix)
        {
            foreach (MediaStream i in theList)
                LogMediaStream(i, prefix);
        }

        private void LogMediaStream(MediaStream ms, string prefix)
        {
            _logger.LogDebug("{Prefix}AspectRatio             {AspectRatio}", prefix, ms.AspectRatio);
            _logger.LogDebug("{Prefix}AverageFrameRate        {AverageFrameRate}", prefix, ms.AverageFrameRate);
            _logger.LogDebug("{Prefix}BitDepth                {BitDepth}", prefix, ms.BitDepth);
            _logger.LogDebug("{Prefix}BitRate                 {BitRate}", prefix, ms.BitRate);
            _logger.LogDebug("{Prefix}ChannelLayout           {ChannelLayout}", prefix, ms.ChannelLayout); // Object
            _logger.LogDebug("{Prefix}Channels                {Channels}", prefix, ms.Channels);
            _logger.LogDebug("{Prefix}Codec                   {Codec}", prefix, ms.Codec); // Object
            _logger.LogDebug("{Prefix}CodecTag                {CodecTag}", prefix, ms.CodecTag); // Object
            _logger.LogDebug("{Prefix}Comment                 {Comment}", prefix, ms.Comment);
            _logger.LogDebug("{Prefix}DeliveryMethod          {DeliveryMethod}", prefix, ms.DeliveryMethod); // Object
            _logger.LogDebug("{Prefix}DeliveryUrl             {DeliveryUrl}", prefix, ms.DeliveryUrl);
            //_logger.LogDebug("{Prefix}ExternalId              {ExternalId}", prefix, ms.ExternalId);
            _logger.LogDebug("{Prefix}Height                  {Height}", prefix, ms.Height);
            _logger.LogDebug("{Prefix}Index                   {Index}", prefix, ms.Index);
            _logger.LogDebug("{Prefix}IsAnamorphic            {IsAnamorphic}", prefix, ms.IsAnamorphic);
            _logger.LogDebug("{Prefix}IsDefault               {IsDefault}", prefix, ms.IsDefault);
            _logger.LogDebug("{Prefix}IsExternal              {IsExternal}", prefix, ms.IsExternal);
            _logger.LogDebug("{Prefix}IsExternalUrl           {IsExternalUrl}", prefix, ms.IsExternalUrl);
            _logger.LogDebug("{Prefix}IsForced                {IsForced}", prefix, ms.IsForced);
            _logger.LogDebug("{Prefix}IsInterlaced            {IsInterlaced}", prefix, ms.IsInterlaced);
            _logger.LogDebug("{Prefix}IsTextSubtitleStream    {IsTextSubtitleStream}", prefix, ms.IsTextSubtitleStream);
            _logger.LogDebug("{Prefix}Language                {Language}", prefix, ms.Language);
            _logger.LogDebug("{Prefix}Level                   {Level}", prefix, ms.Level);
            _logger.LogDebug("{Prefix}PacketLength            {PacketLength}", prefix, ms.PacketLength);
            _logger.LogDebug("{Prefix}Path                    {Path}", prefix, ms.Path);
            _logger.LogDebug("{Prefix}PixelFormat             {PixelFormat}", prefix, ms.PixelFormat);
            _logger.LogDebug("{Prefix}Profile                 {Profile}", prefix, ms.Profile);
            _logger.LogDebug("{Prefix}RealFrameRate           {RealFrameRate}", prefix, ms.RealFrameRate);
            _logger.LogDebug("{Prefix}RefFrames               {RefFrames}", prefix, ms.RefFrames);
            _logger.LogDebug("{Prefix}SampleRate              {SampleRate}", prefix, ms.SampleRate);
            _logger.LogDebug("{Prefix}Score                   {Score}", prefix, ms.Score);
            _logger.LogDebug("{Prefix}SupportsExternalStream  {SupportsExternalStream}", prefix, ms.SupportsExternalStream);
            _logger.LogDebug("{Prefix}Type                    {Type}", prefix, ms.Type); // Object
            _logger.LogDebug("{Prefix}Width                   {Width}", prefix, ms.Width);
            _logger.LogDebug("{Prefix}========================", prefix);
        }

        public async Task<List<MediaSourceInfo>> GetChannelStreamMediaSources(string channelId, CancellationToken cancellationToken)
        {
            var source = await GetChannelStream(channelId, string.Empty, cancellationToken);
            return [source];
        }

        public async Task<SeriesTimerInfo> GetNewTimerDefaultsAsync(CancellationToken cancellationToken, ProgramInfo program = null)
        {
            return await Task.Factory.StartNew(() =>
            {
                return new SeriesTimerInfo
                {
                    PostPaddingSeconds = Plugin.Instance.Configuration.Pre_Padding,
                    PrePaddingSeconds = Plugin.Instance.Configuration.Post_Padding,
                    RecordAnyChannel = true,
                    RecordAnyTime = true,
                    RecordNewOnly = false
                };
            }, cancellationToken);
        }

        public async Task<IEnumerable<ProgramInfo>> GetProgramsAsync(string channelId, DateTime startDateUtc, DateTime endDateUtc, CancellationToken cancellationToken)
        {
            int timeOut = await WaitForInitialLoadTask(cancellationToken);
            if (timeOut == -1 || cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug("LiveTvService.GetProgramsAsync: call cancelled or timed out - returning empty list");
                return new List<ProgramInfo>();
            }

            GetEventsResponseHandler currGetEventsResponseHandler = new GetEventsResponseHandler(startDateUtc, endDateUtc, _logger, cancellationToken);

            HTSMessage queryEvents = new HTSMessage();
            queryEvents.Method = "getEvents";
            queryEvents.putField("channelId", Convert.ToInt32(channelId));
            queryEvents.putField("maxTime", ((DateTimeOffset)endDateUtc).ToUnixTimeSeconds());
            _htsConnectionHandler.SendMessage(queryEvents, currGetEventsResponseHandler);

            _logger.LogDebug("LiveTvService.GetProgramsAsync: ask TVH for events of channel '{chanid}'", channelId);

            TaskWithTimeoutRunner<IEnumerable<ProgramInfo>> twtr = new TaskWithTimeoutRunner<IEnumerable<ProgramInfo>>(_timeout);
            TaskWithTimeoutResult<IEnumerable<ProgramInfo>> twtRes = await
                twtr.RunWithTimeout(currGetEventsResponseHandler.GetEvents(cancellationToken, channelId));

            if (twtRes.HasTimeout)
            {
                _logger.LogDebug("LiveTvService.GetProgramsAsync: timeout reached while calling for events of channel '{chanid}'", channelId);
                return new List<ProgramInfo>();
            }

            return twtRes.Result;
        }

        public async Task<IEnumerable<SeriesTimerInfo>> GetSeriesTimersAsync(CancellationToken cancellationToken)
        {
            int timeOut = await WaitForInitialLoadTask(cancellationToken);
            if (timeOut == -1 || cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug("LiveTvService.GetSeriesTimersAsync: call cancelled ot timed out - returning empty list");
                return new List<SeriesTimerInfo>();
            }

            TaskWithTimeoutRunner<IEnumerable<SeriesTimerInfo>> twtr = new TaskWithTimeoutRunner<IEnumerable<SeriesTimerInfo>>(_timeout);
            TaskWithTimeoutResult<IEnumerable<SeriesTimerInfo>> twtRes = await
                twtr.RunWithTimeout(_htsConnectionHandler.BuildAutorecInfos(cancellationToken));

            if (twtRes.HasTimeout)
            {
                return new List<SeriesTimerInfo>();
            }

            return twtRes.Result;
        }

        public async Task<IEnumerable<TimerInfo>> GetTimersAsync(CancellationToken cancellationToken)
        {
            //  retrieve the 'Pending' recordings");

            int timeOut = await WaitForInitialLoadTask(cancellationToken);
            if (timeOut == -1 || cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug("LiveTvService.GetTimersAsync: call cancelled or timed out - returning empty list");
                return new List<TimerInfo>();
            }

            TaskWithTimeoutRunner<IEnumerable<TimerInfo>> twtr = new TaskWithTimeoutRunner<IEnumerable<TimerInfo>>(_timeout);
            TaskWithTimeoutResult<IEnumerable<TimerInfo>> twtRes = await
                twtr.RunWithTimeout(_htsConnectionHandler.BuildPendingTimersInfos(cancellationToken));

            if (twtRes.HasTimeout)
            {
                return new List<TimerInfo>();
            }

            return twtRes.Result;
        }
        public Task ResetTuner(string id, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public async Task UpdateSeriesTimerAsync(SeriesTimerInfo info, CancellationToken cancellationToken)
        {
            await CancelSeriesTimerAsync(info.Id, cancellationToken);
            _lastRecordingChange = DateTime.UtcNow;
            // TODO add if method is implemented
            // await CreateSeriesTimerAsync(info, cancellationToken);
        }

        public async Task UpdateTimerAsync(TimerInfo info, CancellationToken cancellationToken)
        {
            int timeOut = await WaitForInitialLoadTask(cancellationToken);
            if (timeOut == -1 || cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug("LiveTvService.UpdateTimerAsync: call cancelled or timed out");
                return;
            }

            HTSMessage updateTimerMessage = new HTSMessage();
            updateTimerMessage.Method = "updateDvrEntry";
            updateTimerMessage.putField("id", info.Id);
            updateTimerMessage.putField("startExtra", (long)(info.PrePaddingSeconds / 60));
            updateTimerMessage.putField("stopExtra", (long)(info.PostPaddingSeconds / 60));

            TaskWithTimeoutRunner<HTSMessage> twtr = new TaskWithTimeoutRunner<HTSMessage>(_timeout);
            TaskWithTimeoutResult<HTSMessage> twtRes = await twtr.RunWithTimeout(Task.Factory.StartNew(() =>
            {
                LoopBackResponseHandler lbrh = new LoopBackResponseHandler();
                _htsConnectionHandler.SendMessage(updateTimerMessage, lbrh);
                _lastRecordingChange = DateTime.UtcNow;
                return lbrh.getResponse();
            }));

            if (twtRes.HasTimeout)
            {
                _logger.LogError("LiveTvService.UpdateTimerAsync: can't update timer because the timeout was reached");
            }
            else
            {
                HTSMessage updateTimerResponse = twtRes.Result;
                Boolean success = updateTimerResponse.getInt("success", 0) == 1;
                if (!success)
                {
                    if (updateTimerResponse.containsField("error"))
                    {
                        _logger.LogError("LiveTvService.UpdateTimerAsync: can't update timer: '{why}'", updateTimerResponse.getString("error"));
                    }
                    else if (updateTimerResponse.containsField("noaccess"))
                    {
                        _logger.LogError("LiveTvService.UpdateTimerAsync: can't update timer: '{why}'", updateTimerResponse.getString("noaccess"));
                    }
                }
            }
        }

        /***********/
        /* Helpers */
        /***********/

        private Task<int> WaitForInitialLoadTask(CancellationToken cancellationToken)
        {
            return Task.Factory.StartNew(() => _htsConnectionHandler.WaitForInitialLoad(cancellationToken), cancellationToken);
        }

        private static string Dump(List<DayOfWeek> days)
        {
            StringBuilder sb = new StringBuilder();
            foreach (DayOfWeek dow in days)
            {
                sb.Append(dow + ", ");
            }
            string tmpResult = sb.ToString();
            if (tmpResult.EndsWith(','))
            {
                tmpResult = tmpResult[..^2];
            }
            return tmpResult;
        }
    }

}
