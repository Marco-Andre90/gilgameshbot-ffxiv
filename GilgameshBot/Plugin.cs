using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using GilgameshBot.Chat;
using GilgameshBot.Relay;
using GilgameshBot.Windows;

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

    /// <summary>Gap between two attempts to read the character's world + FC tag.</summary>
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

    private readonly object resolveGate = new();
    private CancellationTokenSource? resolveCts;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        Bridge = new DiscordBridge(Configuration, Log, GetCharacterLabelOnFrameworkThread);
        ChatListener = new FreeCompanyChatListener(ChatGui, PlayerState, Configuration, Bridge, Log);

        configWindow = new ConfigWindow(this);
        WindowSystem.AddWindow(configWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open GilgameshBot settings. Also: /gilgamesh connect | disconnect | status | import. Short form: /gilga",
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

    // --- Branch resolution ------------------------------------------------------------------

    /// <summary>
    /// Reads the logged-in character's home world and Free Company tag, and connects the branch
    /// they belong to. Started on login, on plugin load in the world, from /gilgamesh connect and
    /// from the settings window's Connect button.
    /// </summary>
    /// <remarks>
    /// The FC tag is not populated in the first frames after a login, so this retries on the
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
        var branchFound = false;

        try
        {
            while (!ct.IsCancellationRequested && DateTime.UtcNow < deadline)
            {
                var (world, tag) = await Framework.RunOnFrameworkThread(ReadCharacterBranchKey);

                if (world.Length > 0 && tag.Length > 0)
                {
                    lastWorld = world;
                    lastTag = tag;

                    if (Configuration.FindBranch(world, tag) is { } branch)
                    {
                        branchFound = true;

                        // Already relaying this branch: nothing to do. A different branch means
                        // the officer switched characters — stand down and let a later tick
                        // connect once the teardown has finished. Connect is also a no-op while
                        // a previous session is still tearing down, which is why this retries.
                        if (Bridge.State != BridgeState.Disconnected)
                        {
                            if (ReferenceEquals(Bridge.ActiveBranch, branch))
                                return;

                            Bridge.Disconnect();
                        }
                        else
                        {
                            Bridge.ClearUnavailable();
                            Bridge.Connect(branch);

                            // Either connecting (done here) or refused with a reason already on
                            // the bridge (bad token, incomplete branch) — retrying will not help.
                            return;
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

        if (ct.IsCancellationRequested || branchFound)
            return; // a matching branch was found; the bridge owns the outcome from here

        // The window expired without a match. Say why once, on the bridge, so the status line
        // and /gilgamesh status can both explain it.
        var reason = lastWorld is not null && lastTag is not null
            ? $"No branch configured for «{lastTag}» @ {lastWorld}. "
              + "Ask the officer who set the bot up to add it."
            : "This character is not in a Free Company, so there is nothing to relay.";

        Bridge.SetUnavailable(reason);
        Log.Warning("{Reason}", reason);
    }

    /// <summary>
    /// Home world name + Free Company tag of the logged-in character, both trimmed and empty
    /// when unavailable. Framework thread only: it reads game state.
    /// </summary>
    private static (string World, string Tag) ReadCharacterBranchKey()
    {
        if (!PlayerState.IsLoaded)
            return (string.Empty, string.Empty);

        var world = PlayerState.HomeWorld.ValueNullable?.Name.ExtractText() ?? string.Empty;

        // The FC tag lives on the player's game object, which may not exist yet (and carries no
        // tag at all for a character without a Free Company).
        var tag = ObjectTable.LocalPlayer?.CompanyTag.TextValue ?? string.Empty;

        return (world.Trim(), tag.Trim());
    }

    /// <summary>True when a character with a Free Company is logged in. Framework thread only.</summary>
    public static bool TryReadBranchKey(out string world, out string tag)
    {
        (world, tag) = ReadCharacterBranchKey();
        return world.Length > 0 && tag.Length > 0;
    }

    // --- Events and commands ----------------------------------------------------------------

    private void OnLogin()
    {
        if (Configuration.AutoConnect)
            BeginBranchResolution();
    }

    private void OnLogout(int type, int code)
    {
        CancelBranchResolution();
        Bridge.Disconnect();
    }

    private void OnCommand(string command, string args)
    {
        switch (args.Trim().ToLowerInvariant())
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

            default:
                configWindow.Toggle();
                break;
        }
    }


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
