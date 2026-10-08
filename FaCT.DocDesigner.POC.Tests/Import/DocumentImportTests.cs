using System.IO.Compression;
using System.Text;
using FaCT.DocDesigner.POC.Import;
using FaCT.DocDesigner.POC.Legacy;

namespace FaCT.DocDesigner.POC.Tests.Import;

/// <summary>File detection, upload guards and the name/path rules shared by both importers.</summary>
public sealed class DocumentImportTests
{
	[Theory]
	[InlineData("%PDF-1.7\n...", DocumentKind.Pdf)]
	[InlineData("PK\u0003\u0004rest", DocumentKind.Docx)]
	[InlineData("<html></html>", DocumentKind.Unknown)]
	[InlineData("%PD", DocumentKind.Unknown)]
	[InlineData("", DocumentKind.Unknown)]
	public void Detect_identifies_the_file_by_its_content(string start, DocumentKind expected) =>
		Assert.Equal(expected, DocumentImport.Detect(Encoding.Latin1.GetBytes(start)));

	[Fact]
	public void Old_binary_doc_files_are_recognised()
	{
		byte[] ole = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0, 0];
		Assert.True(DocumentImport.IsLegacyOfficeFile(ole));
		Assert.Equal(DocumentKind.Unknown, DocumentImport.Detect(ole));
		Assert.False(DocumentImport.IsLegacyOfficeFile("%PDF-1.4"u8));
	}

	[Theory]
	[InlineData("application/pdf", DocumentKind.Pdf)]
	[InlineData("Application/PDF", DocumentKind.Pdf)]
	[InlineData("application/pdf; charset=binary", DocumentKind.Pdf)]
	[InlineData("application/vnd.openxmlformats-officedocument.wordprocessingml.document", DocumentKind.Docx)]
	[InlineData("application/msword", DocumentKind.Unknown)]
	[InlineData("multipart/form-data; boundary=x", DocumentKind.Unknown)]
	[InlineData("text/plain", DocumentKind.Unknown)]
	[InlineData(null, DocumentKind.Unknown)]
	public void Content_type_selects_the_importer(string? contentType, DocumentKind expected) =>
		Assert.Equal(expected, DocumentImport.ForContentType(contentType));

	[Fact]
	public void A_real_docx_passes_the_package_check() =>
		DocumentImport.CheckDocxPackage(Docx.WithBody(Docx.P("Hello")));

	[Fact]
	public void A_zip_that_is_not_a_word_document_is_rejected()
	{
		var zip = Zip(("readme.txt", Encoding.UTF8.GetBytes("hello")));
		var error = Assert.Throws<DocumentImportException>(() => DocumentImport.CheckDocxPackage(zip));
		Assert.Contains("not a Word", error.Message);
	}

	[Fact]
	public void A_corrupt_zip_is_rejected()
	{
		var bytes = Encoding.Latin1.GetBytes("PK\u0003\u0004 this is not really a zip");
		Assert.Throws<DocumentImportException>(() => DocumentImport.CheckDocxPackage(bytes));
	}

	[Fact]
	public void A_zip_with_too_many_entries_is_rejected()
	{
		var entries = Enumerable.Range(0, DocumentImport.MaxDocxEntries + 1)
			.Select(i => ($"word/part{i}.xml", Array.Empty<byte>()))
			.Append(("word/document.xml", Array.Empty<byte>()))
			.ToArray();
		var error = Assert.Throws<DocumentImportException>(() => DocumentImport.CheckDocxPackage(Zip(entries)));
		Assert.Contains("too many parts", error.Message);
	}

	[Fact]
	public void A_zip_bomb_is_rejected_before_it_is_expanded()
	{
		// ~201 MB of zeros compresses to well under 1 MB.
		using var stream = new MemoryStream();
		using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
		{
			zip.CreateEntry("word/document.xml");
			using var entry = zip.CreateEntry("word/media/bomb.bin", CompressionLevel.SmallestSize).Open();
			var zeros = new byte[1024 * 1024];
			for (var i = 0; i < 201; i++) entry.Write(zeros);
		}
		Assert.True(stream.Length < DocumentImport.MaxUploadBytes);
		var error = Assert.Throws<DocumentImportException>(() => DocumentImport.CheckDocxPackage(stream.ToArray()));
		Assert.Contains("too large", error.Message);
	}

	[Theory]
	[InlineData("PolicyNumber", "PolicyNumber")]
	[InlineData("Insured Name", "Insured_Name")]
	[InlineData("Policy-Number", "Policy_Number")]
	[InlineData("policy.number", "policy.number")]
	[InlineData("policy..number", "policy.number")]
	[InlineData("insured.address.city", "insured.address.city")]
	[InlineData("2ndInsured", "_2ndInsured")]
	[InlineData("brand", "field_brand")]
	[InlineData("Brand.logo", "field_Brand.logo")]
	[InlineData("empty", "field_empty")]
	[InlineData("{{ evil }}", "evil")]
	[InlineData("x\" onmouseover=\"y", "x_onmouseover_y")]
	[InlineData("a}}{%raw%}", "a_raw")]
	[InlineData("   ", null)]
	[InlineData("***", null)]
	[InlineData(null, null)]
	public void Field_names_become_safe_liquid_paths(string? name, string? expected) =>
		Assert.Equal(expected, DocxImporter.ToFieldPath(name));

	[Fact]
	public void Overlong_field_names_are_refused() =>
		Assert.Null(DocxImporter.ToFieldPath(new string('a', 101)));

	[Theory]
	[InlineData(" MERGEFIELD PolicyNumber ", "PolicyNumber")]
	[InlineData(" MERGEFIELD  PolicyNumber  \\* MERGEFORMAT ", "PolicyNumber")]
	[InlineData(" MERGEFIELD \"Insured Name\" \\* MERGEFORMAT ", "Insured Name")]
	[InlineData("mergefield lower", "lower")]
	[InlineData(" MERGEFIELD Name\\* Upper", "Name")]
	[InlineData(" PAGE ", null)]
	[InlineData(" DATE \\@ \"M/d/yyyy\" ", null)]
	[InlineData("", null)]
	[InlineData(null, null)]
	public void Merge_field_instructions_are_parsed(string? instruction, string? expected) =>
		Assert.Equal(expected, DocxImporter.MergeFieldName(instruction));

	[Theory]
	[InlineData("plain", "plain")]
	[InlineData("a < b & c", "a &lt; b &amp; c")]
	[InlineData("{{ x }}", "{" + DocumentImport.LiquidBreak + "{ x }}")]
	[InlineData("{% if %}", "{" + DocumentImport.LiquidBreak + "% if %}")]
	[InlineData("{{{", "{" + DocumentImport.LiquidBreak + "{" + DocumentImport.LiquidBreak + "{")]
	[InlineData("{ single } braces", "{ single } braces")]
	[InlineData("}} {%", "}} {" + DocumentImport.LiquidBreak + "%")]
	public void Encoded_text_never_contains_a_liquid_opener(string text, string expected)
	{
		var encoded = DocumentImport.EncodeText(text);
		Assert.Equal(expected, encoded);
		Assert.DoesNotMatch(@"\{[{%]", encoded);
	}

	[Theory]
	[InlineData("ABCDEF+Arial-BoldMT", LegacyFormImporter.SansFontStack, true, false)]
	[InlineData("Helvetica", LegacyFormImporter.SansFontStack, false, false)]
	[InlineData("Helvetica-Oblique", LegacyFormImporter.SansFontStack, false, true)]
	[InlineData("Times-Roman", LegacyFormImporter.SerifFontStack, false, false)]
	[InlineData("TimesNewRomanPS-BoldItalicMT", LegacyFormImporter.SerifFontStack, true, true)]
	[InlineData("Georgia", LegacyFormImporter.SerifFontStack, false, false)]
	[InlineData("MicrosoftSansSerif", LegacyFormImporter.SansFontStack, false, false)]
	[InlineData("Courier-Bold", LegacyFormImporter.MonoFontStack, true, false)]
	[InlineData("QWERTY+Consolas", LegacyFormImporter.MonoFontStack, false, false)]
	[InlineData("Arial-Black", LegacyFormImporter.SansFontStack, true, false)]
	[InlineData(null, LegacyFormImporter.SansFontStack, false, false)]
	public void Pdf_font_names_map_to_standard_families(string? fontName, string family, bool bold, bool italic) =>
		Assert.Equal((family, bold, italic), PdfImporter.MapFont(fontName));

	private static byte[] Zip(params (string Name, byte[] Content)[] entries)
	{
		using var stream = new MemoryStream();
		using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
		{
			foreach (var (name, content) in entries)
			{
				using var entry = zip.CreateEntry(name).Open();
				entry.Write(content);
			}
		}
		return stream.ToArray();
	}
}

/// <summary>The legacy importer's style allow-list, which PDF imports go through.</summary>
public sealed class LegacyFontStackTests : IDisposable
{
	private readonly TempAssets _assets = new();

	public void Dispose() => _assets.Dispose();

	[Theory]
	[InlineData(LegacyFormImporter.SansFontStack)]
	[InlineData(LegacyFormImporter.SerifFontStack)]
	[InlineData(LegacyFormImporter.MonoFontStack)]
	public void Standard_font_stacks_are_kept(string stack)
	{
		var result = Import($"font-family:{stack}");
		Assert.Contains("font-family:" + stack + ";", result.Html);
		Assert.Empty(result.MissingFonts);
	}

	[Theory]
	[InlineData("'F_Arial_1A2B3C4D', " + LegacyFormImporter.SansFontStack)]
	[InlineData("'F_Times_X', " + LegacyFormImporter.SerifFontStack)]
	[InlineData("'F_Mono', " + LegacyFormImporter.MonoFontStack)]
	public void A_form_font_with_a_standard_fallback_is_kept(string family)
	{
		var result = Import($"font-family:{family}");
		Assert.Contains("font-family:" + family + ";", result.Html);
	}

	[Theory]
	[InlineData("'F_Arial', 'Comic Sans MS'")]
	[InlineData("'Arial', Arial, Helvetica, sans-serif")]
	[InlineData("'F_Bad Name', Arial, Helvetica, sans-serif")]
	[InlineData("'F_X', Arial, Helvetica, sans-serif, url(x)")]
	public void Other_font_and_fallback_combinations_are_dropped(string family) =>
		Assert.DoesNotContain("font-family", Import($"font-family:{family}").Html);

	[Theory]
	[InlineData("'Comic Sans MS'")]
	[InlineData("Arial")]
	[InlineData("Arial, Helvetica, sans-serif, url(x)")]
	[InlineData("Arial</style><script>alert(1)</script>")]
	public void Other_font_families_are_dropped(string family)
	{
		var result = Import($"font-family:{family}");
		Assert.DoesNotContain("font-family", result.Html);
		Assert.DoesNotContain("<script", result.Html);
	}

	private LegacyImportResult Import(string style) =>
		new LegacyFormImporter(_assets.Store).Import(
			$"<section class=\"form-page\"><span class=\"abs\" style=\"left:10pt;top:10pt;{style}\">Text</span></section>");
}
