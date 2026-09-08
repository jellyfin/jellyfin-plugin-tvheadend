using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TVHeadEnd.HTSP;
using TVHeadEnd.HTSP.Responses;
using TVHeadEnd.TimeoutHelper;

namespace TVHeadEnd;

public class AccessTicketHandler
{
    private readonly ILogger<AccessTicketHandler> _logger;

    private readonly HTSConnectionHandler _htsConnectionHandler;
    private readonly string _ticketItemType;
    private readonly TimeSpan _requestTimeout;
    private readonly int _requestRetries;
    private readonly TimeSpan _ticketLifeSpan;

    private readonly ConcurrentDictionary<string, Task<Ticket>> _ticketCache = new();

    private volatile int _ticketIdSequence;

    internal AccessTicketHandler(
        ILoggerFactory loggerFactory,
        HTSConnectionHandler htsConnectionHandler,
        TimeSpan requestTimeout,
        int requestRetries,
        TimeSpan ticketLifeSpan,
        TicketType ticketType)
    {
        _logger = loggerFactory.CreateLogger<AccessTicketHandler>();
        _htsConnectionHandler = htsConnectionHandler;
        _requestTimeout = requestTimeout;
        _requestRetries = requestRetries;
        _ticketLifeSpan = ticketLifeSpan;

        _ticketItemType = ticketType switch
        {
            TicketType.Channel => "channelId",
            TicketType.Recording => "dvrId",
            _ => throw new ArgumentException("undefined ticketType", nameof(ticketType))
        };
    }

    public async Task<Ticket> GetTicket(string itemId, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        Ticket? ticket = null;

        while (_ticketCache.TryGetValue(itemId, out var ticketTask))
        {
            try
            {
                ticket = await ticketTask.ConfigureAwait(false);
            }
            catch
            {
                // The cached request failed (e.g. TVH unreachable). Drop it so
                // the next attempt can request a fresh ticket, otherwise this
                // item could never be played again without a restart.
                _ticketCache.TryRemove(new KeyValuePair<string, Task<Ticket>>(itemId, ticketTask));
                ticket = null;
                break;
            }

            if (ticket.Expires > now)
            {
                return ticket; // non-expired ticket from cache
            }

            _logger.LogDebug(
                "[TVHclient] AccessTicketHandler.GetAccessTicket: Cache expired for {ItemType}={ItemId}. Revalidating ticket (#{TicketId})",
                _ticketItemType,
                itemId,
                ticket.Id);
            _ticketCache.TryRemove(new KeyValuePair<string, Task<Ticket>>(itemId, ticketTask));
        }

        var newTicketTask = _ticketCache.GetOrAdd(itemId, _ => GetTicketRecord(itemId, ticket, now, cancellationToken));
        try
        {
            return await newTicketTask.ConfigureAwait(false);
        }
        catch
        {
            _ticketCache.TryRemove(new KeyValuePair<string, Task<Ticket>>(itemId, newTicketTask));
            throw;
        }
    }

    private Task<Ticket> GetTicketRecord(string itemId, Ticket? currentRecord, DateTime now, CancellationToken cancellation)
    {
        return RequestTicket(itemId, cancellation).ContinueWith(
            ticketTask =>
            {
                var response = ticketTask.Result;
                var path = response.GetString("path");
                var ticket = response.GetString("ticket");

                if (path == null || ticket == null)
                {
                    throw new InvalidOperationException("TVH returned a playback ticket without a path or ticket value");
                }

                var id = (currentRecord != null && path == currentRecord.Path && ticket == currentRecord.TicketParam)
                    ? currentRecord.Id
                    : $"{NextTicketId()}";

                if (id != currentRecord?.Id)
                {
                    _logger.LogInformation(
                        "[TVHclient] AccessTicketHandler.GetAccessTicket: New ticket (#{TicketId}) created for {ItemType}={ItemId}",
                        id,
                        _ticketItemType,
                        itemId);
                }

                return new Ticket()
                {
                    Id = id,
                    Path = path,
                    TicketParam = ticket,
                    Expires = now + _ticketLifeSpan,
                };
            },
            cancellation,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
    }

    private async Task<HTSMessage> RequestTicket(string itemId, CancellationToken cancellation)
    {
        var request = new HTSMessage { Method = "getTicket" };
        request.PutField(_ticketItemType, itemId);

        for (int attempt = 1, lastAttempt = 1 + _requestRetries;
             attempt <= lastAttempt && !cancellation.IsCancellationRequested;
             attempt++)
        {
            var runner = new TaskWithTimeoutRunner<HTSMessage>(_requestTimeout * attempt);
            var result = await runner.RunWithTimeout(Task.Run(
                () =>
                {
                    var response = new LoopBackResponseHandler();
                    _htsConnectionHandler.SendMessage(request, response);
                    return response.GetResponse();
                },
                cancellation)).ConfigureAwait(false);

            if (!result.HasTimeout)
            {
                return result.Result;
            }
        }

        _logger.LogError("[TVHclient] AccessTicketHandler.GetAccessTicket: can't obtain playback authentication ticket from TVH because the timeout was reached");

        throw new TimeoutException("Obtaining playback authentication ticket from TVH caused a network timeout");
    }

    private int NextTicketId()
    {
        int id;
        while ((id = Interlocked.Increment(ref _ticketIdSequence)) < 0)
        {
            _ticketIdSequence = Math.Max(0, _ticketIdSequence);
        }

        return id;
    }
}
