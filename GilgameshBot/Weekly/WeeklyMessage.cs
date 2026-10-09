using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Discord;
using GilgameshBot.Calendar;
using GilgameshBot.Relay;

namespace GilgameshBot.Weekly;

/// <summary>What the officers wrote in the weekly's form. Empty strings for sections left blank.</summary>
public sealed record WeeklySections(string GameNews, string FcNews, string OfficerNotes, string FatCatSays)
{
    public bool IsEmpty => new[] { GameNews, FcNews, OfficerNotes, FatCatSays }.All(s => s.Length == 0);
}

/// <summary>Which issue a weekly is and the week it covers.</summary>
/// <param name="Number">Issue number.</param>
/// <param name="First">First day of the week, in the calendar's time zone (the day it goes out).</param>
public sealed record WeeklyIssue(int Number, DateOnly First)
{
    public DateOnly Last => First.AddDays(6);

    /// <summary>The planned day of the next issue: a week after this one.</summary>
    public DateOnly Next => First.AddDays(7);
}

/// <summary>
/// The Fat Cat Weekly as a Discord message: the form that collects it, the embeds and pictures
/// it is made of, and the custom ids of its buttons.
/// </summary>
/// <remarks>
/// <para>
/// The message is the header picture, as a plain attachment above everything, and up to five
/// embeds: this week's events, game news, FC news, notes from the officers and "The Fat Cat
/// says:". Empty ones are left out: a week without events, a section left blank. The footer
/// goes on the last embed there is.
/// </para>
/// <para>
/// No state is kept anywhere else: the preview message carries the officers' sections, and the
/// posted weekly carries its issue number and week in its footer. A plugin that approves a
/// preview, or refreshes a posted weekly's events, rebuilds the message from those.
/// </para>
/// </remarks>
public static partial class WeeklyMessage
{
    public const string Name = "The Fat Cat Weekly";

    // Custom ids. Everything a click needs travels in the id, so any plugin can answer it.
    public const string Prefix = "gbweekly:";
    public const string FillId = Prefix + "fill:";
    public const string ModalId = Prefix + "modal:";
    public const string ApproveId = Prefix + "approve";
    public const string RejectId = Prefix + "reject";
    public const string DeleteId = Prefix + "delete:";
    private const string ClaimPrefix = Prefix + "claim:";

    private const string GameField = "game";
    private const string FcField = "fc";
    private const string NotesField = "notes";
    private const string SaysField = "says";

    // The form's limits keep the whole message under Discord's 6000 characters across its embeds:
    // these add up to 3700, the events take at most MaxEvents (+ one "…and N more" line), the
    // width padding about 260 and the titles and footer about 150 more: about 5600 at worst.
    private const int MaxGame = 1000;
    private const int MaxFc = 1000;
    private const int MaxNotes = 1200;
    private const int MaxSays = 500;
    private const int MaxEvents = 1400;

    private const string EventsTitle = "This week's events";
    private const string GameTitle = "Game news";
    private const string FcTitle = "FC news";
    private const string NotesTitle = "Notes from the officers";
    private const string SaysTitle = "The Fat Cat says:";
    private const string FooterPrefix = Name + " · Issue No. ";

    private static readonly Color HeaderColor = new(0xD9, 0x89, 0x3A);
    private static readonly Color EventsColor = new(0x9B, 0x7B, 0xEA);
    private static readonly Color GameColor = new(0x4F, 0x8E, 0xF7);
    private static readonly Color FcColor = new(0x9B, 0x7B, 0xEA);
    private static readonly Color NotesColor = new(0xED, 0x42, 0x45);

    private static readonly string[] ManualTitles = [GameTitle, FcTitle, NotesTitle, SaysTitle];

    // --- The form -------------------------------------------------------------------------------

    /// <summary>
    /// The form. <paramref name="fillMessageId"/> is the "Write the weekly" message it was opened
    /// from (0 for /weekly), deleted once the preview is out.
    /// </summary>
    public static Modal Form(bool draft, ulong fillMessageId) =>
        new ModalBuilder()
            .WithTitle(draft ? Name + " (draft)" : Name)
            .WithCustomId($"{ModalId}{(draft ? 1 : 0)}:{fillMessageId}")
            .AddTextInput("Game news", GameField, TextInputStyle.Paragraph,
                placeholder: "Patch, Live Letter or maintenance: a summary and a link to the official notes.", maxLength: MaxGame, required: false)
            .AddTextInput("FC news", FcField, TextInputStyle.Paragraph,
                placeholder: "Welcome aboard, new members! Achievements, ranks, house news…", maxLength: MaxFc, required: false)
            .AddTextInput("Notes from the officers", NotesField, TextInputStyle.Paragraph,
                placeholder: "Your notes. Sign them, e.g. \"From Name\".", maxLength: MaxNotes, required: false)
            .AddTextInput("The Fat Cat says", SaysField, TextInputStyle.Paragraph,
                placeholder: "Quote or tip of the week.", maxLength: MaxSays, required: false)
            .Build();

    /// <summary>Reads a submitted form: (draft, the message it was opened from, the sections). False when the id is not the weekly's.</summary>
    public static bool TryReadForm(string customId, Func<string, string?> value, out bool draft, out ulong fillMessageId, out WeeklySections sections)
    {
        draft = false;
        fillMessageId = 0;
        sections = new WeeklySections("", "", "", "");

        if (!customId.StartsWith(ModalId, StringComparison.Ordinal))
            return false;

        var parts = customId[ModalId.Length..].Split(':');
        if (parts.Length != 2 || parts[0] is not ("0" or "1") || !ulong.TryParse(parts[1], out fillMessageId))
            return false;

        draft = parts[0] == "1";
        sections = new WeeklySections(
            Clean(value(GameField), MaxGame), Clean(value(FcField), MaxFc),
            Clean(value(NotesField), MaxNotes), Clean(value(SaysField), MaxSays));
        return true;
    }

    /// <summary>Trimmed, capped, and never a mass mention. Officers' markdown is kept: it is theirs to format.</summary>
    private static string Clean(string? text, int max)
    {
        // Neutralised first: it adds characters, and the cap keeps the message within Discord's limit.
        var trimmed = MessageFormatter.NeutraliseMassMentions((text ?? string.Empty).Replace("\r\n", "\n").Trim());
        if (trimmed.Length > max)
            trimmed = trimmed[..(char.IsHighSurrogate(trimmed[max - 1]) ? max - 1 : max)];

        return trimmed;
    }

    /// <summary>The sections as text, to hand back to an officer when their weekly could not be previewed.</summary>
    public static string AsText(WeeklySections s) =>
        $"Game news:\n{s.GameNews}\n\nFC news:\n{s.FcNews}\n\n"
        + $"Notes from the officers:\n{s.OfficerNotes}\n\nThe Fat Cat says:\n{s.FatCatSays}\n";

    // --- Embeds ---------------------------------------------------------------------------------

    /// <summary>The officers' embeds that have text: game news, FC news, notes and "The Fat Cat says:".</summary>
    public static List<EmbedBuilder> Manual(WeeklySections s)
    {
        var embeds = new List<EmbedBuilder>();

        if (s.GameNews.Length > 0)
            embeds.Add(new EmbedBuilder().WithTitle(GameTitle).WithDescription(s.GameNews).WithColor(GameColor));

        if (s.FcNews.Length > 0)
            embeds.Add(new EmbedBuilder().WithTitle(FcTitle).WithDescription(s.FcNews).WithColor(FcColor));

        if (s.OfficerNotes.Length > 0)
            embeds.Add(new EmbedBuilder().WithTitle(NotesTitle).WithDescription(s.OfficerNotes).WithColor(NotesColor));

        if (s.FatCatSays.Length > 0)
            embeds.Add(new EmbedBuilder().WithTitle(SaysTitle).WithDescription($"*“{s.FatCatSays}”*").WithColor(HeaderColor));

        return embeds;
    }

    /// <summary>
    /// The officers' embeds of a preview or a posted weekly, as they were written: everything but
    /// the header and the events. The pictures are put back by <see cref="Assemble"/>.
    /// </summary>
    public static List<EmbedBuilder> Manual(IMessage message)
    {
        var embeds = message.Embeds
            .Where(e => e.Title is { } t && ManualTitles.Contains(t) && !string.IsNullOrEmpty(Unpad(e.Description)))
            .Select(e =>
            {
                var b = new EmbedBuilder().WithTitle(e.Title);
                if (Unpad(e.Description) is { Length: > 0 } text)
                    b.WithDescription(text);
                if (e.Color is { } c)
                    b.WithColor(c);
                return b;
            })
            .ToList();

        return embeds;
    }

    /// <summary>
    /// The whole message: events, the officers' embeds and the footer, plus the files they show.
    /// The header picture (rendered here) is a plain attachment that no embed refers to: Discord
    /// shows such a picture above the embeds, at full width and without a coloured bar, where an
    /// embed would shrink it. Every embed ends with a line of invisible characters (see
    /// <see cref="Pad"/>), so they all stretch to Discord's widest embed instead of fitting their
    /// text. Events are those starting in [<paramref name="from"/>, <paramref name="from"/> + 7
    /// days), and those under way at <paramref name="from"/>.
    /// </summary>
    public static (Embed[] Embeds, List<(string Name, byte[] Bytes)> Files) Assemble(
        List<EmbedBuilder> manual, WeeklyIssue issue, IReadOnlyList<CalendarEvent> events, DateTimeOffset from, ulong guildId)
    {
        var builders = new List<EmbedBuilder>();
        if (EventsText(events, from, guildId) is { } eventsText)
            builders.Add(new EmbedBuilder().WithTitle(EventsTitle).WithDescription(eventsText).WithColor(EventsColor));

        builders.AddRange(manual);

        // Every message needs one embed to carry the footer: an empty week still says so.
        if (builders.Count == 0)
            builders.Add(new EmbedBuilder().WithTitle(EventsTitle).WithDescription(NoEvents).WithColor(EventsColor));

        // The pictures go with their sections; the footer with whatever comes last.
        foreach (var b in builders)
        {
            if (b.Title == GameTitle)
                b.WithThumbnailUrl("attachment://" + WeeklyAssets.EventThumbnail);
            else if (b.Title == SaysTitle)
                b.WithThumbnailUrl("attachment://" + WeeklyAssets.Sticker);
        }

        builders[^1].WithFooter(Footer(issue));

        var embeds = builders
            .Select(b => b.WithDescription(Pad(b.Description, thumbnail: b.ThumbnailUrl is not null)).Build())
            .ToArray();

        var files = new List<(string, byte[])> { (WeeklyHeader.FileName, WeeklyHeader.Render(issue.Number, issue.First, issue.Last)) };
        if (builders.Any(b => b.Title == GameTitle))
            files.Add((WeeklyAssets.EventThumbnail, WeeklyAssets.Get(WeeklyAssets.EventThumbnail)));
        if (builders.Any(b => b.Title == SaysTitle))
            files.Add((WeeklyAssets.Sticker, WeeklyAssets.Get(WeeklyAssets.Sticker)));

        return (embeds, files);
    }

    // --- Width -------------------------------------------------------------------------------

    /// <summary>Braille blank: drawn as nothing, but not whitespace, so Discord neither trims nor collapses it.</summary>
    private const char Blank = '\u2800';

    /// <summary>
    /// Blanks in the last line of an embed: about the width of a desktop embed's text column, so
    /// every embed stretches to the widest Discord draws. Fewer beside a thumbnail, which takes
    /// part of that width. Tuned by eye in Discord.
    /// </summary>
    private const int PadWide = 56;
    private const int PadBesideThumbnail = 45;

    /// <summary><paramref name="text"/> with the line of blanks that widens its embed.</summary>
    private static string Pad(string? text, bool thumbnail)
    {
        var line = new string(Blank, thumbnail ? PadBesideThumbnail : PadWide);
        return string.IsNullOrEmpty(text) ? line : text + "\n" + line;
    }

    /// <summary><paramref name="text"/> without the line <see cref="Pad"/> added; null stays null.</summary>
    private static string? Unpad(string? text)
    {
        if (text is null)
            return null;

        var trimmed = text.TrimEnd(Blank, '\n');
        return trimmed;
    }

    private const string NoEvents = "No events this week yet. Events created with Apollo show up on the calendar.";

    /// <summary>
    /// The events embed's text: one entry per event of the week, then how to read the times. Null
    /// for a week without events: the embed is left out.
    /// </summary>
    public static string? EventsText(IReadOnlyList<CalendarEvent> events, DateTimeOffset from, ulong guildId)
    {
        var until = from.AddDays(7);
        var week = events
            .Where(e => (e.End ?? e.Start) >= from && e.Start < until)
            .OrderBy(e => e.Start)
            .ToList();

        if (week.Count == 0)
            return null;

        var text = new StringBuilder();
        for (var i = 0; i < week.Count; i++)
        {
            var e = week[i];
            // Inside a link's text Discord shows "\[" as typed, so brackets become look-alikes instead of escapes.
            var title = MessageFormatter.NeutraliseMassMentions(
                MessageFormatter.EscapeMarkdown(e.Title.Replace('[', '［').Replace(']', '］')));
            var link = $"https://discord.com/channels/{guildId}/{e.ChannelId}/{e.MessageId}";
            var entry = $"**[{title}]({link})**\n· <t:{e.Start.ToUnixTimeSeconds()}:F>\n\n";

            if (text.Length + entry.Length > MaxEvents)
            {
                text.Append($"…and {week.Count - i} more on the calendar.\n\n");
                break;
            }

            text.Append(entry);
        }

        text.Append("*Times show up in each member's own time zone.*");
        return text.ToString();
    }

    /// <summary>The events embed's text on a weekly, for comparing with a fresh one; null without events.</summary>
    public static string? CurrentEventsText(IMessage message) =>
        Unpad(message.Embeds.FirstOrDefault(e => e.Title == EventsTitle)?.Description) is { } text && text != NoEvents ? text : null;

    // --- Footer: the posted weekly's issue and week ----------------------------------------------

    /// <summary>"The Fat Cat Weekly · Issue No. 12 · Next issue: Oct 19, 2026". Plain text: footers show no markdown.</summary>
    private static string Footer(WeeklyIssue issue) =>
        $"{FooterPrefix}{issue.Number.ToString(CultureInfo.InvariantCulture)} · Next issue: "
        + issue.Next.ToString("MMM d, yyyy", CultureInfo.InvariantCulture);

    [GeneratedRegex(@"^The Fat Cat Weekly · Issue No\. (\d{1,6}) · Next issue: (.+)$")]
    private static partial Regex FooterRegex();

    /// <summary>The issue a weekly message is, read from its footer; null when it is not a weekly.</summary>
    public static WeeklyIssue? ReadIssue(IMessage message)
    {
        var footer = message.Embeds.Select(e => e.Footer?.Text).FirstOrDefault(t => t?.StartsWith(FooterPrefix, StringComparison.Ordinal) == true);
        if (footer is null || FooterRegex().Match(footer) is not { Success: true } m
            || !int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            || !DateOnly.TryParseExact(m.Groups[2].Value, "MMM d, yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var next))
            return null;

        return new WeeklyIssue(number, next.AddDays(-7));
    }

    // --- The approval channel -------------------------------------------------------------------

    /// <summary>The "Write the weekly" message an in-game start posts.</summary>
    public static (string Text, MessageComponent Components) FillPrompt(string who, bool draft) =>
        ($"📰 {MessageFormatter.EscapeMarkdown(who)} is writing {Name}{(draft ? " (draft: nothing will be posted)" : string.Empty)}. "
         + "Click **Write the weekly** to fill it in.",
         new ComponentBuilder().WithButton("Write the weekly", $"{FillId}{(draft ? 1 : 0)}", ButtonStyle.Primary).Build());

    /// <summary>True for a "Write the weekly" button; <paramref name="draft"/> says whether it starts a draft.</summary>
    public static bool IsFill(string customId, out bool draft)
    {
        draft = customId == FillId + "1";
        return draft || customId == FillId + "0";
    }

    /// <summary>The preview's text: whose it is, who should review it, and an early-issue warning.</summary>
    public static string PreviewText(int number, IUser author, IUser? leader, string? warning)
    {
        var text = leader is null
            ? $"📝 **Draft** · Issue No. {number}, by {author.Mention}. Draft mode: nothing is posted and nobody is notified. Delete it when you are done."
            : $"📰 **Preview** · Issue No. {number}, written by {author.Mention}. {leader.Mention}, please review: "
              + "**Approve** posts it, **Reject** cancels it.";

        return warning is null ? text : $"{text}\n⚠️ {warning}";
    }

    /// <summary>Approve / Reject on a preview; Delete draft on a draft.</summary>
    public static MessageComponent PreviewButtons(bool draft, ulong authorId) =>
        draft
            ? DeleteButton(authorId, draft: true)
            : new ComponentBuilder()
                .WithButton("Approve", ApproveId, ButtonStyle.Success)
                .WithButton("Reject", RejectId, ButtonStyle.Danger)
                .Build();

    /// <summary>
    /// Delete draft, or Delete preview on a preview that was posted or rejected, so the approval
    /// channel can be kept clean. For the author (carried in the id) or the FC leader.
    /// </summary>
    public static MessageComponent DeleteButton(ulong authorId, bool draft) =>
        new ComponentBuilder().WithButton(draft ? "Delete draft" : "Delete preview", DeleteId + authorId, ButtonStyle.Secondary).Build();

    [GeneratedRegex(@"\bby <@!?(\d{1,20})>")]
    private static partial Regex AuthorRegex();

    /// <summary>Who wrote a preview, from its text ("written by @someone"); 0 when it cannot be read.</summary>
    public static ulong AuthorOf(IMessage preview) =>
        AuthorRegex().Match(preview.Content) is { Success: true } m && ulong.TryParse(m.Groups[1].Value, out var id) ? id : 0;

    /// <summary>The id an approval claims a preview with: unique per click.</summary>
    public static string ClaimId(ulong interactionId) => ClaimPrefix + interactionId;

    /// <summary>
    /// The preview's buttons while it is being posted: a disabled one carrying the claim, and
    /// Approve, which only takes over a claim that was abandoned (see the service).
    /// </summary>
    public static MessageComponent Posting(string claim) =>
        new ComponentBuilder()
            .WithButton("Posting…", claim, ButtonStyle.Secondary, disabled: true)
            .WithButton("Approve", ApproveId, ButtonStyle.Success)
            .Build();

    /// <summary>The author of a draft, from its Delete draft button; null for other ids.</summary>
    public static ulong? DraftAuthor(string customId) =>
        customId.StartsWith(DeleteId, StringComparison.Ordinal) && ulong.TryParse(customId[DeleteId.Length..], out var id) ? id : null;

    /// <summary>
    /// "It's still early: Issue No. … went out …", when the last issue is less than a week old;
    /// else null. <paramref name="discord"/> writes the time as a Discord timestamp, else as a date.
    /// </summary>
    public static string? EarlyWarning(IMessage? last, WeeklyIssue? lastIssue, DateTimeOffset now, bool discord)
    {
        if (last is null || lastIssue is null || now - last.Timestamp >= TimeSpan.FromDays(7))
            return null;

        var when = discord
            ? $"<t:{last.Timestamp.ToUnixTimeSeconds()}:R>"
            : "on " + last.Timestamp.UtcDateTime.ToString("MMMM d", CultureInfo.InvariantCulture);

        return $"It's still early: Issue No. {lastIssue.Number} went out {when}, and the next one "
               + $"is planned for {lastIssue.Next.ToString("dddd, MMMM d", CultureInfo.InvariantCulture)}.";
    }
}
