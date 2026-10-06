namespace GilgameshBot.Calendar;

/// <summary>
/// One month as the image shows it: the grid of days and the event titles on each. Built from the
/// events and the calendar's time zone; drawing needs nothing else.
/// </summary>
public sealed class CalendarPage
{
    /// <summary>Rows in the grid: always six weeks, like the in-game calendars, so every image has the same size.</summary>
    public const int Weeks = 6;

    private CalendarPage(int year, int month, DayOfWeek firstDay, DateOnly? today, Dictionary<DateOnly, List<string>> entries)
    {
        Year = year;
        Month = month;
        FirstDay = firstDay;
        Today = today;
        Entries = entries;
    }

    public int Year { get; }
    public int Month { get; }
    public DayOfWeek FirstDay { get; }

    /// <summary>Today in the calendar's time zone, when it falls on this page.</summary>
    public DateOnly? Today { get; }

    /// <summary>Event titles per day of this month, in start order. Days without events are absent.</summary>
    public IReadOnlyDictionary<DateOnly, List<string>> Entries { get; }

    public DateOnly FirstOfMonth => new(Year, Month, 1);

    /// <summary>The first day in the grid's top-left cell; may belong to the previous month.</summary>
    public DateOnly GridStart
    {
        get
        {
            var offset = ((int)FirstOfMonth.DayOfWeek - (int)FirstDay + 7) % 7;
            return FirstOfMonth.AddDays(-offset);
        }
    }

    /// <summary>The week days in column order.</summary>
    public IEnumerable<DayOfWeek> Columns => Enumerable.Range(0, 7).Select(i => (DayOfWeek)(((int)FirstDay + i) % 7));

    /// <summary>
    /// The page for <paramref name="year"/>/<paramref name="month"/>: every event whose start falls on
    /// one of its days in <paramref name="zone"/>.
    /// </summary>
    public static CalendarPage Build(
        IEnumerable<CalendarEvent> events, TimeZoneInfo zone, DayOfWeek firstDay, int year, int month, DateTimeOffset now)
    {
        var entries = new Dictionary<DateOnly, List<string>>();
        foreach (var e in events.OrderBy(e => e.Start))
        {
            var day = LocalDay(e.Start, zone);
            if (day.Year != year || day.Month != month)
                continue;

            if (!entries.TryGetValue(day, out var list))
                entries[day] = list = [];

            list.Add(e.Title);
        }

        var today = LocalDay(now, zone);
        return new CalendarPage(year, month, firstDay,
            today.Year == year && today.Month == month ? today : null, entries);
    }

    /// <summary>The calendar day <paramref name="instant"/> falls on in <paramref name="zone"/>.</summary>
    public static DateOnly LocalDay(DateTimeOffset instant, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime);
}
