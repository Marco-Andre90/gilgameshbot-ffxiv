using System.Text.RegularExpressions;

namespace GilgameshBot.Calendar;

/// <summary>The parts of a Discord message the Apollo parser looks at, free of any Discord library type.</summary>
/// <param name="ChannelId">Channel the message is in.</param>
/// <param name="MessageId">The message.</param>
/// <param name="AuthorId">Who posted it.</param>
/// <param name="Title">Title of the message's first embed, if any.</param>
/// <param name="Fields">Name and value of every field of that embed.</param>
/// <param name="CustomIds">Custom ids of the message's buttons and menus.</param>
public sealed record ApolloPost(
    ulong ChannelId,
    ulong MessageId,
    ulong AuthorId,
    string? Title,
    IReadOnlyList<(string Name, string Value)> Fields,
    IReadOnlyList<string> CustomIds);

/// <summary>
/// Turns Apollo's event posts into <see cref="CalendarEvent"/>s.
/// </summary>
/// <remarks>
/// <para>
/// Apollo creates no native Discord events, so its posts are the only source. A post counts when
/// it was written by Apollo, has a title, a <c>Time</c> field whose first Discord timestamp is
/// the start (the second, when present, the end), and an event id: from the <c>manage:&lt;id&gt;</c>
/// button, or else from the <c>View on web</c> link. Anything else in the channel is ignored.
/// </para>
/// <para>
/// A weekly event is posted again as a new message a few days after each occurrence, with the
/// same event id: only occurrences Apollo has posted are on the calendar, so a cancelled one
/// never lingers.
/// </para>
/// </remarks>
public static partial class ApolloParser
{
    /// <summary>Apollo's bot user, the same on every server.</summary>
    public const ulong ApolloBotId = 475744554910351370;

    /// <summary>An event longer than this is taken as a typo and shown without an end.</summary>
    private static readonly TimeSpan MaxDuration = TimeSpan.FromDays(7);

    [GeneratedRegex(@"<t:(-?\d{1,13})(?::[tTdDfFR])?>")]
    private static partial Regex TimestampRegex();

    [GeneratedRegex(@"^manage:(\d{1,20})$")]
    private static partial Regex ManageIdRegex();

    [GeneratedRegex(@"apollo\.fyi/[^)\s]*/events/(\d{1,20})")]
    private static partial Regex WebLinkRegex();

    /// <summary>The event in <paramref name="post"/>, or null when it is not an Apollo event post.</summary>
    public static CalendarEvent? TryParse(ApolloPost post)
    {
        if (post.AuthorId != ApolloBotId)
            return null;

        var title = (post.Title ?? string.Empty).Trim();
        if (title.Length == 0)
            return null;

        var time = post.Fields.FirstOrDefault(f => string.Equals(f.Name.Trim(), "Time", StringComparison.OrdinalIgnoreCase)).Value;
        if (string.IsNullOrEmpty(time))
            return null;

        var stamps = TimestampRegex().Matches(time);
        if (stamps.Count == 0 || !TryUnix(stamps[0].Groups[1].Value, out var start))
            return null;

        DateTimeOffset? end = null;
        if (stamps.Count > 1 && TryUnix(stamps[1].Groups[1].Value, out var parsedEnd)
                             && parsedEnd > start && parsedEnd - start <= MaxDuration)
            end = parsedEnd;

        var id = post.CustomIds
            .Select(c => ManageIdRegex().Match(c))
            .FirstOrDefault(m => m.Success)?.Groups[1].Value;

        id ??= WebLinkRegex().Match(time) is { Success: true } web ? web.Groups[1].Value : null;
        if (id is null)
            return null;

        return new CalendarEvent(id, title, start, end, post.ChannelId, post.MessageId);
    }

    /// <summary>
    /// The events among <paramref name="posts"/>, oldest first. The same occurrence posted twice
    /// (same event id and start) is kept once: the newest post wins.
    /// </summary>
    public static List<CalendarEvent> ParseAll(IEnumerable<ApolloPost> posts) =>
        posts
            .Select(TryParse)
            .OfType<CalendarEvent>()
            .GroupBy(e => (e.Id, e.Start))
            .Select(g => g.MaxBy(e => e.MessageId)!)
            .OrderBy(e => e.Start)
            .ThenBy(e => e.MessageId)
            .ToList();

    private static bool TryUnix(string digits, out DateTimeOffset value)
    {
        value = default;
        if (!long.TryParse(digits, out var seconds))
            return false;

        try
        {
            value = DateTimeOffset.FromUnixTimeSeconds(seconds);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }
}
