using System.Net;
using System.Text.RegularExpressions;

namespace GilgameshBot.Roster;

/// <summary>One member as the Lodestone lists them.</summary>
public sealed record LodestoneMember(ulong Id, string Name, string Rank);

/// <summary>A scan could not produce the complete member list. The message is fixed text meant for the user.</summary>
public sealed class LodestoneException(string message) : Exception(message);

/// <summary>
/// Reads a Free Company's member list from its public Lodestone pages
/// (<c>/lodestone/freecompany/{id}/member/?page=N</c>, 50 members a page).
/// </summary>
/// <remarks>
/// The list is only returned when it is complete: every page read, no duplicates, and exactly
/// as many members as the page says the Free Company has. A partial list would report half the
/// Free Company as having left, so any doubt is an exception and nothing gets written.
/// </remarks>
public static partial class LodestoneClient
{
    private const string BaseUrl = "https://na.finalfantasyxiv.com/lodestone/freecompany/";
    private const int PageSize = 50;

    /// <summary>A Free Company holds at most 512 members; anything past this is a parsing error.</summary>
    private const int MaxPages = 12;

    /// <summary>Gap between two page requests, to stay polite with the Lodestone.</summary>
    private static readonly TimeSpan PageDelay = TimeSpan.FromSeconds(1);

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        // Past the last page the Lodestone redirects; following it would read some other page
        // and duplicate members, so redirects are failures here.
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("GilgameshBot/1.0 (Dalamud plugin; FC roster)");
        return client;
    }

    public static async Task<List<LodestoneMember>> FetchMembersAsync(ulong freeCompanyId, CancellationToken ct)
    {
        var first = await FetchPageAsync(freeCompanyId, 1, ct);

        var totalMatch = TotalRegex().Match(first);
        if (!totalMatch.Success || !int.TryParse(totalMatch.Groups[1].Value, out var total) || total <= 0)
            throw new LodestoneException("The Lodestone page did not show a member count. Check the Lodestone ID, or try again later.");

        var pages = (total + PageSize - 1) / PageSize;
        if (pages > MaxPages)
            throw new LodestoneException("The Lodestone reported more members than a Free Company can have.");

        var members = new Dictionary<ulong, LodestoneMember>();
        AddEntries(first, members);

        for (var page = 2; page <= pages; page++)
        {
            await Task.Delay(PageDelay, ct);
            AddEntries(await FetchPageAsync(freeCompanyId, page, ct), members);
        }

        // Someone joining or leaving mid-scan shifts the pages under us; the next scan is exact.
        if (members.Count != total)
            throw new LodestoneException(
                $"The Lodestone listed {members.Count} of {total} members, probably because the list changed during the scan. Try again in a few minutes.");

        return [.. members.Values];
    }

    private static async Task<string> FetchPageAsync(ulong freeCompanyId, int page, CancellationToken ct)
    {
        using var response = await Http.GetAsync($"{BaseUrl}{freeCompanyId}/member/?page={page}", ct);

        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new LodestoneException("The Lodestone has no Free Company with that ID.");

        if (response.StatusCode != HttpStatusCode.OK)
            throw new LodestoneException(
                $"The Lodestone answered {(int)response.StatusCode} (maintenance or too many requests?). Try again later.");

        return await response.Content.ReadAsStringAsync(ct);
    }

    private static void AddEntries(string html, Dictionary<ulong, LodestoneMember> members)
    {
        foreach (Match m in EntryRegex().Matches(html))
        {
            if (!ulong.TryParse(m.Groups["id"].Value, out var id))
                continue;

            var name = WebUtility.HtmlDecode(m.Groups["name"].Value).Trim();
            var rank = WebUtility.HtmlDecode(m.Groups["rank"].Value).Trim();
            members.TryAdd(id, new LodestoneMember(id, name, rank));
        }
    }

    [GeneratedRegex("""<div class="parts__total">\s*(\d+)""")]
    private static partial Regex TotalRegex();

    // One <li class="entry"> per member: character link, name, then the Free Company info list,
    // whose first item is the rank (icon + name).
    [GeneratedRegex(
        """<li class="entry"><a href="/lodestone/character/(?<id>\d+)/".*?<p class="entry__name">(?<name>[^<]*)</p>.*?<ul class="entry__freecompany__info"><li><img[^>]*><span>(?<rank>[^<]*)</span>""",
        RegexOptions.Singleline)]
    private static partial Regex EntryRegex();
}
