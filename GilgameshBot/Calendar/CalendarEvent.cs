namespace GilgameshBot.Calendar;

/// <summary>
/// One event on the Free Company calendar, read from an Apollo post.
/// </summary>
/// <param name="Id">Apollo's event id. A weekly event keeps it across occurrences, so it is not unique on its own.</param>
/// <param name="Title">The event title as Apollo shows it: raw text, escape it before it goes into Discord markdown.</param>
/// <param name="Start">Start, UTC.</param>
/// <param name="End">End, UTC; null when the post gives none.</param>
/// <param name="ChannelId">Channel of the Apollo post.</param>
/// <param name="MessageId">The Apollo post, so members can be sent there to sign up.</param>
public sealed record CalendarEvent(
    string Id,
    string Title,
    DateTimeOffset Start,
    DateTimeOffset? End,
    ulong ChannelId,
    ulong MessageId);
