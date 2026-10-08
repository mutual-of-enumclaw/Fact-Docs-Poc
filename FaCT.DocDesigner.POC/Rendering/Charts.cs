using System.Globalization;
using System.Net;
using System.Text;

namespace FaCT.DocDesigner.POC.Rendering;

public sealed record ChartPoint(string Label, decimal Value);

/// <summary>
/// Charts from a list in the data, as inline SVG: column, bar (horizontal), line, pie and donut. Geometry is in a fixed
/// 640 x 320 box that scales to the width of its container; colors come from brand CSS classes (chart-s1..chart-s8,
/// chart-axis, ...), so a theme recolors them. Labels and values are encoded text.
/// </summary>
public static class Charts
{
	public const int Width = 640;
	public const int Height = 320;
	public const int MaxPoints = 50;
	public const int Series = 8;

	public static readonly IReadOnlyList<string> Types = ["column", "bar", "line", "pie", "donut"];

	private static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");

	/// <summary>A value in one of the designer's preset formats (currency, dollars, percent, number, decimal), else plain.</summary>
	public static string FormatValue(decimal value, string? format) => format switch
	{
		"currency" => value.ToString("C2", Us),
		"dollars" => value.ToString("C0", Us),
		"percent" => value.ToString("P2", Us),
		"number" => value.ToString("N0", Us),
		"decimal" => value.ToString("N2", Us),
		_ => (value / 1.000000000000000000000000000000000m).ToString(Us)
	};

	public static string Svg(string type, IReadOnlyList<ChartPoint> points, string? format, string noData = "No data")
	{
		if (!Types.Contains(type)) type = "column";
		var data = points.Take(MaxPoints).ToList();
		var svg = new StringBuilder();
		svg.Append($"<svg class=\"doc-chart-svg doc-chart-{type}-svg\" viewBox=\"0 0 {Width} {Height}\" aria-hidden=\"true\">");
		if (data.Count == 0 || (type is "pie" or "donut" && data.All(p => p.Value <= 0)))
		{
			Text(svg, Width / 2.0, Height / 2.0, noData, "chart-empty", "middle");
		}
		else
		{
			switch (type)
			{
				case "bar": Bars(svg, data, format); break;
				case "line": Line(svg, data, format); break;
				case "pie": Pie(svg, data, format, donut: false); break;
				case "donut": Pie(svg, data, format, donut: true); break;
				default: Columns(svg, data, format); break;
			}
		}
		svg.Append("</svg>");
		return svg.ToString();
	}

	// ---- column, bar and line: a value axis from min(0, smallest) to max(0, largest) --------------------------------

	private const double Left = 72, Right = 16, Top = 20, Bottom = 46;

	private static (decimal Low, decimal High) Range(IReadOnlyList<ChartPoint> data)
	{
		var low = Math.Min(0, data.Min(p => p.Value));
		var high = Math.Max(0, data.Max(p => p.Value));
		if (low == high) high = low + 1;
		return (low, high);
	}

	private static void Grid(StringBuilder svg, decimal low, decimal high, string? format, bool vertical)
	{
		for (var i = 0; i <= 4; i++)
		{
			var value = low + (high - low) * i / 4;
			if (vertical)
			{
				var y = Y(value, low, high);
				svg.Append(Inv($"<line class=\"chart-grid\" x1=\"{Left}\" y1=\"{y:0.##}\" x2=\"{Width - Right}\" y2=\"{y:0.##}\"></line>"));
				Text(svg, Left - 6, y + 4, FormatValue(Math.Round(value, 2), format), "chart-axis-label", "end");
			}
			else
			{
				var x = X(value, low, high);
				svg.Append(Inv($"<line class=\"chart-grid\" x1=\"{x:0.##}\" y1=\"{Top}\" x2=\"{x:0.##}\" y2=\"{Height - Bottom + 8}\"></line>"));
				Text(svg, x, Height - Bottom + 22, FormatValue(Math.Round(value, 2), format), "chart-axis-label", "middle");
			}
		}
	}

	private static double Y(decimal value, decimal low, decimal high) =>
		Top + (double)((high - value) / (high - low)) * (Height - Top - Bottom);

	private const double BarLeft = 150;

	private static double X(decimal value, decimal low, decimal high) =>
		BarLeft + (double)((value - low) / (high - low)) * (Width - Right - 60 - BarLeft);

	private static void Columns(StringBuilder svg, IReadOnlyList<ChartPoint> data, string? format)
	{
		var (low, high) = Range(data);
		Grid(svg, low, high, format, vertical: true);
		var slot = (Width - Left - Right) / data.Count;
		var zero = Y(0, low, high);
		for (var i = 0; i < data.Count; i++)
		{
			var x = Left + slot * i + slot * 0.2;
			var y = Y(data[i].Value, low, high);
			var top = Math.Min(y, zero);
			var height = Math.Max(Math.Abs(zero - y), 0.5);
			svg.Append(Inv($"<path class=\"chart-s{i % Series + 1} chart-bar\" d=\"M{x:0.##} {top:0.##}h{slot * 0.6:0.##}v{height:0.##}h-{slot * 0.6:0.##}z\"></path>"));
			Text(svg, x + slot * 0.3, data[i].Value >= 0 ? top - 5 : top + height + 13, FormatValue(data[i].Value, format), "chart-value", "middle");
			Text(svg, x + slot * 0.3, Height - Bottom + 18, Short(data[i].Label, Math.Max(4, (int)(slot / 7))), "chart-label", "middle");
		}
		svg.Append(Inv($"<line class=\"chart-axis\" x1=\"{Left}\" y1=\"{zero:0.##}\" x2=\"{Width - Right}\" y2=\"{zero:0.##}\"></line>"));
	}

	private static void Bars(StringBuilder svg, IReadOnlyList<ChartPoint> data, string? format)
	{
		var (low, high) = Range(data);
		Grid(svg, low, high, format, vertical: false);
		var slot = (Height - Top - Bottom) / data.Count;
		var zero = X(0, low, high);
		for (var i = 0; i < data.Count; i++)
		{
			var y = Top + slot * i + slot * 0.2;
			var x = X(data[i].Value, low, high);
			var left = Math.Min(x, zero);
			var width = Math.Max(Math.Abs(zero - x), 0.5);
			svg.Append(Inv($"<path class=\"chart-s{i % Series + 1} chart-bar\" d=\"M{left:0.##} {y:0.##}h{width:0.##}v{slot * 0.6:0.##}h-{width:0.##}z\"></path>"));
			Text(svg, BarLeft - 8, y + slot * 0.3 + 4, Short(data[i].Label, 20), "chart-label", "end");
			Text(svg, left + width + 5, y + slot * 0.3 + 4, FormatValue(data[i].Value, format), "chart-value", "start");
		}
		svg.Append(Inv($"<line class=\"chart-axis\" x1=\"{zero:0.##}\" y1=\"{Top}\" x2=\"{zero:0.##}\" y2=\"{Height - Bottom}\"></line>"));
	}

	private static void Line(StringBuilder svg, IReadOnlyList<ChartPoint> data, string? format)
	{
		var (low, high) = Range(data);
		Grid(svg, low, high, format, vertical: true);
		var step = data.Count == 1 ? 0 : (Width - Left - Right - 40) / (data.Count - 1);
		var points = data.Select((p, i) => (X: Left + 20 + step * i + (data.Count == 1 ? (Width - Left - Right - 40) / 2 : 0), Y: Y(p.Value, low, high))).ToList();
		svg.Append("<path class=\"chart-line chart-s1-stroke\" d=\"");
		for (var i = 0; i < points.Count; i++)
		{
			svg.Append(Inv($"{(i == 0 ? "M" : "L")}{points[i].X:0.##} {points[i].Y:0.##}"));
		}
		svg.Append("\"></path>");
		for (var i = 0; i < points.Count; i++)
		{
			svg.Append(Inv($"<circle class=\"chart-s1 chart-point\" cx=\"{points[i].X:0.##}\" cy=\"{points[i].Y:0.##}\" r=\"4\"></circle>"));
			Text(svg, points[i].X, points[i].Y - 9, FormatValue(data[i].Value, format), "chart-value", "middle");
			Text(svg, points[i].X, Height - Bottom + 18, Short(data[i].Label, Math.Max(4, (int)(Math.Max(step, 60) / 7))), "chart-label", "middle");
		}
		svg.Append(Inv($"<line class=\"chart-axis\" x1=\"{Left}\" y1=\"{Y(0, low, high):0.##}\" x2=\"{Width - Right}\" y2=\"{Y(0, low, high):0.##}\"></line>"));
	}

	// ---- pie and donut: slices clockwise from 12 o'clock, a legend with values and shares ---------------------------

	private static void Pie(StringBuilder svg, IReadOnlyList<ChartPoint> data, string? format, bool donut)
	{
		var total = data.Where(p => p.Value > 0).Sum(p => p.Value);
		const double cx = 160, cy = 160, r = 130, hole = 70;
		var angle = 0.0;
		for (var i = 0; i < data.Count; i++)
		{
			if (data[i].Value <= 0) continue;
			var share = (double)(data[i].Value / total);
			var css = $"chart-s{i % Series + 1} chart-slice";
			if (share >= 0.9999)
			{
				svg.Append(Inv($"<circle class=\"{css}\" cx=\"{cx}\" cy=\"{cy}\" r=\"{r}\"></circle>"));
			}
			else
			{
				var end = angle + share * 2 * Math.PI;
				var (x1, y1) = (cx + r * Math.Sin(angle), cy - r * Math.Cos(angle));
				var (x2, y2) = (cx + r * Math.Sin(end), cy - r * Math.Cos(end));
				var large = share > 0.5 ? 1 : 0;
				svg.Append(Inv($"<path class=\"{css}\" d=\"M{cx} {cy}L{x1:0.##} {y1:0.##}A{r} {r} 0 {large} 1 {x2:0.##} {y2:0.##}z\"></path>"));
				angle = end;
			}
		}
		if (donut)
		{
			svg.Append(Inv($"<circle class=\"chart-hole\" cx=\"{cx}\" cy=\"{cy}\" r=\"{hole}\"></circle>"));
			Text(svg, cx, cy + 5, FormatValue(total, format), "chart-total", "middle");
		}

		// legend: every item, including those with no share
		var rowHeight = Math.Min(24.0, (Height - 40.0) / data.Count);
		for (var i = 0; i < data.Count; i++)
		{
			var y = 30 + rowHeight * i;
			svg.Append(Inv($"<path class=\"chart-s{i % Series + 1}\" d=\"M330 {y - 9:0.##}h12v12h-12z\"></path>"));
			var share = data[i].Value > 0 && total > 0
				? " (" + Math.Round(data[i].Value / total * 100, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture) + "%)"
				: string.Empty;
			Text(svg, 350, y + 1, Short(data[i].Label, 22) + "  " + FormatValue(data[i].Value, format) + share, "chart-legend", "start");
		}
	}

	// ---- helpers ------------------------------------------------------------------------------------------------------

	private static void Text(StringBuilder svg, double x, double y, string text, string css, string anchor) =>
		svg.Append(Inv($"<text class=\"{css}\" x=\"{x:0.##}\" y=\"{y:0.##}\" text-anchor=\"{anchor}\">")).Append(WebUtility.HtmlEncode(text)).Append("</text>");

	private static string Short(string text, int max) => text.Length <= max ? text : text[..Math.Max(1, max - 1)].TrimEnd() + "\u2026";

	private static string Inv(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
