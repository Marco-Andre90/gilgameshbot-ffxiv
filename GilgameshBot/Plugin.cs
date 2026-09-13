using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using GilgameshBot.Chat;
using GilgameshBot.Relay;
using GilgameshBot.Setup;
using GilgameshBot.Windows;
using FFXIVClientStructs.FFXIV.Client.UI.Info;

namespace GilgameshBot;

/// <summary>
/// Plugin entry point. Wires the game chat listener to the Discord bridge and
/// follows the character's login state: connect on login, disconnect on logout.
/// </summary>
public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/gilgamesh";
    private const string CommandAlias = "/gilga";

    /// <summary>How long to keep waiting for the Free Company tag to appear after a login.</summary>
    private static readonly TimeSpan BranchResolveWindow = TimeSpan.FromSeconds(30);

    /// <summary>Gap between two attempts to read the character's world + Free Company.</summary>
    private static readonly TimeSpan BranchResolveInterval = TimeSpan.FromSeconds(1);

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    public Configuration Configuration { get; }
    public DiscordBridge Bridge { get; }
    public FreeCompanyChatListener ChatListener { get; }

    public readonly WindowSystem WindowSystem = new("GilgameshBot");
    private readonly ConfigWindow configWindow;

    /// <summary>
    /// Last complete world + FC read of the logged-in character, keyed by content id and cleared
    /// on logout. Framework thread only.
    /// </summary>
    private static (ulong ContentId, string World, string Tag, string Name, ulong FcId)? lastGoodKey;

    private readonly object resolveGate = new();
    private CancellationTokenSource? resolveCts;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        MigrateConfiguration(Configuration);

        Bridge = new DiscordBridge(Configuration, Log, GetCharacterLabelOnFrameworkThread);
        ChatListener = new FreeCompanyChatListener(ChatGui, PlayerState, Configuration, Bridge, Log);

        configWindow = new ConfigWindow(this);
        WindowSystem.AddWindow(configWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open GilgameshBot settings. Also: /gilgamesh connect | disconnect | status | import "
                          + "| send <discord name> | revoke. Short form: /gilga",
        });

        CommandManager.AddHandler(CommandAlias, new CommandInfo(OnCommand)
        {
            HelpMessage = "Short for /gilgamesh.",
        });

        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi += ToggleConfigUi;

        ClientState.Login += OnLogin;
        ClientState.Logout += OnLogout;

        // Plugin loaded (or reloaded) while already in the world.
        if (Configuration.AutoConnect && PlayerState.IsLoaded)
            BeginBranchResolution();

        Log.Information("GilgameshBot loaded.");
    }

    public void Dispose()
    {
        CancelBranchResolution();

        ClientState.Login -= OnLogin;
        ClientState.Logout -= OnLogout;

        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleConfigUi;

        CommandManager.RemoveHandler(CommandName);
        CommandManager.RemoveHandler(CommandAlias);
        WindowSystem.RemoveAllWindows();

        ChatListener.Dispose();
        Bridge.Dispose(); // announces "Offline" and closes the gateway connection
    }

    public void ToggleConfigUi() => configWindow.Toggle();

    /// <summary>
    /// Brings a config saved by an older version up to date. Version 3 shortened the presence
    /// timers to 10 s / 20 s, which shortens the takeover gap; older configs carry the previous
    /// values, so adopt the new ones once.
    /// </summary>
    private static void MigrateConfiguration(Configuration config)
    {
        if (config.Version >= 3)
            return;

        config.HeartbeatSeconds = 10;
        config.StaleSeconds = 20;
        config.Version = 3;
        config.Save();
    }

    // --- Branch resolution ------------------------------------------------------------------

    /// <summary>
    /// Reads the logged-in character's home world and Free Company name, and connects the branch
    /// they belong to. Started on login, on plugin load in the world, from /gilgamesh connect and
    /// from the settings window's Connect button.
    /// </summary>
    /// <remarks>
    /// The Free Company is not populated in the first frames after a login, so this retries on the
    /// framework thread once a second for <see cref="BranchResolveWindow"/>. It also keeps
    /// retrying while <see cref="DiscordBridge.Connect"/> is a no-op because a previous session
    /// is still tearing down — that is exactly what happens when an officer logs straight from a
    /// character in one branch into a character in another.
    /// </remarks>
    public void BeginBranchResolution()
    {
        CancellationToken ct;

        lock (resolveGate)
        {
            resolveCts?.Cancel();
            resolveCts?.Dispose();
            resolveCts = new CancellationTokenSource();
            ct = resolveCts.Token;
        }

        _ = Task.Run(() => ResolveAndConnectAsync(ct), ct);
    }

    private void CancelBranchResolution()
    {
        lock (resolveGate)
        {
            resolveCts?.Cancel();
            resolveCts?.Dispose();
            resolveCts = null;
        }
    }

    private async Task ResolveAndConnectAsync(CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + BranchResolveWindow;
        string? lastWorld = null;
        string? lastTag = null;
        string? lastName = null;
        var branchFound = false;

        // What the game gave us at any point during the window, for the diagnostic at the end.
        var sawWorld = false;
        var sawTag = false;
        var sawName = false;
        var requestedFcData = false;

        try
        {
            while (!ct.IsCancellationRequested && DateTime.UtcNow < deadline)
            {
                var (world, tag, name, fcId) = await Framework.RunOnFrameworkThread(ReadCharacterBranchKey);
                sawWorld |= world.Length > 0;
                sawTag |= tag.Length > 0;
                sawName |= name.Length > 0;

                // The tag is on the player object, the name comes from the Free Company info
                // proxy, which the game fills on login. A plugin loaded mid-session can find that
                // proxy empty: ask the game to fill it, once.
                if (tag.Length > 0 && name.Length == 0 && !requestedFcData)
                {
                    requestedFcData = true;
                    await Framework.RunOnFrameworkThread(RequestFreeCompanyData);
                }

                // The name is the key, but the tag is what proves the read is fresh: it hangs
                // off LocalPlayer, which does not exist until the new character has loaded, while
                // the info proxy is a UIModule singleton that survives a character switch and can
                // still hold the previous character's Free Company for a few frames.
                if (world.Length > 0 && name.Length > 0 && tag.Length > 0)
                {
                    lastWorld = world;
                    lastTag = tag;
                    lastName = name;

                    if (Configuration.FindBranch(world, tag, name) is { } branch)
                    {
                        if (!branchFound)
                            Log.Debug("Free Company {Name} (id {Id}) @ {World} matches branch {Branch}.",
                                name, fcId, world, branch.Describe());

                        branchFound = true;

                        switch (Bridge.State)
                        {
                            case BridgeState.Disconnected:
                                Bridge.ClearUnavailable();
                                Bridge.Connect(branch);

                                // Either connecting (done here) or refused with a reason already
                                // on the bridge (bad token, incomplete branch): retrying the same
                                // call would not change the answer.
                                return;

                            case BridgeState.Disconnecting:
                                // The previous session is still winding down and Connect would be
                                // a no-op. Keep ticking until it is gone. This is the ordinary
                                // log out → log straight back in flow, same branch or not.
                                break;

                            default:
                                // Live on this very branch already: nothing to do. A different
                                // branch means the officer switched characters — stand down and
                                // let a later tick connect the new one.
                                if (ReferenceEquals(Bridge.ActiveBranch, branch))
                                    return;

                                Bridge.Disconnect();
                                break;
                        }
                    }
                }

                await Task.Delay(BranchResolveInterval, ct);
            }
        }
        catch (OperationCanceledException)
        {
            return; // logged out, unloaded, or superseded by a newer resolution
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not work out which Free Company branch to relay.");
            return;
        }

        if (ct.IsCancellationRequested)
            return;

        if (branchFound)
        {
            // The window expired while a previous session was still tearing down. One last try,
            // so a very slow handoff does not leave the plugin idle with nothing to show for it.
            if (Bridge.State == BridgeState.Disconnected
                && Configuration.FindBranch(lastWorld!, lastTag!, lastName!) is { } late)
            {
                Bridge.ClearUnavailable();
                Bridge.Connect(late);
            }

            return; // the bridge owns the outcome from here
        }

        // The window expired without a match. Say why once, on the bridge, so the status line
        // and /gilgamesh status can both explain it.
        string reason;
        if (lastWorld is not null && lastName is not null)
        {
            var fc = lastTag is { Length: > 0 } ? $"{lastName} «{lastTag}»" : lastName;
            reason = $"No branch configured for {fc} @ {lastWorld}. "
                     + "Ask the officer who set the bot up to add it.";
        }
        else if (sawTag && !sawName)
        {
            reason = "The game has not loaded this character's Free Company details yet. "
                     + "Open the Free Company window once, or log out and back in, then /gilga connect.";
        }
        else if (!sawWorld)
        {
            reason = "No character is loaded, so there is nothing to relay.";
        }
        else
        {
            reason = "This character is not in a Free Company, so there is nothing to relay.";
        }

        Log.Debug("Branch resolution saw world={World} tag={Tag} name={Name} within {Seconds}s.",
            sawWorld, sawTag, sawName, BranchResolveWindow.TotalSeconds);

        Bridge.SetUnavailable(reason);
        Log.Warning("{Reason}", reason);
    }

    /// <summary>
    /// Home world, Free Company tag, Free Company name and Free Company ID of the logged-in
    /// character. Strings are trimmed and empty when unavailable, the ID is 0. Framework thread
    /// only: it reads game state.
    /// </summary>
    private static (string World, string Tag, string Name, ulong FcId) ReadCharacterBranchKey()
    {
        if (!PlayerState.IsLoaded)
            return (string.Empty, string.Empty, string.Empty, 0);

        var world = PlayerState.HomeWorld.ValueNullable?.Name.ExtractText() ?? string.Empty;

        // The FC tag lives on the player's game object, which may not exist yet (and carries no
        // tag at all for a character without a Free Company).
        var tag = ObjectTable.LocalPlayer?.CompanyTag.TextValue ?? string.Empty;

        var (name, fcId) = ReadFreeCompany();

        var key = (World: world.Trim(), Tag: tag.Trim(), Name: name.Trim(), FcId: fcId);
        var contentId = PlayerState.ContentId;

        if (key.World.Length > 0 && key.Tag.Length > 0 && key.Name.Length > 0)
        {
            if (contentId != 0)
                lastGoodKey = (contentId, key.World, key.Tag, key.Name, key.FcId);

            return key;
        }

        // The game can drop the Free Company proxy (or the tag) mid-session, long after login.
        // Membership does not change under a logged-in character without the game logging it
        // out, so the last complete read for this very character is still the answer.
        if (contentId != 0 && lastGoodKey is { } cached && cached.ContentId == contentId)
        {
            Log.Debug("Free Company read incomplete (world={World} tag={Tag} name={Name}); using this login's last read.",
                key.World.Length > 0, key.Tag.Length > 0, key.Name.Length > 0);
            return (cached.World, cached.Tag, cached.Name, cached.FcId);
        }

        return key;
    }

    /// <summary>
    /// Free Company name + ID from the game's own info proxy. The proxy is filled in from the
    /// zone-in packet, so like the tag it is empty for the first frames after a login — and for
    /// the whole session on a character with no Free Company. Framework thread only.
    /// </summary>
    /// <remarks>
    /// The proxy is a UIModule singleton and outlives a logout inside one game session, so this
    /// may return the <em>previous</em> character's Free Company right after a character switch.
    /// Callers only trust it on a tick where the FC tag (read off LocalPlayer, which the new
    /// character has to exist for) is also present.
    /// <para>
    /// Failures are swallowed and reported as "no name yet" on purpose: a native read that throws
    /// must feed the retry loop, not end branch resolution for this login.
    /// </para>
    /// </remarks>
    /// <summary>Asks the game to (re)load the Free Company info proxy. Framework thread only.</summary>
    private static unsafe void RequestFreeCompanyData()
    {
        try
        {
            var proxy = InfoProxyFreeCompany.Instance();
            if (proxy != null)
            {
                proxy->RequestData();
                Log.Debug("Requested Free Company data from the game.");
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not request Free Company data.");
        }
    }

    private static unsafe (string Name, ulong Id) ReadFreeCompany()
    {
        try
        {
            var proxy = InfoProxyFreeCompany.Instance();
            if (proxy == null || proxy->Id == 0)
                return (string.Empty, 0);

            return (proxy->NameString, proxy->Id);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not read the Free Company info proxy.");
            return (string.Empty, 0);
        }
    }

    /// <summary>
    /// True when a character with a Free Company is logged in. The tag is required alongside the
    /// name for the same freshness reason as in the resolve loop. Framework thread only.
    /// </summary>
    public static bool TryReadBranchKey(out string world, out string tag, out string fcName)
    {
        (world, tag, fcName, _) = ReadCharacterBranchKey();
        return world.Length > 0 && fcName.Length > 0 && tag.Length > 0;
    }

    // --- Events and commands ----------------------------------------------------------------

    private void OnLogin()
    {
        if (Configuration.AutoConnect)
            BeginBranchResolution();
    }

    private void OnLogout(int type, int code)
    {
        lastGoodKey = null;
        CancelBranchResolution();
        Bridge.Disconnect();
    }

    private void OnCommand(string command, string args)
    {
        // The verb is the first word; whatever follows is kept verbatim, because a Discord
        // display name may well contain spaces ("/gilga send Justice Archon").
        var trimmed = args.Trim();
        var split = trimmed.IndexOf(' ');
        var verb = (split < 0 ? trimmed : trimmed[..split]).ToLowerInvariant();
        var rest = split < 0 ? string.Empty : trimmed[(split + 1)..].Trim();

        switch (verb)
        {
            case "connect":
                BeginBranchResolution();
                ChatGui.Print("GilgameshBot: looking for your Free Company branch…", "GilgameshBot");
                break;

            case "disconnect":
                CancelBranchResolution();
                Bridge.Disconnect();
                ChatGui.Print("GilgameshBot: disconnecting from Discord.", "GilgameshBot");
                break;

            case "status":
                var branch = Bridge.ActiveBranch is { } active
                    ? $" Branch: {active.Describe()}."
                    : string.Empty;
                var role = Bridge.State != BridgeState.Connected
                    ? string.Empty
                    : Bridge.IsLeader
                        ? " Relaying (leader)."
                        : $" On standby (#{Bridge.QueuePosition} in queue, leader: {Bridge.LeaderLabel ?? "unknown"}).";
                ChatGui.Print($"GilgameshBot: {Bridge.State}, {Bridge.RelayedCount} message(s) relayed this session."
                              + branch
                              + role
                              + (Bridge.LastError is { } err ? $" {err}" : string.Empty), "GilgameshBot");
                break;

            case "import":
                // The clipboard is read through ImGui, which may only be touched from the
                // draw callback; the settings window picks this up on its next frame.
                configWindow.RequestClipboardImport();
                break;

            case "send":
                // All Discord I/O: hand it to a background task and return to the game at once.
                // The reply never contains the code, only whether it went out.
                if (rest.Length == 0)
                {
                    ChatGui.PrintError("GilgameshBot: say who to send it to — /gilga send <discord username>.",
                        "GilgameshBot");
                    break;
                }

                if (!Bridge.CanSendSetupCode)
                {
                    ChatGui.PrintError("GilgameshBot: Connect first.", "GilgameshBot");
                    break;
                }

                _ = Task.Run(async () => PrintFromBackground(await Bridge.SendSetupCodeAsync(rest)));
                break;

            case "revoke":
                if (!Bridge.CanSendSetupCode)
                {
                    ChatGui.PrintError("GilgameshBot: Connect first.", "GilgameshBot");
                    break;
                }

                _ = Task.Run(async () => PrintFromBackground(await Bridge.RevokeSetupCodesAsync()));
                break;

            default:
                configWindow.Toggle();
                break;
        }
    }

    /// <summary>
    /// Prints the outcome of a background Discord operation to game chat, on the framework thread.
    /// Callers pass only their own fixed text — never a setup code or anything decoded from one.
    /// </summary>
    private static void PrintFromBackground(SetupCodeOutcome outcome) =>
        Framework.RunOnFrameworkThread(() =>
        {
            if (outcome.Ok)
                ChatGui.Print($"GilgameshBot: {outcome.Message}", "GilgameshBot");
            else
                ChatGui.PrintError($"GilgameshBot: {outcome.Message}", "GilgameshBot");
        });


    /// <summary>
    /// "Character Name @ World", read on the framework thread because it touches game state.
    /// Used in the Online announcement so members know which officer is relaying.
    /// </summary>
    private Task<string> GetCharacterLabelOnFrameworkThread() =>
        Framework.RunOnFrameworkThread(() =>
        {
            if (!PlayerState.IsLoaded)
                return "an officer";

            var world = PlayerState.HomeWorld.ValueNullable?.Name.ExtractText() ?? "unknown world";
            return $"{PlayerState.CharacterName} @ {world}";
        });
}
