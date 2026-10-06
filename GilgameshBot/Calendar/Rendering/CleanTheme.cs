using SixLabors.Fonts;
using SixLabors.ImageSharp;

namespace GilgameshBot.Calendar.Rendering;

/// <summary>Flat colours and simple boxes: the easiest to read on a phone.</summary>
internal static class CleanTheme
{
    private static readonly Color Background = Color.ParseHex("2a2118");
    private static readonly Color Frame = Color.ParseHex("6b5432");
    private static readonly Color Title = Color.ParseHex("f3d48a");
    private static readonly Color WeekdayFill = Color.ParseHex("7a4a14");
    private static readonly Color WeekdayText = Color.ParseHex("f8e7bf");
    private static readonly Color Day = Color.ParseHex("d9c08a");
    private static readonly Color OtherMonth = Color.ParseHex("4a3c2a");
    private static readonly Color DayNumber = Color.ParseHex("3b2a12");
    private static readonly Color OtherNumber = Color.ParseHex("8a7a60");
    private static readonly Color Today = Color.ParseHex("fff3c4");
    private static readonly Color ChipFill = Color.ParseHex("0f6e56");
    private static readonly Color ChipText = Color.ParseHex("e1f5ee");

    private const float ChipTop = 22;
    private const float ChipHeight = 16;
    private const float ChipGap = 2;

    public static void Draw(Canvas c, CalendarPage page)
    {
        c.Fill(Background, c.Rect(0, 0, Layout.Width, Layout.Height));
        c.Stroke(Frame, 2, c.RoundRect(1, 1, Layout.Width - 2, Layout.Height - 2, 6));

        var title = c.Font(CalendarFonts.Body, 21);
        c.Text($"{CalendarRenderer.MonthName(page.Month)} {page.Year}", title, Title,
            Layout.Width / 2, 29, HorizontalAlignment.Center, VerticalAlignment.Center, bold: 0.6f);

        var weekday = c.Font(CalendarFonts.Body, 12);
        var column = 0;
        foreach (var day in page.Columns)
        {
            var x = Layout.ColumnX(column++);
            c.Fill(WeekdayFill, c.RoundRect(x, Layout.HeaderTop, Layout.CellWidth, Layout.HeaderHeight, 2));
            c.Text(CalendarRenderer.DayName(day, full: false), weekday, WeekdayText,
                x + Layout.CellWidth / 2, Layout.HeaderTop + Layout.HeaderHeight / 2,
                HorizontalAlignment.Center, VerticalAlignment.Center);
        }

        var number = c.Font(CalendarFonts.Body, 12);
        var chip = c.Font(CalendarFonts.Body, 11);
        var slots = (int)((Layout.CellHeight - ChipTop - 2) / (ChipHeight + ChipGap));

        foreach (var (x, y, date, inMonth) in Layout.Cells(page))
        {
            c.Fill(inMonth ? Day : OtherMonth, c.RoundRect(x, y, Layout.CellWidth, Layout.CellHeight, 3));
            c.Text(date.Day.ToString(), number, inMonth ? DayNumber : OtherNumber, x + 5, y + 3);

            if (inMonth && page.Today == date)
                c.Stroke(Today, 2.5f, c.RoundRect(x + 1.25f, y + 1.25f, Layout.CellWidth - 2.5f, Layout.CellHeight - 2.5f, 3));

            if (!inMonth || !page.Entries.TryGetValue(date, out var titles))
                continue;

            var lines = Layout.Lines(titles, slots);
            for (var i = 0; i < lines.Count; i++)
            {
                var cy = y + ChipTop + i * (ChipHeight + ChipGap);
                c.Fill(ChipFill, c.RoundRect(x + 3, cy, Layout.CellWidth - 6, ChipHeight, 2));
                c.Text(c.Fit(lines[i], chip, Layout.CellWidth - 12), chip, ChipText,
                    x + 6, cy + ChipHeight / 2, v: VerticalAlignment.Center);
            }
        }
    }
}
