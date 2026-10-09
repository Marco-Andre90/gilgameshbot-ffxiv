using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SixLabors.Fonts;
using SixLabors.Fonts.Unicode;

namespace GilgameshBot.Calendar.Rendering;

/// <summary>
/// The fonts embedded in the plugin (SIL Open Font License), loaded once, and the clean-up text
/// needs before it is drawn with them.
/// </summary>
internal static partial class CalendarFonts
{
    private static readonly Lazy<(FontFamily Title, FontFamily Spooky, FontFamily Body)> Families = new(Load);

    /// <summary>Cinzel: month names and week days.</summary>
    public static FontFamily Title => Families.Value.Title;

    /// <summary>Creepster: the Halloween month name.</summary>
    public static FontFamily Spooky => Families.Value.Spooky;

    /// <summary>Noto Sans: day numbers and event titles; broad coverage, also the fallback for the other two.</summary>
    public static FontFamily Body => Families.Value.Body;

    private static (FontFamily, FontFamily, FontFamily) Load()
    {
        var collection = new FontCollection();
        var assembly = typeof(CalendarFonts).Assembly;

        FontFamily Add(string file)
        {
            using var stream = assembly.GetManifestResourceStream($"GilgameshBot.Fonts.{file}")
                               ?? throw new InvalidOperationException($"Embedded font {file} is missing.");
            return collection.Add(stream);
        }

        return (Add("Cinzel.ttf"), Add("Creepster.ttf"), Add("NotoSans.ttf"));
    }

    [GeneratedRegex(@"<a?:[A-Za-z0-9_~]{1,32}:\d{1,20}>")]
    private static partial Regex CustomEmojiRegex();

    /// <summary>
    /// Event title as the image can show it: Discord custom emoji and markdown marks removed,
    /// styled letters (𝔗𝔥𝔢) folded to plain ones, and anything the body font has no glyph for
    /// (emoji, mostly) dropped instead of drawn as boxes.
    /// </summary>
    public static string CleanForImage(string text)
    {
        var withoutEmoji = CustomEmojiRegex().Replace(text, " ");
        var folded = withoutEmoji.Normalize(NormalizationForm.FormKC);

        var font = Body.CreateFont(12);
        var builder = new StringBuilder(folded.Length);
        foreach (var rune in folded.EnumerateRunes())
        {
            if (rune.Value is '*' or '_' or '~' or '`' or '|')
                continue;

            if (Rune.IsWhiteSpace(rune))
            {
                builder.Append(' ');
                continue;
            }

            if (Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format
                or UnicodeCategory.OtherSymbol or UnicodeCategory.Surrogate or UnicodeCategory.PrivateUse)
                continue;

            // Variation selectors and skin tones only ever trail an emoji, which is already gone.
            if (rune.Value is >= 0xFE00 and <= 0xFE0F or >= 0xE0100 and <= 0xE01EF or >= 0x1F3FB and <= 0x1F3FF)
                continue;

            if (!font.TryGetGlyphs(new CodePoint(rune.Value), out var glyphs) || glyphs.Count == 0)
                continue;

            builder.Append(rune.ToString());
        }

        return MultiSpaceRegex().Replace(builder.ToString(), " ").Trim();
    }

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex MultiSpaceRegex();
}
