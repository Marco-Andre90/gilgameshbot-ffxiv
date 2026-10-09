using System.Globalization;
using GilgameshBot.Calendar.Rendering;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace GilgameshBot.Weekly;

/// <summary>
/// Draws the weekly's header: the issue number and the week's dates on the top line of the
/// template, which carries everything else (title, Free Company name, the cat).
/// </summary>
/// <remarks>
/// <para>
/// Follows the design's spec, written for 1200 × 465 and drawn here at twice that size: Libre
/// Franklin SemiBold, 15 px, 0.12 em of tracking after every glyph, colour #1C1A17, baseline at
/// y = 79. The issue number starts at x = 48; the dates end at x = 772, trailing tracking included
/// (as a browser's <c>text-anchor: end</c> places them). Each may take 250 px; a longer text
/// shrinks in 0.5 px steps, down to 12 px.
/// </para>
/// <para>
/// SixLabors.Fonts has no tracking of its own, so the glyphs are placed one by one at the kerned
/// positions of the whole line plus the tracking.
/// </para>
/// </remarks>
internal static class WeeklyHeader
{
    public const string FileName = "header.png";

    /// <summary>The template is the design at twice its size: every spec value is doubled.</summary>
    private const float Scale = 2f;

    private const float FontSize = 15f;
    private const float MinFontSize = 12f;
    private const float TrackingEm = 0.12f;
    private const float Baseline = 79f;
    private const float IssueLeft = 48f;
    private const float DatesRight = 772f;
    private const float MaxWidth = 250f;

    private static readonly Color Ink = Color.ParseHex("1C1A17");

    private static readonly string[] Months = ["JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC"];

    /// <summary>The header for issue <paramref name="issue"/>, the week from <paramref name="first"/> to <paramref name="last"/>, as PNG.</summary>
    public static byte[] Render(int issue, DateOnly first, DateOnly last)
    {
        lock (CalendarRenderer.Gate)
        {
            try
            {
                var decoder = new DecoderOptions { Configuration = CalendarRenderer.ImageConfig };
                using var image = Image.Load<Rgba32>(decoder, WeeklyAssets.Get(WeeklyAssets.HeaderTemplate));

                image.Mutate(ctx =>
                {
                    DrawTracked(ctx, IssueText(issue), IssueLeft, alignRight: false);
                    DrawTracked(ctx, DateRange(first, last), DatesRight, alignRight: true);
                });

                using var stream = new MemoryStream();
                image.SaveAsPng(stream, new PngEncoder { CompressionLevel = PngCompressionLevel.BestCompression });
                return stream.ToArray();
            }
            finally
            {
                CalendarRenderer.ImageConfig.MemoryAllocator.ReleaseRetainedResources();
            }
        }
    }

    /// <summary>"ISSUE NO. 12".</summary>
    public static string IssueText(int issue) => $"ISSUE NO. {issue.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>"OCT 12 – 18, 2026", "OCT 29 – NOV 4, 2026" or "DEC 28, 2026 – JAN 3, 2027".</summary>
    public static string DateRange(DateOnly first, DateOnly last)
    {
        string Day(DateOnly d) => $"{Months[d.Month - 1]} {d.Day.ToString(CultureInfo.InvariantCulture)}";

        if (first.Year != last.Year)
            return $"{Day(first)}, {first.Year} – {Day(last)}, {last.Year}";

        return first.Month == last.Month
            ? $"{Day(first)} – {last.Day.ToString(CultureInfo.InvariantCulture)}, {last.Year}"
            : $"{Day(first)} – {Day(last)}, {last.Year}";
    }

    /// <summary>
    /// Draws <paramref name="text"/> on the top line, starting at <paramref name="x"/> or, with
    /// <paramref name="alignRight"/>, ending there. Spec units; shrunk until it fits <see cref="MaxWidth"/>.
    /// </summary>
    private static void DrawTracked(IImageProcessingContext ctx, string text, float x, bool alignRight)
    {
        var size = FontSize;
        var (font, positions, width) = Layout(text, size);
        while (width > MaxWidth * Scale && size > MinFontSize)
        {
            size -= 0.5f;
            (font, positions, width) = Layout(text, size);
        }

        var left = alignRight ? x * Scale - width : x * Scale;

        // Origins are the top of the line box; the baseline sits a font-dependent distance below.
        var top = Baseline * Scale - BaselineOffset(font);

        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsWhiteSpace(text[i]))
                continue;

            ctx.DrawText(new RichTextOptions(font) { Origin = new PointF(left + positions[i], top) }, text[i].ToString(), Ink);
        }
    }

    /// <summary>
    /// The font at <paramref name="size"/> spec pixels, where each character of <paramref name="text"/>
    /// starts (kerned, plus tracking after every earlier glyph), and the line's width with the
    /// trailing tracking, all in image pixels.
    /// </summary>
    private static (Font Font, float[] Positions, float Width) Layout(string text, float size)
    {
        var font = CalendarFonts.Franklin.CreateFont(size * Scale);
        var tracking = TrackingEm * size * Scale;
        var options = new TextOptions(font) { KerningMode = KerningMode.Standard };

        var positions = new float[text.Length];
        if (!TextMeasurer.TryMeasureCharacterAdvances(text, options, out var advances) || advances.Length != text.Length)
        {
            // Not expected for this text (ASCII and an en dash); fall back to unkerned advances.
            var x = 0f;
            for (var i = 0; i < text.Length; i++)
            {
                positions[i] = x;
                x += TextMeasurer.MeasureAdvance(text[i].ToString(), options).Width + tracking;
            }

            return (font, positions, x);
        }

        // Each advance is the glyph's own width, kerning with its neighbour included; not its position.
        var at = 0f;
        for (var i = 0; i < text.Length; i++)
        {
            positions[i] = at;
            at += advances[i].Bounds.Width + tracking;
        }

        return (font, positions, at);
    }

    /// <summary>How far below a top-aligned origin the baseline of <paramref name="font"/> lies, in pixels.</summary>
    private static float BaselineOffset(Font font) =>
        TextMeasurer.MeasureBounds("H", new TextOptions(font)).Bottom;
}
