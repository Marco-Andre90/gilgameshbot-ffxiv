using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Processing;

namespace GilgameshBot.Calendar.Rendering;

/// <summary>
/// Drawing in layout units: every coordinate and size is multiplied by <see cref="Scale"/>, so the
/// layout is written once at a readable size and the image comes out sharper. Gradients are
/// scaled here too, which a drawing transform would not do for brushes.
/// </summary>
internal sealed class Canvas
{
    private readonly IImageProcessingContext ctx;

    public Canvas(IImageProcessingContext ctx, float scale)
    {
        this.ctx = ctx;
        Scale = scale;
    }

    public float Scale { get; }

    public PointF P(float x, float y) => new(x * Scale, y * Scale);

    // --- Shapes -----------------------------------------------------------------------------

    public IPath Rect(float x, float y, float w, float h) => new RectangularPolygon(x * Scale, y * Scale, w * Scale, h * Scale);

    public IPath RoundRect(float x, float y, float w, float h, float r)
    {
        r = Math.Min(r, Math.Min(w, h) / 2);
        if (r <= 0)
            return Rect(x, y, w, h);

        var points = new List<PointF>();
        void Corner(float cx, float cy, float startDeg)
        {
            for (var i = 0; i <= 6; i++)
            {
                var a = (startDeg + i * 15) * MathF.PI / 180;
                points.Add(P(cx + MathF.Cos(a) * r, cy + MathF.Sin(a) * r));
            }
        }

        Corner(x + w - r, y + r, 270);
        Corner(x + w - r, y + h - r, 0);
        Corner(x + r, y + h - r, 90);
        Corner(x + r, y + r, 180);
        return new Polygon(points.ToArray());
    }

    public IPath Poly(params (float X, float Y)[] points) => new Polygon(points.Select(p => P(p.X, p.Y)).ToArray());

    public IPath Ellipse(float cx, float cy, float rx, float ry) =>
        new EllipsePolygon(P(cx, cy), new SizeF(rx * 2 * Scale, ry * 2 * Scale));

    public IPath Circle(float cx, float cy, float r) => Ellipse(cx, cy, r, r);

    // --- Brushes ----------------------------------------------------------------------------

    public Brush Linear(float x1, float y1, float x2, float y2, params (float At, Color Color)[] stops) =>
        new LinearGradientBrush(P(x1, y1), P(x2, y2), GradientRepetitionMode.None,
            stops.Select(s => new ColorStop(s.At, s.Color)).ToArray());

    public Brush Radial(float cx, float cy, float r, params (float At, Color Color)[] stops) =>
        new RadialGradientBrush(P(cx, cy), r * Scale, GradientRepetitionMode.None,
            stops.Select(s => new ColorStop(s.At, s.Color)).ToArray());

    // --- Painting ---------------------------------------------------------------------------

    public void Fill(Color color, IPath path) => ctx.Fill(color, path);

    public void Fill(Brush brush, IPath path) => ctx.Fill(brush, path);

    public void Stroke(Color color, float width, IPath path) => ctx.Draw(color, width * Scale, path);

    public void Line(Color color, float width, params (float X, float Y)[] points) =>
        ctx.DrawLine(color, width * Scale, points.Select(p => P(p.X, p.Y)).ToArray());

    /// <summary>Paints inside <paramref name="region"/> only.</summary>
    public void Clip(IPath region, Action<Canvas> paint) =>
        ctx.Clip(region, inner => paint(new Canvas(inner, Scale)));

    // --- Text -------------------------------------------------------------------------------

    public Font Font(FontFamily family, float size) => family.CreateFont(size * Scale);

    /// <summary>Width of <paramref name="text"/> in layout units.</summary>
    public float Measure(string text, Font font) =>
        TextMeasurer.MeasureAdvance(text, Options(font, 0, 0, HorizontalAlignment.Left, VerticalAlignment.Top)).Width / Scale;

    /// <summary><paramref name="text"/> cut with an ellipsis to fit <paramref name="maxWidth"/> layout units.</summary>
    public string Fit(string text, Font font, float maxWidth)
    {
        if (Measure(text, font) <= maxWidth)
            return text;

        var runes = text.EnumerateRunes().ToList();
        while (runes.Count > 1)
        {
            runes.RemoveAt(runes.Count - 1);
            var candidate = string.Concat(runes.Select(r => r.ToString())).TrimEnd() + "…";
            if (Measure(candidate, font) <= maxWidth)
                return candidate;
        }

        return "…";
    }

    /// <summary>
    /// Draws <paramref name="text"/> at a layout point. <paramref name="bold"/> thickens the strokes
    /// (the embedded fonts carry a single weight); <paramref name="shadow"/> adds a 1-unit drop shadow.
    /// </summary>
    public void Text(
        string text, Font font, Color color, float x, float y,
        HorizontalAlignment h = HorizontalAlignment.Left, VerticalAlignment v = VerticalAlignment.Top,
        Color? shadow = null, float bold = 0)
    {
        if (text.Length == 0)
            return;

        if (shadow is { } s)
            Draw(text, font, s, x + 1, y + 1, h, v, bold);

        Draw(text, font, color, x, y, h, v, bold);
    }

    private void Draw(string text, Font font, Color color, float x, float y, HorizontalAlignment h, VerticalAlignment v, float bold)
    {
        var options = Options(font, x, y, h, v);
        if (bold > 0)
            ctx.DrawText(options, text, Brushes.Solid(color), Pens.Solid(color, bold * Scale));
        else
            ctx.DrawText(options, text, color);
    }

    private RichTextOptions Options(Font font, float x, float y, HorizontalAlignment h, VerticalAlignment v) =>
        new(font)
        {
            Origin = P(x, y),
            HorizontalAlignment = h,
            VerticalAlignment = v,
            FallbackFontFamilies = [CalendarFonts.Body],
        };
}
