using Discord;
using Discord.Net;
using Discord.Rest;
using Discord.WebSocket;

namespace GilgameshBot.Calendar;

/// <summary>Finding the calendar's channels, which may be threads, and the bot's permissions in them.</summary>
internal static class CalendarChannels
{
    /// <summary>
    /// The text channel or thread <paramref name="channelId"/>: from the gateway cache, or else over
    /// REST. On connect Discord only sends a server's <em>active</em> threads, so a calendar thread
    /// that archived while nobody was connected is missing from the cache. Null when it does not
    /// exist or the bot cannot see it.
    /// </summary>
    public static async Task<ITextChannel?> ResolveAsync(DiscordSocketClient client, ulong channelId, RequestOptions options)
    {
        if (client.GetChannel(channelId) is ITextChannel cached)
            return cached;

        try
        {
            return await client.Rest.GetChannelAsync(channelId, options) as ITextChannel;
        }
        catch (HttpException ex) when (ex.HttpCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Forbidden)
        {
            return null;
        }
    }

    /// <summary>
    /// The bot's permissions in <paramref name="channel"/>; null when its server is unknown. A thread
    /// has no permission overwrites of its own: they are its parent channel's.
    /// </summary>
    public static ChannelPermissions? Permissions(DiscordSocketClient client, ITextChannel channel)
    {
        if (client.GetGuild(channel.GuildId) is not { } guild)
            return null;

        IGuildChannel target = channel switch
        {
            SocketThreadChannel { ParentChannel: SocketGuildChannel parent } => parent,
            RestThreadChannel thread when guild.GetChannel(thread.ParentChannelId) is { } parent => parent,
            _ => channel,
        };

        return guild.CurrentUser.GetPermissions(target);
    }

    /// <summary>True when the bot may post in <paramref name="channel"/>: Send Messages, or Send Messages in Threads for a thread.</summary>
    public static bool CanSend(ITextChannel channel, ChannelPermissions perms) =>
        channel is IThreadChannel ? perms.SendMessagesInThreads : perms.SendMessages;

    /// <summary>Reopens an archived thread: its messages cannot be edited until then.</summary>
    public static async Task ReopenAsync(ITextChannel channel, RequestOptions options)
    {
        if (channel is IThreadChannel { IsArchived: true } thread)
            await thread.ModifyAsync(p => p.Archived = false, options);
    }
}
