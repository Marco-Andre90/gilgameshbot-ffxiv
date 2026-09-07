using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using Dalamud.Plugin.Services;
using Discord;
using Discord.Rest;
using Discord.WebSocket;

namespace GilgameshBot.Relay;

/// <summary>Result of mention resolution: Discord-ready text plus the exact IDs that may be pinged.</summary>
public sealed record ResolvedText(string Text, IReadOnlyList<ulong> UserIds, IReadOnlyList<ulong> RoleIds)
{
    /// <summary>
    /// An allow-list containing only what the resolver produced. Anything else in the text —
    /// including a raw <c>&lt;@id&gt;</c> typed in game — renders as text and pings nobody.
    /// </summary>
    public AllowedMentions ToAllowedMentions() => new()
    {
        UserIds = UserIds.ToList(),
        RoleIds = RoleIds.ToList(),
    };
}

/// <summary>
/// Converts <c>@name</c> tokens typed in game chat into real Discord mentions.
/// </summary>
/// <remarks>
/// Works on the <em>raw</em> game text: segments between mentions are markdown-escaped, mention
/// tokens are replaced by <c>&lt;@id&gt;</c> / <c>&lt;@&amp;id&gt;</c>. A token is the word after
/// the <c>@</c> plus up to three following words, so multi-word names such as
/// <c>@Justice Archon</c> work: candidates are tried from the longest to the single word, and each
/// candidate must match a mentionable role name exactly, or a member's username / display name
/// exactly (case-insensitive). Whatever words are not consumed stay as plain text. Member lookup
/// uses the "Search Guild Members" REST endpoint (prefix search on the first word, one call per
/// token), which does not need the privileged GUILD_MEMBERS intent. Lookups are cached briefly.
/// <c>@everyone</c> and <c>@here</c> are never resolved.
/// </remarks>
public sealed partial class MentionResolver
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);

    private readonly SocketGuild guild;
    private readonly IPluginLog log;
    private readonly ConcurrentDictionary<string, (Mention? Value, DateTime CachedAt)> cache =
        new(StringComparer.OrdinalIgnoreCase);

    public MentionResolver(SocketGuild guild, IPluginLog log)
    {
        this.guild = guild;
        this.log = log;
    }

    private readonly record struct Mention(string Text, ulong Id, bool IsRole);

    /// <summary>Most words a single mention may span ("@Justice Archon" is two).</summary>
    private const int MaxWords = 4;

    // "@name" not preceded by a word character, followed by up to three more space-separated
    // words that may belong to a multi-word role or display name. Discord usernames are 2-32
    // chars of letters, digits, '_' and '.'; '-' is allowed for roles and display names.
    [GeneratedRegex(@"(?<!\w)@([A-Za-z0-9_.\-]{2,32}(?: [A-Za-z0-9_.\-]+){0,3})")]
    private static partial Regex MentionToken();

    /// <summary>Escapes <paramref name="rawText"/> for Discord and resolves its mention tokens.</summary>
    public async Task<ResolvedText> ResolveAsync(string rawText, bool resolveMentions, CancellationToken ct)
    {
        var matches = resolveMentions ? MentionToken().Matches(rawText) : null;
        if (matches is null || matches.Count == 0)
            return new ResolvedText(Escape(rawText), [], []);

        var sb = new StringBuilder(rawText.Length + 32);
        var userIds = new List<ulong>();
        var roleIds = new List<ulong>();
        var last = 0;

        foreach (Match match in matches)
        {
            ct.ThrowIfCancellationRequested();

            var words = match.Groups[1].Value.Split(' ');
            if (IsMassMention(words[0]))
                continue;

            var resolved = await LookupAsync(words, ct);
            if (resolved is null)
                continue;

            var (mention, consumed) = resolved.Value;
            sb.Append(Escape(rawText[last..match.Index])).Append(mention.Text);
            (mention.IsRole ? roleIds : userIds).Add(mention.Id);
            last = match.Index + 1 + consumed;
        }

        sb.Append(Escape(rawText[last..]));
        return new ResolvedText(sb.ToString(), userIds, roleIds);
    }

    private static string Escape(string text) =>
        MessageFormatter.NeutraliseMassMentions(MessageFormatter.EscapeMarkdown(text));

    private static bool IsMassMention(string name) =>
        name.Equals("everyone", StringComparison.OrdinalIgnoreCase)
        || name.Equals("here", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Tries the longest candidate first ("Justice Archon", then "Justice"), roles before members
    /// at each length, and returns the mention plus how many characters of the token it consumed.
    /// Member search results are fetched once per token (prefix search on the first word) and
    /// reused for every candidate length.
    /// </summary>
    private async Task<(Mention Mention, int Consumed)?> LookupAsync(string[] words, CancellationToken ct)
    {
        IReadOnlyCollection<RestGuildUser>? members = null;

        for (var count = Math.Min(words.Length, MaxWords); count >= 1; count--)
        {
            // Keep trailing punctuation ("hi @marco.") out of the lookup and in the text.
            var name = string.Join(' ', words, 0, count).TrimEnd('.', '-');
            if (name.Length == 0)
                continue;

            if (cache.TryGetValue(name, out var cached) && DateTime.UtcNow - cached.CachedAt < CacheTtl)
            {
                if (cached.Value is { } hit)
                    return (hit, name.Length);
                continue;
            }

            try
            {
                members ??= await SearchMembersAsync(words[0], ct);
                var mention = ResolveRole(name) ?? ResolveUser(members, name);
                cache[name] = (mention, DateTime.UtcNow); // negative results are cached too, but only on success
                if (mention is { } found)
                    return (found, name.Length);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Transient REST failure: don't poison the cache, just skip this mention.
                log.Warning(ex, "Mention lookup failed for '{Name}'.", name);
                return null;
            }
        }

        return null;
    }

    private Mention? ResolveRole(string name)
    {
        // Only roles the server marked as mentionable; nobody should mass-ping "Members" from game chat.
        var role = guild.Roles.FirstOrDefault(r =>
            !r.IsEveryone && r.IsMentionable && string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));

        return role is null ? null : new Mention(role.Mention, role.Id, IsRole: true);
    }

    /// <summary>
    /// Prefix search on username and nickname. A multi-word nickname ("Justice Archon") starts
    /// with its first word, so one search per token covers every candidate length.
    /// </summary>
    private async Task<IReadOnlyCollection<RestGuildUser>> SearchMembersAsync(string firstWord, CancellationToken ct) =>
        await guild.SearchUsersAsync(firstWord, limit: 50, options: new RequestOptions { CancelToken = ct });

    private static Mention? ResolveUser(IReadOnlyCollection<RestGuildUser> results, string name)
    {
        // Exact match only, username first.
        var user = results.FirstOrDefault(u => string.Equals(u.Username, name, StringComparison.OrdinalIgnoreCase))
                   ?? results.FirstOrDefault(u =>
                       string.Equals(u.Nickname, name, StringComparison.OrdinalIgnoreCase)
                       || string.Equals(u.GlobalName, name, StringComparison.OrdinalIgnoreCase)
                       || string.Equals(u.DisplayName, name, StringComparison.OrdinalIgnoreCase));

        return user is null ? null : new Mention(user.Mention, user.Id, IsRole: false);
    }
}
