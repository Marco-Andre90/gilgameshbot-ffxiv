using System.Collections.Concurrent;

namespace GilgameshBot.Calendar;

/// <summary>A time zone the calendar can use to place events on days.</summary>
/// <param name="Key">Short id stored in Discord (in the calendar message's custom ids).</param>
/// <param name="Label">Shown to members.</param>
/// <param name="WindowsId">Windows time zone id.</param>
/// <param name="IanaId">IANA time zone id, for systems without the Windows ids.</param>
/// <param name="FirstDay">First column of the calendar, as calendars are printed there.</param>
public sealed record CalendarTimeZone(string Key, string Label, string WindowsId, string IanaId, DayOfWeek FirstDay);

/// <summary>
/// The fixed list of time zones the calendar offers. A fixed list rather than any id: players run
/// the game on Windows and under Wine, which know different sets of ids, so each entry carries
/// both and the first one the system knows is used.
/// </summary>
public static class CalendarTimeZones
{
    public const string DefaultKey = "us-central";

    public static readonly IReadOnlyList<CalendarTimeZone> All =
    [
        new("us-central", "US Central (CT)", "Central Standard Time", "America/Chicago", DayOfWeek.Sunday),
        new("us-eastern", "US Eastern (ET)", "Eastern Standard Time", "America/New_York", DayOfWeek.Sunday),
        new("us-mountain", "US Mountain (MT)", "Mountain Standard Time", "America/Denver", DayOfWeek.Sunday),
        new("us-pacific", "US Pacific (PT)", "Pacific Standard Time", "America/Los_Angeles", DayOfWeek.Sunday),
        new("brasilia", "Brasília (BRT)", "E. South America Standard Time", "America/Sao_Paulo", DayOfWeek.Sunday),
        new("utc", "UTC", "UTC", "Etc/UTC", DayOfWeek.Monday),
        new("uk", "UK (GMT/BST)", "GMT Standard Time", "Europe/London", DayOfWeek.Monday),
        new("central-europe", "Central Europe (CET)", "W. Europe Standard Time", "Europe/Berlin", DayOfWeek.Monday),
        new("japan", "Japan (JST)", "Tokyo Standard Time", "Asia/Tokyo", DayOfWeek.Sunday),
        new("australia-east", "Australia East (AET)", "AUS Eastern Standard Time", "Australia/Sydney", DayOfWeek.Monday),
    ];

    private static readonly ConcurrentDictionary<string, TimeZoneInfo?> Resolved = new();

    /// <summary>The entry for <paramref name="key"/>, or null when the key is unknown.</summary>
    public static CalendarTimeZone? Find(string? key) =>
        All.FirstOrDefault(z => string.Equals(z.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>The entry for <paramref name="key"/>, or the default when the key is unknown.</summary>
    public static CalendarTimeZone FindOrDefault(string? key) => Find(key) ?? Find(DefaultKey)!;

    /// <summary>
    /// The system time zone for <paramref name="zone"/>, or null when this system knows neither id
    /// (the calendar then falls back to UTC and says so).
    /// </summary>
    public static TimeZoneInfo? Resolve(CalendarTimeZone zone) =>
        Resolved.GetOrAdd(zone.Key, _ =>
        {
            foreach (var id in new[] { zone.WindowsId, zone.IanaId })
            {
                try
                {
                    return TimeZoneInfo.FindSystemTimeZoneById(id);
                }
                catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
                {
                    // Try the other id.
                }
            }

            return zone.Key == "utc" ? TimeZoneInfo.Utc : null;
        });
}
