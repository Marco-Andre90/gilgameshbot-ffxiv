using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using Dalamud.Plugin.Services;
using Discord;
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
/// tokens are replaced by <c>&lt;@id&gt;</c> / <c>&lt;@&amp;id&gt;</c>. A token resolves to a
/// mentionable role with that exact name, or a member whose username or display name matches
/// exactly (case-insensitive). Member lookup uses the "Search Guild Members" REST endpoint,
/// which does not need the privileged GUILD_MEMBERS intent. Successful lookups are cached briefly.
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

    // "@name" not preceded by a word character. Discord usernames are 2-32 chars of
    // letters, digits, '_' and '.'; '-' is allowed for roles and display names.
    [GeneratedRegex(@"(?<!\w)@([A-Za-z0-9_.\-]{2,32})")]
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

            // Keep trailing punctuation ("hi @marco.") out of the lookup and in the text.
            var name = match.Groups[1].Value.TrimEnd('.', '-');
            var tokenLength = 1 + name.Length;

            if (IsMassMention(name))
                continue;

            var mention = await LookupAsync(name, ct);
            if (mention is null)
                continue;

            sb.Append(Escape(rawText[last..match.Index])).Append(mention.Value.Text);
            (mention.Value.IsRole ? roleIds : userIds).Add(mention.Value.Id);
            last = match.Index + tokenLength;
        }

        sb.Append(Escape(rawText[last..]));
        return new ResolvedText(sb.ToString(), userIds, roleIds);
    }

    private static string Escape(string text) =>
        MessageFormatter.NeutraliseMassMentions(MessageFormatter.EscapeMarkdown(text));

    private static bool IsMassMention(string name) =>
        name.Equals("everyone", StringComparison.OrdinalIgnoreCase)
        || name.Equals("here", StringComparison.OrdinalIgnoreCase);

    private async Task<Mention?> LookupAsync(string name, CancellationToken ct)
    {
        if (cache.TryGetValue(name, out var cached) && DateTime.UtcNow - cached.CachedAt < CacheTtl)
            return cached.Value;

        try
        {
            var mention = ResolveRole(name) ?? await ResolveUserAsync(name, ct);
            cache[name] = (mention, DateTime.UtcNow); // negative results are cached too, but only on success
            return mention;
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

    private Mention? ResolveRole(string name)
    {
        // Only roles the server marked as mentionable; nobody should mass-ping "Members" from game chat.
        var role = guild.Roles.FirstOrDefault(r =>
            !r.IsEveryone && r.IsMentionable && string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));

        return role is null ? null : new Mention(role.Mention, role.Id, IsRole: true);
    }

    private async Task<Mention?> ResolveUserAsync(string name, CancellationToken ct)
    {
        // Prefix search on username and nickname; we then require an exact match.
        var results = await guild.SearchUsersAsync(name, limit: 20, options: new RequestOptions { CancelToken = ct });

        var user = results.FirstOrDefault(u => string.Equals(u.Username, name, StringComparison.OrdinalIgnoreCase))
                   ?? results.FirstOrDefault(u =>
                       string.Equals(u.Nickname, name, StringComparison.OrdinalIgnoreCase)
                       || string.Equals(u.GlobalName, name, StringComparison.OrdinalIgnoreCase)
                       || string.Equals(u.DisplayName, name, StringComparison.OrdinalIgnoreCase));

        return user is null ? null : new Mention(user.Mention, user.Id, IsRole: false);
    }
}
