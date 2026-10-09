using System.Globalization;
using System.Text;
using Discord;
using GilgameshBot.Calendar.Rendering;
using GilgameshBot.Relay;

namespace GilgameshBot.Calendar;

/// <summary>
/// The text and controls that go with a calendar image: in the channel message and in the
/// replies only the member who clicked sees.
/// </summary>
/// <remarks>
/// <para>
/// The text goes in an embed, so the picture comes first: Discord shows a message's attached
/// files above its embeds.
/// </para>
/// <para>
/// Times are only ever written as Discord timestamps (<c>&lt;t:…&gt;</c>), which every member's
/// client shows in their own time zone. The picture cannot do that, so it shows no times at all:
/// it only places events on days, in the calendar's time zone, which the text names.
/// </para>
/// </remarks>
public static class CalendarMessage
{
    /// <summary>How many upcoming events the channel message lists.</summary>
    private const int UpcomingCount = 8;

    /// <summary>Discord's limit for an embed description, less some room.</summary>
    private const int MaxDescription = 4000;

    /// <summary>Months a member can browse away from the current one, each way.</summary>
    public const int MonthRange = 1;

    private static readonly Color Accent = new(0xC0, 0x58, 0x1A);

    // --- Text -----------------------------------------------------------------------------------

    /// <summary>The channel message: the calendar's name, what is coming up, and how to read the picture.</summary>
    public static Embed ChannelEmbed(
        IReadOnlyList<CalendarEvent> events, ulong guildId, CalendarSettings settings, CalendarTimeZone zone, bool zoneFallback,
        DateTimeOffset now)
    {
        var text = new StringBuilder();

        var upcoming = events
            .Where(e => (e.End ?? e.Start) >= now)
            .OrderBy(e => e.Start)
            .Take(UpcomingCount)
            .ToList();

        if (upcoming.Count == 0)
        {
            text.Append("No upcoming events. Events created with Apollo show up here.");
        }
        else
        {
            text.Append("**Next events**\n");
            foreach (var e in upcoming)
                text.Append(Line(e, guildId)).Append('\n');
            text.Append("Sign up on the event posts.");
        }

        return new EmbedBuilder()
            .WithTitle(CalendarSettings.TitlePrefix + settings.Name)
            .WithDescription(Fit(text.ToString()))
            .WithColor(Accent)
            .WithFooter(Footer(zone, zoneFallback) + " · Updated")
            .WithTimestamp(now)
            .Build();
    }

    /// <summary>A reply showing <paramref name="year"/>/<paramref name="month"/>: its events and the theme it is drawn in.</summary>
    public static Embed MonthEmbed(
        IReadOnlyList<CalendarEvent> events, ulong guildId, int year, int month, string themeKey,
        CalendarTimeZone zone, TimeZoneInfo tz, bool zoneFallback)
    {
        var theme = CalendarThemes.Find(CalendarThemes.Resolve(themeKey, month))?.Label ?? "Clean";

        var inMonth = events
            .Where(e => CalendarPage.LocalDay(e.Start, tz) is var d && d.Year == year && d.Month == month)
            .OrderBy(e => e.Start)
            .ToList();

        var text = new StringBuilder();
        if (inMonth.Count == 0)
            text.Append("No events this month.");

        foreach (var e in inMonth)
            text.Append(Line(e, guildId)).Append('\n');

        return new EmbedBuilder()
            .WithTitle($"{CalendarSettings.TitlePrefix}{CalendarRenderer.MonthName(month)} {year} · {theme}")
            .WithDescription(Fit(text.ToString().TrimEnd('\n')))
            .WithColor(Accent)
            .WithFooter(Footer(zone, zoneFallback))
            .Build();
    }

    /// <summary>The reply to Day details: every event of that day.</summary>
    public static string DayText(IReadOnlyList<CalendarEvent> events, ulong guildId, DateOnly day, TimeZoneInfo tz)
    {
        var onDay = events.Where(e => CalendarPage.LocalDay(e.Start, tz) == day).OrderBy(e => e.Start).ToList();
        var text = new StringBuilder();
        text.Append($"📅 **{DayLabel(day)}**\n");

        if (onDay.Count == 0)
            text.Append("No events on this day.");

        foreach (var e in onDay)
        {
            text.Append(Line(e, guildId));
            if (e.End is { } end)
                text.Append($" until <t:{end.ToUnixTimeSeconds()}:t>");
            text.Append('\n');
        }

        var result = text.ToString();
        return result.Length <= MessageFormatter.MaxLength ? result : result[..(MessageFormatter.MaxLength - 1)] + "…";
    }

    /// <summary>"• [Title](link to the Apollo post) — when · in how long".</summary>
    private static string Line(CalendarEvent e, ulong guildId)
    {
        // Inside a link's text Discord shows "\[" as typed, so brackets become look-alikes instead of escapes.
        var title = MessageFormatter.NeutraliseMassMentions(
            MessageFormatter.EscapeMarkdown(e.Title.Replace('[', '［').Replace(']', '］')));
        var start = e.Start.ToUnixTimeSeconds();
        var link = $"https://discord.com/channels/{guildId}/{e.ChannelId}/{e.MessageId}";
        return $"• [{title}]({link}) — <t:{start}:F> · <t:{start}:R>";
    }

    /// <summary>Footers show plain text only: no markdown, no timestamps.</summary>
    private static string Footer(CalendarTimeZone zone, bool zoneFallback)
    {
        var where = zoneFallback ? "UTC (this computer does not know " + zone.Label + ")" : zone.Label;
        return $"Days on the picture follow {where} time; the times above are in yours";
    }

    /// <summary>Cuts whole lines off the end so the description stays within Discord's limit.</summary>
    private static string Fit(string text)
    {
        if (text.Length <= MaxDescription)
            return text;

        var lines = text.Split('\n').ToList();
        while (lines.Count > 1 && string.Join('\n', lines).Length > MaxDescription - 2)
            lines.RemoveAt(lines.Count - 1);

        return string.Join('\n', lines.Append("…"));
    }

    // --- Controls -------------------------------------------------------------------------------

    /// <summary>
    /// ◀ previous month · Theme · next month ▶, and a menu of the month's days with events.
    /// <paramref name="current"/> is this month in the calendar's zone; browsing stops <see cref="MonthRange"/> away.
    /// </summary>
    public static MessageComponent Controls(
        int year, int month, string themeKey, string zoneKey, DateOnly current, IReadOnlyList<CalendarEvent> events, TimeZoneInfo tz)
    {
        var shown = new DateOnly(year, month, 1);
        var first = new DateOnly(current.Year, current.Month, 1);
        var previous = shown.AddMonths(-1);
        var next = shown.AddMonths(1);

        CalendarCustomId Id(CalendarAction action, DateOnly m) => new(action, m.Year, m.Month, themeKey, zoneKey);

        var builder = new ComponentBuilder()
            .WithButton($"◀ {CalendarRenderer.MonthName(previous.Month)}", Id(CalendarAction.Show, previous).ToString(),
                ButtonStyle.Secondary, disabled: previous < first.AddMonths(-MonthRange), row: 0)
            .WithButton("Theme", Id(CalendarAction.Themes, shown).ToString(), ButtonStyle.Secondary, row: 0)
            .WithButton($"{CalendarRenderer.MonthName(next.Month)} ▶", Id(CalendarAction.Show, next).ToString(),
                ButtonStyle.Secondary, disabled: next > first.AddMonths(MonthRange), row: 0);

        // Menus hold at most 25 options; a month with more event days lists the first 25.
        var days = events
            .Select(e => CalendarPage.LocalDay(e.Start, tz))
            .Where(d => d.Year == year && d.Month == month)
            .GroupBy(d => d)
            .OrderBy(g => g.Key)
            .Take(25)
            .ToList();

        if (days.Count > 0)
        {
            var menu = new SelectMenuBuilder()
                .WithCustomId(Id(CalendarAction.Day, shown).ToString())
                .WithPlaceholder("Day details…");

            foreach (var g in days)
            {
                menu.AddOption(DayLabel(g.Key), g.Key.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    g.Count() == 1 ? "1 event" : $"{g.Count()} events");
            }

            builder.WithSelectMenu(menu, row: 1);
        }

        return builder.Build();
    }

    /// <summary>"Thursday, October 8", in English like the rest of the bot.</summary>
    private static string DayLabel(DateOnly day) =>
        day.ToDateTime(TimeOnly.MinValue).ToString("dddd, MMMM d", CultureInfo.InvariantCulture);

    /// <summary>The theme menu, opened by the Theme button.</summary>
    public static MessageComponent ThemeMenu(int year, int month, string currentTheme, string zoneKey)
    {
        var menu = new SelectMenuBuilder()
            .WithCustomId(new CalendarCustomId(CalendarAction.Pick, year, month, currentTheme, zoneKey).ToString())
            .WithPlaceholder("Pick a theme…");

        foreach (var theme in CalendarThemes.All)
            menu.AddOption(theme.Label, theme.Key, isDefault: theme.Key == currentTheme);

        return new ComponentBuilder().WithSelectMenu(menu).Build();
    }
}
