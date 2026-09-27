using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Html;

namespace CopilotInsights.Charts;

public enum Fmt { Count, Percent, Decimal1, Days }

/// <summary>One series. Slot is the categorical colour (1-8, fixed per entity, never by rank).</summary>
public sealed record Series(string Name, IReadOnlyList<double?> Values, int Slot);

public sealed record Bar(string Label, double? Value, string? Href = null, string? Note = null, int Slot = 1);

/// <summary>An event on a trend: a rollout wave, a training push, an outage (dash.EventMarker), at the point it falls in.</summary>
public sealed record Marker(int Index, string Label, string Kind);

/// <summary>A trend's labels, with the events that fall in it. Line and Columns draw the events as dashed rules.</summary>
public sealed class Axis(IEnumerable<string> labels) : List<string>(labels)
{
    public List<Marker> Markers { get; } = [];
}

/// <summary>
/// Server-rendered SVG charts. Colours come from CSS variables (--s1..--s8,
/// --q0..--q8), so light and dark themes swap in site.css. Every mark carries
/// data-tip, which site.js shows as a tooltip; text uses the ink colours,
/// never a series colour.
/// </summary>
public static class Chart
{
    static readonly CultureInfo Ci = CultureInfo.InvariantCulture;
    const int W = 720, H = 250, L = 52, R = 110, T = 14, B = 30;

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
        var every = Math.Max(1, (int)Math.Ceiling(x.Count / 8.0));
        for (var i = 0; i < x.Count; i++)
            if ((i % every == 0 && (i == x.Count - 1 || x.Count - 1 - i >= Math.Max(2, every * 0.6))) || i == x.Count - 1)
            {
                /* Names (departments, units) can be longer than the space between ticks: cut to fit, full name on hover. */
                var room = x.Count > 1 ? Math.Max(4, (int)((xpos(1) - xpos(0)) * every / 6.2) - 1) : 40;
                var (attr, body) = Cut(x[i], room);
                sb.Append($"<text class=\"tick\" x=\"{N(xpos(i))}\" y=\"{H - 10}\" text-anchor=\"middle\"{attr}>{body}</text>");
            }
    }

    /// <summary>The events on the axis, as dashed rules with a short label and a tooltip.</summary>
    static void Markers(StringBuilder sb, IReadOnlyList<string> x, Func<int, double> xpos)
    {
        if (x is not Axis a || a.Markers.Count == 0) return;
        var rows = new List<double>();   // the right end of the last label on each row
        foreach (var g in a.Markers.Where(m => m.Index >= 0 && m.Index < x.Count).GroupBy(m => m.Index))
        {
            var xx = xpos(g.Key);
            var tip = x[g.Key] + ": " + string.Join("; ", g.Select(m => $"{m.Label} ({m.Kind.ToLowerInvariant()})"));
            var more = g.Count() > 1 ? $" +{g.Count() - 1}" : "";
            var shown = Math.Min(g.First().Label.Length, 16) + more.Length;
            var row = rows.FindIndex(end => end < xx);
            if (row < 0) { rows.Add(0); row = rows.Count - 1; }
            rows[row] = xx + 3 + shown * 5.5 + 6;
            sb.Append($"<line class=\"marker\" x1=\"{N(xx)}\" x2=\"{N(xx)}\" y1=\"{T}\" y2=\"{H - B}\"/>");
            sb.Append($"<rect class=\"mhit\" x=\"{N(xx - 4)}\" y=\"{T}\" width=\"8\" height=\"{H - T - B}\" data-tip=\"{E(tip)}\"/>");
            var label = Trunc(g.First().Label, 16) + more;
            sb.Append($"<text class=\"mlabel\" x=\"{N(xx + 3)}\" y=\"{T + 9 + row * 13}\" data-tip=\"{E(tip)}\">{E(label)}<title>{E(tip)}</title></text>");
        }
    }

    public static HtmlString Legend(IEnumerable<Series> series) =>
        new("<div class=\"legend\">" + string.Concat(series.Select(s =>
            $"<span><i class=\"sw\" style=\"background:var(--s{s.Slot})\"></i>{E(s.Name)}</span>")) + "</div>");

    /// <summary>Lines over the weeks, one axis from zero. Direct labels at the line ends for up to four series.</summary>
    public static HtmlString Line(IReadOnlyList<string> x, IReadOnlyList<Series> series, Fmt f, string aria, double? max = null)
    {
        if (x.Count == 0 || series.All(s => s.Values.All(v => v is null))) return new(Empty("No data for this period."));
        var top0 = max ?? series.SelectMany(s => s.Values).Where(v => v.HasValue).Select(v => v!.Value).DefaultIfEmpty(0).Max();
        var (top, step) = Nice(top0, f);
        double X(int i) => x.Count == 1 ? (L + W - R) / 2.0 : L + i * (W - R - L) / (double)(x.Count - 1);
        double Y(double v) => T + (H - T - B) * (1 - v / top);
        var sb = new StringBuilder($"<svg class=\"chart\" viewBox=\"0 0 {W} {H}\" role=\"img\" aria-label=\"{E(aria)}\">");
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
                var (attr, body) = Cut(s.Name, NameRoom(val), " " + val);
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
        Markers(sb, x, X);
        sb.Append($"<line class=\"cross\" x1=\"0\" x2=\"0\" y1=\"{T}\" y2=\"{H - B}\"/></svg>");
        return new((series.Count >= 2 ? Legend(series).Value : "") + sb);
    }

    /// <summary>
    /// History and its projection on one axis: each history series solid, its
    /// forecast dashed from the last actual point with a shaded 95% band, and a
    /// line where history ends. <paramref name="x"/> covers history then forecast.
    /// </summary>
    public static HtmlString Projection(IReadOnlyList<string> x, int historyCount,
        IReadOnlyList<(Series History, IReadOnlyList<double> Forecast, IReadOnlyList<double> Low, IReadOnlyList<double> High)> lines,
        Fmt f, string aria)
    {
        if (x.Count == 0 || lines.Count == 0 || lines.All(l => l.History.Values.All(v => v is null))) return new(Empty("Not enough history to forecast yet."));
        var top0 = lines.SelectMany(l => l.History.Values.Where(v => v.HasValue).Select(v => v!.Value).Concat(l.High)).DefaultIfEmpty(0).Max();
        var (top, step) = Nice(top0, f);
        double X(int i) => x.Count == 1 ? (L + W - R) / 2.0 : L + i * (W - R - L) / (double)(x.Count - 1);
        double Y(double v) => T + (H - T - B) * (1 - Math.Min(v, top) / top);
        var sb = new StringBuilder($"<svg class=\"chart\" viewBox=\"0 0 {W} {H}\" role=\"img\" aria-label=\"{E(aria)}\">");
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
            var (endAttr, endBody) = Cut(s.Name, NameRoom(endVal), " " + endVal);
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
    public static HtmlString Columns(IReadOnlyList<string> x, IReadOnlyList<Series> series, Fmt f, string aria, bool stacked = true)
    {
        if (x.Count == 0 || series.All(s => s.Values.All(v => v is null or 0))) return new(Empty("Nothing in this period."));
        var totals = Enumerable.Range(0, x.Count).Select(i => stacked
            ? series.Sum(s => s.Values.ElementAtOrDefault(i) ?? 0)
            : series.Max(s => s.Values.ElementAtOrDefault(i) ?? 0)).ToList();
        var (top, step) = Nice(totals.DefaultIfEmpty(0).Max(), f);
        var band = (W - R + 40 - L) / (double)x.Count;
        var bw = Math.Max(3, band * 0.72);
        double X0(int i) => L + i * band + (band - bw) / 2;
        double Y(double v) => T + (H - T - B) * (1 - v / top);
        var sb = new StringBuilder($"<svg class=\"chart\" viewBox=\"0 0 {W} {H}\" role=\"img\" aria-label=\"{E(aria)}\">");
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
        Markers(sb, x, i => X0(i) + bw / 2);
        sb.Append("</svg>");
        return new((series.Count >= 2 ? Legend(series).Value : "") + sb);
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
    public static HtmlString Bars(IReadOnlyList<Bar> bars, Fmt f, string aria, double? max = null)
    {
        if (bars.Count == 0) return new(Empty("Nothing to compare."));
        var top = max ?? bars.Max(b => b.Value ?? 0);
        var low = Math.Min(0, bars.Min(b => b.Value ?? 0));   // a change can be negative: bars grow left from zero
        if (top <= 0 && low >= 0) top = 1;
        top = Math.Max(0, top);
        const int rowH = 26, labelW = 190, valueW = 70, BW = 720;
        var span = BW - labelW - valueW - (low < 0 ? valueW : 0);
        var zero = labelW + (low < 0 ? valueW : 0) + span * (-low) / (top - low);
        var h = bars.Count * rowH + 6;
        var sb = new StringBuilder($"<svg class=\"chart bars\" viewBox=\"0 0 {BW} {h}\" role=\"img\" aria-label=\"{E(aria)}\">");
        if (low < 0) sb.Append($"<line class=\"grid\" x1=\"{N(zero)}\" x2=\"{N(zero)}\" y1=\"0\" y2=\"{h}\"/>");
        for (var i = 0; i < bars.Count; i++)
        {
            var b = bars[i];
            var y = i * rowH + 4;
            var v = b.Value ?? 0;
            var w = span * Math.Abs(v) / (top - low);
            var (attr, label) = Cut(b.Label, 30);
            sb.Append(b.Href is null
                ? $"<text class=\"blabel\" x=\"{labelW - 10}\" y=\"{y + 15}\" text-anchor=\"end\"{attr}>{label}</text>"
                : $"<a href=\"{E(b.Href)}\"><text class=\"blabel link\" x=\"{labelW - 10}\" y=\"{y + 15}\" text-anchor=\"end\"{attr}>{label}</text></a>");
            var tip = E(b.Label + ": " + Format(b.Value, f) + (b.Note is null ? "" : " — " + b.Note));
            if (w > 0.5)
            {
                var r = Math.Min(4, w / 2);
                var bh = rowH - 10;
                /* The data end (away from zero) is rounded; the zero end is square. */
                var d = v >= 0
                    ? $"M{N(zero)},{y} H{N(zero + w - r)} Q{N(zero + w)},{y} {N(zero + w)},{N(y + r)} V{N(y + bh - r)} Q{N(zero + w)},{N(y + bh)} {N(zero + w - r)},{N(y + bh)} H{N(zero)} Z"
                    : $"M{N(zero)},{y} H{N(zero - w + r)} Q{N(zero - w)},{y} {N(zero - w)},{N(y + r)} V{N(y + bh - r)} Q{N(zero - w)},{N(y + bh)} {N(zero - w + r)},{N(y + bh)} H{N(zero)} Z";
                sb.Append($"<path class=\"mark\" d=\"{d}\" style=\"fill:var(--s{b.Slot})\" data-tip=\"{tip}\"/>");
            }
            sb.Append(v >= 0
                ? $"<text class=\"bvalue\" x=\"{N(zero + w + 6)}\" y=\"{y + 15}\">{E(Format(b.Value, f))}</text>"
                : $"<text class=\"bvalue\" x=\"{N(zero - w - 6)}\" y=\"{y + 15}\" text-anchor=\"end\">{E(Format(b.Value, f))}</text>");
        }
        sb.Append("</svg>");
        return new(sb.ToString());
    }

    /// <summary>A grid of cells shaded on the blue sequential ramp (q0 lightest to q8 darkest).</summary>
    public static HtmlString Heatmap(IReadOnlyList<string> rows, IReadOnlyList<string> cols, double?[,] values, Fmt f, string aria, double? max = null)
    {
        if (rows.Count == 0 || cols.Count == 0) return new(Empty("No data for this period."));
        var top = max ?? values.Cast<double?>().Where(v => v.HasValue).Select(v => v!.Value).DefaultIfEmpty(0).Max();
        const int rowH = 24, labelW = 110, HW = 720;
        var cw = (HW - labelW - 8) / (double)cols.Count;
        var h = rows.Count * rowH + 24;
        var sb = new StringBuilder($"<svg class=\"chart heat\" viewBox=\"0 0 {HW} {h}\" role=\"img\" aria-label=\"{E(aria)}\">");
        var every = Math.Max(1, (int)Math.Ceiling(cols.Count / 12.0));
        for (var c = 0; c < cols.Count; c++)
            if (c % every == 0)
                sb.Append($"<text class=\"tick\" x=\"{N(labelW + c * cw + cw / 2)}\" y=\"14\" text-anchor=\"middle\">{E(cols[c])}</text>");
        for (var r = 0; r < rows.Count; r++)
        {
            var y = 20 + r * rowH;
            var (rowAttr, rowBody) = Cut(rows[r], 16);
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
        const double gap = 14, lo = T + 10, hi = H - B + 12;
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
