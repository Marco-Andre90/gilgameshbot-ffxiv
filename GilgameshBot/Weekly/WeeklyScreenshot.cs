using Dalamud.Plugin.Services;
using Discord;

namespace GilgameshBot.Weekly;

/// <summary>The Screenshot of the Week's picture as it is attached to the weekly.</summary>
/// <param name="Name">File name: <c>screenshot</c> plus the original's extension.</param>
public sealed record WeeklyScreenshotFile(string Name, byte[] Bytes);

/// <summary>One picture of the screenshot channel that may be the Screenshot of the Week.</summary>
/// <param name="Index">Which of the post's attachments it is.</param>
/// <param name="Pictures">How many pictures the post has, for "image 2 of 3".</param>
public sealed record WeeklyScreenshotCandidate(IMessage Post, int Index, int Pictures, IAttachment Picture, int Reactions)
{
    /// <summary>The menu value naming it: "&lt;message id&gt;:&lt;attachment index&gt;".</summary>
    public string Value => $"{Post.Id}:{Index}";
}

/// <summary>The screenshot picked for a weekly: the picture and its file.</summary>
public sealed record WeeklyScreenshotPick(WeeklyScreenshotCandidate Candidate, WeeklyScreenshotFile File);

/// <summary>
/// The Screenshot of the Week: the members' pictures in the screenshot channel over the week,
/// read over REST, the one with the most reactions picked for the preview. The preview's menu
/// offers the others; the preview and the posted weekly carry the chosen one as an attachment,
/// which approving and the hourly refresh read back, so what the FC leader approved is what goes out.
/// </summary>
internal static class WeeklyScreenshot
{
    public const string FilePrefix = "screenshot.";

    /// <summary>The menu value for no Screenshot of the Week.</summary>
    public const string NoneValue = "none";

    /// <summary>How far back the channel is read, in pages of 100 messages.</summary>
    private const int MaxPages = 10;

    /// <summary>Larger pictures are passed over: they go out with the header and thumbnails in one upload.</summary>
    private const int MaxBytes = 8 * 1024 * 1024;

    /// <summary>Candidates tried, best first, when a download fails.</summary>
    private const int MaxTries = 3;

    private static readonly string[] Extensions = [".png", ".jpg", ".jpeg", ".webp", ".gif"];

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>
    /// Every picture members posted in <paramref name="channel"/> since <paramref name="from"/> on
    /// a post with reactions (the bot's own left out), best first: most reactions, then the
    /// earliest post, then the post's own order.
    /// </summary>
    public static async Task<List<WeeklyScreenshotCandidate>> CandidatesAsync(
        ITextChannel channel, DateTimeOffset from, IPluginLog log, RequestOptions options)
    {
        var candidates = new List<WeeklyScreenshotCandidate>();
        IMessage? oldest = null;
        int posts = 0, withFiles = 0, withPictures = 0;

        // Through IMessageChannel so the (empty) message cache is never preferred.
        var source = (IMessageChannel)channel;
        for (var page = 0; page < MaxPages; page++)
        {
            var messages = (oldest is null
                    ? await source.GetMessagesAsync(100, CacheMode.AllowDownload, options).FlattenAsync()
                    : await source.GetMessagesAsync(oldest.Id, Direction.Before, 100, CacheMode.AllowDownload, options).FlattenAsync())
                .ToList();

            foreach (var m in messages.Where(m => m.Timestamp >= from && !m.Author.IsBot && !m.Author.IsWebhook))
            {
                posts++;
                withFiles += m.Attachments.Count > 0 ? 1 : 0;
                var pictures = Pictures(m);
                if (pictures.Count == 0)
                    continue;

                withPictures++;
                var reactions = Reactions(m);
                if (reactions > 0)
                    candidates.AddRange(pictures.Select(p => new WeeklyScreenshotCandidate(m, p.Index, pictures.Count, p.Picture, reactions)));
            }

            if (messages.Count < 100)
                break;

            oldest = messages.MinBy(m => m.Id)!;
            if (oldest.Timestamp < from)
                break;
        }

        // Counted, so an empty section can be explained from /xllog.
        log.Information("Screenshot of the Week: in #{Channel} since {From:u}, {Posts} members' posts, {Files} with files, "
                        + "{Pictures} with a usable picture, {Candidates} pictures on posts with reactions.",
            channel.Name, from, posts, withFiles, withPictures, candidates.Count);

        return candidates
            .OrderByDescending(c => c.Reactions)
            .ThenBy(c => c.Post.Id)
            .ThenBy(c => c.Index)
            .ToList();
    }

    /// <summary>The first of <paramref name="candidates"/> whose picture downloads, trying a few posts; null when none does.</summary>
    public static async Task<WeeklyScreenshotPick?> PickAsync(IReadOnlyList<WeeklyScreenshotCandidate> candidates, IPluginLog log, CancellationToken ct)
    {
        // The best picture of each post: a post whose first picture fails is passed over whole.
        foreach (var candidate in candidates.DistinctBy(c => c.Post.Id).Take(MaxTries))
        {
            try
            {
                return new WeeklyScreenshotPick(candidate, await DownloadAsync(candidate, ct));
            }
            catch (HttpRequestException ex)
            {
                // Gone meanwhile, or the CDN refused it: the next best.
                log.Warning("Screenshot of the Week: could not download the picture of message {Message}: {Error}", candidate.Post.Id, ex.Message);
            }
        }

        return null;
    }

    /// <summary>
    /// The picture a preview's menu names (<see cref="WeeklyScreenshotCandidate.Value"/>), read again
    /// from <paramref name="channel"/> with its reactions as they are now; null for
    /// <see cref="NoneValue"/>. Throws when the post or its picture is gone.
    /// </summary>
    public static async Task<WeeklyScreenshotPick?> ChooseAsync(ITextChannel channel, string value, RequestOptions options, CancellationToken ct)
    {
        if (value == NoneValue)
            return null;

        var parts = value.Split(':');
        if (parts.Length != 2 || !ulong.TryParse(parts[0], out var messageId) || !int.TryParse(parts[1], out var index))
            throw new InvalidOperationException("Unknown screenshot choice.");

        var post = await ((IMessageChannel)channel).GetMessageAsync(messageId, CacheMode.AllowDownload, options)
                   ?? throw new InvalidOperationException("That screenshot's post was deleted.");

        var pictures = Pictures(post);
        var picture = pictures.FirstOrDefault(p => p.Index == index).Picture
                      ?? throw new InvalidOperationException("That picture is no longer on its post.");

        var candidate = new WeeklyScreenshotCandidate(post, index, pictures.Count, picture, Reactions(post));
        return new WeeklyScreenshotPick(candidate, await DownloadAsync(candidate, ct));
    }

    /// <summary>
    /// The picture a preview or a posted weekly carries for its Screenshot of the Week; null when it
    /// has none. Throws when the embed is there but its picture cannot be read back, so the weekly is
    /// never rewritten without it.
    /// </summary>
    public static async Task<WeeklyScreenshotFile?> ReadBackAsync(IMessage message, CancellationToken ct)
    {
        if (!WeeklyMessage.HasScreenshot(message))
            return null;

        var picture = message.Attachments.FirstOrDefault(a => a.Filename.StartsWith(FilePrefix, StringComparison.Ordinal))
                      ?? throw new InvalidOperationException("The Screenshot of the Week's picture is missing from the weekly.");

        return new WeeklyScreenshotFile(picture.Filename, await DownloadAsync(picture.Url, ct));
    }

    private static async Task<WeeklyScreenshotFile> DownloadAsync(WeeklyScreenshotCandidate candidate, CancellationToken ct) =>
        new(FilePrefix + Extension(candidate.Picture.Filename)[1..], await DownloadAsync(candidate.Picture.Url, ct));

    /// <summary>
    /// A picture from Discord's CDN. A timeout is an <see cref="HttpRequestException"/> here, not the
    /// <see cref="TaskCanceledException"/> HttpClient throws, which callers would take for a disconnect.
    /// </summary>
    private static async Task<byte[]> DownloadAsync(string url, CancellationToken ct)
    {
        try
        {
            return await Http.GetByteArrayAsync(url, ct);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new HttpRequestException("Downloading the screenshot timed out.", ex);
        }
    }

    /// <summary>A post's usable pictures, with their place among its attachments.</summary>
    private static List<(int Index, IAttachment Picture)> Pictures(IMessage post) =>
        post.Attachments.Select((a, i) => (Index: i, Picture: (IAttachment)a)).Where(p => IsPicture(p.Picture)).ToList();

    /// <summary>A post's reactions, the bot's own left out.</summary>
    private static int Reactions(IMessage post) =>
        post.Reactions.Values.Sum(r => r.ReactionCount - (r.IsMe ? 1 : 0));

    /// <summary>A picture a member posted openly: an image type, not a spoiler, small enough to re-upload.</summary>
    private static bool IsPicture(IAttachment a) =>
        a.Size <= MaxBytes
        && !a.Filename.StartsWith("SPOILER_", StringComparison.Ordinal)
        && Extensions.Contains(Extension(a.Filename))
        && (a.ContentType is null || a.ContentType.StartsWith("image/", StringComparison.Ordinal));

    private static string Extension(string fileName) => Path.GetExtension(fileName).ToLowerInvariant();
}
