using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Html;

namespace CopilotInsights.Charts;

public enum Fmt { Count, Percent, Decimal1, Days }

/// <summary>One series. Slot is the categorical color (1-8, fixed per entity, never by rank).</summary>
public sealed record Series(string Name, IReadOnlyList<double?> Values, int Slot);

/// <summary>A bar: Note goes in its tooltip; Shown is printed beside its value (2.4.5), such as "2 of 2" under a rate.</summary>
public sealed record Bar(string Label, double? Value, string? Href = null, string? Note = null, int Slot = 1, string? Shown = null);

/// <summary>
/// A chart that can draw itself again at another width. Drawn at 720 units by default; a page
/// draws one in a half-width panel at a narrower width, so its text comes out the same size as
/// in a full-width panel (the SVG scales to its panel).
/// </summary>
public sealed class ChartSvg(string value, Func<string> draw) : HtmlString(value)
{
    public string Render(int width) => Chart.At(width, draw);
}

/// <summary>An event on a trend: a rollout wave, a training push, an outage (dash.EventMarker), at the point it falls in, and its day.</summary>
public sealed record Marker(int Index, string Label, string Kind, DateTime? Day = null);

/// <summary>
/// A trend's labels, with the events that fall in it. Line and Columns draw each event as a numbered badge above
/// the plot and a dashed rule through it, with a key under the chart: nothing is written over the data (2.4.5).
/// </summary>
public sealed class Axis(IEnumerable<string> labels) : List<string>(labels)
{
    public List<Marker> Markers { get; } = [];
}

/// <summary>
/// Server-rendered SVG charts. Colors come from CSS variables (--s1..--s8,
/// --q0..--q8), so light and dark themes swap in site.css. Every mark carries
/// data-tip, which site.js shows as a tooltip; text uses the ink colors,
/// never a series color.
/// </summary>
public static class Chart
{
    static readonly CultureInfo Ci = CultureInfo.InvariantCulture;
    const int L = 52;
    [ThreadStatic] static int _w;
    /// <summary>The drawing width in viewBox units: 720, or the panel's (see ChartSvg).</summary>
    static int W => _w == 0 ? 720 : _w;
    /// <summary>No chart is drawn more than 1.3 times its units, so its text stays near its set size however wide the panel.</summary>
    static string MaxWidth => $" style=\"max-width:{N(W * 1.3)}px\"";

    internal static string At(int width, Func<string> draw)
    {
        var before = _w;
        _w = width;
        try { return draw(); }
        finally { _w = before; }
    }

    static HtmlString Svg(Func<HtmlString> draw)
    {
        var first = draw();
        return first.Value is { } v && v.Contains("<svg class=\"chart") ? new ChartSvg(v, () => draw().Value ?? "") : first;
    }

    /*
        The right margin (line-end labels), bottom margin (axis labels) and height are set per chart from
        the labels it draws, so every label is shown whole: nothing on a page is cut short with "…".
        Charts render synchronously, so a thread's layout belongs to the chart being drawn.
    */
    [ThreadStatic] static int _r, _b, _h, _t;
    static int R => _r == 0 ? 110 : _r;
    /// <summary>The top margin: 14, or 34 with a strip for event badges above the plot.</summary>
    static int T => _t == 0 ? 14 : _t;
    static int B => _b == 0 ? 30 : _b;
    static int H => _h == 0 ? 250 : _h;

    sealed class Layout : IDisposable
    {
        public Layout(int r = 110, int b = 30, int t = 14) { _r = r; _b = b; _t = t; _h = 220 + b + (t - 14); }
        public void Dispose() { _r = 0; _b = 0; _h = 0; _t = 0; }
    }

    /// <summary>Approximate width of a label at 11-12px, in viewBox units.</summary>
    static double TextW(string s) => s.Length * 6.3;

    /// <summary>Words packed into lines of at most <paramref name="n"/> characters (a word longer than that keeps its own line).</summary>
    static List<string> WrapLines(string s, int n)
    {
        /* Break at spaces, and inside a long word after / _ - . (a site address, a schema name) so no line outruns its room. */
        var pieces = new List<(string Text, bool Space)>();
        foreach (var w in s.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (w.Length <= n) { pieces.Add((w, true)); continue; }
            var parts = System.Text.RegularExpressions.Regex.Split(w, "(?<=[/_\\-\\.])");
            for (var i = 0; i < parts.Length; i++)
                if (parts[i].Length > 0) pieces.Add((parts[i], i == 0));
        }
        var lines = new List<string>();
        var cur = "";
        foreach (var (text, space) in pieces)
        {
            var join = cur.Length == 0 ? text : cur + (space ? " " : "") + text;
            if (cur.Length == 0 || join.Length <= n) cur = join;
            else { lines.Add(cur); cur = text; }
        }
        if (cur.Length > 0) lines.Add(cur);
        return lines.Count == 0 ? [""] : lines;
    }

    /// <summary>Lines as tspans under one text element, the first at its y.</summary>
    static string Tspans(IReadOnlyList<string> lines, double x, double lineH = 11) =>
        string.Concat(lines.Select((l, i) => $"<tspan x=\"{N(x)}\" dy=\"{(i == 0 ? 0 : lineH)}\">{E(l)}</tspan>"));

    /// <summary>The axis labels drawn for a trend: which points, and the lines each wraps to.</summary>
    static List<(int I, List<string> Lines)> AxisLabels(IReadOnlyList<string> x, double spacing)
    {
        var every = Math.Max(1, (int)Math.Ceiling(x.Count / 8.0));
        /* Fewer labels where even wrapped they would run into each other (2.4.10: a year of weeks and a year of forecast
           in a half-width chart drew "w/e 22 May" over its neighbor). A word does not break, so the widest line decides. */
        int Room(int e) => Math.Max(6, (int)(spacing * e / 6.3) - 1);
        while (every < x.Count)
        {
            var e = every;
            var widest = Enumerable.Range(0, x.Count).Where(i => i % e == 0 || i == x.Count - 1).Max(i => WrapLines(x[i], Room(e)).Max(l => l.Length));
            if ((widest + 1) * 6.3 <= spacing * every) break;
            every++;
        }
        var room = Room(every);
        var list = new List<(int, List<string>)>();
        for (var i = 0; i < x.Count; i++)
            if ((i % every == 0 && (i == x.Count - 1 || x.Count - 1 - i >= Math.Max(2, every * 0.85))) || i == x.Count - 1)
                list.Add((i, WrapLines(x[i], room)));
        return list;
    }

    /// <summary>The bottom margin the axis labels need: one line is the usual 30.</summary>
    static int BottomFor(IReadOnlyList<string> x, double plotWidth)
    {
        if (x.Count == 0) return 30;
        var lines = AxisLabels(x, plotWidth / Math.Max(1, x.Count - 1)).Max(l => l.Lines.Count);
        return 30 + (Math.Min(lines, 4) - 1) * 11;
    }

    public static string Format(double? v, Fmt f) => v is null ? "—" : f switch
    {
        Fmt.Percent => v.Value.ToString("0.#%", CultureInfo.CurrentCulture),
        Fmt.Decimal1 => v.Value.ToString("#,0.0", CultureInfo.CurrentCulture),
        Fmt.Days => v.Value.ToString("#,0.#", CultureInfo.CurrentCulture) + " d",
        _ => v.Value.ToString("#,0", CultureInfo.CurrentCulture),
    };

    static string E(string s) => WebUtility.HtmlEncode(s);
    static string N(double d) => d.ToString("0.##", Ci);

    /// <summary>A round axis top and step for a maximum.</summary>
    static (double top, double step) Nice(double max, Fmt f)
    {
        if (f == Fmt.Percent) return max <= 0.5 ? (0.5, 0.1) : (1.0, 0.25);
        if (max <= 0) return (4, 1);
        var raw = max / 4;
        var mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        var step = new[] { 1, 2, 2.5, 5, 10 }.Select(m => m * mag).First(s => s >= raw);
        return (Math.Ceiling(max / step) * step, step);
    }

    static string Empty(string text) =>
        $"<div class=\"empty-chart\">{E(text)}</div>";

    static void Grid(StringBuilder sb, double top, double step, Fmt f, Func<double, double> y)
    {
        for (double v = 0; v <= top + step / 2; v += step)
        {
            var yy = y(v);
            sb.Append($"<line class=\"grid\" x1=\"{L}\" x2=\"{W - R}\" y1=\"{N(yy)}\" y2=\"{N(yy)}\"/>");
            sb.Append($"<text class=\"tick\" x=\"{L - 8}\" y=\"{N(yy + 4)}\" text-anchor=\"end\">{E(Format(v, f))}</text>");
        }
    }

    static void XLabels(StringBuilder sb, IReadOnlyList<string> x, Func<int, double> xpos)
    {
        /* Names (departments, units) longer than the space between ticks wrap onto more lines; the chart is taller for them. */
        var spacing = x.Count > 1 ? xpos(1) - xpos(0) : 400;
        foreach (var (i, lines) in AxisLabels(x, spacing))
            sb.Append($"<text class=\"tick\" x=\"{N(xpos(i))}\" y=\"{H - B + 20}\" text-anchor=\"middle\">{Tspans(lines.Take(4).ToList(), xpos(i))}</text>");
    }

    /// <summary>The top margin a trend needs: room for the event badges when it has any.</summary>
    static int TopFor(IReadOnlyList<string> x) => x is Axis a && a.Markers.Any(m => m.Index >= 0 && m.Index < x.Count) ? 34 : 14;

    /// <summary>
    /// The events on the axis (2.4.5): a numbered badge each in the strip above the plot, a light dashed rule
    /// through the plot where it falls, and the key under the chart, which this returns. Events at one point
    /// share a badge; badges too close to tell apart step to the right, joined to their rule by a short line.
    /// </summary>
    static string Markers(StringBuilder sb, IReadOnlyList<string> x, Func<int, double> xpos)
    {
        if (x is not Axis a) return "";
        var groups = a.Markers.Where(m => m.Index >= 0 && m.Index < x.Count).GroupBy(m => m.Index).OrderBy(g => g.Key).ToList();
        if (groups.Count == 0) return "";
        var key = new StringBuilder("<ol class=\"mkey\">");
        var last = double.MinValue;
        var by = T - 15;
        for (var n = 0; n < groups.Count; n++)
        {
            var g = groups[n];
            var xx = xpos(g.Key);
            var bx = Math.Min(Math.Max(xx, last + 19), W - 10);
            last = bx;
            string Day(Marker m) => m.Day?.ToString("d MMM", Ci) ?? x[g.Key];
            var tip = string.Join("; ", g.Select(m => $"{Day(m)}: {m.Label} ({m.Kind.ToLowerInvariant()})"));
            sb.Append($"<line class=\"marker\" x1=\"{N(xx)}\" x2=\"{N(xx)}\" y1=\"{T}\" y2=\"{H - B}\"/>");
            if (Math.Abs(bx - xx) > 0.5) sb.Append($"<line class=\"mjoin\" x1=\"{N(bx)}\" y1=\"{by + 8}\" x2=\"{N(xx)}\" y2=\"{T}\"/>");
            sb.Append($"<rect class=\"mhit\" x=\"{N(xx - 4)}\" y=\"{T}\" width=\"8\" height=\"{H - T - B}\" data-tip=\"{E(tip)}\"/>");
            sb.Append($"<g class=\"mbadge\" data-tip=\"{E(tip)}\"><title>{E(tip)}</title><circle cx=\"{N(bx)}\" cy=\"{by}\" r=\"8\"/>" +
                      $"<text x=\"{N(bx)}\" y=\"{by + 3.5}\" text-anchor=\"middle\">{n + 1}</text></g>");
            key.Append($"<li><span class=\"k\">{n + 1}</span>{E(string.Join(" · ", g.Select(m => $"{Day(m)} {m.Label}")))}</li>");
        }
        return key.Append("</ol>").ToString();
    }

    /// <summary>One group in an adoption ranking: licensed people who used Copilot, of those licensed.</summary>
    public sealed record AdoptionGroup(string Name, int Active, int Licensed, string? Href = null, string? Note = null);

    /// <summary>
    /// Adoption ranked across groups (2.4.5): only groups with at least <paramref name="min"/> license holders are
    /// ranked, highest first and, at the same rate, the larger first; the rest are one bar at the end, so no bar
    /// rests on one or two people. Each bar shows its "a of b" beside the rate.
    /// </summary>
    public static HtmlString Adoption(IEnumerable<AdoptionGroup> groups, int min, string aria, int take = 20)
    {
        var list = groups.Where(g => g.Licensed > 0).ToList();
        var bars = list.Where(g => g.Licensed >= min)
            .OrderByDescending(g => g.Active / (double)g.Licensed).ThenByDescending(g => g.Licensed).Take(take)
            .Select(g => new Bar(g.Name, g.Active / (double)g.Licensed, g.Href, g.Note ?? $"{g.Active:#,0} of {g.Licensed:#,0} licensed", Shown: $"{g.Active:#,0} of {g.Licensed:#,0}"))
            .ToList();
        var few = list.Where(g => g.Licensed < min).ToList();
        if (few.Count > 0)
        {
            int a = few.Sum(g => g.Active), l = few.Sum(g => g.Licensed);
            bars.Add(new Bar($"{few.Count:#,0} with fewer than {min} license holders, combined", a / (double)l, Note: $"{a:#,0} of {l:#,0} licensed, across {few.Count:#,0}", Slot: 2, Shown: $"{a:#,0} of {l:#,0}"));
        }
        return Bars(bars, Fmt.Percent, aria, 1);
    }

    public static HtmlString Legend(IEnumerable<Series> series) =>
        new("<div class=\"legend\">" + string.Concat(series.Select(s =>
            $"<span><i class=\"sw\" style=\"background:var(--s{s.Slot})\"></i>{E(s.Name)}</span>")) + "</div>");

    /// <summary>Lines over the weeks, one axis from zero. Direct labels at the line ends for up to four series.</summary>
    public static HtmlString Line(IReadOnlyList<string> x, IReadOnlyList<Series> series, Fmt f, string aria, double? max = null) =>
        Svg(() => LineAt(x, series, f, aria, max));

    static HtmlString LineAt(IReadOnlyList<string> x, IReadOnlyList<Series> series, Fmt f, string aria, double? max)
    {
        if (x.Count == 0 || series.All(s => s.Values.All(v => v is null))) return new(Empty("No data for this period."));
        var top0 = max ?? series.SelectMany(s => s.Values).Where(v => v.HasValue).Select(v => v!.Value).DefaultIfEmpty(0).Max();
        var (top, step) = Nice(top0, f);
        /* Drawn narrow (a phone, 2.4.10) with a legend above it, a line's end carries its value alone: the name is in the
           legend, and beside the line it took half the chart. */
        var compact = W <= 380 && series.Count is >= 2 and <= 4;
        var endLabel = series.Count > 4 ? 0 : series.Select(s => TextW((compact ? "" : s.Name + " ") + Format(s.Values.LastOrDefault(v => v.HasValue), f))).DefaultIfEmpty(0).Max();
        var r = (int)Math.Clamp(endLabel + 16, compact ? 48 : 110, Math.Max(110, W * 0.42));
        using var layout = new Layout(r, BottomFor(x, W - r - L), TopFor(x));
        double X(int i) => x.Count == 1 ? (L + W - R) / 2.0 : L + i * (W - R - L) / (double)(x.Count - 1);
        double Y(double v) => T + (H - T - B) * (1 - v / top);
        var sb = new StringBuilder($"<svg class=\"chart\" viewBox=\"0 0 {W} {H}\"{MaxWidth} role=\"img\" aria-label=\"{E(aria)}\">");
        Grid(sb, top, step, f, Y);
        XLabels(sb, x, X);
        var labelYs = new List<double>();
        foreach (var s in series)
        {
            var pts = s.Values.Select((v, i) => (v, i)).Where(p => p.v.HasValue).ToList();
            if (pts.Count == 0) continue;
            var d = string.Join(" ", pts.Select((p, j) => $"{(j == 0 ? "M" : "L")}{N(X(p.i))},{N(Y(p.v!.Value))}"));
            sb.Append($"<path class=\"line\" d=\"{d}\" style=\"stroke:var(--s{s.Slot})\"/>");
            var last = pts[^1];
            sb.Append($"<circle class=\"end\" cx=\"{N(X(last.i))}\" cy=\"{N(Y(last.v!.Value))}\" r=\"4\" style=\"fill:var(--s{s.Slot})\"/>");
            if (series.Count <= 4)
            {
                var ly = Place(labelYs, Y(last.v!.Value) + 4);
                var val = Format(last.v, f);
                var (attr, body) = compact ? ("", E(val)) : Cut(s.Name, Math.Max(NameRoom(val), 60), " " + val);
                sb.Append($"<text class=\"dlabel\" x=\"{N(X(last.i) + 9)}\" y=\"{N(ly)}\"{attr}>{body}</text>");
            }
        }
        /* Hover: one column per week carrying every series' value. */
        for (var i = 0; i < x.Count; i++)
        {
            var tip = x[i] + ": " + string.Join("; ", series.Select(s => $"{s.Name} {Format(s.Values.ElementAtOrDefault(i), f)}"));
            var w = x.Count == 1 ? 60 : (W - R - L) / (double)(x.Count - 1);
            sb.Append($"<rect class=\"hit\" x=\"{N(X(i) - w / 2)}\" y=\"{T}\" width=\"{N(w)}\" height=\"{H - T - B}\" data-tip=\"{E(tip)}\" data-x=\"{N(X(i))}\"/>");
        }
        var events = Markers(sb, x, X);
        sb.Append($"<line class=\"cross\" x1=\"0\" x2=\"0\" y1=\"{T}\" y2=\"{H - B}\"/></svg>");
        return new((series.Count >= 2 ? Legend(series).Value : "") + sb + events);
    }

    /// <summary>
    /// History and its projection on one axis: each history series solid, its
    /// forecast dashed from the last actual point with a shaded 95% band, and a
    /// line where history ends. <paramref name="x"/> covers history then forecast.
    /// </summary>
    public static HtmlString Projection(IReadOnlyList<string> x, int historyCount,
        IReadOnlyList<(Series History, IReadOnlyList<double> Forecast, IReadOnlyList<double> Low, IReadOnlyList<double> High)> lines,
        Fmt f, string aria) => Svg(() => ProjectionAt(x, historyCount, lines, f, aria));

    static HtmlString ProjectionAt(IReadOnlyList<string> x, int historyCount,
        IReadOnlyList<(Series History, IReadOnlyList<double> Forecast, IReadOnlyList<double> Low, IReadOnlyList<double> High)> lines,
        Fmt f, string aria)
    {
        if (x.Count == 0 || lines.Count == 0 || lines.All(l => l.History.Values.All(v => v is null))) return new(Empty("Not enough history to forecast yet."));
        var top0 = lines.SelectMany(l => l.History.Values.Where(v => v.HasValue).Select(v => v!.Value).Concat(l.High)).DefaultIfEmpty(0).Max();
        var (top, step) = Nice(top0, f);
        var compact = W <= 380 && lines.Count >= 2;
        var endLabel = lines.Select(l => TextW((compact ? "" : l.History.Name + " ") + Format(l.Forecast.Count > 0 ? l.Forecast[^1] : l.History.Values.LastOrDefault(v => v.HasValue), f))).DefaultIfEmpty(0).Max();
        var r = (int)Math.Clamp(endLabel + 16, compact ? 48 : 110, Math.Max(110, W * 0.42));
        using var layout = new Layout(r, BottomFor(x, W - r - L));
        double X(int i) => x.Count == 1 ? (L + W - R) / 2.0 : L + i * (W - R - L) / (double)(x.Count - 1);
        double Y(double v) => T + (H - T - B) * (1 - Math.Min(v, top) / top);
        var sb = new StringBuilder($"<svg class=\"chart\" viewBox=\"0 0 {W} {H}\"{MaxWidth} role=\"img\" aria-label=\"{E(aria)}\">");
        Grid(sb, top, step, f, Y);
        XLabels(sb, x, X);
        var now = X(historyCount - 1);
        sb.Append($"<line class=\"grid now\" x1=\"{N(now)}\" x2=\"{N(now)}\" y1=\"{T}\" y2=\"{H - B}\"/>");
        sb.Append($"<text class=\"tick\" x=\"{N(now + 4)}\" y=\"{T + 10}\">forecast</text>");
        var labelYs = new List<double>();
        foreach (var (s, fc, lo, hi) in lines)
        {
            var pts = s.Values.Select((v, i) => (v, i)).Where(p => p.v.HasValue).ToList();
            if (pts.Count == 0) continue;
            var last = pts[^1];
            /* the band: along the interval's top, and back along its bottom */
            var band = new StringBuilder($"M{N(X(last.i))},{N(Y(last.v!.Value))}");
            for (var h = 0; h < hi.Count; h++) band.Append($" L{N(X(historyCount + h))},{N(Y(hi[h]))}");
            for (var h = lo.Count - 1; h >= 0; h--) band.Append($" L{N(X(historyCount + h))},{N(Y(lo[h]))}");
            sb.Append($"<path class=\"band\" d=\"{band} Z\" style=\"fill:var(--s{s.Slot})\"/>");
            var d = string.Join(" ", pts.Select((p, j) => $"{(j == 0 ? "M" : "L")}{N(X(p.i))},{N(Y(p.v!.Value))}"));
            sb.Append($"<path class=\"line\" d=\"{d}\" style=\"stroke:var(--s{s.Slot})\"/>");
            var fd = $"M{N(X(last.i))},{N(Y(last.v!.Value))} " + string.Join(" ", fc.Select((v, h) => $"L{N(X(historyCount + h))},{N(Y(v))}"));
            sb.Append($"<path class=\"line dashed\" d=\"{fd}\" style=\"stroke:var(--s{s.Slot})\"/>");
            var endV = fc.Count > 0 ? fc[^1] : last.v!.Value;
            var endX = fc.Count > 0 ? X(historyCount + fc.Count - 1) : X(last.i);
            sb.Append($"<circle class=\"end\" cx=\"{N(endX)}\" cy=\"{N(Y(endV))}\" r=\"4\" style=\"fill:var(--s{s.Slot})\"/>");
            var ly = Place(labelYs, Y(endV) + 4);
            var endVal = Format(endV, f);
            var (endAttr, endBody) = compact ? ("", E(endVal)) : Cut(s.Name, Math.Max(NameRoom(endVal), 60), " " + endVal);
            sb.Append($"<text class=\"dlabel\" x=\"{N(endX + 9)}\" y=\"{N(ly)}\"{endAttr}>{endBody}</text>");
        }
        for (var i = 0; i < x.Count; i++)
        {
            var parts = lines.Select(l => i < historyCount
                ? $"{l.History.Name} {Format(l.History.Values.ElementAtOrDefault(i), f)}"
                : $"{l.History.Name} {Format(l.Forecast.ElementAtOrDefault(i - historyCount), f)} (95%: {Format(l.Low.ElementAtOrDefault(i - historyCount), f)} to {Format(l.High.ElementAtOrDefault(i - historyCount), f)})");
            var tip = x[i] + (i < historyCount ? ": " : " (forecast): ") + string.Join("; ", parts);
            var w = x.Count == 1 ? 60 : (W - R - L) / (double)(x.Count - 1);
            sb.Append($"<rect class=\"hit\" x=\"{N(X(i) - w / 2)}\" y=\"{T}\" width=\"{N(w)}\" height=\"{H - T - B}\" data-tip=\"{E(tip)}\" data-x=\"{N(X(i))}\"/>");
        }
        sb.Append($"<line class=\"cross\" x1=\"0\" x2=\"0\" y1=\"{T}\" y2=\"{H - B}\"/></svg>");
        return new((lines.Count >= 2 ? Legend(lines.Select(l => l.History)).Value : "") + sb);
    }

    /// <summary>Columns per week, stacked (parts of a whole) or grouped (side by side).</summary>
    public static HtmlString Columns(IReadOnlyList<string> x, IReadOnlyList<Series> series, Fmt f, string aria, bool stacked = true) =>
        Svg(() => ColumnsAt(x, series, f, aria, stacked));

    static HtmlString ColumnsAt(IReadOnlyList<string> x, IReadOnlyList<Series> series, Fmt f, string aria, bool stacked)
    {
        if (x.Count == 0 || series.All(s => s.Values.All(v => v is null or 0))) return new(Empty("Nothing in this period."));
        var totals = Enumerable.Range(0, x.Count).Select(i => stacked
            ? series.Sum(s => s.Values.ElementAtOrDefault(i) ?? 0)
            : series.Max(s => s.Values.ElementAtOrDefault(i) ?? 0)).ToList();
        var (top, step) = Nice(totals.DefaultIfEmpty(0).Max(), f);
        using var layout = new Layout(110, BottomFor(x, (W - 110 + 40 - L) / (double)x.Count * Math.Max(1, x.Count - 1)), TopFor(x));
        var band = (W - R + 40 - L) / (double)x.Count;
        var bw = Math.Max(3, band * 0.72);
        double X0(int i) => L + i * band + (band - bw) / 2;
        double Y(double v) => T + (H - T - B) * (1 - v / top);
        var sb = new StringBuilder($"<svg class=\"chart\" viewBox=\"0 0 {W} {H}\"{MaxWidth} role=\"img\" aria-label=\"{E(aria)}\">");
        Grid(sb, top, step, f, Y);
        XLabels(sb, x, i => X0(i) + bw / 2);
        for (var i = 0; i < x.Count; i++)
        {
            double acc = 0;
            var sub = stacked ? bw : Math.Max(2, (bw - 2 * (series.Count - 1)) / series.Count);
            for (var k = 0; k < series.Count; k++)
            {
                var v = series[k].Values.ElementAtOrDefault(i) ?? 0;
                if (v <= 0) continue;
                var x0 = stacked ? X0(i) : X0(i) + k * (sub + 2);
                var y0 = stacked ? Y(acc + v) : Y(v);
                var y1 = stacked ? Y(acc) : Y(0);
                var isTop = !stacked || series.Skip(k + 1).All(s => (s.Values.ElementAtOrDefault(i) ?? 0) <= 0);
                var h = Math.Max(0, y1 - y0 - (stacked && acc > 0 ? 2 : 0));
                sb.Append(Rect(x0, y0, sub, h, isTop, $"var(--s{series[k].Slot})",
                    $"{x[i]}: {series[k].Name} {Format(v, f)}"));
                acc += v;
            }
        }
        var events = Markers(sb, x, i => X0(i) + bw / 2);
        sb.Append("</svg>");
        return new((series.Count >= 2 ? Legend(series).Value : "") + sb + events);
    }

    /// <summary>A rectangle with its data end (the top) rounded 4px, anchored square at the baseline.</summary>
    static string Rect(double x, double y, double w, double h, bool roundTop, string fill, string tip)
    {
        if (h <= 0) return "";
        var r = roundTop ? Math.Min(4, Math.Min(w / 2, h)) : 0;
        var d = r > 0
            ? $"M{N(x)},{N(y + h)} V{N(y + r)} Q{N(x)},{N(y)} {N(x + r)},{N(y)} H{N(x + w - r)} Q{N(x + w)},{N(y)} {N(x + w)},{N(y + r)} V{N(y + h)} Z"
            : $"M{N(x)},{N(y + h)} V{N(y)} H{N(x + w)} V{N(y + h)} Z";
        return $"<path class=\"mark\" d=\"{d}\" style=\"fill:{fill}\" data-tip=\"{E(tip)}\"/>";
    }

    /// <summary>Horizontal bars with labels and values, for rankings and comparisons.</summary>
    public static HtmlString Bars(IReadOnlyList<Bar> bars, Fmt f, string aria, double? max = null) =>
        Svg(() => BarsAt(bars, f, aria, max));

    static HtmlString BarsAt(IReadOnlyList<Bar> bars, Fmt f, string aria, double? max)
    {
        if (bars.Count == 0) return new(Empty("Nothing to compare."));
        var top = max ?? bars.Max(b => b.Value ?? 0);
        var low = Math.Min(0, bars.Min(b => b.Value ?? 0));   // a change can be negative: bars grow left from zero
        if (top <= 0 && low >= 0) top = 1;
        top = Math.Max(0, top);
        /* The value column fits the widest value and what is shown beside it. */
        var valueW = (int)Math.Clamp(bars.Max(b => TextW(Format(b.Value, f) + (b.Shown is null ? "" : "  " + b.Shown))) + 12, 70, 200);
        var BW = W;
        /* The label column fits the longest name up to 300; longer names wrap onto a second line. */
        var labelW = (int)Math.Clamp(bars.Max(b => b.Label.Length * 6.9) + 18, Math.Min(190, BW * 0.3), BW * 0.42);
        var perLine = Math.Max(10, (int)((labelW - 18) / 6.9));
        var wrapped = bars.Select(b => WrapLines(b.Label, perLine)).ToList();
        /* Up to three lines of name; drawn narrow (a phone), where a line holds little, up to five (2.4.10). */
        var maxLines = BW <= 380 ? 5 : 3;
        var lineCount = Math.Min(maxLines, wrapped.Max(w => w.Count));
        var rowH = lineCount switch { 1 => 26, 2 => 36, 3 => 48, _ => 48 + (lineCount - 3) * 12 };
        var span = BW - labelW - valueW - (low < 0 ? valueW : 0);
        var zero = labelW + (low < 0 ? valueW : 0) + span * (-low) / (top - low);
        var h = bars.Count * rowH + 6;
        var sb = new StringBuilder($"<svg class=\"chart bars\" viewBox=\"0 0 {BW} {h}\"{MaxWidth} role=\"img\" aria-label=\"{E(aria)}\">");
        if (low < 0) sb.Append($"<line class=\"grid\" x1=\"{N(zero)}\" x2=\"{N(zero)}\" y1=\"0\" y2=\"{h}\"/>");
        for (var i = 0; i < bars.Count; i++)
        {
            var b = bars[i];
            var y = i * rowH + 4;
            var v = b.Value ?? 0;
            var w = span * Math.Abs(v) / (top - low);
            var lines = wrapped[i].Take(maxLines).ToList();
            if (wrapped[i].Count > maxLines) lines[maxLines - 1] = string.Concat(wrapped[i].Skip(maxLines - 1));   // never cut: the rest joins the last line
            var ly = y + 15 - (lines.Count - 1) * 6;
            var text = Tspans(lines, labelW - 10, 12);
            sb.Append(b.Href is null
                ? $"<text class=\"blabel\" x=\"{labelW - 10}\" y=\"{ly}\" text-anchor=\"end\">{text}</text>"
                : $"<a href=\"{E(b.Href)}\"><text class=\"blabel link\" x=\"{labelW - 10}\" y=\"{ly}\" text-anchor=\"end\">{text}</text></a>");
            var tip = E(b.Label + ": " + Format(b.Value, f) + (b.Note is null ? "" : " — " + b.Note));
            if (w > 0.5)
            {
                var r = Math.Min(4, w / 2);
                var bh = 16;
                /* The data end (away from zero) is rounded; the zero end is square. */
                var d = v >= 0
                    ? $"M{N(zero)},{y} H{N(zero + w - r)} Q{N(zero + w)},{y} {N(zero + w)},{N(y + r)} V{N(y + bh - r)} Q{N(zero + w)},{N(y + bh)} {N(zero + w - r)},{N(y + bh)} H{N(zero)} Z"
                    : $"M{N(zero)},{y} H{N(zero - w + r)} Q{N(zero - w)},{y} {N(zero - w)},{N(y + r)} V{N(y + bh - r)} Q{N(zero - w)},{N(y + bh)} {N(zero - w + r)},{N(y + bh)} H{N(zero)} Z";
                sb.Append($"<path class=\"mark\" d=\"{d}\" style=\"fill:var(--s{b.Slot})\" data-tip=\"{tip}\"/>");
            }
            var shown = b.Shown is null ? "" : $"<tspan class=\"bshown\">  {E(b.Shown)}</tspan>";
            sb.Append(v >= 0
                ? $"<text class=\"bvalue\" x=\"{N(zero + w + 6)}\" y=\"{y + 15}\">{E(Format(b.Value, f))}{shown}</text>"
                : $"<text class=\"bvalue\" x=\"{N(zero - w - 6)}\" y=\"{y + 15}\" text-anchor=\"end\">{E(Format(b.Value, f))}{shown}</text>");
        }
        sb.Append("</svg>");
        return new(sb.ToString());
    }

    /// <summary>A grid of cells shaded on the blue sequential ramp (q0 lightest to q8 darkest).</summary>
    public static HtmlString Heatmap(IReadOnlyList<string> rows, IReadOnlyList<string> cols, double?[,] values, Fmt f, string aria, double? max = null) =>
        Svg(() => HeatmapAt(rows, cols, values, f, aria, max));

    static HtmlString HeatmapAt(IReadOnlyList<string> rows, IReadOnlyList<string> cols, double?[,] values, Fmt f, string aria, double? max)
    {
        if (rows.Count == 0 || cols.Count == 0) return new(Empty("No data for this period."));
        var top = max ?? values.Cast<double?>().Where(v => v.HasValue).Select(v => v!.Value).DefaultIfEmpty(0).Max();
        const int rowH = 24;
        var HW = W;
        var labelW = (int)Math.Clamp(rows.Max(r => TextW(r)) + 14, 110, Math.Max(110, HW * 0.36));
        var cw = (HW - labelW - 8) / (double)cols.Count;
        var h = rows.Count * rowH + 24;
        var sb = new StringBuilder($"<svg class=\"chart heat\" viewBox=\"0 0 {HW} {h}\"{MaxWidth} role=\"img\" aria-label=\"{E(aria)}\">");
        /* A label over every so many columns: twelve across at most, and fewer where the columns are narrower than a label (2.4.10). */
        var every = Math.Max(Math.Max(1, (int)Math.Ceiling(cols.Count / 12.0)), (int)Math.Ceiling((cols.Max(c => TextW(c)) + 6) / Math.Max(1, cw)));
        for (var c = 0; c < cols.Count; c++)
            if (c % every == 0)
                sb.Append($"<text class=\"tick\" x=\"{N(labelW + c * cw + cw / 2)}\" y=\"14\" text-anchor=\"middle\">{E(cols[c])}</text>");
        for (var r = 0; r < rows.Count; r++)
        {
            var y = 20 + r * rowH;
            var (rowAttr, rowBody) = Cut(rows[r], 60);
            sb.Append($"<text class=\"blabel\" x=\"{labelW - 8}\" y=\"{y + 15}\" text-anchor=\"end\"{rowAttr}>{rowBody}</text>");
            for (var c = 0; c < cols.Count; c++)
            {
                var v = values[r, c];
                var q = v is null || top <= 0 ? -1 : Math.Min(8, (int)Math.Floor(v.Value / top * 8.999));
                var cls = q < 0 ? "cell none" : $"cell";
                var fill = q < 0 ? "" : $" style=\"fill:var(--q{q})\"";
                sb.Append($"<rect class=\"{cls}\" x=\"{N(labelW + c * cw + 1)}\" y=\"{y}\" width=\"{N(cw - 2)}\" height=\"{rowH - 2}\" rx=\"2\"{fill} data-tip=\"{E($"{rows[r]}, {cols[c]}: {Format(v, f)}")}\"/>");
            }
        }
        sb.Append("</svg>");
        var scale = "<div class=\"scale\"><span>" + E(Format(0, f)) + "</span>" +
                    string.Concat(Enumerable.Range(0, 9).Select(i => $"<i style=\"background:var(--q{i})\"></i>")) +
                    "<span>" + E(Format(top, f)) + "</span></div>";
        return new(sb + scale);
    }

    /// <summary>A small line for a table cell.</summary>
    public static HtmlString Sparkline(IReadOnlyList<double?> values, string tip)
    {
        var pts = values.Select((v, i) => (v, i)).Where(p => p.v.HasValue).ToList();
        if (pts.Count < 2) return new("");
        var max = pts.Max(p => p.v!.Value); if (max <= 0) max = 1;
        const int SW = 96, SH = 22;
        double X(int i) => 2 + i * (SW - 4) / (double)Math.Max(1, values.Count - 1);
        double Y(double v) => SH - 3 - (SH - 6) * v / max;
        var d = string.Join(" ", pts.Select((p, j) => $"{(j == 0 ? "M" : "L")}{N(X(p.i))},{N(Y(p.v!.Value))}"));
        return new($"<svg class=\"spark\" viewBox=\"0 0 {SW} {SH}\" data-tip=\"{E(tip)}\"><path d=\"{d}\"/></svg>");
    }

    static string Trunc(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";

    /// <summary>
    /// A label cut to <paramref name="n"/> characters: the attributes to put on its &lt;text&gt; and the text itself.
    /// When it is cut, the full label shows on hover (data-tip for the site's tooltip, &lt;title&gt; for everything else).
    /// </summary>
    static (string Attr, string Body) Cut(string s, int n, string suffix = "")
    {
        if (s.Length <= n) return ("", E(s) + suffix);
        return ($" data-tip=\"{E(s + suffix)}\"", E(Trunc(s, n)) + suffix + $"<title>{E(s + suffix)}</title>");
    }

    /// <summary>
    /// Where a line-end label goes: as near its line as it can without touching another label,
    /// trying below then above, and never outside the chart.
    /// </summary>
    static double Place(List<double> used, double want)
    {
        const double gap = 14;
        double lo = T + 10;
        double hi = H - B - 2;   // above the axis labels
        want = Math.Clamp(want, lo, hi);
        for (var step = 0; step < 30; step++)
            foreach (var y in new[] { want + step * gap, want - step * gap })
                if (y >= lo && y <= hi && !used.Any(o => Math.Abs(o - y) < gap)) { used.Add(y); return y; }
        used.Add(want);
        return want;
    }

    /// <summary>How many characters of a series name fit beside its end value in the right margin (about 6px a character at 11px).</summary>
    static int NameRoom(string value) => Math.Max(4, (R - 12) / 6 - value.Length - 1);
}
