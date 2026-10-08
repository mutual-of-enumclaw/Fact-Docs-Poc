#if DOCGEN_FILLER
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FaCT.DocDesigner.POC.Export;
using FaCT.DocDesigner.POC.Rendering;
using FaCT.DocDesigner.POC.Tests.Clauses;
using FaCT.DocDesigner.POC.Tests.PageSetup;
using Microsoft.Extensions.DependencyInjection;
using MoE.Commercial.Documents.Generation.Service.CustomDocuments.Docx;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace FaCT.DocDesigner.POC.Tests.Export;

/// <summary>
/// Do the exports give the same document as the designer's PDF? The same design and data go down both roads:
/// <list type="bullet">
/// <item>Designer: Liquid + HTML => Chromium PDF (Preview, Render Published).</item>
/// <item>DocGen: Word template export => DocGen's own DocxTemplateFiller (compiled in from fact-docgen) => LibreOffice PDF,
/// which is how DocGen makes custom documents.</item>
/// </list>
/// The words on each page (body, header and footer) must match. Fonts and line breaks differ between the two renderers,
/// so layout is compared only where it is fixed: page size, margins and forced page breaks. Features Word templates
/// can't carry must be reported as export warnings, never dropped silently.
/// </summary>
public sealed class ExportParityTests(ClauseAppFactory factory) : IClassFixture<ClauseAppFactory>
{
	private static readonly JsonElement Data = JsonDocument.Parse("""
		{
		  "policy": { "number": "CPP1234567", "insured": "Acme Bakery", "premium": 1234.5, "effective": "2026-07-01",
		              "rate": 0.125, "status": "active", "units": 4200, "deductible": 2500, "notes": "Call before visiting",
		              "lob": "Commercial Package" },
		  "locations": [ { "address": "100 Main St", "fire": 7 }, { "address": "200 Oak Ave", "fire": 3 } ],
		  "claims": [ { "number": "C-1", "loss": 1500 }, { "number": "C-2", "loss": 250.75 } ]
		}
		""").RootElement.Clone();

	private DocumentComposer Composer => factory.Services.GetRequiredService<DocumentComposer>();
	private PdfRenderer Renderer => factory.Services.GetRequiredService<PdfRenderer>();
	private DocxTemplateExporter Exporter => factory.Services.GetRequiredService<DocxTemplateExporter>();

	private sealed record Road(byte[] Pdf, IReadOnlyList<string> Warnings, IReadOnlyList<string> Unresolved);

	private async Task<byte[]> ChromiumAsync(string html, JsonElement data)
	{
		var composed = await Composer.ComposeAsync(html, "", data);
		Assert.True(composed.Error is null, composed.Error);
		return await Renderer.RenderAsync(composed.Html!);
	}

	private async Task<Road> DocGenAsync(string html, JsonElement data)
	{
		var export = Exporter.Export(html, "");
		var filled = new DocxTemplateFiller().Fill(export.Docx, data);
		return new Road(await LibreOffice.ToPdfAsync(filled.Docx), export.Warnings, filled.UnresolvedTokens);
	}

	/// <summary>Per page: the words of the top margin, the body and the bottom margin, in reading order, normalized.</summary>
	private static List<(string Header, string Body, string Footer)> Pages(byte[] pdf, double topInches = 0.5, double bottomInches = 0.6)
	{
		using var document = PdfDocument.Open(pdf);
		return document.GetPages().Select(page =>
		{
			var words = page.GetWords().ToList();
			var top = page.Height - topInches * 72;
			var bottom = bottomInches * 72;
			return (
				Normalize(words.Where(w => w.BoundingBox.Bottom >= top)),
				Normalize(words.Where(w => w.BoundingBox.Bottom < top && w.BoundingBox.Top > bottom)),
				Normalize(words.Where(w => w.BoundingBox.Top <= bottom)));
		}).ToList();
	}

	// Lines top to bottom (words within 3pt of a baseline are one line), left to right; case and list markers ignored.
	private static string Normalize(IEnumerable<Word> words)
	{
		var lines = new List<List<Word>>();
		foreach (var word in words.OrderByDescending(w => w.BoundingBox.Bottom))
		{
			var line = lines.FirstOrDefault(l => Math.Abs(l[0].BoundingBox.Bottom - word.BoundingBox.Bottom) <= 3);
			if (line is null) lines.Add([word]);
			else line.Add(word);
		}
		var text = string.Join(" ", lines.Select(l => string.Join(" ", l.OrderBy(w => w.BoundingBox.Left).Select(w => w.Text))));
		var tokens = Regex.Split(text, @"\s+").Select(t => Regex.Replace(t, @"[^\p{L}\p{N}$.,%/:\-]", "")).Where(t => t.Length > 0);
		return string.Join(" ", tokens).ToLowerInvariant();
	}

	private static string Body(byte[] pdf) => string.Join(" | ", Pages(pdf).Select(p => p.Body));

	private async Task AssertSameAsync(string html, string expected, JsonElement? data = null)
	{
		var payload = data ?? Data;
		var chromium = await ChromiumAsync(html, payload);
		var docgen = await DocGenAsync(html, payload);
		Assert.Empty(docgen.Warnings);
		Assert.Empty(docgen.Unresolved);
		Assert.Equal(Body(chromium), Body(docgen.Pdf));
		var body = Body(docgen.Pdf);
		Assert.True(body.Contains(expected, StringComparison.Ordinal), body);
	}

	// ---- body text -------------------------------------------------------------------------------------------------

	[Fact]
	public async Task Text_fields_and_formats_print_the_same()
	{
		await AssertSameAsync("""
			<h1>Policy summary</h1>
			<h2>The insured</h2>
			<p>{{ policy.insured }} holds policy {{ policy.number }} for {{ policy.lob | upcase }}.</p>
			<p>Premium {{ policy.premium | currency }}, deductible {{ policy.deductible | dollars }}, rate {{ policy.rate | percent }}.</p>
			<p>Effective {{ policy.effective | shortdate }}; {{ policy.units | number }} units at {{ policy.premium | decimal }}.</p>
			""", "acme bakery holds policy cpp1234567 for commercial package. premium $1,234.50, deductible $2,500, rate 12.50%. effective 07/01/2026 4,200 units at 1,234.50.");
	}

	[Fact]
	public async Task Repeated_sections_print_the_same()
	{
		await AssertSameAsync("""
			<h1>Locations</h1>
			{% for loc in locations %}<h3>{{ loc.address }}</h3><p>Fire score {{ loc.fire }}</p>{% endfor %}
			<p>End of locations</p>
			""", "100 main st fire score 7 200 oak ave fire score 3 end of locations");
	}

	[Fact]
	public async Task Table_rows_print_the_same()
	{
		await AssertSameAsync("""
			<table class="moe-table"><thead><tr><th>Claim</th><th>Loss</th></tr></thead>
			<tbody>{% for c in claims %}<tr><td>{{ c.number }}</td><td class="num">{{ c.loss | currency }}</td></tr>{% endfor %}</tbody></table>
			<p>After the table</p>
			""", "claim loss c-1 $1,500.00 c-2 $250.75 after the table");
	}

	[Theory]
	[InlineData("active", "Call before visiting", "the policy is in force. note: call before visiting done")]
	[InlineData("cancelled", "", "the policy is not in force. done")]
	public async Task Conditions_choose_the_same_content(string status, string notes, string expected)
	{
		var data = JsonNode.Parse(Data.GetRawText())!;
		data["policy"]!["status"] = status;
		data["policy"]!["notes"] = notes;
		await AssertSameAsync("""
			{% if policy.status == "active" %}<p>The policy is in force.</p>{% else %}<p>The policy is not in force.</p>{% endif %}
			{% if policy.notes != blank %}<p>Note: {{ policy.notes }}</p>{% endif %}
			<p>Done</p>
			""", expected, JsonDocument.Parse(data.ToJsonString()).RootElement);
	}

	// ---- pages, headers and footers -------------------------------------------------------------------------------

	[Fact]
	public async Task The_standard_page_and_footer_are_the_same()
	{
		const string html = "<p>First sheet</p><div class=\"page-break\"></div><p>Second sheet</p>";
		var chromium = await ChromiumAsync(html, Data);
		var docgen = await DocGenAsync(html, Data);

		using (var a = PdfDocument.Open(chromium))
		using (var b = PdfDocument.Open(docgen.Pdf))
		{
			Assert.Equal(a.NumberOfPages, b.NumberOfPages);
			Assert.Equal(a.GetPage(1).Width, b.GetPage(1).Width, 1.0);
			Assert.Equal(a.GetPage(1).Height, b.GetPage(1).Height, 1.0);
			// the same left margin
			Assert.Equal(a.GetPage(1).GetWords().First().BoundingBox.Left, b.GetPage(1).GetWords().First(w => w.Text == "First").BoundingBox.Left, 3.0);
		}
		Assert.Equal(Pages(chromium).Select(p => p.Footer), Pages(docgen.Pdf).Select(p => p.Footer));
		Assert.Equal("mutual of enumclaw page 2 of 2", Pages(docgen.Pdf)[1].Footer);
	}

	[Theory]
	[InlineData("letter", "portrait")]
	[InlineData("legal", "landscape")]
	[InlineData("a4", "portrait")]
	public async Task Paper_size_and_orientation_are_the_same(string size, string orientation)
	{
		var html = Setup.Markup(size, orientation) + "<p>Body</p>";
		using var a = PdfDocument.Open(await ChromiumAsync(html, Data));
		using var b = PdfDocument.Open((await DocGenAsync(html, Data)).Pdf);
		Assert.Equal(a.GetPage(1).Width, b.GetPage(1).Width, 1.5);
		Assert.Equal(a.GetPage(1).Height, b.GetPage(1).Height, 1.5);
	}

	[Fact]
	public async Task Margins_are_the_same()
	{
		var html = Setup.Markup(margins: "1_0.75_1_1.5") + "<p>Indented text</p>";
		using var a = PdfDocument.Open(await ChromiumAsync(html, Data));
		using var b = PdfDocument.Open((await DocGenAsync(html, Data)).Pdf);
		Assert.Equal(108, a.GetPage(1).GetWords().First(w => w.Text == "Indented").BoundingBox.Left, 3.0);
		Assert.Equal(108, b.GetPage(1).GetWords().First(w => w.Text == "Indented").BoundingBox.Left, 3.0);
	}

	[Fact]
	public async Task Header_and_footer_with_fields_and_page_numbers_are_the_same()
	{
		var html = Setup.Markup(inner:
			Setup.Slots("header", "default", "{{ policy.insured }}", "", "Policy {{ policy.number }}") +
			Setup.Slots("footer", "default", "Mutual Of Enumclaw", "Printed {{ 'now' | date: '%m/%d/%Y' }}", Setup.PageOfPages)) +
			"<p>Sheet one</p><div class=\"page-break\"></div><p>Sheet two</p><div class=\"page-break\"></div><p>Sheet three</p>";
		var chromium = Pages(await ChromiumAsync(html, Data));
		var docgen = await DocGenAsync(html, Data);
		Assert.Empty(docgen.Warnings);
		var word = Pages(docgen.Pdf);

		Assert.Equal(3, word.Count);
		Assert.Equal(chromium.Select(p => p.Header), word.Select(p => p.Header));
		Assert.Equal(chromium.Select(p => p.Footer), word.Select(p => p.Footer));
		Assert.Equal(chromium.Select(p => p.Body), word.Select(p => p.Body));
		Assert.Equal("acme bakery policy cpp1234567", word[0].Header);
		Assert.Equal($"mutual of enumclaw printed {DateTime.Now:MM/dd/yyyy} page 3 of 3", word[2].Footer);
	}

	[Fact]
	public async Task First_page_and_even_page_variants_are_the_same()
	{
		var html = Setup.Markup(inner:
			Setup.Slots("header", "default", "Odd header") + Setup.Slots("footer", "default", "", "", Setup.PageOfPages) +
			Setup.Slots("header", "first", "Cover") + Setup.Slots("footer", "first") +
			Setup.Slots("header", "even", "", "", "Even header") + Setup.Slots("footer", "even", "Even " + Setup.PageOfPages)) +
			string.Join("<div class=\"page-break\"></div>", Enumerable.Range(1, 4).Select(n => $"<p>Sheet {n}</p>"));
		var chromium = Pages(await ChromiumAsync(html, Data));
		var word = Pages((await DocGenAsync(html, Data)).Pdf);

		Assert.Equal(["cover", "even header", "odd header", "even header"], word.Select(p => p.Header));
		Assert.Equal(["", "even page 2 of 4", "page 3 of 4", "even page 4 of 4"], word.Select(p => p.Footer));
		Assert.Equal(chromium.Select(p => p.Header), word.Select(p => p.Header));
		Assert.Equal(chromium.Select(p => p.Footer), word.Select(p => p.Footer));
	}

	[Fact]
	public async Task The_page_setup_never_prints_in_the_body()
	{
		var html = Setup.Markup(inner: Setup.Slots("footer", "default", "Footer words")) + "<p>Body words</p>";
		var word = Pages((await DocGenAsync(html, Data)).Pdf);
		Assert.Equal("body words", word[0].Body);
		Assert.Equal("footer words", word[0].Footer);
	}

	// ---- what Word templates can't carry is reported ------------------------------------------------------------

	[Theory]
	[InlineData("<div class=\"doc-theme theme-farm\"></div><p>x</p>", "theme")]
	[InlineData("<div class=\"doc-watermark wm-color-grey wm-strength-medium wm-size-medium wm-angle-diagonal\"><span class=\"doc-watermark-text\">DRAFT</span></div><p>x</p>", "watermark")]
	[InlineData("{% assign calc_t1 = policy.premium | times: 2 %}<p>{{ calc_t1 | calc_round: 2 | calc_value }}</p>", "Calculated")]
	[InlineData("<p>{{ claims | sum: \"loss\" | currency }}</p>", "sum")]
	[InlineData("<p>{{ policy.missing | default: \"None\" }}</p>", "default")]
	[InlineData("<div class=\"form-page\"><p>Fixed</p></div>", "form pages")]
	public void What_cannot_be_exported_is_reported(string html, string expected)
	{
		var export = Exporter.Export(html, "");
		Assert.Contains(export.Warnings, w => w.Contains(expected, StringComparison.OrdinalIgnoreCase));
	}

	// ---- custom formats (item 23): the same .NET format in both, so the same text -------------------------------------

	[Fact]
	public async Task Plain_booleans_and_numbers_print_the_same()
	{
		var data = JsonDocument.Parse("""
			{ "risk": { "sprinklered": true, "vacant": false, "area": 1200.0, "rate": 1234.50, "units": 42, "share": 0.25 } }
			""").RootElement;
		await AssertSameAsync("""
			<p>Sprinklered {{ risk.sprinklered }} vacant {{ risk.vacant }}</p>
			<p>Area {{ risk.area }} rate {{ risk.rate }} units {{ risk.units }} share {{ risk.share }}</p>
			""", "sprinklered yes vacant no area 1200 rate 1234.5 units 42 share 0.25", data);
	}

	[Fact]
	public async Task Custom_number_and_date_formats_print_the_same()
	{
		var data = JsonNode.Parse(Data.GetRawText())!;
		data["policy"]!["credit"] = -250;
		data["policy"]!["fees"] = 0;
		await AssertSameAsync("""
			<p>Premium {{ policy.premium | format: "$#,##0.00;($#,##0.00);'None'" }}</p>
			<p>Credit {{ policy.credit | format: "$#,##0.00;($#,##0.00);'None'" }}</p>
			<p>Fees {{ policy.fees | format: "$#,##0.00;($#,##0.00);'None'" }}</p>
			<p>Units {{ policy.units | format: "#,##0;-#,##0;'Included'" }} rate {{ policy.rate | format: "0.0%" }}</p>
			<p>Effective {{ policy.effective | format: "MMMM d, yyyy" }} ({{ policy.effective | format: "dddd" }})</p>
			""", "premium $1,234.50 credit $250.00 fees none units 4,200 rate 12.5% effective july 1, 2026 wednesday",
			JsonDocument.Parse(data.ToJsonString()).RootElement);
	}

	[Fact]
	public void Custom_formats_become_docgen_format_tokens()
	{
		var export = Exporter.Export("<p>{{ policy.premium | format: \"$#,##0.00;($#,##0.00);'None'\" }} {{ d | format: \"MMMM d, yyyy\" }}</p>", "");
		Assert.Empty(export.Warnings);
		using var document = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Open(new MemoryStream(export.Docx), false);
		Assert.Contains("{{policy.premium:$#,##0.00;($#,##0.00);'None'}} {{d:MMMM d, yyyy}}", document.MainDocumentPart!.Document.Body!.InnerText);
	}

	[Theory]
	[InlineData("<p>{{ d | format: \"d 'de' MMMM\", \"es-US\" }}</p>", "US English")]
	[InlineData("<p>{{ phone | mask: \"(###) ###-####\" }}</p>", "Masks")]
	public void Language_formats_and_masks_are_reported(string html, string expected)
	{
		Assert.Contains(Exporter.Export(html, "").Warnings, w => w.Contains(expected, StringComparison.Ordinal));
	}

	// ---- the HTML export -----------------------------------------------------------------------------------------

	[Fact]
	public async Task The_filled_html_export_is_exactly_what_the_pdf_is_made_from()
	{
		const string html = "<h1>Summary</h1><p>{{ policy.insured }} {{ policy.premium | currency }}</p>{% for c in claims %}<p>{{ c.number }}</p>{% endfor %}";
		var client = factory.CreateClient();
		using var response = await client.PostAsJsonAsync("/api/export/html?withData=true", new { html, css = ".x{color:red}", data = Data });
		response.EnsureSuccessStatusCode();
		var exported = await response.Content.ReadAsStringAsync();
		var composed = await Composer.ComposeAsync(html, ".x{color:red}", Data);
		Assert.Equal(composed.Html, exported);

		var fromExport = await Renderer.RenderAsync(exported);
		using var preview = await client.PostAsJsonAsync("/api/render", new { html, css = ".x{color:red}", data = Data });
		Assert.Equal(Pages(await preview.Content.ReadAsByteArrayAsync()), Pages(fromExport));
	}
}
#endif
