using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using FaCT.DocDesigner.POC.Rendering;
using FaCT.DocDesigner.POC.Tests.Clauses;
using Microsoft.Extensions.DependencyInjection;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace FaCT.DocDesigner.POC.Tests.PageSetup;

/// <summary>Builds the page setup markup the designer exports (div.doc-setup with header/footer slots).</summary>
internal static class Setup
{
	public static string Slots(string part, string variant, string left = "", string center = "", string right = "") =>
		$"<div class=\"doc-hf doc-hf-{part} doc-hf-{variant}\"><span class=\"doc-hf-left\">{left}</span>" +
		$"<span class=\"doc-hf-center\">{center}</span><span class=\"doc-hf-right\">{right}</span></div>";

	public static string Markup(string size = "letter", string orientation = "portrait", string margins = "0.5_0.5_0.6_0.5", string font = "10", string inner = "") =>
		$"<div class=\"doc-setup ds-size-{size} ds-orient-{orientation} ds-margins-{margins} ds-font-{font}\">{inner}</div>";

	public const string PageOfPages = "Page <span class=\"doc-pageno\"></span> of <span class=\"doc-pagecount\"></span>";

	/// <summary>Body content that fills the given number of pages.</summary>
	public static string Pages(int count) => string.Concat(Enumerable.Range(1, count).Select(n =>
		$"<p style=\"{(n < count ? "break-after:page;" : "")}\">Body text on sheet {n}</p>"));
}

/// <summary>What a template's page setup does to the PDF: paper, orientation, margins and header/footer variants.</summary>
public sealed class PageSetupRenderTests(ClauseAppFactory factory) : IClassFixture<ClauseAppFactory>
{
	private readonly HttpClient _client = factory.CreateClient();

	private static readonly JsonObject Data = new()
	{
		["policy"] = new JsonObject { ["number"] = "CPP7654321", ["insured"] = "Acme <b>Bakery</b>" }
	};

	private async Task<byte[]> RenderAsync(string html, string css = "")
	{
		var response = await _client.PostAsJsonAsync("/api/render", new JsonObject { ["html"] = html, ["css"] = css, ["data"] = Data.DeepClone() });
		Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
		return await response.Content.ReadAsByteArrayAsync();
	}

	private static List<(double Width, double Height)> Sizes(byte[] pdf)
	{
		using var document = PdfDocument.Open(pdf);
		return document.GetPages().Select(p => (Math.Round(p.Width), Math.Round(p.Height))).ToList();
	}

	/// <summary>Per page: the words in the top margin, the body and the bottom margin.</summary>
	private static List<(string Header, string Body, string Footer)> Regions(byte[] pdf, double topInches, double bottomInches)
	{
		using var document = PdfDocument.Open(pdf);
		return document.GetPages().Select(page =>
		{
			var words = page.GetWords().ToList();
			string Join(IEnumerable<Word> w) => string.Join(" ", w.OrderByDescending(x => Math.Round(x.BoundingBox.Bottom)).ThenBy(x => x.BoundingBox.Left).Select(x => x.Text));
			var top = page.Height - topInches * 72;
			var bottom = bottomInches * 72;
			return (
				Join(words.Where(w => w.BoundingBox.Bottom >= top)),
				Join(words.Where(w => w.BoundingBox.Bottom < top && w.BoundingBox.Top > bottom)),
				Join(words.Where(w => w.BoundingBox.Top <= bottom)));
		}).ToList();
	}

	private static double LeftmostBodyX(byte[] pdf, string word)
	{
		using var document = PdfDocument.Open(pdf);
		return document.GetPage(1).GetWords().First(w => w.Text == word).BoundingBox.Left;
	}

	private static double BodyTopY(byte[] pdf, string word)
	{
		using var document = PdfDocument.Open(pdf);
		var page = document.GetPage(1);
		return page.Height - page.GetWords().First(w => w.Text == word).BoundingBox.Top;
	}

	// ---- Without a page setup nothing changes ------------------------------------------------------------------

	[Fact]
	public async Task Without_a_page_setup_the_standard_letter_page_and_footer_are_used()
	{
		var pdf = await RenderAsync(Setup.Pages(2));
		Assert.Equal([(612d, 792d), (612d, 792d)], Sizes(pdf));
		var pages = Regions(pdf, 0.5, 0.6);
		Assert.Equal("Mutual Of Enumclaw Page 1 of 2", pages[0].Footer);
		Assert.Equal("Mutual Of Enumclaw Page 2 of 2", pages[1].Footer);
		Assert.Equal("", pages[0].Header);
		Assert.Equal(36, LeftmostBodyX(pdf, "Body"), 1.5);
	}

	// ---- Paper size and orientation -------------------------------------------------------------------------------

	[Theory]
	[InlineData("letter", "portrait", 612, 792)]
	[InlineData("letter", "landscape", 792, 612)]
	[InlineData("legal", "portrait", 612, 1008)]
	[InlineData("legal", "landscape", 1008, 612)]
	[InlineData("a4", "portrait", 595, 842)]
	[InlineData("a4", "landscape", 842, 595)]
	public async Task Paper_size_and_orientation_set_the_page_dimensions(string size, string orientation, double width, double height)
	{
		var pdf = await RenderAsync(Setup.Markup(size, orientation, inner: Setup.Slots("footer", "default", right: Setup.PageOfPages)) + Setup.Pages(2));
		var sizes = Sizes(pdf);
		Assert.Equal(2, sizes.Count);
		Assert.All(sizes, s =>
		{
			Assert.Equal(width, s.Width, 1.0);
			Assert.Equal(height, s.Height, 1.0);
		});
	}

	[Fact]
	public async Task Unknown_sizes_and_orientations_fall_back_to_letter_portrait()
	{
		var pdf = await RenderAsync(Setup.Markup("tabloid", "sideways") + Setup.Pages(1));
		Assert.Equal([(612d, 792d)], Sizes(pdf));
	}

	[Fact]
	public async Task Landscape_pages_are_wider_so_more_words_fit_on_a_line()
	{
		var long_ = "<p style=\"font-size:12pt\">" + string.Join(" ", Enumerable.Repeat("word", 400)) + "</p>";
		using var portrait = PdfDocument.Open(await RenderAsync(Setup.Markup() + long_));
		using var landscape = PdfDocument.Open(await RenderAsync(Setup.Markup(orientation: "landscape") + long_));
		static int FirstLine(Page page)
		{
			var words = page.GetWords().Where(w => w.Text == "word").ToList();
			var top = words.Max(w => Math.Round(w.BoundingBox.Bottom));
			return words.Count(w => Math.Round(w.BoundingBox.Bottom) == top);
		}
		Assert.True(FirstLine(landscape.GetPage(1)) > FirstLine(portrait.GetPage(1)) + 3,
			$"portrait {FirstLine(portrait.GetPage(1))}, landscape {FirstLine(landscape.GetPage(1))}");
	}

	// ---- Margins ------------------------------------------------------------------------------------------------

	[Fact]
	public async Task The_left_margin_moves_the_body_text()
	{
		var pdf = await RenderAsync(Setup.Markup(margins: "0.5_0.5_0.6_2") + "<p>Indented body</p>");
		Assert.Equal(144, LeftmostBodyX(pdf, "Indented"), 1.5);
	}

	[Fact]
	public async Task The_top_margin_moves_the_body_text_down()
	{
		var near = BodyTopY(await RenderAsync(Setup.Markup(margins: "0.5_0.5_0.6_0.5") + "<p style=\"margin:0\">Top line</p>"), "Top");
		var far = BodyTopY(await RenderAsync(Setup.Markup(margins: "2_0.5_0.6_0.5") + "<p style=\"margin:0\">Top line</p>"), "Top");
		Assert.Equal(108, far - near, 2.0);
	}

	[Fact]
	public async Task The_bottom_margin_keeps_body_text_above_it()
	{
		var text = string.Concat(Enumerable.Range(1, 80).Select(n => $"<p>Line {n}</p>"));
		static (int Lines, double Lowest) FirstPage(byte[] pdf)
		{
			using var document = PdfDocument.Open(pdf);
			var words = document.GetPage(1).GetWords().Where(w => w.Text == "Line").ToList();
			return (words.Count, words.Min(w => w.BoundingBox.Bottom));
		}
		var normal = FirstPage(await RenderAsync(Setup.Markup(margins: "0.5_0.5_0.5_0.5") + text));
		var tall = FirstPage(await RenderAsync(Setup.Markup(margins: "0.5_0.5_3_0.5") + text));
		Assert.True(tall.Lines < normal.Lines, $"normal {normal.Lines}, tall {tall.Lines}");
		Assert.True(normal.Lowest >= 33 && normal.Lowest < 100, $"normal lowest {normal.Lowest}");
		Assert.True(tall.Lowest >= 213 && tall.Lowest < 280, $"tall lowest {tall.Lowest}");
	}

	[Theory]
	[InlineData("9_9_9_9")]
	[InlineData("x_y_z_w")]
	[InlineData("-1_-1_-1_-1")]
	[InlineData("")]
	public async Task Invalid_margins_fall_back_to_the_standard_margins(string margins)
	{
		var pdf = await RenderAsync(Setup.Markup(margins: margins) + "<p>Body</p>");
		Assert.Equal(36, LeftmostBodyX(pdf, "Body"), 1.5);
	}

	// ---- Header and footer --------------------------------------------------------------------------------------

	[Fact]
	public async Task Header_and_footer_print_on_every_page_with_page_x_of_y()
	{
		var setup = Setup.Markup(inner:
			Setup.Slots("header", "default", "Commercial Package", "", "Declarations") +
			Setup.Slots("footer", "default", "Mutual Of Enumclaw", "", Setup.PageOfPages));
		var pages = Regions(await RenderAsync(setup + Setup.Pages(3)), 0.5, 0.6);
		Assert.Equal(3, pages.Count);
		for (var i = 0; i < 3; i++)
		{
			Assert.Equal("Commercial Package Declarations", pages[i].Header);
			Assert.Equal($"Mutual Of Enumclaw Page {i + 1} of 3", pages[i].Footer);
			Assert.Equal($"Body text on sheet {i + 1}", pages[i].Body);
		}
	}

	[Fact]
	public async Task Left_center_and_right_slots_are_aligned_across_the_page()
	{
		var pdf = await RenderAsync(Setup.Markup(inner: Setup.Slots("footer", "default", "LeftSlot", "CenterSlot", "RightSlot")) + "<p>Body</p>");
		using var document = PdfDocument.Open(pdf);
		var words = document.GetPage(1).GetWords().ToDictionary(w => w.Text, w => w.BoundingBox);
		Assert.Equal(36, words["LeftSlot"].Left, 2.0);
		Assert.Equal(306, (words["CenterSlot"].Left + words["CenterSlot"].Right) / 2, 3.0);
		Assert.Equal(576, words["RightSlot"].Right, 2.0);
	}

	[Fact]
	public async Task Header_text_follows_the_left_and_right_margins()
	{
		var pdf = await RenderAsync(Setup.Markup(margins: "0.5_1_0.6_1.5", inner: Setup.Slots("header", "default", "LeftSlot", "", "RightSlot")) + "<p>Body</p>");
		using var document = PdfDocument.Open(pdf);
		var words = document.GetPage(1).GetWords().ToDictionary(w => w.Text, w => w.BoundingBox);
		Assert.Equal(108, words["LeftSlot"].Left, 2.0);
		Assert.Equal(540, words["RightSlot"].Right, 2.0);
	}

	[Fact]
	public async Task Header_and_footer_font_size_comes_from_the_setup()
	{
		static async Task<double> Height(PageSetupRenderTests t, string font)
		{
			using var document = PdfDocument.Open(await t.RenderAsync(Setup.Markup(font: font, inner: Setup.Slots("footer", "default", "Sized")) + "<p>Body</p>"));
			return document.GetPage(1).GetWords().First(w => w.Text == "Sized").Letters[0].PointSize;
		}
		Assert.Equal(8, await Height(this, "8"), 0.6);
		Assert.Equal(14, await Height(this, "14"), 0.6);
		Assert.Equal(10, await Height(this, "99"), 0.6);
	}

	[Fact]
	public async Task Fields_in_the_header_print_the_documents_data_html_encoded()
	{
		var setup = Setup.Markup(inner: Setup.Slots("header", "default", "Policy {{ policy.number }}", "", "{{ policy.insured }}"));
		var pages = Regions(await RenderAsync(setup + Setup.Pages(2)), 0.5, 0.6);
		Assert.All(pages, p => Assert.Equal("Policy CPP7654321 Acme <b>Bakery</b>", p.Header));
	}

	[Fact]
	public async Task The_date_token_prints_todays_date()
	{
		var setup = Setup.Markup(inner: Setup.Slots("footer", "default", "Printed {{ 'now' | date: '%m/%d/%Y' }}"));
		var before = DateTime.Now.ToString("MM/dd/yyyy");
		var pages = Regions(await RenderAsync(setup + "<p>Body</p>"), 0.5, 0.6);
		var after = DateTime.Now.ToString("MM/dd/yyyy");
		Assert.Contains(pages[0].Footer, new[] { "Printed " + before, "Printed " + after });
	}

	[Fact]
	public async Task A_setup_with_empty_slots_prints_no_header_or_footer()
	{
		var setup = Setup.Markup(inner: Setup.Slots("header", "default") + Setup.Slots("footer", "default"));
		var pages = Regions(await RenderAsync(setup + Setup.Pages(2)), 0.5, 0.6);
		Assert.All(pages, p =>
		{
			Assert.Equal("", p.Header);
			Assert.Equal("", p.Footer);
		});
	}

	[Fact]
	public async Task The_setup_block_itself_is_never_printed_in_the_body()
	{
		var setup = Setup.Markup(inner: Setup.Slots("header", "default", "OnlyInHeader"));
		var pages = Regions(await RenderAsync(setup + "<p>Body</p>"), 0.5, 0.6);
		Assert.Equal("OnlyInHeader", pages[0].Header);
		Assert.Equal("Body", pages[0].Body);
	}

	[Fact]
	public async Task Only_the_first_setup_counts()
	{
		var html = Setup.Markup("legal", inner: Setup.Slots("footer", "default", "FirstSetup")) +
			Setup.Markup("a4", "landscape", inner: Setup.Slots("footer", "default", "SecondSetup")) + "<p>Body</p>";
		var pdf = await RenderAsync(html);
		Assert.Equal([(612d, 1008d)], Sizes(pdf));
		var pages = Regions(pdf, 0.5, 0.6);
		Assert.Equal("FirstSetup", pages[0].Footer);
		Assert.DoesNotContain("SecondSetup", pages[0].Body);
	}

	// ---- First page and odd/even variants -----------------------------------------------------------------------

	[Fact]
	public async Task A_different_first_page_uses_its_own_header_and_footer()
	{
		var setup = Setup.Markup(inner:
			Setup.Slots("header", "default", "Running header") + Setup.Slots("footer", "default", "", "", Setup.PageOfPages) +
			Setup.Slots("header", "first", "Cover header") + Setup.Slots("footer", "first", "Cover footer"));
		var pages = Regions(await RenderAsync(setup + Setup.Pages(3)), 0.5, 0.6);
		Assert.Equal(("Cover header", "Cover footer"), (pages[0].Header, pages[0].Footer));
		Assert.Equal(("Running header", "Page 2 of 3"), (pages[1].Header, pages[1].Footer));
		Assert.Equal(("Running header", "Page 3 of 3"), (pages[2].Header, pages[2].Footer));
		Assert.Equal(["Body text on sheet 1", "Body text on sheet 2", "Body text on sheet 3"], pages.Select(p => p.Body));
	}

	[Fact]
	public async Task A_blank_first_page_variant_leaves_the_cover_clean()
	{
		var setup = Setup.Markup(inner:
			Setup.Slots("footer", "default", "", "", Setup.PageOfPages) + Setup.Slots("header", "first") + Setup.Slots("footer", "first"));
		var pages = Regions(await RenderAsync(setup + Setup.Pages(2)), 0.5, 0.6);
		Assert.Equal("", pages[0].Footer);
		Assert.Equal("Page 2 of 2", pages[1].Footer);
	}

	[Fact]
	public async Task Different_odd_and_even_pages_alternate()
	{
		var setup = Setup.Markup(inner:
			Setup.Slots("footer", "default", "", "", "Odd " + Setup.PageOfPages) +
			Setup.Slots("footer", "even", "Even " + Setup.PageOfPages));
		var pages = Regions(await RenderAsync(setup + Setup.Pages(4)), 0.5, 0.6);
		Assert.Equal(["Odd Page 1 of 4", "Even Page 2 of 4", "Odd Page 3 of 4", "Even Page 4 of 4"], pages.Select(p => p.Footer));
	}

	[Fact]
	public async Task Even_page_footers_can_sit_on_the_other_side()
	{
		var setup = Setup.Markup(inner:
			Setup.Slots("footer", "default", "", "", "OddSide") + Setup.Slots("footer", "even", "EvenSide"));
		using var document = PdfDocument.Open(await RenderAsync(setup + Setup.Pages(2)));
		Assert.Equal(576, document.GetPage(1).GetWords().First(w => w.Text == "OddSide").BoundingBox.Right, 2.0);
		Assert.Equal(36, document.GetPage(2).GetWords().First(w => w.Text == "EvenSide").BoundingBox.Left, 2.0);
	}

	[Fact]
	public async Task First_page_and_odd_even_together()
	{
		var setup = Setup.Markup(inner:
			Setup.Slots("header", "default", "Odd header") + Setup.Slots("footer", "default", Setup.PageOfPages) +
			Setup.Slots("header", "first", "First header") + Setup.Slots("footer", "first", "First footer") +
			Setup.Slots("header", "even", "Even header") + Setup.Slots("footer", "even", "Even " + Setup.PageOfPages));
		var pages = Regions(await RenderAsync(setup + Setup.Pages(5)), 0.5, 0.6);
		Assert.Equal(["First header", "Even header", "Odd header", "Even header", "Odd header"], pages.Select(p => p.Header));
		Assert.Equal(["First footer", "Even Page 2 of 5", "Page 3 of 5", "Even Page 4 of 5", "Page 5 of 5"], pages.Select(p => p.Footer));
		Assert.Equal(Enumerable.Range(1, 5).Select(n => $"Body text on sheet {n}"), pages.Select(p => p.Body));
	}

	[Fact]
	public async Task Variants_keep_the_paper_size_and_margins()
	{
		var setup = Setup.Markup("legal", "landscape", "0.5_0.5_0.6_1", inner:
			Setup.Slots("footer", "default", "Default") + Setup.Slots("footer", "first", "First") + Setup.Slots("footer", "even", "Even"));
		var pdf = await RenderAsync(setup + Setup.Pages(3));
		Assert.Equal([(1008d, 612d), (1008d, 612d), (1008d, 612d)], Sizes(pdf));
		using var document = PdfDocument.Open(pdf);
		foreach (var page in document.GetPages())
		{
			Assert.Equal(72, page.GetWords().First(w => w.Text == "Body").BoundingBox.Left, 1.5);
		}
	}

	[Fact]
	public async Task A_single_page_document_with_an_even_variant_prints_the_default()
	{
		var setup = Setup.Markup(inner: Setup.Slots("footer", "default", "DefaultFoot") + Setup.Slots("footer", "even", "EvenFoot"));
		var pages = Regions(await RenderAsync(setup + "<p>Body</p>"), 0.5, 0.6);
		Assert.Single(pages);
		Assert.Equal("DefaultFoot", pages[0].Footer);
	}

	[Theory]
	[InlineData(1, false, false, "default")]
	[InlineData(2, false, false, "default")]
	[InlineData(1, true, false, "first")]
	[InlineData(2, true, false, "default")]
	[InlineData(1, false, true, "default")]
	[InlineData(2, false, true, "even")]
	[InlineData(3, false, true, "default")]
	[InlineData(1, true, true, "first")]
	[InlineData(2, true, true, "even")]
	[InlineData(3, true, true, "default")]
	[InlineData(10, true, true, "even")]
	public void Which_variant_each_page_uses(int page, bool hasFirst, bool hasEven, string expected)
	{
		Assert.Equal(expected, PdfRenderer.VariantFor(page, hasFirst, hasEven));
	}

	// ---- Safety ---------------------------------------------------------------------------------------------------

	[Fact]
	public async Task Script_in_header_slots_is_removed_before_printing()
	{
		var composer = factory.Services.GetRequiredService<DocumentComposer>();
		var setup = Setup.Markup(inner: Setup.Slots("header", "default", "Safe<script>alert(1)</script><img src=x onerror=alert(2)>"));
		var result = await composer.ComposeAsync(setup, "", JsonDocument.Parse("{}").RootElement);
		Assert.Null(result.Error);
		Assert.DoesNotContain("<script", result.Html!, StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain("onerror", result.Html!, StringComparison.OrdinalIgnoreCase);
		Assert.Contains("doc-setup ds-size-letter ds-orient-portrait ds-margins-0.5_0.5_0.6_0.5 ds-font-10", result.Html!);
		var pages = Regions(await RenderAsync(setup + "<p>Body</p>"), 0.5, 0.6);
		Assert.Equal("Safe", pages[0].Header);
	}

	[Fact]
	public async Task Header_images_must_be_inline_data()
	{
		var setup = Setup.Markup(inner: Setup.Slots("header", "default", "<img class=\"doc-hf-logo\" src=\"{{ brand.logos.horizontal_4color }}\" alt=\"logo\"> Logo"));
		var composer = factory.Services.GetRequiredService<DocumentComposer>();
		var result = await composer.ComposeAsync(setup, "", JsonDocument.Parse("{}").RootElement);
		Assert.Contains("class=\"doc-hf-logo\" src=\"data:image/svg+xml", result.Html!);
		var pages = Regions(await RenderAsync(setup + "<p>Body</p>"), 0.75, 0.6);
		Assert.Equal("Logo", pages[0].Header);
	}

	[Fact]
	public async Task The_brand_stylesheet_hides_the_setup_in_html_previews()
	{
		var composer = factory.Services.GetRequiredService<DocumentComposer>();
		var result = await composer.ComposeAsync(Setup.Markup() + "<p>x</p>", "", JsonDocument.Parse("{}").RootElement);
		Assert.Contains(".doc-setup { display: none !important; }", result.Html!);
	}

	[Fact]
	public async Task Published_templates_render_with_their_page_setup()
	{
		var name = "ps-" + Guid.NewGuid().ToString("N")[..8];
		var html = Setup.Markup("legal", inner: Setup.Slots("footer", "default", "Published footer")) + "<p>Published body</p>";
		var saved = await _client.PutAsJsonAsync($"/api/templates/{name}/draft", new { project = new { }, html, css = "" });
		Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
		(await _client.PostAsync($"/api/templates/{name}/versions/1/publish", null)).EnsureSuccessStatusCode();
		var response = await _client.PostAsync($"/api/templates/{name}/pdf", null);
		Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
		var pdf = await response.Content.ReadAsByteArrayAsync();
		Assert.Equal([(612d, 1008d)], Sizes(pdf));
		Assert.Equal("Published footer", Regions(pdf, 0.5, 0.6)[0].Footer);
	}
}
