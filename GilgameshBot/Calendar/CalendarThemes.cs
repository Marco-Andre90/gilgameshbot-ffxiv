namespace GilgameshBot.Calendar;

/// <summary>A look for the calendar image.</summary>
/// <param name="Key">Short id stored in Discord (in the calendar message's custom ids).</param>
/// <param name="Label">Shown to members.</param>
public sealed record CalendarTheme(string Key, string Label);

/// <summary>The calendar's themes. Drawing lives in <see cref="Rendering.CalendarRenderer"/>.</summary>
public static class CalendarThemes
{
    public const string Clean = "clean";
    public const string Wow = "wow";
    public const string Halloween = "halloween";

    /// <summary>Not drawn itself: picks a theme by the month shown (<see cref="Resolve"/>).</summary>
    public const string Seasonal = "seasonal";

    public const string DefaultKey = Clean;

    public static readonly IReadOnlyList<CalendarTheme> All =
    [
        new(Clean, "Clean"),
        new(Wow, "World of Warcraft"),
        new(Halloween, "Halloween"),
        new(Seasonal, "Seasonal (Halloween in October, Clean otherwise)"),
    ];

    /// <summary>The entry for <paramref name="key"/>, or null when the key is unknown.</summary>
    public static CalendarTheme? Find(string? key) =>
        All.FirstOrDefault(t => string.Equals(t.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>The key of a known theme, or the default.</summary>
    public static string Normalize(string? key) => Find(key)?.Key ?? DefaultKey;

    /// <summary>The theme actually drawn for <paramref name="month"/>: <see cref="Seasonal"/> picks one by month.</summary>
    public static string Resolve(string? key, int month) => Normalize(key) switch
    {
        Seasonal => month == 10 ? Halloween : Clean,
        var k => k,
    };
}
