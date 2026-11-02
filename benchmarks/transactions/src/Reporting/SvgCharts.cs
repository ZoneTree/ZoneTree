using System.Globalization;
using System.Net;
using System.Text;
using Transactions.Benchmark.Engines;

namespace Transactions.Benchmark.Reporting;

/// <summary>
/// The value of one engine at one point of a chart.
/// </summary>
public sealed record ChartSeries(string EngineId, IReadOnlyDictionary<string, double> Values);

/// <summary>
/// Static charts for Markdown reports. An image cannot read the page's theme or show
/// hover tooltips, so each chart paints its own surface, follows the viewer's color scheme
/// through a media query, and sits above a table with the same numbers.
/// </summary>
public static class SvgCharts
{
  const int ChartWidth = 900;
  const double SidePadding = 24;
  const double TitleY = 32;
  const double SubtitleY = 54;
  const double LegendY = 80;
  const double LegendRowHeight = 22;

  // Slots validated for adjacent series in both modes; the engine catalog's order assigns
  // them, so an engine keeps its color when others are filtered out. Each engine family has
  // its own color range: ZoneTree blues, RocksDB oranges, then SQLite. In ZoneTree, async is
  // the darker shade of each isolation level's pair. All four stay apart from each other.
  const string Style =
      """
      <style>
        svg {
          --surface: #fcfcfb; --ink: #0b0b0b; --ink2: #52514e; --muted: #898781; --grid: #e1e0d9; --axis: #c3c2b7;
          --s1: #5a40c9; --s2: #0c77a4; --s3: #678afd; --s4: #15c1d5; --s5: #c44206; --s6: #ee8d04; --s7: #ad238a;
          --s8: #253d95; --s9: #2693d5; --s10: #9e5416; --s11: #d35b9f;
          font-family: system-ui, -apple-system, "Segoe UI", sans-serif;
        }
        @media (prefers-color-scheme: dark) {
          svg {
            --surface: #1a1a19; --ink: #f0efec; --ink2: #c3c2b7; --muted: #898781; --grid: #2c2c2a; --axis: #383835;
            --s1: #6838c1; --s2: #006e9c; --s3: #4283fe; --s4: #0da4a1; --s5: #a53603; --s6: #db7605; --s7: #ad1c81;
            --s8: #8a9fff; --s9: #48b7ef; --s10: #dfae76; --s11: #f79acb;
          }
        }
        .surface { fill: var(--surface); }
        .title { fill: var(--ink); font-size: 18px; font-weight: 600; }
        .subtitle { fill: var(--ink2); font-size: 13px; }
        .label { fill: var(--ink2); font-size: 13px; }
        .value { fill: var(--ink2); font-size: 12px; font-variant-numeric: tabular-nums; }
        .tick { fill: var(--muted); font-size: 12px; font-variant-numeric: tabular-nums; }
        .grid { stroke: var(--grid); stroke-width: 1; }
        .axis { stroke: var(--axis); stroke-width: 1; }
        .line { fill: none; stroke-width: 2; stroke-linejoin: round; stroke-linecap: round; }
        .ring { stroke: var(--surface); stroke-width: 2; }
        .f1 { fill: var(--s1); } .f2 { fill: var(--s2); } .f3 { fill: var(--s3); } .f4 { fill: var(--s4); } .f5 { fill: var(--s5); } .f6 { fill: var(--s6); } .f7 { fill: var(--s7); }
        .c1 { stroke: var(--s1); } .c2 { stroke: var(--s2); } .c3 { stroke: var(--s3); } .c4 { stroke: var(--s4); } .c5 { stroke: var(--s5); } .c6 { stroke: var(--s6); } .c7 { stroke: var(--s7); }
        .f8 { fill: var(--s8); } .f9 { fill: var(--s9); } .f10 { fill: var(--s10); } .f11 { fill: var(--s11); }
        .c8 { stroke: var(--s8); } .c9 { stroke: var(--s9); } .c10 { stroke: var(--s10); } .c11 { stroke: var(--s11); }
      </style>
      """;

  static string DisplayName(string engineId) =>
      EngineCatalog.All.First(x => x.Id == engineId).DisplayName;

  /// <summary>
  /// A measure over the worker counts, one line per engine. Values that span a wide range
  /// use a log axis, because one fast engine would otherwise flatten the others to zero.
  /// </summary>
  public static string Lines(
      string title,
      string subtitle,
      string xCaption,
      IReadOnlyList<string> xLabels,
      IReadOnlyList<ChartSeries> series,
      Func<double, string> formatValue,
      bool allowLogAxis)
  {
    var values = series.SelectMany(x => x.Values.Values).Where(x => x > 0).ToArray();
    var max = values.Length > 0 ? values.Max() : 1;
    var min = values.Length > 0 ? values.Min() : 1;
    var useLog = allowLogAxis && max / min >= 20;
    if (useLog)
      subtitle += " Log scale.";

    var legendRows = LegendRows(series.Select(x => x.EngineId).ToArray());
    var plotTop = LegendY + legendRows.Count * LegendRowHeight + 8;
    const double plotHeight = 280;
    var plotLeft = SidePadding + 52;
    var plotRight = ChartWidth - SidePadding - 12;
    var plotBottom = plotTop + plotHeight;
    var height = (int)(plotBottom + 56);

    var canvas = new Canvas(ChartWidth, height, title, subtitle);
    canvas.Legend(legendRows);

    IReadOnlyList<double> ticks;
    Func<double, double> y;
    if (useLog)
    {
      var low = Math.Floor(Math.Log10(min));
      var high = Math.Ceiling(Math.Log10(max));
      if (high == low)
        high = low + 1;
      ticks = Enumerable.Range((int)low, (int)(high - low) + 1).Select(x => Math.Pow(10, x)).ToArray();
      y = value => plotBottom - (Math.Log10(Math.Max(value, ticks[0])) - low) / (high - low) * plotHeight;
    }
    else
    {
      ticks = NiceTicks(max);
      var top = ticks[^1];
      y = value => plotBottom - value / top * plotHeight;
    }

    for (var i = 0; i < ticks.Count; i++)
    {
      var tickY = y(ticks[i]);
      canvas.Line(plotLeft, tickY, plotRight, tickY, i == 0 ? "axis" : "grid");
      canvas.Text(plotLeft - 8, tickY + 4, formatValue(ticks[i]), "tick", "end");
    }

    double X(int index) =>
        xLabels.Count == 1
            ? (plotLeft + plotRight) / 2
            : plotLeft + 28 + index * (plotRight - plotLeft - 56) / (xLabels.Count - 1);

    for (var i = 0; i < xLabels.Count; i++)
      canvas.Text(X(i), plotBottom + 20, xLabels[i], "tick", "middle");
    canvas.Text((plotLeft + plotRight) / 2, plotBottom + 40, xCaption, "tick", "middle");

    foreach (var line in series)
    {
      var slot = EngineCatalog.ColorSlot(line.EngineId);
      var points = new List<(double X, double Y, string Label)>();
      for (var i = 0; i < xLabels.Count; i++)
      {
        if (line.Values.TryGetValue(xLabels[i], out var value))
          points.Add((X(i), y(useLog ? Math.Max(value, ticks[0]) : value), $"{DisplayName(line.EngineId)}, {xLabels[i]}: {formatValue(value)}"));
      }
      canvas.Polyline(points.Select(p => (p.X, p.Y)).ToArray(), slot);
      foreach (var point in points)
        canvas.Marker(point.X, point.Y, slot, point.Label);
    }
    return canvas.Build();
  }

  /// <summary>
  /// The latency distribution of each engine on a shared log axis.
  /// The rows name the engines, so the chart needs no legend.
  /// </summary>
  public static string LatencyRanges(
      string title,
      string subtitle,
      IReadOnlyList<(string EngineId, double P50, double P95, double P99, double P999)> rows)
  {
    const double rowHeight = 36;
    const double top = 76;
    const double labelWidth = 225;
    const double valueWidth = 190;
    var plotLeft = SidePadding + labelWidth;
    var plotRight = ChartWidth - SidePadding - valueWidth;
    var plotBottom = top + rows.Count * rowHeight;
    var canvas = new Canvas(ChartWidth, (int)(plotBottom + 40), title, subtitle);

    var low = Math.Floor(Math.Log10(Math.Max(rows.Min(x => x.P50), 0.01)));
    var high = Math.Ceiling(Math.Log10(Math.Max(rows.Max(x => x.P999), 0.01)));
    if (high == low)
      high = low + 1;
    double X(double value) =>
        plotLeft + (Math.Log10(Math.Max(value, Math.Pow(10, low))) - low) / (high - low) * (plotRight - plotLeft);

    for (var decade = low; decade <= high; decade++)
    {
      var x = X(Math.Pow(10, decade));
      canvas.Line(x, top - 6, x, plotBottom, decade == low ? "axis" : "grid");
      canvas.Text(x, plotBottom + 20, Format.Microseconds(Math.Pow(10, decade)), "tick", "middle");
    }

    for (var i = 0; i < rows.Count; i++)
    {
      var row = rows[i];
      var slot = EngineCatalog.ColorSlot(row.EngineId);
      var y = top + i * rowHeight + rowHeight / 2;
      canvas.Text(SidePadding, y + 4, DisplayName(row.EngineId), "label", "start");
      canvas.Polyline([(X(row.P50), y), (X(row.P999), y)], slot);
      canvas.Marker(X(row.P50), y, slot, $"p50: {Format.Microseconds(row.P50)}");
      canvas.Marker(X(row.P95), y, slot, $"p95: {Format.Microseconds(row.P95)}");
      canvas.Marker(X(row.P99), y, slot, $"p99: {Format.Microseconds(row.P99)}");
      canvas.Marker(X(row.P999), y, slot, $"p99.9: {Format.Microseconds(row.P999)}");
      canvas.Text(plotRight + 14, y + 4, $"p50 {Format.Microseconds(row.P50)} · p99 {Format.Microseconds(row.P99)}", "value", "start");
    }
    return canvas.Build();
  }

  /// <summary>
  /// Horizontal bars per group, each group scaled to its own best engine, so groups with
  /// very different rates can share a chart. The table beside it has the absolute numbers.
  /// </summary>
  public static string RelativeBars(
      string title,
      string subtitle,
      IReadOnlyList<(string Group, IReadOnlyList<(string EngineId, double Value, string Display)> Bars)> groups)
  {
    const double barHeight = 10;
    const double barStep = 12;
    const double groupGap = 16;
    const double labelWidth = 216;
    var engines = groups.SelectMany(x => x.Bars.Select(b => b.EngineId)).Distinct()
        .OrderBy(EngineCatalog.ColorSlot).ToArray();
    var legendRows = LegendRows(engines);
    var plotTop = LegendY + legendRows.Count * LegendRowHeight + 8;
    var plotLeft = SidePadding + labelWidth;
    var plotRight = ChartWidth - SidePadding - 12;
    var plotWidth = plotRight - plotLeft;
    var plotHeight = groups.Sum(x => Math.Max(x.Bars.Count, 1) * barStep + groupGap);
    var plotBottom = plotTop + plotHeight;

    var canvas = new Canvas(ChartWidth, (int)(plotBottom + 32), title, subtitle);
    canvas.Legend(legendRows);
    foreach (var percent in new[] { 0, 50, 100 })
    {
      var x = plotLeft + plotWidth * percent / 100;
      canvas.Line(x, plotTop, x, plotBottom, percent == 0 ? "axis" : "grid");
      canvas.Text(x, plotBottom + 18, $"{percent}%", "tick", "middle");
    }

    var y = plotTop + groupGap / 2;
    foreach (var (group, bars) in groups)
    {
      var best = bars.Count > 0 ? bars.Max(x => x.Value) : 0;
      var groupHeight = Math.Max(bars.Count, 1) * barStep;
      canvas.Text(plotLeft - 12, y + groupHeight / 2 + 2, group, "label", "end");
      var barY = y;
      foreach (var (engineId, value, display) in bars)
      {
        if (best > 0 && value > 0)
        {
          canvas.Bar(
              plotLeft + 1,
              barY,
              Math.Max(plotWidth * value / best - 1, 2),
              barHeight,
              EngineCatalog.ColorSlot(engineId),
              $"{DisplayName(engineId)}: {display}");
        }
        barY += barStep;
      }
      y += groupHeight + groupGap;
    }
    return canvas.Build();
  }

  static List<IReadOnlyList<string>> LegendRows(IReadOnlyList<string> engines)
  {
    var rows = new List<IReadOnlyList<string>>();
    var row = new List<string>();
    var width = 0.0;
    foreach (var engine in engines)
    {
      var entryWidth = LegendEntryWidth(engine);
      if (row.Count > 0 && width + entryWidth > ChartWidth - 2 * SidePadding)
      {
        rows.Add(row);
        row = [];
        width = 0;
      }
      row.Add(engine);
      width += entryWidth;
    }
    if (row.Count > 0)
      rows.Add(row);
    return rows;
  }

  static double LegendEntryWidth(string engine) => 34 + DisplayName(engine).Length * 7;

  static IReadOnlyList<double> NiceTicks(double max)
  {
    if (max <= 0)
      return [0, 1];
    var rough = max / 4;
    var magnitude = Math.Pow(10, Math.Floor(Math.Log10(rough)));
    var normalized = rough / magnitude;
    var step = (normalized <= 1 ? 1 : normalized <= 2 ? 2 : normalized <= 5 ? 5 : 10) * magnitude;
    var ticks = new List<double>();
    for (var tick = 0.0; ; tick += step)
    {
      ticks.Add(tick);
      if (tick >= max)
        return ticks;
    }
  }

  sealed class Canvas(int width, int height, string title, string subtitle)
  {
    readonly StringBuilder Body = new();

    public void Text(double x, double y, string text, string style, string anchor)
    {
      Body.Append(CultureInfo.InvariantCulture, $"<text x=\"{x:0.#}\" y=\"{y:0.#}\" class=\"{style}\" text-anchor=\"{anchor}\">{Escape(text)}</text>\n");
    }

    public void Line(double x1, double y1, double x2, double y2, string style)
    {
      Body.Append(CultureInfo.InvariantCulture, $"<line x1=\"{x1:0.#}\" y1=\"{y1:0.#}\" x2=\"{x2:0.#}\" y2=\"{y2:0.#}\" class=\"{style}\"/>\n");
    }

    public void Polyline(IReadOnlyList<(double X, double Y)> points, int slot)
    {
      if (points.Count < 2)
        return;
      var path = string.Join(" ", points.Select(p => string.Create(CultureInfo.InvariantCulture, $"{p.X:0.#},{p.Y:0.#}")));
      Body.Append(CultureInfo.InvariantCulture, $"<polyline points=\"{path}\" class=\"line c{slot}\"/>\n");
    }

    public void Marker(double x, double y, int slot, string label)
    {
      Body.Append(CultureInfo.InvariantCulture, $"<circle cx=\"{x:0.#}\" cy=\"{y:0.#}\" r=\"4\" class=\"f{slot} ring\"><title>{Escape(label)}</title></circle>\n");
    }

    /// <summary>
    /// A bar with a rounded data end and a square baseline.
    /// </summary>
    public void Bar(double x, double y, double barWidth, double barHeight, int slot, string label)
    {
      var radius = Math.Min(4, Math.Min(barWidth, barHeight / 2));
      Body.Append(
          CultureInfo.InvariantCulture,
          $"<path d=\"M{x:0.#} {y:0.#}h{barWidth - radius:0.#}a{radius:0.#} {radius:0.#} 0 0 1 {radius:0.#} {radius:0.#}v{barHeight - 2 * radius:0.#}a{radius:0.#} {radius:0.#} 0 0 1 -{radius:0.#} {radius:0.#}h-{barWidth - radius:0.#}z\" class=\"f{slot}\"><title>{Escape(label)}</title></path>\n");
    }

    public void Legend(IReadOnlyList<IReadOnlyList<string>> rows)
    {
      for (var r = 0; r < rows.Count; r++)
      {
        var x = SidePadding;
        var y = LegendY + r * LegendRowHeight;
        foreach (var engine in rows[r])
        {
          var slot = EngineCatalog.ColorSlot(engine);
          Body.Append(CultureInfo.InvariantCulture, $"<circle cx=\"{x + 5:0.#}\" cy=\"{y - 4:0.#}\" r=\"5\" class=\"f{slot}\"/>\n");
          Text(x + 16, y, DisplayName(engine), "label", "start");
          x += LegendEntryWidth(engine);
        }
      }
    }

    public string Build()
    {
      var builder = new StringBuilder();
      builder.Append(CultureInfo.InvariantCulture, $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" role=\"img\" aria-label=\"{Escape(title)}\">\n");
      builder.Append(Style).Append('\n');
      builder.Append(CultureInfo.InvariantCulture, $"<rect width=\"{width}\" height=\"{height}\" rx=\"8\" class=\"surface\"/>\n");
      builder.Append(CultureInfo.InvariantCulture, $"<text x=\"{SidePadding}\" y=\"{TitleY}\" class=\"title\">{Escape(title)}</text>\n");
      builder.Append(CultureInfo.InvariantCulture, $"<text x=\"{SidePadding}\" y=\"{SubtitleY}\" class=\"subtitle\">{Escape(subtitle)}</text>\n");
      builder.Append(Body);
      builder.Append("</svg>\n");
      return builder.ToString();
    }

    static string Escape(string value) => WebUtility.HtmlEncode(value);
  }
}
