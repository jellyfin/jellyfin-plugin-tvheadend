using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TVHeadEnd.Configuration;
using TVHeadEnd.DataHelper;
using TVHeadEnd.Helper;
using TVHeadEnd.HTSP;
using TVHeadEnd.HTSP.Responses;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace TVHeadEnd.Tests;

public class ConnectionTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public async Task ClosingBuffersWakesReadersAndWriters()
    {
        var queue = new BlockingBuffer<int>(1);
        var read = Task.Run(() => Assert.Throws<IOException>(() => queue.Dequeue()));
        queue.Close(new IOException("closed"));
        await read.WaitAsync(Deadline);
        Assert.Throws<IOException>(() => queue.Enqueue(1));

        queue = new BlockingBuffer<int>(1);
        queue.Enqueue(1);
        var write = Task.Run(() => Assert.Throws<IOException>(() => queue.Enqueue(2)));
        queue.Close(new IOException("closed"));
        await write.WaitAsync(Deadline);

        var bytes = new ByteList();
        var header = Task.Run(() => Assert.Throws<IOException>(() => bytes.GetFromStart(4)));
        var body = Task.Run(() => Assert.Throws<IOException>(() => bytes.ExtractFromStart(20)));
        bytes.Close();
        await Task.WhenAll(header, body).WaitAsync(Deadline);
    }

    [Fact]
    public async Task DisposingIdleConnectionJoinsAllWorkersAndRejectsReuse()
    {
        await using var server = new Peer();
        var connection = NewConnection();
        connection.Open("127.0.0.1", server.Port, Deadline);
        await server.Accepted.Task.WaitAsync(Deadline);
        connection.Dispose();
        AssertWorkersStopped(connection);
        Assert.Throws<ObjectDisposedException>(() => connection.Open("127.0.0.1", server.Port, Deadline));
        Assert.Throws<ObjectDisposedException>(() => connection.SendMessage(new HTSMessage(), null));
        connection.Dispose();
    }

    [Fact]
    public async Task ConcurrentRequestsKeepTheirOwnSequencesAndResponses()
    {
        await using var server = new Peer();
        using var connection = NewConnection();
        connection.Open("127.0.0.1", server.Port, Deadline);
        Assert.True(connection.Authenticate("user", "password"));
        var requests = Enumerable.Range(0, 300).Select(i => Task.Run(() =>
        {
            var response = new LoopBackResponseHandler();
            var message = new HTSMessage { Method = "echo" };
            message.PutField("value", i);
            connection.SendMessage(message, response);
            return (Value: i, Response: response);
        }));
        var responses = await Task.WhenAll(requests).WaitAsync(Deadline);
        foreach (var response in responses)
        {
            Assert.Equal(response.Value, response.Response.GetResponse(Deadline)!.GetInt("value"));
        }
        Assert.Equal(300, server.EchoSequences.Distinct().Count());
        Assert.Equal(300, server.EchoSequences.Count);
    }

    [Fact]
    public async Task ConnectionDeathFailsOutstandingUntimedRequests()
    {
        await using var server = new Peer { IgnoreEvents = true };
        using var connection = NewConnection();
        connection.Open("127.0.0.1", server.Port, Deadline);
        Assert.True(connection.Authenticate("user", "password"));
        var response = new LoopBackResponseHandler();
        connection.SendMessage(new HTSMessage { Method = "getEvents" }, response);
        await server.EventsRequested.Task.WaitAsync(Deadline);
        var waiting = Task.Run(() => Assert.Throws<IOException>(() => response.GetResponse()));
        connection.Stop();
        await waiting.WaitAsync(Deadline);
        Assert.Throws<IOException>(() => connection.SendMessage(new HTSMessage(), null));
    }

    [Fact]
    public async Task GracefulCloseDuringSyncFailsWaiterAndReconnects()
    {
        await using var server = new Peer { Sync = false };
        Configure(server.Port);
        using var handler = new HTSConnectionHandler(NullLoggerFactory.Instance);
        var waiting = Task.Run(() => handler.WaitForInitialLoad(CancellationToken.None));
        await server.MetadataRequested.Task.WaitAsync(Deadline);
        var old = await CurrentConnection(handler);
        server.CloseClients();
        Assert.Equal(-1, await waiting.WaitAsync(Deadline));
        await Until(() => Get<HTSConnectionAsync?>(handler, "_htsConnection") == null);
        AssertWorkersStopped(old);
        server.Sync = true;
        await Until(() => server.ConnectionCount >= 2);
        await AssertSynced(handler);
    }

    [Fact]
    public async Task StaleMessagesAndErrorsCannotChangeReplacement()
    {
        await using var server = new Peer();
        Configure(server.Port);
        using var handler = new HTSConnectionHandler(NullLoggerFactory.Instance);
        Assert.Equal(0, await Task.Run(() => handler.WaitForInitialLoad(CancellationToken.None)).WaitAsync(Deadline));
        var current = await CurrentConnection(handler);
        using var stale = NewConnection();
        var add = new HTSMessage { Method = "dvrEntryAdd" };
        add.PutField("id", "recording");
        handler.OnMessage(current, add);
        var delete = new HTSMessage { Method = "dvrEntryDelete" };
        delete.PutField("id", "recording");
        handler.OnMessage(stale, delete);
        handler.OnError(stale, new IOException("late error"));
        Assert.Same(current, Get<HTSConnectionAsync>(handler, "_htsConnection"));
        var helper = Get<object>(handler, "_dvrDataHelper");
        Assert.True(Get<Dictionary<string, HTSMessage>>(helper, "_data").ContainsKey("recording"));
        lock (Get<object>(handler, "_lock"))
        {
            Set(handler, "_initialLoadFinished", false);
            Set(handler, "_initialSyncReceived", false);
        }

        handler.OnMessage(stale, new HTSMessage { Method = "initialSyncCompleted" });
        Assert.False(Get<bool>(handler, "_initialLoadFinished"));
        handler.OnMessage(current, new HTSMessage { Method = "initialSyncCompleted" });
    }

    [Fact]
    public async Task SyncCannotPublishSuccessBeforeAuthenticationCommit()
    {
        await using var server = new Peer();
        Configure(server.Port);
        using var logs = new GateLogger("authenticate: authenticated");
        using var handler = new HTSConnectionHandler(logs);
        var waiting = Task.Run(() => handler.WaitForInitialLoad(CancellationToken.None));
        await logs.Entered.Task.WaitAsync(Deadline);
        try
        {
            await Until(() => Get<bool>(handler, "_initialSyncReceived"));
            Assert.False(Get<bool>(handler, "_connected"));
            Assert.False(waiting.IsCompleted);
        }
        finally
        {
            logs.Release.Set();
        }

        Assert.Equal(0, await waiting.WaitAsync(Deadline));
        Assert.Contains("/tvh/", handler.GetAuthenticatedUrl("dvrfile/1"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShutdownBetweenPublicationAndOpenCannotResurrectSocket()
    {
        await using var server = new Peer();
        Configure(server.Port);
        using var logs = new GateLogger("opening HTSP connection");
        var handler = new HTSConnectionHandler(logs);
        var waiting = Task.Run(() => handler.WaitForInitialLoad(CancellationToken.None));
        await logs.Entered.Task.WaitAsync(Deadline);
        var connection = await CurrentConnection(handler);
        var shutdown = Task.Run(handler.Dispose);
        try
        {
            await Until(connection.NeedsRestart);
        }
        finally
        {
            logs.Release.Set();
        }

        await shutdown.WaitAsync(Deadline);
        Assert.Equal(-1, await waiting.WaitAsync(Deadline));
        Assert.Equal(0, server.ConnectionCount);
        AssertWorkersStopped(connection);
        Assert.Null(Get<HTSConnectionAsync?>(handler, "_htsConnection"));
    }

    [Fact]
    public async Task RejectedCredentialsStopUntilConfigurationChanges()
    {
        await using var server = new Peer { Reject = true };
        Configure(server.Port);
        using var logs = new GateLogger(null);
        using var handler = new HTSConnectionHandler(logs);
        Assert.Equal(-1, await Task.Run(() => handler.WaitForInitialLoad(CancellationToken.None)).WaitAsync(Deadline));
        await Get<Task>(handler, "_connectionTask").WaitAsync(Deadline);
        Assert.True(Get<bool>(handler, "_authenticationFailed"));
        Assert.Single(logs.Errors);
        Assert.Contains("rejected", logs.Errors.Single(), StringComparison.Ordinal);
        server.Reject = false;
        Assert.Equal(-1, handler.WaitForInitialLoad(CancellationToken.None));
        Assert.Equal(1, server.ConnectionCount);

        // Changing the rejected settings re-arms the loop without a restart.
        Plugin.Instance.Configuration.Password = "corrected";
        Assert.Equal(0, await Task.Run(() => handler.WaitForInitialLoad(CancellationToken.None)).WaitAsync(Deadline));
        Assert.Equal(2, server.ConnectionCount);
        Assert.False(Get<bool>(handler, "_authenticationFailed"));
    }

    [Fact]
    public async Task SilentHandshakeTimesOutWithoutRejectingCredentialsAndRetries()
    {
        await using var server = new Peer { IgnoreHello = true };
        Configure(server.Port);
        using var handler = new HTSConnectionHandler(NullLoggerFactory.Instance);
        var waiting = Task.Run(() => handler.WaitForInitialLoad(CancellationToken.None));
        await server.Accepted.Task.WaitAsync(Deadline);
        var old = await CurrentConnection(handler);
        Assert.Equal(-1, await waiting.WaitAsync(TimeSpan.FromSeconds(25)));
        Assert.False(Get<bool>(handler, "_authenticationFailed"));
        await Until(() => WorkersStopped(old));
        server.IgnoreHello = false;
        await Until(() => server.ConnectionCount >= 2);
        await AssertSynced(handler);
    }

    [Fact]
    public async Task StalledSyncIsRetiredAndRetriesWithBackoff()
    {
        await using var server = new Peer { Sync = false };
        Configure(server.Port);
        using var handler = new HTSConnectionHandler(NullLoggerFactory.Instance);
        var waiting = Task.Run(() => handler.WaitForInitialLoad(CancellationToken.None));
        await server.MetadataRequested.Task.WaitAsync(Deadline);
        await Until(() => Get<bool>(handler, "_connected"));
        var old = await CurrentConnection(handler);
        lock (Get<object>(handler, "_lock"))
        {
            Set(handler, "_lastSyncProgress", Stopwatch.GetTimestamp() - (30 * Stopwatch.Frequency));
        }

        Assert.Equal(-1, await waiting.WaitAsync(Deadline));
        await Until(() => WorkersStopped(old));
        Assert.False(Get<bool>(handler, "_authenticationFailed"));
        var failedAt = Stopwatch.GetTimestamp();
        server.Sync = true;
        await Until(() => server.ConnectionCount >= 2);
        Assert.True(Stopwatch.GetElapsedTime(failedAt) >= TimeSpan.FromSeconds(4));
        await AssertSynced(handler);
    }

    [Fact]
    public async Task LostHandshakeUsesBackoffInsteadOfImmediateRetry()
    {
        await using var server = new Peer();
        Configure(server.Port);
        using var logs = new GateLogger("authenticate: authenticated");
        using var handler = new HTSConnectionHandler(logs);
        var waiting = Task.Run(() => handler.WaitForInitialLoad(CancellationToken.None));
        await logs.Entered.Task.WaitAsync(Deadline);
        var old = await CurrentConnection(handler);
        old.Stop();
        logs.Release.Set();
        Assert.Equal(-1, await waiting.WaitAsync(Deadline));
        var failedAt = Stopwatch.GetTimestamp();

        // Retries of a failed attempt are not awaited: callers fail fast during the backoff.
        Assert.Equal(-1, handler.WaitForInitialLoad(CancellationToken.None));
        Assert.True(Stopwatch.GetElapsedTime(failedAt) < TimeSpan.FromSeconds(1));
        await Until(() => server.ConnectionCount >= 2);
        Assert.True(Stopwatch.GetElapsedTime(failedAt) >= TimeSpan.FromSeconds(4));
        await AssertSynced(handler);
    }

    [Fact]
    public async Task OutageThrowsForRecordingsAndTimersInsteadOfReturningEmpty()
    {
        await using var server = new Peer { Reject = true };
        Configure(server.Port);
        using var handler = new HTSConnectionHandler(NullLoggerFactory.Instance);
        var service = new LiveTvService(NullLoggerFactory.Instance, null!, handler);
        handler.SetLiveTvService(service);
        var recordings = new RecordingsChannel(NullLoggerFactory.Instance, handler);

        // Jellyfin deletes recording items missing from a channel refresh, so an empty
        // list during an outage is data loss; timers must fail the same way for consistency.
        await Assert.ThrowsAsync<InvalidOperationException>(() => recordings.GetChannelItems(new MediaBrowser.Controller.Channels.InternalChannelItemQuery(), CancellationToken.None).WaitAsync(Deadline));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetTimersAsync(CancellationToken.None).WaitAsync(Deadline));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetSeriesTimersAsync(CancellationToken.None).WaitAsync(Deadline));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CancelTimerAsync("1", CancellationToken.None).WaitAsync(Deadline));
    }

    [Fact]
    public async Task RejectedMutationThrowsInsteadOfSucceeding()
    {
        await using var server = new Peer();
        Configure(server.Port);
        using var handler = new HTSConnectionHandler(NullLoggerFactory.Instance);
        var service = new LiveTvService(NullLoggerFactory.Instance, null!, handler);

        // Jellyfin deletes its recording entry and answers 204 on a normal return, so a
        // reply without success (or with an error) must throw.
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DeleteRecordingAsync("1", CancellationToken.None).WaitAsync(Deadline));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CancelTimerAsync("1", CancellationToken.None).WaitAsync(Deadline));
        server.AcceptMutations = true;
        await service.DeleteRecordingAsync("1", CancellationToken.None).WaitAsync(Deadline);
        await service.CancelTimerAsync("1", CancellationToken.None).WaitAsync(Deadline);
    }

    [Fact]
    public async Task ReconnectRebuildsTheMetadataSnapshot()
    {
        await using var server = new Peer { Recordings = ["kept", "deleted-during-outage"] };
        Configure(server.Port);
        using var handler = new HTSConnectionHandler(NullLoggerFactory.Instance);
        Assert.Equal(0, await Task.Run(() => handler.WaitForInitialLoad(CancellationToken.None)).WaitAsync(Deadline));
        var data = Get<Dictionary<string, HTSMessage>>(Get<object>(handler, "_dvrDataHelper"), "_data");
        Assert.Equal(new[] { "deleted-during-outage", "kept" }, data.Keys.Order().ToArray());

        // The server forgets one recording while the connection is down; the fresh dump on
        // reconnect must replace the old snapshot instead of being merged into it.
        server.Recordings = ["kept"];
        var old = await CurrentConnection(handler);
        server.CloseClients();
        await Until(old.NeedsRestart);
        await AssertSynced(handler);
        Assert.Equal(new[] { "kept" }, data.Keys.ToArray());
    }

    [Fact]
    public async Task ProgramRequestThrowsOnDisconnectInsteadOfReturningEmpty()
    {
        await using var server = new Peer { CloseOnEvents = true };
        Configure(server.Port);
        using var handler = new HTSConnectionHandler(NullLoggerFactory.Instance);
        var service = new LiveTvService(NullLoggerFactory.Instance, null!, handler);
        var error = await Assert.ThrowsAnyAsync<Exception>(() => service.GetProgramsAsync("1", DateTime.UtcNow, DateTime.UtcNow.AddHours(1), CancellationToken.None).WaitAsync(Deadline));
        Assert.IsNotType<TimeoutException>(error);
    }

    [Fact]
    public async Task SilentRequestFailsThroughWatchdog()
    {
        await using var server = new Peer { IgnoreEvents = true };
        Configure(server.Port);
        using var handler = new HTSConnectionHandler(NullLoggerFactory.Instance);
        var service = new LiveTvService(NullLoggerFactory.Instance, null!, handler);
        var request = service.GetProgramsAsync("1", DateTime.UtcNow, DateTime.UtcNow.AddHours(1), CancellationToken.None);
        await server.EventsRequested.Task.WaitAsync(Deadline);
        var connection = await CurrentConnection(handler);
        lock (Get<object>(connection, "_lock"))
        {
            var pending = Get<Dictionary<int, long>>(connection, "_responseStarted");
            foreach (int sequence in pending.Keys.ToArray())
            {
                pending[sequence] = Stopwatch.GetTimestamp() - (150 * Stopwatch.Frequency);
            }
        }

        var error = await Assert.ThrowsAnyAsync<Exception>(() => request.WaitAsync(Deadline));
        Assert.IsNotType<TimeoutException>(error);
        await Until(connection.NeedsRestart);
        Assert.False(Get<bool>(handler, "_authenticationFailed"));
    }

    [Fact]
    public async Task CancelledChannelBuildDoesNotReturnPartialLineup()
    {
        await using var server = new Peer();
        Configure(server.Port);
        using var handler = new HTSConnectionHandler(NullLoggerFactory.Instance);
        Assert.Equal(0, await Task.Run(() => handler.WaitForInitialLoad(CancellationToken.None)).WaitAsync(Deadline));
        var channel = new HTSMessage { Method = "channelAdd" };
        channel.PutField("channelId", 1);
        channel.PutField("channelNumber", 1);
        handler.OnMessage(await CurrentConnection(handler), HTSMessage.Parse(channel.BuildBytes(), NullLogger<HTSMessage>.Instance)!);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => handler.BuildChannelInfos(cancellation.Token));
    }

    [Fact]
    public async Task ConfigurationCorrectionIsUsedOnNextAttempt()
    {
        await using var first = new Peer { Sync = false };
        await using var replacement = new Peer();
        Configure(first.Port);
        using var handler = new HTSConnectionHandler(NullLoggerFactory.Instance);
        var waiting = Task.Run(() => handler.WaitForInitialLoad(CancellationToken.None));
        await first.MetadataRequested.Task.WaitAsync(Deadline);
        Plugin.Instance.Configuration.HTSP_Port = replacement.Port;
        first.CloseClients();
        Assert.Equal(-1, await waiting.WaitAsync(Deadline));
        await replacement.Accepted.Task.WaitAsync(Deadline);
        await AssertSynced(handler);
    }

    [Fact]
    public async Task DisconnectAfterSuccessfulSyncReconnectsWithoutAnotherApiCall()
    {
        await using var server = new Peer();
        Configure(server.Port);
        using var handler = new HTSConnectionHandler(NullLoggerFactory.Instance);
        Assert.Equal(0, await Task.Run(() => handler.WaitForInitialLoad(CancellationToken.None)).WaitAsync(Deadline));
        var old = await CurrentConnection(handler);
        server.CloseClients();
        await Until(old.NeedsRestart);
        var droppedAt = Stopwatch.GetTimestamp();

        // The reconnect after a working session is immediate (no backoff) and awaited.
        Assert.Equal(0, await Task.Run(() => handler.WaitForInitialLoad(CancellationToken.None)).WaitAsync(Deadline));
        Assert.True(Stopwatch.GetElapsedTime(droppedAt) < TimeSpan.FromSeconds(4));
        await Until(() => server.ConnectionCount >= 2);
        await Until(() => Get<bool>(handler, "_initialLoadFinished"));
        Assert.NotSame(old, await CurrentConnection(handler));
        AssertWorkersStopped(old);
        Assert.True(Get<bool>(old, "_disposed"));
    }

    [Fact]
    public async Task OpenRacingDisposeAlwaysReleasesSocketAndWorkers()
    {
        await using var server = new Peer();
        for (int iteration = 0; iteration < 32; iteration++)
        {
            var connection = NewConnection();
            var opening = Task.Run(() =>
            {
                try
                {
                    connection.Open("127.0.0.1", server.Port, Deadline);
                }
                catch (ObjectDisposedException) { }
                catch (InvalidOperationException) { }
                catch (OperationCanceledException) { }
                catch (SocketException) { }
            });
            var stopping = Task.Run(connection.Dispose);
            await Task.WhenAll(opening, stopping).WaitAsync(Deadline);
            AssertWorkersStopped(connection);
            var socket = Get<Socket?>(connection, "_socket");
            Assert.True(socket == null || socket.SafeHandle.IsClosed);
        }
    }

    [Fact]
    public async Task RejectedEventRequestThrowsInsteadOfReturningEmpty()
    {
        await using var server = new Peer { RejectEvents = true };
        Configure(server.Port);
        using var handler = new HTSConnectionHandler(NullLoggerFactory.Instance);
        var service = new LiveTvService(NullLoggerFactory.Instance, null!, handler);
        var error = await Assert.ThrowsAnyAsync<Exception>(() => service.GetProgramsAsync("1", DateTime.UtcNow, DateTime.UtcNow.AddHours(1), CancellationToken.None).WaitAsync(Deadline));
        Assert.IsNotType<TimeoutException>(error);
        Assert.Equal(0, handler.WaitForInitialLoad(CancellationToken.None));
    }

    [Fact]
    public async Task SnapshotBuildersRejectReconnectAfterAvailabilityGate()
    {
        await using var server = new Peer();
        Configure(server.Port);
        using var handler = new HTSConnectionHandler(NullLoggerFactory.Instance);
        await handler.EnsureAvailableAsync("test", CancellationToken.None).WaitAsync(Deadline);
        var old = await CurrentConnection(handler);
        server.Sync = false;
        server.CloseClients();
        await Until(() => !ReferenceEquals(old, Get<HTSConnectionAsync?>(handler, "_htsConnection")) && Get<bool>(handler, "_connected"));
        Assert.False(Get<bool>(handler, "_initialLoadFinished"));

        // These are the exact calls made after the public operations' availability gate.
        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.BuildChannelInfos(CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.BuildDvrInfos(CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.BuildPendingTimersInfos(CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.BuildAutorecInfos(CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SnapshotCompletionRejectsReplacementEvenWhenItHasSynced(bool syncReplacement)
    {
        await using var server = new Peer();
        Configure(server.Port);
        using var handler = new HTSConnectionHandler(NullLoggerFactory.Instance);
        await handler.EnsureAvailableAsync("test", CancellationToken.None).WaitAsync(Deadline);
        var old = await CurrentConnection(handler);
        var completion = new TaskCompletionSource<IEnumerable<ChannelInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = BuildControlledSnapshot(handler, completion.Task, CancellationToken.None);
        server.Sync = syncReplacement;
        server.CloseClients();
        await Until(() => !ReferenceEquals(old, Get<HTSConnectionAsync?>(handler, "_htsConnection")) && Get<bool>(handler, "_connected"));
        if (syncReplacement) await AssertSynced(handler);

        // Model the builder finishing after it read a cleared or partly rebuilt helper.
        completion.SetResult([]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => pending.WaitAsync(Deadline));
    }

    [Fact]
    public async Task SnapshotCompletionPreservesCancellation()
    {
        await using var server = new Peer();
        Configure(server.Port);
        using var handler = new HTSConnectionHandler(NullLoggerFactory.Instance);
        await handler.EnsureAvailableAsync("test", CancellationToken.None).WaitAsync(Deadline);
        using var cancellation = new CancellationTokenSource();
        var completion = new TaskCompletionSource<IEnumerable<ChannelInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = BuildControlledSnapshot(handler, completion.Task, cancellation.Token);
        await cancellation.CancelAsync();
        completion.SetResult([]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(Deadline));
    }

    [Fact]
    public async Task EmptyMetadataBuildersThrowOnCancellation()
    {
        var channels = new ChannelDataHelper(NullLogger<ChannelDataHelper>.Instance);
        var dvr = new DvrDataHelper(NullLogger<DvrDataHelper>.Instance);
        var autorec = new AutorecDataHelper(NullLogger<AutorecDataHelper>.Instance);
        var token = new CancellationToken(true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => channels.BuildChannelInfos(token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dvr.BuildDvrInfos(token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dvr.BuildPendingTimersInfos(token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => autorec.BuildAutorecInfos(token));
    }

    private static Task<IEnumerable<ChannelInfo>> BuildControlledSnapshot(HTSConnectionHandler handler, Task<IEnumerable<ChannelInfo>> result, CancellationToken token)
    {
        Func<CancellationToken, Task<IEnumerable<ChannelInfo>>> build = _ => result;
        return (Task<IEnumerable<ChannelInfo>>)typeof(HTSConnectionHandler)
            .GetMethod("BuildSnapshotAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .MakeGenericMethod(typeof(ChannelInfo)).Invoke(handler, [build, token])!;
    }

    private static HTSConnectionAsync NewConnection() => new(new Listener(), "test", "1", NullLoggerFactory.Instance);

    private static void Configure(int port)
    {
        // Avoid filesystem-backed Jellyfin configuration in these protocol tests.
        var plugin = (Plugin)RuntimeHelpers.GetUninitializedObject(typeof(Plugin));
        var config = new PluginConfiguration { TVH_ServerName = "127.0.0.1", HTSP_Port = port, Username = "user", Password = "password" };
        for (Type? type = typeof(Plugin); type != null; type = type.BaseType)
        {
            foreach (var field in type.GetFields(Fields | BindingFlags.DeclaredOnly))
            {
                if (field.FieldType == typeof(PluginConfiguration))
                {
                    field.SetValue(plugin, config);
                }
            }
        }

        typeof(Plugin).GetField("<Instance>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, plugin);
        Assert.Same(config, Plugin.Instance.Configuration);
    }

    private static async Task AssertSynced(HTSConnectionHandler handler)
    {
        await Until(() => Get<bool>(handler, "_initialLoadFinished"));
        Assert.Equal(0, handler.WaitForInitialLoad(CancellationToken.None));
    }

    private static T Get<T>(object value, string name) => (T)value.GetType().GetField(name, Fields)!.GetValue(value)!;

    private static void Set(object value, string name, object? field) => value.GetType().GetField(name, Fields)!.SetValue(value, field);

    private static async Task<HTSConnectionAsync> CurrentConnection(HTSConnectionHandler handler)
    {
        await Until(() => Get<HTSConnectionAsync?>(handler, "_htsConnection") != null);
        return Get<HTSConnectionAsync>(handler, "_htsConnection");
    }

    private static bool WorkersStopped(HTSConnectionAsync connection) => new[] { "_receiveHandlerThread", "_messageBuilderThread", "_sendingHandlerThread", "_messageDistributorThread" }
        .All(name => Get<Thread?>(connection, name)?.IsAlive != true);

    private static void AssertWorkersStopped(HTSConnectionAsync connection) => Assert.True(WorkersStopped(connection));

    private static async Task Until(Func<bool> condition)
    {
        var started = Stopwatch.GetTimestamp();
        while (!condition())
        {
            Assert.True(Stopwatch.GetElapsedTime(started) < Deadline, "Condition did not become true before its deadline");
            await Task.Delay(10);
        }
    }

    private sealed class Listener : IHTSConnectionListener
    {
        public void OnError(HTSConnectionAsync connection, Exception ex) { }
        public void OnMessage(HTSConnectionAsync connection, HTSMessage response) { }
    }

    private sealed class GateLogger(string? gate) : ILoggerFactory, ILogger
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new(false);
        public ConcurrentQueue<string> Errors { get; } = new();
        public ILogger CreateLogger(string name) => this;
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { Release.Set(); Release.Dispose(); }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter)
        {
            string text = formatter(state, error);
            if (level == LogLevel.Error) Errors.Enqueue(text);
            if (gate != null && text.Contains(gate, StringComparison.Ordinal))
            {
                Entered.TrySetResult();
                Release.Wait();
            }
        }
    }

    private sealed class Peer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly ConcurrentBag<TcpClient> _clients = new();
        private readonly ConcurrentBag<Task> _handlers = new();
        private readonly Task _accept;
        private int _connectionCount;
        public bool Sync = true;
        public bool AcceptMutations;
        public string[] Recordings = [];
        public bool Reject;
        public bool IgnoreHello;
        public bool IgnoreEvents;
        public bool CloseOnEvents;
        public bool RejectEvents;
        public TaskCompletionSource Accepted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource MetadataRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource EventsRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<int> EchoSequences { get; } = new();
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public int ConnectionCount => Volatile.Read(ref _connectionCount);

        public Peer()
        {
            _listener.Start();
            _accept = Accept();
        }

        public void CloseClients()
        {
            foreach (var client in _clients) client.Dispose();
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            CloseClients();
            await _accept;
            await Task.WhenAll(_handlers);
            _stop.Dispose();
        }

        private async Task Accept()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    _clients.Add(client);
                    Interlocked.Increment(ref _connectionCount);
                    Accepted.TrySetResult();
                    _handlers.Add(Serve(client));
                }
            }
            catch (OperationCanceledException) { }
            catch (SocketException) when (_stop.IsCancellationRequested) { }
        }

        private async Task Serve(TcpClient client)
        {
            try
            {
                using (client)
                {
                    var stream = client.GetStream();
                    while (!_stop.IsCancellationRequested)
                    {
                        byte[] header = new byte[4];
                        await stream.ReadExactlyAsync(header, _stop.Token);
                        int length = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header);
                        byte[] packet = new byte[length + 4];
                        header.CopyTo(packet, 0);
                        await stream.ReadExactlyAsync(packet.AsMemory(4), _stop.Token);
                        var request = HTSMessage.Parse(packet, NullLogger<HTSMessage>.Instance)!;
                        if (request.Method == "hello" && IgnoreHello) continue;
                        if (request.Method == "getEvents")
                        {
                            EventsRequested.TrySetResult();
                            if (CloseOnEvents) return;
                            if (IgnoreEvents) continue;
                        }

                        var response = new HTSMessage();
                        response.PutField("seq", request.GetInt("seq"));
                        if (request.Method == "hello")
                        {
                            response.PutField("htspversion", 40);
                            response.PutField("webroot", "/tvh");
                        }

                        if (request.Method == "getTicket")
                        {
                            response.PutField("path", "/stream/channel/1");
                            response.PutField("ticket", "ticket");
                        }

                        if ((request.Method == "authenticate" && Reject) || (request.Method == "getEvents" && RejectEvents)) response.PutField("noaccess", 1);
                        if (request.Method is "cancelDvrEntry" or "deleteDvrEntry" or "addDvrEntry" or "updateDvrEntry" or "addAutorecEntry" or "updateAutorecEntry" or "deleteAutorecEntry")
                        {
                            if (AcceptMutations) response.PutField("success", 1);
                            else response.PutField("error", "rejected by the test peer");
                        }
                        if (request.Method == "echo")
                        {
                            EchoSequences.Enqueue(request.GetInt("seq"));
                            response.PutField("value", request.GetInt("value"));
                        }

                        await stream.WriteAsync(response.BuildBytes(), _stop.Token);
                        if (request.Method == "enableAsyncMetadata")
                        {
                            MetadataRequested.TrySetResult();
                            foreach (var id in Recordings)
                            {
                                var entry = new HTSMessage { Method = "dvrEntryAdd" };
                                entry.PutField("id", id);
                                await stream.WriteAsync(entry.BuildBytes(), _stop.Token);
                            }

                            if (Sync)
                            {
                                await stream.WriteAsync(new HTSMessage { Method = "initialSyncCompleted" }.BuildBytes(), _stop.Token);
                            }
                        }
                    }
                }
            }
            catch (IOException) { }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (SocketException) { }
        }
    }
}
