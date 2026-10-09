using Discord;
using Discord.WebSocket;
using GilgameshBot.Calendar;

namespace GilgameshBot.Weekly;

/// <summary>The weekly's channels and role, resolved and checked.</summary>
public sealed record WeeklyTarget(ITextChannel Announcement, ITextChannel Approval, IRole Role);

/// <summary>Resolves the weekly's channels and role and checks everything it needs before any write.</summary>
internal static class WeeklyChecks
{
    /// <summary>Fixed text: the weekly's channels are on another server than this session's.</summary>
    public const string NotHere =
        "The weekly's channels are not on this branch's Discord server. Connect with a character from a branch on that server.";

    /// <summary>
    /// The weekly's channels and role, or the reason they cannot be used: not set up, clashing
    /// with another feature's channel, missing, a thread, on different servers (or not on
    /// <paramref name="guildId"/> when given), the bot lacking a permission, or a role the bot
    /// cannot mention. Apollo channels must be on the same server, since the weekly reads its
    /// events there.
    /// </summary>
    public static async Task<(WeeklyTarget? Target, string? Problem)> ResolveAsync(
        DiscordSocketClient client, Configuration config, ulong? guildId, CancellationToken ct)
    {
        // Read once: the settings window and a configuration sync replace these from other threads.
        var channelId = config.WeeklyChannelId;
        var approvalId = config.WeeklyApprovalChannelId;
        var roleId = config.WeeklyApproverRoleId;

        if (channelId == 0 || approvalId == 0 || roleId == 0)
            return (null, "No weekly is set up. Fill in the Weekly Announcements tab and publish it.");

        if (config.WeeklyChannelProblem(channelId, approvalId) is { } clash)
            return (null, clash);

        var options = new RequestOptions { CancelToken = ct };
        var announcement = await CalendarChannels.ResolveAsync(client, channelId, options);
        var approval = await CalendarChannels.ResolveAsync(client, approvalId, options);

        if (announcement is null)
            return (null, "The weekly's announcement channel was not found, or the bot cannot see it.");

        if (approval is null)
            return (null, "The weekly's approval channel was not found, or the bot cannot see it.");

        if (announcement is IThreadChannel || approval is IThreadChannel)
            return (null, "The weekly's channels must be text channels, not threads.");

        if (announcement.GuildId != approval.GuildId)
            return (null, "The weekly's announcement and approval channels must be on the same server.");

        if (guildId is { } here && announcement.GuildId != here)
            return (null, NotHere);

        if (client.GetGuild(announcement.GuildId) is not { } guild)
            return (null, "The bot is not a member of the weekly's server.");

        if (CalendarChannels.Permissions(client, announcement) is not { ViewChannel: true, ManageWebhooks: true, ReadMessageHistory: true })
            return (null, "The bot needs View Channel, Manage Webhooks and Read Message History in the weekly's announcement channel.");

        if (CalendarChannels.Permissions(client, approval) is not { } perms
            || !perms.ViewChannel || !perms.SendMessages || !perms.EmbedLinks || !perms.AttachFiles || !perms.ReadMessageHistory)
            return (null, "The bot needs View Channel, Send Messages, Embed Links, Attach Files and Read Message History in the weekly's approval channel.");

        if (guild.GetRole(roleId) is not { } role)
            return (null, "The weekly's approver role was not found on its server.");

        // Otherwise the preview names the role without notifying anybody.
        if (!role.IsMentionable && !perms.MentionEveryone)
            return (null, $"The bot cannot notify @{role.Name}: make the role mentionable (Server Settings → Roles), "
                          + "or give the bot Mention @everyone, @here and All Roles in the approval channel.");

        foreach (var id in config.ApolloChannelIds.ToList())
        {
            if (await CalendarChannels.ResolveAsync(client, id, options) is not { } apollo || apollo.GuildId != guild.Id)
                return (null, "The weekly reads its events from the calendar's Apollo channels, which must be on the weekly's server.");
        }

        return (new WeeklyTarget(announcement, approval, role), null);
    }
}
