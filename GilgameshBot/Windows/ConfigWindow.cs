using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
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

    // Set for one frame to force the Status tab (which owns the setup code block) to the front.
    private bool selectStatusTab;

    public ConfigWindow(Plugin plugin) : base("GilgameshBot###GilgameshBotConfig")
    {
        this.plugin = plugin;
        config = plugin.Configuration;

        tokenBuffer = string.Empty;
        guildIdBuffer = string.Empty;
        channelIdBuffer = string.Empty;
        stateChannelIdBuffer = string.Empty;
        RefreshBuffersFromConfig();

        // Dalamud multiplies Size and SizeConstraints by the global scale itself,
        // so these are deliberately unscaled numbers.
        Size = new Vector2(500, 600);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(440, 400),
            MaximumSize = new Vector2(1000, 1200),
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
        selectStatusTab = true;
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

        // Consumed exactly once, whether or not the tab ends up being drawn this frame.
        var statusFlags = selectStatusTab ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
        selectStatusTab = false;

        using var tabs = ImRaii.TabBar("##gilgameshTabs");
        if (!tabs)
            return;

        using (var statusTab = ImRaii.TabItem("Status", statusFlags))
        {
            if (statusTab)
                DrawStatusTab();
        }

        using (var discordTab = ImRaii.TabItem("Discord"))
        {
            if (discordTab)
                DrawDiscordSettings();
        }

        using (var advancedTab = ImRaii.TabItem("Advanced"))
        {
            if (advancedTab)
                DrawAdvancedSettings();
        }
    }

    /// <summary>
    /// Section title + rule + a little air underneath. ImGui.SeparatorText does not exist in
    /// Dalamud's ImGui bindings, so this is the stand-in used everywhere in this window.
    /// </summary>
    private static void SectionHeader(string label)
    {
        ImGui.TextUnformatted(label);
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(4);
    }

    /// <summary>Breathing room between two sections.</summary>
    private static void SectionGap() => ImGuiHelpers.ScaledDummy(10);

    private static void TextColoured(Vector4 colour, string text)
    {
        using (ImRaii.PushColor(ImGuiCol.Text, colour))
            ImGui.TextUnformatted(text);
    }

    /// <summary>Explanatory prose: wraps with the window instead of at a hard-coded break.</summary>
    private static void TextWrappedColoured(Vector4 colour, string text)
    {
        using (ImRaii.PushColor(ImGuiCol.Text, colour))
            ImGui.TextWrapped(text);
    }

    private void DrawStatusTab()
    {
        DrawConnection();
        SectionGap();
        DrawSetupCode();
        SectionGap();
        DrawBehaviourSettings();
    }

    private void DrawConnection()
    {
        var bridge = plugin.Bridge;

        SectionHeader("Connection");

        var (colour, label) = bridge.State switch
        {
            BridgeState.Connected => (Green, "Connected"),
            BridgeState.Connecting => (Yellow, "Connecting…"),
            BridgeState.Reconnecting => (Yellow, "Reconnecting…"),
            BridgeState.Disconnecting => (Yellow, "Disconnecting…"),
            _ => (Grey, "Disconnected"),
        };

        using (ImRaii.PushColor(ImGuiCol.Text, colour))
        {
            // IUiBuilder calls the icon font "FontIcon"; UiBuilder's own alias is IconFont.
            using (ImRaii.PushFont(Plugin.PluginInterface.UiBuilder.FontIcon))
                ImGui.TextUnformatted(FontAwesomeIcon.Circle.ToIconString());

            ImGui.SameLine();
            ImGui.TextUnformatted(label);
        }

        using (ImRaii.PushIndent(1))
        {
            if (bridge.State == BridgeState.Connected)
            {
                string role;
                if (bridge.IsLeader)
                {
                    role = "Relaying Free Company chat (leader)";
                }
                else
                {
                    var position = bridge.QueuePosition;
                    var leader = bridge.LeaderLabel ?? "unknown";
                    role = position > 0
                        ? $"Standby — #{position} in queue, leader: {leader}"
                        : $"Standby — leader: {leader}";
                }

                TextColoured(Grey, role);
            }

            TextColoured(Grey, $"Relayed this session: {bridge.RelayedCount} · Queued: {bridge.QueuedCount}");

            if (bridge.LastError is { } error)
                TextWrappedColoured(Red, $"Last error: {error}");
        }

        ImGuiHelpers.ScaledDummy(4);

        if (bridge.State == BridgeState.Disconnected)
        {
            if (ImGui.Button("Connect", ImGuiHelpers.ScaledVector2(120, 0)))
                bridge.Connect();
        }
        else
        {
            if (ImGui.Button("Disconnect", ImGuiHelpers.ScaledVector2(120, 0)))
                bridge.Disconnect();
        }
    }

    private void DrawDiscordSettings()
    {
        ImGuiHelpers.ScaledDummy(4);
        SectionHeader("Discord");

        TextWrappedColoured(Grey,
            "Only the officer who sets the bot up needs this tab. "
            + "Everyone else imports a setup code on the Status tab.");
        ImGuiHelpers.ScaledDummy(6);

        var flags = showToken ? ImGuiInputTextFlags.None : ImGuiInputTextFlags.Password;
        ImGui.InputText("Bot token", ref tokenBuffer, 256, flags);
        ImGui.SameLine();
        ImGui.Checkbox("Show", ref showToken);

        ImGui.InputText("Server (guild) ID", ref guildIdBuffer, 32);
        ImGui.InputText("Channel ID", ref channelIdBuffer, 32);
        ImGui.InputText("State channel ID", ref stateChannelIdBuffer, 32);

        ImGuiHelpers.ScaledDummy(4);
        TextWrappedColoured(Grey,
            "Enable Developer Mode in Discord, then right-click the server / channel → Copy ID.");
        TextWrappedColoured(Grey,
            "State channel: hidden admin channel where each running plugin keeps a presence message. "
            + "Every officer must use the same one.");
        ImGuiHelpers.ScaledDummy(4);

        if (ImGui.Button("Save Discord settings", ImGuiHelpers.ScaledVector2(180, 0)))
            SaveDiscordSettings();

        if (validationMessage is { } msg)
        {
            ImGui.SameLine();
            TextColoured(Yellow, msg);
        }
    }

    /// <summary>
    /// Export/import of the whole Discord configuration as one string. The code carries the bot
    /// token, so it only ever moves through the clipboard: it is never drawn, logged or printed.
    /// </summary>
    private void DrawSetupCode()
    {
        SectionHeader("Setup code");

        TextWrappedColoured(Grey,
            "Got a setup code from your FC? Import it here — no other Discord settings are needed.");
        ImGuiHelpers.ScaledDummy(4);

        if (ImGui.Button("Import from clipboard", ImGuiHelpers.ScaledVector2(180, 0)))
            ImportFromClipboard();

        ImGui.SameLine();

        using (ImRaii.Disabled(!config.IsDiscordConfigured))
        {
            if (ImGui.Button("Export setup code", ImGuiHelpers.ScaledVector2(160, 0)))
            {
                ImGui.SetClipboardText(SetupCode.Encode(config));
                setupMessage = "Copied to clipboard. It contains the bot token — share it only by private message.";
                setupMessageIsWarning = true;
            }
        }

        // Outside the disabled scope, so the explanation still works while the button is greyed out.
        ImGui.SameLine();
        ImGuiComponents.HelpMarker(
            "Copies this plugin's whole Discord configuration to the clipboard as one setup code, "
            + "for your fellow officers to import.\n\n"
            + "The code contains the bot token: send it by private message only.\n\n"
            + "Available once the Discord tab has been filled in and saved.");

        ImGuiHelpers.ScaledDummy(4);

        var importWidth = 90 * ImGuiHelpers.GlobalScale;
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - importWidth - ImGui.GetStyle().ItemSpacing.X);
        ImGui.InputTextWithHint("##setupCode", "…or paste a setup code here", ref setupCodeBuffer, 4096,
            ImGuiInputTextFlags.Password);

        ImGui.SameLine();

        if (ImGui.Button("Import", new Vector2(importWidth, 0)))
            TryImport(setupCodeBuffer);

        if (setupMessage is { } setupMsg)
            TextWrappedColoured(setupMessageIsWarning ? Yellow : Green, setupMsg);
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
        SectionHeader("Behaviour");

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
    }

    private void DrawAdvancedSettings()
    {
        ImGuiHelpers.ScaledDummy(4);
        TextWrappedColoured(Grey,
            "Defaults are fine for almost everyone. Every officer should use the same heartbeat "
            + "and stale values (the setup code carries them).");
        ImGuiHelpers.ScaledDummy(6);

        SectionHeader("Timers");

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

        ImGuiHelpers.ScaledDummy(4);
        TextWrappedColoured(Grey,
            "Standby queue only: how often this instance proves it is alive, "
            + "and how long a silent instance keeps its place in the queue.");
    }
}
