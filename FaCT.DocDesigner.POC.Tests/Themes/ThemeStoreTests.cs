using System.Text;
using FaCT.DocDesigner.POC.Themes;

namespace FaCT.DocDesigner.POC.Tests.Themes;

/// <summary>A theme store in a temp folder over a small brand stylesheet.</summary>
public sealed class TempThemes : IDisposable
{
	public const string BrandCss = """
		:root {
			--moe-green: #144835;
			--moe-alpine-green: #448843;
			--moe-white: #FFFFFF;
			--moe-font-sans: "Figtree", sans-serif;
		}
		h1 { color: var(--moe-green); }
		""";

	public TempThemes()
	{
		Root = Path.Combine(Path.GetTempPath(), "theme-tests-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(Root);
		var css = Path.Combine(Root, "brand.css");
		File.WriteAllText(css, BrandCss);
		Store = new ThemeStore(Path.Combine(Root, "themes"), css);
	}

	public string Root { get; }
	public ThemeStore Store { get; }

	public void Dispose()
	{
		try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
	}
}

/// <summary>Themes and fonts: validation, storage and the stylesheet a theme adds.</summary>
public sealed class ThemeStoreTests : IDisposable
{
	private readonly TempThemes _temp = new();
	private ThemeStore Store => _temp.Store;

	public void Dispose() => _temp.Dispose();

	internal static byte[] Consolas => Import.SystemFonts.Read("consola.ttf");

	private static ThemeInput Input(string label = "Farm", Dictionary<string, string>? colors = null, string? body = null, string? heading = null, string? description = null) =>
		new(label, description, colors, body, heading);

	// ---- Tokens ------------------------------------------------------------------------------------------------------

	[Fact]
	public void Color_tokens_come_from_the_brand_stylesheet_root()
	{
		Assert.Equal(
			[new ThemeToken("moe-green", "#144835"), new ThemeToken("moe-alpine-green", "#448843"), new ThemeToken("moe-white", "#ffffff")],
			Store.ColorTokens);
	}

	// ---- Saving ------------------------------------------------------------------------------------------------------

	[Fact]
	public async Task A_saved_theme_can_be_read_and_listed()
	{
		var (theme, error) = await Store.SaveAsync("farm", Input("  MOE Farm  ", new() { ["moe-green"] = "#2E5E1E" }, "Georgia", "Verdana", " Farm and ranch "), "ann");
		Assert.Null(error);
		Assert.Equal("MOE Farm", theme!.Label);
		Assert.Equal("Farm and ranch", theme.Description);
		Assert.Equal("#2e5e1e", theme.Colors["moe-green"]);
		Assert.Equal(("Georgia", "Verdana", "ann"), (theme.BodyFont, theme.HeadingFont, theme.SavedBy));

		var read = await Store.GetAsync("farm");
		Assert.Equal(theme.Label, read!.Label);
		Assert.Equal(theme.Colors, read.Colors);
		Assert.Equal("farm", Assert.Single(await Store.ListAsync()).Name);
	}

	[Fact]
	public async Task Saving_again_replaces_the_theme()
	{
		await Store.SaveAsync("farm", Input("Farm", new() { ["moe-green"] = "#000000" }), null);
		await Store.SaveAsync("farm", Input("Farm 2"), null);
		var theme = await Store.GetAsync("farm");
		Assert.Equal("Farm 2", theme!.Label);
		Assert.Empty(theme.Colors);
	}

	[Fact]
	public async Task Themes_are_listed_by_label()
	{
		await Store.SaveAsync("b", Input("Zeta"), null);
		await Store.SaveAsync("a", Input("alpha"), null);
		await Store.SaveAsync("c", Input("Mid"), null);
		Assert.Equal(["alpha", "Mid", "Zeta"], (await Store.ListAsync()).Select(t => t.Label));
	}

	[Theory]
	[InlineData("moe")]
	[InlineData("MOE")]
	[InlineData("fonts")]
	[InlineData("bad name")]
	[InlineData("../x")]
	[InlineData("")]
	public async Task Reserved_and_invalid_names_are_refused(string name)
	{
		var (theme, error) = await Store.SaveAsync(name, Input(), null);
		Assert.Null(theme);
		Assert.StartsWith("Theme names may contain", error);
	}

	[Theory]
	[InlineData("", "Give the theme a name of up to 60 characters.")]
	[InlineData("   ", "Give the theme a name of up to 60 characters.")]
	public async Task A_label_is_required(string label, string expected)
	{
		Assert.Equal(expected, (await Store.SaveAsync("x", Input(label), null)).Error);
		Assert.Equal(expected, (await Store.SaveAsync("x", Input(new string('a', 61)), null)).Error);
	}

	[Fact]
	public async Task Descriptions_are_at_most_200_characters()
	{
		Assert.Equal("The description is at most 200 characters.", (await Store.SaveAsync("x", Input(description: new string('d', 201)), null)).Error);
		Assert.Null((await Store.SaveAsync("x", Input(description: new string('d', 200)), null)).Error);
	}

	[Fact]
	public async Task Only_brand_color_tokens_can_be_changed()
	{
		Assert.Equal("'moe-font-sans' is not a brand color.", (await Store.SaveAsync("x", Input(colors: new() { ["moe-font-sans"] = "#000000" }), null)).Error);
		Assert.Equal("'background' is not a brand color.", (await Store.SaveAsync("x", Input(colors: new() { ["background"] = "#000000" }), null)).Error);
	}

	[Theory]
	[InlineData("red", false)]
	[InlineData("#12345", false)]
	[InlineData("#1234567", false)]
	[InlineData("#12345g", false)]
	[InlineData("#000;} body{display:none", false)]
	[InlineData("", false)]
	[InlineData("#abc", true)]
	[InlineData("#A1B2C3", true)]
	public async Task Colors_must_be_hex(string value, bool valid)
	{
		var (_, error) = await Store.SaveAsync("x", Input(colors: new() { ["moe-green"] = value }), null);
		if (valid) Assert.Null(error);
		else Assert.Equal("The color for moe-green must look like #1A2B3C.", error);
	}

	[Fact]
	public async Task Fonts_must_be_available()
	{
		Assert.Equal("The font 'Comic Sans' isn't available. Upload it first.", (await Store.SaveAsync("x", Input(body: "Comic Sans"), null)).Error);
		Assert.Equal("The font 'Nope' isn't available. Upload it first.", (await Store.SaveAsync("x", Input(heading: "Nope"), null)).Error);
		foreach (var family in ThemeStore.SystemFonts)
		{
			Assert.Null((await Store.SaveAsync("x", Input(body: family, heading: family), null)).Error);
		}
		await Store.AddFontAsync(Consolas, "Brand Mono", 400, "normal", null);
		Assert.Null((await Store.SaveAsync("x", Input(body: "Brand Mono"), null)).Error);
	}

	[Fact]
	public async Task Blank_fonts_mean_the_standard_ones()
	{
		var (theme, _) = await Store.SaveAsync("x", Input(body: "  ", heading: ""), null);
		Assert.Null(theme!.BodyFont);
		Assert.Null(theme.HeadingFont);
	}

	[Fact]
	public async Task There_is_a_limit_on_the_number_of_themes()
	{
		for (var i = 0; i < ThemeStore.MaxThemes; i++)
		{
			Assert.Null((await Store.SaveAsync("t" + i, Input(), null)).Error);
		}
		Assert.Equal($"There can be at most {ThemeStore.MaxThemes} themes.", (await Store.SaveAsync("one-more", Input(), null)).Error);
		Assert.Null((await Store.SaveAsync("t0", Input("Replaced"), null)).Error);
	}

	[Fact]
	public async Task Deleting_a_theme()
	{
		await Store.SaveAsync("farm", Input(), null);
		Assert.True(await Store.DeleteAsync("farm"));
		Assert.False(await Store.DeleteAsync("farm"));
		Assert.Null(await Store.GetAsync("farm"));
		Assert.False(await Store.DeleteAsync("../brand"));
		Assert.True(File.Exists(Path.Combine(_temp.Root, "brand.css")));
	}

	[Fact]
	public async Task Unknown_and_invalid_names_are_not_found()
	{
		Assert.Null(await Store.GetAsync("missing"));
		Assert.Null(await Store.GetAsync("moe"));
		Assert.Null(await Store.GetAsync("..\\brand"));
	}

	// ---- The stylesheet ------------------------------------------------------------------------------------------------

	[Fact]
	public async Task The_standard_theme_adds_nothing_and_a_missing_one_is_null()
	{
		Assert.Equal("", await Store.CssAsync("moe"));
		Assert.Equal("", await Store.CssAsync("MOE"));
		Assert.Null(await Store.CssAsync("missing"));
		Assert.Null(await Store.CssAsync("bad name"));
	}

	[Fact]
	public async Task A_theme_overrides_the_tokens_it_changes()
	{
		await Store.SaveAsync("farm", Input(colors: new() { ["moe-green"] = "#C00000", ["moe-white"] = "#fafafa" }), null);
		Assert.Equal(":root{--moe-green:#c00000;--moe-white:#fafafa;}\n", await Store.CssAsync("farm"));
	}

	[Fact]
	public async Task System_fonts_set_the_font_tokens_without_font_files()
	{
		await Store.SaveAsync("farm", Input(body: "Georgia", heading: "Verdana"), null);
		var css = await Store.CssAsync("farm");
		Assert.Equal(":root{--moe-font-sans:\"Georgia\", \"Segoe UI\", Arial, sans-serif;--moe-font-heading:\"Verdana\", \"Segoe UI\", Arial, sans-serif;}\n", css);
		Assert.DoesNotContain("@font-face", css);
	}

	[Fact]
	public async Task Uploaded_fonts_are_embedded_only_when_the_theme_uses_them()
	{
		await Store.AddFontAsync(Consolas, "Brand Mono", 400, "normal", null);
		await Store.AddFontAsync(Consolas, "Brand Mono", 700, "italic", null);
		await Store.AddFontAsync(Consolas, "Unused", 400, "normal", null);
		await Store.SaveAsync("farm", Input(heading: "Brand Mono"), null);
		var css = (await Store.CssAsync("farm"))!;
		Assert.Equal(2, css.Split("@font-face").Length - 1);
		Assert.Contains("@font-face{font-family:\"Brand Mono\";src:url(data:font/ttf;base64," + Convert.ToBase64String(Consolas)[..40], css);
		Assert.Contains("format(\"truetype\");font-weight:400;font-style:normal;}", css);
		Assert.Contains("format(\"truetype\");font-weight:700;font-style:italic;}", css);
		Assert.DoesNotContain("Unused", css);
		Assert.EndsWith(":root{--moe-font-heading:\"Brand Mono\", \"Segoe UI\", Arial, sans-serif;}\n", css);
	}

	// ---- Fonts -------------------------------------------------------------------------------------------------------

	[Theory]
	[InlineData("wOF2", "woff2")]
	[InlineData("wOFF", "woff")]
	[InlineData("OTTO", "opentype")]
	[InlineData("true", "truetype")]
	public void Font_formats_are_known_by_their_first_bytes(string tag, string format)
	{
		Assert.Equal(format, ThemeStore.FormatOf([.. Encoding.ASCII.GetBytes(tag), .. new byte[20]]));
	}

	[Fact]
	public void TrueType_files_without_a_tag_and_non_fonts()
	{
		Assert.Equal("truetype", ThemeStore.FormatOf(Consolas));
		Assert.Equal("truetype", ThemeStore.FormatOf([0, 1, 0, 0, .. new byte[20]]));
		Assert.Null(ThemeStore.FormatOf(Encoding.ASCII.GetBytes("<html><script>alert(1)</script></html>")));
		Assert.Null(ThemeStore.FormatOf(Encoding.ASCII.GetBytes("%PDF-1.7 not a font")));
		Assert.Null(ThemeStore.FormatOf(Encoding.ASCII.GetBytes("wOF2")));
		Assert.Null(ThemeStore.FormatOf([]));
	}

	[Fact]
	public async Task An_uploaded_font_is_stored_and_listed()
	{
		var (font, error) = await Store.AddFontAsync(Consolas, " Brand Mono ", 700, "italic", "pat");
		Assert.Null(error);
		Assert.Equal(("Brand Mono", 700, "italic", "truetype", (long)Consolas.Length, "pat"), (font!.Family, font.Weight, font.Style, font.Format, font.Bytes, font.UploadedBy));
		Assert.Equal(Consolas, File.ReadAllBytes(Store.FontPath(font)));
		Assert.EndsWith(".ttf", Store.FontPath(font));
		Assert.Equal(font.Id, Assert.Single(await Store.ListFontsAsync()).Id);
		Assert.Equal(font.Id, (await Store.GetFontAsync(font.Id))!.Id);
	}

	[Fact]
	public async Task Uploading_the_same_family_weight_and_style_replaces_the_file()
	{
		var (first, _) = await Store.AddFontAsync(Consolas, "Brand Mono", 400, null, null);
		var (second, _) = await Store.AddFontAsync(Consolas, "Brand Mono", 400, "normal", null);
		Assert.NotEqual(first!.Id, second!.Id);
		Assert.Equal(second.Id, Assert.Single(await Store.ListFontsAsync()).Id);
		Assert.False(File.Exists(Store.FontPath(first)));
		await Store.AddFontAsync(Consolas, "Brand Mono", 700, "normal", null);
		Assert.Equal(2, (await Store.ListFontsAsync()).Count);
	}

	[Theory]
	[InlineData("", 400, "normal", "Font family names may contain letters, numbers, spaces and \"-\" (at most 60).")]
	[InlineData("Bad\"; } body {", 400, "normal", "Font family names may contain letters, numbers, spaces and \"-\" (at most 60).")]
	[InlineData("-Dash", 400, "normal", "Font family names may contain letters, numbers, spaces and \"-\" (at most 60).")]
	[InlineData("Georgia", 400, "normal", "'Georgia' is already available.")]
	[InlineData("figtree", 400, "normal", "'figtree' is already available.")]
	[InlineData("Brand", 450, "normal", "The weight must be 100, 200, ... 900.")]
	[InlineData("Brand", 1000, "normal", "The weight must be 100, 200, ... 900.")]
	[InlineData("Brand", 0, "normal", "The weight must be 100, 200, ... 900.")]
	[InlineData("Brand", 400, "oblique", "The style must be normal or italic.")]
	public async Task Font_details_are_checked(string family, int weight, string style, string expected)
	{
		var (font, error) = await Store.AddFontAsync(Consolas, family, weight, style, null);
		Assert.Null(font);
		Assert.Equal(expected, error);
		Assert.Empty(await Store.ListFontsAsync());
	}

	[Fact]
	public async Task Only_font_files_are_accepted()
	{
		Assert.Equal("That isn't a font file (WOFF2, WOFF, TTF or OTF).",
			(await Store.AddFontAsync(Encoding.UTF8.GetBytes("<svg onload=alert(1)></svg>   "), "Brand", 400, "normal", null)).Error);
		Assert.Equal("The font file must be at most 5 MB.", (await Store.AddFontAsync([], "Brand", 400, "normal", null)).Error);
		Assert.Equal("The font file must be at most 5 MB.", (await Store.AddFontAsync(new byte[ThemeStore.MaxFontBytes + 1], "Brand", 400, "normal", null)).Error);
	}

	[Fact]
	public async Task Deleting_a_font_removes_its_file()
	{
		var (font, _) = await Store.AddFontAsync(Consolas, "Brand Mono", 400, "normal", null);
		Assert.True(await Store.DeleteFontAsync(font!.Id));
		Assert.False(File.Exists(Store.FontPath(font)));
		Assert.Empty(await Store.ListFontsAsync());
		Assert.False(await Store.DeleteFontAsync(font.Id));
	}

	[Fact]
	public async Task Which_themes_use_a_family()
	{
		await Store.AddFontAsync(Consolas, "Brand Mono", 400, "normal", null);
		await Store.SaveAsync("a", Input(body: "Brand Mono"), null);
		await Store.SaveAsync("b", Input(heading: "Brand Mono"), null);
		await Store.SaveAsync("c", Input(body: "Georgia"), null);
		Assert.Equal(["a", "b"], (await Store.ThemesUsingFamilyAsync("Brand Mono")).Order());
		Assert.Empty(await Store.ThemesUsingFamilyAsync("Other"));
	}

	[Theory]
	[InlineData("<div class=\"doc-theme theme-farm\"></div><p>x</p>", "farm")]
	[InlineData("<p>x</p><div class=\"doc-theme theme-Farm_2-b\" id=\"i9\"></div>", "Farm_2-b")]
	[InlineData("<div class=\"doc-theme theme-moe\"></div>", "moe")]
	[InlineData("<p>x</p>", null)]
	[InlineData("<p class=\"theme-farm\">x</p>", null)]
	[InlineData("<div class=\"doc-theme theme-../x\"></div>", null)]
	public void The_theme_a_template_uses(string html, string? expected)
	{
		Assert.Equal(expected, Rendering.DocumentComposer.ThemeOf(html));
	}
}
