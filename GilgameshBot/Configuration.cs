using Dalamud.Configuration;

namespace GilgameshBot;

/// <summary>
/// One Free Company branch: a Free Company on a given home world, with the Discord server and
/// channels that mirror it. The plugin picks the branch from the logged-in character, so an
/// officer with characters in several branches needs no extra setup.
/// </summary>
/// <remarks>
/// The matching key is <see cref="World"/> + <see cref="FcName"/>, compared trimmed and
/// case-insensitively. Free Company <em>names</em> are unique on a world; tags are not, so
/// <see cref="FcTag"/> is only a human label and a secondary check. Each branch must have its
/// <em>own</em> state channel: two branches sharing one would make their leaders contend and
/// silence one of the two Free Companies.
/// </remarks>
[Serializable]
public sealed class FcBranch
{
    /// <summary>Human label shown in the UI, e.g. "Kraken". Free text.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Home world of the characters in this branch, e.g. "Behemoth".</summary>
    public string World { get; set; } = string.Empty;

    /// <summary>
    /// Full Free Company name, e.g. "Kraken Company". Together with <see cref="World"/> this is
    /// the matching key: FC names are unique per world, FC tags are not.
    /// </summary>
    public string FcName { get; set; } = string.Empty;

    /// <summary>
    /// Free Company tag without the « » brackets, e.g. "KRKN". A label, and a secondary check
    /// when both sides have one — never the key on its own.
    /// </summary>
    public string FcTag { get; set; } = string.Empty;

    /// <summary>ID of the Discord server (guild) this branch relays into.</summary>
    public ulong GuildId { get; set; }

    /// <summary>ID of the text channel that receives this branch's Free Company chat.</summary>
    public ulong ChannelId { get; set; }

    /// <summary>
    /// Hidden/admin channel where every instance relaying this branch keeps a presence message,
    /// so that exactly one of them relays and the others queue up. One per branch.
    /// </summary>
    public ulong StateChannelId { get; set; }

    /// <summary>True when every field needed to connect this branch is filled in.</summary>
    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(Name)
        && !string.IsNullOrWhiteSpace(World)
        && !string.IsNullOrWhiteSpace(FcName)
        && GuildId != 0
        && ChannelId != 0
        && StateChannelId != 0;

    /// <summary>
    /// "Kraken («KRKN» Kraken Company @ Behemoth)", for the status line and log messages.
    /// The tag is dropped when the branch has none.
    /// </summary>
    public string Describe() =>
        FcTag.Trim().Length > 0
            ? $"{Name} («{FcTag}» {FcName} @ {World})"
            : $"{Name} ({FcName} @ {World})";

    /// <summary>
    /// True when this branch is the one the given character belongs to: same home world and same
    /// Free Company name. The tag is only compared when both sides have one, so a branch saved
    /// without a tag still matches, and a mistyped tag on a matching name is caught.
    /// </summary>
    public bool Matches(string world, string fcTag, string fcName)
    {
        if (!string.Equals(World.Trim(), world.Trim(), StringComparison.OrdinalIgnoreCase)
            || !string.Equals(FcName.Trim(), fcName.Trim(), StringComparison.OrdinalIgnoreCase))
            return false;

        var ourTag = FcTag.Trim();
        var theirTag = fcTag.Trim();

        return ourTag.Length == 0
               || theirTag.Length == 0
               || string.Equals(ourTag, theirTag, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Plugin settings. Persisted by Dalamud as JSON in
/// %AppData%\XIVLauncher\pluginConfigs\GilgameshBot.json.
/// </summary>
/// <remarks>
/// The bot token is stored in plain text in that file. Treat the file as a secret:
/// do not share it, and regenerate the token in the Discord Developer Portal if it leaks.
/// </remarks>
[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    /// <summary>
    /// Bumped when loaded settings need adjusting on the way in; see the migration in
    /// <see cref="Plugin"/>. Version 3 introduced the 10 s / 20 s presence timers.
    /// </summary>
    public int Version { get; set; } = 3;

    // --- Discord ---

    /// <summary>Bot token from the Discord Developer Portal (Bot → Reset Token). One bot, all branches.</summary>
    public string BotToken { get; set; } = string.Empty;

    /// <summary>The Free Company branches this bot serves, one per (home world, FC name).</summary>
    public List<FcBranch> Branches { get; set; } = [];

    // --- Behaviour ---

    /// <summary>Connect to Discord automatically when a character logs in.</summary>
    public bool AutoConnect { get; set; } = true;

    /// <summary>Master switch for relaying. When off, the bot may stay connected but nothing is forwarded.</summary>
    public bool RelayEnabled { get; set; } = true;

    /// <summary>Also relay messages typed by the character running the plugin.</summary>
    public bool RelayOwnMessages { get; set; } = true;

    /// <summary>Turn "@name" in game chat into a real Discord mention.</summary>
    public bool ResolveMentions { get; set; } = true;

    /// <summary>Post "GilgameshBot Online!" / "GilgameshBot Offline" to the channel.</summary>
    public bool AnnounceOnlineOffline { get; set; } = true;

    /// <summary>Minimum delay between two Discord messages, to stay clear of rate limits.</summary>
    public int SendDelayMs { get; set; } = 300;

    /// <summary>
    /// How often the presence message is edited to prove this instance is alive. Clamped to
    /// 10–120 s where it is used, so a hand-edited config cannot break the lease.
    /// </summary>
    public int HeartbeatSeconds { get; set; } = 10;

    /// <summary>
    /// How long a presence message may go untouched before its instance counts as gone.
    /// Clamped to at least twice <see cref="HeartbeatSeconds"/> and at most 600 s. Also the
    /// leader's own lease: it stops relaying once its heartbeat is this old.
    /// </summary>
    public int StaleSeconds { get; set; } = 20;

    public bool IsDiscordConfigured =>
        !string.IsNullOrWhiteSpace(BotToken) && Branches.Any(b => b.IsComplete);

    /// <summary>Finds the branch the given character belongs to, or null if none is configured.</summary>
    public FcBranch? FindBranch(string world, string fcTag, string fcName)
    {
        if (string.IsNullOrWhiteSpace(world) || string.IsNullOrWhiteSpace(fcName))
            return null;

        return Branches.FirstOrDefault(b => b.IsComplete && b.Matches(world, fcTag, fcName));
    }

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
