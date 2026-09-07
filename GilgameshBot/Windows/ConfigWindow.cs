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
    private bool showToken;
    private string? validationMessage;

    // Branch table + editor. -1 means "the editor holds a new branch, not one from the list".
    private int selectedBranch = -1;
    private string branchNameBuffer = string.Empty;
    private string branchWorldBuffer = string.Empty;
    private string branchFcNameBuffer = string.Empty;
    private string branchTagBuffer = string.Empty;
    private string guildIdBuffer = string.Empty;
    private string channelIdBuffer = string.Empty;
    private string stateChannelIdBuffer = string.Empty;

    // "Use my character" reads game state on the framework thread; the result lands here on a
    // later frame. Draw itself never blocks.
    private Task<(bool Ok, string World, string Tag, string FcName)>? characterProbe;

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

        ConsumeCharacterProbe();

        // Consumed exactly once, whether or not the tab ends up being drawn this frame.
        var statusFlags = selectStatusTab ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
        selectStatusTab = false;

        // Dalamud's default theme draws buttons and fields without a frame border, which makes
        // them hard to tell from plain text; give every framed widget in this window a 1px edge.
        using var frameBorder = ImRaii.PushStyle(ImGuiStyleVar.FrameBorderSize, 1f * ImGuiHelpers.GlobalScale);

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
            // Which Free Company this session is relaying. Picked from the logged-in character,
            // never chosen by hand, so it is worth showing.
            if (bridge.ActiveBranch is { } branch)
                TextColoured(Grey, $"Branch: {branch.Describe()}");

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
                plugin.BeginBranchResolution();
        }
        else
        {
            if (ImGui.Button("Disconnect", ImGuiHelpers.ScaledVector2(120, 0)))
                plugin.Bridge.Disconnect();
        }
    }

    // --- Discord tab ------------------------------------------------------------------------

    private void DrawDiscordSettings()
    {
        ImGuiHelpers.ScaledDummy(4);

        TextWrappedColoured(Grey,
            "Only the officer who sets the bot up needs this tab. "
            + "Everyone else imports a setup code on the Status tab.");
        ImGuiHelpers.ScaledDummy(6);

        SectionHeader("Bot token");

        var flags = showToken ? ImGuiInputTextFlags.None : ImGuiInputTextFlags.Password;
        ImGui.InputText("Bot token", ref tokenBuffer, 256, flags);
        ImGui.SameLine();
        ImGui.Checkbox("Show", ref showToken);

        ImGuiHelpers.ScaledDummy(4);

        if (ImGui.Button("Save token", ImGuiHelpers.ScaledVector2(180, 0)))
        {
            if (string.IsNullOrWhiteSpace(tokenBuffer))
            {
                validationMessage = "Bot token is required.";
            }
            else
            {
                config.BotToken = tokenBuffer.Trim();
                config.Save();
                validationMessage = "Saved. Reconnect to apply.";
            }
        }

        SectionGap();
        DrawBranchTable();
        SectionGap();
        DrawBranchEditor();

        if (validationMessage is { } msg)
        {
            ImGuiHelpers.ScaledDummy(4);
            TextColoured(Yellow, msg);
        }
    }

    private void DrawBranchTable()
    {
        SectionHeader("Free Company branches");

        TextWrappedColoured(Grey,
            "One row per Free Company. The plugin picks the row that matches the logged-in "
            + "character's home world and Free Company name — nothing is ever chosen by hand. "
            + "(FC tags are not unique on a world, so the name is what identifies the FC.)");
        ImGuiHelpers.ScaledDummy(4);

        if (config.Branches.Count == 0)
        {
            TextColoured(Grey, "No branches yet. Fill in the editor below and click Add branch.");
            return;
        }

        // The Remove button fires in the middle of the loop; apply it once the table is closed,
        // so the list is never mutated while it is being drawn.
        var removeIndex = -1;

        using (var table = ImRaii.Table("##branches", 5,
                   ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp))
        {
            if (table)
            {
                ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch, 3f);
                ImGui.TableSetupColumn("World", ImGuiTableColumnFlags.WidthStretch, 3f);
                ImGui.TableSetupColumn("Free Company", ImGuiTableColumnFlags.WidthStretch, 4f);
                ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthStretch, 2f);
                ImGui.TableSetupColumn("##actions", ImGuiTableColumnFlags.WidthFixed,
                    ImGui.GetFrameHeight() + ImGui.GetStyle().CellPadding.X);
                ImGui.TableHeadersRow();

                for (var i = 0; i < config.Branches.Count; i++)
                {
                    var branch = config.Branches[i];
                    ImGui.TableNextRow();

                    ImGui.TableNextColumn();
                    if (ImGui.Selectable($"{(branch.Name.Length > 0 ? branch.Name : "(unnamed)")}##branch{i}",
                            selectedBranch == i,
                            // AllowItemOverlap so the Remove button in the last column stays
                            // clickable through the row-wide selectable.
                            ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowItemOverlap))
                        SelectBranch(i);

                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(branch.World);

                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(branch.FcTag.Trim().Length > 0
                        ? $"«{branch.FcTag}» {branch.FcName}"
                        : branch.FcName);

                    ImGui.TableNextColumn();
                    TextColoured(branch.IsComplete ? Green : Yellow, branch.IsComplete ? "complete" : "incomplete");

                    ImGui.TableNextColumn();
                    if (ImGuiComponents.IconButton($"##removeBranch{i}", FontAwesomeIcon.Trash))
                        removeIndex = i;

                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("Remove this branch");
                }
            }
        }

        if (removeIndex < 0)
            return;

        config.Branches.RemoveAt(removeIndex);
        config.Save();
        validationMessage = "Branch removed. Reconnect to apply.";

        // Keep the editor pointing at the row the officer thinks it points at.
        if (selectedBranch == removeIndex)
            ClearBranchEditor();
        else if (selectedBranch > removeIndex)
            selectedBranch--;
    }

    private void DrawBranchEditor()
    {
        var isNew = selectedBranch < 0 || selectedBranch >= config.Branches.Count;
        SectionHeader(isNew ? "New branch" : "Edit branch");

        ImGui.InputText("Name", ref branchNameBuffer, 64);
        ImGui.InputText("Home world", ref branchWorldBuffer, 64);
        ImGui.InputText("FC name", ref branchFcNameBuffer, 64);
        ImGui.InputText("FC tag", ref branchTagBuffer, 32);

        ImGuiHelpers.ScaledDummy(2);
        TextWrappedColoured(Grey,
            "The home world and the full FC name are what a character is matched on. The tag is a "
            + "label: two Free Companies on one world may share a tag, so it cannot be the key.");
        ImGuiHelpers.ScaledDummy(4);

        // Draw runs on the game thread, so IsLoaded / LocalPlayer may be read here directly;
        // the actual Free Company read still goes through the framework thread, on click.
        var canProbe = characterProbe is null
                       && Plugin.PlayerState.IsLoaded
                       && Plugin.ObjectTable.LocalPlayer is not null;

        using (ImRaii.Disabled(!canProbe))
        {
            if (ImGui.Button("Use my character", ImGuiHelpers.ScaledVector2(160, 0)))
                StartCharacterProbe();
        }

        ImGui.SameLine();
        ImGuiComponents.HelpMarker(
            "Fills in Home world, FC name and FC tag from the character you are logged in as. "
            + "Needs a character in a Free Company to be logged in.");

        ImGuiHelpers.ScaledDummy(4);

        ImGui.InputText("Server (guild) ID", ref guildIdBuffer, 32);
        ImGui.InputText("Channel ID", ref channelIdBuffer, 32);
        ImGui.InputText("State channel ID", ref stateChannelIdBuffer, 32);

        ImGuiHelpers.ScaledDummy(4);
        TextWrappedColoured(Grey,
            "Enable Developer Mode in Discord, then right-click the server / channel → Copy ID.");
        TextWrappedColoured(Grey,
            "State channel: hidden admin channel where each running plugin keeps a presence message. "
            + "Every officer relaying this branch must use the same one, and every branch needs its own.");
        ImGuiHelpers.ScaledDummy(4);

        if (ImGui.Button(isNew ? "Add branch" : "Save branch", ImGuiHelpers.ScaledVector2(180, 0)))
            SaveBranch(isNew);

        if (isNew)
            return;

        ImGui.SameLine();
        if (ImGui.Button("New branch", ImGuiHelpers.ScaledVector2(140, 0)))
            ClearBranchEditor();
    }

    /// <summary>
    /// Reads the world + Free Company name and tag off the logged-in character, on the framework
    /// thread.
    /// </summary>
    private void StartCharacterProbe()
    {
        characterProbe = Plugin.Framework.RunOnFrameworkThread(() =>
        {
            var ok = Plugin.TryReadBranchKey(out var world, out var tag, out var fcName);
            return (ok, world, tag, fcName);
        });
    }

    /// <summary>Picks up a finished "Use my character" probe. Called once per frame.</summary>
    private void ConsumeCharacterProbe()
    {
        if (characterProbe is not { IsCompleted: true } probe)
            return;

        characterProbe = null;

        if (!probe.IsCompletedSuccessfully)
        {
            validationMessage = "Could not read your character.";
            return;
        }

        var (ok, world, tag, fcName) = probe.Result;
        if (!ok)
        {
            validationMessage = "Log in on a character that is in a Free Company first.";
            return;
        }

        branchWorldBuffer = world;
        branchFcNameBuffer = fcName;
        branchTagBuffer = tag;

        if (branchNameBuffer.Trim().Length == 0)
            branchNameBuffer = tag.Length > 0 ? tag : fcName;

        validationMessage = null;
    }

    private void SaveBranch(bool isNew)
    {
        if (string.IsNullOrWhiteSpace(branchNameBuffer))
        {
            validationMessage = "Name is required.";
            return;
        }

        if (string.IsNullOrWhiteSpace(branchWorldBuffer))
        {
            validationMessage = "Home world is required.";
            return;
        }

        if (string.IsNullOrWhiteSpace(branchFcNameBuffer))
        {
            validationMessage = "FC name is required.";
            return;
        }

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

        if (!ulong.TryParse(stateChannelIdBuffer.Trim(), out var stateChannelId) || stateChannelId == 0)
        {
            validationMessage = "State channel ID must be a number.";
            return;
        }

        var branch = isNew ? new FcBranch() : config.Branches[selectedBranch];

        branch.Name = branchNameBuffer.Trim();
        branch.World = branchWorldBuffer.Trim();
        branch.FcName = branchFcNameBuffer.Trim();
        branch.FcTag = branchTagBuffer.Trim().Trim('«', '»').Trim();
        branch.GuildId = guildId;
        branch.ChannelId = channelId;
        branch.StateChannelId = stateChannelId;

        if (isNew)
        {
            config.Branches.Add(branch);
            selectedBranch = config.Branches.Count - 1;
        }

        config.Save();
        validationMessage = "Saved. Reconnect to apply.";
    }

    private void SelectBranch(int index)
    {
        selectedBranch = index;

        var branch = config.Branches[index];
        branchNameBuffer = branch.Name;
        branchWorldBuffer = branch.World;
        branchFcNameBuffer = branch.FcName;
        branchTagBuffer = branch.FcTag;
        guildIdBuffer = branch.GuildId == 0 ? string.Empty : branch.GuildId.ToString();
        channelIdBuffer = branch.ChannelId == 0 ? string.Empty : branch.ChannelId.ToString();
        stateChannelIdBuffer = branch.StateChannelId == 0 ? string.Empty : branch.StateChannelId.ToString();
    }

    private void ClearBranchEditor()
    {
        selectedBranch = -1;
        branchNameBuffer = string.Empty;
        branchWorldBuffer = string.Empty;
        branchFcNameBuffer = string.Empty;
        branchTagBuffer = string.Empty;
        guildIdBuffer = string.Empty;
        channelIdBuffer = string.Empty;
        stateChannelIdBuffer = string.Empty;
    }

    // --- Setup code -------------------------------------------------------------------------

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
            "Copies the bot token and every Free Company branch to the clipboard as one setup code, "
            + "for your fellow officers to import.\n\n"
            + "The code contains the bot token: send it by private message only.\n\n"
            + "Available once the Discord tab has a token and at least one complete branch.");

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

            // Plug & play: a freshly configured plugin looks for this character's branch straight
            // away. An already running session keeps its old settings until the officer reconnects.
            if (plugin.Bridge.State == BridgeState.Disconnected)
            {
                plugin.BeginBranchResolution();
                setupMessage = "Imported. Looking for your Free Company branch…";
                setupMessageIsWarning = false;
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
        ClearBranchEditor();
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
