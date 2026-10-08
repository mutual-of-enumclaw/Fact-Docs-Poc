using System.Text.Json;
using FaCT.DocDesigner.POC.Import;
using FaCT.DocDesigner.POC.Legacy;
using FaCT.DocDesigner.POC.Rendering;
using Microsoft.Extensions.DependencyInjection;
using UglyToad.PdfPig.Writer;
using static FaCT.DocDesigner.POC.Tests.Import.Pdf;

namespace FaCT.DocDesigner.POC.Tests.Import;

internal static class SystemFonts
{
	public static byte[] Read(string file) => File.ReadAllBytes(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), file));

	/// <summary>
	/// A one-page PDF whose text is set in an embedded TrueType font: the whole font file as FontFile2, WinAnsi text.
	/// (PdfPig's writer subsets fonts and drops their Unicode map, which the importer rightly refuses.)
	/// </summary>
	public static byte[] PdfWith(string fontFile, params string[] lines)
	{
		var font = Read(fontFile);
		var baseFont = Path.GetFileNameWithoutExtension(fontFile) switch
		{
			"georgia" => "Georgia",
			"georgiab" => "Georgia-Bold",
			"consola" => "Consolas",
			"wingding" => "Wingdings-Regular",
			var other => other
		};
		var content = new System.Text.StringBuilder();
		for (var i = 0; i < lines.Length; i++)
		{
			content.Append("BT /F1 14 Tf 72 ").Append(720 - i * 24).Append(" Td (").Append(lines[i]).Append(") Tj ET ");
		}
		return Raw(
			"<< /Type /Catalog /Pages 2 0 R >>",
			"<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
			"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
			Stream(content.ToString()),
			// Approximate widths: positions only need to be plausible; the render uses the real font.
			$"<< /Type /Font /Subtype /TrueType /BaseFont /{baseFont} /Encoding /WinAnsiEncoding /FirstChar 32 /LastChar 126 /Widths [{string.Join(' ', Enumerable.Repeat("560", 95))}] /FontDescriptor 6 0 R >>",
			$"<< /Type /FontDescriptor /FontName /{baseFont} /Flags 32 /FontBBox [-200 -300 1200 1000] /ItalicAngle 0 /Ascent 900 /Descent -220 /CapHeight 700 /StemV 80 /FontFile2 7 0 R >>",
			$"<< /Length {font.Length} /Length1 {font.Length} >>\nstream\n{System.Text.Encoding.Latin1.GetString(font)}\nendstream");
	}

	/// <summary>The same text with PdfPig's writer, which embeds a subset without a Unicode map.</summary>
	public static byte[] SubsetPdfWith(string fontFile, string text)
	{
		var builder = new PdfDocumentBuilder();
		var font = builder.AddTrueTypeFont(Read(fontFile));
		builder.AddPage(612, 792).AddText(text, 14, At(72, 720), font);
		return builder.Build();
	}
}

public sealed class TrueTypeFontTests
{
	[Fact]
	public void A_unicode_font_is_read_and_maps_its_characters()
	{
		var font = TrueTypeFont.TryRead(SystemFonts.Read("arial.ttf"));
		Assert.NotNull(font);
		Assert.True(font.Glyph('A') > 0);
		Assert.True(font.Glyph('\u00E9') > 0);
		Assert.True(font.Glyph('\u20AC') > 0);
		Assert.Equal(0, font.Glyph(0x4E2D));
		Assert.Equal(0, font.Glyph(0x1F600));
	}

	[Theory]
	[InlineData("Hello, World! 123 $%&", true)]
	[InlineData("Caf\u00E9 \u2013 \u201Cquoted\u201D", true)]
	[InlineData("tabs\tand\nnewlines", true)]
	[InlineData("\u4E2D\u6587", false)]
	[InlineData("mixed A \u4E2D", false)]
	[InlineData("", true)]
	public void Coverage_is_all_or_nothing(string text, bool covered) =>
		Assert.Equal(covered, TrueTypeFont.TryRead(SystemFonts.Read("arial.ttf"))!.Covers(text));

	[Fact]
	public void The_full_repertoire_map_is_used_beyond_the_basic_plane()
	{
		var emoji = TrueTypeFont.TryRead(SystemFonts.Read("seguiemj.ttf"));
		Assert.NotNull(emoji);
		Assert.True(emoji.Glyph(0x1F600) > 0);
	}

	[Fact]
	public void Symbol_fonts_without_a_unicode_map_are_refused() =>
		Assert.Null(TrueTypeFont.TryRead(SystemFonts.Read("wingding.ttf")));

	[Theory]
	[InlineData(0)]
	[InlineData(11)]
	[InlineData(100)]
	[InlineData(5000)]
	public void Truncated_fonts_are_refused(int length) =>
		Assert.Null(TrueTypeFont.TryRead(SystemFonts.Read("arial.ttf")[..length]));

	[Fact]
	public void Non_font_bytes_are_refused()
	{
		Assert.Null(TrueTypeFont.TryRead("%PDF-1.7 not a font at all......"u8.ToArray()));
		Assert.Null(TrueTypeFont.TryRead(new byte[64]));
	}

	[Fact]
	public void Table_records_pointing_outside_the_file_are_refused()
	{
		var font = new byte[12 + 16];
		font[1] = 1;              // version 0x00010000
		font[5] = 1;              // one table
		"cmap"u8.CopyTo(font.AsSpan(12));
		font[20] = 0x7F;          // offset far beyond the end
		font[27] = 8;             // length 8
		Assert.Null(TrueTypeFont.TryRead(font));
	}

	[Fact]
	public void Fonts_without_outlines_are_refused()
	{
		// A real cmap but the outline tables renamed away.
		var font = SystemFonts.Read("arial.ttf");
		var tables = (font[4] << 8) | font[5];
		for (var i = 0; i < tables; i++)
		{
			var tag = font.AsSpan(12 + i * 16, 4);
			if (tag.SequenceEqual("glyf"u8)) "xxxx"u8.CopyTo(tag);
		}
		Assert.Null(TrueTypeFont.TryRead(font));
	}
}

public sealed class EmbeddedFontImportTests : IDisposable
{
	private readonly TempAssets _assets = new();

	public void Dispose() => _assets.Dispose();

	private DocumentImportResult Import(byte[] pdf) => new PdfImporter(new LegacyFormImporter(_assets.Store)).Import(pdf);

	private static Dictionary<string, string> Style(AngleSharp.Dom.IElement element) =>
		(element.GetAttribute("style") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)
			.Select(d => d.Split(':', 2)).ToDictionary(p => p[0].Trim(), p => p[1].Trim());

	[Fact]
	public void Text_in_an_embedded_font_uses_that_font_with_a_standard_fallback()
	{
		var result = Import(SystemFonts.PdfWith("georgia.ttf", "Georgia wording"));

		var span = Html.Parse(result.Html).QuerySelector("span.abs")!;
		var family = Style(span)["font-family"];
		Assert.Matches(@"^'F_Georgia_[0-9A-F]{8}', 'Times New Roman', Times, serif$", family);
		var registered = family[1..family.IndexOf('\'', 1)];
		Assert.Contains(registered, _assets.Store.FontFamilies);
		Assert.Contains("1 font(s) embedded in the PDF are used for its text.", result.Notes);
		Assert.Contains(registered, _assets.Store.FontCss());
	}

	[Fact]
	public void A_bold_embedded_font_is_registered_bold_so_the_browser_does_not_embolden_it_again()
	{
		var result = Import(SystemFonts.PdfWith("georgiab.ttf", "Bold wording"));

		var span = Html.Parse(result.Html).QuerySelector("span.abs")!;
		Assert.Equal("bold", Style(span)["font-weight"]);
		var css = _assets.Store.FontCss();
		Assert.Matches(@"@font-face\{font-family:'F_Georgia[A-Za-z_]*_[0-9A-F]{8}';font-weight:bold;", css);
	}

	[Fact]
	public void The_same_font_program_is_one_family_across_documents()
	{
		var first = Import(SystemFonts.PdfWith("consola.ttf", "First"));
		var second = Import(SystemFonts.PdfWith("consola.ttf", "Second"));

		string FamilyOf(DocumentImportResult r) => Style(Html.Parse(r.Html).QuerySelector("span.abs")!)["font-family"];
		Assert.Equal(FamilyOf(first), FamilyOf(second));
		Assert.EndsWith("'Courier New', Courier, monospace", FamilyOf(first));
		Assert.Single(_assets.Store.FontFamilies, f => f.StartsWith("F_Consolas", StringComparison.Ordinal));
	}

	[Fact]
	public void Standard_14_fonts_keep_the_standard_family()
	{
		var result = Import(Letter((page, fonts) => page.AddText("Helvetica wording", 12, At(72, 720), fonts.Helvetica)));

		Assert.Equal(LegacyFormImporter.SansFontStack, Style(Html.Parse(result.Html).QuerySelector("span.abs")!)["font-family"]);
		Assert.DoesNotContain(result.Notes, n => n.Contains("font(s)"));
		Assert.Empty(_assets.Store.FontFamilies);
	}

	[Fact]
	public void An_embedded_symbol_font_is_replaced_and_reported()
	{
		var result = Import(SystemFonts.PdfWith("wingding.ttf", "abc"));

		Assert.DoesNotContain("F_", result.Html);
		Assert.Contains(result.Notes, n => n.StartsWith("1 embedded font(s) can't be used"));
	}

	[Fact]
	public void A_subset_font_without_a_unicode_map_is_replaced_and_reported()
	{
		var result = Import(SystemFonts.SubsetPdfWith("georgia.ttf", "Subset wording"));

		Assert.DoesNotContain("F_", result.Html);
		Assert.Equal(LegacyFormImporter.SerifFontStack, Style(Html.Parse(result.Html).QuerySelector("span.abs")!)["font-family"]);
		Assert.Contains(result.Notes, n => n.StartsWith("1 embedded font(s) can't be used"));
	}

	[Fact]
	public void An_embedded_font_missing_some_characters_is_not_used()
	{
		// Consolas has no glyph for U+2603 (snowman): the font is not used rather than printing blanks.
		using var document = UglyToad.PdfPig.PdfDocument.Open(SystemFonts.PdfWith("consola.ttf", "ok"));
		var fonts = new EmbeddedFonts(document);
		fonts.Collect(document.GetPage(1));

		Assert.NotNull(fonts.Resolve("Consolas", "plain text"));
		Assert.Null(fonts.Resolve("Consolas", "snow \u2603"));
		Assert.Null(fonts.Resolve("Unknown", "x"));
		Assert.Equal(1, fonts.Used);
		Assert.Equal(0, fonts.Rejected);
		Assert.Contains("@font-face { font-family: 'F_Consolas_", fonts.FaceCss());
	}
}

/// <summary>The embedded font reaches the rendered PDF.</summary>
public sealed class EmbeddedFontRenderTests(ImportAppFactory factory) : IClassFixture<ImportAppFactory>
{
	[Fact]
	public async Task The_render_prints_the_text_in_the_embedded_font()
	{
		var result = factory.Services.GetRequiredService<PdfImporter>().Import(SystemFonts.PdfWith("georgia.ttf", "Rendered in Georgia"));
		var composed = await factory.Services.GetRequiredService<DocumentComposer>().ComposeAsync(result.Html, result.Css, JsonSerializer.SerializeToElement(new { }));

		var pdf = await factory.Services.GetRequiredService<PdfRenderer>().RenderAsync(composed.Html!);

		using var document = UglyToad.PdfPig.PdfDocument.Open(pdf);
		var letters = document.GetPage(1).Letters.Where(l => !string.IsNullOrWhiteSpace(l.Value)).ToList();
		Assert.Equal("RenderedinGeorgia", string.Concat(letters.Select(l => l.Value)));
		Assert.All(letters, l => Assert.Contains("Georgia", l.FontName));
	}
}
