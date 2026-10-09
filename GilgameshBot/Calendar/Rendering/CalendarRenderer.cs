using System.Globalization;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Memory;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using ImageSharpConfiguration = SixLabors.ImageSharp.Configuration;

namespace GilgameshBot.Calendar.Rendering;

/// <summary>
/// Draws a <see cref="CalendarPage"/> as a PNG in one of the <see cref="CalendarThemes"/>.
/// </summary>
/// <remarks>
/// <para>
/// Everything is drawn in code: no game assets. Every random touch (paper stains, uneven edges,
/// noise) comes from a generator seeded by the month and theme, so two plugins drawing the same
/// month produce the same picture.
/// </para>
/// <para>
/// One image at a time, with a small private memory pool that is handed back after each render:
/// the plugin lives inside the game, and an image is only drawn when the calendar changes or a
/// member asks for one.
/// </para>
/// </remarks>
public static class CalendarRenderer
{
    /// <summary>Layout units are multiplied by this; 680 units wide become 1020 pixels.</summary>
    private const float Scale = 1.5f;

    /// <summary>One render at a time, for every picture the plugin draws (the calendar and the weekly header).</summary>
    internal static readonly object Gate = new();

    /// <summary>The small private memory pool every render uses; released after each one.</summary>
    internal static readonly ImageSharpConfiguration ImageConfig = CreateConfig();

    private static ImageSharpConfiguration CreateConfig()
    {
        var config = ImageSharpConfiguration.Default.Clone();
        config.MemoryAllocator = MemoryAllocator.Create(new MemoryAllocatorOptions { MaximumPoolSizeMegabytes = 8 });
        config.MaxDegreeOfParallelism = 1;
        return config;
    }

    /// <summary>
    /// Draws <paramref name="page"/> in theme <paramref name="themeKey"/> (resolved: not <c>seasonal</c>).
    /// Flat themes come out as PNG; grainy ones as JPEG, which keeps them around a fifth of the size.
    /// </summary>
    public static CalendarImage Render(CalendarPage page, string themeKey)
    {
        lock (Gate)
        {
            try
            {
                using var image = new Image<Rgba32>(ImageConfig, Px(Layout.Width), Px(Layout.Height(page)));
                // Not HashCode / string.GetHashCode: those change with every process.
                var themeIndex = CalendarThemes.All.ToList().FindIndex(t => t.Key == themeKey);
                var seed = (page.Year * 12 + page.Month) * 31 + themeIndex + 1;

                var textured = themeKey switch
                {
                    CalendarThemes.Wow => TexturedStyle.Wow,
                    CalendarThemes.Halloween => TexturedStyle.Halloween,
                    _ => null,
                };

                if (textured is not null)
                    TexturedTheme.Draw(image, page, textured, Scale, seed);
                else
                    image.Mutate(ctx => CleanTheme.Draw(new Canvas(ctx, Scale), page));

                using var stream = new MemoryStream();
                if (textured is not null)
                {
                    image.SaveAsJpeg(stream, new JpegEncoder { Quality = 90 });
                    return new CalendarImage(stream.ToArray(), "calendar.jpg");
                }

                image.SaveAsPng(stream, new PngEncoder { CompressionLevel = PngCompressionLevel.BestCompression });
                return new CalendarImage(stream.ToArray(), "calendar.png");
            }
            finally
            {
                ImageConfig.MemoryAllocator.ReleaseRetainedResources();
            }
        }
    }

    private static int Px(float units) => (int)MathF.Round(units * Scale);

    /// <summary>"October", in English like the rest of the bot.</summary>
    internal static string MonthName(int month) =>
        CultureInfo.InvariantCulture.DateTimeFormat.GetMonthName(month);

    internal static string DayName(DayOfWeek day, bool full) =>
        full ? CultureInfo.InvariantCulture.DateTimeFormat.GetDayName(day)
             : CultureInfo.InvariantCulture.DateTimeFormat.GetAbbreviatedDayName(day);
}

/// <summary>An encoded calendar image and the file name it is attached under.</summary>
public sealed record CalendarImage(byte[] Bytes, string FileName);

/// <summary>The grid's geometry in layout units, shared by every theme.</summary>
internal static class Layout
{
    public const float Width = 680;
    public const float GridX = 10;
    public const float HeaderTop = 58;
    public const float HeaderHeight = 22;
    public const float Gap = 3;
    /// <summary>
    /// Low cells: Discord shows a picture at most about 550 × 350 pixels in the chat, so the wider
    /// it is against its height, the larger it appears. Room for two lines of events.
    /// </summary>
    public const float CellHeight = 64;

    public const float CellWidth = (Width - 2 * GridX - 6 * Gap) / 7;
    public const float CellsTop = HeaderTop + HeaderHeight + Gap;
    public static float Height(CalendarPage page) => CellsTop + page.Weeks * CellHeight + (page.Weeks - 1) * Gap + 10;

    public static float ColumnX(int column) => GridX + column * (CellWidth + Gap);

    public static float RowY(int row) => CellsTop + row * (CellHeight + Gap);

    /// <summary>Every cell of the grid: position, date, and whether it belongs to the page's month.</summary>
    public static IEnumerable<(float X, float Y, DateOnly Date, bool InMonth)> Cells(CalendarPage page)
    {
        var start = page.GridStart;
        for (var i = 0; i < page.Weeks * 7; i++)
        {
            var date = start.AddDays(i);
            yield return (ColumnX(i % 7), RowY(i / 7), date, date.Month == page.Month && date.Year == page.Year);
        }
    }

    /// <summary>
    /// The titles to show in a cell holding <paramref name="titles"/>: at most <paramref name="slots"/>
    /// lines, the last one turned into "+N more" when they do not all fit.
    /// </summary>
    public static List<string> Lines(IReadOnlyList<string> titles, int slots)
    {
        var cleaned = titles.Select(CalendarFonts.CleanForImage).Select(t => t.Length > 0 ? t : "Event").ToList();
        if (cleaned.Count <= slots)
            return cleaned;

        var shown = cleaned.Take(slots - 1).ToList();
        shown.Add($"+{cleaned.Count - shown.Count} more");
        return shown;
    }
}
