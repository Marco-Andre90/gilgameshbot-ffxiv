using Discord;

namespace GilgameshBot.Weekly;

/// <summary>The Screenshot of the Week's picture as it is attached to the weekly.</summary>
/// <param name="Name">File name: <c>screenshot</c> plus the original's extension.</param>
public sealed record WeeklyScreenshotFile(string Name, byte[] Bytes);

/// <summary>The screenshot picked for a weekly: the post it came from and its picture.</summary>
public sealed record WeeklyScreenshotPick(IMessage Post, int Reactions, WeeklyScreenshotFile File);

/// <summary>
/// Picks the Screenshot of the Week: the members' picture with the most reactions in the
/// screenshot channel over the week, read over REST. It is picked once, for the preview; the
/// preview and the posted weekly then carry it as an attachment, which approving and the hourly
/// refresh read back, so what the FC leader approved is what goes out.
/// </summary>
internal static class WeeklyScreenshot
{
    public const string FilePrefix = "screenshot.";

    /// <summary>How far back the channel is read, in pages of 100 messages.</summary>
    private const int MaxPages = 10;

    /// <summary>Larger pictures are passed over: they go out with the header and thumbnails in one upload.</summary>
    private const int MaxBytes = 8 * 1024 * 1024;

    /// <summary>Candidates tried, best first, when a download fails.</summary>
    private const int MaxTries = 3;

    private static readonly string[] Extensions = [".png", ".jpg", ".jpeg", ".webp", ".gif"];

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>
    /// The members' picture posted in <paramref name="channel"/> since <paramref name="from"/> with
    /// the most reactions (the bot's own left out; ties go to the earliest), downloaded. Null when
    /// no picture has a reaction.
    /// </summary>
    public static async Task<WeeklyScreenshotPick?> PickAsync(ITextChannel channel, DateTimeOffset from, RequestOptions options, CancellationToken ct)
    {
        var candidates = new List<(IMessage Post, IAttachment Picture, int Reactions)>();
        IMessage? oldest = null;

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
                var reactions = m.Reactions.Values.Sum(r => r.ReactionCount - (r.IsMe ? 1 : 0));
                if (reactions > 0 && m.Attachments.FirstOrDefault(IsPicture) is { } picture)
                    candidates.Add((m, picture, reactions));
            }

            if (messages.Count < 100)
                break;

            oldest = messages.MinBy(m => m.Id)!;
            if (oldest.Timestamp < from)
                break;
        }

        foreach (var (post, picture, reactions) in candidates.OrderByDescending(c => c.Reactions).ThenBy(c => c.Post.Id).Take(MaxTries))
        {
            try
            {
                var bytes = await DownloadAsync(picture.Url, ct);
                return new WeeklyScreenshotPick(post, reactions, new WeeklyScreenshotFile(FilePrefix + Extension(picture.Filename)[1..], bytes));
            }
            catch (HttpRequestException)
            {
                // Gone meanwhile, or the CDN refused it: the next best.
            }
        }

        return null;
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

    /// <summary>A picture a member posted openly: an image type, not a spoiler, small enough to re-upload.</summary>
    private static bool IsPicture(IAttachment a) =>
        a.Size <= MaxBytes
        && !a.Filename.StartsWith("SPOILER_", StringComparison.Ordinal)
        && Extensions.Contains(Extension(a.Filename))
        && (a.ContentType is null || a.ContentType.StartsWith("image/", StringComparison.Ordinal));

    private static string Extension(string fileName) => Path.GetExtension(fileName).ToLowerInvariant();
}
