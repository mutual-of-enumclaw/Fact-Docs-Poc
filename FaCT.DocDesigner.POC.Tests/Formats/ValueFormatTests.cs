using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using FaCT.DocDesigner.POC.Rendering;
using FaCT.DocDesigner.POC.Tests.Clauses;
using Microsoft.Extensions.DependencyInjection;

namespace FaCT.DocDesigner.POC.Tests.Formats;

/// <summary>Custom number and date formats, languages and masks (ValueFormats).</summary>
public sealed class ValueFormatTests
{
	// ---- numbers ---------------------------------------------------------------------------------------------------

	[Theory]
	[InlineData(1234.5, "#,##0.00", "1,234.50")]
	[InlineData(1234.5, "0", "1235")]
	[InlineData(1234.567, "#,##0.000", "1,234.567")]
	[InlineData(1234.5, "$#,##0.00", "$1,234.50")]
	[InlineData(-1234.5, "$#,##0.00", "-$1,234.50")]
	[InlineData(-1234.5, "$#,##0.00;($#,##0.00)", "($1,234.50)")]
	[InlineData(0, "$#,##0.00;($#,##0.00);'None'", "None")]
	[InlineData(0, "#,##0;-#,##0;'Included'", "Included")]
	[InlineData(5, "#,##0;-#,##0;'Included'", "5")]
	[InlineData(0.125, "0.0%", "12.5%")]
	[InlineData(0.125, "P1", "12.5%")]
	[InlineData(1234.5, "C2", "$1,234.50")]
	[InlineData(1234.5, "N0", "1,235")]
	[InlineData(42, "000000", "000042")]
	public void Number_formats(decimal value, string format, string expected)
	{
		Assert.Equal(expected, ValueFormats.FormatNumber(value, format));
	}

	[Theory]
	[InlineData("en-US", "1,234.50")]
	[InlineData("es-US", "1,234.50")]
	[InlineData("es-MX", "1,234.50")]
	[InlineData("fr-CA", "1\u00a0234,50")]
	[InlineData("bogus", "1,234.50")]
	public void Number_formats_follow_the_language(string culture, string expected)
	{
		Assert.Equal(expected, ValueFormats.FormatNumber(1234.5m, "#,##0.00", culture));
	}

	// ---- dates and text ----------------------------------------------------------------------------------------------

	[Theory]
	[InlineData("2026-07-01", "MMMM d, yyyy", null, "July 1, 2026")]
	[InlineData("2026-07-01", "MM/dd/yyyy", null, "07/01/2026")]
	[InlineData("2026-07-01T15:30:00", "M/d/yyyy h:mm tt", null, "7/1/2026 3:30 PM")]
	[InlineData("07/01/2026", "yyyy-MM-dd", null, "2026-07-01")]
	[InlineData("2026-07-01", "dddd, MMMM d, yyyy", null, "Wednesday, July 1, 2026")]
	[InlineData("2026-07-01", "d 'de' MMMM 'de' yyyy", "es-US", "1 de julio de 2026")]
	[InlineData("2026-07-01", "dddd d 'de' MMMM", "es-MX", "miércoles 1 de julio")]
	[InlineData("2026-07-01", "d MMMM yyyy", "fr-CA", "1 juillet 2026")]
	public void Date_formats(string value, string format, string? culture, string expected)
	{
		Assert.Equal(expected, ValueFormats.FormatText(value, format, culture));
	}

	[Theory]
	[InlineData("Acme Bakery", "MMMM d, yyyy", "Acme Bakery")]
	[InlineData("Acme Bakery", "upper", "ACME BAKERY")]
	[InlineData("Acme Bakery", "LOWER", "acme bakery")]
	[InlineData("", "#,##0", "")]
	public void Text_that_is_not_a_date_is_unchanged_except_upper_and_lower(string value, string format, string expected)
	{
		Assert.Equal(expected, ValueFormats.FormatText(value, format));
	}

	// ---- masks -----------------------------------------------------------------------------------------------------

	[Theory]
	[InlineData("5551234567", "(###) ###-####", "(555) 123-4567")]
	[InlineData("555-123-4567", "(###) ###-####", "(555) 123-4567")]
	[InlineData("(555) 123 4567", "###.###.####", "555.123.4567")]
	[InlineData("980221234", "#####-####", "98022-1234")]
	[InlineData("911234567", "##-#######", "91-1234567")]
	[InlineData("123456789", "***-**-####", "***-**-6789")]
	[InlineData("12345", "(###) ###-####", "12345")]
	[InlineData("123456789012", "(###) ###-####", "123456789012")]
	[InlineData("", "(###) ###-####", "")]
	[InlineData("call me", "(###) ###-####", "call me")]
	public void Masks(string value, string mask, string expected)
	{
		Assert.Equal(expected, ValueFormats.Mask(value, mask));
	}

	// ---- what isn't allowed -------------------------------------------------------------------------------------------

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("   ")]
	[InlineData("#,##0.00 {{ x }}")]
	[InlineData("0 \"text\"")]
	[InlineData("0}")]
	public void Formats_that_would_break_the_template_are_refused(string? format)
	{
		Assert.NotNull(ValueFormats.FormatProblem(format));
	}

	[Fact]
	public void Formats_are_at_most_100_characters()
	{
		Assert.Null(ValueFormats.FormatProblem(new string('0', 100)));
		Assert.Equal("A format is 1 to 100 characters.", ValueFormats.FormatProblem(new string('0', 101)));
	}

	[Theory]
	[InlineData("#,##0.00")]
	[InlineData("$#,##0.00;($#,##0.00);'None'")]
	[InlineData("MMMM d, yyyy")]
	[InlineData("D")]
	[InlineData("C2")]
	public void Ordinary_formats_are_fine(string format)
	{
		Assert.Null(ValueFormats.FormatProblem(format));
	}

	[Fact]
	public void Only_known_languages()
	{
		Assert.Null(ValueFormats.CultureProblem(null));
		Assert.Null(ValueFormats.CultureProblem("es-US"));
		Assert.StartsWith("The language 'de-DE' isn't one of", ValueFormats.CultureProblem("de-DE"));
	}

	[Theory]
	[InlineData(null, "A mask is 1 to 40 characters.")]
	[InlineData("abc", "A mask needs at least one # (a digit) or * (a hidden digit).")]
	[InlineData("(###) {###}", "A mask can't contain { } or \".")]
	public void Bad_masks(string? mask, string expected)
	{
		Assert.Equal(expected, ValueFormats.MaskProblem(mask));
	}
}

/// <summary>The format and mask Liquid filters in rendered documents, and the format preview API.</summary>
public sealed class FormatRenderTests(ClauseAppFactory factory) : IClassFixture<ClauseAppFactory>
{
	private static readonly JsonElement Data = JsonDocument.Parse("""
		{ "policy": { "premium": 1234.5, "credit": -250, "fees": 0, "effective": "2026-07-01", "phone": "5551234567",
		              "phoneNumber": 5551234567, "insured": "Acme Bakery", "missing": null } }
		""").RootElement.Clone();

	private DocumentComposer Composer => factory.Services.GetRequiredService<DocumentComposer>();

	private async Task<string> BodyAsync(string html)
	{
		var result = await Composer.ComposeAsync(html, "", Data);
		Assert.True(result.Error is null, result.Error);
		var body = result.Html![(result.Html.IndexOf("<body>", StringComparison.Ordinal) + 6)..];
		return body[..body.IndexOf("</body>", StringComparison.Ordinal)];
	}

	[Theory]
	[InlineData("{{ policy.premium | format: \"$#,##0.00;($#,##0.00);'None'\" }}", "$1,234.50")]
	[InlineData("{{ policy.credit | format: \"$#,##0.00;($#,##0.00);'None'\" }}", "($250.00)")]
	[InlineData("{{ policy.fees | format: \"$#,##0.00;($#,##0.00);'None'\" }}", "None")]
	[InlineData("{{ policy.effective | format: \"MMMM d, yyyy\" }}", "July 1, 2026")]
	[InlineData("{{ policy.effective | format: \"d 'de' MMMM 'de' yyyy\", \"es-US\" }}", "1 de julio de 2026")]
	[InlineData("{{ policy.premium | format: \"#,##0.00\", \"fr-CA\" }}", "1&nbsp;234,50")]
	[InlineData("{{ policy.insured | format: \"upper\" }}", "ACME BAKERY")]
	[InlineData("{{ policy.phone | mask: \"(###) ###-####\" }}", "(555) 123-4567")]
	[InlineData("{{ policy.phoneNumber | mask: \"(###) ###-####\" }}", "(555) 123-4567")]
	[InlineData("{{ policy.missing | format: \"#,##0\" }}", "")]
	[InlineData("{{ policy.nothing | mask: \"###\" }}", "")]
	[InlineData("{{ policy.missing | format: \"#,##0\" | default: \"n/a\" }}", "n/a")]
	public async Task Filters_in_templates(string liquid, string expected)
	{
		Assert.Equal($"<p>{expected}</p>", await BodyAsync($"<p>{liquid}</p>"));
	}

	[Theory]
	[InlineData("{{ policy.premium | format: \"\" }}", "A format is 1 to 100 characters.")]
	[InlineData("{{ policy.premium | format: \"0\", \"de-DE\" }}", "The language 'de-DE' isn't one of")]
	[InlineData("{{ policy.phone | mask: \"abc\" }}", "A mask needs at least one #")]
	public async Task Bad_formats_are_render_errors(string liquid, string expected)
	{
		var result = await Composer.ComposeAsync($"<p>{liquid}</p>", "", Data);
		Assert.Null(result.Html);
		Assert.Contains(expected, result.Error);
	}

	[Fact]
	public async Task The_preview_api_formats_values_like_the_pdf()
	{
		var client = factory.CreateClient();
		var response = await client.PostAsJsonAsync("/api/formats/preview", new
		{
			items = new object[]
			{
				new { value = 0, format = "#,##0;-#,##0;'None'" },
				new { value = "2026-07-01", format = "MMMM d, yyyy", culture = "es-US" },
				new { value = "5551234567", mask = "(###) ###-####" },
				new { value = 5551234567, mask = "(###) ###-####" },
				new { value = (object?)null, format = "0" },
				new { value = 1, format = "{bad}" },
				new { value = 1, format = "0", culture = "xx-XX" },
				new { value = "1", mask = "nope" }
			}
		});
		response.EnsureSuccessStatusCode();
		var results = (await response.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().ToList();
		Assert.Equal("None", results[0].GetProperty("text").GetString());
		Assert.Equal("julio 1, 2026", results[1].GetProperty("text").GetString());
		Assert.Equal("(555) 123-4567", results[2].GetProperty("text").GetString());
		Assert.Equal("(555) 123-4567", results[3].GetProperty("text").GetString());
		Assert.Equal("", results[4].GetProperty("text").GetString());
		Assert.StartsWith("A format can't contain", results[5].GetProperty("error").GetString());
		Assert.StartsWith("The language 'xx-XX'", results[6].GetProperty("error").GetString());
		Assert.StartsWith("A mask needs", results[7].GetProperty("error").GetString());
	}

	[Fact]
	public async Task The_preview_api_takes_at_most_200_values()
	{
		var client = factory.CreateClient();
		var response = await client.PostAsJsonAsync("/api/formats/preview", new { items = Enumerable.Range(0, 201).Select(i => new { value = i, format = "0" }) });
		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		Assert.Equal(["en-US", "es-US", "es-MX", "en-CA", "fr-CA"], await client.GetFromJsonAsync<string[]>("/api/formats/cultures"));
	}

	[Fact]
	public async Task Custom_formats_print_in_the_pdf()
	{
		var client = factory.CreateClient();
		var response = await client.PostAsJsonAsync("/api/render", new JsonObject
		{
			["html"] = "<p>Credit {{ policy.credit | format: \"$#,##0.00;($#,##0.00);'None'\" }} fees {{ policy.fees | format: \"$#,##0.00;($#,##0.00);'None'\" }} call {{ policy.phone | mask: \"(###) ###-####\" }}</p>",
			["css"] = "",
			["data"] = JsonNode.Parse(Data.GetRawText())
		});
		Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
		var text = System.Text.RegularExpressions.Regex.Replace(PdfText.Extract(await response.Content.ReadAsByteArrayAsync()), @"\s+", " ");
		Assert.Contains("Credit ($250.00) fees None call (555) 123-4567", text);
	}
}
