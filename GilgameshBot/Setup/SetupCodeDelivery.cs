using Dalamud.Plugin.Services;
using Discord;
using Discord.Net;
using Discord.WebSocket;
using GilgameshBot.Relay;

namespace GilgameshBot.Setup;

/// <summary>
/// What to tell the officer after a setup code operation. <see cref="Message"/> is written for
/// game chat and the settings window, and never contains the code or any part of it.
/// </summary>
public readonly record struct SetupCodeOutcome(bool Ok, string Message);

/// <summary>
/// Hands a setup code to another officer as a Discord direct message, and cleans up afterwards.
/// </summary>
/// <remarks>
/// The DM carries the bot token, exactly like the clipboard code does, so it is treated as a
/// short-lived secret: the importing plugin replaces the message with a receipt on its first
/// successful connect, and the sending plugin deletes any message that was never imported after
/// its lifetime (<see cref="InGameLifetime"/> or <see cref="SlashLifetime"/>). Nothing here is ever logged or printed — not the code, not any part
/// of it. This class owns no state: the caller passes in the live client, the guild, the config
/// and the character label.
/// </remarks>
public static class SetupCodeDelivery
{
    /// <summary>How long a code sent from in-game (/gilga send) may sit unimported in the DM.</summary>
    public static readonly TimeSpan InGameLifetime = TimeSpan.FromHours(24);

    /// <summary>How long a code fetched with /setupcode may sit unimported: the member asked for it just now.</summary>
    public static readonly TimeSpan SlashLifetime = TimeSpan.FromMinutes(5);

    /// <summary>How often the expiry sweep runs while connected; short enough for the 5-minute codes.</summary>
    public static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);

    /// <summary>A receipt starts with this; used to tell a scrubbed DM from one still carrying a code.</summary>
    private const string ReceiptMarker = "✅";

    private const string Placeholder = "Preparing your GilgameshBot setup code…";

    /// <summary>
    /// Serialises the read-modify-write of <see cref="Configuration.SentSetupCodes"/> and
    /// <see cref="Configuration.PendingReceipt"/>, which happens on background tasks while the
    /// settings window reads the same config on the draw thread. The list is always replaced,
    /// never mutated in place, so a reader mid-frame keeps enumerating a stable snapshot.
    /// </summary>
    private static readonly object ConfigGate = new();

    // --- Sending ----------------------------------------------------------------------------

    /// <summary>
    /// Resolves <paramref name="targetName"/> in <paramref name="guild"/> and DMs them a setup
    /// code. Returns the line to show the officer — never containing the code.
    /// </summary>
    public static async Task<SetupCodeOutcome> SendAsync(
        DiscordSocketClient client,
        SocketGuild guild,
        Configuration config,
        string targetName,
        string characterLabel,
        IPluginLog log,
        CancellationToken ct)
    {
        var name = targetName.Trim();
        if (name.Length == 0)
            return new SetupCodeOutcome(false, "Say who to send it to: /gilga send <discord username>.");

        if (!config.IsDiscordConfigured)
            return new SetupCodeOutcome(false, "There is nothing to send yet: save a bot token and at least one branch first.");

        IUser? user;
        try
        {
            user = await GuildMemberSearch.FindAsync(guild, name, ct);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Looking up a member to DM the setup code to failed.");
            return new SetupCodeOutcome(false, $"Could not search {guild.Name} for members. Try again in a moment.");
        }

        if (user is null)
            return new SetupCodeOutcome(false, $"No member named {name} in {guild.Name}.");

        return await SendToUserAsync(user, config, characterLabel, InGameLifetime, log, ct);
    }

    /// <summary>
    /// DMs a setup code to an already-resolved member. This is the whole delivery: placeholder,
    /// edit with the code carrying its own receipt pointer, and the tracking entry the 24-hour
    /// sweep works from. <see cref="SendAsync"/> reaches it after resolving a typed name; the
    /// <c>/setupcode</c> slash command hands in the invoking member directly.
    /// </summary>
    public static async Task<SetupCodeOutcome> SendToUserAsync(
        IUser user,
        Configuration config,
        string characterLabel,
        TimeSpan lifetime,
        IPluginLog log,
        CancellationToken ct)
    {
        if (!config.IsDiscordConfigured)
            return new SetupCodeOutcome(false, "There is nothing to send yet: save a bot token and at least one branch first.");

        var displayName = user.Username;

        IDMChannel dm;
        IUserMessage message;
        try
        {
            dm = await user.CreateDMChannelAsync(new RequestOptions { CancelToken = ct });

            // Sent as a placeholder first: the code has to carry the id of the very message it
            // travels in, so the importer knows which DM to replace with a receipt.
            message = await dm.SendMessageAsync(
                Placeholder,
                allowedMentions: AllowedMentions.None,
                options: new RequestOptions { CancelToken = ct });
        }
        catch (HttpException ex) when (ex.DiscordCode == DiscordErrorCode.CannotSendMessageToUser)
        {
            // Worded so it reads the same to the officer who typed /gilga send and to the member
            // who ran /setupcode and is reading it as an ephemeral reply.
            return new SetupCodeOutcome(false,
                $"GilgameshBot cannot DM {displayName}: they have direct messages from members of "
                + "this server turned off. Turn that on (Privacy Settings for this server), "
                + "or share the code by clipboard.");
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Could not open a DM to hand over a setup code.");
            return new SetupCodeOutcome(false, $"Could not send a DM to {displayName}. Share the code by clipboard instead.");
        }

        // Tracked from the moment a message id exists, before the edit that puts the code in it:
        // a DM the plugin has forgotten about would never expire, while a tracked placeholder is
        // harmlessly dropped by the next sweep.
        var entry = new SentSetupCode
        {
            ChannelId = dm.Id,
            MessageId = message.Id,
            To = displayName,
            SentAtUtc = DateTime.UtcNow,
            ExpiresAtUtc = DateTime.UtcNow + lifetime,
        };

        Remember(config, entry);

        var branches = string.Join(", ", config.Branches.Where(b => b.IsComplete).Select(b => b.Name));
        var body = $"🎮 **GilgameshBot setup code** for {MessageFormatter.EscapeMarkdown(branches)}, "
                   + $"sent by {MessageFormatter.EscapeMarkdown(characterLabel)}.\n"
                   + "Copy the line below, then in game type `/gilga import`.\n"
                   + "This message is replaced with a receipt once you import it, "
                   + $"and is deleted after {Describe(lifetime)} if you don't.\n\n"
                   + SetupCode.Encode(config, new SetupReceipt
                   {
                       Channel = dm.Id.ToString(),
                       Message = message.Id.ToString(),
                   });

        // Discord's own limit. Each branch costs a few hundred base64 characters, so an officer
        // with a long branch table hits this — and a bare 400 from Discord would explain nothing.
        if (body.Length > MessageFormatter.MaxLength)
        {
            await AbandonAsync(config, message, entry, log);
            return new SetupCodeOutcome(false,
                "The setup code is too long to fit in one Discord message — you have too many branches. "
                + "Use Export setup code and share it by private message instead.");
        }

        try
        {
            await message.ModifyAsync(m => m.Content = body, new RequestOptions { CancelToken = ct });
        }
        catch (Exception ex)
        {
            // Never log the exception's content here beyond its type: the request body carried
            // the code. Take the placeholder back down rather than leaving the recipient looking
            // at "Preparing…" until the 24-hour sweep gets to it.
            log.Warning("Could not write the setup code into the DM ({Type}).", ex.GetType().Name);
            await AbandonAsync(config, message, entry, log);
            return new SetupCodeOutcome(false, $"Could not finish the DM to {displayName}. Share the code by clipboard instead.");
        }

        return new SetupCodeOutcome(true, $"Setup code sent to {displayName} by DM. It expires in {Describe(lifetime)}.");
    }

    // --- Expiry and revocation ---------------------------------------------------------------

    /// <summary>
    /// Deletes setup code DMs that were never imported. With <paramref name="all"/> the age is
    /// ignored, which is what <c>/gilga revoke</c> does. Returns how many were withdrawn.
    /// </summary>
    /// <remarks>
    /// A message that has already become a receipt is left alone — it carries no code — and its
    /// entry is simply dropped. So is an entry whose channel or message is gone.
    /// </remarks>
    public static async Task<int> SweepAsync(
        DiscordSocketClient client,
        Configuration config,
        bool all,
        IPluginLog log,
        CancellationToken ct)
    {
        var entries = config.SentSetupCodes.ToArray();
        if (entries.Length == 0)
            return 0;

        var now = DateTime.UtcNow;
        var revoked = 0;

        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();

            if (!all && entry.ExpiresAtUtc > now)
                continue;

            try
            {
                var channel = await GetDmChannelAsync(client, entry.ChannelId, ct);
                var message = channel is null
                    ? null
                    : await channel.GetMessageAsync(entry.MessageId, options: new RequestOptions { CancelToken = ct });

                if (message is IUserMessage sent && !sent.Content.StartsWith(ReceiptMarker, StringComparison.Ordinal))
                {
                    await sent.DeleteAsync(new RequestOptions { CancelToken = ct });
                    revoked++;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Gone, or unreachable. Either way the entry is of no further use: keeping it
                // would retry forever. Nothing here may quote the message content.
                log.Debug("Could not withdraw a setup code DM ({Type}); dropping the entry.", ex.GetType().Name);
            }

            Forget(config, entry);
        }

        return revoked;
    }

    // --- Receipt (importer side) --------------------------------------------------------------

    /// <summary>
    /// Replaces the DM this plugin's setup code arrived in with a receipt, now that the token has
    /// proven to work. Clears <see cref="Configuration.PendingReceipt"/> when done — or when the
    /// message is beyond reach, so this never retries forever.
    /// </summary>
    public static async Task ApplyPendingReceiptAsync(
        DiscordSocketClient client,
        Configuration config,
        string characterLabel,
        IPluginLog log,
        CancellationToken ct)
    {
        if (config.PendingReceipt is not { } pending)
            return;

        try
        {
            var channel = await GetDmChannelAsync(client, pending.ChannelId, ct);
            var message = channel is null
                ? null
                : await channel.GetMessageAsync(pending.MessageId, options: new RequestOptions { CancelToken = ct });

            // Only ever edit the bot's own message, and only while it still holds a code.
            if (message is IUserMessage sent && sent.Author.Id == client.CurrentUser.Id)
            {
                await sent.ModifyAsync(
                    m => m.Content = $"{ReceiptMarker} **Setup code imported** by "
                                     + $"{MessageFormatter.EscapeMarkdown(characterLabel)} on "
                                     + $"{DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC. "
                                     + "The code was removed from this message for safety.",
                    new RequestOptions { CancelToken = ct });
            }

            ClearPendingReceipt(config, pending);
        }
        catch (OperationCanceledException)
        {
            // Disconnecting; try again on the next connect.
        }
        catch (HttpException ex) when (ex.HttpCode is System.Net.HttpStatusCode.Forbidden
                                           or System.Net.HttpStatusCode.NotFound)
        {
            // The DM is not reachable by this bot at all (different token, deleted channel).
            // Retrying on every connect would never change the answer.
            log.Debug("The setup code DM cannot be scrubbed ({Code}); giving up on the receipt.", ex.HttpCode);
            ClearPendingReceipt(config, pending);
        }
        catch (Exception ex)
        {
            log.Debug("Could not leave the setup code receipt ({Type}); will retry on the next connect.",
                ex.GetType().Name);
        }
    }

    // --- Helpers -------------------------------------------------------------------------------

    private static string Describe(TimeSpan lifetime) =>
        lifetime.TotalHours >= 1 ? $"{(int)lifetime.TotalHours} hours" : $"{(int)lifetime.TotalMinutes} minutes";

    /// <summary>
    /// The DM channel with the given id, over REST. The socket client caches no DM channels
    /// (MessageCacheSize is 0), so this always goes to Discord.
    /// </summary>
    private static async Task<IDMChannel?> GetDmChannelAsync(
        DiscordSocketClient client, ulong channelId, CancellationToken ct) =>
        await client.Rest.GetChannelAsync(channelId, new RequestOptions { CancelToken = ct }) as IDMChannel;

    /// <summary>
    /// Takes a placeholder back down when the code never made it into the message, and forgets
    /// the entry. Best effort: a placeholder left behind carries no code, and the sweep will
    /// drop the entry anyway.
    /// </summary>
    private static async Task AbandonAsync(
        Configuration config, IUserMessage message, SentSetupCode entry, IPluginLog log)
    {
        try
        {
            await message.DeleteAsync();
        }
        catch (Exception ex)
        {
            log.Debug("Could not withdraw the placeholder DM ({Type}).", ex.GetType().Name);
        }

        Forget(config, entry);
    }

    private static void Remember(Configuration config, SentSetupCode entry)
    {
        lock (ConfigGate)
        {
            config.SentSetupCodes = [.. config.SentSetupCodes, entry];
            config.Save();
        }
    }

    private static void Forget(Configuration config, SentSetupCode entry)
    {
        lock (ConfigGate)
        {
            config.SentSetupCodes = config.SentSetupCodes
                .Where(e => e.ChannelId != entry.ChannelId || e.MessageId != entry.MessageId)
                .ToList();
            config.Save();
        }
    }

    private static void ClearPendingReceipt(Configuration config, ReceiptPointer pending)
    {
        lock (ConfigGate)
        {
            // Leave a newer pointer alone: another code may have been imported meanwhile.
            if (config.PendingReceipt is { } current
                && current.ChannelId == pending.ChannelId
                && current.MessageId == pending.MessageId)
            {
                config.PendingReceipt = null;
                config.Save();
            }
        }
    }
}
