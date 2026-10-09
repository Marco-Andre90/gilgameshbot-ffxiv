using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace GilgameshBot.Calendar.Rendering;

/// <summary>Colours and touches of one textured theme; the drawing itself is shared.</summary>
internal sealed record TexturedStyle
{
    public required Color Background { get; init; }
    public required Color Frame { get; init; }
    public required Color FrameInner { get; init; }
    public required Color PlateTop { get; init; }
    public required Color PlateBottom { get; init; }
    public required Color PlateStroke { get; init; }
    public required bool SpookyTitle { get; init; }
    public required Color TitleFill { get; init; }
    public required Color TitleShadow { get; init; }
    public required Color ArrowTop { get; init; }
    public required Color ArrowBottom { get; init; }
    public required Color ArrowInk { get; init; }
    public required Color Year { get; init; }
    public required Color[] Header { get; init; }
    public required Color HeaderStroke { get; init; }
    public required Color HeaderText { get; init; }
    public required Color[] Cell { get; init; }
    public required Color[] OtherCell { get; init; }
    public required (byte R, byte G, byte B) Stain { get; init; }
    public required Color Edge { get; init; }
    public required float Highlight { get; init; }
    public required Color Number { get; init; }
    public required Color OtherNumber { get; init; }
    public required Color LabelFill { get; init; }
    public required Color LabelBar { get; init; }
    public required Color Today { get; init; }
    public required float Noise { get; init; }

    /// <summary>Pennant colours along the top of every day of the month; null for none.</summary>
    public Color[]? Pennants { get; init; }

    /// <summary>Moon, bats, pumpkins and cobwebs.</summary>
    public bool Decorations { get; init; }

    public static readonly TexturedStyle Wow = new()
    {
        Background = Color.ParseHex("17120d"),
        Frame = Color.ParseHex("6f6a62"),
        FrameInner = Color.ParseHex("2a251f"),
        PlateTop = Color.ParseHex("2b3d6b"),
        PlateBottom = Color.ParseHex("111a33"),
        PlateStroke = Color.ParseHex("b8933a"),
        SpookyTitle = false,
        TitleFill = Color.White,
        TitleShadow = Color.Black,
        ArrowTop = Color.ParseHex("e8c45a"),
        ArrowBottom = Color.ParseHex("7a5a14"),
        ArrowInk = Color.ParseHex("1c1203"),
        Year = Color.ParseHex("e0b84a"),
        Header = [Color.ParseHex("e88a2c"), Color.ParseHex("c0581a"), Color.ParseHex("7a2e08")],
        HeaderStroke = Color.ParseHex("3a1404"),
        HeaderText = Color.ParseHex("fbe6b4"),
        Cell = [Color.ParseHex("e6c27a"), Color.ParseHex("cc9f55"), Color.ParseHex("8f6128")],
        OtherCell = [Color.ParseHex("4a3b28"), Color.ParseHex("241a10")],
        Stain = (120, 70, 20),
        Edge = Color.FromRgba(70, 40, 10, 140),
        Highlight = 0.35f,
        Number = Color.White,
        OtherNumber = Color.ParseHex("7a6a52"),
        LabelFill = Color.FromRgba(18, 10, 3, 184),
        LabelBar = Color.ParseHex("5dcaa5"),
        Today = Color.ParseHex("d8d4c8"),
        Noise = 22,
    };

    public static readonly TexturedStyle Halloween = new()
    {
        Background = Color.ParseHex("120a1a"),
        Frame = Color.ParseHex("7a45b0"),
        FrameInner = Color.ParseHex("2a1638"),
        PlateTop = Color.ParseHex("4a1d73"),
        PlateBottom = Color.ParseHex("1d0b30"),
        PlateStroke = Color.ParseHex("ff8a1f"),
        SpookyTitle = true,
        TitleFill = Color.ParseHex("ff9a2e"),
        TitleShadow = Color.ParseHex("2a0b40"),
        ArrowTop = Color.ParseHex("ffa94a"),
        ArrowBottom = Color.ParseHex("b3480c"),
        ArrowInk = Color.ParseHex("2a0b40"),
        Year = Color.ParseHex("ff9a2e"),
        Header = [Color.ParseHex("ff8f2a"), Color.ParseHex("c4521a"), Color.ParseHex("4e1d74")],
        HeaderStroke = Color.ParseHex("1d0b30"),
        HeaderText = Color.ParseHex("fff1de"),
        Cell = [Color.ParseHex("5b3478"), Color.ParseHex("3d2156"), Color.ParseHex("21102f")],
        OtherCell = [Color.ParseHex("1d1228"), Color.ParseHex("0e0814")],
        Stain = (255, 130, 30),
        Edge = Color.FromRgba(255, 140, 40, 140),
        Highlight = 0.12f,
        Number = Color.ParseHex("ffd9a8"),
        OtherNumber = Color.ParseHex("5a4470"),
        LabelFill = Color.FromRgba(10, 4, 16, 199),
        LabelBar = Color.ParseHex("5dcaa5"),
        Today = Color.ParseHex("ff9a2e"),
        Noise = 14,
        Pennants = [Color.ParseHex("ff8a1f"), Color.ParseHex("140a1c"), Color.ParseHex("8a4cc8")],
        Decorations = true,
    };
}

/// <summary>
/// The in-game-calendar look: parchment (or, for Halloween, purple stone) days with uneven edges,
/// stains and grain, an orange weekday bar and a title plate with arrows.
/// </summary>
internal static class TexturedTheme
{
    private const float PlateWidth = 170;
    private const float PennantHeight = 11;

    public static void Draw(Image<Rgba32> image, CalendarPage page, TexturedStyle style, float scale, int seed)
    {
        var random = new Random(seed);
        var cells = Layout.Cells(page).ToList();

        image.Mutate(ctx => DrawBase(new Canvas(ctx, scale), page, style, cells, random));
        AddGrain(image, style.Noise, random);
        image.Mutate(ctx => DrawForeground(new Canvas(ctx, scale), page, style, cells, random));
    }

    // --- Under the grain ----------------------------------------------------------------------

    private static void DrawBase(
        Canvas c, CalendarPage page, TexturedStyle s, List<(float X, float Y, DateOnly Date, bool InMonth)> cells, Random random)
    {
        var height = Layout.Height(page);
        c.Fill(s.Background, c.Rect(0, 0, Layout.Width, height));
        c.Stroke(s.Frame, 3, c.Rect(2, 2, Layout.Width - 4, height - 4));
        c.Stroke(s.FrameInner, 1, c.Rect(6, 6, Layout.Width - 12, height - 12));

        var plateX = (Layout.Width - PlateWidth) / 2;
        if (s.Decorations)
            Spooky.TitleDecorations(c, plateX, PlateWidth);

        DrawTitle(c, page, s, plateX);
        DrawWeekdays(c, page, s);

        foreach (var (x, y, date, inMonth) in cells)
            DrawCell(c, s, x, y, inMonth, random);
    }

    private static void DrawTitle(Canvas c, CalendarPage page, TexturedStyle s, float plateX)
    {
        c.Fill(c.Linear(0, 10, 0, 38, (0, s.PlateTop), (1, s.PlateBottom)), c.RoundRect(plateX, 10, PlateWidth, 28, 6));
        c.Stroke(s.PlateStroke, 2, c.RoundRect(plateX, 10, PlateWidth, 28, 6));

        var font = s.SpookyTitle ? c.Font(CalendarFonts.Spooky, 24) : c.Font(CalendarFonts.Title, 19);
        c.Text(CalendarRenderer.MonthName(page.Month), font, s.TitleFill, Layout.Width / 2, 24,
            HorizontalAlignment.Center, VerticalAlignment.Center, shadow: s.TitleShadow, bold: s.SpookyTitle ? 0 : 0.4f);

        foreach (var (ax, left) in new[] { (plateX - 30, true), (plateX + PlateWidth + 8, false) })
        {
            c.Fill(c.Linear(0, 12, 0, 36, (0, s.ArrowTop), (1, s.ArrowBottom)), c.RoundRect(ax, 12, 22, 24, 3));
            c.Stroke(Color.ParseHex("1a0c04"), 1.5f, c.RoundRect(ax, 12, 22, 24, 3));
            c.Fill(s.ArrowInk, left
                ? c.Poly((ax + 15, 17), (ax + 7, 24), (ax + 15, 31))
                : c.Poly((ax + 7, 17), (ax + 15, 24), (ax + 7, 31)));
        }

        c.Fill(Color.ParseHex("0c0c0c"), c.RoundRect(Layout.Width / 2 - 24, 38, 48, 13, 3));
        c.Stroke(s.FrameInner, 1, c.RoundRect(Layout.Width / 2 - 24, 38, 48, 13, 3));
        c.Text(page.Year.ToString(), c.Font(CalendarFonts.Body, 10), s.Year, Layout.Width / 2, 44.5f,
            HorizontalAlignment.Center, VerticalAlignment.Center);
    }

    private static void DrawWeekdays(Canvas c, CalendarPage page, TexturedStyle s)
    {
        var font = c.Font(CalendarFonts.Title, 11.5f);
        var column = 0;
        foreach (var day in page.Columns)
        {
            var x = Layout.ColumnX(column++);
            const float y = Layout.HeaderTop;
            const float h = Layout.HeaderHeight;

            c.Fill(c.Linear(0, y, 0, y + h, (0, s.Header[0]), (0.5f, s.Header[1]), (1, s.Header[2])), c.Rect(x, y, Layout.CellWidth, h));
            c.Stroke(s.HeaderStroke, 1, c.Rect(x + 0.5f, y + 0.5f, Layout.CellWidth - 1, h - 1));
            c.Fill(Color.FromRgba(255, 220, 160, 90), c.Rect(x + 1, y + 1, Layout.CellWidth - 2, 1));

            var name = CalendarRenderer.DayName(day, full: true);
            if (c.Measure(name, font) > Layout.CellWidth - 6)
                name = CalendarRenderer.DayName(day, full: false);

            c.Text(name, font, s.HeaderText, x + Layout.CellWidth / 2, y + h / 2,
                HorizontalAlignment.Center, VerticalAlignment.Center, shadow: Color.Black, bold: 0.35f);
        }
    }

    private static void DrawCell(Canvas c, TexturedStyle s, float x, float y, bool inMonth, Random random)
    {
        const float w = Layout.CellWidth;
        const float h = Layout.CellHeight;
        float J() => (random.NextSingle() - 0.5f) * 3;

        var shape = c.Poly(
            (x + J(), y + J()), (x + w / 2, y + J()), (x + w + J(), y + J()), (x + w + J(), y + h / 2),
            (x + w + J(), y + h + J()), (x + w / 2, y + h + J()), (x + J(), y + h + J()), (x + J(), y + h / 2));

        var fill = inMonth
            ? c.Radial(x + w / 2, y + h / 2, w * 0.75f, (0, s.Cell[0]), (0.6f, s.Cell[1]), (1, s.Cell[2]))
            : c.Radial(x + w / 2, y + h / 2, w * 0.75f, (0, s.OtherCell[0]), (1, s.OtherCell[1]));
        c.Fill(fill, shape);

        // Drawn even when unused, so every cell takes the same numbers from the generator and
        // the picture never depends on which days have events.
        var stains = Enumerable.Range(0, 5)
            .Select(_ => (X: x + random.NextSingle() * w, Y: y + random.NextSingle() * h,
                R: 4 + random.NextSingle() * 14, A: 0.05f + random.NextSingle() * 0.08f))
            .ToList();

        c.Clip(shape, inner =>
        {
            foreach (var st in stains)
            {
                // Soft-edged: dense in the middle, gone at the rim, like a damp spot on paper.
                var colour = inMonth
                    ? Color.FromRgba(s.Stain.R, s.Stain.G, s.Stain.B, (byte)(st.A * 255))
                    : Color.FromRgba(0, 0, 0, 46);
                var pixel = colour.ToPixel<Rgba32>();
                var clear = Color.FromRgba(pixel.R, pixel.G, pixel.B, 0);
                inner.Fill(inner.Radial(st.X, st.Y, st.R, (0, colour), (0.55f, colour), (1, clear)), inner.Circle(st.X, st.Y, st.R));
            }

            inner.Stroke(inMonth ? s.Edge : Color.FromRgba(0, 0, 0, 153), 3, shape);
            inner.Line(Color.FromRgba(255, 240, 200, (byte)((inMonth ? s.Highlight : 0.05f) * 255)), 1,
                (x + 2, y + h - 2), (x + 2, y + 2), (x + w - 2, y + 2));

            if (inMonth && s.Pennants is { } colours)
            {
                for (var k = 0; k < 7; k++)
                {
                    var px = x + k * w / 7;
                    inner.Fill(colours[k % colours.Length], inner.Poly((px, y), (px + w / 7, y), (px + w / 14, y + PennantHeight)));
                }

                inner.Line(Color.ParseHex("2a1a10"), 1, (x, y + 0.5f), (x + w, y + 0.5f));
            }
        });
    }

    // --- Grain --------------------------------------------------------------------------------

    private static void AddGrain(Image<Rgba32> image, float amount, Random random)
    {
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    var v = (random.NextSingle() - 0.5f) * amount;
                    ref var p = ref row[x];
                    p.R = Clamp(p.R + v);
                    p.G = Clamp(p.G + v);
                    p.B = Clamp(p.B + v * 0.8f);
                }
            }
        });
    }

    private static byte Clamp(float value) => (byte)Math.Clamp((int)MathF.Round(value), 0, 255);

    // --- Over the grain -----------------------------------------------------------------------

    private static void DrawForeground(
        Canvas c, CalendarPage page, TexturedStyle s, List<(float X, float Y, DateOnly Date, bool InMonth)> cells, Random random)
    {
        if (s.Decorations)
            Spooky.GridDecorations(c, page, cells, random);

        var number = c.Font(CalendarFonts.Body, 13);
        var label = c.Font(CalendarFonts.Body, 12);
        const float w = Layout.CellWidth;
        const float h = Layout.CellHeight;

        foreach (var (x, y, date, inMonth) in cells)
        {
            var top = inMonth && s.Pennants is not null ? y + PennantHeight + 1 : y + 3;
            c.Text(date.Day.ToString(), number, inMonth ? s.Number : s.OtherNumber, x + 5, top,
                shadow: Color.Black, bold: 0.5f);

            if (!inMonth || !page.Entries.TryGetValue(date, out var titles))
                continue;

            var slots = (int)((y + h - 2 - (top + 17)) / 16);
            var lines = Layout.Lines(titles, Math.Max(1, slots));
            for (var i = 0; i < lines.Count; i++)
            {
                // Stacked from the bottom of the day, like the in-game calendar's banners.
                var ly = y + h - 17 - (lines.Count - 1 - i) * 16;
                c.Fill(s.LabelFill, c.Rect(x + 2, ly, w - 4, 15));
                c.Fill(s.LabelBar, c.Rect(x + 2, ly, 3, 15));
                c.Text(c.Fit(lines[i], label, w - 14), label, Color.White, x + 8, ly + 7.5f, v: VerticalAlignment.Center);
            }
        }

        if (cells.FirstOrDefault(cell => cell.InMonth && cell.Date == page.Today) is { InMonth: true } today)
        {
            if (s.Decorations)
                c.Stroke(Color.FromRgba(255, 138, 31, 90), 7, c.Rect(today.X + 1.5f, today.Y + 1.5f, w - 3, h - 3));

            c.Stroke(s.Today, 3, c.Rect(today.X + 1.5f, today.Y + 1.5f, w - 3, h - 3));
            c.Stroke(Color.ParseHex("5a5650"), 1, c.Rect(today.X - 1, today.Y - 1, w + 2, h + 2));
        }
    }
}
