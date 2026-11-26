using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.IO;
using SkiaSharp;

namespace CircuitRF.Render.DataDisplay;

public sealed record MarkerTableTraceReading(Trace Trace, string Label, IReadOnlyList<string> Values, int TraceOrder)
{
    internal IReadOnlyList<string> DisplayValues { get; } = Values.Select(value => MarkerTable.CellValue(Trace, value)).ToArray();
    internal IReadOnlyList<string> ValueLines { get; } = Values.Select(value => MarkerTable.CellValue(Trace, value))
        .SelectMany(value => MarkerTableRenderer.WrapText(value, MarkerTableRenderer.ValueColumn - 10)).ToArray();
}

public sealed record MarkerTableRow(Marker Marker, Trace Trace, string TraceLabel, IReadOnlyList<string> Readout,
    IReadOnlyList<string> Coordinates, IReadOnlyList<MarkerTableTraceReading> TraceReadings, string? CommonCoordinateName = null)
{
    public string MarkerName { get; } = Marker.Name;
    internal IReadOnlyList<string> MarkerLines { get; } = MarkerTableRenderer.WrapText(Marker.Name, MarkerTableRenderer.MarkerColumn - 10);
    internal IReadOnlyList<string> DisplayCoordinates { get; } = Coordinates.Select(coordinate =>
        CommonCoordinateName is { } name && coordinate.StartsWith(name + "=", StringComparison.Ordinal)
            ? coordinate[(name.Length + 1)..] : coordinate).ToArray();
    internal IReadOnlyList<string> CoordinateLines { get; } = Coordinates.Select(coordinate =>
        CommonCoordinateName is { } name && coordinate.StartsWith(name + "=", StringComparison.Ordinal)
            ? coordinate[(name.Length + 1)..] : coordinate)
        .SelectMany(line => MarkerTableRenderer.WrapText(line, MarkerTableRenderer.CoordinateColumn - 10)).ToArray();
}

public static class MarkerTable
{
    internal static string? CoordinateName(IReadOnlyList<MarkerTableRow> rows)
    {
        string? common = null;
        foreach (var row in rows)
        {
            if (row.Coordinates.Count == 0) continue;
            if (row.Coordinates.Count != 1) return null;
            string coordinate = row.Coordinates[0];
            int equals = coordinate.IndexOf('=');
            if (equals <= 0) return null;
            string name = coordinate[..equals];
            if (common is not null && !string.Equals(name, common, StringComparison.Ordinal)) return null;
            common = name;
        }
        return common;
    }

    internal static string CellValue(Trace trace, string value)
    {
        string description = trace.ReadoutDescription(false);
        if (description.Length == 0) return value;
        string equalsPrefix = description + "=";
        if (value.StartsWith(equalsPrefix, StringComparison.Ordinal)) return value[equalsPrefix.Length..];
        string floorPrefix = description + " " + DbFloor.FlooredPrefix;
        return value.StartsWith(floorPrefix, StringComparison.Ordinal) ? value[(description.Length + 1)..] : value;
    }

    public static IReadOnlyList<MarkerTableTraceReading> Columns(IReadOnlyList<MarkerTableRow> rows)
        => rows.SelectMany(row => row.TraceReadings)
            .OrderBy(reading => reading.TraceOrder)
            .DistinctBy(reading => reading.Trace)
            .ToArray();

    public static IReadOnlyList<MarkerTableRow> Rows(Plot plot, bool showFilePrefix = false)
    {
        ArgumentNullException.ThrowIfNull(plot);
        var rows = new List<MarkerTableRow>();
        var sources = plot.Traces.Select(trace => trace.EffectiveSourcePath)
            .Where(path => !string.IsNullOrEmpty(path)).Distinct(StringComparer.Ordinal).ToList();
        bool showSource = showFilePrefix || sources.Count > 1;
        string Source(Trace trace)
        {
            if (trace.EffectiveSourcePath is not { Length: > 0 } source) return "";
            string stem = Path.GetFileNameWithoutExtension(source);
            return sources.Count(path => Path.GetFileNameWithoutExtension(path) == stem) > 1 ? source : stem;
        }
        string Label(Trace trace)
        {
            string label = trace.ReadoutDescription(false);
            string source = showSource ? Source(trace) : "";
            return source.Length > 0 ? $"{source}: {label}" : label;
        }
        foreach (var trace in plot.Traces)
        foreach (var marker in trace.Markers)
        {
            var resolved = plot.ResolveMarker(trace, marker);
            bool separateMulti = resolved.IsMulti && !resolved.Unavailable && !trace.IsContourTrace
                                 && (!trace.IsHarmonicStem || resolved.Function == MarkerFunction.Vertical);
            var coordinateIndices = new List<int>();
            var boxLines = trace.BuildMarkerBoxLines(resolved, plot.FreqUnits, false,
                separateMulti ? null : plot.Traces, coordinateIndices);
            var lines = boxLines.Skip(1).Select(line => line.Text).ToList();
            var coordinates = coordinateIndices.Select(index => boxLines[index].Text).ToArray();
            var values = boxLines.Select((line, index) => (line.Text, index))
                .Where(line => line.index > 0 && !coordinateIndices.Contains(line.index))
                .Select(line => line.Text).ToList();
            var readings = new List<MarkerTableTraceReading>();
            if (trace.IsContourTrace && !resolved.Unavailable && !resolved.FreePosition)
            {
                var contours = plot.Traces.Where(other => other.IsContourTrace).ToList();
                for (int index = 0; index < contours.Count && index < values.Count; index++)
                    readings.Add(new MarkerTableTraceReading(contours[index], Label(contours[index]), [values[index]],
                        plot.Traces.IndexOf(contours[index])));
            }
            else
                readings.Add(new MarkerTableTraceReading(trace, Label(trace), values, plot.Traces.IndexOf(trace)));
            if (separateMulti)
                foreach (var other in plot.Traces.Where(other => !Equals(other, trace) && !other.IsAnnotation))
                {
                    string reading = trace.GetMultiMarkerLine(resolved, other);
                    string source = showSource ? Source(other) : "";
                    lines.Add(source.Length > 0 ? $"{source}: {reading}" : reading);
                    readings.Add(new MarkerTableTraceReading(other, Label(other), [reading], plot.Traces.IndexOf(other)));
                }
            rows.Add(new MarkerTableRow(marker, trace, Label(trace), lines, coordinates, readings));
        }
        string? coordinateName = CoordinateName(rows);
        return rows.OrderBy(row => row.Marker.Index)
            .Select(row => new MarkerTableRow(row.Marker, row.Trace, row.TraceLabel, row.Readout,
                row.Coordinates, row.TraceReadings, coordinateName)).ToArray();
    }
}

public static class MarkerTableRenderer
{
    public const float Width = 700;
    public const float HeaderHeight = 24;
    public const float LineHeight = 17;
    public const float MaxHeight = 260;
    public const float ScrollbarHeight = 12;
    internal const float MarkerColumn = 56;
    internal const float CoordinateColumn = 120;
    internal const float ValueColumn = 200;
    internal const float FixedWidth = MarkerColumn + CoordinateColumn;

    public static float RowHeight(MarkerTableRow row)
        => Math.Max(row.MarkerLines.Count, Math.Max(row.CoordinateLines.Count,
            row.TraceReadings.Count == 0 ? 0 : row.TraceReadings.Max(reading => reading.ValueLines.Count))) * LineHeight + 4;

    public static float HeaderHeightFor(IReadOnlyList<MarkerTableRow> rows)
    {
        var columns = MarkerTable.Columns(rows);
        int lines = columns.Count == 0 ? 1 : columns.Max(column => WrapText(column.Label, ValueColumn - 10).Count);
        lines = Math.Max(lines, WrapText(MarkerTable.CoordinateName(rows) ?? "X", CoordinateColumn - 10).Count);
        return Math.Max(HeaderHeight, 7 + lines * LineHeight);
    }

    public static float FullWidth(IReadOnlyList<MarkerTableRow> rows)
        => FixedWidth + Math.Max(1, MarkerTable.Columns(rows).Count) * ValueColumn;

    internal static IReadOnlyList<string> WrapText(string text, float width)
    {
        if (text.Length == 0) return [""];
        using var font = new SKFont(SkiaFonts.PlexRegular, 12);
        using var fallback = new SKFont(SkiaFonts.DejaVuRegular, 12);
        int[] positions = StringInfo.ParseCombiningCharacters(text);
        var result = new List<string>();
        int start = 0;
        while (start < positions.Length)
        {
            int low = start + 1, high = positions.Length, end = low;
            while (low <= high)
            {
                int middle = (low + high) / 2;
                int after = middle == positions.Length ? text.Length : positions[middle];
                string candidate = text[positions[start]..after];
                if (RendererText.MeasureTextWithFallback(candidate, font, fallback) <= width)
                {
                    end = middle;
                    low = middle + 1;
                }
                else high = middle - 1;
            }
            if (end < positions.Length)
            {
                int boundary = -1;
                for (int index = start; index < end; index++)
                {
                    char character = text[positions[index]];
                    if (char.IsWhiteSpace(character) || character is '=' or ':' or '/') boundary = index + 1;
                }
                if (boundary > start) end = boundary;
            }
            int endChar = end == positions.Length ? text.Length : positions[end];
            result.Add(text[positions[start]..endChar]);
            start = end;
        }
        return result;
    }

    public static float ContentHeight(IReadOnlyList<MarkerTableRow> rows)
        => rows.Count == 0 ? LineHeight + 4 : rows.Sum(RowHeight);

    public static (float W, float H) Measure(IReadOnlyList<MarkerTableRow> rows)
    {
        float header = HeaderHeightFor(rows);
        float scrollbar = MaxHorizontalScroll(rows) > 0 ? ScrollbarHeight : 0;
        return (Math.Min(Width, FullWidth(rows)), header + scrollbar +
            Math.Min(Math.Max(LineHeight + 4, MaxHeight - header - scrollbar), ContentHeight(rows)));
    }

    public static (float W, float H) MeasureAll(IReadOnlyList<MarkerTableRow> rows)
        => (FullWidth(rows), HeaderHeightFor(rows) + ContentHeight(rows));

    public static float MaxHorizontalScroll(IReadOnlyList<MarkerTableRow> rows)
        => Math.Max(0, FullWidth(rows) - Width);

    public static float MaxScroll(IReadOnlyList<MarkerTableRow> rows)
        => Math.Max(0, ContentHeight(rows) - (Measure(rows).H - HeaderHeightFor(rows) -
            (MaxHorizontalScroll(rows) > 0 ? ScrollbarHeight : 0)));

    public static MarkerTableRow? RowAt(IReadOnlyList<MarkerTableRow> rows, double y, double scroll)
    {
        float header = HeaderHeightFor(rows);
        float bottom = Measure(rows).H - (MaxHorizontalScroll(rows) > 0 ? ScrollbarHeight : 0);
        if (y < header || y >= bottom) return null;
        double position = y - header + Math.Clamp(scroll, 0, MaxScroll(rows));
        foreach (var row in rows)
        {
            double height = RowHeight(row);
            if (position < height) return row;
            position -= height;
        }
        return null;
    }

    public static void Draw(SKCanvas canvas, IReadOnlyList<MarkerTableRow> rows, RenderTheme theme, float scroll = 0,
        IReadOnlySet<Guid>? selected = null, SKColor selectionColor = default, bool showAll = false, float horizontalScroll = 0)
    {
        var (width, height) = showAll ? MeasureAll(rows) : Measure(rows);
        var columns = MarkerTable.Columns(rows);
        float header = HeaderHeightFor(rows);
        float maxHorizontal = showAll ? 0 : MaxHorizontalScroll(rows);
        float offset = Math.Clamp(horizontalScroll, 0, maxHorizontal);
        float bottom = height - (maxHorizontal > 0 ? ScrollbarHeight : 0);
        using var fill = new SKPaint { Color = theme.BackgroundColor, Style = SKPaintStyle.Fill };
        using var border = new SKPaint { Color = theme.BorderColor, Style = SKPaintStyle.Stroke, StrokeWidth = 0 };
        using var text = new SKPaint { Color = theme.TextColor, IsAntialias = true };
        using var font = new SKFont(SkiaFonts.PlexRegular, 12);
        using var bold = new SKFont(SkiaFonts.PlexBold, 12);
        using var fallback = new SKFont(SkiaFonts.DejaVuRegular, 12);
        using var fallbackBold = new SKFont(SkiaFonts.DejaVuBold, 12);
        canvas.DrawRect(0, 0, width, height, fill);
        RendererText.DrawLeftTextWithFallback(canvas, "Marker", 5, 16, bold, fallbackBold, text);
        var coordinateHeader = WrapText(MarkerTable.CoordinateName(rows) ?? "X", CoordinateColumn - 10);
        for (int line = 0; line < coordinateHeader.Count; line++)
            DrawCell(canvas, coordinateHeader[line], MarkerColumn, CoordinateColumn, 16 + line * LineHeight, bold, fallbackBold, text);
        canvas.Save();
        canvas.ClipRect(new SKRect(FixedWidth, 0, width, bottom));
        for (int index = 0; index < columns.Count; index++)
        {
            float left = FixedWidth + index * ValueColumn - offset;
            var label = WrapText(columns[index].Label, ValueColumn - 10);
            for (int line = 0; line < label.Count; line++)
                DrawCell(canvas, label[line], left, ValueColumn, 16 + line * LineHeight, bold, fallbackBold, text);
        }
        canvas.Restore();
        canvas.Save();
        canvas.ClipRect(new SKRect(0, header, width, bottom));
        float y = header - (showAll ? 0 : Math.Clamp(scroll, 0, MaxScroll(rows)));
        if (rows.Count == 0)
            RendererText.DrawLeftTextWithFallback(canvas, "No markers", 5, y + 15, font, fallback, text);
        foreach (var row in rows)
        {
            float rowHeight = RowHeight(row);
            if (y + rowHeight >= header && y <= bottom)
            {
                if (selected?.Contains(row.Marker.Id) == true)
                {
                    using var highlight = new SKPaint { Color = selectionColor.WithAlpha(55), Style = SKPaintStyle.Fill };
                    canvas.DrawRect(1, y + 1, width - 2, rowHeight - 1, highlight);
                }
                for (int line = 0; line < row.MarkerLines.Count; line++)
                    DrawCell(canvas, row.MarkerLines[line], 0, MarkerColumn, y + 15 + line * LineHeight, font, fallback, text);
                for (int line = 0; line < row.CoordinateLines.Count; line++)
                    DrawCell(canvas, row.CoordinateLines[line], MarkerColumn, CoordinateColumn,
                        y + 15 + line * LineHeight, font, fallback, text);
                canvas.Save();
                canvas.ClipRect(new SKRect(FixedWidth, header, width, bottom));
                for (int index = 0; index < columns.Count; index++)
                {
                    var reading = row.TraceReadings.FirstOrDefault(item => ReferenceEquals(item.Trace, columns[index].Trace));
                    if (reading is null) continue;
                    for (int line = 0; line < reading.ValueLines.Count; line++)
                        DrawCell(canvas, reading.ValueLines[line], FixedWidth + index * ValueColumn - offset,
                            ValueColumn, y + 15 + line * LineHeight, font, fallback, text);
                }
                canvas.Restore();
                if (y + rowHeight < bottom)
                    canvas.DrawLine(0, y + rowHeight, width, y + rowHeight, border);
            }
            y += rowHeight;
        }
        canvas.Restore();
        var matrix = canvas.TotalMatrix;
        float insetX = matrix.ScaleX != 0 ? 0.5f / Math.Abs(matrix.ScaleX) : 0;
        float insetY = matrix.ScaleY != 0 ? 0.5f / Math.Abs(matrix.ScaleY) : 0;
        canvas.DrawRect(insetX, insetY, width - 2 * insetX, height - 2 * insetY, border);
        canvas.DrawLine(insetX, header, width - insetX, header, border);
        canvas.DrawLine(MarkerColumn, insetY, MarkerColumn, bottom, border);
        canvas.DrawLine(FixedWidth, insetY, FixedWidth, bottom, border);
        canvas.Save();
        canvas.ClipRect(new SKRect(FixedWidth + insetX, 0, width - insetX, bottom));
        for (int index = 1; index < columns.Count; index++)
        {
            float x = FixedWidth + index * ValueColumn - offset;
            canvas.DrawLine(x, insetY, x, bottom, border);
        }
        canvas.Restore();
        float maxScroll = showAll ? 0 : MaxScroll(rows);
        if (maxScroll > 0)
        {
            float trackHeight = bottom - header;
            float thumbHeight = Math.Max(12, trackHeight * trackHeight / ContentHeight(rows));
            float thumbTop = header + (trackHeight - thumbHeight) * Math.Clamp(scroll, 0, maxScroll) / maxScroll;
            using var thumb = new SKPaint { Color = theme.TextColor.WithAlpha(120), Style = SKPaintStyle.Fill };
            canvas.DrawRect(width - 4, thumbTop, 3, thumbHeight, thumb);
        }
        if (maxHorizontal > 0)
        {
            canvas.DrawLine(insetX, bottom, width - insetX, bottom, border);
            float track = width - FixedWidth;
            float thumbWidth = Math.Max(20, track * track / (FullWidth(rows) - FixedWidth));
            float thumbLeft = FixedWidth + (track - thumbWidth) * offset / maxHorizontal;
            using var thumb = new SKPaint { Color = theme.TextColor.WithAlpha(120), Style = SKPaintStyle.Fill };
            canvas.DrawRect(thumbLeft, bottom + 3, thumbWidth, ScrollbarHeight - 6, thumb);
        }
    }

    private static void DrawCell(SKCanvas canvas, string value, float left, float width, float baseline,
        SKFont font, SKFont fallback, SKPaint paint)
    {
        canvas.Save();
        canvas.ClipRect(new SKRect(left + 1, baseline - 14, left + width - 1, baseline + 4));
        RendererText.DrawLeftTextWithFallback(canvas, value, left + 5, baseline, font, fallback, paint);
        canvas.Restore();
    }
}