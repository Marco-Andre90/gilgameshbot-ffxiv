using System.Threading.Channels;
using Dalamud.Plugin.Services;
using Discord;
using Discord.Net;
using Discord.WebSocket;
using GilgameshBot.Chat;

namespace GilgameshBot.Relay;

public enum BridgeState
{
    Disconnected,
    Connecting,
    Connected,
    Reconnecting,
    Disconnecting,
}

/// <summary>
/// Owns the Discord gateway connection and the outbound message queue.
/// </summary>
/// <remarks>
/// Lifecycle: <see cref="Connect"/> logs the bot in and starts a background worker that drains
/// the queue into the configured channel. <see cref="Disconnect"/> posts the Offline notice and
/// closes the connection. While the bot is connected its presence shows as online in Discord;
/// when the last plugin instance disconnects (cleanly or by crash) Discord marks it offline,
/// which is the reliable "chat is not being relayed" signal.
///
/// Thread model: <see cref="Enqueue"/> and <see cref="Disconnect"/> are called from the game
/// thread and never block. Only <see cref="Dispose"/> (plugin unload) waits, with a bound.
/// Everything else runs on Discord.Net / thread-pool threads. Each Connect creates a fresh
/// <see cref="Session"/>; late callbacks from an old session are ignored.
/// </remarks>
public sealed class DiscordBridge : IDisposable
{
    private static readonly TimeSpan DisposeTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Budget for the two REST calls of a clean handoff, inside <see cref="DisposeTimeout"/>.</summary>
    private static readonly TimeSpan ResignTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StaleMessageAge = TimeSpan.FromMinutes(5);
    private const int QueueCapacity = 200;

    private readonly Configuration config;
    private readonly IPluginLog log;
    private readonly Func<Task<string>> characterLabelProvider;
    private readonly object gate = new();

    private Session? session;
    private Task? teardownTask;
    private int relayedCount;

    public DiscordBridge(Configuration config, IPluginLog log, Func<Task<string>> characterLabelProvider)
    {
        this.config = config;
        this.log = log;
        this.characterLabelProvider = characterLabelProvider;
    }

    public BridgeState State { get; private set; } = BridgeState.Disconnected;

    public string? LastError { get; private set; }

    /// <summary>The branch this session is relaying, or null while disconnected.</summary>
    public FcBranch? ActiveBranch => session?.Branch;

    public int RelayedCount => Volatile.Read(ref relayedCount);

    /// <summary>True when this instance is the one relaying.</summary>
    public bool IsLeader => session?.Coordinator?.IsLeader ?? false;

    /// <summary>1-based place in the standby queue, 0 while unknown.</summary>
    public int QueuePosition => session?.Coordinator?.QueuePosition ?? 0;

    /// <summary>Character label of the instance currently relaying, if known.</summary>
    public string? LeaderLabel => session?.Coordinator?.LeaderLabel;

    public int QueuedCount
    {
        get
        {
            var s = session;
            return s is not null && s.Queue.Reader.CanCount ? s.Queue.Reader.Count : 0;
        }
    }

    /// <summary>
    /// Starts relaying <paramref name="branch"/> if no session is running. Safe to call repeatedly;
    /// does nothing (and leaves <see cref="LastError"/> alone) while a teardown is still in flight.
    /// </summary>
    public void Connect(FcBranch branch)
    {
        Session s;

        lock (gate)
        {
            if (State != BridgeState.Disconnected)
                return;

            if (string.IsNullOrWhiteSpace(config.BotToken))
            {
                LastError = "Discord is not configured: the bot token is missing.";
                log.Warning("{Error}", LastError);
                return;
            }

            if (!branch.IsComplete)
            {
                LastError = $"Branch {branch.Describe()} is missing its server, channel or state channel ID.";
                log.Warning("{Error}", LastError);
                return;
            }

            LastError = null;
            State = BridgeState.Connecting;

            s = new Session(branch, new DiscordSocketClient(new DiscordSocketConfig
            {
                // Guilds is enough to resolve the channel and roles; no privileged intents needed.
                GatewayIntents = GatewayIntents.Guilds,
                MessageCacheSize = 0,
                AlwaysDownloadUsers = false,
                LogLevel = LogSeverity.Info,
            }));

            session = s;
        }

        s.Client.Log += OnDiscordLog;
        s.Client.Ready += () => OnReadyAsync(s);
        s.Client.Connected += () => OnConnectedAsync(s);
        s.Client.Disconnected += ex => OnDisconnectedAsync(s, ex);

        s.Worker = Task.Run(() => WorkerLoopAsync(s));
        s.ConnectTask = Task.Run(async () =>
        {
            try
            {
                await s.Client.LoginAsync(TokenType.Bot, config.BotToken);
                await s.Client.StartAsync();
            }
            catch (Exception ex)
            {
                log.Error(ex, "Could not connect to Discord.");
                LastError = ex.Message;
                BeginTeardown(s, announceOffline: false);
            }
        });
    }

    /// <summary>
    /// Records why this instance is not relaying (no branch for the logged-in character, no
    /// character at all, …) so the status line and /gilgamesh status can explain it. Ignored
    /// while a session is running: a live connection's own errors matter more.
    /// </summary>
    public void SetUnavailable(string reason)
    {
        lock (gate)
        {
            if (State == BridgeState.Disconnected)
                LastError = reason;
        }
    }

    /// <summary>Clears a previously recorded reason once it no longer applies.</summary>
    public void ClearUnavailable()
    {
        lock (gate)
        {
            if (State == BridgeState.Disconnected)
                LastError = null;
        }
    }

    /// <summary>
    /// Posts the Offline notice (if Online was announced) and closes the connection.
    /// Returns immediately; the work happens in the background.
    /// </summary>
    public void Disconnect()
    {
        Session? s;
        lock (gate)
        {
            s = session;
        }

        if (s is not null)
            BeginTeardown(s, announceOffline: true);
    }

    /// <summary>Queues a message for delivery. Never blocks; drops the message if not connected.</summary>
    public void Enqueue(OutboundMessage message)
    {
        var s = session;
        if (s is null || State is BridgeState.Disconnected or BridgeState.Disconnecting)
        {
            log.Debug("Dropped FC message from {Sender}: bridge is {State}.", message.SenderName, State);
            return;
        }

        // Standby: another officer is relaying, so drop instead of buffering - replaying the
        // backlog after a handoff would duplicate what the previous leader already sent.
        if (s.Coordinator is { } coordinator && !coordinator.CanRelay)
        {
            log.Debug("Dropped FC message from {Sender}: this instance is on standby.", message.SenderName);
            return;
        }

        // Bounded + DropOldest: a long reconnect never builds an unbounded backlog.
        s.Queue.Writer.TryWrite(message);
    }

    /// <summary>Plugin unload: this is the one place we wait, so the Offline notice gets out.</summary>
    public void Dispose()
    {
        Disconnect();

        Task? pending;
        lock (gate)
        {
            pending = teardownTask;
        }

        if (pending is not null && !pending.Wait(DisposeTimeout))
            log.Warning("Discord disconnect timed out after {Seconds}s during unload.", DisposeTimeout.TotalSeconds);
    }

    // --- Discord.Net events -------------------------------------------------------------

    private Task OnDiscordLog(LogMessage message)
    {
        // Discord.Net never includes the token in log messages.
        switch (message.Severity)
        {
            case LogSeverity.Critical:
            case LogSeverity.Error:
                log.Error(message.Exception, "[Discord] {Message}", message.Message ?? string.Empty);
                break;
            case LogSeverity.Warning:
                log.Warning("[Discord] {Message}", message.Message ?? string.Empty);
                break;
            default:
                log.Debug("[Discord] {Message}", message.Message ?? string.Empty);
                break;
        }

        return Task.CompletedTask;
    }

    private Task OnReadyAsync(Session s)
    {
        if (!IsCurrent(s) || s.Cts.IsCancellationRequested)
            return Task.CompletedTask;

        var branch = s.Branch;
        var guild = s.Client.GetGuild(branch.GuildId);
        var textChannel = guild?.GetTextChannel(branch.ChannelId);
        var stateChannel = guild?.GetTextChannel(branch.StateChannelId);

        if (guild is null || textChannel is null || stateChannel is null)
        {
            LastError = guild is null
                ? $"Bot is not a member of server {branch.GuildId} (branch {branch.Describe()}). Invite it first."
                : textChannel is null
                    ? $"Channel {branch.ChannelId} not found in {guild.Name}, or the bot cannot see it."
                    : $"State channel {branch.StateChannelId} not found in {guild.Name}, or the bot cannot see it.";
            log.Error("{Error}", LastError);
            BeginTeardown(s, announceOffline: false);
            return Task.CompletedTask;
        }

        s.TextChannel = textChannel;
        s.Mentions = new MentionResolver(guild, log);
        LastError = null;

        lock (gate)
        {
            if (!IsCurrent(s) || State == BridgeState.Disconnecting)
                return Task.CompletedTask; // torn down while we were resolving the channel

            // Ready fires again after a failed resume, so only ever build one coordinator per
            // session: two would mean two presence messages and two heartbeat loops.
            if (s.Coordinator is null)
            {
                var coordinator = new PresenceCoordinator(config, log, s.Client, stateChannel, characterLabelProvider);
                coordinator.LeadershipChanged += (leader, reclaim) => OnLeadershipChanged(s, leader, reclaim);
                s.Coordinator = coordinator;
                s.Presence = Task.Run(() => coordinator.RunAsync(s.Cts.Token));
            }

            State = BridgeState.Connected;
        }

        log.Information("Connected to Discord as {Bot}; relaying branch {Branch} to #{Channel} in {Guild}.",
            s.Client.CurrentUser.Username, branch.Describe(), textChannel.Name, guild.Name);

        // Online is announced by OnLeadershipChanged once this instance is the one relaying.
        return Task.CompletedTask;
    }

    /// <summary>
    /// Fires whenever the gateway socket (re)connects. On a resumed reconnect Discord.Net
    /// replays the existing session and raises no public Resumed/Ready event, so this is the
    /// only signal that the link is back. Without it the bridge would sit in
    /// <see cref="BridgeState.Reconnecting"/> forever after Discord's periodic reconnect and
    /// the worker would stop relaying. We only act while <see cref="BridgeState.Reconnecting"/>
    /// (the first connect is driven by <see cref="OnReadyAsync"/>, which also resolves the
    /// channel); the cached channel stays valid because sends are REST calls keyed by id.
    /// </summary>
    private Task OnConnectedAsync(Session s)
    {
        if (!IsCurrent(s) || s.Cts.IsCancellationRequested)
            return Task.CompletedTask;

        lock (gate)
        {
            if (!IsCurrent(s) || State != BridgeState.Reconnecting || s.TextChannel is null)
                return Task.CompletedTask;

            State = BridgeState.Connected;
            LastError = null;
        }

        log.Information("Discord gateway reconnected; relaying restored.");
        return Task.CompletedTask;
    }

    private async Task AnnounceOnlineAsync(Session s, SocketTextChannel textChannel)
    {
        try
        {
            // The label is read on the game thread; don't wait forever if that thread is busy.
            var labelTask = characterLabelProvider();
            var who = await Task.WhenAny(labelTask, Task.Delay(TimeSpan.FromSeconds(2))) == labelTask
                ? labelTask.Result
                : "an officer";

            await textChannel.SendMessageAsync(
                $"🟢 **GilgameshBot Online!** Relaying Free Company chat via {MessageFormatter.EscapeMarkdown(who)}.",
                allowedMentions: AllowedMentions.None,
                options: new RequestOptions { CancelToken = s.Cts.Token });
        }
        catch (Exception ex)
        {
            s.AnnouncedOnline = false; // no Online went out, so no Offline should follow
            log.Warning(ex, "Could not post the Online announcement.");
        }
    }

    /// <summary>
    /// Announces Online when this instance becomes the relaying one - first login, clean handoff
    /// or takeover after a crash all arrive here. Followers never announce.
    /// </summary>
    private void OnLeadershipChanged(Session s, bool leader, bool reclaim)
    {
        if (!leader || !IsCurrent(s) || s.Cts.IsCancellationRequested)
            return;

        log.Information("This instance is now relaying Free Company chat.");

        if (!config.AnnounceOnlineOffline || s.TextChannel is not { } channel)
            return;

        // A forfeit + re-post with nobody else leading in between is not a change of relaying
        // officer as far as the channel is concerned: don't announce Online on every blip.
        if (reclaim && s.AnnouncedOnline)
            return;

        s.AnnouncedOnline = true; // claim first, so a retry cannot announce twice
        _ = Task.Run(() => AnnounceOnlineAsync(s, channel)); // never stall the heartbeat loop
    }

    private Task OnDisconnectedAsync(Session s, Exception exception)
    {
        if (!IsCurrent(s))
            return Task.CompletedTask;

        // Fatal close codes (bad token, disallowed intents…) will not recover by reconnecting.
        if (exception is WebSocketClosedException { CloseCode: 4004 or 4010 or 4011 or 4012 or 4013 or 4014 } closed)
        {
            LastError = $"Discord rejected the connection ({closed.CloseCode}): {closed.Reason}";
            log.Error("{Error}", LastError);
            BeginTeardown(s, announceOffline: false);
            return Task.CompletedTask;
        }

        lock (gate)
        {
            if (IsCurrent(s) && State == BridgeState.Connected)
            {
                State = BridgeState.Reconnecting;
                log.Warning(exception, "Discord gateway disconnected; Discord.Net will reconnect.");
            }
        }

        return Task.CompletedTask;
    }

    // --- Outbound worker ----------------------------------------------------------------

    private async Task WorkerLoopAsync(Session s)
    {
        var ct = s.Cts.Token;

        try
        {
            await foreach (var message in s.Queue.Reader.ReadAllAsync(ct))
            {
                // Wait for the channel to be resolved (first Ready) or for a reconnect to finish.
                while (s.TextChannel is null || State != BridgeState.Connected)
                    await Task.Delay(500, ct);

                // Leadership may have been lost between Enqueue and now. Drop, never buffer.
                if (s.Coordinator is { } coordinator && !coordinator.CanRelay)
                {
                    log.Debug("Skipped an FC message from {Sender}: this instance is on standby.", message.SenderName);
                    continue;
                }

                if (DateTimeOffset.Now - message.ReceivedAt > StaleMessageAge)
                {
                    log.Debug("Skipped a stale FC message from {Sender}.", message.SenderName);
                    continue;
                }

                try
                {
                    await SendAsync(s, message, ct);
                    Interlocked.Increment(ref relayedCount);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                    log.Warning(ex, "Failed to relay a message from {Sender}.", message.SenderName);
                }

                await Task.Delay(Math.Max(0, config.SendDelayMs), ct);
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
        catch (Exception ex)
        {
            log.Error(ex, "Outbound worker crashed.");
        }
    }

    private async Task SendAsync(Session s, OutboundMessage message, CancellationToken ct)
    {
        var resolved = await s.Mentions!.ResolveAsync(message.Text, config.ResolveMentions, ct);
        var content = MessageFormatter.Compose(message.SenderName, resolved.Text);

        await s.TextChannel!.SendMessageAsync(
            content,
            allowedMentions: resolved.ToAllowedMentions(), // only the IDs we resolved; never everyone/here
            options: new RequestOptions { CancelToken = ct });
    }

    // --- Teardown -----------------------------------------------------------------------

    private bool IsCurrent(Session s) => ReferenceEquals(session, s);

    /// <summary>Starts tearing down <paramref name="s"/> if it is still the active session.</summary>
    private void BeginTeardown(Session s, bool announceOffline)
    {
        lock (gate)
        {
            if (!IsCurrent(s) || State == BridgeState.Disconnecting)
                return;

            State = BridgeState.Disconnecting;
            teardownTask = Task.Run(() => TeardownAsync(s, announceOffline));
        }
    }

    private async Task TeardownAsync(Session s, bool announceOffline)
    {
        s.Cts.Cancel();

        // Let an in-flight StartAsync finish before stopping, otherwise Discord.Net may keep reconnecting.
        if (s.ConnectTask is { } connect)
        {
            try { await connect; } catch { /* logged in Connect */ }
        }

        if (s.Worker is { } worker)
        {
            try { await worker; } catch { /* worker logs its own errors */ }
        }

        if (s.Presence is { } presence)
        {
            try { await presence; } catch { /* the coordinator logs its own errors */ }
        }

        // Hand off: drop our presence message so a peer can take the head of the queue, and find
        // out whether anyone is left. Own short-lived token - the session's is already cancelled.
        var peerAlive = false;
        if (s.Coordinator is { } coordinator)
        {
            using var resignCts = new CancellationTokenSource(ResignTimeout);
            try
            {
                peerAlive = await coordinator.ResignAsync(resignCts.Token);
            }
            catch (Exception ex)
            {
                log.Debug(ex, "Handing off the presence message failed.");
            }
        }

        if (peerAlive)
            log.Information("Another officer is still relaying; skipping the Offline notice.");

        if (announceOffline && !peerAlive && s.AnnouncedOnline && config.AnnounceOnlineOffline && s.TextChannel is { } ch)
        {
            try
            {
                await ch.SendMessageAsync(
                    "🔴 **GilgameshBot Offline.** Free Company chat is not being relayed.",
                    allowedMentions: AllowedMentions.None);
            }
            catch (Exception ex)
            {
                log.Warning(ex, "Could not post the Offline announcement.");
            }
        }

        try { await s.Client.StopAsync(); } catch (Exception ex) { log.Debug(ex, "StopAsync failed."); }
        try { await s.Client.LogoutAsync(); } catch (Exception ex) { log.Debug(ex, "LogoutAsync failed."); }
        try { s.Client.Dispose(); } catch (Exception ex) { log.Debug(ex, "Client dispose failed."); }
        s.Cts.Dispose();

        lock (gate)
        {
            if (IsCurrent(s))
            {
                session = null;
                State = BridgeState.Disconnected;
            }
        }

        log.Information("Disconnected from Discord.");
    }

    /// <summary>Everything that belongs to one Connect → Disconnect cycle.</summary>
    private sealed class Session
    {
        public Session(FcBranch branch, DiscordSocketClient client)
        {
            Branch = branch;
            Client = client;
            Queue = Channel.CreateBounded<OutboundMessage>(new BoundedChannelOptions(QueueCapacity)
            {
                SingleReader = true,
                FullMode = BoundedChannelFullMode.DropOldest,
            });
        }

        public FcBranch Branch { get; }
        public DiscordSocketClient Client { get; }
        public Channel<OutboundMessage> Queue { get; }
        public CancellationTokenSource Cts { get; } = new();
        public Task? ConnectTask { get; set; }
        public Task? Worker { get; set; }
        public SocketTextChannel? TextChannel { get; set; }
        public MentionResolver? Mentions { get; set; }
        public PresenceCoordinator? Coordinator { get; set; }
        public Task? Presence { get; set; }
        public bool AnnouncedOnline { get; set; }
    }
}
