using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using GilgameshBot.Relay;

namespace GilgameshBot.Windows;

/// <summary>Settings + status window, opened with /gilgamesh or from the plugin installer.</summary>
public sealed class ConfigWindow : Window, IDisposable
{
    private static readonly Vector4 Green = new(0.4f, 0.9f, 0.4f, 1f);
    private static readonly Vector4 Yellow = new(0.95f, 0.85f, 0.3f, 1f);
    private static readonly Vector4 Red = new(0.95f, 0.4f, 0.4f, 1f);
    private static readonly Vector4 Grey = new(0.6f, 0.6f, 0.6f, 1f);

    private readonly Plugin plugin;
    private readonly Configuration config;

    // ImGui needs mutable buffers; IDs are edited as text and parsed on save.
    private string tokenBuffer;
    private string guildIdBuffer;
    private string channelIdBuffer;
    private bool showToken;
    private string? validationMessage;

    public ConfigWindow(Plugin plugin) : base("GilgameshBot###GilgameshBotConfig")
    {
        this.plugin = plugin;
        config = plugin.Configuration;

        tokenBuffer = config.BotToken;
        guildIdBuffer = config.GuildId == 0 ? string.Empty : config.GuildId.ToString();
        channelIdBuffer = config.ChannelId == 0 ? string.Empty : config.ChannelId.ToString();

        Size = new Vector2(480, 420);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(420, 360),
            MaximumSize = new Vector2(900, 900),
        };
    }

    public void Dispose() { }

    public override void Draw()
    {
        DrawStatus();
        ImGui.Separator();
        DrawDiscordSettings();
        ImGui.Separator();
        DrawBehaviourSettings();
    }

    private void DrawStatus()
    {
        var bridge = plugin.Bridge;

        ImGui.TextUnformatted("Status:");
        ImGui.SameLine();

        var (color, label) = bridge.State switch
        {
            BridgeState.Connected => (Green, "Connected — relaying Free Company chat"),
            BridgeState.Connecting => (Yellow, "Connecting…"),
            BridgeState.Reconnecting => (Yellow, "Reconnecting…"),
            BridgeState.Disconnecting => (Yellow, "Disconnecting…"),
            _ => (Grey, "Disconnected"),
        };
        ImGui.TextColored(color, label);

        ImGui.TextUnformatted($"Relayed this session: {bridge.RelayedCount}   Queued: {bridge.QueuedCount}");

        if (bridge.LastError is { } error)
            ImGui.TextColored(Red, $"Last error: {error}");

        if (bridge.State == BridgeState.Disconnected)
        {
            if (ImGui.Button("Connect"))
                bridge.Connect();
        }
        else
        {
            if (ImGui.Button("Disconnect"))
                bridge.Disconnect();
        }
    }

    private void DrawDiscordSettings()
    {
        ImGui.TextUnformatted("Discord");

        var flags = showToken ? ImGuiInputTextFlags.None : ImGuiInputTextFlags.Password;
        ImGui.InputText("Bot token", ref tokenBuffer, 256, flags);
        ImGui.SameLine();
        ImGui.Checkbox("Show", ref showToken);

        ImGui.InputText("Server (guild) ID", ref guildIdBuffer, 32);
        ImGui.InputText("Channel ID", ref channelIdBuffer, 32);

        ImGui.TextColored(Grey, "Enable Developer Mode in Discord, then right-click the server / channel → Copy ID.");

        if (ImGui.Button("Save Discord settings"))
            SaveDiscordSettings();

        if (validationMessage is { } msg)
        {
            ImGui.SameLine();
            ImGui.TextColored(Yellow, msg);
        }
    }

    private void SaveDiscordSettings()
    {
        if (!ulong.TryParse(guildIdBuffer.Trim(), out var guildId) || guildId == 0)
        {
            validationMessage = "Server ID must be a number.";
            return;
        }

        if (!ulong.TryParse(channelIdBuffer.Trim(), out var channelId) || channelId == 0)
        {
            validationMessage = "Channel ID must be a number.";
            return;
        }

        if (string.IsNullOrWhiteSpace(tokenBuffer))
        {
            validationMessage = "Bot token is required.";
            return;
        }

        config.BotToken = tokenBuffer.Trim();
        config.GuildId = guildId;
        config.ChannelId = channelId;
        config.Save();
        validationMessage = "Saved. Reconnect to apply.";
    }

    private void DrawBehaviourSettings()
    {
        ImGui.TextUnformatted("Behaviour");

        var autoConnect = config.AutoConnect;
        if (ImGui.Checkbox("Connect automatically when a character logs in", ref autoConnect))
        {
            config.AutoConnect = autoConnect;
            config.Save();
        }

        var relayEnabled = config.RelayEnabled;
        if (ImGui.Checkbox("Relay Free Company chat", ref relayEnabled))
        {
            config.RelayEnabled = relayEnabled;
            config.Save();
        }

        var relayOwn = config.RelayOwnMessages;
        if (ImGui.Checkbox("Include my own messages", ref relayOwn))
        {
            config.RelayOwnMessages = relayOwn;
            config.Save();
        }

        var resolveMentions = config.ResolveMentions;
        if (ImGui.Checkbox("Turn @name into Discord mentions", ref resolveMentions))
        {
            config.ResolveMentions = resolveMentions;
            config.Save();
        }

        var announce = config.AnnounceOnlineOffline;
        if (ImGui.Checkbox("Post Online / Offline announcements", ref announce))
        {
            config.AnnounceOnlineOffline = announce;
            config.Save();
        }

        var delay = config.SendDelayMs;
        if (ImGui.InputInt("Delay between messages (ms)", ref delay, 50, 250))
        {
            config.SendDelayMs = Math.Clamp(delay, 0, 5000);
            config.Save();
        }
    }
}
