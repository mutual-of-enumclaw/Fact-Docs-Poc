using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using FaCT.DocDesigner.POC.Rendering;
using FaCT.DocDesigner.POC.Tests.Clauses;
using FaCT.DocDesigner.POC.Tests.Import;
using FaCT.DocDesigner.POC.Tests.PageSetup;
using Microsoft.Extensions.DependencyInjection;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace FaCT.DocDesigner.POC.Tests.Watermarks;

/// <summary>Watermarks in the PDF: the template's own (designed, optionally conditional) and words stamped at render time.</summary>
public sealed class WatermarkRenderTests(ClauseAppFactory factory) : IClassFixture<ClauseAppFactory>
{
	private readonly HttpClient _client = factory.CreateClient();

	private static readonly JsonObject Data = new() { ["policy"] = new JsonObject { ["number"] = "CPP7654321", ["status"] = "active" } };

	/// <summary>The markup the designer exports for a watermark.</summary>
	internal static string Mark(string text, string color = "grey", string strength = "medium", string size = "medium", string angle = "diagonal") =>
		$"<div class=\"doc-watermark wm-color-{color} wm-strength-{strength} wm-size-{size} wm-angle-{angle}\"><span class=\"doc-watermark-text\">{text}</span></div>";

	private async Task<HttpResponseMessage> PostAsync(string html, string? watermark = null, JsonObject? data = null)
	{
		var body = new JsonObject { ["html"] = html, ["css"] = "", ["data"] = (data ?? Data).DeepClone() };
		if (watermark is not null) body["watermark"] = watermark;
		return await _client.PostAsJsonAsync("/api/render", body);
	}

	private async Task<byte[]> RenderAsync(string html, string? watermark = null, JsonObject? data = null)
	{
		var response = await PostAsync(html, watermark, data);
		Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
		return await response.Content.ReadAsByteArrayAsync();
	}

	/// <summary>Each page's letters in drawing order (rotated words aren't split into words reliably).</summary>
	private static List<string> PageLetters(byte[] pdf)
	{
		using var document = PdfDocument.Open(pdf);
		return document.GetPages().Select(p => string.Concat(p.Letters.Select(l => l.Value))).ToList();
	}

	private static List<Letter> LettersOf(byte[] pdf, string word, int page = 1)
	{
		using var document = PdfDocument.Open(pdf);
		var letters = document.GetPage(page).Letters.ToList();
		var all = string.Concat(letters.Select(l => l.Value));
		var at = all.IndexOf(word, StringComparison.Ordinal);
		Assert.True(at >= 0, $"'{word}' not on page {page}: {all}");
		return letters.Skip(at).Take(word.Length).ToList();
	}

	/// <summary>The darkest pixel in the middle of the page (where the watermark is), composited over white.</summary>
	private static (int Luminance, int R, int G, int B) Darkest(byte[] pdf, int page = 1)
	{
		var (bgra, width, height) = PageImages.Pixels(pdf, page);
		var best = (Luminance: 256, R: 0, G: 0, B: 0);
		for (var y = (int)(height * 0.3); y < height * 0.7; y++)
		{
			for (var x = (int)(width * 0.2); x < width * 0.8; x++)
			{
				var i = (y * width + x) * 4;
				int a = bgra[i + 3];
				int Over(int c) => (c * a + 255 * (255 - a)) / 255;
				int b = Over(bgra[i]), g = Over(bgra[i + 1]), r = Over(bgra[i + 2]);
				var luminance = (r * 299 + g * 587 + b * 114) / 1000;
				if (luminance < best.Luminance) best = (luminance, r, g, b);
			}
		}
		return best;
	}

	// ---- The template's own watermark ----------------------------------------------------------------------------

	[Fact]
	public async Task A_watermark_prints_on_every_page_and_the_text_under_it_stays()
	{
		var pdf = await RenderAsync(Mark("DRAFT") + Setup.Pages(3));
		var pages = PageLetters(pdf);
		Assert.Equal(3, pages.Count);
		for (var i = 0; i < 3; i++)
		{
			Assert.Contains("DRAFT", pages[i]);
			Assert.Contains($"Body text on sheet {i + 1}", pages[i]);
		}
	}

	[Fact]
	public async Task Text_under_the_watermark_is_still_readable()
	{
		var lines = string.Concat(Enumerable.Range(1, 45).Select(n => $"<p>Coverage line {n}</p>"));
		var text = System.Text.RegularExpressions.Regex.Replace(PdfText.Extract(await RenderAsync(Mark("SPECIMEN", angle: "horizontal") + lines)), @"\s+", " ");
		Assert.Contains("Coverage line 1", text);
		Assert.Contains("Coverage line 20", text);
		Assert.Contains("Coverage line 45", text);
	}

	[Fact]
	public async Task The_watermark_sits_in_the_middle_of_the_page()
	{
		// Painted, see-through pixels (not the black body text or the footer) in PDF points from the top-left.
		var (bgra, width, height) = PageImages.Pixels(await RenderAsync(Mark("VOID", angle: "horizontal") + "<p>Body</p>"));
		int left = width, right = 0, top = height, bottom = 0;
		for (var y = (int)(height * 0.1); y < height * 0.9; y++)
		{
			for (var x = 0; x < width; x++)
			{
				var i = (y * width + x) * 4;
				int a = bgra[i + 3];
				var luminance = ((bgra[i + 2] * 299 + bgra[i + 1] * 587 + bgra[i] * 114) / 1000 * a + 255 * (255 - a)) / 255;
				if (luminance > 245) continue;
				(left, right, top, bottom) = (Math.Min(left, x), Math.Max(right, x), Math.Min(top, y), Math.Max(bottom, y));
			}
		}
		var scale = 1 / PageImages.Scale;
		Assert.Equal(306, (left + right) / 2.0 * scale, 12.0);
		// The page area is between the 0.5in top and 0.6in bottom margins: its middle is 3.6pt above the sheet's.
		Assert.Equal(396 - 3.6, (top + bottom) / 2.0 * scale, 20.0);
		Assert.True((right - left) * scale > 150, $"width {(right - left) * scale}");
	}

	[Fact]
	public async Task Diagonal_runs_corner_to_corner_and_horizontal_runs_level()
	{
		var diagonal = LettersOf(await RenderAsync(Mark("DRAFT") + "<p>Body</p>"), "DRAFT");
		var level = LettersOf(await RenderAsync(Mark("DRAFT", angle: "horizontal") + "<p>Body</p>"), "DRAFT");
		Assert.True(diagonal[^1].StartBaseLine.Y - diagonal[0].StartBaseLine.Y > 100,
			$"diagonal rise {diagonal[^1].StartBaseLine.Y - diagonal[0].StartBaseLine.Y}");
		Assert.Equal(level[0].StartBaseLine.Y, level[^1].StartBaseLine.Y, 1.0);
	}

	[Fact]
	public async Task Bigger_sizes_print_wider()
	{
		async Task<double> Width(string size)
		{
			var letters = LettersOf(await RenderAsync(Mark("COPY", size: size, angle: "horizontal") + "<p>Body</p>"), "COPY");
			return letters.Max(l => l.GlyphRectangle.Right) - letters.Min(l => l.GlyphRectangle.Left);
		}
		var small = await Width("small");
		var medium = await Width("medium");
		var large = await Width("large");
		Assert.True(small < medium && medium < large, $"small {small}, medium {medium}, large {large}");
		Assert.Equal(96.0 / 60, medium / small, 0.1);
	}

	[Fact]
	public async Task Strength_sets_how_see_through_it_is()
	{
		var light = Darkest(await RenderAsync(Mark("VOID", strength: "light") + "<p>Body</p>"));
		var medium = Darkest(await RenderAsync(Mark("VOID", strength: "medium") + "<p>Body</p>"));
		var strong = Darkest(await RenderAsync(Mark("VOID", strength: "strong") + "<p>Body</p>"));
		Assert.True(light.Luminance > medium.Luminance && medium.Luminance > strong.Luminance,
			$"light {light.Luminance}, medium {medium.Luminance}, strong {strong.Luminance}");
		// See-through: never close to the black of the text, never invisible.
		Assert.InRange(light.Luminance, 200, 250);
		Assert.InRange(strong.Luminance, 150, 235);
	}

	[Theory]
	[InlineData("grey")]
	[InlineData("red")]
	[InlineData("green")]
	[InlineData("blue")]
	public async Task Color_choices_print_in_that_color(string color)
	{
		var (_, r, g, b) = Darkest(await RenderAsync(Mark("VOID", color, strength: "strong") + "<p>Body</p>"));
		switch (color)
		{
			case "grey": Assert.True(Math.Abs(r - b) < 20 && Math.Abs(r - g) < 20, $"{r},{g},{b}"); break;
			case "red": Assert.True(r - b > 40 && r - g > 40, $"{r},{g},{b}"); break;
			case "green": Assert.True(g - r > 15 && b < r + 30, $"{r},{g},{b}"); break;
			case "blue": Assert.True(b - r > 20, $"{r},{g},{b}"); break;
		}
	}

	[Fact]
	public async Task A_watermark_with_a_condition_prints_only_when_it_holds()
	{
		var html = "{% if policy.status == \"void\" %}" + Mark("VOID", "red") + "{% endif %}<p>Body</p>";
		var voided = new JsonObject { ["policy"] = new JsonObject { ["status"] = "void" } };
		Assert.Contains("VOID", PageLetters(await RenderAsync(html, data: voided))[0]);
		Assert.DoesNotContain("VOID", PageLetters(await RenderAsync(html))[0]);
	}

	[Fact]
	public async Task Watermarks_work_with_a_page_setup()
	{
		var setup = Setup.Markup("legal", "landscape", inner: Setup.Slots("footer", "default", "Footer text"));
		var pdf = await RenderAsync(setup + Mark("SAMPLE") + Setup.Pages(2));
		using var document = PdfDocument.Open(pdf);
		Assert.All(document.GetPages(), p =>
		{
			Assert.Equal(1008, p.Width, 1.0);
			Assert.Contains("SAMPLE", string.Concat(p.Letters.Select(l => l.Value)));
		});
	}

	[Fact]
	public async Task Watermark_text_is_markup_safe()
	{
		var composer = factory.Services.GetRequiredService<DocumentComposer>();
		var result = await composer.ComposeAsync(Mark("VOID<script>alert(1)</script>"), "", JsonDocument.Parse("{}").RootElement);
		Assert.DoesNotContain("<script", result.Html!, StringComparison.OrdinalIgnoreCase);
		Assert.Contains("<div class=\"doc-watermark wm-color-grey wm-strength-medium wm-size-medium wm-angle-diagonal\">", result.Html!);
	}

	[Fact]
	public async Task The_brand_stylesheet_carries_the_watermark_styles()
	{
		var composer = factory.Services.GetRequiredService<DocumentComposer>();
		var html = (await composer.ComposeAsync("<p>x</p>", "", JsonDocument.Parse("{}").RootElement)).Html!;
		foreach (var rule in new[] { ".doc-watermark {", ".wm-color-red", ".wm-strength-light", ".wm-size-large", ".wm-angle-horizontal", ".doc-watermark-stamped" })
		{
			Assert.Contains(rule, html);
		}
	}

	// ---- Stamped at render time ----------------------------------------------------------------------------------

	[Fact]
	public async Task A_stamp_prints_on_every_page_and_replaces_the_templates_watermark()
	{
		var pdf = await RenderAsync(Mark("COPY") + Setup.Pages(2), watermark: "SPECIMEN");
		Assert.All(PageLetters(pdf), page =>
		{
			Assert.Contains("SPECIMEN", page);
			Assert.DoesNotContain("COPY", page);
		});
	}

	[Fact]
	public async Task Without_a_stamp_the_templates_watermark_prints()
	{
		Assert.Contains("COPY", PageLetters(await RenderAsync(Mark("COPY") + "<p>Body</p>"))[0]);
		Assert.Contains("COPY", PageLetters(await RenderAsync(Mark("COPY") + "<p>Body</p>", watermark: "  "))[0]);
	}

	[Fact]
	public async Task A_stamp_on_a_template_without_a_watermark()
	{
		var pdf = await RenderAsync("<p>Body</p>", watermark: "Not for issue");
		Assert.Contains("Not for issue", PageLetters(pdf)[0]);
		Assert.True(Darkest(pdf).R - Darkest(pdf).B > 30, "stamps are red");
	}

	[Theory]
	[InlineData("<script>")]
	[InlineData("DRAFT{{ x }}")]
	[InlineData("a\"b")]
	[InlineData("12345678901234567890123456789012345678901")]
	public async Task Invalid_stamps_are_refused(string watermark)
	{
		var response = await PostAsync("<p>Body</p>", watermark);
		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		Assert.Equal(DocumentComposer.StampRule, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
	}

	[Theory]
	[InlineData("DRAFT", true)]
	[InlineData("Not for issue", true)]
	[InlineData("BORRADOR", true)]
	[InlineData("Muestra-2026.1", true)]
	[InlineData("ÉCHANTILLON", true)]
	[InlineData("", false)]
	[InlineData("   ", false)]
	[InlineData("a&b", false)]
	[InlineData("<b>", false)]
	public void Stamp_text_rule(string text, bool valid)
	{
		Assert.Equal(valid, DocumentComposer.IsValidStamp(text));
	}

	[Fact]
	public async Task The_composer_adds_the_stamp_last_and_marks_the_body()
	{
		var composer = factory.Services.GetRequiredService<DocumentComposer>();
		var html = (await composer.ComposeAsync("<p>x</p>", "", JsonDocument.Parse("{}").RootElement, "SPECIMEN")).Html!;
		Assert.Contains("<body class=\"doc-watermark-stamped\"><p>x</p><div class=\"doc-watermark doc-watermark-stamp ", html);
		Assert.Contains("<span class=\"doc-watermark-text\">SPECIMEN</span></div></body>", html);
		Assert.Equal(DocumentComposer.StampRule, (await composer.ComposeAsync("<p>x</p>", "", JsonDocument.Parse("{}").RootElement, "<x>")).Error);
	}

	// ---- Proofs of saved versions ----------------------------------------------------------------------------------

	private async Task<string> SaveAsync(string html, string? name = null)
	{
		name ??= "wm-" + Guid.NewGuid().ToString("N")[..8];
		(await _client.PutAsJsonAsync($"/api/templates/{name}/draft", new { project = new { }, html, css = "" })).EnsureSuccessStatusCode();
		return name;
	}

	private async Task PublishAsync(string name, int version) =>
		(await _client.PostAsync($"/api/templates/{name}/versions/{version}/publish", null)).EnsureSuccessStatusCode();

	private async Task<(HttpResponseMessage Response, string Letters)> ProofAsync(string url)
	{
		var response = await _client.PostAsync(url, null);
		Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
		return (response, string.Join("|", PageLetters(await response.Content.ReadAsByteArrayAsync())));
	}

	[Fact]
	public async Task A_draft_proof_is_stamped_draft()
	{
		var name = await SaveAsync("<p>Draft wording</p>");
		var (response, letters) = await ProofAsync($"/api/templates/{name}/pdf?version=1");
		Assert.Contains("DRAFT", letters);
		Assert.Contains("Draft wording", letters);
		Assert.Equal("DRAFT", response.Headers.GetValues("X-Watermark").Single());
		Assert.Equal("1 (Draft)", response.Headers.GetValues("X-Template-Version").Single());
	}

	[Fact]
	public async Task The_published_version_is_not_stamped()
	{
		var name = await SaveAsync("<p>Final wording</p>");
		await PublishAsync(name, 1);
		var (response, letters) = await ProofAsync($"/api/templates/{name}/pdf");
		Assert.DoesNotContain("DRAFT", letters);
		Assert.False(response.Headers.Contains("X-Watermark"));
		(response, letters) = await ProofAsync($"/api/templates/{name}/pdf?version=1");
		Assert.DoesNotContain("DRAFT", letters);
	}

	[Fact]
	public async Task A_retired_version_is_not_stamped_draft()
	{
		var name = await SaveAsync("<p>Old wording</p>");
		await PublishAsync(name, 1);
		await SaveAsync("<p>New wording</p>", name);
		await PublishAsync(name, 2);
		var (response, letters) = await ProofAsync($"/api/templates/{name}/pdf?version=1");
		Assert.Equal("1 (Retired)", response.Headers.GetValues("X-Template-Version").Single());
		Assert.DoesNotContain("DRAFT", letters);
	}

	[Fact]
	public async Task A_specimen_of_the_published_version()
	{
		var name = await SaveAsync(Mark("COPY") + "<p>Final wording</p>");
		await PublishAsync(name, 1);
		var (response, letters) = await ProofAsync($"/api/templates/{name}/pdf?watermark=SPECIMEN");
		Assert.Contains("SPECIMEN", letters);
		Assert.DoesNotContain("COPY", letters);
		Assert.Equal("SPECIMEN", response.Headers.GetValues("X-Watermark").Single());
	}

	[Fact]
	public async Task A_chosen_stamp_replaces_draft_on_a_draft_proof()
	{
		var name = await SaveAsync("<p>Draft wording</p>");
		var (_, letters) = await ProofAsync($"/api/templates/{name}/pdf?version=1&watermark=SAMPLE");
		Assert.Contains("SAMPLE", letters);
		Assert.DoesNotContain("DRAFT", letters);
	}

	[Fact]
	public async Task An_invalid_stamp_on_a_proof_is_refused()
	{
		var name = await SaveAsync("<p>x</p>");
		var response = await _client.PostAsync($"/api/templates/{name}/pdf?version=1&watermark=%3Cb%3E", null);
		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
	}
}
