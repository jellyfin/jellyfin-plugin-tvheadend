using System;

namespace TVHeadEnd;

/// <summary>
/// A TVHeadend playback authentication ticket.
/// </summary>
public record Ticket
{
    /// <summary>
    /// Gets the plugin-local identifier of this ticket.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Gets the stream path the ticket was issued for.
    /// </summary>
    public required string Path { get; init; }

    /// <summary>
    /// Gets the ticket value to pass to TVHeadend as the 'ticket' query parameter.
    /// </summary>
    public required string TicketParam { get; init; }

    /// <summary>
    /// Gets the stream path including the ticket query parameter.
    /// </summary>
    public string Url => $"{Path}?ticket={TicketParam}";

    /// <summary>
    /// Gets the UTC time at which this ticket has to be revalidated.
    /// </summary>
    public required DateTime Expires { get; init; }
}
