using System.Text;
using Dalamud.Plugin.Services;
using Discord;
using Discord.Net;
using Discord.Webhook;
using Discord.WebSocket;
using GilgameshBot.Calendar;

namespace GilgameshBot.Weekly;

/// <summary>Result of a weekly action, as one line for the user. Fixed text plus names from the config.</summary>
public sealed record WeeklyOutcome(bool Ok, string Message);

/// <summary>
/// The Fat Cat Weekly on this session's server: written in a form, previewed in the approval
/// channel, approved by the approver role, posted through the bot's webhook in the announcement
/// channel, and its events kept current every hour.
/// </summary>
/// <remarks>
/// <para>
/// Every connected plugin on the server receives every click, form and <c>/weekly</c>; the first
/// to acknowledge answers (40060 and 10062 swallowed), like the calendar. Nothing is kept in the
/// plugins: the preview message carries the officers' text, and the posted weekly its issue
/// number and week (in its footer), so any plugin can approve a preview or refresh a weekly.
/// </para>
/// <para>
/// No duplicates: Approve removes the preview's buttons before anything else, and the post is
/// skipped when the announcement channel already has a weekly newer than the preview.
/// </para>
/// <para>
/// Never written from bad input: an unreadable Apollo channel stops a preview, a post and a
/// refresh alike, and leaves what is in Discord as it is.
/// </para>
/// </remarks>
public sealed class WeeklyService
{
    /// <summary>How many of the announcement channel's newest messages are searched for the last weekly.</summary>
    private const int SearchWindow = 100;

    private const string ApproveLine = "⏳ ";
    private const string ApprovedLine = "✅ ";
    private const string RejectedLine = "❌ ";
    private const string FailedLine = "⚠️ Posting failed";

    private readonly Configuration config;
    private readonly IPluginLog log;
    private readonly DiscordSocketClient client;
    private readonly ulong guildId;
    private readonly CalendarService calendar;
    private readonly SemaphoreSlim writeGate = new(1, 1);

    /// <param name="guildId">This session's server, kept as an id (see <see cref="CalendarService"/>).</param>
    public WeeklyService(Configuration config, IPluginLog log, DiscordSocketClient client, ulong guildId, CalendarService calendar)
    {
        this.config = config;
        this.log = log;
        this.client = client;
        this.guildId = guildId;
        this.calendar = calendar;
    }

    // --- Starting: from the game and with /weekly ------------------------------------------------

    /// <summary>
    /// From the settings window: posts "Write the weekly" in the approval channel, whose button
    /// opens the form (a form can only open in answer to a click). Says so when it is early.
    /// </summary>
    public async Task<WeeklyOutcome> StartAsync(string who, bool draft, CancellationToken ct)
    {
        try
        {
            var (target, problem) = await WeeklyChecks.ResolveAsync(client, config, guildId, ct);
            if (target is null)
                return new WeeklyOutcome(false, problem!);

            var options = new RequestOptions { CancelToken = ct };
            var (last, lastIssue) = await LastWeeklyAsync(target.Announcement, options);

            var (text, components) = WeeklyMessage.FillPrompt(who, draft);
            await target.Approval.SendMessageAsync(text, components: components, allowedMentions: AllowedMentions.None, options: options);

            var message = $"Posted \"Write the weekly\" in #{target.Approval.Name}. Click it in Discord to fill in the weekly.";
            if (WeeklyMessage.EarlyWarning(last, lastIssue, DateTimeOffset.UtcNow, discord: false) is { } warning)
                message += " " + warning;

            return new WeeklyOutcome(true, message);
        }
        catch (OperationCanceledException)
        {
            return new WeeklyOutcome(false, "Disconnected before the weekly was started.");
        }
        catch (HttpException ex) when (IsForbidden(ex))
        {
            return new WeeklyOutcome(false, "The bot is missing a permission in the weekly's channels.");
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Starting the weekly failed.");
            return new WeeklyOutcome(false, "Starting the weekly failed; see /xllog for details.");
        }
    }

    /// <summary>
    /// <c>/weekly</c>: opens the form straight away — it must be the first answer, within three
    /// seconds, so nothing is read from Discord before it.
    /// </summary>
    public async Task HandleSlashAsync(SocketSlashCommand command, bool draft, CancellationToken ct)
    {
        if (command.GuildId != guildId || ct.IsCancellationRequested)
            return;

        var options = new RequestOptions { CancelToken = ct };
        try
        {
            var approvalId = config.WeeklyApprovalChannelId;
            if (!config.IsWeeklyConfigured)
            {
                await command.RespondAsync("No weekly is set up. Fill in the plugin's Weekly Announcements tab and publish it.",
                    ephemeral: true, allowedMentions: AllowedMentions.None, options: options);
            }
            else if (client.GetChannel(approvalId) is SocketGuildChannel approval && approval.Guild.Id != guildId)
            {
                await command.RespondAsync("The weekly lives on another Discord server. Use /weekly there.",
                    ephemeral: true, allowedMentions: AllowedMentions.None, options: options);
            }
            else
            {
                await command.RespondWithModalAsync(WeeklyMessage.Form(draft, 0), options);
            }
        }
        catch (HttpException ex) when (IsLostRace(ex))
        {
            // Another member's plugin answered first.
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Could not open the weekly form.");
        }
    }

    // --- The form -----------------------------------------------------------------------------

    /// <summary>A submitted form: acknowledged here, previewed off the gateway task.</summary>
    public async Task HandleModalAsync(SocketModal modal, CancellationToken ct)
    {
        if (modal.GuildId != guildId || ct.IsCancellationRequested
            || !WeeklyMessage.TryReadForm(modal.Data.CustomId,
                field => modal.Data.Components.FirstOrDefault(c => c.CustomId == field)?.Value,
                out var draft, out var fillMessageId, out var sections))
            return;

        try
        {
            await modal.DeferAsync(ephemeral: true, new RequestOptions { CancelToken = ct });
        }
        catch (HttpException ex) when (IsLostRace(ex))
        {
            return;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Could not acknowledge the weekly form.");
            return;
        }

        _ = Task.Run(() => PreviewAsync(modal, draft, fillMessageId, sections, ct), CancellationToken.None);
    }

    private async Task PreviewAsync(SocketModal modal, bool draft, ulong fillMessageId, WeeklySections sections, CancellationToken ct)
    {
        var options = new RequestOptions { CancelToken = ct };
        try
        {
            if (sections.IsEmpty)
            {
                await modal.FollowupAsync("The form was empty, so there is nothing to preview.", ephemeral: true,
                    allowedMentions: AllowedMentions.None, options: options);
                return;
            }

            var (target, problem) = await WeeklyChecks.ResolveAsync(client, config, guildId, ct);
            if (target is null)
            {
                await GiveBackAsync(modal, problem!, sections, options);
                return;
            }

            await writeGate.WaitAsync(ct);
            IUserMessage preview;
            string? warning;
            try
            {
                var now = DateTimeOffset.UtcNow;
                var (last, lastIssue) = await LastWeeklyAsync(target.Announcement, options);
                var issue = new WeeklyIssue((lastIssue?.Number ?? 0) + 1, CalendarPage.LocalDay(now, await calendar.TimeZoneAsync(ct)));
                warning = WeeklyMessage.EarlyWarning(last, lastIssue, now, discord: true);

                var (embeds, files) = await BuildAsync(WeeklyMessage.Manual(sections), issue, now, ct);
                var allowed = draft ? AllowedMentions.None : new AllowedMentions { RoleIds = [target.Role.Id] };

                var attachments = files.Select(f => new FileAttachment(new MemoryStream(f.Bytes), f.Name)).ToList();
                try
                {
                    preview = await target.Approval.SendFilesAsync(attachments,
                        WeeklyMessage.PreviewText(issue.Number, modal.User, draft ? null : target.Role, warning),
                        embeds: embeds, allowedMentions: allowed, components: WeeklyMessage.PreviewButtons(draft, modal.User.Id), options: options);
                }
                finally
                {
                    foreach (var a in attachments)
                        a.Dispose();
                }
            }
            catch (CalendarSourceException ex)
            {
                await GiveBackAsync(modal, ex.Message, sections, options);
                return;
            }
            finally
            {
                writeGate.Release();
            }

            // The "Write the weekly" message has done its job.
            if (fillMessageId != 0)
                await DeleteQuietlyAsync(target.Approval, fillMessageId, options);

            var link = $"https://discord.com/channels/{guildId}/{target.Approval.Id}/{preview.Id}";
            var reply = draft
                ? $"Draft previewed: {link}. Nothing is posted; delete it when you are done."
                : $"Preview posted for approval: {link}.";
            if (warning is not null)
                reply += "\n⚠️ " + warning;

            await modal.FollowupAsync(reply, ephemeral: true, allowedMentions: AllowedMentions.None, options: options);
        }
        catch (OperationCanceledException)
        {
            // Disconnecting; the interaction simply times out.
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Previewing the weekly failed.");
            try
            {
                await GiveBackAsync(modal, IsForbiddenException(ex)
                    ? "The bot is missing a permission in the weekly's channels."
                    : "The weekly could not be previewed; see /xllog for details.", sections, options);
            }
            catch
            {
                // Best effort.
            }
        }
    }

    /// <summary>Says why the weekly could not be previewed, and hands the officer's text back so nothing is lost.</summary>
    private static async Task GiveBackAsync(SocketModal modal, string problem, WeeklySections sections, RequestOptions options)
    {
        using var file = new FileAttachment(new MemoryStream(Encoding.UTF8.GetBytes(WeeklyMessage.AsText(sections))), "weekly.txt");
        await modal.FollowupWithFilesAsync([file], $"{problem} Your text is attached, so nothing is lost.", ephemeral: true,
            allowedMentions: AllowedMentions.None, options: options);
    }

    // --- Buttons ------------------------------------------------------------------------------

    /// <summary>
    /// Entry point for button clicks. Returns without touching Discord unless the button is the
    /// weekly's; anything slow happens off the gateway task.
    /// </summary>
    public async Task HandleComponentAsync(SocketMessageComponent component, CancellationToken ct)
    {
        var id = component.Data.CustomId;
        if (component.GuildId != guildId || ct.IsCancellationRequested || !id.StartsWith(WeeklyMessage.Prefix, StringComparison.Ordinal))
            return;

        if (component.ChannelId != config.WeeklyApprovalChannelId || component.Message.Author.Id != client.CurrentUser.Id)
            return;

        var options = new RequestOptions { CancelToken = ct };
        try
        {
            if (WeeklyMessage.IsFill(id, out var draft))
            {
                // The form must be the first answer.
                await component.RespondWithModalAsync(WeeklyMessage.Form(draft, component.Message.Id), options);
                return;
            }

            var roleId = config.WeeklyApproverRoleId;
            var isApprover = component.User is IGuildUser member && member.RoleIds.Contains(roleId);

            if (id is WeeklyMessage.ApproveId or WeeklyMessage.RejectId)
            {
                if (!isApprover)
                {
                    await component.RespondAsync($"Only members with <@&{roleId}> can approve or reject the weekly.",
                        ephemeral: true, allowedMentions: AllowedMentions.None, options: options);
                    return;
                }

                await component.DeferAsync(options: options);
                var approve = id == WeeklyMessage.ApproveId;
                _ = Task.Run(() => approve ? ApproveAsync(component, ct) : RejectAsync(component, ct), CancellationToken.None);
                return;
            }

            if (WeeklyMessage.DraftAuthor(id) is { } author)
            {
                if (component.User.Id != author && !isApprover)
                {
                    await component.RespondAsync($"Only the draft's author or members with <@&{roleId}> can delete it.",
                        ephemeral: true, allowedMentions: AllowedMentions.None, options: options);
                    return;
                }

                await component.DeferAsync(options: options);
                await component.Message.DeleteAsync(options);
            }
        }
        catch (HttpException ex) when (IsLostRace(ex) || ex.HttpCode == System.Net.HttpStatusCode.NotFound)
        {
            // Another member's plugin answered first, or the message is already gone.
        }
        catch (OperationCanceledException)
        {
            // Disconnecting.
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Answering a weekly click failed.");
        }
    }

    private async Task ApproveAsync(SocketMessageComponent component, CancellationToken ct)
    {
        var options = new RequestOptions { CancelToken = ct };
        var preview = component.Message;
        var who = component.User.Mention;
        var head = Head(preview.Content);

        try
        {
            // First of all, take the buttons away: a second approver must not start a second post.
            await component.ModifyOriginalResponseAsync(p =>
            {
                p.Content = $"{head}\n{ApproveLine}Approved by {who}; posting…";
                p.Components = new ComponentBuilder().Build();
                p.AllowedMentions = AllowedMentions.None;
            }, options);

            await writeGate.WaitAsync(ct);
            try
            {
                var (target, problem) = await WeeklyChecks.ResolveAsync(client, config, guildId, ct);
                if (target is null)
                {
                    await RestoreAsync(component, head, problem!, options);
                    return;
                }

                var (last, lastIssue) = await LastWeeklyAsync(target.Announcement, options);
                if (last is not null && last.Timestamp > preview.Timestamp)
                {
                    await component.ModifyOriginalResponseAsync(p =>
                    {
                        p.Content = $"{head}\n{ApprovedLine}Approved by {who}, but {Link(last)} went out after this preview was written, "
                                    + "so it was not posted. Write a new one if it should still go out.";
                        p.AllowedMentions = AllowedMentions.None;
                    }, options);
                    return;
                }

                var now = DateTimeOffset.UtcNow;
                var issue = new WeeklyIssue((lastIssue?.Number ?? 0) + 1, CalendarPage.LocalDay(now, await calendar.TimeZoneAsync(ct)));
                var (embeds, files) = await BuildAsync(WeeklyMessage.Manual(preview), issue, now, ct);

                var webhook = await WebhookAsync(target.Announcement, create: true, options)
                              ?? throw new InvalidOperationException("The webhook could not be created.");

                ulong postedId;
                var attachments = files.Select(f => new FileAttachment(new MemoryStream(f.Bytes), f.Name)).ToList();
                try
                {
                    using var hook = new DiscordWebhookClient(webhook);
                    postedId = await hook.SendFilesAsync(attachments, text: null, embeds: embeds, username: WeeklyMessage.Name,
                        options: options, allowedMentions: AllowedMentions.None);
                }
                finally
                {
                    foreach (var a in attachments)
                        a.Dispose();
                }

                var link = $"https://discord.com/channels/{guildId}/{target.Announcement.Id}/{postedId}";
                log.Information("Posted The Fat Cat Weekly issue {Issue} in #{Channel}.", issue.Number, target.Announcement.Name);

                await component.ModifyOriginalResponseAsync(p =>
                {
                    p.Content = $"{head}\n{ApprovedLine}Approved by {who}: posted as Issue No. {issue.Number}, {link}";
                    p.AllowedMentions = AllowedMentions.None;
                }, options);
            }
            finally
            {
                writeGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            await RestoreQuietlyAsync(component, head, "the plugin disconnected before it was posted");
        }
        catch (CalendarSourceException ex)
        {
            await RestoreQuietlyAsync(component, head, ex.Message);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Posting the weekly failed.");
            await RestoreQuietlyAsync(component, head, IsForbiddenException(ex)
                ? "the bot is missing a permission in the weekly's channels"
                : "see /xllog of the plugin that tried");
        }
    }

    private async Task RejectAsync(SocketMessageComponent component, CancellationToken ct)
    {
        try
        {
            var head = Head(component.Message.Content);
            await component.ModifyOriginalResponseAsync(p =>
            {
                p.Content = $"{head}\n{RejectedLine}Rejected by {component.User.Mention}. Nothing was posted.";
                p.Components = new ComponentBuilder().Build();
                p.AllowedMentions = AllowedMentions.None;
            }, new RequestOptions { CancelToken = ct });
        }
        catch (OperationCanceledException)
        {
            // Disconnecting.
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Rejecting the weekly failed.");
        }
    }

    /// <summary>Puts Approve / Reject back after a failed post, with the reason.</summary>
    private static Task RestoreAsync(SocketMessageComponent component, string head, string reason, RequestOptions options) =>
        component.ModifyOriginalResponseAsync(p =>
        {
            p.Content = $"{head}\n{FailedLine}: {reason.TrimEnd('.')}. Try again.";
            p.Components = WeeklyMessage.PreviewButtons(false, 0);
            p.AllowedMentions = AllowedMentions.None;
        }, options);

    private async Task RestoreQuietlyAsync(SocketMessageComponent component, string head, string reason)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await RestoreAsync(component, head, reason, new RequestOptions { CancelToken = cts.Token });
        }
        catch (Exception ex)
        {
            log.Debug(ex, "Could not put the weekly preview's buttons back.");
        }
    }

    /// <summary>The preview's own text, without the status lines earlier clicks added.</summary>
    private static string Head(string content) =>
        string.Join('\n', content.Split('\n').Where(line =>
            !line.StartsWith(ApproveLine, StringComparison.Ordinal)
            && !line.StartsWith(ApprovedLine, StringComparison.Ordinal)
            && !line.StartsWith(RejectedLine, StringComparison.Ordinal)
            && !line.StartsWith(FailedLine, StringComparison.Ordinal)));

    // --- Hourly refresh -----------------------------------------------------------------------

    /// <summary>Keeps the events of the newest weekly current while its week lasts: shortly after connecting, then hourly.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            // Spread, so plugins connecting together rarely meet (an unchanged weekly is never written anyway).
            await Task.Delay(TimeSpan.FromSeconds(60 + Random.Shared.Next(120)), ct);

            while (!ct.IsCancellationRequested)
            {
                if (config.IsWeeklyConfigured)
                    await RefreshAsync(ct);

                var now = DateTimeOffset.UtcNow;
                var next = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, TimeSpan.Zero).AddHours(1);
                await Task.Delay(next - now + TimeSpan.FromSeconds(240 + Random.Shared.Next(240)), ct);
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
        catch (Exception ex)
        {
            log.Warning(ex, "The weekly loop stopped.");
        }
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        var options = new RequestOptions { CancelToken = ct };
        try
        {
            var (target, problem) = await WeeklyChecks.ResolveAsync(client, config, guildId, ct);
            if (target is null)
            {
                if (problem != WeeklyChecks.NotHere)
                    log.Debug("Weekly: {Problem}", problem!);
                return;
            }

            await writeGate.WaitAsync(ct);
            try
            {
                if (await WebhookAsync(target.Announcement, create: false, options) is not { } webhook)
                    return;

                var (last, issue) = await LastWeeklyAsync(target.Announcement, options);
                if (last is null || issue is null || last.Author.Id != webhook.Id || DateTimeOffset.UtcNow - last.Timestamp >= TimeSpan.FromDays(7))
                    return;

                var events = await EventsAsync(ct);
                if (WeeklyMessage.EventsText(events, last.Timestamp, guildId) == WeeklyMessage.CurrentEventsText(last))
                    return;

                var (embeds, files) = WeeklyMessage.Assemble(WeeklyMessage.Manual(last), issue, events, last.Timestamp, guildId);
                var attachments = files.Select(f => new FileAttachment(new MemoryStream(f.Bytes), f.Name)).ToList();
                try
                {
                    using var hook = new DiscordWebhookClient(webhook);
                    await hook.ModifyMessageAsync(last.Id, p =>
                    {
                        p.Embeds = embeds;
                        p.Attachments = attachments;
                        p.AllowedMentions = AllowedMentions.None;
                    }, options);
                }
                finally
                {
                    foreach (var a in attachments)
                        a.Dispose();
                }

                log.Information("Updated the events of The Fat Cat Weekly issue {Issue}.", issue.Number);
            }
            finally
            {
                writeGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (CalendarSourceException ex)
        {
            log.Warning("Weekly: {Message}", ex.Message);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Refreshing the weekly's events failed.");
        }
    }

    // --- Shared helpers -----------------------------------------------------------------------

    /// <summary>The message for <paramref name="issue"/>, with the events of the week from <paramref name="from"/>.</summary>
    private async Task<(Embed[] Embeds, List<(string Name, byte[] Bytes)> Files)> BuildAsync(
        List<EmbedBuilder> manual, WeeklyIssue issue, DateTimeOffset from, CancellationToken ct)
    {
        var events = await EventsAsync(ct);
        return WeeklyMessage.Assemble(manual, issue, events, from, guildId);
    }

    /// <summary>The calendar's Apollo events; none when no Apollo channel is set. Throws <see cref="CalendarSourceException"/> on an unreadable one.</summary>
    private async Task<List<CalendarEvent>> EventsAsync(CancellationToken ct)
    {
        var sources = config.ApolloChannelIds.ToList();
        return sources.Count == 0 ? [] : await ApolloReader.ReadAsync(client, guildId, sources, ct);
    }

    /// <summary>
    /// The newest weekly in the announcement channel and its issue: a webhook message named
    /// The Fat Cat Weekly with a weekly footer. Any webhook counts, so a recreated webhook keeps
    /// the numbering; only the bot's own is ever edited.
    /// </summary>
    private static async Task<(IMessage? Message, WeeklyIssue? Issue)> LastWeeklyAsync(ITextChannel channel, RequestOptions options)
    {
        // Through IMessageChannel so the (empty) message cache is never preferred.
        var messages = await ((IMessageChannel)channel).GetMessagesAsync(SearchWindow, CacheMode.AllowDownload, options).FlattenAsync();

        foreach (var m in messages.OrderByDescending(m => m.Id))
        {
            if (m.Author.IsWebhook && m.Author.Username == WeeklyMessage.Name && WeeklyMessage.ReadIssue(m) is { } issue)
                return (m, issue);
        }

        return (null, null);
    }

    /// <summary>
    /// The bot's webhook in <paramref name="channel"/>. With <paramref name="create"/> it is created
    /// when missing (named The Fat Cat Weekly, with the Fat Cat icon) and its name and avatar are
    /// put back when someone removed them; without, null when there is none.
    /// </summary>
    private async Task<IWebhook?> WebhookAsync(ITextChannel channel, bool create, RequestOptions options)
    {
        var botId = client.CurrentUser.Id;
        var ours = (await channel.GetWebhooksAsync(options))
            .Where(h => h.Creator?.Id == botId && !string.IsNullOrEmpty(h.Token))
            .OrderByDescending(h => h.Name == WeeklyMessage.Name)
            .FirstOrDefault();

        if (!create)
            return ours;

        if (ours is null)
        {
            using var icon = new MemoryStream(WeeklyAssets.Get(WeeklyAssets.Icon));
            log.Information("Creating The Fat Cat Weekly's webhook in #{Channel}.", channel.Name);
            return await channel.CreateWebhookAsync(WeeklyMessage.Name, icon, options);
        }

        if (ours.Name != WeeklyMessage.Name || ours.AvatarId is null)
        {
            using var icon = new MemoryStream(WeeklyAssets.Get(WeeklyAssets.Icon));
            await ours.ModifyAsync(p =>
            {
                p.Name = WeeklyMessage.Name;
                p.Image = new Image(icon);
            }, options);
        }

        return ours;
    }

    private static string Link(IMessage message) =>
        $"https://discord.com/channels/{((IGuildChannel)message.Channel).GuildId}/{message.Channel.Id}/{message.Id}";

    private static async Task DeleteQuietlyAsync(ITextChannel channel, ulong messageId, RequestOptions options)
    {
        try
        {
            await channel.DeleteMessageAsync(messageId, options);
        }
        catch (HttpException)
        {
            // Already gone, or not ours to delete.
        }
    }

    /// <summary>Another plugin acknowledged first (40060), or the interaction expired while it did (10062).</summary>
    private static bool IsLostRace(HttpException ex) =>
        ex.DiscordCode is DiscordErrorCode.InteractionHasAlreadyBeenAcknowledged or DiscordErrorCode.UnknownInteraction;

    private static bool IsForbidden(HttpException ex) =>
        ex.DiscordCode is DiscordErrorCode.MissingPermissions or DiscordErrorCode.InsufficientPermissions
        || ex.HttpCode == System.Net.HttpStatusCode.Forbidden;

    private static bool IsForbiddenException(Exception ex) => ex is HttpException http && IsForbidden(http);
}
