using System.Collections.Concurrent;
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
/// channel, approved by the FC leader, posted through the bot's webhook in the announcement
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

    /// <summary>Previews and approvals under way, so a disconnect lets them finish their last edit first.</summary>
    private readonly ConcurrentDictionary<Task, byte> jobs = new();

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
            await CalendarChannels.ReopenAsync(target.Approval, options);
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

        Track(() => PreviewAsync(modal, draft, fillMessageId, sections, ct));
    }

    private async Task PreviewAsync(SocketModal modal, bool draft, ulong fillMessageId, WeeklySections sections, CancellationToken ct)
    {
        var options = new RequestOptions { CancelToken = ct };
        string? previewed = null;
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
            ulong previewId;
            string? warning;
            try
            {
                var now = DateTimeOffset.UtcNow;
                var (last, lastIssue) = await LastWeeklyAsync(target.Announcement, options);
                var issue = new WeeklyIssue((lastIssue?.Number ?? 0) + 1, CalendarPage.LocalDay(now, await calendar.TimeZoneAsync(ct)));
                warning = WeeklyMessage.EarlyWarning(last, lastIssue, now, discord: true);

                var (embeds, files) = await BuildAsync(WeeklyMessage.Manual(sections), issue, now, ct);
                // Exactly the FC leader, and nobody at all for a draft.
                var allowed = draft ? AllowedMentions.None : new AllowedMentions { UserIds = [target.Leader.Id] };

                // Through the webhook, so the preview is exactly what will go out, name and icon included.
                var webhook = await WebhookAsync(target.ApprovalHome, create: true, options)
                              ?? throw new InvalidOperationException("The webhook could not be created.");

                await CalendarChannels.ReopenAsync(target.Approval, options);
                var attachments = files.Select(f => new FileAttachment(new MemoryStream(f.Bytes), f.Name)).ToList();
                try
                {
                    using var hook = new DiscordWebhookClient(webhook);
                    previewId = await hook.SendFilesAsync(attachments,
                        WeeklyMessage.PreviewText(issue.Number, modal.User, draft ? null : target.Leader, warning),
                        embeds: embeds, username: WeeklyMessage.Name, avatarUrl: webhook.GetAvatarUrl(), options: options,
                        allowedMentions: allowed, components: WeeklyMessage.PreviewButtons(draft, modal.User.Id),
                        threadId: target.ApprovalThreadId);
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

            var link = $"https://discord.com/channels/{guildId}/{target.Approval.Id}/{previewId}";
            previewed = draft
                ? $"Draft previewed: {link}. Nothing is posted; delete it when you are done."
                : $"Preview posted for approval: {link}.";
            if (warning is not null)
                previewed += "\n⚠️ " + warning;

            // The "Write the weekly" message has done its job.
            if (fillMessageId != 0)
                await DeleteQuietlyAsync(target.Approval, fillMessageId, options);

            await modal.FollowupAsync(previewed, ephemeral: true, allowedMentions: AllowedMentions.None, options: options);
        }
        catch (Exception ex)
        {
            if (ex is not OperationCanceledException)
                log.Warning(ex, "Previewing the weekly failed.");

            try
            {
                // Own short timeout: on a disconnect the session's token is already cancelled.
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

                // The preview is out: only say where, never hand the text back for a second one.
                if (previewed is not null)
                {
                    await modal.FollowupAsync(previewed, ephemeral: true, allowedMentions: AllowedMentions.None,
                        options: new RequestOptions { CancelToken = cts.Token });
                    return;
                }

                await GiveBackAsync(modal, ex switch
                {
                    OperationCanceledException => "The plugin disconnected before the weekly was previewed.",
                    _ when IsForbiddenException(ex) => "The bot is missing a permission in the weekly's channels.",
                    _ => "The weekly could not be previewed; see /xllog for details.",
                }, sections, new RequestOptions { CancelToken = cts.Token });
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

        // Discord only sends clicks on the bot's own messages and its webhooks' (previews), so the
        // custom id and the channel are enough. Not the author: in a thread missing from the cache
        // (archived since the last connect) Discord.Net cannot tell a webhook author apart.
        if (component.ChannelId != config.WeeklyApprovalChannelId)
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

            var leaderId = config.FcLeaderId;
            var isApprover = leaderId != 0 && component.User.Id == leaderId;

            if (id is WeeklyMessage.ApproveId or WeeklyMessage.RejectId)
            {
                if (!isApprover)
                {
                    await component.RespondAsync(leaderId != 0
                            ? $"Only the FC leader, <@{leaderId}>, can approve or reject the weekly."
                            : "No FC leader is set (plugin's Advanced tab), so nobody can approve or reject the weekly.",
                        ephemeral: true, allowedMentions: AllowedMentions.None, options: options);
                    return;
                }

                await component.DeferAsync(options: options);
                var approve = id == WeeklyMessage.ApproveId;
                Track(() => approve ? ApproveAsync(component, ct) : RejectAsync(component, ct));
                return;
            }

            if (WeeklyMessage.DraftAuthor(id) is { } author)
            {
                if (component.User.Id != author && !isApprover)
                {
                    await component.RespondAsync("Only its author or the FC leader can delete it.",
                        ephemeral: true, allowedMentions: AllowedMentions.None, options: options);
                    return;
                }

                await component.DeferAsync(options: options);
                Track(() => DeleteDraftAsync(component, ct));
            }
        }
        catch (HttpException ex) when (IsLostRace(ex))
        {
            // Another member's plugin answered first.
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

    private async Task DeleteDraftAsync(SocketMessageComponent component, CancellationToken ct)
    {
        var options = new RequestOptions { CancelToken = ct };
        try
        {
            // Through the interaction: the clicked message is its "original response", whoever
            // posted it (the bot, or its webhook, whose messages the bot itself may not delete).
            await ClickedChannelAsync(component, options);
            await component.DeleteOriginalResponseAsync(options);
        }
        catch (HttpException ex) when (ex.HttpCode == System.Net.HttpStatusCode.NotFound)
        {
            // Already gone: another plugin, or a second click.
        }
        catch (OperationCanceledException)
        {
            // Disconnecting.
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Deleting a weekly draft failed.");
            try
            {
                await component.FollowupAsync("The draft could not be deleted; see /xllog of the plugin that tried.", ephemeral: true,
                    allowedMentions: AllowedMentions.None, options: options);
            }
            catch
            {
                // Best effort.
            }
        }
    }

    /// <summary>
    /// Approve. Every plugin receives every click, and two approvers may click at once, so a post
    /// is claimed on the preview first: the claimer's custom id goes on a disabled "Posting…"
    /// button, and after a pause only the plugin whose claim is still there goes on (the last
    /// write wins, so exactly one does). The announcement channel is checked once more right
    /// before sending.
    /// </summary>
    private async Task ApproveAsync(SocketMessageComponent component, CancellationToken ct)
    {
        var options = new RequestOptions { CancelToken = ct };
        var preview = component.Message;
        var who = component.User.Mention;
        var head = Head(preview.Content);
        var claim = WeeklyMessage.ClaimId(component.Id);
        var claimed = false;
        string? posted = null;

        try
        {
            // Settled already (posted, rejected, or another approver is posting): nothing to do.
            // A "posting" claim left behind by a plugin that crashed mid-post may be taken over.
            if (await FreshAsync(component, options) is not { } fresh)
                return;

            if (HasStatus(fresh) && !IsAbandoned(fresh, component.CreatedAt))
            {
                await component.FollowupAsync("This weekly is already being posted, or was posted or rejected.",
                    ephemeral: true, allowedMentions: AllowedMentions.None, options: options);
                return;
            }

            await component.ModifyOriginalResponseAsync(p =>
            {
                p.Content = $"{head}\n{ApproveLine}Approved by {who}; posting…";
                p.Components = WeeklyMessage.Posting(claim);
                p.AllowedMentions = AllowedMentions.None;
            }, options);
            claimed = true;
            var claimedAt = System.Diagnostics.Stopwatch.StartNew();

            await Task.Delay(ClaimSettle, ct);
            if (await FreshAsync(component, options) is not { } settled
                || !ApolloReader.CustomIds(settled.Components).Contains(claim))
            {
                // Another click won, or the preview was rejected meanwhile: theirs to finish.
                claimed = false;
                return;
            }

            await writeGate.WaitAsync(ct);
            try
            {
                var (target, problem) = await WeeklyChecks.ResolveAsync(client, config, guildId, ct);
                if (target is null)
                {
                    await RestoreAsync(component, head, problem!, options);
                    return;
                }

                var now = DateTimeOffset.UtcNow;
                var (_, lastIssue) = await LastWeeklyAsync(target.Announcement, options);
                var issue = new WeeklyIssue((lastIssue?.Number ?? 0) + 1, CalendarPage.LocalDay(now, await calendar.TimeZoneAsync(ct)));
                var (embeds, files) = await BuildAsync(WeeklyMessage.Manual(preview), issue, now, ct);

                var webhook = await WebhookAsync(target.WebhookHome, create: true, options)
                              ?? throw new InvalidOperationException("The webhook could not be created.");

                // Last look before sending: building took a few seconds.
                var (last, latestIssue) = await LastWeeklyAsync(target.Announcement, options);
                if (last is not null && last.Timestamp > preview.Timestamp)
                {
                    // A slower claim that lost the race: the winner has already said where it went, or
                    // this very preview is what went out, and the line saying so was overwritten.
                    if (await FreshAsync(component, options) is { } current && HasLine(current, ApprovedLine))
                        return;

                    if (SameSections(last, preview))
                    {
                        await component.ModifyOriginalResponseAsync(p =>
                        {
                            p.Content = $"{head}\n{ApprovedLine}Posted as Issue No. {latestIssue?.Number}, {Link(last)}";
                            p.Components = WeeklyMessage.DeleteButton(WeeklyMessage.AuthorOf(preview), draft: false);
                            p.AllowedMentions = AllowedMentions.None;
                        }, options);
                        return;
                    }

                    await component.ModifyOriginalResponseAsync(p =>
                    {
                        p.Content = $"{head}\n{ApprovedLine}Approved by {who}, but {Link(last)} went out after this preview was written, "
                                    + "so it was not posted. Write a new one if it should still go out.";
                        p.Components = WeeklyMessage.DeleteButton(WeeklyMessage.AuthorOf(preview), draft: false);
                        p.AllowedMentions = AllowedMentions.None;
                    }, options);
                    return;
                }

                if (latestIssue?.Number != lastIssue?.Number)
                    throw new InvalidOperationException("The last issue changed while the weekly was being built.");

                // Still ours? A slower click may have claimed it since; then it posts, not this one.
                // And past the deadline another click may take it over: stop rather than overlap.
                if (await FreshAsync(component, options) is not { } stillOurs
                    || !ApolloReader.CustomIds(stillOurs.Components).Contains(claim))
                {
                    claimed = false;
                    return;
                }

                if (claimedAt.Elapsed > ClaimDeadline)
                {
                    await RestoreAsync(component, head, "it took too long to build", options);
                    return;
                }

                ulong postedId;
                var attachments = files.Select(f => new FileAttachment(new MemoryStream(f.Bytes), f.Name)).ToList();
                try
                {
                    // Not cancelled by a disconnect: a send cut off halfway may still have gone out.
                    using var sendCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    using var hook = new DiscordWebhookClient(webhook);
                    await CalendarChannels.ReopenAsync(target.Announcement, new RequestOptions { CancelToken = sendCts.Token });
                    postedId = await hook.SendFilesAsync(attachments, text: null, embeds: embeds, username: WeeklyMessage.Name,
                        avatarUrl: webhook.GetAvatarUrl(), options: new RequestOptions { CancelToken = sendCts.Token },
                        allowedMentions: AllowedMentions.None, threadId: target.ThreadId);
                }
                finally
                {
                    foreach (var a in attachments)
                        a.Dispose();
                }

                var link = $"https://discord.com/channels/{guildId}/{target.Announcement.Id}/{postedId}";
                posted = $"{head}\n{ApprovedLine}Approved by {who}: posted as Issue No. {issue.Number}, {link}";
                log.Information("Posted The Fat Cat Weekly issue {Issue} in #{Channel}.", issue.Number, target.Announcement.Name);

                await component.ModifyOriginalResponseAsync(p =>
                {
                    p.Content = posted;
                    p.Components = WeeklyMessage.DeleteButton(WeeklyMessage.AuthorOf(preview), draft: false);
                    p.AllowedMentions = AllowedMentions.None;
                }, options);
            }
            finally
            {
                writeGate.Release();
            }
        }
        catch (Exception ex)
        {
            if (ex is not OperationCanceledException and not CalendarSourceException)
                log.Warning(ex, "Posting the weekly failed.");

            if (posted is not null)
            {
                // It went out: never offer the buttons again, only say so.
                await EditQuietlyAsync(component, posted, WeeklyMessage.DeleteButton(WeeklyMessage.AuthorOf(preview), draft: false));
            }
            else if (claimed)
            {
                var reason = ex switch
                {
                    OperationCanceledException => "the plugin disconnected before it was posted",
                    CalendarSourceException source => source.Message,
                    _ when IsForbiddenException(ex) => "the bot is missing a permission in the weekly's channels",
                    _ => "see /xllog of the plugin that tried",
                };
                await EditQuietlyAsync(component, $"{head}\n{FailedLine}: {reason.TrimEnd('.')}. Try again.", WeeklyMessage.PreviewButtons(false, 0));
            }
        }
    }

    private async Task RejectAsync(SocketMessageComponent component, CancellationToken ct)
    {
        try
        {
            var options = new RequestOptions { CancelToken = ct };

            // Too late when it is posted, or being posted.
            if (await FreshAsync(component, options) is not { } fresh)
                return;

            if (HasStatus(fresh))
            {
                await component.FollowupAsync("This weekly is already being posted, or was posted or rejected.",
                    ephemeral: true, allowedMentions: AllowedMentions.None, options: options);
                return;
            }

            var head = Head(component.Message.Content);
            await component.ModifyOriginalResponseAsync(p =>
            {
                p.Content = $"{head}\n{RejectedLine}Rejected by {component.User.Mention}. Nothing was posted.";
                p.Components = WeeklyMessage.DeleteButton(WeeklyMessage.AuthorOf(component.Message), draft: false);
                p.AllowedMentions = AllowedMentions.None;
            }, options);
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

    /// <summary>How long a claim on a preview is left to settle before the claimer looks again.</summary>
    private static readonly TimeSpan ClaimSettle = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The clicked preview as it is now in Discord; null when it was deleted. The channel comes
    /// from the click, not the message: an archived thread is not in the cache.
    /// </summary>
    private async Task<IMessage?> FreshAsync(SocketMessageComponent component, RequestOptions options) =>
        await ClickedChannelAsync(component, options) is { } channel
            ? await ((IMessageChannel)channel).GetMessageAsync(component.Message.Id, CacheMode.AllowDownload, options)
            : null;

    /// <summary>The channel or thread of a click, reopened when it is an archived thread so its messages can be edited.</summary>
    private async Task<ITextChannel?> ClickedChannelAsync(SocketMessageComponent component, RequestOptions options)
    {
        if (component.ChannelId is not { } id || await CalendarChannels.ResolveAsync(client, id, options) is not { } channel)
            return null;

        await CalendarChannels.ReopenAsync(channel, options);
        return channel;
    }

    /// <summary>True when <paramref name="posted"/> carries the same officers' sections as <paramref name="preview"/>.</summary>
    private static bool SameSections(IMessage posted, IMessage preview)
    {
        static IEnumerable<string> Key(IMessage m) =>
            WeeklyMessage.Manual(m).Select(e => $"{e.Title}\u0001{e.Description}");

        return Key(posted).SequenceEqual(Key(preview));
    }

    /// <summary>True once a preview is being posted, posted or rejected.</summary>
    private static bool HasStatus(IMessage message) =>
        HasLine(message, ApproveLine) || HasLine(message, ApprovedLine) || HasLine(message, RejectedLine);

    private static bool HasLine(IMessage message, string prefix) =>
        message.Content.Split('\n').Any(line => line.StartsWith(prefix, StringComparison.Ordinal));

    /// <summary>
    /// A "posting" claim nobody finished: still only the claim, untouched for longer than any post
    /// takes. The plugin that made it crashed or lost its connection without putting the buttons back.
    /// </summary>
    /// <remarks><paramref name="clickedAt"/> is Discord's time of the click, not this PC's clock, which may be off.</remarks>
    private static bool IsAbandoned(IMessage message, DateTimeOffset clickedAt) =>
        HasLine(message, ApproveLine) && !HasLine(message, ApprovedLine) && !HasLine(message, RejectedLine)
        && clickedAt - (message.EditedTimestamp ?? message.Timestamp) > AbandonedAfter;

    /// <summary>How long a "posting" claim may sit before another Approve may take it over.</summary>
    private static readonly TimeSpan AbandonedAfter = TimeSpan.FromMinutes(3);

    /// <summary>A claimer that has not sent within this gives up, so a takeover never overlaps it.</summary>
    private static readonly TimeSpan ClaimDeadline = TimeSpan.FromMinutes(2);

    /// <summary>Puts Approve / Reject back after a failed post, with the reason.</summary>
    private static Task RestoreAsync(SocketMessageComponent component, string head, string reason, RequestOptions options) =>
        component.ModifyOriginalResponseAsync(p =>
        {
            p.Content = $"{head}\n{FailedLine}: {reason.TrimEnd('.')}. Try again.";
            p.Components = WeeklyMessage.PreviewButtons(false, 0);
            p.AllowedMentions = AllowedMentions.None;
        }, options);

    /// <summary>Last word on a preview, with its own short timeout: the session's may be cancelled already.</summary>
    private async Task EditQuietlyAsync(SocketMessageComponent component, string content, MessageComponent buttons)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await component.ModifyOriginalResponseAsync(p =>
            {
                p.Content = content;
                p.Components = buttons;
                p.AllowedMentions = AllowedMentions.None;
            }, new RequestOptions { CancelToken = cts.Token });
        }
        catch (Exception ex)
        {
            log.Debug(ex, "Could not update the weekly preview.");
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
                var (last, issue) = await LastWeeklyAsync(target.Announcement, options);
                if (last is null || issue is null || DateTimeOffset.UtcNow - last.Timestamp >= TimeSpan.FromDays(7))
                    return;

                // Only a weekly the bot's own webhook posted can be edited.
                var webhook = (await BotWebhooksAsync(target.WebhookHome, options)).FirstOrDefault(h => h.Id == last.Author.Id);
                if (webhook is null)
                    return;

                var events = await EventsAsync(ct);
                if (WeeklyMessage.EventsText(events, last.Timestamp, guildId) == WeeklyMessage.CurrentEventsText(last))
                    return;

                var (embeds, files) = WeeklyMessage.Assemble(WeeklyMessage.Manual(last), issue, events, last.Timestamp, guildId);
                var attachments = files.Select(f => new FileAttachment(new MemoryStream(f.Bytes), f.Name)).ToList();
                try
                {
                    using var hook = new DiscordWebhookClient(webhook);
                    await CalendarChannels.ReopenAsync(target.Announcement, options);
                    await hook.ModifyMessageAsync(last.Id, p =>
                    {
                        p.Embeds = embeds;
                        p.Attachments = attachments;
                        p.AllowedMentions = AllowedMentions.None;
                    }, options, target.ThreadId);
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
            // Previews come from the same webhook but carry text (who wrote it, its status); a posted
            // weekly has none.
            if (m.Author.IsWebhook && m.Author.Username == WeeklyMessage.Name && string.IsNullOrEmpty(m.Content)
                && WeeklyMessage.ReadIssue(m) is { } issue)
                return (m, issue);
        }

        return (null, null);
    }

    /// <summary>
    /// The bot's webhook in <paramref name="channel"/>. With <paramref name="create"/> it is created
    /// when missing (named The Fat Cat Weekly, with the Fat Cat icon) and its name and avatar are
    /// put back when someone removed them; without, null when there is none.
    /// </summary>
    private async Task<IWebhook?> WebhookAsync(IIntegrationChannel channel, bool create, RequestOptions options)
    {
        var ours = (await BotWebhooksAsync(channel, options))
            .OrderByDescending(h => h.Name == WeeklyMessage.Name)
            .ThenBy(h => h.Id)
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

    /// <summary>The webhooks the bot created in <paramref name="channel"/> and can post with.</summary>
    private async Task<List<IWebhook>> BotWebhooksAsync(IIntegrationChannel channel, RequestOptions options)
    {
        var botId = client.CurrentUser.Id;
        return (await channel.GetWebhooksAsync(options))
            .Where(h => h.Creator?.Id == botId && !string.IsNullOrEmpty(h.Token))
            .ToList();
    }

    /// <summary>Runs <paramref name="work"/> off the gateway task, tracked so a disconnect can wait for it.</summary>
    private void Track(Func<Task> work)
    {
        var task = Task.Run(work, CancellationToken.None);
        jobs[task] = 0;
        _ = task.ContinueWith(t => jobs.TryRemove(t, out _), TaskScheduler.Default);
    }

    /// <summary>
    /// Waits, at most <paramref name="timeout"/>, for previews and approvals under way. Called on
    /// disconnect after the session's token is cancelled and before the client stops, so they can
    /// still hand an officer's text back or put a preview's buttons back.
    /// </summary>
    public async Task DrainAsync(TimeSpan timeout)
    {
        var pending = jobs.Keys.ToList();
        if (pending.Count > 0)
            await Task.WhenAny(Task.WhenAll(pending), Task.Delay(timeout));
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
