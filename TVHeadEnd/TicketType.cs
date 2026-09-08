namespace TVHeadEnd;

/// <summary>
/// The kind of item a TVHeadend access ticket is requested for.
/// </summary>
public enum TicketType
{
    /// <summary>
    /// A live TV channel.
    /// </summary>
    Channel,

    /// <summary>
    /// A DVR recording.
    /// </summary>
    Recording
}
