using Discord;
using Discord.Rest;
using Discord.WebSocket;

namespace GilgameshBot.Relay;

/// <summary>
/// Finds a guild member by the name a human typed, without the privileged GUILD_MEMBERS intent.
/// </summary>
/// <remarks>
/// Shared by <see cref="MentionResolver"/> (which caches around it) and by the setup-code DM
/// delivery, so both agree on what "@marco" or "Justice Archon" resolves to. The lookup is the
/// "Search Guild Members" REST endpoint: a prefix search on the first word, then an exact,
/// case-insensitive match on username, global name, nickname or display name.
/// </remarks>
internal static class GuildMemberSearch
{
    /// <summary>Prefix search on the first word of the name. Covers every candidate length.</summary>
    public static async Task<IReadOnlyCollection<RestGuildUser>> SearchAsync(
        SocketGuild guild, string firstWord, CancellationToken ct) =>
        await guild.SearchUsersAsync(firstWord, limit: 50, options: new RequestOptions { CancelToken = ct });

    /// <summary>Exact, case-insensitive match inside search results. Username first.</summary>
    public static RestGuildUser? MatchExact(IEnumerable<RestGuildUser> results, string name)
    {
        var list = results as IReadOnlyCollection<RestGuildUser> ?? results.ToList();

        return list.FirstOrDefault(u => string.Equals(u.Username, name, StringComparison.OrdinalIgnoreCase))
               ?? list.FirstOrDefault(u =>
                   string.Equals(u.GlobalName, name, StringComparison.OrdinalIgnoreCase)
                   || string.Equals(u.Nickname, name, StringComparison.OrdinalIgnoreCase)
                   || string.Equals(u.DisplayName, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Search and match in one go, for callers with a complete name and no cache.</summary>
    public static async Task<RestGuildUser?> FindAsync(SocketGuild guild, string name, CancellationToken ct)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0)
            return null;

        var firstWord = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        return MatchExact(await SearchAsync(guild, firstWord, ct), trimmed);
    }
}
