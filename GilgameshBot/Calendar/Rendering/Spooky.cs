using SixLabors.ImageSharp;

namespace GilgameshBot.Calendar.Rendering;

/// <summary>The Halloween theme's moon, bats, pumpkins and cobwebs.</summary>
internal static class Spooky
{
    private static readonly Color BatDark = Color.ParseHex("140a1c");
    private static readonly Color BatLight = Color.ParseHex("9a6ad0");

    /// <summary>Around the title plate: a moon with a bat across it, more bats, two pumpkins.</summary>
    public static void TitleDecorations(Canvas c, float plateX, float plateWidth)
    {
        c.Fill(Color.FromRgba(255, 138, 31, 50), c.Circle(70, 30, 27));
        c.Fill(Color.ParseHex("ffb347"), c.Circle(70, 30, 19));
        c.Fill(Color.FromRgba(200, 110, 20, 90), c.Circle(64, 25, 4));
        c.Fill(Color.FromRgba(200, 110, 20, 90), c.Circle(76, 36, 3));

        Bat(c, 72, 32, 1.1f, Color.ParseHex("120a1a"));
        Bat(c, 112, 18, 0.7f, BatLight);
        Bat(c, 580, 22, 0.85f, BatLight);
        Bat(c, 612, 38, 0.6f, BatLight);

        Pumpkin(c, plateX - 52, 28, 12);
        Pumpkin(c, plateX + plateWidth + 52, 28, 12);
    }

    /// <summary>
    /// Cobwebs in the frame's top corners, small pumpkins on some days without events, bats on
    /// some others, and a big pumpkin on the 31st of October.
    /// </summary>
    public static void GridDecorations(
        Canvas c, CalendarPage page, List<(float X, float Y, DateOnly Date, bool InMonth)> cells, Random random)
    {
        Web(c, 8, 8, 46, 0, MathF.PI / 2);
        Web(c, Layout.Width - 8, 8, 46, MathF.PI / 2, MathF.PI);

        const float w = Layout.CellWidth;
        const float h = Layout.CellHeight;

        foreach (var (x, y, date, inMonth) in cells)
        {
            // Drawn for every cell so the generator advances the same way whatever the events are.
            var roll = random.NextSingle();
            var batScale = 0.6f + random.NextSingle() * 0.4f;

            if (!inMonth || page.Entries.ContainsKey(date))
                continue;

            if (date is { Month: 10, Day: 31 })
            {
                Pumpkin(c, x + w / 2, y + h / 2 + 10, 22);
                Bat(c, x + w - 14, y + 24, 0.6f, BatDark);
            }
            else if (roll < 0.16f)
            {
                Pumpkin(c, x + w - 16, y + h - 16, 10);
            }
            else if (roll < 0.28f)
            {
                Bat(c, x + w - 20, y + 30, batScale, BatDark);
            }
        }
    }

    public static void Bat(Canvas c, float cx, float cy, float size, Color colour)
    {
        (float X, float Y) B(float x, float y) => (cx + x * size, cy + y * size);

        var points = new List<(float X, float Y)> { B(0, 0) };
        void Quad(float qx, float qy, float ex, float ey)
        {
            var (sx, sy) = points[^1];
            var (kx, ky) = B(qx, qy);
            var (tx, ty) = B(ex, ey);
            for (var i = 1; i <= 6; i++)
            {
                var t = i / 6f;
                var u = 1 - t;
                points.Add((u * u * sx + 2 * u * t * kx + t * t * tx, u * u * sy + 2 * u * t * ky + t * t * ty));
            }
        }

        Quad(-5, -7, -14, -4);
        Quad(-11, -2, -11, 1);
        Quad(-8, -1, -6, 2);
        Quad(-4, 0, -2, 3);
        points.Add(B(0, 2));
        points.Add(B(2, 3));
        Quad(4, 0, 6, 2);
        Quad(8, -1, 11, 1);
        Quad(11, -2, 14, -4);
        Quad(5, -7, 0, 0);

        c.Fill(colour, c.Poly(points.ToArray()));
        c.Fill(colour, c.Circle(cx, cy - size, 2.2f * size));
        c.Fill(colour, c.Poly(B(-1.8f, -2), B(-1.2f, -5), B(-0.2f, -2.6f)));
        c.Fill(colour, c.Poly(B(1.8f, -2), B(1.2f, -5), B(0.2f, -2.6f)));
    }

    public static void Pumpkin(Canvas c, float cx, float cy, float r)
    {
        var outline = Color.ParseHex("6e2606");
        var stroke = Math.Max(0.8f, r * 0.06f);

        c.Fill(Color.ParseHex("4d6b1f"), c.Rect(cx - r * 0.1f, cy - r * 1.02f, r * 0.2f, r * 0.32f));

        foreach (var (offset, colour) in new[] { (-0.42f, "c9561a"), (0.42f, "c9561a"), (0f, "f08a24") })
        {
            var lobe = c.Ellipse(cx + offset * r, cy, r * (offset == 0 ? 0.5f : 0.55f), r * 0.78f);
            c.Fill(Color.ParseHex(colour), lobe);
            c.Stroke(outline, stroke, lobe);
        }

        Face(c, cx, cy, r * 1.12f, Color.FromRgba(255, 176, 46, 90));
        Face(c, cx, cy, r, Color.ParseHex("ffe07a"));
    }

    private static void Face(Canvas c, float cx, float cy, float r, Color colour)
    {
        (float X, float Y) F(float x, float y) => (cx + x * r, cy + y * r);

        c.Fill(colour, c.Poly(F(-0.42f, -0.08f), F(-0.14f, -0.08f), F(-0.28f, -0.34f)));
        c.Fill(colour, c.Poly(F(0.42f, -0.08f), F(0.14f, -0.08f), F(0.28f, -0.34f)));

        var mouth = new List<(float X, float Y)>
        {
            F(-0.46f, 0.12f), F(-0.3f, 0.3f), F(-0.18f, 0.18f), F(-0.06f, 0.36f),
            F(0.06f, 0.18f), F(0.18f, 0.36f), F(0.3f, 0.18f), F(0.46f, 0.12f),
        };

        // The jaw: a curve from the right corner back to the left one.
        for (var i = 1; i < 8; i++)
        {
            var t = i / 8f;
            var u = 1 - t;
            mouth.Add(F(u * u * 0.46f + t * t * -0.46f, u * u * 0.12f + 2 * u * t * 0.62f + t * t * 0.12f));
        }

        c.Fill(colour, c.Poly(mouth.ToArray()));
    }

    private static void Web(Canvas c, float cx, float cy, float r, float from, float to)
    {
        var colour = Color.FromRgba(230, 215, 255, 97);
        var angles = Enumerable.Range(0, 5).Select(i => from + (to - from) * i / 4).ToArray();

        foreach (var a in angles)
            c.Line(colour, 0.8f, (cx, cy), (cx + MathF.Cos(a) * r, cy + MathF.Sin(a) * r));

        foreach (var f in new[] { 0.3f, 0.55f, 0.8f })
        {
            var points = new List<(float X, float Y)> { (cx + MathF.Cos(angles[0]) * r * f, cy + MathF.Sin(angles[0]) * r * f) };
            for (var i = 1; i < angles.Length; i++)
            {
                var (sx, sy) = points[^1];
                var mid = (angles[i] + angles[i - 1]) / 2;
                var (kx, ky) = (cx + MathF.Cos(mid) * r * f * 0.82f, cy + MathF.Sin(mid) * r * f * 0.82f);
                var (tx, ty) = (cx + MathF.Cos(angles[i]) * r * f, cy + MathF.Sin(angles[i]) * r * f);
                for (var k = 1; k <= 6; k++)
                {
                    var t = k / 6f;
                    var u = 1 - t;
                    points.Add((u * u * sx + 2 * u * t * kx + t * t * tx, u * u * sy + 2 * u * t * ky + t * t * ty));
                }
            }

            c.Line(colour, 0.8f, points.ToArray());
        }
    }
}
