using System.Collections.Concurrent;
using System.Globalization;
using Dalamud.Plugin.Services;
using Discord;
using Discord.Net;
using Discord.WebSocket;
using GilgameshBot.Calendar.Rendering;
using GilgameshBot.Relay;

namespace GilgameshBot.Calendar;

/// <summary>Result of a calendar update, as one line for the user. Fixed text plus names from the config.</summary>
public sealed record CalendarOutcome(bool Ok, string Message);

/// <summary>
/// Keeps the Free Company calendar of one Discord server: the calendar message in its channel,
/// refreshed every hour, and the answers to its buttons and menus.
/// </summary>
/// <remarks>
/// <para>
/// One calendar for the whole Free Company, every branch included, with the events of every
/// configured Apollo channel. It is kept by the plugins connected to the calendar channel's server.
/// </para>
/// <para>
/// Every leader on the server runs the hourly loop, and nobody coordinates: before writing, a
/// plugin reads the calendar message and leaves it alone when the bot edited it within the last
/// <see cref="FreshFor"/>. Two plugins that still write at the same moment write the same thing
/// (the picture is seeded by month and theme), and a duplicate message is deleted by the next pass.
/// </para>
/// <para>
/// The calendar message is the bot's message that starts with <see cref="CalendarMessage.Marker"/>:
/// the channel's first message when the channel was empty, pinned otherwise — so members can
/// talk in the channel without pushing it out of reach. Its buttons carry the calendar's settings
/// (see <see cref="CalendarCustomId"/>).
/// </para>
/// <para>
/// A calendar is never written from bad input: an Apollo channel that cannot be read leaves the
/// last calendar in place instead of posting an empty month.
/// </para>
/// </remarks>
public sealed class CalendarService
{
    /// <summary>A calendar edited more recently than this is not redrawn by the hourly loop.</summary>
    private static readonly TimeSpan FreshFor = TimeSpan.FromMinutes(55);

    /// <summary>How long button clicks reuse the events read last.</summary>
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(10);

    /// <summary>How far back stray copies of the calendar message are looked for.</summary>
    private const int StrayWindow = 50;

    private const string PermissionHint =
        "The bot needs View Channel, Send Messages, Attach Files and Read Message History in the calendar channel "
        + "(and Pin Messages if the channel was not empty when the calendar was first posted).";

    private readonly Configuration config;
    private readonly IPluginLog log;
    private readonly SocketGuild guild;
    private readonly PresenceCoordinator coordinator;
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private readonly ConcurrentDictionary<ulong, (DateTimeOffset At, List<CalendarEvent> Events)> cache = new();

    public CalendarService(Configuration config, IPluginLog log, SocketGuild guild, PresenceCoordinator coordinator)
    {
        this.config = config;
        this.log = log;
        this.guild = guild;
        this.coordinator = coordinator;
    }

    /// <summary>The calendar: its channel and the Apollo channels it reads.</summary>
    public sealed record Setup(ulong ChannelId, List<ulong> SourceIds);

    /// <summary>The calendar, when one is set up and its channel is on this session's server; null otherwise.</summary>
    public Setup? Current()
    {
        // Read once: the settings window and a configuration sync replace these from other threads.
        var channelId = config.CalendarChannelId;
        var sources = config.ApolloChannelIds.ToList();

        return channelId != 0 && sources.Count > 0 && guild.GetTextChannel(channelId) is not null
            ? new Setup(channelId, sources)
            : null;
    }

    // --- Hourly loop ----------------------------------------------------------------------------

    /// <summary>Refreshes this server's calendars shortly after connecting, then every hour, while this plugin leads.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            // Give the presence queue time to settle on a leader first.
            await Task.Delay(TimeSpan.FromSeconds(45 + Random.Shared.Next(45)), ct);

            while (!ct.IsCancellationRequested)
            {
                if (coordinator.IsLeader)
                {
                    if (Current() is { } setup)
                    {
                        var outcome = await UpdateAsync(setup, force: false, change: null, ct);
                        if (!outcome.Ok)
                            log.Warning("Calendar: {Message}", outcome.Message);
                    }
                }

                await Task.Delay(UntilNextHour(), ct);
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
        catch (Exception ex)
        {
            log.Warning(ex, "The calendar loop stopped.");
        }
    }

    /// <summary>Just past the next full hour, with a few minutes of spread so plugins rarely meet.</summary>
    private static TimeSpan UntilNextHour()
    {
        var now = DateTimeOffset.UtcNow;
        var next = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, TimeSpan.Zero).AddHours(1);
        return next - now + TimeSpan.FromSeconds(15 + Random.Shared.Next(225));
    }

    // --- Writing the calendar message -----------------------------------------------------------

    /// <summary>
    /// Redraws the calendar message of <paramref name="setup"/> (or posts it). Skipped when it was
    /// updated within the hour, unless <paramref name="force"/>. <paramref name="change"/> rewrites
    /// the calendar's settings on the way, and implies a redraw.
    /// </summary>
    public async Task<CalendarOutcome> UpdateAsync(
        Setup setup, bool force, Func<CalendarSettings, CalendarSettings>? change, CancellationToken ct)
    {
        // The update edits and deletes the bot's own messages here; in a relay, state or roster
        // channel those would be relayed chat, presence messages or roster reports.
        if (config.IsRelayOrStateChannel(setup.ChannelId) || config.IsRosterChannel(setup.ChannelId))
            return new CalendarOutcome(false, "The calendar channel must not be any branch's relay, state or roster channel.");

        try
        {
            await writeGate.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return new CalendarOutcome(false, "Disconnected before the calendar was updated.");
        }

        try
        {
            if (guild.GetTextChannel(setup.ChannelId) is not { } channel)
                return new CalendarOutcome(false, "The calendar channel was not found, or the bot cannot see it.");

            var perms = guild.CurrentUser.GetPermissions(channel);
            if (!perms.ViewChannel || !perms.SendMessages || !perms.AttachFiles || !perms.ReadMessageHistory)
                return new CalendarOutcome(false, PermissionHint);

            var options = new RequestOptions { CancelToken = ct };
            var found = await FindAsync(channel, options);

            // A calendar that was unpinned is no longer found, but its copy still holds the settings.
            var holder = found.Message ?? found.Strays.OrderByDescending(m => m.Id).FirstOrDefault();
            var stored = holder is not null
                ? CalendarSettings.FromCustomIds(ApolloReader.CustomIds(holder.Components))
                : null;
            var settings = stored ?? CalendarSettings.Default;
            if (change is not null)
            {
                settings = change(settings);
                force = true;
            }

            // Duplicates go even when the calendar itself is fresh: two plugins that posted it at the
            // same moment would otherwise leave both copies until the next redraw.
            if (found.Message is not null)
                await DeleteAsync(found.Strays, options);

            if (!force && found.Message is { } existing
                       && DateTimeOffset.UtcNow - (existing.EditedTimestamp ?? existing.Timestamp) < FreshFor)
                return new CalendarOutcome(true, "The calendar is already up to date.");

            List<CalendarEvent> events;
            try
            {
                events = await ApolloReader.ReadAsync(guild, setup.SourceIds, ct);
            }
            catch (CalendarSourceException ex)
            {
                return new CalendarOutcome(false, ex.Message);
            }

            cache[setup.ChannelId] = (DateTimeOffset.UtcNow, events);

            var (zone, tz, fallback) = Zone(settings.TimeZone);
            var now = DateTimeOffset.UtcNow;
            var today = CalendarPage.LocalDay(now, tz);

            var image = Draw(events, zone, tz, settings.Theme, today.Year, today.Month, now);
            var text = CalendarMessage.ChannelText(events, guild.Id, zone, fallback, now);
            var controls = CalendarMessage.Controls(today.Year, today.Month, settings.Theme, zone.Key, today, events, tz);

            using (var file = new FileAttachment(new MemoryStream(image.Bytes), image.FileName))
            {
                if (found.Message is null)
                {
                    var sent = await channel.SendFileAsync(file, text, allowedMentions: AllowedMentions.None,
                        components: controls, options: options);

                    if (!found.ChannelWasEmpty && await PinOrTakeBackAsync(sent, options) is { } problem)
                        return problem;
                }
                else
                {
                    await found.Message.ModifyAsync(p =>
                    {
                        p.Content = text;
                        p.Attachments = new Optional<IEnumerable<FileAttachment>>([file]);
                        p.Components = controls;
                        p.AllowedMentions = AllowedMentions.None;
                    }, options);
                }
            }

            if (found.Message is null)
                await DeleteAsync(found.Strays, options);

            log.Information("Calendar in #{Channel} updated: {Count} events, theme {Theme}, {Zone}.",
                channel.Name, events.Count, settings.Theme, zone.Key);

            var upcoming = events.Count(e => (e.End ?? e.Start) >= now);
            return new CalendarOutcome(true, upcoming == 1
                ? "Calendar updated: 1 upcoming event."
                : $"Calendar updated: {upcoming} upcoming events.");
        }
        catch (OperationCanceledException)
        {
            return new CalendarOutcome(false, "Disconnected before the calendar was updated.");
        }
        catch (HttpException ex) when (ex.DiscordCode is DiscordErrorCode.MissingPermissions or DiscordErrorCode.InsufficientPermissions
                                           || ex.HttpCode == System.Net.HttpStatusCode.Forbidden)
        {
            return new CalendarOutcome(false, PermissionHint);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Updating the calendar in channel {Channel} failed.", setup.ChannelId);
            return new CalendarOutcome(false, "The calendar update failed; see /xllog for details.");
        }
        finally
        {
            writeGate.Release();
        }
    }

    /// <summary>Pins a freshly posted calendar; on failure deletes it again and says why. Null on success.</summary>
    private async Task<CalendarOutcome?> PinOrTakeBackAsync(IUserMessage sent, RequestOptions options)
    {
        try
        {
            await sent.PinAsync(options);
            return null;
        }
        catch (HttpException ex)
        {
            // Neither first nor pinned, the next update could not find it: take it back.
            log.Warning("Could not pin the calendar message ({Code}).", ex.HttpCode);
            try { await sent.DeleteAsync(); } catch { /* best effort */ }

            return new CalendarOutcome(false,
                "The calendar channel is not empty, so the bot has to pin the calendar and could not. "
                + "Use an empty channel, or give the bot Pin Messages there.");
        }
    }

    private static async Task DeleteAsync(IEnumerable<IMessage> messages, RequestOptions options)
    {
        foreach (var m in messages)
        {
            try
            {
                await m.DeleteAsync(options);
            }
            catch (HttpException ex) when (ex.HttpCode == System.Net.HttpStatusCode.NotFound)
            {
                // Already gone.
            }
        }
    }

    private sealed record Found(IUserMessage? Message, bool ChannelWasEmpty, List<IMessage> Strays);

    /// <summary>
    /// The calendar message (pinned, or else the channel's first message) and any other copy of
    /// it the bot left behind: pinned, or among the newest messages.
    /// </summary>
    private async Task<Found> FindAsync(SocketTextChannel channel, RequestOptions options)
    {
        var botId = guild.CurrentUser.Id;
        bool IsCalendar(IMessage m) => m.Author.Id == botId && m.Content.StartsWith(CalendarMessage.Marker, StringComparison.Ordinal);

        var pinned = (await channel.GetPinnedMessagesAsync(options)).Where(IsCalendar).OrderBy(m => m.Id).ToList();
        var message = pinned.OfType<IUserMessage>().FirstOrDefault();

        var oldest = (await channel.GetMessagesAsync(0, Direction.After, 1, options).FlattenAsync()).ToList();
        var channelWasEmpty = oldest.Count == 0;
        message ??= oldest.Where(IsCalendar).OfType<IUserMessage>().FirstOrDefault();

        var recent = await ((IMessageChannel)channel)
            .GetMessagesAsync(StrayWindow, CacheMode.AllowDownload, options)
            .FlattenAsync();

        var strays = pinned.Concat(recent.Where(IsCalendar))
            .DistinctBy(m => m.Id)
            .Where(m => message is null || m.Id != message.Id)
            .ToList();

        return new Found(message, channelWasEmpty, strays);
    }

    // --- Buttons and menus ----------------------------------------------------------------------

    /// <summary>
    /// Entry point for button clicks and menu picks. Returns without touching Discord unless the
    /// control is a calendar's and this plugin leads; acknowledges, then works off the gateway task.
    /// </summary>
    public async Task HandleComponentAsync(SocketMessageComponent component, CancellationToken ct)
    {
        if (component.GuildId != guild.Id || !coordinator.IsLeader || ct.IsCancellationRequested
            || !CalendarCustomId.TryParse(component.Data.CustomId, out var id))
            return;

        if (Current() is not { } setup || setup.ChannelId != component.ChannelId)
            return;

        var options = new RequestOptions { CancelToken = ct };
        try
        {
            if (id.Action == CalendarAction.Themes)
            {
                // Nothing to read or draw: answer straight away.
                await component.RespondAsync("Pick a theme to see this month in. Only you see it.",
                    components: CalendarMessage.ThemeMenu(id.Year, id.Month, id.Theme, id.TimeZone),
                    ephemeral: true, allowedMentions: AllowedMentions.None, options: options);
                return;
            }

            // A click on a reply only this member sees updates that reply; a click on the channel
            // message (or a day pick anywhere) answers with a new one.
            var inPlace = component.Message.Flags is { } flags && flags.HasFlag(MessageFlags.Ephemeral)
                          && id.Action is CalendarAction.Show or CalendarAction.Pick;

            if (inPlace)
                await component.DeferAsync(options: options);
            else
                await component.DeferLoadingAsync(ephemeral: true, options);
        }
        catch (HttpException ex) when (IsAlreadyAcknowledged(ex))
        {
            // Two branches on one server: the other branch's leader got there first.
            return;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Could not acknowledge a calendar click.");
            return;
        }

        _ = Task.Run(() => AnswerAsync(component, id, setup, ct), CancellationToken.None);
    }

    private async Task AnswerAsync(SocketMessageComponent component, CalendarCustomId id, Setup setup, CancellationToken ct)
    {
        var options = new RequestOptions { CancelToken = ct };
        try
        {
            List<CalendarEvent> events;
            try
            {
                events = await EventsAsync(setup, ct);
            }
            catch (CalendarSourceException ex)
            {
                await component.ModifyOriginalResponseAsync(p => p.Content = ex.Message, options);
                return;
            }

            var (zone, tz, fallback) = Zone(id.TimeZone);
            var now = DateTimeOffset.UtcNow;
            var today = CalendarPage.LocalDay(now, tz);

            if (id.Action == CalendarAction.Day)
            {
                var picked = component.Data.Values?.FirstOrDefault();
                var text = DateOnly.TryParseExact(picked, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
                    ? CalendarMessage.DayText(events, guild.Id, day, tz)
                    : "That day could not be read. Try the menu again.";

                await component.ModifyOriginalResponseAsync(p =>
                {
                    p.Content = text;
                    p.AllowedMentions = AllowedMentions.None;
                }, options);
                return;
            }

            var theme = id.Action == CalendarAction.Pick
                ? CalendarThemes.Normalize(component.Data.Values?.FirstOrDefault())
                : id.Theme;

            // An old button may point further than members can browse: show the nearest allowed month.
            var current = new DateOnly(today.Year, today.Month, 1);
            var month = new DateOnly(id.Year, id.Month, 1);
            if (month < current.AddMonths(-CalendarMessage.MonthRange))
                month = current.AddMonths(-CalendarMessage.MonthRange);
            else if (month > current.AddMonths(CalendarMessage.MonthRange))
                month = current.AddMonths(CalendarMessage.MonthRange);

            var image = Draw(events, zone, tz, theme, month.Year, month.Month, now);
            var reply = CalendarMessage.MonthText(events, guild.Id, month.Year, month.Month, theme, zone, tz, fallback);
            var controls = CalendarMessage.Controls(month.Year, month.Month, theme, zone.Key, today, events, tz);

            using var file = new FileAttachment(new MemoryStream(image.Bytes), image.FileName);
            await component.ModifyOriginalResponseAsync(p =>
            {
                p.Content = reply;
                p.Attachments = new Optional<IEnumerable<FileAttachment>>([file]);
                p.Components = controls;
                p.AllowedMentions = AllowedMentions.None;
            }, options);
        }
        catch (OperationCanceledException)
        {
            // Disconnecting; the interaction simply times out.
        }
        catch (HttpException ex) when (IsAlreadyAcknowledged(ex))
        {
            // Harmless duplicate from a second leader on the same server.
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Answering a calendar click failed.");

            // Never leave the member looking at "thinking…" until Discord gives up.
            try
            {
                await component.ModifyOriginalResponseAsync(p =>
                {
                    p.Content = "The calendar could not be shown. Try again in a moment.";
                    p.AllowedMentions = AllowedMentions.None;
                }, options);
            }
            catch
            {
                // Best effort.
            }
        }
    }

    /// <summary>The events of <paramref name="setup"/>, read again when the copy from the last read is old.</summary>
    private async Task<List<CalendarEvent>> EventsAsync(Setup setup, CancellationToken ct)
    {
        if (cache.TryGetValue(setup.ChannelId, out var hit) && DateTimeOffset.UtcNow - hit.At < CacheFor)
            return hit.Events;

        var events = await ApolloReader.ReadAsync(guild, setup.SourceIds, ct);
        cache[setup.ChannelId] = (DateTimeOffset.UtcNow, events);
        return events;
    }

    private static bool IsAlreadyAcknowledged(HttpException ex) =>
        ex.DiscordCode == DiscordErrorCode.InteractionHasAlreadyBeenAcknowledged;

    // --- /calendar ------------------------------------------------------------------------------

    /// <summary>The calendar a <c>/calendar</c> command is about, or the reason there is none on this server.</summary>
    public (Setup? Setup, string? Problem) Target() =>
        Current() is { } setup
            ? (setup, null)
            : (null, "No calendar is set up on this server. Set the calendar and Apollo channels in the plugin's Calendar tab, and publish them.");

    // --- Shared helpers -------------------------------------------------------------------------

    /// <summary>The calendar's zone; UTC (and a flag saying so) when this system knows neither of its ids.</summary>
    private static (CalendarTimeZone Zone, TimeZoneInfo Tz, bool Fallback) Zone(string key)
    {
        var zone = CalendarTimeZones.FindOrDefault(key);
        var tz = CalendarTimeZones.Resolve(zone);
        return (zone, tz ?? TimeZoneInfo.Utc, tz is null);
    }

    private static CalendarImage Draw(
        List<CalendarEvent> events, CalendarTimeZone zone, TimeZoneInfo tz, string theme, int year, int month, DateTimeOffset now)
    {
        var page = CalendarPage.Build(events, tz, zone.FirstDay, year, month, now);
        return CalendarRenderer.Render(page, CalendarThemes.Resolve(theme, month));
    }
}
