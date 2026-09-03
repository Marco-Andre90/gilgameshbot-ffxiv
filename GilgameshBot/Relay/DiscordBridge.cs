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

    public int RelayedCount => Volatile.Read(ref relayedCount);

    public int QueuedCount
    {
        get
        {
            var s = session;
            return s is not null && s.Queue.Reader.CanCount ? s.Queue.Reader.Count : 0;
        }
    }

    /// <summary>Starts the connection if not already running. Safe to call repeatedly.</summary>
    public void Connect()
    {
        Session s;

        lock (gate)
        {
            if (State != BridgeState.Disconnected)
                return;

            if (!config.IsDiscordConfigured)
            {
                LastError = "Discord is not configured (token, server ID and channel ID are required).";
                log.Warning("{Error}", LastError);
                return;
            }

            LastError = null;
            State = BridgeState.Connecting;

            s = new Session(new DiscordSocketClient(new DiscordSocketConfig
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

        var guild = s.Client.GetGuild(config.GuildId);
        var textChannel = guild?.GetTextChannel(config.ChannelId);

        if (guild is null || textChannel is null)
        {
            LastError = guild is null
                ? $"Bot is not a member of server {config.GuildId}. Invite it first."
                : $"Channel {config.ChannelId} not found in {guild.Name}, or the bot cannot see it.";
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

            State = BridgeState.Connected;
        }

        log.Information("Connected to Discord as {Bot}; relaying to #{Channel} in {Guild}.",
            s.Client.CurrentUser.Username, textChannel.Name, guild.Name);

        // Ready fires again after every reconnect; announce only once per session.
        if (config.AnnounceOnlineOffline && !s.AnnouncedOnline)
        {
            s.AnnouncedOnline = true; // claim first so a fast reconnect can't announce twice
            _ = Task.Run(() => AnnounceOnlineAsync(s, textChannel)); // don't block the gateway task
        }

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

        if (announceOffline && s.AnnouncedOnline && config.AnnounceOnlineOffline && s.TextChannel is { } ch)
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
        public Session(DiscordSocketClient client)
        {
            Client = client;
            Queue = Channel.CreateBounded<OutboundMessage>(new BoundedChannelOptions(QueueCapacity)
            {
                SingleReader = true,
                FullMode = BoundedChannelFullMode.DropOldest,
            });
        }

        public DiscordSocketClient Client { get; }
        public Channel<OutboundMessage> Queue { get; }
        public CancellationTokenSource Cts { get; } = new();
        public Task? ConnectTask { get; set; }
        public Task? Worker { get; set; }
        public SocketTextChannel? TextChannel { get; set; }
        public MentionResolver? Mentions { get; set; }
        public bool AnnouncedOnline { get; set; }
    }
}
