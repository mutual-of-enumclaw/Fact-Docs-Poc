using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FaCT.DocDesigner.POC.Export;
using FaCT.DocDesigner.POC.Rendering;
using FaCT.DocDesigner.POC.Tests.Clauses;
using FaCT.DocDesigner.POC.Tests.Import;
using FaCT.DocDesigner.POC.Tests.PageSetup;
using Microsoft.Extensions.DependencyInjection;
using UglyToad.PdfPig;
using ZXing;

namespace FaCT.DocDesigner.POC.Tests.Visuals;

/// <summary>Reads barcodes back out of rendered PDFs.</summary>
internal static class BarcodeReader
{
	/// <summary>The barcode on page 1 (rendered at 4x, composited on white, cropped to the ink: ZXing's Data Matrix
	/// detector looks outward from the middle of the image), or null.</summary>
	public static string? Read(byte[] pdf, params BarcodeFormat[] formats)
	{
		var (bgra, width, height) = PageImages.Pixels(pdf, 1, 4.0);
		var gray = new byte[width * height];
		int left = width, top = height, right = 0, bottom = 0;
		for (var p = 0; p < gray.Length; p++)
		{
			int a = bgra[p * 4 + 3];
			var luminance = (bgra[p * 4 + 2] * 299 + bgra[p * 4 + 1] * 587 + bgra[p * 4] * 114) / 1000;
			gray[p] = (byte)((luminance * a + 255 * (255 - a)) / 255);
			if (gray[p] < 128)
			{
				(left, top, right, bottom) = (Math.Min(left, p % width), Math.Min(top, p / width), Math.Max(right, p % width), Math.Max(bottom, p / width));
			}
		}
		if (right < left) return null;
		const int margin = 40;
		(left, top) = (Math.Max(0, left - margin), Math.Max(0, top - margin));
		(right, bottom) = (Math.Min(width - 1, right + margin), Math.Min(height - 1, bottom + margin));
		var (w, h) = (right - left + 1, bottom - top + 1);
		var rgb = new byte[w * h * 3];
		for (var y = 0; y < h; y++)
		{
			for (var x = 0; x < w; x++)
			{
				var v = gray[(top + y) * width + left + x];
				rgb[(y * w + x) * 3] = rgb[(y * w + x) * 3 + 1] = rgb[(y * w + x) * 3 + 2] = v;
			}
		}
		var reader = new BarcodeReaderGeneric { Options = { TryHarder = true, PossibleFormats = formats } };
		return reader.Decode(new RGBLuminanceSource(rgb, w, h, RGBLuminanceSource.BitmapFormat.RGB24))?.Text;
	}
}

/// <summary>Barcodes / QR codes, charts and signature blocks: SVG output, the PDF, and the Word export.</summary>
public sealed class VisualsRenderTests(ClauseAppFactory factory) : IClassFixture<ClauseAppFactory>
{
	private static readonly JsonElement Data = JsonDocument.Parse("""
		{ "policy": { "number": "CPP1234567", "url": "https://www.mutualofenumclaw.com/policy/CPP1234567", "lower": "café", "empty": "",
		              "insured": "Acme Bakery", "signer": "Pat Smith", "role": "Owner", "signed": "2026-07-01" },
		  "losses": [ { "year": "2023", "amount": 1000 }, { "year": "2024", "amount": 2500 }, { "year": "2025", "amount": 500 } ],
		  "mixed": [ { "name": "A", "v": 10 }, { "name": "B", "v": null }, { "name": "C", "v": "n/a" }, { "name": "D", "v": "5" }, { "name": "E" } ],
		  "negative": [ { "name": "Up", "v": 100 }, { "name": "Down", "v": -50 } ],
		  "evil": [ { "name": "<script>alert(1)</script>", "v": 1 } ],
		  "single": [ { "name": "All", "v": 7 } ],
		  "none": [] }
		""").RootElement.Clone();

	private DocumentComposer Composer => factory.Services.GetRequiredService<DocumentComposer>();

	private async Task<string> BodyAsync(string template)
	{
		var result = await Composer.ComposeAsync(template, "", Data);
		Assert.True(result.Error is null, result.Error);
		var body = result.Html![(result.Html.IndexOf("<body>", StringComparison.Ordinal) + 6)..];
		return body[..body.IndexOf("</body>", StringComparison.Ordinal)];
	}

	private async Task<byte[]> PdfAsync(string html, string css = "")
	{
		var client = factory.CreateClient();
		var response = await client.PostAsJsonAsync("/api/render", new JsonObject { ["html"] = html, ["css"] = css, ["data"] = JsonNode.Parse(Data.GetRawText()) });
		Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
		return await response.Content.ReadAsByteArrayAsync();
	}

	private static List<string> Paths(string svg, string css) =>
		Regex.Matches(svg, $"<path class=\"[^\"]*{css}[^\"]*\" d=\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToList();

	// ---- barcodes --------------------------------------------------------------------------------------------------

	[Theory]
	[InlineData("qr", false)]
	[InlineData("code128", true)]
	[InlineData("code39", true)]
	[InlineData("datamatrix", false)]
	[InlineData("pdf417", false)]
	public void Each_kind_is_one_svg_path(string kind, bool linear)
	{
		var svg = Barcodes.Svg("CPP1234567", kind)!;
		Assert.StartsWith($"<svg class=\"doc-code doc-code-{kind}\" viewBox=\"{(linear ? "0 0 " : "-4 -4 ")}", svg);
		Assert.Single(Regex.Matches(svg, "<path "));
		Assert.Equal(linear, svg.Contains("preserveAspectRatio=\"none\"", StringComparison.Ordinal));
		Assert.DoesNotContain("<rect", svg);
		Assert.Equal(linear, Barcodes.IsLinear(kind));
	}

	[Theory]
	[InlineData("qr", "CPP1234567", BarcodeFormat.QR_CODE)]
	[InlineData("qr", "https://www.mutualofenumclaw.com/policy/CPP1234567", BarcodeFormat.QR_CODE)]
	[InlineData("code128", "CPP1234567", BarcodeFormat.CODE_128)]
	[InlineData("code39", "CPP1234567", BarcodeFormat.CODE_39)]
	[InlineData("datamatrix", "CPP1234567", BarcodeFormat.DATA_MATRIX)]
	[InlineData("pdf417", "CPP1234567", BarcodeFormat.PDF_417)]
	public async Task The_printed_barcode_scans_back_to_the_value(string kind, string value, BarcodeFormat format)
	{
		var linear = Barcodes.IsLinear(kind);
		var field = value.StartsWith("https", StringComparison.Ordinal) ? "policy.url" : "policy.number";
		var html = $"<span class=\"doc-barcode {(linear ? "doc-barcode-1d doc-barcode-h-l" : "doc-barcode-2d")}\" id=\"bc\">{{{{ {field} | barcode: \"{kind}\" }}}}</span>";
		// the standard footer is ink too: only the code is on a page without it
		var pdf = await PdfAsync(Setup.Markup(inner: Setup.Slots("footer", "default")) + html, $"#bc{{width:{(linear ? 3 : 1.5)}in}}");
		Assert.Equal(value, BarcodeReader.Read(pdf, format));
	}

	[Theory]
	[InlineData("{{ policy.lower | barcode: \"code39\" }}")]
	[InlineData("{{ policy.empty | barcode: \"qr\" }}")]
	[InlineData("{{ policy.missing | barcode: \"qr\" }}")]
	public async Task Values_that_cannot_be_encoded_print_nothing(string liquid)
	{
		Assert.Equal("<p></p>", await BodyAsync($"<p>{liquid}</p>"));
	}

	[Fact]
	public void Values_longer_than_the_limit_print_nothing()
	{
		Assert.Null(Barcodes.Svg(new string('A', Barcodes.MaxLength + 1), "qr"));
		Assert.NotNull(Barcodes.Svg(new string('A', 500), "qr"));
		Assert.Null(Barcodes.Svg("x", "upc"));
	}

	[Fact]
	public async Task Barcodes_survive_the_sanitizer()
	{
		var body = await BodyAsync("<span class=\"doc-barcode doc-barcode-1d\">{{ policy.number | barcode: \"code128\" }}</span>");
		Assert.Contains("<svg class=\"doc-code doc-code-code128\" viewBox=\"0 0 ", body);
		Assert.Contains("preserveAspectRatio=\"none\"", body, StringComparison.OrdinalIgnoreCase);
		Assert.Contains("<path d=\"M0 0h", body);
	}

	[Fact]
	public async Task An_unknown_kind_is_a_render_error()
	{
		var result = await Composer.ComposeAsync("<p>{{ policy.number | barcode: \"upc\" }}</p>", "", Data);
		Assert.Contains("'upc' isn't a barcode kind", result.Error);
	}

	// ---- charts ----------------------------------------------------------------------------------------------------

	[Fact]
	public async Task A_column_chart_has_one_bar_per_item_in_proportion()
	{
		var svg = await BodyAsync("{{ losses | chart: \"column\", \"year\", \"amount\", \"currency\" }}");
		var bars = Paths(svg, "chart-bar");
		Assert.Equal(3, bars.Count);
		var heights = bars.Select(d => double.Parse(Regex.Match(d, @"v([\d.]+)").Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)).ToList();
		Assert.Equal(2.5, heights[1] / heights[0], 2);
		Assert.Equal(0.5, heights[2] / heights[0], 2);
		foreach (var text in new[] { "2023", "2024", "2025", "$1,000.00", "$2,500.00", "$500.00" })
		{
			Assert.Contains(">" + text + "</text>", svg);
		}
		Assert.Contains("class=\"chart-s1 chart-bar\"", svg);
		Assert.Contains("class=\"chart-s2 chart-bar\"", svg);
	}

	[Fact]
	public async Task A_bar_chart_runs_across()
	{
		var svg = await BodyAsync("{{ losses | chart: \"bar\", \"year\", \"amount\" }}");
		var widths = Paths(svg, "chart-bar").Select(d => double.Parse(Regex.Match(d, @"h([\d.]+)").Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)).ToList();
		Assert.Equal(3, widths.Count);
		Assert.Equal(2.5, widths[1] / widths[0], 2);
		Assert.Contains(">2500</text>", svg);
	}

	[Fact]
	public async Task A_line_chart_joins_the_points()
	{
		var svg = await BodyAsync("{{ losses | chart: \"line\", \"year\", \"amount\", \"dollars\" }}");
		var line = Regex.Match(svg, "<path class=\"chart-line chart-s1-stroke\" d=\"([^\"]+)\"").Groups[1].Value;
		Assert.Equal(1, line.Count(c => c == 'M'));
		Assert.Equal(2, line.Count(c => c == 'L'));
		Assert.Equal(3, Regex.Matches(svg, "<circle class=\"chart-s1 chart-point\"").Count);
		Assert.Contains(">$2,500</text>", svg);
	}

	[Fact]
	public async Task A_pie_chart_has_a_slice_and_a_legend_line_per_item()
	{
		var svg = await BodyAsync("{{ losses | chart: \"pie\", \"year\", \"amount\" }}");
		Assert.Equal(3, Paths(svg, "chart-slice").Count);
		Assert.Contains(">2024  2500 (63%)</text>", svg);
		Assert.Contains(">2025  500 (13%)</text>", svg);
		Assert.DoesNotContain("chart-hole", svg);
	}

	[Fact]
	public async Task A_donut_shows_the_total_in_the_middle()
	{
		var svg = await BodyAsync("{{ losses | chart: \"donut\", \"year\", \"amount\", \"currency\" }}");
		Assert.Contains("<circle class=\"chart-hole\"", svg);
		Assert.Contains("class=\"chart-total\"", svg);
		Assert.Contains(">$4,000.00</text>", svg);
	}

	[Fact]
	public async Task A_single_item_pie_is_a_full_circle()
	{
		var svg = await BodyAsync("{{ single | chart: \"pie\", \"name\", \"v\" }}");
		Assert.Contains("<circle class=\"chart-s1 chart-slice\"", svg);
		Assert.Contains(">All  7 (100%)</text>", svg);
	}

	[Theory]
	[InlineData("none", "column")]
	[InlineData("none", "pie")]
	[InlineData("negative", "pie")]
	[InlineData("missing", "column")]
	public async Task No_data_says_so(string list, string type)
	{
		var svg = await BodyAsync($"{{{{ {list} | chart: \"{type}\", \"name\", \"v\" }}}}");
		if (list == "negative")
		{
			// one positive slice still draws
			Assert.Contains("chart-slice", svg);
		}
		else
		{
			Assert.Contains(">No data</text>", svg);
		}
	}

	[Fact]
	public async Task Items_without_a_number_are_left_out()
	{
		var svg = await BodyAsync("{{ mixed | chart: \"column\", \"name\", \"v\" }}");
		Assert.Equal(2, Paths(svg, "chart-bar").Count);
		Assert.Contains(">A</text>", svg);
		Assert.Contains(">D</text>", svg);
		Assert.DoesNotContain(">B</text>", svg);
		Assert.DoesNotContain(">C</text>", svg);
	}

	[Fact]
	public async Task Negative_values_go_below_the_axis()
	{
		var svg = await BodyAsync("{{ negative | chart: \"column\", \"name\", \"v\" }}");
		var bars = Paths(svg, "chart-bar");
		var tops = bars.Select(d => double.Parse(Regex.Match(d, @"^M[\d.]+ ([\d.]+)").Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)).ToList();
		Assert.True(tops[1] > tops[0], $"{tops[0]} {tops[1]}");
		Assert.Contains(">-50</text>", svg);
	}

	[Fact]
	public void At_most_fifty_points()
	{
		var svg = Charts.Svg("column", Enumerable.Range(1, 80).Select(i => new ChartPoint("P" + i, i)).ToList(), null);
		Assert.Equal(Charts.MaxPoints, Regex.Matches(svg, "chart-bar").Count);
	}

	[Fact]
	public async Task Labels_are_text_never_markup()
	{
		var svg = await BodyAsync("{{ evil | chart: \"bar\", \"name\", \"v\" }}");
		Assert.DoesNotContain("<script", svg, StringComparison.OrdinalIgnoreCase);
		Assert.Contains("&lt;script&gt;", svg);
	}

	[Fact]
	public async Task An_unknown_chart_type_is_a_render_error()
	{
		var result = await Composer.ComposeAsync("{{ losses | chart: \"radar\", \"year\", \"amount\" }}", "", Data);
		Assert.Contains("'radar' isn't a chart type", result.Error);
	}

	[Fact]
	public async Task Charts_print_their_labels_and_values_in_the_pdf()
	{
		var pdf = await PdfAsync("<div class=\"doc-chart\"><div class=\"doc-chart-title\">Losses by year</div>{{ losses | chart: \"column\", \"year\", \"amount\", \"dollars\" }}</div>");
		var text = Regex.Replace(PdfText.Extract(pdf), @"\s+", " ");
		foreach (var expected in new[] { "Losses by year", "2023", "2024", "2025", "$1,000", "$2,500", "$500" })
		{
			Assert.Contains(expected, text);
		}
	}

	[Fact]
	public async Task Chart_colors_are_brand_tokens_so_themes_recolor_them()
	{
		var result = await Composer.ComposeAsync("<p>x</p>", "", Data);
		Assert.Contains(".chart-s1 { fill: var(--moe-green); }", result.Html!);
		Assert.Contains(".chart-s3 { fill: var(--moe-aqua); }", result.Html!);
	}

	// ---- signature -------------------------------------------------------------------------------------------------

	private const string Signature = """
		<div class="doc-signature"><div class="doc-signature-line"><span class="doc-esign-anchor">\s1\</span></div>
		<div class="doc-signature-name">{{ policy.signer }}</div><div class="doc-signature-title">{{ policy.role }}</div>
		<div class="doc-signature-label">Insured</div><div class="doc-signature-date">Date: {{ policy.signed | shortdate }}</div></div>
		""";

	[Fact]
	public async Task A_signature_block_prints_name_title_role_and_date()
	{
		var text = Regex.Replace(PdfText.Extract(await PdfAsync(Signature)), @"\s+", " ");
		Assert.Contains("Pat Smith", text);
		Assert.Contains("Owner", text);
		Assert.Contains("Insured", text);
		Assert.Contains("Date: 07/01/2026", text);
	}

	[Fact]
	public async Task The_e_signature_anchor_is_in_the_pdf_but_invisible()
	{
		using var document = PdfDocument.Open(await PdfAsync(Signature));
		var letters = document.GetPage(1).Letters.ToList();
		var text = string.Concat(letters.Select(l => l.Value));
		var at = text.IndexOf("\\s1\\", StringComparison.Ordinal);
		Assert.True(at >= 0, text);
		var (r, g, b) = letters[at + 1].Color.ToRGBValues();
		Assert.Equal((1.0, 1.0, 1.0), (Math.Round(r, 2), Math.Round(g, 2), Math.Round(b, 2)));
		Assert.True(letters[at + 1].PointSize <= 1.5, letters[at + 1].PointSize.ToString());
	}

	// ---- Word export -------------------------------------------------------------------------------------------------

	[Fact]
	public void The_word_export_reports_barcodes_and_charts_and_keeps_signature_lines()
	{
		var exporter = factory.Services.GetRequiredService<DocxTemplateExporter>();
		var export = exporter.Export(
			"<span class=\"doc-barcode\">{{ policy.number | barcode: \"qr\" }}</span><div class=\"doc-chart\">{{ losses | chart: \"pie\", \"year\", \"amount\" }}</div>" + Signature, "");
		Assert.Contains(export.Warnings, w => w.StartsWith("Barcodes and QR codes can't be exported", StringComparison.Ordinal));
		Assert.Contains(export.Warnings, w => w.StartsWith("Charts can't be exported", StringComparison.Ordinal));
		Assert.Contains(export.Warnings, w => w.StartsWith("E-signature anchors", StringComparison.Ordinal));
		using var document = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Open(new MemoryStream(export.Docx), false);
		var body = document.MainDocumentPart!.Document.Body!;
		Assert.DoesNotContain("\\s1\\", body.InnerText);
		Assert.Contains("{{policy.signer}}", body.InnerText);
		Assert.Contains(body.Descendants<DocumentFormat.OpenXml.Wordprocessing.BottomBorder>(), b => b.Val! == DocumentFormat.OpenXml.Wordprocessing.BorderValues.Single);
	}
}
