using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using FaCT.DocDesigner.POC.Export;
using FaCT.DocDesigner.POC.Rendering;
using FaCT.DocDesigner.POC.Tests.Clauses;
using Microsoft.Extensions.DependencyInjection;
using UglyToad.PdfPig;

namespace FaCT.DocDesigner.POC.Tests.ConditionalStyling;

/// <summary>Conditional styling in rendered documents: {% if %} class names in class attributes, styled by the brand CSS.</summary>
public sealed class ConditionalStyleRenderTests(ClauseAppFactory factory) : IClassFixture<ClauseAppFactory>
{
	private static readonly JsonElement Data = JsonDocument.Parse("""
		{ "policy": { "status": "void", "lossRatio": 1.25, "sprinklered": false },
		  "claims": [ { "number": "C-1", "open": true }, { "number": "C-2", "open": false }, { "number": "C-3", "open": true } ] }
		""").RootElement.Clone();

	private DocumentComposer Composer => factory.Services.GetRequiredService<DocumentComposer>();

	private async Task<string> HtmlAsync(string template, JsonElement? data = null)
	{
		var result = await Composer.ComposeAsync(template, "", data ?? Data);
		Assert.True(result.Error is null, result.Error);
		return result.Html![result.Html.IndexOf("<body>", StringComparison.Ordinal)..];
	}

	[Theory]
	[InlineData("policy.status == 'void'", true)]
	[InlineData("policy.status == 'active'", false)]
	[InlineData("policy.status != 'active'", true)]
	[InlineData("policy.lossRatio > 1", true)]
	[InlineData("policy.lossRatio < 1", false)]
	[InlineData("policy.status contains 'vo'", true)]
	[InlineData("policy.status != blank", true)]
	[InlineData("policy.missing == blank", true)]
	[InlineData("policy.sprinklered == false", true)]
	public async Task A_rule_adds_its_class_only_when_the_condition_holds(string condition, bool holds)
	{
		var html = await HtmlAsync($"<p class=\"note {{% if {condition} %}}cs-red-text{{% endif %}}\">Text</p>");
		Assert.Contains(holds ? "<p class=\"note cs-red-text\">Text</p>" : "<p class=\"note \">Text</p>", html);
	}

	[Fact]
	public async Task Several_rules_add_several_classes()
	{
		var html = await HtmlAsync("<p class=\"x {% if policy.status == 'void' %}cs-red-text{% endif %} {% if policy.lossRatio > 1 %}cs-bold{% endif %} {% if policy.lossRatio > 2 %}cs-highlight-red{% endif %}\">Text</p>");
		Assert.Contains("class=\"x cs-red-text cs-bold \"", html);
	}

	[Fact]
	public async Task Rules_inside_a_list_are_checked_for_every_item()
	{
		var html = await HtmlAsync("<table><tbody>{% for claim in claims %}<tr class=\"{% if claim.open == true %}cs-highlight-yellow{% endif %}\"><td>{{ claim.number }}</td></tr>{% endfor %}</tbody></table>");
		Assert.Contains("<tr class=\"cs-highlight-yellow\"><td>C-1</td></tr><tr class=\"\"><td>C-2</td></tr><tr class=\"cs-highlight-yellow\"><td>C-3</td></tr>", html);
	}

	[Fact]
	public async Task The_brand_stylesheet_has_every_style()
	{
		var result = await Composer.ComposeAsync("<p>x</p>", "", Data);
		foreach (var style in new[] { "red-text", "green-text", "muted", "bold", "italic", "strike", "highlight-yellow", "highlight-red", "highlight-green" })
		{
			Assert.Contains(".cs-" + style + " {", result.Html!);
		}
		Assert.Contains("tr.cs-highlight-yellow > td", result.Html!);
	}

	[Fact]
	public async Task Data_cannot_inject_classes_or_markup()
	{
		var data = JsonDocument.Parse("""{ "policy": { "status": "x\" onclick=\"alert(1)" } }""").RootElement;
		var html = await HtmlAsync("<p class=\"{% if policy.status != blank %}cs-bold{% endif %}\">{{ policy.status }}</p>", data);
		// the value is only ever text inside the element, never an attribute
		Assert.Contains("<p class=\"cs-bold\">x\" onclick=\"alert(1)</p>", html);
	}

	[Fact]
	public async Task The_pdf_prints_the_style()
	{
		var client = factory.CreateClient();
		async Task<(int R, int G, int B)> ColorAsync(string status)
		{
			var response = await client.PostAsJsonAsync("/api/render", new JsonObject
			{
				["html"] = "<p class=\"{% if policy.status == 'void' %}cs-red-text{% endif %}\">Marked</p>",
				["css"] = "",
				["data"] = new JsonObject { ["policy"] = new JsonObject { ["status"] = status } }
			});
			Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
			using var document = PdfDocument.Open(await response.Content.ReadAsByteArrayAsync());
			var letter = document.GetPage(1).Letters.First(l => l.Value == "M");
			var (r, g, b) = letter.Color.ToRGBValues();
			return ((int)Math.Round(r * 255), (int)Math.Round(g * 255), (int)Math.Round(b * 255));
		}
		Assert.Equal((0xD9, 0x34, 0x2B), await ColorAsync("void"));
		Assert.Equal((0x2D, 0x29, 0x27), await ColorAsync("active"));
	}

	[Fact]
	public async Task The_designers_capture_form_renders_the_same()
	{
		const string template = "{% for claim in claims %}{% capture cs_abc %}{% if claim.open == true %}cs-highlight-yellow{% endif %} {% if policy.lossRatio > 1 %}cs-bold{% endif %}{% endcapture %}<p class=\"row {{ cs_abc }}\">{{ claim.number }}</p>{% endfor %}";
		var html = await HtmlAsync(template);
		Assert.Contains("<p class=\"row cs-highlight-yellow cs-bold\">C-1</p><p class=\"row  cs-bold\">C-2</p><p class=\"row cs-highlight-yellow cs-bold\">C-3</p>", html);
	}

	[Fact]
	public void The_word_export_drops_the_capture_form_too()
	{
		var exporter = factory.Services.GetRequiredService<DocxTemplateExporter>();
		var export = exporter.Export("{% capture cs_abc %}{% if policy.lossRatio > 1 %}cs-bold{% endif %}{% endcapture %}<p class=\"note {{ cs_abc }}\">Ratio {{ policy.lossRatio }}</p>", "");
		Assert.Equal(["Conditional styling isn't carried into the Word template; that content prints in its normal style."], export.Warnings);
		using var document = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Open(new MemoryStream(export.Docx), false);
		Assert.Equal("Ratio {{policy.lossRatio}}", document.MainDocumentPart!.Document.Body!.Descendants<DocumentFormat.OpenXml.Wordprocessing.Paragraph>().First().InnerText);
	}

	[Fact]
	public void The_word_export_reports_conditional_styling_and_keeps_the_element()
	{
		var exporter = factory.Services.GetRequiredService<DocxTemplateExporter>();
		var export = exporter.Export("<p class=\"note {% if policy.status == 'void' %}cs-red-text{% endif %}\">Styled text {{ policy.status }}</p>", "");
		Assert.Contains(export.Warnings, w => w.StartsWith("Conditional styling isn't carried", StringComparison.Ordinal));
		using var document = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Open(new MemoryStream(export.Docx), false);
		var text = document.MainDocumentPart!.Document.Body!.InnerText;
		Assert.Contains("Styled text {{policy.status}}", text);
		Assert.DoesNotContain("cs-red-text", text);
		Assert.DoesNotContain("{%", text);
	}
}
