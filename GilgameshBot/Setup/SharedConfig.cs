using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dalamud.Plugin.Services;
using Discord;
using Discord.Net;
using Discord.WebSocket;
using GilgameshBot.Calendar;
using GilgameshBot.Relay;
using GilgameshBot.Weekly;

namespace GilgameshBot.Setup;

/// <summary>
/// The shared configuration as it is stored in a state channel: the branch table and the presence
/// timers, with a revision. There is deliberately no token field — the token only ever travels in
/// a setup code.
/// </summary>
public sealed class SharedConfigFile
{
    [JsonPropertyName("v")] public int Version { get; set; }
    [JsonPropertyName("revision")] public int Revision { get; set; }
    [JsonPropertyName("publishedBy")] public string PublishedBy { get; set; } = string.Empty;
    [JsonPropertyName("publishedAt")] public DateTime PublishedAtUtc { get; set; }
    [JsonPropertyName("heartbeat")] public int Heartbeat { get; set; } = 10;
    [JsonPropertyName("stale")] public int Stale { get; set; } = 20;
    [JsonPropertyName("branches")] public List<SetupBranch>? Branches { get; set; }

    /// <summary>
    /// True on files from plugins that know the calendar settings: theirs is the calendar
    /// everybody follows, none included. A file without it comes from an older plugin, and
    /// applying it keeps the local calendar settings.
    /// </summary>
    [JsonPropertyName("calendar")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? CarriesCalendar { get; set; }

    [JsonPropertyName("calendarChannel")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CalendarChannel { get; set; }

    [JsonPropertyName("apolloChannels")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ApolloChannels { get; set; }

    /// <summary>
    /// True on files from plugins that know the weekly settings, like <see cref="CarriesCalendar"/>:
    /// without it the local weekly settings are kept.
    /// </summary>
    [JsonPropertyName("weekly")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? CarriesWeekly { get; set; }

    [JsonPropertyName("weeklyChannel")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WeeklyChannel { get; set; }

    [JsonPropertyName("weeklyApproval")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WeeklyApproval { get; set; }

    [JsonPropertyName("weeklyRole")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WeeklyRole { get; set; }

    // Parsed, filled in by TryParse.
    [JsonIgnore] public ulong CalendarChannelId { get; set; }
    [JsonIgnore] public List<ulong> ApolloChannelIds { get; set; } = [];
    [JsonIgnore] public WeeklyIds Weekly { get; set; } = WeeklyIds.None;
}

/// <summary>What to tell the officer after publishing. Fixed text plus names from the config.</summary>
public readonly record struct SharedConfigOutcome(bool Ok, string Message);

/// <summary>
/// Keeps the shared configuration in Discord: published by an officer, followed by every plugin.
/// </summary>
/// <remarks>
/// <para>
/// Each distinct state channel holds one bot-authored message that starts with <see cref="Marker"/>
/// and carries <see cref="FileName"/>. It is looked up among the newest messages of the channel,
/// the same window the presence queue reads; the state channel only ever holds a handful of
/// messages, so it stays in view without being pinned. Only the bot's own messages count:
/// anyone else who can post in the channel cannot plant a configuration.
/// </para>
/// <para>
/// A file is used whole or not at all: it goes through the same branch validation as a setup
/// code, and any failure makes it invisible. Publishing is refused unless every branch resolves
/// with the live client, because a bad ID would otherwise reach every plugin and leave them
/// unable to connect — and therefore unable to receive the fix.
/// </para>
/// </remarks>
public static class SharedConfig
{
    public const string Marker = "⚙️ **GilgameshBot shared configuration**";
    public const string FileName = "gilgamesh-config.json";

    private const int FileVersion = 1;

    /// <summary>Same window as the presence queue.</summary>
    private const int FetchWindow = 100;

    private const int MaxFileBytes = 256 * 1024;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>One publish at a time per plugin.</summary>
    private static readonly SemaphoreSlim PublishGate = new(1, 1);

    /// <summary>True for the bot's own shared configuration message.</summary>
    public static bool IsSharedConfigMessage(IMessage message, ulong botId) =>
        message.Author.Id == botId && message.Content is { } c && c.StartsWith(Marker, StringComparison.Ordinal);

    // --- Reading --------------------------------------------------------------------------------

    /// <summary>A configuration message in a state channel; <see cref="File"/> is null when its file is missing or invalid.</summary>
    private sealed record Copy(IUserMessage Message, SharedConfigFile? File);

    /// <summary>
    /// Every configuration message in <paramref name="channel"/>. A download failure throws: a
    /// publish must not mistake an unreadable copy for an absent one.
    /// </summary>
    private static async Task<List<Copy>> ReadChannelAsync(SocketTextChannel channel, ulong botId, CancellationToken ct)
    {
        // Through IMessageChannel so the cache is never preferred: it is empty (MessageCacheSize 0).
        var messages = await ((IMessageChannel)channel)
            .GetMessagesAsync(FetchWindow, CacheMode.AllowDownload, new RequestOptions { CancelToken = ct })
            .FlattenAsync();

        var copies = new List<Copy>();
        foreach (var message in messages.OfType<IUserMessage>().Where(m => IsSharedConfigMessage(m, botId)))
        {
            SharedConfigFile? file = null;
            var attachment = message.Attachments.FirstOrDefault(a => a.Filename.Equals(FileName, StringComparison.OrdinalIgnoreCase));
            if (attachment is not null && attachment.Size <= MaxFileBytes)
            {
                var json = await Http.GetStringAsync(attachment.Url, ct);
                TryParse(json, out file);
            }

            copies.Add(new Copy(message, file));
        }

        return copies;
    }

    /// <summary>Parses and validates a configuration file. Anything wrong makes the whole file invalid.</summary>
    private static bool TryParse(string json, [NotNullWhen(true)] out SharedConfigFile? file)
    {
        file = null;

        SharedConfigFile? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<SharedConfigFile>(json, JsonOptions);
        }
        catch (Exception)
        {
            return false;
        }

        if (parsed is null || parsed.Version != FileVersion || parsed.Revision <= 0)
            return false;

        if (!SetupCode.TryValidateBranches(parsed.Branches, "shared configuration", out _))
            return false;

        (parsed.Heartbeat, parsed.Stale) = SetupCode.ClampTimers(parsed.Heartbeat, parsed.Stale);
        (parsed.CalendarChannelId, parsed.ApolloChannelIds) = SetupCode.ParseCalendar(parsed.CalendarChannel, parsed.ApolloChannels, parsed.Branches!);
        parsed.Weekly = SetupCode.ParseWeekly(parsed.WeeklyChannel, parsed.WeeklyApproval, parsed.WeeklyRole, parsed.Branches!);

        var by = (parsed.PublishedBy ?? string.Empty).Trim();
        parsed.PublishedBy = by.Length > 100 ? by[..100] : by;
        parsed.PublishedAtUtc = DateTime.SpecifyKind(parsed.PublishedAtUtc, DateTimeKind.Utc);

        file = parsed;
        return true;
    }

    /// <summary>The state channels of <paramref name="branches"/> the bot can see, each once.</summary>
    private static List<SocketTextChannel> StateChannels(DiscordSocketClient client, IEnumerable<FcBranch> branches) =>
        branches
            .Where(b => b.IsComplete)
            .Select(b => client.GetGuild(b.GuildId)?.GetTextChannel(b.StateChannelId))
            .OfType<SocketTextChannel>()
            .DistinctBy(c => c.Id)
            .ToList();

    /// <summary>
    /// The newest valid configuration across every state channel the bot can read, or null when
    /// there is none. An unreadable channel is skipped: another one may hold the same revision.
    /// </summary>
    public static async Task<SharedConfigFile?> FetchNewestAsync(
        DiscordSocketClient client, Configuration config, IPluginLog log, CancellationToken ct)
    {
        if (client.CurrentUser?.Id is not { } botId)
            return null;

        SharedConfigFile? newest = null;
        foreach (var channel in StateChannels(client, config.Branches.ToList()))
        {
            List<Copy> copies;
            try
            {
                copies = await ReadChannelAsync(channel, botId, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                log.Debug(ex, "Could not read the shared configuration in #{Channel}.", channel.Name);
                continue;
            }

            foreach (var file in copies.Select(c => c.File).OfType<SharedConfigFile>())
            {
                if (newest is null || file.Revision > newest.Revision)
                    newest = file;
            }
        }

        return newest;
    }

    /// <summary>
    /// Replaces the branches and timers with <paramref name="file"/> and saves. Framework thread
    /// only: the settings window reads the branch list while it draws.
    /// </summary>
    public static void Apply(SharedConfigFile file, Configuration config)
    {
        SetupCode.ApplyBranches(file.Branches, file.Heartbeat, file.Stale, config);

        // A plugin that predates the calendar never saw its settings, so their absence means
        // "unknown", not "removed": keep this plugin's own then.
        if (file.CarriesCalendar == true)
        {
            config.CalendarChannelId = file.CalendarChannelId;
            config.ApolloChannelIds = file.ApolloChannelIds;
        }
        else if (config.IsRelayOrStateChannel(config.CalendarChannelId) || config.IsRosterChannel(config.CalendarChannelId))
        {
            config.CalendarChannelId = 0;
        }

        // The weekly likewise: absent from an older plugin's file means "unknown", not "removed".
        if (file.CarriesWeekly == true)
            SetupCode.ApplyWeekly(file.Weekly, config);

        config.DropClashingWeekly();

        config.SharedRevision = file.Revision;
        config.SharedPublishedBy = file.PublishedBy;
        config.SharedPublishedAtUtc = file.PublishedAtUtc;
        config.Save();
    }

    // --- Publishing -----------------------------------------------------------------------------

    /// <summary>
    /// Writes the local branches and timers to every branch's state channel as the next revision.
    /// Nothing is written unless every branch resolves and no channel holds a newer revision.
    /// </summary>
    public static async Task<SharedConfigOutcome> PublishAsync(
        DiscordSocketClient client, Configuration config, string publishedBy, IPluginLog log, CancellationToken ct)
    {
        if (!await PublishGate.WaitAsync(0, ct))
            return new SharedConfigOutcome(false, "Already publishing. Try again when it finishes.");

        try
        {
            if (client.CurrentUser?.Id is not { } botId)
                return new SharedConfigOutcome(false, "Connect first.");

            // Snapshot: the settings window may replace the list while this runs.
            var branches = config.Branches.ToList();
            if (branches.Count == 0)
                return new SharedConfigOutcome(false, "There are no branches to publish.");

            if (branches.FirstOrDefault(b => !b.IsComplete) is { } incomplete)
                return new SharedConfigOutcome(false,
                    $"Branch {Label(incomplete)} is incomplete. Finish or remove it before publishing.");

            if ((Preflight(client, branches) ?? await CalendarPreflightAsync(client, config, ct) ?? await WeeklyPreflightAsync(client, config, ct)) is { } problem)
                return new SharedConfigOutcome(false, $"{problem} Nothing was published.");

            var channels = StateChannels(client, branches);

            var existing = new Dictionary<ulong, List<Copy>>();
            SharedConfigFile? remoteNewest = null;
            foreach (var channel in channels)
            {
                try
                {
                    existing[channel.Id] = await ReadChannelAsync(channel, botId, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    log.Warning(ex, "Could not read the shared configuration in #{Channel} before publishing.", channel.Name);
                    return new SharedConfigOutcome(false,
                        $"Could not read the state channel on {channel.Guild.Name}. Nothing was published.");
                }

                foreach (var file in existing[channel.Id].Select(c => c.File).OfType<SharedConfigFile>())
                {
                    if (remoteNewest is null || file.Revision > remoteNewest.Revision)
                        remoteNewest = file;
                }
            }

            if (remoteNewest is not null && remoteNewest.Revision > config.SharedRevision)
            {
                var by = remoteNewest.PublishedBy.Length > 0 ? $" by {remoteNewest.PublishedBy}" : string.Empty;
                return new SharedConfigOutcome(false,
                    $"Discord already has a newer configuration (revision {remoteNewest.Revision}{by}). "
                    + "Reconnect to take it over, make your changes on top of it and publish again.");
            }

            var (heartbeat, stale) = SetupCode.ClampTimers(config.HeartbeatSeconds, config.StaleSeconds);
            var published = new SharedConfigFile
            {
                Version = FileVersion,
                Revision = Math.Max(config.SharedRevision, remoteNewest?.Revision ?? 0) + 1,
                PublishedBy = publishedBy,
                PublishedAtUtc = DateTime.UtcNow,
                Heartbeat = heartbeat,
                Stale = stale,
                Branches = SetupCode.ToSetupBranches(branches),
                CarriesCalendar = true,
                CalendarChannel = SetupCode.FormatCalendar(config).Channel,
                ApolloChannels = SetupCode.FormatCalendar(config).Apollo,
                CarriesWeekly = true,
                WeeklyChannel = SetupCode.FormatWeekly(config).Channel,
                WeeklyApproval = SetupCode.FormatWeekly(config).Approval,
                WeeklyRole = SetupCode.FormatWeekly(config).Role,
            };

            // Checked exactly as a reader will check it: a file that other plugins would ignore
            // must not go out.
            var json = JsonSerializer.Serialize(published, JsonOptions);
            if (!TryParse(json, out var check))
                return new SharedConfigOutcome(false,
                    "The branch table is not valid (check the IDs, and that no branch uses its relay channel as its state channel). Nothing was published.");

            if (config.IsCalendarConfigured && check.CalendarChannelId == 0)
                return new SharedConfigOutcome(false,
                    "The calendar channel must not be an Apollo channel or any branch's relay, state or roster channel. Nothing was published.");

            if (config.IsWeeklyConfigured && check.Weekly == WeeklyIds.None)
                return new SharedConfigOutcome(false,
                    "The weekly's channels must not be any branch's relay, state or roster channel. Nothing was published.");

            var failed = new List<string>();
            foreach (var channel in channels)
            {
                try
                {
                    await WriteAsync(channel, existing[channel.Id], published, json, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    log.Warning(ex, "Publishing the shared configuration to #{Channel} in {Guild} failed.", channel.Name, channel.Guild.Name);
                    failed.Add(channel.Guild.Name);
                }
            }

            if (failed.Count == channels.Count)
                return new SharedConfigOutcome(false, "Could not publish the configuration; see /xllog for details.");

            config.SharedRevision = published.Revision;
            config.SharedPublishedBy = published.PublishedBy;
            config.SharedPublishedAtUtc = published.PublishedAtUtc;
            config.Save();

            log.Information("Published shared configuration revision {Revision} to {Count} state channel(s).",
                published.Revision, channels.Count - failed.Count);

            return failed.Count == 0
                ? new SharedConfigOutcome(true,
                    $"Published revision {published.Revision}. Every other plugin takes it over the next time it connects.")
                : new SharedConfigOutcome(false,
                    $"Published revision {published.Revision}, but not on {string.Join(", ", failed.Distinct())}. See /xllog, then publish again.");
        }
        finally
        {
            PublishGate.Release();
        }
    }

    /// <summary>
    /// Every server and channel the branches name must exist for the bot, and it must be able to
    /// write the configuration into each state channel. Null when all is well.
    /// </summary>
    private static string? Preflight(DiscordSocketClient client, List<FcBranch> branches)
    {
        foreach (var b in branches)
        {
            if (client.GetGuild(b.GuildId) is not { } guild)
                return $"The bot is not a member of the Discord server of {Label(b)}.";

            if (guild.GetTextChannel(b.ChannelId) is null)
                return $"The relay channel of {Label(b)} was not found, or the bot cannot see it.";

            if (guild.GetTextChannel(b.StateChannelId) is not { } state)
                return $"The state channel of {Label(b)} was not found, or the bot cannot see it.";

            if (b.RosterChannelId != 0 && guild.GetTextChannel(b.RosterChannelId) is null)
                return $"The roster channel of {Label(b)} was not found, or the bot cannot see it.";

            var perms = guild.CurrentUser.GetPermissions(state);
            if (!perms.ViewChannel || !perms.SendMessages || !perms.AttachFiles || !perms.ReadMessageHistory)
                return $"The bot needs View Channel, Send Messages, Attach Files and Read Message History in the state channel of {Label(b)}.";
        }

        return null;
    }

    /// <summary>
    /// The calendar channel and every Apollo channel must exist, on one server, with the
    /// permissions the calendar needs. Looked up over REST when not cached: an archived thread is
    /// not in the cache. Null when all is well.
    /// </summary>
    private static async Task<string?> CalendarPreflightAsync(DiscordSocketClient client, Configuration config, CancellationToken ct)
    {
        if (!config.IsCalendarConfigured)
            return null;

        var options = new RequestOptions { CancelToken = ct };
        if (await CalendarChannels.ResolveAsync(client, config.CalendarChannelId, options) is not { } calendar)
            return "The calendar channel was not found, or the bot cannot see it.";

        if (CalendarChannels.Permissions(client, calendar) is not { } perms
            || !perms.ViewChannel || !CalendarChannels.CanSend(calendar, perms) || !perms.EmbedLinks
            || !perms.AttachFiles || !perms.ReadMessageHistory)
            return "The bot needs View Channel, Send Messages, Embed Links, Attach Files and Read Message History in the calendar channel.";

        foreach (var id in config.ApolloChannelIds.ToList())
        {
            if (await CalendarChannels.ResolveAsync(client, id, options) is not { } apollo || apollo.GuildId != calendar.GuildId)
                return "An Apollo channel was not found on the calendar's server, or the bot cannot see it.";

            if (CalendarChannels.Permissions(client, apollo) is not { ViewChannel: true, ReadMessageHistory: true })
                return $"The bot needs View Channel and Read Message History in #{apollo.Name}.";
        }

        return null;
    }

    /// <summary>
    /// The weekly's channels and role must exist, on one server with the Apollo channels, with the
    /// permissions the weekly needs. Null when all is well or there is no weekly.
    /// </summary>
    private static async Task<string?> WeeklyPreflightAsync(DiscordSocketClient client, Configuration config, CancellationToken ct) =>
        config.IsWeeklyConfigured ? (await WeeklyChecks.ResolveAsync(client, config, guildId: null, ct)).Problem : null;

    /// <summary>Edits the channel's newest-revision copy (or posts one) and deletes any other copy.</summary>
    private static async Task WriteAsync(
        SocketTextChannel channel, List<Copy> copies, SharedConfigFile file, string json, CancellationToken ct)
    {
        var options = new RequestOptions { CancelToken = ct };
        var text = Describe(file);

        var keep = copies
            .OrderByDescending(c => c.File?.Revision ?? 0)
            .ThenByDescending(c => c.Message.Id)
            .FirstOrDefault();

        var attachment = new FileAttachment(new MemoryStream(Encoding.UTF8.GetBytes(json)), FileName);
        try
        {
            if (keep is null)
            {
                await channel.SendFileAsync(attachment, text, allowedMentions: AllowedMentions.None, options: options);
            }
            else
            {
                // An edit replaces the attachments wholesale.
                await keep.Message.ModifyAsync(p =>
                {
                    p.Content = text;
                    p.Attachments = new Optional<IEnumerable<FileAttachment>>([attachment]);
                    p.AllowedMentions = AllowedMentions.None;
                }, options);
            }
        }
        finally
        {
            attachment.Dispose();
        }

        foreach (var extra in copies.Where(c => !ReferenceEquals(c, keep)))
        {
            try
            {
                await extra.Message.DeleteAsync(options);
            }
            catch (HttpException ex) when (ex.HttpCode == System.Net.HttpStatusCode.NotFound)
            {
                // Already gone.
            }
        }
    }

    /// <summary>The message text: what it is, who published it, and one line per branch.</summary>
    private static string Describe(SharedConfigFile file)
    {
        var at = new DateTimeOffset(DateTime.SpecifyKind(file.PublishedAtUtc, DateTimeKind.Utc)).ToUnixTimeSeconds();
        var header =
            $"{Marker} — revision {file.Revision}, published by {MessageFormatter.EscapeMarkdown(file.PublishedBy)} <t:{at}:f>.\n"
            + "The Free Company branches every member's plugin follows (no bot token). Kept by the bot: do not edit or delete it.";

        var text = new StringBuilder(header);
        var branches = file.Branches ?? [];
        for (var i = 0; i < branches.Count; i++)
        {
            var b = branches[i];
            var line = $"\n• {MessageFormatter.EscapeMarkdown(b.Name)} — {MessageFormatter.EscapeMarkdown(b.FcName)} @ {MessageFormatter.EscapeMarkdown(b.World)}";
            if (text.Length + line.Length > 1900)
            {
                text.Append($"\n…and {branches.Count - i} more.");
                break;
            }

            text.Append(line);
        }

        return text.ToString();
    }

    private static string Label(FcBranch branch) => branch.Name.Trim().Length > 0 ? branch.Name.Trim() : branch.Describe();
}
