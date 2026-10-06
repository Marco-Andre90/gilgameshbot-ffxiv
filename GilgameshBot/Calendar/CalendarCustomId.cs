using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace GilgameshBot.Calendar;

/// <summary>What a calendar button or menu does.</summary>
public enum CalendarAction
{
    /// <summary>Show <see cref="CalendarCustomId.Month"/> in <see cref="CalendarCustomId.Theme"/>.</summary>
    Show,

    /// <summary>Menu of the month's days with events; the picked day's events are listed.</summary>
    Day,

    /// <summary>Opens the theme menu.</summary>
    Themes,

    /// <summary>The theme menu: shows the month in the picked theme.</summary>
    Pick,
}

/// <summary>
/// The custom id carried by every calendar button and menu:
/// <c>gbcal:&lt;action&gt;:&lt;yyyy-MM&gt;:&lt;theme&gt;:&lt;time zone&gt;</c>.
/// </summary>
/// <remarks>
/// <para>
/// Everything a click needs travels in the id, so any plugin can answer it without state of its
/// own: the month is absolute (never "next from now", which would shift at midnight), the theme
/// is the one to draw, and the time zone places events on days.
/// </para>
/// <para>
/// On the calendar message in the channel, the theme and time zone are the calendar's settings.
/// That message is where they are kept: <c>/calendar theme</c> and <c>/calendar timezone</c>
/// rewrite it, and every plugin reads them back from its buttons. Only the bot's own message is
/// read, and an unknown theme or zone falls back to the default.
/// </para>
/// </remarks>
public sealed record CalendarCustomId(CalendarAction Action, int Year, int Month, string Theme, string TimeZone)
{
    public const string Prefix = "gbcal:";

    public override string ToString() =>
        $"{Prefix}{Action.ToString().ToLowerInvariant()}:{Year:D4}-{Month:D2}:{Theme}:{TimeZone}";

    public static bool TryParse(string? value, [NotNullWhen(true)] out CalendarCustomId? id)
    {
        id = null;
        if (value is null || !value.StartsWith(Prefix, StringComparison.Ordinal))
            return false;

        var parts = value[Prefix.Length..].Split(':');
        if (parts.Length != 4
            || !Enum.TryParse<CalendarAction>(parts[0], ignoreCase: true, out var action)
            || !Enum.IsDefined(action)
            || !DateOnly.TryParseExact(parts[1] + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var month))
            return false;

        id = new CalendarCustomId(action, month.Year, month.Month,
            CalendarThemes.Normalize(parts[2]),
            CalendarTimeZones.FindOrDefault(parts[3]).Key);
        return true;
    }
}

/// <summary>
/// A calendar's settings: the server theme, the time zone that places events on days, and the
/// name in its title. All three are kept on the calendar message: theme and zone in its custom
/// ids, the name as its embed title.
/// </summary>
public sealed record CalendarSettings(string Theme, string TimeZone, string Name)
{
    public const string DefaultName = "Free Company calendar";

    /// <summary>Longest name accepted; Discord allows 256 characters in an embed title.</summary>
    public const int MaxNameLength = 80;

    /// <summary>Starts the embed title, before the name.</summary>
    public const string TitlePrefix = "📅 ";

    public static readonly CalendarSettings Default = new(CalendarThemes.DefaultKey, CalendarTimeZones.DefaultKey, DefaultName);

    /// <summary>
    /// The settings of a calendar message from its custom ids and embed title, or null when it
    /// carries no calendar custom id.
    /// </summary>
    public static CalendarSettings? From(IEnumerable<string> customIds, string? embedTitle)
    {
        var id = customIds.Select(c => CalendarCustomId.TryParse(c, out var parsed) ? parsed : null)
            .OfType<CalendarCustomId>()
            .FirstOrDefault();

        if (id is null)
            return null;

        var title = embedTitle ?? string.Empty;
        return new CalendarSettings(id.Theme, id.TimeZone,
            NormalizeName(title.StartsWith(TitlePrefix, StringComparison.Ordinal) ? title[TitlePrefix.Length..] : null));
    }

    /// <summary>One line, trimmed, at most <see cref="MaxNameLength"/> characters; the default when empty.</summary>
    public static string NormalizeName(string? name)
    {
        var line = string.Join(' ', (name ?? string.Empty).Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        if (line.Length > MaxNameLength)
            line = line[..(char.IsHighSurrogate(line[MaxNameLength - 1]) ? MaxNameLength - 1 : MaxNameLength)].TrimEnd();

        return line.Length > 0 ? line : DefaultName;
    }
}
