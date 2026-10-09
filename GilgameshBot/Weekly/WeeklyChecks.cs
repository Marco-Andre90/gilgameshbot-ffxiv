using Discord;
using Discord.WebSocket;
using GilgameshBot.Calendar;

namespace GilgameshBot.Weekly;

/// <summary>The weekly's channels and role, resolved and checked.</summary>
/// <param name="Announcement">Where the weekly goes out: a text channel or a thread.</param>
/// <param name="WebhookHome">Where its webhook lives: the announcement channel, or a thread's parent.</param>
/// <param name="ThreadId">The announcement thread the webhook posts into; null for a plain channel.</param>
/// <param name="Approval">Where previews go: a text channel or a thread.</param>
/// <param name="ApprovalHome">Where the previews' webhook lives, like <paramref name="WebhookHome"/>.</param>
/// <param name="ApprovalThreadId">The approval thread, like <paramref name="ThreadId"/>.</param>
/// <param name="Leader">The FC leader, who approves or rejects previews; a member of the weekly's server.</param>
public sealed record WeeklyTarget(
    ITextChannel Announcement, IIntegrationChannel WebhookHome, ulong? ThreadId,
    ITextChannel Approval, IIntegrationChannel ApprovalHome, ulong? ApprovalThreadId,
    IUser Leader);

/// <summary>Resolves the weekly's channels and role and checks everything it needs before any write.</summary>
internal static class WeeklyChecks
{
    /// <summary>Fixed text: the weekly's channels are on another server than this session's.</summary>
    public const string NotHere =
        "The weekly's channels are not on this branch's Discord server. Connect with a character from a branch on that server.";

    /// <summary>
    /// The weekly's channels and role, or the reason they cannot be used: not set up, clashing
    /// with another feature's channel, missing, on different servers (or not on
    /// <paramref name="guildId"/> when given), the bot lacking a permission, or no FC leader on the
    /// server. Apollo channels must be on the same server, since the weekly reads its events there.
    /// </summary>
    public static async Task<(WeeklyTarget? Target, string? Problem)> ResolveAsync(
        DiscordSocketClient client, Configuration config, ulong? guildId, CancellationToken ct)
    {
        // Read once: the settings window and a configuration sync replace these from other threads.
        var channelId = config.WeeklyChannelId;
        var approvalId = config.WeeklyApprovalChannelId;
        var leaderId = config.FcLeaderId;

        if (channelId == 0 || approvalId == 0)
            return (null, "No weekly is set up. Fill in the Weekly Announcements tab and publish it.");

        if (leaderId == 0)
            return (null, "The weekly is approved by the FC leader: set the FC leader's Discord user ID on the Advanced tab and publish it.");

        if (config.WeeklyChannelProblem(channelId, approvalId) is { } clash)
            return (null, clash);

        var options = new RequestOptions { CancelToken = ct };
        var announcement = await CalendarChannels.ResolveAsync(client, channelId, options);
        var approval = await CalendarChannels.ResolveAsync(client, approvalId, options);

        if (announcement is null)
            return (null, "The weekly's announcement channel was not found, or the bot cannot see it.");

        if (approval is null)
            return (null, "The weekly's approval channel was not found, or the bot cannot see it.");

        if (announcement.GuildId != approval.GuildId)
            return (null, "The weekly's announcement and approval channels must be on the same server.");

        if (guildId is { } here && announcement.GuildId != here)
            return (null, NotHere);

        if (client.GetGuild(announcement.GuildId) is not { } guild)
            return (null, "The bot is not a member of the weekly's server.");

        // Either may be a thread: permissions are its parent's, and a webhook belongs to the parent
        // channel and posts into the thread.
        if (CalendarChannels.Permissions(client, announcement) is not { ViewChannel: true, ManageWebhooks: true, ReadMessageHistory: true })
            return (null, "The bot needs View Channel, Manage Webhooks and Read Message History in the weekly's announcement channel "
                          + "(for a thread, in its parent channel).");

        if (WebhookHome(client, announcement) is not var (home, threadId))
            return (null, "The weekly's announcement thread has no parent channel the bot can see.");

        // An archived announcement thread is reopened before the weekly goes out or is refreshed.
        if (announcement is IThreadChannel && CalendarChannels.Permissions(client, announcement) is not { SendMessagesInThreads: true })
            return (null, "The bot needs Send Messages in Threads in the parent channel of the weekly's announcement thread.");

        // Previews go out through a webhook too, so they look exactly like the weekly; the bot
        // still posts the "Write the weekly" button there itself.
        if (CalendarChannels.Permissions(client, approval) is not { } perms
            || !perms.ViewChannel || !perms.ManageWebhooks || !CalendarChannels.CanSend(approval, perms) || !perms.EmbedLinks
            || !perms.AttachFiles || !perms.ReadMessageHistory)
            return (null, "The bot needs View Channel, Manage Webhooks, Send Messages (Send Messages in Threads for a thread), "
                          + "Embed Links, Attach Files and Read Message History in the weekly's approval channel "
                          + "(for a thread, in its parent channel).");

        if (WebhookHome(client, approval) is not var (approvalHome, approvalThreadId))
            return (null, "The weekly's approval thread has no parent channel the bot can see.");

        // Over REST: without the members intent the cache does not hold the server's members.
        if (await client.Rest.GetGuildUserAsync(guild.Id, leaderId, options) is not { } leader)
            return (null, "The FC leader (Advanced tab) is not a member of the weekly's server.");

        foreach (var id in config.ApolloChannelIds.ToList())
        {
            if (await CalendarChannels.ResolveAsync(client, id, options) is not { } apollo || apollo.GuildId != guild.Id)
                return (null, "The weekly reads its events from the calendar's Apollo channels, which must be on the weekly's server.");
        }

        return (new WeeklyTarget(announcement, home, threadId, approval, approvalHome, approvalThreadId, leader), null);
    }

    /// <summary>
    /// Where a webhook posting into <paramref name="channel"/> lives, and the thread it posts into:
    /// the channel itself, or a thread's parent and the thread. Null when a thread's parent is unknown.
    /// </summary>
    private static (IIntegrationChannel Home, ulong? ThreadId)? WebhookHome(DiscordSocketClient client, ITextChannel channel)
    {
        if (channel is not IThreadChannel thread)
            return channel is IIntegrationChannel home ? (home, null) : null;

        var parentId = thread switch
        {
            SocketThreadChannel { ParentChannel: { } p } => p.Id,
            Discord.Rest.RestThreadChannel rest => rest.ParentChannelId,
            _ => 0UL,
        };

        return client.GetChannel(parentId) is IIntegrationChannel parent ? (parent, thread.Id) : null;
    }
}
