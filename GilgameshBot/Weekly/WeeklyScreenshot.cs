using Dalamud.Plugin.Services;
using Discord;

namespace GilgameshBot.Weekly;

/// <summary>One of the Screenshot of the Week's pictures as it is attached to the weekly.</summary>
/// <param name="Name">File name: <c>screenshot</c> (<c>screenshot-2</c>, <c>screenshot-3</c> for the others) plus the original's extension.</param>
public sealed record WeeklyScreenshotFile(string Name, byte[] Bytes);

/// <summary>One picture of the screenshot channel that may be in the Screenshot of the Week.</summary>
/// <param name="Index">Which of the post's attachments it is.</param>
/// <param name="Pictures">How many pictures the post has, for "image 2 of 3".</param>
public sealed record WeeklyScreenshotCandidate(IMessage Post, int Index, int Pictures, IAttachment Picture, int Reactions)
{
    /// <summary>The menu value naming it: "&lt;message id&gt;:&lt;attachment index&gt;".</summary>
    public string Value => $"{Post.Id}:{Index}";
}

/// <summary>A screenshot picked for a weekly: the picture and its file.</summary>
public sealed record WeeklyScreenshotPick(WeeklyScreenshotCandidate Candidate, WeeklyScreenshotFile File);

/// <summary>
/// The Screenshot of the Week: the members' pictures in the screenshot channel over the week,
/// read over REST. The preview gets the post with the most reactions, and the posts tied with it
/// up to <see cref="MaxPictures"/>; its menu picks others. The preview and the posted weekly carry
/// the chosen ones as attachments, which approving and the hourly refresh read back, so what the
/// FC leader approved is what goes out.
/// </summary>
internal static class WeeklyScreenshot
{
    /// <summary>Pictures in the section at most: Discord shows them as one gallery.</summary>
    public const int MaxPictures = 3;

    /// <summary>The menu value for no Screenshot of the Week.</summary>
    public const string NoneValue = "none";

    private const string FileStem = "screenshot";

    /// <summary>How far back the channel is read, in pages of 100 messages.</summary>
    private const int MaxPages = 10;

    /// <summary>Larger pictures are passed over: they go out with the header and thumbnails in one upload.</summary>
    private const int MaxBytes = 8 * 1024 * 1024;

    /// <summary>Kept free of the server's upload limit for the header and thumbnails.</summary>
    private const long ReservedBytes = 2 * 1024 * 1024;

    /// <summary>Posts tried, best first, while no picture has downloaded.</summary>
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

        return Ranked(candidates);
    }

    /// <summary>
    /// The pictures for a new preview: the best post's, and those of the posts tied with it, one
    /// picture per post, up to <see cref="MaxPictures"/>. A picture that does not download is passed
    /// over (a few posts are tried while none has), and so is one that would take the upload past
    /// <paramref name="maxUpload"/>; tied posts are never made up for with lesser ones. Empty when none does.
    /// </summary>
    public static async Task<List<WeeklyScreenshotPick>> PickAsync(
        IReadOnlyList<WeeklyScreenshotCandidate> candidates, ulong maxUpload, IPluginLog log, CancellationToken ct)
    {
        var picks = new List<WeeklyScreenshotPick>();
        var budget = Budget(maxUpload);
        var tries = 0;

        // The best picture of each post: a post whose first picture fails is passed over whole.
        foreach (var candidate in candidates.DistinctBy(c => c.Post.Id))
        {
            if (picks.Count == MaxPictures
                || (picks.Count > 0 && candidate.Reactions < picks[0].Candidate.Reactions)
                || (picks.Count == 0 && tries == MaxTries))
                break;

            if (candidate.Picture.Size > budget)
                continue;

            tries++;
            try
            {
                var pick = new WeeklyScreenshotPick(candidate, await DownloadAsync(candidate, picks.Count, ct));
                picks.Add(pick);
                budget -= pick.File.Bytes.Length;
            }
            catch (HttpRequestException ex)
            {
                // Gone meanwhile, or the CDN refused it: the next one.
                log.Warning("Screenshot of the Week: could not download the picture of message {Message}: {Error}", candidate.Post.Id, ex.Message);
            }
        }

        return picks;
    }

    /// <summary>
    /// The pictures a preview's menu names (<see cref="WeeklyScreenshotCandidate.Value"/>), read
    /// again from <paramref name="channel"/> with their reactions as they are now, best first;
    /// empty when <see cref="NoneValue"/> is among them. Throws when a post or its picture is gone,
    /// or when together they would take the upload past <paramref name="maxUpload"/>.
    /// </summary>
    public static async Task<List<WeeklyScreenshotPick>> ChooseAsync(
        ITextChannel channel, IReadOnlyCollection<string> values, ulong maxUpload, RequestOptions options, CancellationToken ct)
    {
        if (values.Count == 0 || values.Contains(NoneValue))
            return [];

        var posts = new Dictionary<ulong, IMessage>();
        var chosen = new List<WeeklyScreenshotCandidate>();
        foreach (var value in values.Distinct().Take(MaxPictures))
        {
            var parts = value.Split(':');
            if (parts.Length != 2 || !ulong.TryParse(parts[0], out var messageId) || !int.TryParse(parts[1], out var index))
                throw new InvalidOperationException("Unknown screenshot choice.");

            if (!posts.TryGetValue(messageId, out var post))
                posts[messageId] = post = await ((IMessageChannel)channel).GetMessageAsync(messageId, CacheMode.AllowDownload, options)
                                          ?? throw new InvalidOperationException("A chosen screenshot's post was deleted.");

            var pictures = Pictures(post);
            var picture = pictures.FirstOrDefault(p => p.Index == index).Picture
                          ?? throw new InvalidOperationException("A chosen picture is no longer on its post.");

            chosen.Add(new WeeklyScreenshotCandidate(post, index, pictures.Count, picture, Reactions(post)));
        }

        // Discord sends the values in the menu's order, not the clicks', and reactions may have changed since.
        chosen = Ranked(chosen);
        if (chosen.Sum(c => (long)c.Picture.Size) > Budget(maxUpload))
            throw new InvalidOperationException("Those pictures are too large to go out together.");

        var picks = new List<WeeklyScreenshotPick>();
        foreach (var candidate in chosen)
            picks.Add(new WeeklyScreenshotPick(candidate, await DownloadAsync(candidate, picks.Count, ct)));

        return picks;
    }

    /// <summary>
    /// The pictures a preview or a posted weekly carries for its Screenshot of the Week, in order;
    /// empty when it has none. Throws when the embed is there but its pictures cannot be read back,
    /// so the weekly is never rewritten without them.
    /// </summary>
    public static async Task<List<WeeklyScreenshotFile>> ReadBackAsync(IMessage message, CancellationToken ct)
    {
        if (!WeeklyMessage.HasScreenshot(message))
            return [];

        var pictures = Attached(message);
        if (pictures.Count == 0)
            throw new InvalidOperationException("The Screenshot of the Week's picture is missing from the weekly.");

        var files = new List<WeeklyScreenshotFile>();
        foreach (var (name, url) in pictures)
            files.Add(new WeeklyScreenshotFile(name, await DownloadAsync(url, ct)));

        return files;
    }

    /// <summary>
    /// The Screenshot of the Week's pictures on a preview or a posted weekly, in order: file name and
    /// CDN url. Discord lists a file shown in an embed only as that embed's image, not among the
    /// message's attachments, so both are looked at.
    /// </summary>
    public static List<(string Name, string Url)> Attached(IMessage message) =>
        message.Embeds.Select(e => e.Image?.Url)
            .OfType<string>()
            .Select(url => (Name: Uri.TryCreate(url, UriKind.Absolute, out var uri) ? Path.GetFileName(uri.AbsolutePath) : "", Url: url))
            .Concat(message.Attachments.Select(a => (Name: a.Filename, a.Url)))
            .Select(p => (p.Name, p.Url, Order: FileOrder(p.Name)))
            .Where(p => p.Order >= 0)
            .DistinctBy(p => p.Order)
            .OrderBy(p => p.Order)
            .Select(p => (p.Name, p.Url))
            .ToList();

    private static List<WeeklyScreenshotCandidate> Ranked(IEnumerable<WeeklyScreenshotCandidate> candidates) =>
        candidates
            .OrderByDescending(c => c.Reactions)
            .ThenBy(c => c.Post.Id)
            .ThenBy(c => c.Index)
            .ToList();

    /// <summary>The bytes the pictures may take of one message's upload.</summary>
    private static long Budget(ulong maxUpload) => Math.Max(0, (long)Math.Min(maxUpload, (ulong)long.MaxValue) - ReservedBytes);

    /// <summary>The file name of the picture at <paramref name="order"/>: the first is named as it always was.</summary>
    private static string FileName(int order, string extension) =>
        (order == 0 ? FileStem : $"{FileStem}-{order + 1}") + extension;

    /// <summary>0 for <c>screenshot.*</c>, 1 for <c>screenshot-2.*</c> and so on; -1 for any other file.</summary>
    private static int FileOrder(string fileName)
    {
        if (!fileName.StartsWith(FileStem, StringComparison.Ordinal))
            return -1;

        var rest = fileName[FileStem.Length..];
        if (rest.StartsWith('.'))
            return 0;

        return rest is ['-', >= '2' and <= '9', '.', ..] ? rest[1] - '1' : -1;
    }

    private static async Task<WeeklyScreenshotFile> DownloadAsync(WeeklyScreenshotCandidate candidate, int order, CancellationToken ct) =>
        new(FileName(order, Extension(candidate.Picture.Filename)), await DownloadAsync(candidate.Picture.Url, ct));

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
