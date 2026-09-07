using Dalamud.Configuration;

namespace GilgameshBot;

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
    public int Version { get; set; } = 1;

    // --- Discord ---

    /// <summary>Bot token from the Discord Developer Portal (Bot → Reset Token).</summary>
    public string BotToken { get; set; } = string.Empty;

    /// <summary>ID of the Discord server (guild) the bot was invited to.</summary>
    public ulong GuildId { get; set; }

    /// <summary>ID of the text channel that receives Free Company chat.</summary>
    public ulong ChannelId { get; set; }

    /// <summary>
    /// Optional hidden/admin channel where every running instance keeps a presence message, so
    /// that exactly one of them relays and the others queue up. 0 disables the standby queue:
    /// this instance always relays (Phase 1 behaviour).
    /// </summary>
    public ulong StateChannelId { get; set; }

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
        !string.IsNullOrWhiteSpace(BotToken) && GuildId != 0 && ChannelId != 0;

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
