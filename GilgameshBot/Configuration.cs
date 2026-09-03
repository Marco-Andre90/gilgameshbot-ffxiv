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

    public bool IsDiscordConfigured =>
        !string.IsNullOrWhiteSpace(BotToken) && GuildId != 0 && ChannelId != 0;

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
