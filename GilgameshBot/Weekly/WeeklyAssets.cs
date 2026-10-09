using System.Collections.Concurrent;

namespace GilgameshBot.Weekly;

/// <summary>The Fat Cat Weekly's pictures, embedded in the plugin (<c>Weekly/Assets</c>), read once.</summary>
internal static class WeeklyAssets
{
    /// <summary>The header without its issue number and dates, 2400 × 930 (the design's 1200 × 465 at twice the size).</summary>
    public const string HeaderTemplate = "header-template.png";

    /// <summary>The webhook's avatar, 512 × 512.</summary>
    public const string Icon = "icon.png";

    /// <summary>Thumbnail of "The Fat Cat says:".</summary>
    public const string Sticker = "sticker.png";

    /// <summary>Thumbnail of "This week's events".</summary>
    public const string EventThumbnail = "event-thumbnail.png";

    private static readonly ConcurrentDictionary<string, byte[]> Cache = new();

    /// <summary>The bytes of <paramref name="name"/>; never modify them.</summary>
    public static byte[] Get(string name) =>
        Cache.GetOrAdd(name, file =>
        {
            using var stream = typeof(WeeklyAssets).Assembly.GetManifestResourceStream($"GilgameshBot.Weekly.{file}")
                               ?? throw new InvalidOperationException($"Embedded picture {file} is missing.");
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            return copy.ToArray();
        });
}
