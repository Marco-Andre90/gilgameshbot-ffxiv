using Discord;
using Discord.WebSocket;

namespace GilgameshBot.Calendar;

/// <summary>A source channel the bot cannot read: fixed text for the officer.</summary>
public sealed class CalendarSourceException(string message) : Exception(message);

/// <summary>Reads the Apollo posts of a calendar's source channels through Discord's REST API.</summary>
/// <remarks>
/// Message content (embeds included) needs the Message Content intent switched on for the bot in
/// the Developer Portal. It is not requested on the gateway: reading over REST is enough, and the
/// gateway would otherwise deliver every message of the server to every member's plugin.
/// </remarks>
public static class ApolloReader
{
    /// <summary>How many of a channel's newest messages are read: one page, which covers months of Apollo posts.</summary>
    private const int Window = 100;

    /// <summary>
    /// Every event in <paramref name="channelIds"/>. Throws <see cref="CalendarSourceException"/>
    /// when a channel is missing or unreadable: a calendar built from part of its sources would
    /// show events as gone.
    /// </summary>
    public static async Task<List<CalendarEvent>> ReadAsync(SocketGuild guild, IEnumerable<ulong> channelIds, CancellationToken ct)
    {
        var posts = new List<ApolloPost>();
        foreach (var channelId in channelIds.Distinct())
        {
            if (guild.GetTextChannel(channelId) is not { } channel)
                throw new CalendarSourceException("The Apollo channel was not found, or the bot cannot see it.");

            // Without Read Message History Discord answers an empty list, not an error.
            var perms = guild.CurrentUser.GetPermissions(channel);
            if (!perms.ViewChannel || !perms.ReadMessageHistory)
                throw new CalendarSourceException("The bot needs View Channel and Read Message History in the Apollo channel.");

            // Through IMessageChannel so the (empty) message cache is never preferred.
            var messages = await ((IMessageChannel)channel)
                .GetMessagesAsync(Window, CacheMode.AllowDownload, new RequestOptions { CancelToken = ct })
                .FlattenAsync();

            var apollo = messages.Where(m => m.Author.Id == ApolloParser.ApolloBotId).ToList();

            // Without the Message Content intent Discord still lists Apollo's posts, but with their
            // embeds and buttons stripped: they would read as "no events" and empty the calendar.
            if (apollo.Count > 0 && apollo.All(m => m.Embeds.Count == 0))
                throw new CalendarSourceException(
                    "Discord hides Apollo's posts from the bot. Switch on Message Content Intent for the bot "
                    + "in the Discord Developer Portal (Bot tab).");

            posts.AddRange(apollo.Select(ToPost));
        }

        return ApolloParser.ParseAll(posts);
    }

    private static ApolloPost ToPost(IMessage message)
    {
        var embed = message.Embeds.FirstOrDefault();
        return new ApolloPost(
            message.Channel.Id,
            message.Id,
            message.Author.Id,
            embed?.Title,
            embed?.Fields.Select(f => (f.Name, f.Value)).ToList() ?? [],
            CustomIds(message.Components));
    }

    /// <summary>Custom ids of every button and menu, however deeply nested in rows.</summary>
    internal static List<string> CustomIds(IEnumerable<IMessageComponent> components)
    {
        var ids = new List<string>();
        foreach (var component in components)
        {
            switch (component)
            {
                case ActionRowComponent row:
                    ids.AddRange(CustomIds(row.Components));
                    break;
                case ButtonComponent { CustomId: { } buttonId }:
                    ids.Add(buttonId);
                    break;
                case SelectMenuComponent { CustomId: { } menuId }:
                    ids.Add(menuId);
                    break;
            }
        }

        return ids;
    }
}
