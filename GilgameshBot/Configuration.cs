using Dalamud.Configuration;

namespace GilgameshBot;

/// <summary>
/// One Free Company branch: a Free Company on a given home world, with the Discord server and
/// channels that mirror it. The plugin picks the branch from the logged-in character, so an
/// officer with characters in several branches needs no extra setup.
/// </summary>
/// <remarks>
/// The matching key is <see cref="World"/> + <see cref="FcTag"/>, compared trimmed and
/// case-insensitively. Each branch must have its <em>own</em> state channel: two branches
/// sharing one would make their leaders contend and silence one of the two Free Companies.
/// </remarks>
[Serializable]
public sealed class FcBranch
{
    /// <summary>Human label shown in the UI, e.g. "Kraken". Free text.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Home world of the characters in this branch, e.g. "Behemoth".</summary>
    public string World { get; set; } = string.Empty;

    /// <summary>Free Company tag without the « » brackets, e.g. "KRKN".</summary>
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
        && !string.IsNullOrWhiteSpace(FcTag)
        && GuildId != 0
        && ChannelId != 0
        && StateChannelId != 0;

    /// <summary>"Kraken («KRKN» @ Behemoth)", for the status line and log messages.</summary>
    public string Describe() => $"{Name} («{FcTag}» @ {World})";

    /// <summary>True when this branch is the one the given character belongs to.</summary>
    public bool Matches(string world, string fcTag) =>
        string.Equals(World.Trim(), world.Trim(), StringComparison.OrdinalIgnoreCase)
        && string.Equals(FcTag.Trim(), fcTag.Trim(), StringComparison.OrdinalIgnoreCase);
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
    public int Version { get; set; } = 2;

    // --- Discord ---

    /// <summary>Bot token from the Discord Developer Portal (Bot → Reset Token). One bot, all branches.</summary>
    public string BotToken { get; set; } = string.Empty;

    /// <summary>The Free Company branches this bot serves, one per (home world, FC tag).</summary>
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
    public int HeartbeatSeconds { get; set; } = 30;

    /// <summary>
    /// How long a presence message may go untouched before its instance counts as gone.
    /// Clamped to at least twice <see cref="HeartbeatSeconds"/> and at most 600 s. Also the
    /// leader's own lease: it stops relaying once its heartbeat is this old.
    /// </summary>
    public int StaleSeconds { get; set; } = 90;

    public bool IsDiscordConfigured =>
        !string.IsNullOrWhiteSpace(BotToken) && Branches.Any(b => b.IsComplete);

    /// <summary>Finds the branch the given character belongs to, or null if none is configured.</summary>
    public FcBranch? FindBranch(string world, string fcTag)
    {
        if (string.IsNullOrWhiteSpace(world) || string.IsNullOrWhiteSpace(fcTag))
            return null;

        return Branches.FirstOrDefault(b => b.IsComplete && b.Matches(world, fcTag));
    }

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
