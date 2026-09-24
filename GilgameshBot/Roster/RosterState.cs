using System.Text;
using System.Text.Json.Serialization;
using GilgameshBot.Relay;

namespace GilgameshBot.Roster;

/// <summary>One member as last seen by a scan.</summary>
public sealed class RosterMember
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("rank")] public string Rank { get; set; } = string.Empty;

    /// <summary>First scan that saw them. Null for everyone already there on the first scan.</summary>
    [JsonPropertyName("joined")] public DateTime? JoinedUtc { get; set; }

    /// <summary>
    /// First scan that saw them in the starter rank. Null for someone already in it on the first
    /// scan (or when the starter rank setting changed), which reads "since before the first scan".
    /// </summary>
    [JsonPropertyName("starterSince")] public DateTime? StarterSinceUtc { get; set; }
}

/// <summary>
/// The roster of one Free Company, stored as a JSON attachment on the roster state message.
/// Keyed by Lodestone character ID, so a name change is not a leave + join.
/// </summary>
public sealed class RosterState
{
    [JsonPropertyName("v")] public int Version { get; set; } = 1;
    [JsonPropertyName("lodestone")] public ulong LodestoneId { get; set; }
    [JsonPropertyName("world")] public string World { get; set; } = string.Empty;
    [JsonPropertyName("fcName")] public string FcName { get; set; } = string.Empty;
    [JsonPropertyName("starterRank")] public string StarterRank { get; set; } = string.Empty;
    [JsonPropertyName("firstScan")] public DateTime FirstScanUtc { get; set; }
    [JsonPropertyName("lastScan")] public DateTime LastScanUtc { get; set; }
    [JsonPropertyName("members")] public Dictionary<string, RosterMember> Members { get; set; } = [];

    public static string FileName(ulong lodestoneId) => $"roster-{lodestoneId}.json";
}

/// <summary>What changed between two scans, plus who is in the starter rank now.</summary>
public sealed record RosterDiff(
    bool IsFirstScan,
    List<RosterMember> Joined,
    List<RosterMember> Left,
    List<(string OldName, string NewName)> Renamed,
    List<RosterMember> Starters);

public static class RosterUpdater
{
    /// <summary>
    /// Folds a complete Lodestone list into <paramref name="previous"/> (null on the first scan)
    /// and returns the new state and what changed.
    /// </summary>
    public static (RosterState State, RosterDiff Diff) Apply(
        RosterState? previous, FcBranch branch, IReadOnlyList<LodestoneMember> members, DateTime nowUtc)
    {
        var starterRank = branch.StarterRank.Trim();
        var isFirst = previous is null;

        // A different starter rank means the old "since" dates measure something else.
        var starterRankChanged = previous is not null
                                 && !string.Equals(previous.StarterRank, starterRank, StringComparison.OrdinalIgnoreCase);

        var state = new RosterState
        {
            LodestoneId = branch.LodestoneId,
            World = branch.World.Trim(),
            FcName = branch.FcName.Trim(),
            StarterRank = starterRank,
            FirstScanUtc = previous?.FirstScanUtc ?? nowUtc,
            LastScanUtc = nowUtc,
        };

        var joined = new List<RosterMember>();
        var renamed = new List<(string OldName, string NewName)>();

        foreach (var m in members)
        {
            var key = m.Id.ToString();
            var inStarter = string.Equals(m.Rank, starterRank, StringComparison.OrdinalIgnoreCase);
            var old = previous?.Members.GetValueOrDefault(key);

            var member = new RosterMember
            {
                Name = m.Name,
                Rank = m.Rank,
                JoinedUtc = old is null ? (isFirst ? null : nowUtc) : old.JoinedUtc,
            };

            if (inStarter)
            {
                var wasStarter = old is not null
                                 && string.Equals(old.Rank, previous!.StarterRank, StringComparison.OrdinalIgnoreCase);

                member.StarterSinceUtc = isFirst || starterRankChanged
                    ? null
                    : wasStarter ? old!.StarterSinceUtc : nowUtc;
            }

            if (old is null && !isFirst)
                joined.Add(member);

            // Same Lodestone ID, different name: a rename, not a leave + join.
            if (old is not null && !string.Equals(old.Name, member.Name, StringComparison.Ordinal))
                renamed.Add((old.Name, member.Name));

            state.Members[key] = member;
        }

        var left = previous is null
            ? []
            : previous.Members.Where(kv => !state.Members.ContainsKey(kv.Key)).Select(kv => kv.Value).ToList();

        var starters = state.Members.Values
            .Where(m => string.Equals(m.Rank, starterRank, StringComparison.OrdinalIgnoreCase))
            .OrderBy(m => m.StarterSinceUtc ?? DateTime.MinValue) // longest first; "before" is longest
            .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        renamed.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.NewName, b.NewName));

        return (state, new RosterDiff(isFirst, joined, left, renamed, starters));
    }

    /// <summary>
    /// The report, split into Discord-sized messages. Names and ranks are Free Company text, so
    /// they are escaped; nothing in it may ping anyone.
    /// </summary>
    public static List<string> ComposeReport(RosterState state, RosterDiff diff, string requestedBy, DateTime nowUtc)
    {
        var lines = new List<string>
        {
            $"{ReportTitle(state)} · {state.Members.Count} members · scanned <t:{Unix(nowUtc)}:f> by {requestedBy}",
        };

        var before = $"before <t:{Unix(state.FirstScanUtc)}:d>";

        if (diff.IsFirstScan)
        {
            lines.Add($"First scan: every current member is recorded. Joins and leaves are reported from the next scan on; "
                      + $"time in {Escape(state.StarterRank)} counts from today, for everyone already in it too.");
        }
        else
        {
            lines.Add(string.Empty);
            lines.Add($"**Joined ({diff.Joined.Count})**");
            lines.AddRange(diff.Joined.Count == 0 ? ["—"] : diff.Joined.Select(m => $"• {Escape(m.Name)}"));

            lines.Add(string.Empty);
            lines.Add($"**Left ({diff.Left.Count})**");
            lines.AddRange(diff.Left.Count == 0 ? ["—"] : diff.Left.Select(m => $"• {Escape(m.Name)}"));

            lines.Add(string.Empty);
            lines.Add($"**Renamed ({diff.Renamed.Count})**");
            lines.AddRange(diff.Renamed.Count == 0
                ? ["—"]
                : diff.Renamed.Select(r => $"• {Escape(r.OldName)} → {Escape(r.NewName)}"));
        }

        var due = DueForPromotion(state, diff, nowUtc);

        lines.Add(string.Empty);
        lines.Add($"**{Escape(state.StarterRank)} for {PromotionDays}+ days ({due.Count} of {diff.Starters.Count})**");
        lines.AddRange(due.Count == 0
            ? ["—"]
            : due.Select(m => m.StarterSinceUtc is { } since
                ? $"• {Escape(m.Name)} — {Days(nowUtc - since)} (since <t:{Unix(since)}:d>)"
                // Already in the rank on the first scan: counted from that scan, so "at least".
                : $"• {Escape(m.Name)} — {Days(nowUtc - state.FirstScanUtc)}+ ({before})"));

        return Chunk(lines);
    }

    /// <summary>How long a member stays in the starter rank before the report lists them.</summary>
    public const int PromotionDays = 30;

    /// <summary>
    /// Starter-rank members in it for <see cref="PromotionDays"/> or more, longest first. Members
    /// already in the rank on the first scan count from that scan.
    /// </summary>
    public static List<RosterMember> DueForPromotion(RosterState state, RosterDiff diff, DateTime nowUtc) =>
        diff.Starters
            .Where(m => (nowUtc - (m.StarterSinceUtc ?? state.FirstScanUtc)).TotalDays >= PromotionDays)
            .ToList();

    /// <summary>
    /// First words of a Free Company's report. Also how the scanner finds that report again to
    /// edit it, so it must stay stable between scans.
    /// </summary>
    public static string ReportTitle(RosterState state) =>
        $"📋 **Roster — {Escape(state.FcName)} @ {Escape(state.World)}**";

    /// <summary>First words of the "report updated" notice posted under the reports; stable like <see cref="ReportTitle"/>.</summary>
    public static string NoticeTitle(RosterState state) =>
        $"🔄 **Roster updated — {Escape(state.FcName)} @ {Escape(state.World)}**";

    /// <summary>The short line posted after each scan, pointing at the (edited) report.</summary>
    public static string ComposeNotice(RosterState state, RosterDiff diff, string reportUrl, DateTime nowUtc) =>
        $"{NoticeTitle(state)} · scan of <t:{Unix(nowUtc)}:f> · "
        + (diff.IsFirstScan
            ? $"first scan, {state.Members.Count} members recorded"
            : $"{diff.Joined.Count} joined, {diff.Left.Count} left, {diff.Renamed.Count} renamed")
        + $" · [see the report]({reportUrl})";

    private static string Days(TimeSpan span)
    {
        var days = Math.Max(0, (int)span.TotalDays);
        return days == 1 ? "1 day" : $"{days} days";
    }

    private static long Unix(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();

    private static string Escape(string text) => MessageFormatter.NeutraliseMassMentions(MessageFormatter.EscapeMarkdown(text));

    /// <summary>Joins lines into messages under Discord's limit, never splitting a line.</summary>
    private static List<string> Chunk(List<string> lines)
    {
        const int limit = MessageFormatter.MaxLength - 50;
        var messages = new List<string>();
        var current = new StringBuilder();

        foreach (var line in lines)
        {
            if (current.Length > 0 && current.Length + line.Length + 1 > limit)
            {
                messages.Add(current.ToString());
                current.Clear();
            }

            if (current.Length > 0)
                current.Append('\n');
            current.Append(line.Length > limit ? line[..limit] : line);
        }

        if (current.Length > 0)
            messages.Add(current.ToString());

        return messages.Where(m => m.Trim().Length > 0).ToList();
    }
}
