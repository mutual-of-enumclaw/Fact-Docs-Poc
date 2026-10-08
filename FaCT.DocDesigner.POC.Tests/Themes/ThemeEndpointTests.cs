using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using FaCT.DocDesigner.POC.Rendering;
using FaCT.DocDesigner.POC.Tests.Clauses;
using FaCT.DocDesigner.POC.Tests.Security;
using Microsoft.Extensions.DependencyInjection;
using UglyToad.PdfPig;

namespace FaCT.DocDesigner.POC.Tests.Themes;

/// <summary>/api/themes: themes, fonts, usage, and what a theme does to the PDF.</summary>
public sealed class ThemeEndpointTests(ClauseAppFactory factory) : IClassFixture<ClauseAppFactory>
{
	private readonly HttpClient _client = factory.CreateClient();

	private static string Unique(string name) => name + "-" + Guid.NewGuid().ToString("N")[..8];

	internal static string Marker(string theme) => $"<div class=\"doc-theme theme-{theme}\"></div>";

	private async Task<JsonElement> SaveThemeAsync(string name, object body)
	{
		var response = await _client.PutAsJsonAsync($"/api/themes/{name}", body);
		Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
		return await response.Content.ReadFromJsonAsync<JsonElement>();
	}

	private async Task<JsonElement> UploadFontAsync(string family, int weight = 400, string style = "normal", byte[]? data = null)
	{
		var response = await PostFontAsync(family, weight, style, data ?? ThemeStoreTests.Consolas);
		Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
		return await response.Content.ReadFromJsonAsync<JsonElement>();
	}

	private Task<HttpResponseMessage> PostFontAsync(string family, int weight, string style, byte[] data, string contentType = "application/octet-stream")
	{
		var content = new ByteArrayContent(data);
		content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
		return _client.PostAsync($"/api/themes/fonts?family={Uri.EscapeDataString(family)}&weight={weight}&style={style}", content);
	}

	private async Task SaveTemplateAsync(string name, string html, bool publish = false)
	{
		var saved = await _client.PutAsJsonAsync($"/api/templates/{name}/draft", new { project = new { }, html, css = "" });
		Assert.True(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync());
		if (publish)
		{
			var version = (await saved.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("version").GetInt32();
			(await _client.PostAsync($"/api/templates/{name}/versions/{version}/publish", null)).EnsureSuccessStatusCode();
		}
	}

	private async Task<HttpResponseMessage> RenderAsync(string html) =>
		await _client.PostAsJsonAsync("/api/render", new JsonObject { ["html"] = html, ["css"] = "", ["data"] = new JsonObject() });

	private async Task<byte[]> PdfAsync(string html)
	{
		var response = await RenderAsync(html);
		Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
		return await response.Content.ReadAsByteArrayAsync();
	}

	/// <summary>Fill color (0-255 RGB) and font of the first letter of a word on page 1.</summary>
	private static ((int R, int G, int B) Color, string Font) LookOf(byte[] pdf, string word)
	{
		using var document = PdfDocument.Open(pdf);
		var page = document.GetPage(1);
		var letters = page.Letters.ToList();
		var text = string.Concat(letters.Select(l => l.Value));
		var at = text.IndexOf(word, StringComparison.Ordinal);
		Assert.True(at >= 0, $"'{word}' not in: {text}");
		var letter = letters[at];
		var (r, g, b) = letter.Color.ToRGBValues();
		return (((int)Math.Round(r * 255), (int)Math.Round(g * 255), (int)Math.Round(b * 255)), letter.FontName ?? "");
	}

	private static async Task<string> ErrorAsync(HttpResponseMessage response) =>
		(await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()!;

	// ---- Listing and saving ------------------------------------------------------------------------------------------

	[Fact]
	public async Task The_list_has_the_brand_tokens_and_fonts_to_choose_from()
	{
		var info = await _client.GetFromJsonAsync<JsonElement>("/api/themes");
		Assert.Equal("moe", info.GetProperty("standard").GetString());
		var tokens = info.GetProperty("tokens").EnumerateArray().ToDictionary(t => t.GetProperty("name").GetString()!, t => t.GetProperty("default").GetString());
		Assert.Equal("#144835", tokens["moe-green"]);
		Assert.Equal("#448843", tokens["moe-alpine-green"]);
		Assert.Equal("#00a8bf", tokens["moe-aqua"]);
		Assert.Equal("#2d2927", tokens["moe-black"]);
		Assert.True(tokens.Count >= 15, string.Join(", ", tokens.Keys));
		Assert.DoesNotContain("moe-font-sans", tokens.Keys);
		Assert.Contains("Figtree", info.GetProperty("systemFonts").EnumerateArray().Select(f => f.GetString()));
	}

	[Fact]
	public async Task Save_read_and_delete_a_theme()
	{
		var name = Unique("farm");
		var saved = await SaveThemeAsync(name, new { label = "MOE Farm", description = "Farm and ranch", colors = new Dictionary<string, string> { ["moe-green"] = "#2E5E1E" }, bodyFont = "Georgia" });
		Assert.Equal("MOE Farm", saved.GetProperty("label").GetString());
		Assert.Equal("#2e5e1e", saved.GetProperty("colors").GetProperty("moe-green").GetString());
		Assert.Equal("Local designer", saved.GetProperty("savedBy").GetString());

		var read = await _client.GetFromJsonAsync<JsonElement>($"/api/themes/{name}");
		Assert.Equal("Georgia", read.GetProperty("bodyFont").GetString());
		var listed = await _client.GetFromJsonAsync<JsonElement>("/api/themes");
		Assert.Contains(listed.GetProperty("themes").EnumerateArray(), t => t.GetProperty("name").GetString() == name);

		Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"/api/themes/{name}")).StatusCode);
		Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/themes/{name}")).StatusCode);
		Assert.Equal(HttpStatusCode.NotFound, (await _client.DeleteAsync($"/api/themes/{name}")).StatusCode);
	}

	[Fact]
	public async Task Invalid_themes_are_refused_with_a_reason()
	{
		var bad = await _client.PutAsJsonAsync($"/api/themes/{Unique("x")}", new { label = "X", colors = new Dictionary<string, string> { ["moe-green"] = "green" } });
		Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
		Assert.Equal("The color for moe-green must look like #1A2B3C.", await ErrorAsync(bad));

		var reserved = await _client.PutAsJsonAsync("/api/themes/moe", new { label = "Mine" });
		Assert.Equal(HttpStatusCode.BadRequest, reserved.StatusCode);
		Assert.Equal(HttpStatusCode.BadRequest, (await _client.DeleteAsync("/api/themes/moe")).StatusCode);
	}

	[Fact]
	public async Task Saves_and_deletes_are_in_the_audit_log()
	{
		var name = Unique("audited");
		await SaveThemeAsync(name, new { label = "Audited" });
		await _client.DeleteAsync($"/api/themes/{name}");
		var saved = await _client.GetFromJsonAsync<JsonElement>("/api/audit?action=theme.saved");
		Assert.Contains(saved.EnumerateArray(), e => e.GetProperty("detail").GetString() == name);
		var deleted = await _client.GetFromJsonAsync<JsonElement>("/api/audit?action=theme.deleted");
		Assert.Contains(deleted.EnumerateArray(), e => e.GetProperty("detail").GetString() == name);
	}

	// ---- The stylesheet ----------------------------------------------------------------------------------------------

	[Fact]
	public async Task The_theme_stylesheet_is_served_for_the_canvas()
	{
		var name = Unique("css");
		await SaveThemeAsync(name, new { label = "Css", colors = new Dictionary<string, string> { ["moe-green"] = "#c00000" } });
		var response = await _client.GetAsync($"/api/themes/{name}/theme.css");
		Assert.Equal("text/css", response.Content.Headers.ContentType!.MediaType);
		Assert.Equal(":root{--moe-green:#c00000;}\n", await response.Content.ReadAsStringAsync());
		Assert.Equal("", await _client.GetStringAsync("/api/themes/moe/theme.css"));
		Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/themes/{Unique("none")}/theme.css")).StatusCode);
	}

	// ---- Fonts -------------------------------------------------------------------------------------------------------

	[Fact]
	public async Task A_font_can_be_uploaded_and_deleted()
	{
		var family = "Mono " + Guid.NewGuid().ToString("N")[..6];
		var font = await UploadFontAsync(family, 700, "italic");
		Assert.Equal(family, font.GetProperty("family").GetString());
		Assert.Equal(700, font.GetProperty("weight").GetInt32());
		Assert.Equal("truetype", font.GetProperty("format").GetString());
		var listed = await _client.GetFromJsonAsync<JsonElement>("/api/themes");
		Assert.Contains(listed.GetProperty("fonts").EnumerateArray(), f => f.GetProperty("family").GetString() == family);

		var id = font.GetProperty("id").GetString();
		Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"/api/themes/fonts/{id}")).StatusCode);
		Assert.Equal(HttpStatusCode.NotFound, (await _client.DeleteAsync($"/api/themes/fonts/{id}")).StatusCode);
	}

	[Fact]
	public async Task Font_uploads_must_be_font_files_sent_as_files()
	{
		var html = await PostFontAsync("Evil", 400, "normal", "<html><body>hi</body></html>"u8.ToArray(), "text/html");
		Assert.Equal(HttpStatusCode.BadRequest, html.StatusCode);
		Assert.Equal("Send the font file itself (application/octet-stream).", await ErrorAsync(html));

		var form = await PostFontAsync("Evil", 400, "normal", ThemeStoreTests.Consolas, "application/x-www-form-urlencoded");
		Assert.Equal(HttpStatusCode.BadRequest, form.StatusCode);

		var notFont = await PostFontAsync("Evil", 400, "normal", "<svg onload=alert(1)>  </svg>"u8.ToArray());
		Assert.Equal(HttpStatusCode.BadRequest, notFont.StatusCode);
		Assert.Equal("That isn't a font file (WOFF2, WOFF, TTF or OTF).", await ErrorAsync(notFont));

		var asFontType = await PostFontAsync("Typed " + Guid.NewGuid().ToString("N")[..6], 400, "normal", ThemeStoreTests.Consolas, "font/ttf");
		Assert.Equal(HttpStatusCode.OK, asFontType.StatusCode);
	}

	[Fact]
	public async Task Too_large_font_uploads_are_refused()
	{
		var big = new byte[FaCT.DocDesigner.POC.Themes.ThemeStore.MaxFontBytes + 1];
		ThemeStoreTests.Consolas.CopyTo(big, 0);
		var response = await PostFontAsync("Big", 400, "normal", big);
		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		Assert.Equal("The font file must be at most 5 MB.", await ErrorAsync(response));
	}

	[Fact]
	public async Task A_font_a_theme_uses_cannot_be_deleted_while_it_is_the_last_of_its_family()
	{
		var family = "Used " + Guid.NewGuid().ToString("N")[..6];
		var regular = (await UploadFontAsync(family, 400)).GetProperty("id").GetString();
		var bold = (await UploadFontAsync(family, 700)).GetProperty("id").GetString();
		var theme = Unique("uses-font");
		await SaveThemeAsync(theme, new { label = "Uses font", bodyFont = family });

		// Another file of the family is left: fine.
		Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"/api/themes/fonts/{bold}")).StatusCode);
		var last = await _client.DeleteAsync($"/api/themes/fonts/{regular}");
		Assert.Equal(HttpStatusCode.Conflict, last.StatusCode);
		var body = await last.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal($"Themes still use '{family}': choose another font for them first.", body.GetProperty("error").GetString());
		Assert.Equal([theme], body.GetProperty("usedBy").EnumerateArray().Select(t => t.GetString()));

		await SaveThemeAsync(theme, new { label = "Uses font" });
		Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"/api/themes/fonts/{regular}")).StatusCode);
	}

	// ---- Usage -------------------------------------------------------------------------------------------------------

	[Fact]
	public async Task A_theme_templates_use_cannot_be_deleted()
	{
		var theme = Unique("in-use");
		await SaveThemeAsync(theme, new { label = "In use" });
		var published = Unique("tpl");
		var draft = Unique("tpl");
		await SaveTemplateAsync(published, Marker(theme) + "<p>x</p>", publish: true);
		await SaveTemplateAsync(draft, Marker(theme) + "<p>y</p>");

		var usage = await _client.GetFromJsonAsync<JsonElement>($"/api/themes/{theme}/usage");
		Assert.Equal(new[] { draft, published }.Order(), usage.EnumerateArray().Select(u => u.GetProperty("template").GetString()).Order());

		var refused = await _client.DeleteAsync($"/api/themes/{theme}");
		Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
		Assert.Equal(2, (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("usedBy").GetArrayLength());

		// A newer draft without the theme still leaves the published version using it.
		await SaveTemplateAsync(published, "<p>no theme</p>");
		await SaveTemplateAsync(draft, "<p>no theme</p>");
		var after = await _client.GetFromJsonAsync<JsonElement>($"/api/themes/{theme}/usage");
		var entry = Assert.Single(after.EnumerateArray());
		Assert.Equal(published, entry.GetProperty("template").GetString());
		Assert.Equal("Published", entry.GetProperty("versions")[0].GetProperty("status").GetString());
	}

	// ---- Rendering ---------------------------------------------------------------------------------------------------

	[Fact]
	public async Task Without_a_theme_documents_keep_the_standard_colors()
	{
		var (color, _) = LookOf(await PdfAsync("<h1>Heading</h1><p>Body text</p>"), "Heading");
		Assert.Equal((0x14, 0x48, 0x35), color);
	}

	[Fact]
	public async Task A_theme_changes_the_brand_colors_in_the_pdf()
	{
		var theme = Unique("red");
		await SaveThemeAsync(theme, new { label = "Red", colors = new Dictionary<string, string> { ["moe-green"] = "#c00000" } });
		var pdf = await PdfAsync(Marker(theme) + "<h1>Heading</h1><table class=\"moe-table\"><thead><tr><th>Column</th></tr></thead></table>");
		Assert.Equal((0xc0, 0, 0), LookOf(pdf, "Heading").Color);
	}

	[Fact]
	public async Task The_standard_theme_marker_is_the_standard_look()
	{
		var (color, _) = LookOf(await PdfAsync(Marker("moe") + "<h1>Heading</h1>"), "Heading");
		Assert.Equal((0x14, 0x48, 0x35), color);
	}

	[Fact]
	public async Task A_theme_sets_the_text_and_heading_fonts()
	{
		var family = "Brand Mono " + Guid.NewGuid().ToString("N")[..6];
		await UploadFontAsync(family, 400);
		await UploadFontAsync(family, 700);
		var theme = Unique("fonts");
		await SaveThemeAsync(theme, new { label = "Fonts", headingFont = family, bodyFont = "Georgia" });
		var pdf = await PdfAsync(Marker(theme) + "<h1>Heading</h1><p>Paragraph</p>");
		Assert.Contains("Consolas", LookOf(pdf, "Heading").Font);
		Assert.Contains("Georgia", LookOf(pdf, "Paragraph").Font);
	}

	[Fact]
	public async Task The_heading_font_follows_the_text_font_unless_set()
	{
		var family = "Text Mono " + Guid.NewGuid().ToString("N")[..6];
		// Headings are bold: without a bold file Chrome would draw a synthetic bold (an unnamed Type3 font).
		await UploadFontAsync(family);
		await UploadFontAsync(family, 700);
		var theme = Unique("body-only");
		await SaveThemeAsync(theme, new { label = "Body only", bodyFont = family });
		var pdf = await PdfAsync(Marker(theme) + "<h1>Heading</h1><p>Paragraph</p>");
		Assert.Contains("Consolas", LookOf(pdf, "Heading").Font);
		Assert.Contains("Consolas", LookOf(pdf, "Paragraph").Font);
	}

	[Fact]
	public async Task A_missing_theme_is_a_clear_render_error()
	{
		var theme = Unique("gone");
		var response = await RenderAsync(Marker(theme) + "<p>x</p>");
		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		Assert.Equal($"The template uses the theme '{theme}', which doesn't exist.", await ErrorAsync(response));
	}

	[Fact]
	public async Task Changing_a_theme_changes_published_documents_that_use_it()
	{
		var theme = Unique("live");
		var template = Unique("tpl");
		await SaveThemeAsync(theme, new { label = "Live", colors = new Dictionary<string, string> { ["moe-green"] = "#0000c0" } });
		await SaveTemplateAsync(template, Marker(theme) + "<h1>Heading</h1>", publish: true);

		async Task<(int, int, int)> ColorAsync()
		{
			var response = await _client.PostAsync($"/api/templates/{template}/pdf", null);
			Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
			return LookOf(await response.Content.ReadAsByteArrayAsync(), "Heading").Color;
		}
		Assert.Equal((0, 0, 0xc0), await ColorAsync());
		await SaveThemeAsync(theme, new { label = "Live", colors = new Dictionary<string, string> { ["moe-green"] = "#00c000" } });
		Assert.Equal((0, 0xc0, 0), await ColorAsync());
	}

	[Fact]
	public async Task The_theme_comes_after_the_brand_stylesheet_and_before_the_templates_css()
	{
		var theme = Unique("order");
		await SaveThemeAsync(theme, new { label = "Order", colors = new Dictionary<string, string> { ["moe-green"] = "#123456" } });
		var composer = factory.Services.GetRequiredService<DocumentComposer>();
		var html = (await composer.ComposeAsync(Marker(theme) + "<p>x</p>", ".mine{color:red}", JsonDocument.Parse("{}").RootElement)).Html!;
		var brand = html.IndexOf("--moe-green: #144835", StringComparison.Ordinal);
		var themed = html.IndexOf("--moe-green:#123456", StringComparison.Ordinal);
		var mine = html.IndexOf(".mine{color:red}", StringComparison.Ordinal);
		Assert.True(brand >= 0 && brand < themed && themed < mine, $"{brand} {themed} {mine}");
		Assert.Contains("class=\"doc-theme theme-" + theme + "\"", html);
	}
}

/// <summary>Who may change themes: they change published documents, so Publishers only.</summary>
public sealed class ThemeSecurityTests(SecureAppFactory factory) : IClassFixture<SecureAppFactory>
{
	private async Task<HttpClient> SignInAsync(string id)
	{
		var client = factory.CreateClient();
		(await client.PostAsJsonAsync("/api/signin", new { id })).EnsureSuccessStatusCode();
		return client;
	}

	private static ByteArrayContent Font()
	{
		var content = new ByteArrayContent(ThemeStoreTests.Consolas);
		content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
		return content;
	}

	[Fact]
	public async Task Signed_out_users_see_nothing()
	{
		var client = factory.CreateClient();
		Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/themes")).StatusCode);
		Assert.Equal(HttpStatusCode.Unauthorized, (await client.PutAsJsonAsync("/api/themes/x", new { label = "X" })).StatusCode);
	}

	[Fact]
	public async Task Authors_can_read_themes_but_not_change_them()
	{
		var ann = await SignInAsync("ann");
		Assert.Equal(HttpStatusCode.OK, (await ann.GetAsync("/api/themes")).StatusCode);
		Assert.Equal(HttpStatusCode.Forbidden, (await ann.PutAsJsonAsync("/api/themes/ann-theme", new { label = "X" })).StatusCode);
		Assert.Equal(HttpStatusCode.Forbidden, (await ann.DeleteAsync("/api/themes/ann-theme")).StatusCode);
		Assert.Equal(HttpStatusCode.Forbidden, (await ann.PostAsync("/api/themes/fonts?family=Ann", Font())).StatusCode);
		Assert.Equal(HttpStatusCode.Forbidden, (await ann.DeleteAsync("/api/themes/fonts/abc")).StatusCode);
	}

	[Fact]
	public async Task Publishers_change_themes_and_are_named_as_the_saver()
	{
		var pat = await SignInAsync("pat");
		var name = "pat-" + Guid.NewGuid().ToString("N")[..8];
		var saved = await pat.PutAsJsonAsync($"/api/themes/{name}", new { label = "Pat's" });
		Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
		Assert.Equal("Pat Publisher", (await saved.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("savedBy").GetString());
		var font = await pat.PostAsync("/api/themes/fonts?family=Pat%20Mono", Font());
		Assert.Equal(HttpStatusCode.OK, font.StatusCode);
		Assert.Equal("Pat Publisher", (await font.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("uploadedBy").GetString());
		Assert.Equal(HttpStatusCode.NoContent, (await pat.DeleteAsync($"/api/themes/{name}")).StatusCode);
	}
}
