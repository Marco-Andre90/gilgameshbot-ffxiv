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

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    public Configuration Configuration { get; }
    public DiscordBridge Bridge { get; }
    public FreeCompanyChatListener ChatListener { get; }

    public readonly WindowSystem WindowSystem = new("GilgameshBot");
    private readonly ConfigWindow configWindow;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        Bridge = new DiscordBridge(Configuration, Log, GetCharacterLabelOnFrameworkThread);
        ChatListener = new FreeCompanyChatListener(ChatGui, PlayerState, Configuration, Bridge, Log);

        configWindow = new ConfigWindow(this);
        WindowSystem.AddWindow(configWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open GilgameshBot settings. Also: /gilgamesh connect | disconnect | status",
        });

        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi += ToggleConfigUi;

        ClientState.Login += OnLogin;
        ClientState.Logout += OnLogout;

        // Plugin loaded (or reloaded) while already in the world.
        if (Configuration.AutoConnect && PlayerState.IsLoaded)
            Bridge.Connect();

        Log.Information("GilgameshBot loaded.");
    }

    public void Dispose()
    {
        ClientState.Login -= OnLogin;
        ClientState.Logout -= OnLogout;

        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleConfigUi;

        CommandManager.RemoveHandler(CommandName);
        WindowSystem.RemoveAllWindows();

        ChatListener.Dispose();
        Bridge.Dispose(); // announces "Offline" and closes the gateway connection
    }

    public void ToggleConfigUi() => configWindow.Toggle();

    private void OnLogin()
    {
        if (Configuration.AutoConnect)
            Bridge.Connect();
    }

    private void OnLogout(int type, int code)
    {
        Bridge.Disconnect();
    }

    private void OnCommand(string command, string args)
    {
        switch (args.Trim().ToLowerInvariant())
        {
            case "connect":
                Bridge.Connect();
                ChatGui.Print(Bridge.State == BridgeState.Disconnected
                        ? $"GilgameshBot: could not connect. {Bridge.LastError}"
                        : "GilgameshBot: connecting to Discord…",
                    "GilgameshBot");
                break;

            case "disconnect":
                Bridge.Disconnect();
                ChatGui.Print("GilgameshBot: disconnecting from Discord.", "GilgameshBot");
                break;

            case "status":
                var role = Bridge.State != BridgeState.Connected
                    ? string.Empty
                    : Bridge.IsLeader
                        ? " Relaying (leader)."
                        : $" On standby (#{Bridge.QueuePosition} in queue, leader: {Bridge.LeaderLabel ?? "unknown"}).";
                ChatGui.Print($"GilgameshBot: {Bridge.State}, {Bridge.RelayedCount} message(s) relayed this session."
                              + role
                              + (Bridge.LastError is { } err ? $" Last error: {err}" : string.Empty), "GilgameshBot");
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
