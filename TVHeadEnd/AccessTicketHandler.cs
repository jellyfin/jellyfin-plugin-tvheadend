using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TVHeadEnd.HTSP;
using TVHeadEnd.HTSP_Responses;
using TVHeadEnd.TimeoutHelper;

namespace TVHeadEnd;

public class AccessTicketHandler
{
    private readonly ILogger<AccessTicketHandler> _logger;

    private readonly HTSConnectionHandler _htsConnectionHandler;
    private readonly string _ticketItemType;
    private readonly TimeSpan _requestTimeout;
    private readonly TimeSpan _ticketLifeSpan;

    private volatile int _ticketIdSequence;

    public enum TicketType : byte { Channel, Recording };

    public record Ticket
    {
        public string Id { get; init; }
        public string Path { get; init; }
        public string TicketParam { get; init; }
        public DateTime Expires { get; init; }
    }

    private readonly ConcurrentDictionary<string, Ticket> _ticketCache = new();

    internal AccessTicketHandler(
        ILoggerFactory loggerFactory, HTSConnectionHandler htsConnectionHandler,
        TimeSpan requestTimeout, TimeSpan ticketLifeSpan, TicketType ticketType)
    {
        _logger = loggerFactory.CreateLogger<AccessTicketHandler>();
        _htsConnectionHandler = htsConnectionHandler;
        _requestTimeout = requestTimeout;
        _ticketLifeSpan = ticketLifeSpan;

        _ticketItemType = ticketType switch
        {
            TicketType.Channel => "channelId",
            TicketType.Recording => "dvrId",
            _ => throw new ArgumentException("undefined ticketType")
        };
    }

    public async Task<Ticket> GetTicket(string itemId, CancellationToken cancellationToken)
    {
        var now = DateTime.Now;
        Ticket ticket;

        while (_ticketCache.TryGetValue(itemId, out ticket))
        {
            if (ticket.Expires > now)
            {
                return ticket; // non-expired ticket from cache
            }

            _ticketCache.TryRemove(new KeyValuePair<string, Ticket>(itemId, ticket));
        }

        var ticketResponse = await RequestNewTicket(itemId, cancellationToken);

        ticket = _ticketCache.GetOrAdd(itemId, _ => new Ticket()
        {
            Id = $"{NextTicketId()}",
            Path = ticketResponse.getString("path"),
            TicketParam = ticketResponse.getString("ticket"),
            Expires = now + _ticketLifeSpan,
        });

        _logger.LogInformation($"[TVHclient] AccessTicketHandler.GetAccessTicket: New ticket created for {_ticketItemType}={itemId}, Ticket-Id={ticket.Id}. Expires at {ticket.Expires}");

        return ticket;
    }

    private async Task<HTSMessage> RequestNewTicket(string itemId, CancellationToken cancellationToken)
    {
        var request = new HTSMessage { Method = "getTicket" };
        request.putField(_ticketItemType, itemId);

        var runner = new TaskWithTimeoutRunner<HTSMessage>(_requestTimeout);
        var result = await runner.RunWithTimeout(Task.Factory.StartNew(() =>
        {
            var response = new LoopBackResponseHandler();
            _htsConnectionHandler.SendMessage(request, response);
            return response.getResponse();
        }, cancellationToken));

        if (!result.HasTimeout)
        {
            return result.Result;
        }

        _logger.LogError("[TVHclient] AccessTicketHandler.GetAccessTicket: can't obtain playback authentication ticket from TVH because the timeout was reached");

        throw new TimeoutException("Obtaining playback authentication ticket from TVH caused a network timeout");
    }

    private int NextTicketId()
    {
        int id;
        for (; (id = Interlocked.Increment(ref _ticketIdSequence)) < 0;)
        {
            _ticketIdSequence = Math.Max(0, _ticketIdSequence);
        }

        return id;
    }
}
