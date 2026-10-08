using System.Text.Json;
using FaCT.DocDesigner.POC.Rendering;
using FaCT.DocDesigner.POC.Templates;
using FaCT.DocDesigner.POC.Tests.Clauses;
using Microsoft.Extensions.DependencyInjection;

namespace FaCT.DocDesigner.POC.Tests.Languages;

/// <summary>What a translated template prints: its language's words for values, its clause translations, its PDF footer.</summary>
public sealed class LanguageRenderTests(ClauseAppFactory factory) : IClassFixture<ClauseAppFactory>
{
	private const string Spanish = "<div class=\"doc-language lang-es\"></div>";
	private const string French = "<div class=\"doc-language lang-fr\"></div>";
	private static readonly JsonElement EmptyProject = JsonDocument.Parse("{}").RootElement.Clone();

	private static readonly JsonElement Data = JsonDocument.Parse("""
		{ "policy": { "number": "CPP1234567", "effective": "2026-10-01", "renewal": true, "audit": false, "premium": 1500.5 },
		  "claims": [] }
		""").RootElement.Clone();

	private DocumentComposer Composer => factory.Services.GetRequiredService<DocumentComposer>();
	private ClauseStore Clauses => factory.Services.GetRequiredService<ClauseStore>();

	private static string Unique(string name) => name + "-" + Guid.NewGuid().ToString("N")[..8];

	private async Task<string> BodyAsync(string html)
	{
		var result = await Composer.ComposeAsync(html, "", Data);
		Assert.True(result.Error is null, result.Error);
		var page = result.Html!;
		return page[(page.IndexOf("<body>", StringComparison.Ordinal) + 6)..page.LastIndexOf("</body>", StringComparison.Ordinal)];
	}

	private void PublishClause(string name, string html, string? language = null)
	{
		var store = Clauses.Versions.Language(language);
		var version = store.SaveDraftAsync(name, EmptyProject, html, "", null).GetAwaiter().GetResult().Version;
		store.PublishAsync(name, version).GetAwaiter().GetResult();
	}

	[Fact]
	public async Task The_page_says_its_language()
	{
		var english = (await Composer.ComposeAsync("<p>x</p>", "", Data)).Html!;
		var spanish = (await Composer.ComposeAsync(Spanish + "<p>x</p>", "", Data)).Html!;
		Assert.StartsWith("<!DOCTYPE html><html lang=\"en\">", english);
		Assert.StartsWith("<!DOCTYPE html><html lang=\"es\">", spanish);
		// the marker is kept (class-only div) and hidden by the brand stylesheet
		Assert.Contains("class=\"doc-language lang-es\"", spanish);
		Assert.Contains(".doc-language { display: none !important; }", spanish);
	}

	[Fact]
	public async Task True_and_false_print_in_the_document_language()
	{
		const string fields = "<p>{{ policy.renewal }}/{{ policy.audit }}</p>";
		Assert.Equal("<p>Yes/No</p>", await BodyAsync(fields));
		Assert.Equal(Spanish + "<p>S\u00ed/No</p>", await BodyAsync(Spanish + fields));
		Assert.Equal(French + "<p>Oui/Non</p>", await BodyAsync(French + fields));
	}

	[Fact]
	public async Task True_and_false_still_work_in_conditions()
	{
		Assert.Equal(Spanish + "<p>renueva</p>", await BodyAsync(Spanish + "<p>{% if policy.renewal %}renueva{% endif %}{% if policy.audit %}audita{% endif %}</p>"));
	}

	[Fact]
	public async Task A_format_without_a_language_uses_the_document_language()
	{
		const string date = "<p>{{ policy.effective | format: \"d 'de' MMMM 'de' yyyy\" }}</p>";
		Assert.Equal(Spanish + "<p>1 de octubre de 2026</p>", await BodyAsync(Spanish + date));
		Assert.Equal("<p>1 de October de 2026</p>", await BodyAsync(date));
		Assert.Equal(French + "<p>1 octobre 2026</p>", await BodyAsync(French + "<p>{{ policy.effective | format: \"d MMMM yyyy\" }}</p>"));
	}

	[Fact]
	public async Task A_format_with_a_language_keeps_it()
	{
		Assert.Equal(Spanish + "<p>October 1, 2026</p>",
			await BodyAsync(Spanish + "<p>{{ policy.effective | format: \"MMMM d, yyyy\", \"en-US\" }}</p>"));
	}

	[Fact]
	public async Task The_preset_formats_print_the_same_in_every_language()
	{
		const string presets = "<p>{{ policy.premium | currency }} {{ policy.effective | shortdate }} {{ policy.premium | number }}</p>";
		var english = await BodyAsync(presets);
		Assert.Equal("<p>$1,500.50 10/01/2026 1,501</p>", english);
		Assert.Equal(Spanish + english, await BodyAsync(Spanish + presets));
	}

	[Fact]
	public async Task An_empty_chart_says_so_in_the_document_language()
	{
		Assert.Contains("Sin datos", await BodyAsync(Spanish + "{{ claims | chart: \"column\", \"year\", \"amount\" }}"));
		Assert.Contains("No data", await BodyAsync("{{ claims | chart: \"column\", \"year\", \"amount\" }}"));
	}

	[Fact]
	public async Task A_Spanish_template_includes_the_Spanish_clause()
	{
		var name = Unique("excl");
		PublishClause(name, "<p>Exclusion {{ policy.number }}</p>");
		PublishClause(name, "<p>Exclusi\u00f3n {{ policy.number }}</p>", "es");

		Assert.Equal("<p>Exclusion CPP1234567</p>", await BodyAsync($"{{% include '{name}' %}}"));
		Assert.Equal(Spanish + "<p>Exclusi\u00f3n CPP1234567</p>", await BodyAsync(Spanish + $"{{% include '{name}' %}}"));
	}

	[Fact]
	public async Task A_clause_without_a_translation_prints_in_English()
	{
		var name = Unique("excl");
		PublishClause(name, "<p>Exclusion</p>");
		Assert.Equal(Spanish + "<p>Exclusion</p>", await BodyAsync(Spanish + $"{{% include '{name}' %}}"));
	}

	[Fact]
	public async Task The_clause_css_comes_from_the_version_printed()
	{
		var name = Unique("excl");
		var english = Clauses.Versions.SaveDraftAsync(name, EmptyProject, "<p>x</p>", ".en-only{}", null).GetAwaiter().GetResult().Version;
		await Clauses.Versions.PublishAsync(name, english);
		var spanish = Clauses.Versions.Language("es").SaveDraftAsync(name, EmptyProject, "<p>y</p>", ".es-only{}", null).GetAwaiter().GetResult().Version;
		await Clauses.Versions.Language("es").PublishAsync(name, spanish);

		var page = (await Composer.ComposeAsync(Spanish + $"{{% include '{name}' %}}", "", Data)).Html!;
		Assert.Contains(".es-only{}", page);
		Assert.DoesNotContain(".en-only{}", page);
	}

	[Fact]
	public async Task The_template_export_page_inlines_the_Spanish_clause()
	{
		var name = Unique("excl");
		PublishClause(name, "<p>Exclusion</p>");
		PublishClause(name, "<p>Exclusi\u00f3n</p>", "es");
		var page = (await Composer.ComposeTemplatePageAsync(Spanish + $"{{% include '{name}' %}}<p>{{{{ policy.number }}}}</p>", "")).Html!;
		Assert.StartsWith("<!DOCTYPE html><html lang=\"es\">", page);
		Assert.Contains("<p>Exclusi\u00f3n</p>", page);
		Assert.Contains("{{ policy.number }}", page);
	}

	[Fact]
	public async Task The_standard_footer_is_in_the_document_language()
	{
		var renderer = factory.Services.GetRequiredService<PdfRenderer>();
		var spanish = (await Composer.ComposeAsync(Spanish + "<p>Hola</p>", "", Data)).Html!;
		var english = (await Composer.ComposeAsync("<p>Hello</p>", "", Data)).Html!;
		var spanishText = string.Join(' ', PdfText.Extract(await renderer.RenderAsync(spanish)).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
		var englishText = string.Join(' ', PdfText.Extract(await renderer.RenderAsync(english)).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
		Assert.Contains("P\u00e1gina 1 de 1", spanishText);
		Assert.DoesNotContain("Page 1 of 1", spanishText);
		Assert.Contains("Page 1 of 1", englishText);
	}
}
