using System.Text;
using System.Text.Json;
using Dalamud.Plugin.Services;
using Discord;
using Discord.Net;
using Discord.WebSocket;
using GilgameshBot.Relay;

namespace GilgameshBot.Roster;

/// <summary>Result of a roster scan, as one line for the user. Fixed text plus names from the config.</summary>
public sealed record RosterOutcome(bool Ok, string Message);

/// <summary>
/// Runs a manual roster scan: reads the Free Company's member list from the Lodestone, compares
/// it with the state saved in the roster channel, posts the report and saves the new state.
/// </summary>
/// <remarks>
/// <para>
/// State lives in Discord, not in the plugin config, because every member's plugin may run a
/// scan. The roster channel's state message is authored by the bot, starts with
/// <see cref="StateMarker"/> and carries one <c>roster-{lodestoneId}.json</c> attachment per
/// Free Company. It is the first message of the channel when the channel was empty on the first
/// scan, and pinned otherwise. Only the bot edits it.
/// </para>
/// <para>
/// A scan either completes or changes nothing: the Lodestone list must be complete and the state
/// message must be readable before anything is posted.
/// </para>
/// </remarks>
public static class RosterScanner
{
    private const string StateMarker = "🗂️ **GilgameshBot roster state**";

    private const string PermissionHint =
        "The bot needs View Channel, Send Messages, Attach Files and Read Message History in the roster channel "
        + "(and Pin Messages if the channel was not empty on the first scan).";

    /// <summary>One scan at a time per plugin; scans are manual, so this is all the locking needed.</summary>
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static async Task<RosterOutcome> ScanAsync(
        SocketGuild guild, FcBranch branch, string requestedBy, IPluginLog log, CancellationToken ct)
    {
        if (!branch.IsRosterConfigured)
            return new RosterOutcome(false, $"Roster tracking is not set up for {branch.Name}.");

        if (branch.RosterChannelId == branch.ChannelId || branch.RosterChannelId == branch.StateChannelId)
            return new RosterOutcome(false, "The roster channel must be different from the relay and state channels.");

        if (!await Gate.WaitAsync(0, ct))
            return new RosterOutcome(false, "A roster scan is already running. Try again when it finishes.");

        try
        {
            if (guild.GetTextChannel(branch.RosterChannelId) is not { } channel)
                return new RosterOutcome(false, "The roster channel was not found, or the bot cannot see it.");

            // Without Read Message History Discord answers an empty list instead of an error: the
            // scan would miss its state message and start over every time.
            var perms = guild.CurrentUser.GetPermissions(channel);
            if (!perms.ViewChannel || !perms.SendMessages || !perms.AttachFiles || !perms.ReadMessageHistory)
                return new RosterOutcome(false, PermissionHint);

            var options = new RequestOptions { CancelToken = ct };
            var stored = await LoadStateAsync(channel, guild.CurrentUser.Id, options, ct);

            var fileName = RosterState.FileName(branch.LodestoneId);
            RosterState? previous = null;
            if (stored.Files.TryGetValue(fileName, out var previousJson))
            {
                try
                {
                    previous = JsonSerializer.Deserialize<RosterState>(previousJson, JsonOptions);
                }
                catch (JsonException)
                {
                    return new RosterOutcome(false,
                        $"The saved roster of {branch.Name} could not be read. Nothing was changed; delete the roster state message to start over.");
                }
            }

            List<LodestoneMember> members;
            try
            {
                members = await LodestoneClient.FetchMembersAsync(branch.LodestoneId, ct);
            }
            catch (LodestoneException ex)
            {
                return new RosterOutcome(false, ex.Message);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                return new RosterOutcome(false, "Could not reach the Lodestone. Try again later.");
            }

            var now = DateTime.UtcNow;
            var (state, diff) = RosterUpdater.Apply(previous, branch, members, now);

            stored.Files[fileName] = JsonSerializer.Serialize(state, JsonOptions);
            var report = RosterUpdater.ComposeReport(state, diff, requestedBy, now);

            if (stored.Message is null)
            {
                // First scan in this channel: the state message has to exist (and be findable)
                // before any report is posted under it.
                var created = await CreateStateMessageAsync(channel, stored, options, log);
                if (created is not null)
                    return created;

                await PublishAsync(channel, guild.CurrentUser.Id, state, diff, report, now, options);
            }
            else
            {
                // Report first: if saving fails afterwards, the next scan repeats this report
                // instead of silently losing it.
                await PublishAsync(channel, guild.CurrentUser.Id, state, diff, report, now, options);
                await SaveStateAsync(stored.Message, stored.Files, options);
            }

            log.Information("Roster scan of {Branch}: {Count} members, {Joined} joined, {Left} left, {Starters} in the starter rank.",
                branch.Describe(), state.Members.Count, diff.Joined.Count, diff.Left.Count, diff.Starters.Count);

            return new RosterOutcome(true, diff.IsFirstScan
                ? $"First roster of {branch.Name} recorded: {state.Members.Count} members, {diff.Starters.Count} in {state.StarterRank}."
                : $"Roster of {branch.Name} scanned: {diff.Joined.Count} joined, {diff.Left.Count} left, "
                  + $"{diff.Renamed.Count} renamed, {diff.Starters.Count} in {state.StarterRank}.");
        }
        catch (OperationCanceledException)
        {
            return new RosterOutcome(false, "Disconnected before the roster scan finished.");
        }
        catch (HttpException ex) when (ex.DiscordCode is DiscordErrorCode.MissingPermissions or DiscordErrorCode.InsufficientPermissions
                                           || ex.HttpCode == System.Net.HttpStatusCode.Forbidden)
        {
            return new RosterOutcome(false, PermissionHint);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Roster scan of {Branch} failed.", branch.Describe());
            return new RosterOutcome(false, "The roster scan failed; see /xllog for details.");
        }
        finally
        {
            Gate.Release();
        }
    }

    // --- State message ------------------------------------------------------------------------

    private sealed class StoredState
    {
        public IUserMessage? Message { get; init; }
        public bool ChannelWasEmpty { get; init; }

        /// <summary>Every roster file on the state message, by file name: other branches' too.</summary>
        public Dictionary<string, string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsStateMessage(IMessage message, ulong botId) =>
        message.Author.Id == botId && message.Content.StartsWith(StateMarker, StringComparison.Ordinal);

    private static async Task<StoredState> LoadStateAsync(
        SocketTextChannel channel, ulong botId, RequestOptions options, CancellationToken ct)
    {
        // Pinned when the channel was not empty on the first scan; otherwise the oldest message.
        var pinned = await channel.GetPinnedMessagesAsync(options);
        var message = pinned.FirstOrDefault(m => IsStateMessage(m, botId)) as IUserMessage;

        var channelWasEmpty = false;
        if (message is null)
        {
            var oldest = (await channel.GetMessagesAsync(0, Direction.After, 1, options)
                .FlattenAsync()).ToList();
            channelWasEmpty = oldest.Count == 0;
            message = oldest.FirstOrDefault(m => IsStateMessage(m, botId)) as IUserMessage;
        }

        var stored = new StoredState { Message = message, ChannelWasEmpty = channelWasEmpty };
        if (message is null)
            return stored;

        foreach (var attachment in message.Attachments)
        {
            if (!attachment.Filename.StartsWith("roster-", StringComparison.OrdinalIgnoreCase)
                || !attachment.Filename.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                continue;

            stored.Files[attachment.Filename] = await Http.GetStringAsync(attachment.Url, ct);
        }

        return stored;
    }

    /// <summary>Posts the state message; pins it when it is not the channel's first message. Null on success.</summary>
    private static async Task<RosterOutcome?> CreateStateMessageAsync(
        SocketTextChannel channel, StoredState stored, RequestOptions options, IPluginLog log)
    {
        var attachments = ToAttachments(stored.Files);
        IUserMessage message;
        try
        {
            message = await channel.SendFilesAsync(
                attachments,
                text: DescribeState(stored.Files),
                allowedMentions: AllowedMentions.None,
                options: options);
        }
        finally
        {
            foreach (var a in attachments)
                a.Dispose();
        }

        if (stored.ChannelWasEmpty)
            return null;

        try
        {
            await message.PinAsync(options);
            return null;
        }
        catch (HttpException ex)
        {
            // Neither first nor pinned, the next scan could not find it: take it back.
            log.Warning("Could not pin the roster state message ({Code}).", ex.HttpCode);
            try { await message.DeleteAsync(); } catch { /* best effort */ }

            return new RosterOutcome(false,
                "The roster channel is not empty, so the bot has to pin its state message and could not. "
                + "Use an empty channel, or give the bot Pin Messages there.");
        }
    }

    private static async Task SaveStateAsync(IUserMessage message, Dictionary<string, string> files, RequestOptions options)
    {
        // An edit replaces every attachment, so all branches' files go back up together.
        var attachments = ToAttachments(files);
        try
        {
            await message.ModifyAsync(p =>
            {
                p.Content = DescribeState(files);
                p.Attachments = new Optional<IEnumerable<FileAttachment>>(attachments);
            }, options);
        }
        finally
        {
            foreach (var a in attachments)
                a.Dispose();
        }
    }

    private static List<FileAttachment> ToAttachments(Dictionary<string, string> files) =>
        files.Select(kv => new FileAttachment(new MemoryStream(Encoding.UTF8.GetBytes(kv.Value)), kv.Key)).ToList();

    /// <summary>The state message's text: what it is, and one line per Free Company it holds.</summary>
    private static string DescribeState(Dictionary<string, string> files)
    {
        var lines = new List<string>
        {
            $"{StateMarker} — the saved member lists the roster scans compare against. Kept by the bot: do not edit or delete it.",
        };

        foreach (var json in files.Values)
        {
            try
            {
                if (JsonSerializer.Deserialize<RosterState>(json, JsonOptions) is { } s)
                    lines.Add($"• {MessageFormatter.EscapeMarkdown(s.FcName)} @ {MessageFormatter.EscapeMarkdown(s.World)} — "
                              + $"{s.Members.Count} members, last scan <t:{new DateTimeOffset(DateTime.SpecifyKind(s.LastScanUtc, DateTimeKind.Utc)).ToUnixTimeSeconds()}:f>");
            }
            catch (JsonException)
            {
                // Listed files are informational only.
            }
        }

        return string.Join('\n', lines);
    }

    // --- Report -------------------------------------------------------------------------------

    /// <summary>How far back the channel is searched for this Free Company's report and notice.</summary>
    private const int HistoryDepth = 300;

    /// <summary>
    /// Keeps one report per Free Company: the newest one is edited in place (or reposted when
    /// its length in messages changed), older copies are deleted, and a short notice pointing at
    /// it replaces the previous notice so the channel still shows that a scan happened.
    /// </summary>
    private static async Task PublishAsync(
        SocketTextChannel channel, ulong botId, RosterState state, RosterDiff diff,
        List<string> report, DateTime nowUtc, RequestOptions options)
    {
        var reportTitle = RosterUpdater.ReportTitle(state);
        var noticeTitle = RosterUpdater.NoticeTitle(state);

        var history = (await channel.GetMessagesAsync(HistoryDepth, options).FlattenAsync())
            .Where(m => m.Author.Id == botId)
            .OrderBy(m => m.Id) // oldest first
            .ToList();

        // A report is its titled message plus the untitled parts the bot posted right after it.
        var reports = new List<List<IMessage>>();
        var notices = new List<IMessage>();
        List<IMessage>? current = null;

        foreach (var m in history)
        {
            if (m.Content.StartsWith("📋 **Roster — ", StringComparison.Ordinal))
            {
                current = [m];
                if (m.Content.StartsWith(reportTitle, StringComparison.Ordinal))
                    reports.Add(current);
            }
            else if (m.Content.StartsWith("🔄 **Roster updated — ", StringComparison.Ordinal)
                     || m.Content.StartsWith(StateMarker, StringComparison.Ordinal))
            {
                current = null;
                if (m.Content.StartsWith(noticeTitle, StringComparison.Ordinal))
                    notices.Add(m);
            }
            else
            {
                current?.Add(m);
            }
        }

        var newest = reports.LastOrDefault();
        foreach (var old in reports.Where(r => !ReferenceEquals(r, newest)))
            await DeleteAllAsync(old, options);

        IUserMessage first;
        if (newest is not null && newest.Count == report.Count && newest.All(m => m is IUserMessage))
        {
            for (var i = 0; i < report.Count; i++)
            {
                var text = report[i];
                await ((IUserMessage)newest[i]).ModifyAsync(p =>
                {
                    p.Content = text;
                    p.AllowedMentions = AllowedMentions.None;
                }, options);
            }

            first = (IUserMessage)newest[0];
        }
        else
        {
            // A different number of parts: repost, so no part ends up below another Free
            // Company's report.
            if (newest is not null)
                await DeleteAllAsync(newest, options);

            first = null!;
            foreach (var part in report)
            {
                var sent = await channel.SendMessageAsync(part, allowedMentions: AllowedMentions.None, options: options);
                first ??= sent;
            }
        }

        await DeleteAllAsync(notices, options);
        await channel.SendMessageAsync(
            RosterUpdater.ComposeNotice(state, diff, first.GetJumpUrl(), nowUtc),
            allowedMentions: AllowedMentions.None,
            options: options);
    }

    private static async Task DeleteAllAsync(IEnumerable<IMessage> messages, RequestOptions options)
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
}
