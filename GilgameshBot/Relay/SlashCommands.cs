using Dalamud.Plugin.Services;
using Discord;
using Discord.Net;
using Discord.WebSocket;
using GilgameshBot.Setup;

namespace GilgameshBot.Relay;

/// <summary>
/// The bot's own Discord slash commands: <c>/setupcode</c> and <c>/relaystatus</c>.
/// </summary>
/// <remarks>
/// <para>
/// The bot lives inside every officer's plugin, so every connected instance receives every
/// interaction. Only the <em>leader</em> of the branch whose server the command came from answers;
/// the rest return immediately. Two branches sharing one Discord server means two leaders may
/// both try, so a "already acknowledged" error is swallowed rather than logged as a failure.
/// </para>
/// <para>
/// Nothing here ever blocks the gateway task: the handler acknowledges with
/// <see cref="SocketInteraction.DeferAsync"/> and does the work on a background task.
/// </para>
/// <para>
/// Who may run <c>/setupcode</c> is decided by Discord alone: the command is registered as
/// Manage Server only, and the server owner opens it to roles under Server Settings →
/// Integrations. The code itself only ever travels in the DM: it is never logged, never
/// rendered, never put in the interaction reply.
/// </para>
/// </remarks>
public sealed class SlashCommands
{
    public const string SetupCodeCommand = "setupcode";
    public const string RelayStatusCommand = "relaystatus";

    private const string SetupCodeDescription = "Get your GilgameshBot setup code as a direct message.";
    private const string RelayStatusDescription = "Show who is relaying Free Company chat right now.";

    private readonly Configuration config;
    private readonly IPluginLog log;
    private readonly SocketGuild guild;
    private readonly FcBranch branch;
    private readonly PresenceCoordinator coordinator;
    private readonly Func<Task<string>> characterLabelProvider;

    public SlashCommands(
        Configuration config,
        IPluginLog log,
        SocketGuild guild,
        FcBranch branch,
        PresenceCoordinator coordinator,
        Func<Task<string>> characterLabelProvider)
    {
        this.config = config;
        this.log = log;
        this.guild = guild;
        this.branch = branch;
        this.coordinator = coordinator;
        this.characterLabelProvider = characterLabelProvider;
    }

    // --- Registration -------------------------------------------------------------------------

    /// <summary>
    /// Makes sure this branch's server has the two commands. Reads what is registered first and
    /// only overwrites when it differs: the server owner's per-role grants hang off the command
    /// ids, and re-registering on every officer's login would churn them for no reason.
    /// </summary>
    public async Task RegisterAsync(CancellationToken ct)
    {
        try
        {
            var existing = await guild.GetApplicationCommandsAsync(options: new RequestOptions { CancelToken = ct });

            if (Matches(existing))
            {
                log.Debug("Slash commands are already registered in {Guild}.", guild.Name);
                return;
            }

            await guild.BulkOverwriteApplicationCommandAsync(
                [Build(SetupCodeCommand, SetupCodeDescription), Build(RelayStatusCommand, RelayStatusDescription)],
                new RequestOptions { CancelToken = ct });

            log.Information("Registered the /{Setup} and /{Status} commands in {Guild}.",
                SetupCodeCommand, RelayStatusCommand, guild.Name);
        }
        catch (OperationCanceledException)
        {
            // Disconnecting; the next connect registers them.
        }
        catch (HttpException ex) when (ex.DiscordCode == DiscordErrorCode.MissingPermissions)
        {
            log.Warning("Could not register the slash commands in {Guild} (Missing Access). The bot was most "
                        + "likely invited without the applications.commands scope: open the OAuth2 URL again "
                        + "with both the bot and applications.commands scopes ticked.", guild.Name);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Could not register the slash commands in {Guild}. If this says Missing Access, "
                            + "re-invite the bot with the applications.commands scope.", guild.Name);
        }
    }

    private static ApplicationCommandProperties Build(string name, string description) =>
        new SlashCommandBuilder()
            .WithName(name)
            .WithDescription(description)
            // Only members with Manage Server see the commands until the server owner opens them
            // to specific roles in Server Settings → Integrations → GilgameshBot.
            .WithDefaultMemberPermissions(GuildPermission.ManageGuild)
            // Guild only. WithDMPermission(false) says the same thing but is deprecated in
            // Discord.Net 3.20 in favour of the context types.
            .WithContextTypes(InteractionContextType.Guild)
            .Build();

    /// <summary>True when the server already carries exactly the two commands as we want them.</summary>
    private static bool Matches(IReadOnlyCollection<SocketApplicationCommand> existing)
    {
        if (existing.Count != 2)
            return false;

        return IsOurs(existing, SetupCodeCommand, SetupCodeDescription)
               && IsOurs(existing, RelayStatusCommand, RelayStatusDescription);
    }

    /// <summary>
    /// Name and description only, on purpose. Those two are echoed back verbatim by Discord;
    /// the default permissions and the context type are set once when the command is created and
    /// are not read back reliably enough to compare. A false negative here would re-register on
    /// every officer's login and churn the server owner's per-role grants, which is far worse
    /// than a stale definition.
    /// </summary>
    private static bool IsOurs(
        IEnumerable<SocketApplicationCommand> existing, string name, string description) =>
        existing.Any(c => c.Type == ApplicationCommandType.Slash
                          && c.Name == name
                          && c.Description == description);

    // --- Handling -----------------------------------------------------------------------------

    /// <summary>
    /// Entry point for <see cref="BaseSocketClient.SlashCommandExecuted"/>. Returns without
    /// touching Discord unless this instance is the one that should answer.
    /// </summary>
    public async Task HandleAsync(SocketSlashCommand command, CancellationToken ct)
    {
        if (command.GuildId != branch.GuildId || !coordinator.IsLeader || ct.IsCancellationRequested)
            return;

        var name = command.Data.Name;
        if (name != SetupCodeCommand && name != RelayStatusCommand)
            return;

        try
        {
            // Acknowledge inside Discord's three-second window, then get off the gateway task.
            await command.DeferAsync(ephemeral: true, new RequestOptions { CancelToken = ct });
        }
        catch (HttpException ex) when (IsAlreadyAcknowledged(ex))
        {
            // Two branches on one server: the other branch's leader got there first.
            return;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Could not acknowledge /{Command}.", name);
            return;
        }

        _ = Task.Run(() => RunAsync(command, name, ct), CancellationToken.None);
    }

    private async Task RunAsync(SocketSlashCommand command, string name, CancellationToken ct)
    {
        try
        {
            var reply = name == SetupCodeCommand
                ? await RunSetupCodeAsync(command, ct)
                : DescribeRelayStatus();

            await command.FollowupAsync(
                reply,
                ephemeral: true,
                allowedMentions: AllowedMentions.None,
                options: new RequestOptions { CancelToken = ct });
        }
        catch (OperationCanceledException)
        {
            // Disconnecting mid-command; the interaction simply times out.
        }
        catch (HttpException ex) when (IsAlreadyAcknowledged(ex))
        {
            // Harmless duplicate from a second leader on the same server.
        }
        catch (Exception ex)
        {
            // Never quote the exception's content for /setupcode: the request body carried the code.
            log.Warning("Handling /{Command} failed ({Type}).", name, ex.GetType().Name);
        }
    }

    private static bool IsAlreadyAcknowledged(HttpException ex) =>
        ex.DiscordCode == DiscordErrorCode.InteractionHasAlreadyBeenAcknowledged;

    // --- /setupcode ---------------------------------------------------------------------------

    private async Task<string> RunSetupCodeAsync(SocketSlashCommand command, CancellationToken ct)
    {
        if (!config.IsDiscordConfigured)
            return "GilgameshBot is not configured yet, so there is no setup code to hand out.";

        if (command.User is not SocketGuildUser member)
            return "GilgameshBot could not read your server membership. Try again in a moment.";

        var who = await CharacterLabelAsync();
        var outcome = await SetupCodeDelivery.SendToUserAsync(member, config, who, SetupCodeDelivery.SlashLifetime, log, ct);

        return outcome.Ok
            ? "Check your DMs 📬 — the code is deleted after 5 minutes, or replaced with a receipt as soon as you import it."
            : outcome.Message;
    }

    private async Task<string> CharacterLabelAsync()
    {
        try
        {
            // Read on the game thread; don't wait forever if that thread is busy.
            var labelTask = characterLabelProvider();
            return await Task.WhenAny(labelTask, Task.Delay(TimeSpan.FromSeconds(2))) == labelTask
                ? labelTask.Result
                : "an officer";
        }
        catch (Exception ex)
        {
            log.Debug(ex, "Could not read the character label.");
            return "an officer";
        }
    }

    // --- /relaystatus -------------------------------------------------------------------------

    private string DescribeRelayStatus()
    {
        var leader = coordinator.LeaderLabel is { Length: > 0 } label
            ? MessageFormatter.EscapeMarkdown(label)
            : "nobody (yet)";

        var standby = coordinator.AlivePeers;
        var queue = standby switch
        {
            0 => "No other officer is on standby.",
            1 => "1 other officer is on standby.",
            _ => $"{standby} other officers are on standby.",
        };

        return $"🎮 **{MessageFormatter.EscapeMarkdown(branch.Name)}** — relaying via {leader}.\n{queue}";
    }
}
