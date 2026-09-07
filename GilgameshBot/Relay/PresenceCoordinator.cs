using Discord;
using Discord.WebSocket;
using Dalamud.Plugin.Services;

namespace GilgameshBot.Relay;

/// <summary>
/// Decides which of the running plugin instances relays Free Company chat, using Discord itself
/// as the coordination medium. No server, no clock sync between officers' PCs.
/// </summary>
/// <remarks>
/// <para>
/// Every instance posts <em>its own</em> presence message in the configured state channel —
/// <c>🎮 &lt;Character @ World&gt; · beat &lt;n&gt;</c> — and keeps it fresh by editing it every
/// <see cref="Configuration.HeartbeatSeconds"/>. Message ids are Discord snowflakes, assigned by
/// the server and monotonic, so the <em>oldest</em> presence message is the head of the queue:
/// ordering never depends on a local clock.
/// </para>
/// <para>
/// A presence message is <em>alive</em> when its last edit is younger than
/// <see cref="Configuration.StaleSeconds"/> compared to <c>serverNow</c> (the newest edit
/// timestamp in the channel, i.e. server time). The leader is the oldest alive message.
/// Leadership is therefore a pure function of what the channel contains — there is no claim
/// protocol to race on.
/// </para>
/// <para>
/// <b>Lease.</b> <see cref="CanRelay"/> additionally requires that <em>our own</em> last heartbeat
/// succeeded within <see cref="Configuration.StaleSeconds"/> (measured with
/// <see cref="Environment.TickCount64"/>, monotonic and local). A leader that can no longer reach
/// Discord stands down before any peer can consider it stale, so a takeover cannot duplicate
/// messages. The cost is a relay gap of up to ~StaleSeconds after a crash; that is accepted.
/// </para>
/// <para>
/// <b>Ownership.</b> An instance only ever edits or deletes its own presence message. The single
/// exception is deleting messages older than 10 minutes, which are leftovers from crashed
/// instances; anyone may clean those up.
/// </para>
/// <para>Thread model: <see cref="RunAsync"/> owns all mutation; the public state is read from the
/// game thread (UI, Enqueue) and is published through volatile / interlocked fields.</para>
/// </remarks>
public sealed class PresenceCoordinator
{
    /// <summary>Marks a message as a presence message. Combined with "authored by this bot".</summary>
    private const string Prefix = "🎮 ";

    private const string BeatSeparator = " · beat ";

    /// <summary>Presence messages older than this are debris from crashed instances.</summary>
    private static readonly TimeSpan CleanupAge = TimeSpan.FromMinutes(10);

    private readonly Configuration config;
    private readonly IPluginLog log;
    private readonly DiscordSocketClient client;
    private readonly SocketTextChannel stateChannel;
    private readonly Func<Task<string>> characterLabelProvider;
    private readonly Action<string?> reportProblem;
    private bool historyProblemReported;

    private IUserMessage? own;
    private string label = "an officer";
    private int beat;
    private long lastHeartbeatOkTicks;
    private DateTimeOffset lastServerNow;

    /// <summary>Every presence message id this instance has ever posted (forfeits re-post under a new id).</summary>
    private readonly HashSet<ulong> ownIds = [];

    /// <summary>Leader observed at the previous refresh; tells a takeover from a reclaim of our own slot.</summary>
    private ulong? lastLeaderId;

    private volatile bool isLeader;
    private volatile string? leaderLabel;
    private int queuePosition;
    private int alivePeers;

    public PresenceCoordinator(
        Configuration config,
        IPluginLog log,
        DiscordSocketClient client,
        SocketTextChannel stateChannel,
        Func<Task<string>> characterLabelProvider,
        Action<string?> reportProblem)
    {
        this.reportProblem = reportProblem;
        this.config = config;
        this.log = log;
        this.client = client;
        this.stateChannel = stateChannel;
        this.characterLabelProvider = characterLabelProvider;
    }

    /// <summary>
    /// Raised on a false → true or true → false change of <see cref="IsLeader"/>. The second
    /// argument is true when we merely got our own slot back (a forfeit followed by a re-post,
    /// with nobody else having led in between); the bridge uses it to avoid announcing Online
    /// again on every network blip.
    /// </summary>
    public event Action<bool, bool>? LeadershipChanged;

    /// <summary>True when this instance owns the oldest alive presence message.</summary>
    public bool IsLeader => isLeader;

    /// <summary>1-based position of this instance in the queue, 0 while unknown.</summary>
    public int QueuePosition => Volatile.Read(ref queuePosition);

    /// <summary>Number of other alive instances.</summary>
    public int AlivePeers => Volatile.Read(ref alivePeers);

    /// <summary>Character label of the current leader, if known.</summary>
    public string? LeaderLabel => leaderLabel;

    /// <summary>The relay gate: leadership plus a valid heartbeat lease.</summary>
    public bool CanRelay =>
        isLeader && Environment.TickCount64 - Volatile.Read(ref lastHeartbeatOkTicks) < StaleSeconds * 1000L;

    // Clamped here rather than only in the UI: the config file is hand-editable and old configs
    // do not have the keys at all. StaleSeconds < 2 × heartbeat would break the lease invariant.
    private int HeartbeatSeconds => Math.Clamp(config.HeartbeatSeconds, 10, 120);

    private int StaleSeconds => Math.Clamp(config.StaleSeconds, HeartbeatSeconds * 2, 600);

    /// <summary>
    /// Posts this instance's presence message, then heartbeats and recomputes leadership until
    /// <paramref name="ct"/> is cancelled.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            label = await ReadCharacterLabelAsync();
            await PostOwnAsync(ct);

            // Compute once straight away: waiting a full heartbeat first would leave a fresh
            // instance unable to relay for up to HeartbeatSeconds after every login.
            await RefreshAsync(ct);

            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(HeartbeatSeconds), ct);
                await HeartbeatAsync(ct);
                await RefreshAsync(ct);
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
        catch (Exception ex)
        {
            log.Error(ex, "Presence coordinator stopped; this instance will not relay.");
            SetLeadership(false);
        }
    }

    /// <summary>
    /// Clean handoff: deletes this instance's presence message and reports whether any other
    /// instance is still alive (in which case the caller should not post the Offline notice).
    /// Never uses the session token — teardown cancels that before calling us.
    /// </summary>
    public async Task<bool> ResignAsync(CancellationToken ct)
    {
        var mine = own;
        own = null;
        SetLeadership(false);

        if (mine is not null)
        {
            try
            {
                await mine.DeleteAsync(new RequestOptions { CancelToken = ct });
            }
            catch (Exception ex)
            {
                // Worth a warning: the message stays behind with a fresh edit timestamp, so peers
                // see a phantom leader and all stay on standby until it goes stale.
                log.Warning(ex, "Could not delete the presence message on shutdown; peers may wait "
                                + "up to the stale timeout before one of them takes over.");
            }
        }

        try
        {
            var presence = await FetchPresenceAsync(ct);

            // Our own message is gone, so the newest remaining edit may itself be stale.
            // Anchor on the last server time we observed while heartbeating.
            var serverNow = lastServerNow;
            foreach (var message in presence)
            {
                var at = LastTouched(message);
                if (at > serverNow)
                    serverNow = at;
            }

            var stale = TimeSpan.FromSeconds(StaleSeconds);
            return presence.Any(m => m.Id != mine?.Id && serverNow - LastTouched(m) < stale);
        }
        catch (Exception ex)
        {
            // Unknown peer state: the caller falls back to posting Offline, as in Phase 1.
            log.Debug(ex, "Could not check for other instances on shutdown.");
            return false;
        }
    }

    // --- heartbeat ----------------------------------------------------------------------

    private async Task PostOwnAsync(CancellationToken ct)
    {
        try
        {
            own = await stateChannel.SendMessageAsync(
                BuildContent(),
                allowedMentions: AllowedMentions.None,
                options: new RequestOptions { CancelToken = ct });

            ownIds.Add(own.Id);
            Volatile.Write(ref lastHeartbeatOkTicks, Environment.TickCount64);
            log.Debug("Presence message posted in #{Channel}.", stateChannel.Name);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            own = null;
            log.Warning(ex, "Could not post the presence message; this instance stays on standby.");
        }
    }

    private async Task HeartbeatAsync(CancellationToken ct)
    {
        if (own is null)
        {
            await PostOwnAsync(ct);
            return;
        }

        try
        {
            beat++;
            var content = BuildContent();
            await own.ModifyAsync(p => p.Content = content, new RequestOptions { CancelToken = ct });
            Volatile.Write(ref lastHeartbeatOkTicks, Environment.TickCount64);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A failed heartbeat forfeits the slot. Simply retrying the edit would let a leader
            // that went quiet long enough for a peer to promote itself jump straight back to the
            // head of the queue on recovery (its snowflake is still the oldest) and relay
            // alongside that peer until the peer's next tick. Re-posting gives us a new, younger
            // snowflake — the back of the queue — so "oldest alive wins" stays a real invariant
            // that a network blip cannot rewind.
            log.Warning(ex, "Presence heartbeat failed; giving up this instance's place in the queue.");
            await ForfeitAndRepostAsync(ct);
        }
    }

    private async Task ForfeitAndRepostAsync(CancellationToken ct)
    {
        var mine = own;
        own = null;
        SetLeadership(false); // no message, no lease: CanRelay is false from here

        if (mine is not null)
        {
            try
            {
                await mine.DeleteAsync(new RequestOptions { CancelToken = ct });
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Leaving it behind is safe: nothing edits it any more, so it never counts as
                // alive again and the 10-minute cleanup removes it.
                log.Debug(ex, "Could not delete the forfeited presence message.");
            }
        }

        await PostOwnAsync(ct);
    }

    // --- leadership ---------------------------------------------------------------------

    private async Task RefreshAsync(CancellationToken ct)
    {
        List<IMessage> presence;
        try
        {
            presence = await FetchPresenceAsync(ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Keep the previous decision; the lease still expires if this persists.
            log.Warning(ex, "Could not read the presence channel; keeping the previous leadership state.");
            return;
        }

        if (presence.Count == 0)
        {
            if (own is not null)
            {
                // We just posted (or edited) our message successfully and still see nothing:
                // Discord returns an EMPTY list, not an error, when the bot lacks Read Message
                // History in the channel. Re-posting would only pile up messages we can't see,
                // so stand down, keep heartbeating the one we have, and say what is missing.
                if (!historyProblemReported)
                {
                    historyProblemReported = true;
                    var problem = $"The bot cannot read #{stateChannel.Name}: give it Read Message History "
                                  + "(and View Channel) on the state channel. Relaying is paused until then.";
                    log.Error("{Problem}", problem);
                    reportProblem(problem);
                }

                SetLeadership(false);
                return;
            }

            await PostOwnAsync(ct);
            return;
        }

        if (historyProblemReported)
        {
            historyProblemReported = false;
            reportProblem(null);
            log.Information("Presence channel is readable again.");
        }

        // Server time: immune to clock skew between officers' PCs. Our own message, just edited,
        // is normally the newest. Floor it with the last value we saw, so that a tick where our
        // own post failed and only stale peers remain does not make those peers look alive.
        var serverNow = presence.Max(LastTouched);
        if (serverNow > lastServerNow)
            lastServerNow = serverNow;
        else
            serverNow = lastServerNow;

        await CleanUpAsync(presence, serverNow, ct);

        if (own is not null && presence.All(m => m.Id != own.Id))
        {
            // Someone deleted our message. Re-post: a new snowflake puts us at the back of the
            // queue, which is acceptable and much simpler than trying to keep the old slot.
            log.Warning("This instance's presence message disappeared; re-posting at the back of the queue.");
            own = null;
            SetLeadership(false);
            await PostOwnAsync(ct);
            return;
        }

        var stale = TimeSpan.FromSeconds(StaleSeconds);
        var alive = presence
            .Where(m => serverNow - LastTouched(m) < stale)
            .OrderBy(m => m.Id)
            .ToList();

        var leader = alive.Count > 0 ? alive[0] : null;
        leaderLabel = leader is null ? null : ExtractLabel(leader.Content);

        var ownId = own?.Id;
        var index = ownId is null ? -1 : alive.FindIndex(m => m.Id == ownId);
        Volatile.Write(ref queuePosition, index + 1); // 0 when we are not in the alive set
        Volatile.Write(ref alivePeers, Math.Max(0, alive.Count - (index >= 0 ? 1 : 0)));

        SetLeadership(ownId is not null && leader is not null && leader.Id == ownId);
        lastLeaderId = leader?.Id;
    }

    /// <summary>Removes debris left behind by instances that crashed. Bot's own messages only.</summary>
    private async Task CleanUpAsync(List<IMessage> presence, DateTimeOffset serverNow, CancellationToken ct)
    {
        foreach (var message in presence)
        {
            if (message.Id == own?.Id || serverNow - LastTouched(message) <= CleanupAge)
                continue;

            try
            {
                await message.DeleteAsync(new RequestOptions { CancelToken = ct });
                log.Information("Removed a stale presence message from a crashed instance.");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                log.Debug(ex, "Could not delete a stale presence message.");
            }
        }
    }

    private void SetLeadership(bool value)
    {
        if (isLeader == value)
            return;

        isLeader = value;

        // Reclaim: the previous leader was one of our own (forfeited) messages, so from the
        // channel's point of view the relaying officer never changed.
        var reclaim = value && lastLeaderId is { } previous && ownIds.Contains(previous);

        try
        {
            LeadershipChanged?.Invoke(value, reclaim);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Leadership change handler threw.");
        }
    }

    // --- helpers ------------------------------------------------------------------------

    private async Task<List<IMessage>> FetchPresenceAsync(CancellationToken ct)
    {
        // Through IMessageChannel so CacheMode can be stated explicitly: presence is only correct
        // with fresh edit timestamps, and the socket overload would prefer the (empty) cache.
        var messages = await ((IMessageChannel)stateChannel)
            .GetMessagesAsync(100, CacheMode.AllowDownload, new RequestOptions { CancelToken = ct })
            .FlattenAsync();

        // Without our own user id every message would be filtered out, and an empty result is
        // read as "our message was deleted" → re-post. Treat it as a failed fetch instead.
        var self = client.CurrentUser?.Id
                   ?? throw new InvalidOperationException("Bot user is not available yet.");

        return messages
            .Where(m => m.Author.Id == self
                        && m.Content is { } c && c.StartsWith(Prefix, StringComparison.Ordinal))
            .ToList();
    }

    private static DateTimeOffset LastTouched(IMessage message) => message.EditedTimestamp ?? message.Timestamp;

    private string BuildContent() => $"{Prefix}{label}{BeatSeparator}{beat}";

    /// <summary>Reads the character label back out of a peer's presence message.</summary>
    private static string ExtractLabel(string content)
    {
        var body = content[Prefix.Length..];
        var cut = body.LastIndexOf(BeatSeparator, StringComparison.Ordinal);
        return (cut < 0 ? body : body[..cut]).Trim();
    }

    private async Task<string> ReadCharacterLabelAsync()
    {
        // The label is read on the game thread; don't wait forever if that thread is busy.
        var labelTask = characterLabelProvider();
        return await Task.WhenAny(labelTask, Task.Delay(TimeSpan.FromSeconds(2))) == labelTask
            ? labelTask.Result
            : "an officer";
    }
}
