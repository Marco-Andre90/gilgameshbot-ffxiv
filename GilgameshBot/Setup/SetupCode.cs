using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GilgameshBot.Setup;

/// <summary>One Free Company branch inside a setup code. IDs travel as strings.</summary>
public sealed class SetupBranch
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("world")] public string World { get; set; } = string.Empty;
    [JsonPropertyName("fcName")] public string FcName { get; set; } = string.Empty;
    [JsonPropertyName("tag")] public string Tag { get; set; } = string.Empty;
    [JsonPropertyName("guild")] public string Guild { get; set; } = string.Empty;
    [JsonPropertyName("channel")] public string Channel { get; set; } = string.Empty;
    [JsonPropertyName("state")] public string State { get; set; } = string.Empty;

    // Roster tracking. Optional: absent on codes from older plugins and on branches without it.
    [JsonPropertyName("lodestone")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Lodestone { get; set; }

    [JsonPropertyName("starterRank")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StarterRank { get; set; }

    [JsonPropertyName("roster")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Roster { get; set; }

    [JsonPropertyName("promotionDays")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? PromotionDays { get; set; }

    // Parsed IDs, filled in by TryDecode once validated.
    [JsonIgnore] public ulong GuildId { get; set; }
    [JsonIgnore] public ulong ChannelId { get; set; }
    [JsonIgnore] public ulong StateChannelId { get; set; }
    [JsonIgnore] public ulong LodestoneId { get; set; }
    [JsonIgnore] public ulong RosterChannelId { get; set; }
}

/// <summary>
/// Pointer to the Discord DM a setup code was delivered in, so the importing plugin can replace
/// that message with a receipt. Optional: a code exported to the clipboard carries none.
/// </summary>
public sealed class SetupReceipt
{
    [JsonPropertyName("channel")] public string Channel { get; set; } = string.Empty;
    [JsonPropertyName("message")] public string Message { get; set; } = string.Empty;

    // Parsed IDs, filled in by TryDecode once validated.
    [JsonIgnore] public ulong ChannelId { get; set; }
    [JsonIgnore] public ulong MessageId { get; set; }
}

/// <summary>
/// The shareable half of the configuration, carried between officers as one opaque string.
/// </summary>
/// <remarks>
/// The payload contains the bot token. It only ever travels through the clipboard:
/// never log it, never print it to game chat, never render it on screen.
/// </remarks>
public sealed class SetupPayload
{
    [JsonPropertyName("v")] public int Version { get; set; } = 2;
    [JsonPropertyName("token")] public string Token { get; set; } = string.Empty;
    [JsonPropertyName("heartbeat")] public int Heartbeat { get; set; } = 10;
    [JsonPropertyName("stale")] public int Stale { get; set; } = 20;
    [JsonPropertyName("branches")] public List<SetupBranch>? Branches { get; set; }

    /// <summary>
    /// Revision of the shared configuration the branches came from. Optional: absent on codes
    /// from older plugins and from plugins that never saw a published configuration.
    /// </summary>
    [JsonPropertyName("rev")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Revision { get; set; }

    /// <summary>
    /// Set only on codes delivered by DM. Never required: an older plugin simply ignores it, and
    /// a code without one behaves exactly as before.
    /// </summary>
    [JsonPropertyName("receipt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SetupReceipt? Receipt { get; set; }
}

/// <summary>
/// Encodes/decodes the "setup code": the one string an officer exports so the rest of the
/// Free Company can be configured by pasting it, without hunting for IDs.
/// </summary>
/// <remarks>
/// Format: <c>GB2:</c> + base64url(UTF-8 JSON). This is encoding, not encryption — the code is
/// as sensitive as the token inside it and must only be shared by private message.
/// </remarks>
public static class SetupCode
{
    /// <summary>Marker + format version. Case-sensitive; a future format bumps the digit.</summary>
    public const string Prefix = "GB2:";

    /// <summary>Current payload version. Bumped together with <see cref="Prefix"/>.</summary>
    private const int CurrentVersion = 2;

    /// <summary>Said for every malformed code: it must never quote any part of the input.</summary>
    private const string DamagedMessage = "The setup code is damaged or incomplete.";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Builds the setup code from the shareable settings. Never log the result.</summary>
    /// <param name="config">The settings to share.</param>
    /// <param name="receipt">
    /// Where the code is about to be delivered, when it travels as a Discord DM. The importing
    /// plugin uses it to replace that message with a receipt. Null for the clipboard export.
    /// </param>
    public static string Encode(Configuration config, SetupReceipt? receipt = null)
    {
        var (heartbeat, stale) = ClampTimers(config.HeartbeatSeconds, config.StaleSeconds);

        var payload = new SetupPayload
        {
            Version = CurrentVersion,
            Token = config.BotToken,
            Heartbeat = heartbeat,
            Stale = stale,
            Branches = ToSetupBranches(config.Branches),
            Revision = config.SharedRevision > 0 ? config.SharedRevision : null,
            Receipt = receipt,
        };

        var json = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        return Prefix + ToBase64Url(json);
    }

    /// <summary>The complete branches among <paramref name="branches"/>, as they travel in a setup code or the shared configuration.</summary>
    internal static List<SetupBranch> ToSetupBranches(IEnumerable<FcBranch> branches) =>
        branches
            .Where(b => b.IsComplete)
            .Select(b => new SetupBranch
            {
                Name = b.Name.Trim(),
                World = b.World.Trim(),
                FcName = b.FcName.Trim(),
                Tag = b.FcTag.Trim(),
                Guild = b.GuildId.ToString(),
                Channel = b.ChannelId.ToString(),
                State = b.StateChannelId.ToString(),
                Lodestone = b.LodestoneId != 0 ? b.LodestoneId.ToString() : null,
                StarterRank = b.StarterRank.Trim() is { Length: > 0 } rank ? rank : null,
                Roster = b.RosterChannelId != 0 ? b.RosterChannelId.ToString() : null,
                PromotionDays = Math.Clamp(b.PromotionDays, 1, 365),
            })
            .ToList();

    /// <summary>
    /// Same clamps as the settings window, applied in the same order: the heartbeat first,
    /// because the lower bound of the stale window is twice the <em>clamped</em> heartbeat.
    /// </summary>
    internal static (int Heartbeat, int Stale) ClampTimers(int heartbeat, int stale)
    {
        var h = Math.Clamp(heartbeat, 10, 120);
        return (h, Math.Clamp(stale, h * 2, 600));
    }

    /// <summary>
    /// Parses a setup code. On failure <paramref name="error"/> holds a message meant for the
    /// settings window; it never contains any part of the code.
    /// </summary>
    public static bool TryDecode(string? code, [NotNullWhen(true)] out SetupPayload? payload, out string error)
    {
        payload = null;
        error = string.Empty;

        var trimmed = (code ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            error = "Paste a setup code first.";
            return false;
        }

        if (!trimmed.StartsWith(Prefix, StringComparison.Ordinal))
        {
            error = "This is not a GilgameshBot setup code.";
            return false;
        }

        SetupPayload? parsed;
        try
        {
            var bytes = FromBase64Url(trimmed[Prefix.Length..]);
            parsed = JsonSerializer.Deserialize<SetupPayload>(bytes, JsonOptions);
        }
        catch (Exception)
        {
            // Deliberately swallowed: the exception message can quote the payload.
            error = DamagedMessage;
            return false;
        }

        if (parsed is null)
        {
            error = DamagedMessage;
            return false;
        }

        if (parsed.Version != CurrentVersion)
        {
            error = "This setup code was made by a different version of GilgameshBot. Update the plugin, "
                    + "or ask for a code exported by the version you have.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(parsed.Token))
        {
            error = "The setup code has no bot token in it.";
            return false;
        }

        if (!TryValidateBranches(parsed.Branches, "setup code", out error))
            return false;

        // The receipt pointer is a convenience, never a requirement: a malformed one is dropped
        // rather than failing the import, and an older code simply has none.
        if (parsed.Receipt is { } receipt)
        {
            if (ulong.TryParse(receipt.Channel, out var receiptChannelId) && receiptChannelId != 0
                && ulong.TryParse(receipt.Message, out var receiptMessageId) && receiptMessageId != 0)
            {
                receipt.ChannelId = receiptChannelId;
                receipt.MessageId = receiptMessageId;
            }
            else
            {
                parsed.Receipt = null;
            }
        }

        parsed.Token = parsed.Token.Trim();
        (parsed.Heartbeat, parsed.Stale) = ClampTimers(parsed.Heartbeat, parsed.Stale);

        if (parsed.Revision is <= 0)
            parsed.Revision = null;

        payload = parsed;
        return true;
    }

    /// <summary>
    /// Checks and normalises a branch list from a setup code or the shared configuration, and
    /// fills in the parsed IDs. <paramref name="source"/> names it in the error ("setup code");
    /// every error is fixed text, never any part of the input.
    /// </summary>
    internal static bool TryValidateBranches(List<SetupBranch>? branches, string source, out string error)
    {
        error = string.Empty;

        if (branches is not { Count: > 0 })
        {
            error = $"The {source} has no Free Company branches in it.";
            return false;
        }

        foreach (var branch in branches)
        {
            // Every message here is fixed text: branch names, worlds, Free Company names and
            // tags come from the input and must never be echoed back.
            if (string.IsNullOrWhiteSpace(branch.Name)
                || string.IsNullOrWhiteSpace(branch.World)
                || string.IsNullOrWhiteSpace(branch.FcName))
            {
                error = $"One of the Free Company branches in the {source} is incomplete.";
                return false;
            }

            if (!ulong.TryParse(branch.Guild, out var guildId) || guildId == 0
                || !ulong.TryParse(branch.Channel, out var channelId) || channelId == 0
                || !ulong.TryParse(branch.State, out var stateChannelId) || stateChannelId == 0)
            {
                error = $"One of the Free Company branches in the {source} has no valid Discord IDs.";
                return false;
            }

            if (stateChannelId == channelId)
            {
                error = $"One of the Free Company branches in the {source} uses its relay channel as the state channel; "
                        + "they must be different channels. Ask the officer who set the bot up to fix it.";
                return false;
            }

            branch.Name = branch.Name.Trim();
            branch.World = branch.World.Trim();
            branch.FcName = branch.FcName.Trim();
            branch.Tag = (branch.Tag ?? string.Empty).Trim();
            branch.GuildId = guildId;
            branch.ChannelId = channelId;
            branch.StateChannelId = stateChannelId;

            // Roster fields are a convenience like the receipt pointer: a malformed one is
            // dropped, never a failed import.
            branch.LodestoneId = ulong.TryParse(branch.Lodestone, out var lodestoneId) ? lodestoneId : 0;
            branch.RosterChannelId = ulong.TryParse(branch.Roster, out var rosterId) ? rosterId : 0;
            branch.StarterRank = branch.StarterRank?.Trim() is { Length: > 0 and <= 32 } rank ? rank : null;
            branch.PromotionDays = branch.PromotionDays is >= 1 and <= 365 ? branch.PromotionDays : null;
        }

        // A roster channel shared with any branch's relay or state channel would let the roster
        // scan edit or delete relay and presence messages: drop it, like any other bad roster field.
        var busyChannels = branches.SelectMany(b => new[] { b.ChannelId, b.StateChannelId }).ToHashSet();
        foreach (var branch in branches.Where(b => busyChannels.Contains(b.RosterChannelId)))
            branch.RosterChannelId = 0;

        return true;
    }

    /// <summary>
    /// Writes the shareable settings into the config and persists it. The branch list is
    /// replaced wholesale: the exporting officer's table is the source of truth.
    /// </summary>
    public static void Apply(SetupPayload payload, Configuration config)
    {
        config.BotToken = payload.Token;
        ApplyBranches(payload.Branches, payload.Heartbeat, payload.Stale, config);

        // The code's revision, or 0 when it carries none: the next sync then brings in whatever
        // the state channels hold, so a code older than the shared configuration heals itself.
        config.SharedRevision = payload.Revision ?? 0;
        config.SharedPublishedBy = string.Empty;
        config.SharedPublishedAtUtc = null;

        // Only a code that came with a pointer sets one. A clipboard import leaves an earlier
        // pending receipt alone, so a DM still gets scrubbed even if the officer pasted the code
        // by hand in between.
        if (payload.Receipt is { } receipt)
        {
            config.PendingReceipt = new ReceiptPointer
            {
                ChannelId = receipt.ChannelId,
                MessageId = receipt.MessageId,
            };
        }

        config.Save();
    }

    /// <summary>
    /// Replaces the branch table and the presence timers with validated ones (see
    /// <see cref="TryValidateBranches"/>). Does not save. The list is replaced, never mutated:
    /// other threads read it.
    /// </summary>
    internal static void ApplyBranches(List<SetupBranch>? branches, int heartbeat, int stale, Configuration config)
    {
        config.Branches = (branches ?? [])
            .Select(b => new FcBranch
            {
                Name = b.Name,
                World = b.World,
                FcName = b.FcName,
                FcTag = b.Tag,
                GuildId = b.GuildId,
                ChannelId = b.ChannelId,
                StateChannelId = b.StateChannelId,
                LodestoneId = b.LodestoneId,
                StarterRank = b.StarterRank ?? "Member",
                PromotionDays = b.PromotionDays ?? 30,
                RosterChannelId = b.RosterChannelId,
            })
            .ToList();
        config.HeartbeatSeconds = heartbeat;
        config.StaleSeconds = stale;
    }

    private static string ToBase64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static byte[] FromBase64Url(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch
        {
            2 => "==",
            3 => "=",
            0 => string.Empty,
            _ => throw new FormatException("Invalid base64url length."),
        };

        return Convert.FromBase64String(padded);
    }
}
