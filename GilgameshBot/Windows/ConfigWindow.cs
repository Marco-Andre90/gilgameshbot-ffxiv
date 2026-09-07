using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using GilgameshBot.Relay;
using GilgameshBot.Setup;

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
    private string stateChannelIdBuffer;
    private bool showToken;
    private string? validationMessage;

    // Setup code. The pasted code is never echoed back to the screen or the log.
    private string setupCodeBuffer = string.Empty;
    private string? setupMessage;
    private bool setupMessageIsWarning;
    private bool pendingClipboardImport;

    public ConfigWindow(Plugin plugin) : base("GilgameshBot###GilgameshBotConfig")
    {
        this.plugin = plugin;
        config = plugin.Configuration;

        tokenBuffer = string.Empty;
        guildIdBuffer = string.Empty;
        channelIdBuffer = string.Empty;
        stateChannelIdBuffer = string.Empty;
        RefreshBuffersFromConfig();

        Size = new Vector2(480, 520);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(420, 360),
            MaximumSize = new Vector2(900, 900),
        };
    }

    public void Dispose() { }

    /// <summary>
    /// Queues an import of whatever setup code is in the clipboard, for /gilgamesh import.
    /// The command runs on the game thread; the clipboard is reached through ImGui, which may
    /// only be touched from the draw callback, so the work happens on the next frame instead.
    /// Opening the window is what guarantees that frame comes.
    /// </summary>
    public void RequestClipboardImport()
    {
        pendingClipboardImport = true;
        IsOpen = true;
    }

    public override void Draw()
    {
        if (pendingClipboardImport)
        {
            pendingClipboardImport = false;
            ImportFromClipboard();

            // Only the outcome is echoed to chat — never the code or anything decoded from it.
            if (setupMessage is { } importMsg)
            {
                if (setupMessageIsWarning)
                    Plugin.ChatGui.PrintError($"GilgameshBot: {importMsg}", "GilgameshBot");
                else
                    Plugin.ChatGui.Print($"GilgameshBot: {importMsg}", "GilgameshBot");
            }
        }

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

        if (bridge.State == BridgeState.Connected)
        {
            if (bridge.IsLeader)
            {
                ImGui.TextColored(Green, "Relaying (leader)");
            }
            else
            {
                var position = bridge.QueuePosition;
                var leader = bridge.LeaderLabel ?? "unknown";
                ImGui.TextColored(Yellow, position > 0
                    ? $"Standby - #{position} in queue, leader: {leader}"
                    : $"Standby - leader: {leader}");
            }
        }

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
        ImGui.InputText("State channel ID", ref stateChannelIdBuffer, 32);

        ImGui.TextColored(Grey, "Enable Developer Mode in Discord, then right-click the server / channel → Copy ID.");
        ImGui.TextColored(Grey, "State channel: hidden admin channel where each running plugin keeps a presence message.");
        ImGui.TextColored(Grey, "Every officer must use the same one.");

        if (ImGui.Button("Save Discord settings"))
            SaveDiscordSettings();

        if (validationMessage is { } msg)
        {
            ImGui.SameLine();
            ImGui.TextColored(Yellow, msg);
        }

        ImGui.Spacing();
        DrawSetupCode();
    }

    /// <summary>
    /// Export/import of the whole Discord configuration as one string. The code carries the bot
    /// token, so it only ever moves through the clipboard: it is never drawn, logged or printed.
    /// </summary>
    private void DrawSetupCode()
    {
        ImGui.TextUnformatted("Setup code");
        ImGui.TextColored(Grey, "Got a setup code from your FC? Import it here — no other Discord settings are needed.");

        var canExport = config.IsDiscordConfigured;
        if (!canExport)
            ImGui.BeginDisabled();

        if (ImGui.Button("Export setup code"))
        {
            ImGui.SetClipboardText(SetupCode.Encode(config));
            setupMessage = "Copied to clipboard. It contains the bot token — share it only by private message.";
            setupMessageIsWarning = true;
        }

        if (!canExport)
            ImGui.EndDisabled();

        ImGui.InputText("Paste setup code", ref setupCodeBuffer, 4096, ImGuiInputTextFlags.Password);

        if (ImGui.Button("Import"))
            TryImport(setupCodeBuffer);

        ImGui.SameLine();

        if (ImGui.Button("Import from clipboard"))
            ImportFromClipboard();

        if (setupMessage is { } setupMsg)
            ImGui.TextColored(setupMessageIsWarning ? Yellow : Green, setupMsg);
    }

    /// <summary>Imports the setup code in the clipboard. Draw thread only (touches ImGui).</summary>
    private void ImportFromClipboard()
    {
        string clipboard;
        try
        {
            clipboard = ImGui.GetClipboardText();
        }
        catch (Exception)
        {
            setupMessage = "Could not read the clipboard.";
            setupMessageIsWarning = true;
            return;
        }

        TryImport(clipboard);
    }

    private void TryImport(string? code)
    {
        if (SetupCode.TryDecode(code, out var payload, out var error))
        {
            SetupCode.Apply(payload, config);
            RefreshBuffersFromConfig();
            setupCodeBuffer = string.Empty;
            validationMessage = null;
            // Plug & play: a freshly configured plugin connects straight away. An already
            // running session keeps its old settings until the officer reconnects.
            if (plugin.Bridge.State == BridgeState.Disconnected)
            {
                plugin.Bridge.Connect();
                setupMessage = plugin.Bridge.State == BridgeState.Disconnected
                    ? $"Imported, but could not connect: {plugin.Bridge.LastError}"
                    : "Imported. Connecting to Discord…";
                setupMessageIsWarning = plugin.Bridge.State == BridgeState.Disconnected;
            }
            else
            {
                setupMessage = "Imported. Reconnect to apply.";
                setupMessageIsWarning = false;
            }
        }
        else
        {
            setupMessage = error;
            setupMessageIsWarning = true;
        }
    }

    /// <summary>Re-reads the edit buffers from the config, after an import.</summary>
    private void RefreshBuffersFromConfig()
    {
        tokenBuffer = config.BotToken;
        guildIdBuffer = config.GuildId == 0 ? string.Empty : config.GuildId.ToString();
        channelIdBuffer = config.ChannelId == 0 ? string.Empty : config.ChannelId.ToString();
        stateChannelIdBuffer = config.StateChannelId == 0 ? string.Empty : config.StateChannelId.ToString();
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

        if (!ulong.TryParse(stateChannelIdBuffer.Trim(), out var stateChannelId) || stateChannelId == 0)
        {
            validationMessage = "State channel ID must be a number.";
            return;
        }

        config.BotToken = tokenBuffer.Trim();
        config.GuildId = guildId;
        config.ChannelId = channelId;
        config.StateChannelId = stateChannelId;
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

        var heartbeat = config.HeartbeatSeconds;
        if (ImGui.InputInt("Heartbeat (s)", ref heartbeat, 5, 15))
        {
            config.HeartbeatSeconds = Math.Clamp(heartbeat, 10, 120);
            config.StaleSeconds = Math.Clamp(config.StaleSeconds, config.HeartbeatSeconds * 2, 600);
            config.Save();
        }

        var stale = config.StaleSeconds;
        if (ImGui.InputInt("Stale after (s)", ref stale, 10, 30))
        {
            config.StaleSeconds = Math.Clamp(stale, Math.Clamp(config.HeartbeatSeconds, 10, 120) * 2, 600);
            config.Save();
        }

        ImGui.TextColored(Grey, "Standby queue only: how often this instance proves it is alive,");
        ImGui.TextColored(Grey, "and how long a silent instance keeps its place in the queue.");
    }
}
