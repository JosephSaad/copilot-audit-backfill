using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace CopilotInsights.Charts;

/// <summary>
/// A chart as a PNG image, for email: Outlook and most mail apps do not show SVG. It draws the SVG the pages draw
/// (<see cref="Chart"/>: lines, paths, circles, rectangles and text) in the light theme's colors, at twice its size so
/// it stays sharp, with Windows' own graphics library (GDI+, in every Windows Server). Nothing to install. Where there is
/// no GDI+ (another operating system) or the chart cannot be read, there is no image: the email keeps its figures and
/// its link to the report.
/// </summary>
public static partial class ChartImage
{
    /// <summary>The light theme's colors (site.css :root), by token.</summary>
    static readonly Dictionary<string, uint> Tokens = new(StringComparer.Ordinal)
    {
        ["ground"] = 0xf4f6f5, ["surface"] = 0xffffff, ["ink"] = 0x17212a, ["muted"] = 0x55626d, ["faint"] = 0x7b8791, ["rule"] = 0xdde3e1,
        ["accent"] = 0x0a6b6b, ["accent-soft"] = 0xe0efec, ["grid"] = 0xe8ecea,
        ["s1"] = 0x2a78d6, ["s2"] = 0xeb6834, ["s3"] = 0x1baf7a, ["s4"] = 0xeda100, ["s5"] = 0xe87ba4, ["s6"] = 0x008300, ["s7"] = 0x4a3aa7, ["s8"] = 0xe34948,
        ["q0"] = 0xcde2fb, ["q1"] = 0xb7d3f6, ["q2"] = 0x9ec5f4, ["q3"] = 0x86b6ef, ["q4"] = 0x6da7ec, ["q5"] = 0x3987e5, ["q6"] = 0x256abf, ["q7"] = 0x184f95, ["q8"] = 0x0d366b,
    };

    /// <summary>A color token's RGB, for the HTML around an image (a legend's swatches); the ink color for one not known.</summary>
    public static string Hex(string token) => "#" + (Tokens.TryGetValue(token, out var c) ? c : Tokens["ink"]).ToString("x6", CultureInfo.InvariantCulture);

    /// <summary>The PNG of one chart's SVG, drawn at <paramref name="scale"/> times its size; null where it cannot be drawn.</summary>
    public static byte[]? Png(string svg, float scale = 2f)
    {
        if (!OperatingSystem.IsWindows()) return null;
        XElement root;
        try { root = XElement.Parse(svg, LoadOptions.PreserveWhitespace); }
        catch (System.Xml.XmlException) { return null; }
        var box = ((string?)root.Attribute("viewBox"))?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (box is not { Length: 4 } || !float.TryParse(box[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var w) || !float.TryParse(box[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var h)
            || w is <= 0 or > 4000 || h is <= 0 or > 4000) return null;
        try { return Gdi.Draw(root, w, h, scale); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException or ExternalException or IOException) { return null; }
    }

    /// <summary>The size of the image <see cref="Png"/> draws, in CSS pixels at scale 1: the SVG's view box.</summary>
    public static (int Width, int Height)? Size(string svg)
    {
        var m = ViewBox().Match(svg);
        return m.Success ? ((int)Math.Ceiling(double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)), (int)Math.Ceiling(double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture))) : null;
    }

    [GeneratedRegex("viewBox=\"0 0 ([0-9.]+) ([0-9.]+)\"")]
    private static partial Regex ViewBox();

    /// <summary>How a mark is drawn: its fill and stroke (ARGB, 0 for none), stroke width and dashes, and its text.</summary>
    internal sealed record Style(uint Fill, uint Stroke, float Width, float[]? Dash, float Font, bool Bold, bool Skip);

    static uint Argb(uint rgb, double opacity = 1) => ((uint)Math.Round(255 * Math.Clamp(opacity, 0, 1)) << 24) | (rgb & 0xffffff);

    /// <summary>A mark's style from its class (as site.css draws it) and its own style attribute (a series color).</summary>
    internal static Style StyleOf(XElement e)
    {
        var tag = e.Name.LocalName;
        var classes = (((string?)e.Attribute("class")) ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        var parentClasses = (((string?)e.Parent?.Attribute("class")) ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        uint T(string t) => Argb(Tokens[t]);
        var own = OwnColor((string?)e.Attribute("style"));
        if (classes.Overlaps(["hit", "mhit", "cross"])) return new(0, 0, 0, null, 0, false, true);
        if (tag == "text" || tag == "tspan")
        {
            if (parentClasses.Contains("mbadge")) return new(T("ink"), 0, 0, null, 10, true, false);
            if (classes.Contains("bshown")) return new(T("muted"), 0, 0, null, 11, false, false);
            if (classes.Contains("blabel")) return new(classes.Contains("link") ? T("accent") : T("ink"), 0, 0, null, 12, false, false);
            if (classes.Contains("bvalue")) return new(T("muted"), 0, 0, null, 12, false, false);
            if (classes.Contains("dlabel")) return new(T("muted"), 0, 0, null, 11, false, false);
            return new(T("faint"), 0, 0, null, 11, false, false);   // tick, and any other
        }
        if (parentClasses.Contains("mbadge") && tag == "circle") return new(T("surface"), T("muted"), 1.2f, null, 0, false, false);
        if (classes.Contains("grid") && classes.Contains("now")) return new(0, T("faint"), 1, [2, 3], 0, false, false);
        if (classes.Contains("grid")) return new(0, T("grid"), 1, null, 0, false, false);
        if (classes.Contains("marker")) return new(0, T("faint"), 1, [2, 4], 0, false, false);
        if (classes.Contains("mjoin")) return new(0, T("faint"), 1, null, 0, false, false);
        if (classes.Contains("line")) return new(0, Argb(own ?? Tokens["s1"]), 2, classes.Contains("dashed") ? [6, 4] : null, 0, false, false);
        if (classes.Contains("band")) return new(Argb(own ?? Tokens["s1"], 0.14), 0, 0, null, 0, false, false);
        if (classes.Contains("end")) return new(Argb(own ?? Tokens["s1"]), T("surface"), 2, null, 0, false, false);
        if (classes.Contains("cell")) return new(classes.Contains("none") ? T("ground") : Argb(own ?? Tokens["q0"]), 0, 0, null, 0, false, false);
        if (classes.Contains("mark")) return new(Argb(own ?? Tokens["s1"]), 0, 0, null, 0, false, false);
        return new(own is { } o ? Argb(o) : 0, 0, 0, null, 0, false, false);
    }

    /// <summary>"fill:var(--s3)" or "stroke:var(--q4)": the token's color.</summary>
    static uint? OwnColor(string? style)
    {
        if (style is null) return null;
        var m = Regex.Match(style, @"(?:fill|stroke|background)\s*:\s*var\(--([a-z0-9-]+)\)");
        return m.Success && Tokens.TryGetValue(m.Groups[1].Value, out var c) ? c : null;
    }

    static float F(XElement e, string name, float fallback = 0) =>
        float.TryParse((string?)e.Attribute(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    /// <summary>A path's figures, from the commands the charts write: M, L, H, V, Q (to a cubic) and Z, absolute.</summary>
    internal static List<(char Op, float[] Pts)> Segments(string d)
    {
        var list = new List<(char, float[])>();
        var tokens = Regex.Matches(d, @"[MLHVQZmlhvqz]|-?[0-9]*\.?[0-9]+(?:e-?[0-9]+)?").Select(m => m.Value).ToList();
        float x = 0, y = 0, sx = 0, sy = 0;
        var i = 0;
        float N() => float.Parse(tokens[i++], CultureInfo.InvariantCulture);
        char op = 'M';
        while (i < tokens.Count)
        {
            if (char.IsLetter(tokens[i][0])) op = char.ToUpperInvariant(tokens[i++][0]);
            switch (op)
            {
                case 'M': x = N(); y = N(); sx = x; sy = y; list.Add(('M', [x, y])); op = 'L'; break;
                case 'L': { var nx = N(); var ny = N(); list.Add(('L', [x, y, nx, ny])); x = nx; y = ny; break; }
                case 'H': { var nx = N(); list.Add(('L', [x, y, nx, y])); x = nx; break; }
                case 'V': { var ny = N(); list.Add(('L', [x, y, x, ny])); y = ny; break; }
                case 'Q':
                {
                    float qx = N(), qy = N(), ex = N(), ey = N();
                    list.Add(('C', [x, y, x + 2f / 3 * (qx - x), y + 2f / 3 * (qy - y), ex + 2f / 3 * (qx - ex), ey + 2f / 3 * (qy - ey), ex, ey]));
                    x = ex; y = ey; break;
                }
                case 'Z': list.Add(('Z', [])); x = sx; y = sy; break;
                default: i++; break;
            }
        }
        return list;
    }

    /// <summary>The drawing, through GDI+'s flat API (gdiplus.dll), which System.Drawing itself calls.</summary>
    static class Gdi
    {
        const string Lib = "gdiplus.dll";
        static readonly object Gate = new();
        static IntPtr _token;

        [StructLayout(LayoutKind.Sequential)]
        struct StartupInput { public uint Version; public IntPtr Callback; public int NoThread; public int NoCodecs; }

        [StructLayout(LayoutKind.Sequential)]
        struct RectF { public float X, Y, W, H; }

        [DllImport(Lib)] static extern int GdiplusStartup(out IntPtr token, ref StartupInput input, IntPtr output);
        [DllImport(Lib)] static extern int GdipCreateBitmapFromScan0(int w, int h, int stride, int format, IntPtr scan0, out IntPtr bitmap);
        [DllImport(Lib)] static extern int GdipGetImageGraphicsContext(IntPtr image, out IntPtr graphics);
        [DllImport(Lib)] static extern int GdipSetSmoothingMode(IntPtr g, int mode);
        [DllImport(Lib)] static extern int GdipSetTextRenderingHint(IntPtr g, int hint);
        [DllImport(Lib)] static extern int GdipSetPixelOffsetMode(IntPtr g, int mode);
        [DllImport(Lib)] static extern int GdipScaleWorldTransform(IntPtr g, float sx, float sy, int order);
        [DllImport(Lib)] static extern int GdipGraphicsClear(IntPtr g, uint argb);
        [DllImport(Lib)] static extern int GdipCreatePen1(uint argb, float width, int unit, out IntPtr pen);
        [DllImport(Lib)] static extern int GdipSetPenDashArray(IntPtr pen, float[] dash, int count);
        [DllImport(Lib)] static extern int GdipSetPenLineJoin(IntPtr pen, int join);
        [DllImport(Lib)] static extern int GdipSetPenStartCap(IntPtr pen, int cap);
        [DllImport(Lib)] static extern int GdipSetPenEndCap(IntPtr pen, int cap);
        [DllImport(Lib)] static extern int GdipDeletePen(IntPtr pen);
        [DllImport(Lib)] static extern int GdipCreateSolidFill(uint argb, out IntPtr brush);
        [DllImport(Lib)] static extern int GdipDeleteBrush(IntPtr brush);
        [DllImport(Lib)] static extern int GdipCreatePath(int fillMode, out IntPtr path);
        [DllImport(Lib)] static extern int GdipStartPathFigure(IntPtr path);
        [DllImport(Lib)] static extern int GdipAddPathLine(IntPtr path, float x1, float y1, float x2, float y2);
        [DllImport(Lib)] static extern int GdipAddPathBezier(IntPtr path, float x1, float y1, float x2, float y2, float x3, float y3, float x4, float y4);
        [DllImport(Lib)] static extern int GdipClosePathFigure(IntPtr path);
        [DllImport(Lib)] static extern int GdipFillPath(IntPtr g, IntPtr brush, IntPtr path);
        [DllImport(Lib)] static extern int GdipDrawPath(IntPtr g, IntPtr pen, IntPtr path);
        [DllImport(Lib)] static extern int GdipDeletePath(IntPtr path);
        [DllImport(Lib)] static extern int GdipAddPathRectangle(IntPtr path, float x, float y, float w, float h);
        [DllImport(Lib)] static extern int GdipAddPathEllipse(IntPtr path, float x, float y, float w, float h);
        [DllImport(Lib)] static extern int GdipAddPathArc(IntPtr path, float x, float y, float w, float h, float start, float sweep);
        [DllImport(Lib, CharSet = CharSet.Unicode)] static extern int GdipCreateFontFamilyFromName(string name, IntPtr collection, out IntPtr family);
        [DllImport(Lib)] static extern int GdipGetGenericFontFamilySansSerif(out IntPtr family);
        [DllImport(Lib)] static extern int GdipDeleteFontFamily(IntPtr family);
        [DllImport(Lib)] static extern int GdipCreateFont(IntPtr family, float size, int style, int unit, out IntPtr font);
        [DllImport(Lib)] static extern int GdipDeleteFont(IntPtr font);
        [DllImport(Lib)] static extern int GdipGetCellAscent(IntPtr family, int style, out ushort ascent);
        [DllImport(Lib)] static extern int GdipGetEmHeight(IntPtr family, int style, out ushort em);
        [DllImport(Lib)] static extern int GdipStringFormatGetGenericTypographic(out IntPtr format);
        [DllImport(Lib)] static extern int GdipCloneStringFormat(IntPtr format, out IntPtr clone);
        [DllImport(Lib)] static extern int GdipSetStringFormatFlags(IntPtr format, int flags);
        [DllImport(Lib)] static extern int GdipDeleteStringFormat(IntPtr format);
        [DllImport(Lib, CharSet = CharSet.Unicode)] static extern int GdipDrawString(IntPtr g, string text, int length, IntPtr font, ref RectF layout, IntPtr format, IntPtr brush);
        [DllImport(Lib, CharSet = CharSet.Unicode)] static extern int GdipMeasureString(IntPtr g, string text, int length, IntPtr font, ref RectF layout, IntPtr format, out RectF bound, out int fitted, out int lines);
        [DllImport(Lib)] static extern int GdipDeleteGraphics(IntPtr g);
        [DllImport(Lib)] static extern int GdipDisposeImage(IntPtr image);
        [DllImport(Lib, CharSet = CharSet.Unicode)] static extern int GdipSaveImageToFile(IntPtr image, string file, ref Guid encoder, IntPtr parameters);

        static readonly Guid PngEncoder = new("557cf406-1a04-11d3-9a73-0000f81ef32e");
        const int Argb32 = 0x26200A, UnitWorld = 0, UnitPixel = 2;

        static void Ok(int status, string what)
        {
            if (status != 0) throw new InvalidOperationException($"GDI+ {what} failed ({status}).");
        }

        static void Start()
        {
            lock (Gate)
            {
                if (_token != IntPtr.Zero) return;
                var input = new StartupInput { Version = 1 };
                Ok(GdiplusStartup(out _token, ref input, IntPtr.Zero), "start");
            }
        }

        public static byte[] Draw(XElement root, float w, float h, float scale)
        {
            Start();
            int pw = (int)Math.Ceiling(w * scale), ph = (int)Math.Ceiling(h * scale);
            IntPtr bmp = IntPtr.Zero, g = IntPtr.Zero, typo = IntPtr.Zero, format = IntPtr.Zero;
            var families = new Dictionary<bool, IntPtr>();
            var fonts = new Dictionary<(float, bool), IntPtr>();
            var file = Path.Combine(Path.GetTempPath(), "insights-chart-" + Guid.NewGuid().ToString("N") + ".png");
            try
            {
                Ok(GdipCreateBitmapFromScan0(pw, ph, 0, Argb32, IntPtr.Zero, out bmp), "bitmap");
                Ok(GdipGetImageGraphicsContext(bmp, out g), "graphics");
                GdipSetSmoothingMode(g, 4);        // anti-aliased
                GdipSetPixelOffsetMode(g, 4);      // half-pixel: crisp one-pixel rules
                GdipSetTextRenderingHint(g, 4);    // anti-aliased text, not ClearType: the image is shown on any screen
                GdipScaleWorldTransform(g, scale, scale, 0);
                GdipGraphicsClear(g, Argb(Tokens["surface"]));
                Ok(GdipStringFormatGetGenericTypographic(out typo), "format");
                Ok(GdipCloneStringFormat(typo, out format), "format");
                GdipSetStringFormatFlags(format, 0x1000 | 0x4000 | 0x0800);   // no wrap, no clip, measure trailing spaces

                IntPtr Family(bool bold)
                {
                    if (families.TryGetValue(bold, out var f)) return f;
                    if (GdipCreateFontFamilyFromName("Segoe UI", IntPtr.Zero, out f) != 0) Ok(GdipGetGenericFontFamilySansSerif(out f), "font family");
                    return families[bold] = f;
                }
                IntPtr Font(float size, bool bold)
                {
                    if (fonts.TryGetValue((size, bold), out var f)) return f;
                    Ok(GdipCreateFont(Family(bold), size, bold ? 1 : 0, UnitPixel, out f), "font");
                    return fonts[(size, bold)] = f;
                }
                float Ascent(float size, bool bold)
                {
                    GdipGetCellAscent(Family(bold), bold ? 1 : 0, out var a);
                    GdipGetEmHeight(Family(bold), bold ? 1 : 0, out var em);
                    return em == 0 ? size * 0.8f : size * a / em;
                }
                float Measure(string text, float size, bool bold)
                {
                    var layout = new RectF { W = 10000, H = 1000 };
                    return GdipMeasureString(g, text, text.Length, Font(size, bold), ref layout, format, out var b, out _, out _) == 0 ? b.W : text.Length * size * 0.55f;
                }

                foreach (var e in root.Descendants())
                {
                    if (e.Name.LocalName is "title" or "tspan") continue;
                    if (e.Ancestors().Any(a => a.Name.LocalName == "title")) continue;
                    var st = StyleOf(e);
                    if (st.Skip) continue;
                    switch (e.Name.LocalName)
                    {
                        case "line":
                            Stroke(g, st, p => GdipAddPathLine(p, F(e, "x1"), F(e, "y1"), F(e, "x2"), F(e, "y2")));
                            break;
                        case "path":
                        {
                            var segs = Segments((string?)e.Attribute("d") ?? "");
                            void Build(IntPtr p)
                            {
                                foreach (var (op, pts) in segs)
                                    switch (op)
                                    {
                                        case 'M': GdipStartPathFigure(p); break;
                                        case 'L': GdipAddPathLine(p, pts[0], pts[1], pts[2], pts[3]); break;
                                        case 'C': GdipAddPathBezier(p, pts[0], pts[1], pts[2], pts[3], pts[4], pts[5], pts[6], pts[7]); break;
                                        case 'Z': GdipClosePathFigure(p); break;
                                    }
                            }
                            Fill(g, st, Build);
                            Stroke(g, st, Build);
                            break;
                        }
                        case "circle":
                        {
                            float cx = F(e, "cx"), cy = F(e, "cy"), r = F(e, "r");
                            Fill(g, st, p => GdipAddPathEllipse(p, cx - r, cy - r, 2 * r, 2 * r));
                            Stroke(g, st, p => GdipAddPathEllipse(p, cx - r, cy - r, 2 * r, 2 * r));
                            break;
                        }
                        case "rect":
                        {
                            float x = F(e, "x"), y = F(e, "y"), rw = F(e, "width"), rh = F(e, "height"), rx = Math.Min(F(e, "rx"), Math.Min(rw, rh) / 2);
                            void Rect(IntPtr p)
                            {
                                if (rx <= 0) { GdipAddPathRectangle(p, x, y, rw, rh); return; }
                                GdipAddPathArc(p, x, y, 2 * rx, 2 * rx, 180, 90);
                                GdipAddPathArc(p, x + rw - 2 * rx, y, 2 * rx, 2 * rx, 270, 90);
                                GdipAddPathArc(p, x + rw - 2 * rx, y + rh - 2 * rx, 2 * rx, 2 * rx, 0, 90);
                                GdipAddPathArc(p, x, y + rh - 2 * rx, 2 * rx, 2 * rx, 90, 90);
                                GdipClosePathFigure(p);
                            }
                            if (rw > 0 && rh > 0) { Fill(g, st, Rect); Stroke(g, st, Rect); }
                            break;
                        }
                        case "text":
                            DrawText(e, st);
                            break;
                    }
                }

                /* A text's lines: each tspan with an x starts one (moved down by its dy), one without continues the line
                   in its own style (a bar's "2 of 2" after its value). The anchor places the whole line. */
                void DrawText(XElement text, Style st)
                {
                    var anchor = (string?)text.Attribute("text-anchor") ?? "start";
                    float x = F(text, "x"), y = F(text, "y");
                    var lines = new List<(float X, float Y, List<(string Text, Style S)> Runs)>();
                    var current = (X: x, Y: y, Runs: new List<(string, Style)>());
                    lines.Add(current);
                    foreach (var node in text.Nodes())
                    {
                        if (node is XText t) { if (t.Value.Length > 0) current.Runs.Add((t.Value, st)); continue; }
                        if (node is not XElement span || span.Name.LocalName != "tspan") continue;
                        /* A tspan with a class of its own is drawn in it; one without, as its text. */
                        var spanStyle = span.Attribute("class") is null ? st : StyleOf(span);
                        if (span.Attribute("x") is not null)
                        {
                            var dy = F(span, "dy");
                            current = (F(span, "x"), current.Runs.Count == 0 && lines.Count == 1 ? y + dy : current.Y + dy, new List<(string, Style)>());
                            if (lines.Count == 1 && lines[0].Runs.Count == 0) lines[0] = current; else lines.Add(current);
                        }
                        if (span.Value.Length > 0) current.Runs.Add((span.Value, spanStyle));
                    }
                    foreach (var (lx, ly, runs) in lines)
                    {
                        if (runs.Count == 0) continue;
                        var widths = runs.Select(r => Measure(r.Text, r.S.Font, r.S.Bold)).ToList();
                        var total = widths.Sum();
                        var at = anchor switch { "middle" => lx - total / 2, "end" => lx - total, _ => lx };
                        for (var i = 0; i < runs.Count; i++)
                        {
                            var (s, rs) = runs[i];
                            IntPtr brush = IntPtr.Zero;
                            try
                            {
                                Ok(GdipCreateSolidFill(rs.Fill == 0 ? Argb(Tokens["faint"]) : rs.Fill, out brush), "brush");
                                var layout = new RectF { X = at, Y = ly - Ascent(rs.Font, rs.Bold), W = 0, H = 0 };
                                GdipDrawString(g, s, s.Length, Font(rs.Font, rs.Bold), ref layout, format, brush);
                            }
                            finally { if (brush != IntPtr.Zero) GdipDeleteBrush(brush); }
                            at += widths[i];
                        }
                    }
                }

                Ok(GdipSaveImageToFile(bmp, file, ref Codec.Png, IntPtr.Zero), "save");
                return File.ReadAllBytes(file);
            }
            finally
            {
                foreach (var f in fonts.Values) GdipDeleteFont(f);
                foreach (var f in families.Values) GdipDeleteFontFamily(f);
                if (format != IntPtr.Zero) GdipDeleteStringFormat(format);
                if (typo != IntPtr.Zero) GdipDeleteStringFormat(typo);
                if (g != IntPtr.Zero) GdipDeleteGraphics(g);
                if (bmp != IntPtr.Zero) GdipDisposeImage(bmp);
                try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        /// <summary>The PNG encoder's id, passed by reference.</summary>
        static class Codec
        {
            public static Guid Png = PngEncoder;
        }

        static void Fill(IntPtr g, Style st, Action<IntPtr> build)
        {
            if (st.Fill >> 24 == 0) return;
            IntPtr path = IntPtr.Zero, brush = IntPtr.Zero;
            try
            {
                Ok(GdipCreatePath(0, out path), "path");
                build(path);
                Ok(GdipCreateSolidFill(st.Fill, out brush), "brush");
                GdipFillPath(g, brush, path);
            }
            finally
            {
                if (brush != IntPtr.Zero) GdipDeleteBrush(brush);
                if (path != IntPtr.Zero) GdipDeletePath(path);
            }
        }

        static void Stroke(IntPtr g, Style st, Action<IntPtr> build)
        {
            if (st.Stroke >> 24 == 0 || st.Width <= 0) return;
            IntPtr path = IntPtr.Zero, pen = IntPtr.Zero;
            try
            {
                Ok(GdipCreatePath(0, out path), "path");
                build(path);
                Ok(GdipCreatePen1(st.Stroke, st.Width, UnitWorld, out pen), "pen");
                GdipSetPenLineJoin(pen, 2);
                GdipSetPenStartCap(pen, st.Dash is null ? 2 : 0);
                GdipSetPenEndCap(pen, st.Dash is null ? 2 : 0);
                /* GDI+ dashes are in pen widths; the SVG's are in units. */
                if (st.Dash is { } d) GdipSetPenDashArray(pen, [.. d.Select(x => x / st.Width)], d.Length);
                GdipDrawPath(g, pen, path);
            }
            finally
            {
                if (pen != IntPtr.Zero) GdipDeletePen(pen);
                if (path != IntPtr.Zero) GdipDeletePath(path);
            }
        }
    }
}
