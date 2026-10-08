using System.Globalization;
using System.Text.Json;
using FaCT.DocDesigner.POC.Rendering;
using FaCT.DocDesigner.POC.Templates;
using FaCT.DocDesigner.POC.Tests.Clauses;

namespace FaCT.DocDesigner.POC.Tests.Languages;

/// <summary>Language markers, language stores and clause translations (no web host).</summary>
public sealed class DocumentLanguageTests : IDisposable
{
	private static readonly JsonElement EmptyProject = JsonDocument.Parse("{}").RootElement.Clone();
	private readonly TempStores _stores = new();

	public void Dispose() => _stores.Dispose();

	private static DocumentLanguage Spanish => DocumentLanguages.Find("es")!;

	// ---- languages and markers ---------------------------------------------------------------------------------------

	[Fact]
	public void English_Spanish_and_French_are_the_document_languages()
	{
		Assert.Equal(["en", "es", "fr"], DocumentLanguages.All.Select(l => l.Code));
		Assert.Equal(["en-US", "es-US", "fr-CA"], DocumentLanguages.All.Select(l => l.Culture));
		// every culture is one the format filter accepts
		Assert.All(DocumentLanguages.All, l => Assert.Null(ValueFormats.CultureProblem(l.Culture)));
	}

	[Theory]
	[InlineData(null, "en")]
	[InlineData("", "en")]
	[InlineData("en", "en")]
	[InlineData("es", "es")]
	[InlineData("fr", "fr")]
	public void Find_knows_the_codes_and_treats_no_code_as_English(string? code, string expected) =>
		Assert.Equal(expected, DocumentLanguages.Find(code)!.Code);

	[Theory]
	[InlineData("de")]
	[InlineData("ES")]
	[InlineData("es-US")]
	[InlineData("../x")]
	public void Find_rejects_other_codes(string code)
	{
		Assert.Null(DocumentLanguages.Find(code));
		Assert.Contains("es = Spanish", DocumentLanguages.UnknownLanguage(code));
	}

	[Fact]
	public void Of_reads_the_marker_and_defaults_to_English()
	{
		Assert.Equal("en", DocumentLanguages.Of("<p>Hello</p>").Code);
		Assert.Equal("en", DocumentLanguages.Of(null).Code);
		Assert.Equal("es", DocumentLanguages.Of("<body><div class=\"doc-language lang-es\"></div><p>Hola</p></body>").Code);
		Assert.Equal("fr", DocumentLanguages.Of("<div class=\"doc-language lang-fr\"></div>").Code);
		// an unknown marker is English
		Assert.Equal("en", DocumentLanguages.Of("<div class=\"doc-language lang-de\"></div>").Code);
	}

	[Fact]
	public void WithMarker_puts_the_marker_first_in_the_body()
	{
		var html = DocumentLanguages.WithMarker("<body id=\"b\"><p>Hola</p></body>", Spanish);
		Assert.Equal("<body id=\"b\"><div class=\"doc-language lang-es\"></div><p>Hola</p></body>", html);
	}

	[Fact]
	public void WithMarker_replaces_any_other_marker()
	{
		var html = DocumentLanguages.WithMarker("<body><div class=\"doc-language lang-fr\"></div><p>x</p><div id=\"i\" class=\"doc-language lang-es\"></div></body>", Spanish);
		Assert.Equal("<body><div class=\"doc-language lang-es\"></div><p>x</p></body>", html);
		Assert.Equal("es", DocumentLanguages.Of(html).Code);
	}

	[Fact]
	public void WithMarker_for_English_removes_the_marker()
	{
		Assert.Equal("<body><p>x</p></body>",
			DocumentLanguages.WithMarker("<body><div class=\"doc-language lang-es\"></div><p>x</p></body>", DocumentLanguages.English));
	}

	[Fact]
	public void WithMarker_prepends_to_a_fragment_without_a_body()
	{
		Assert.Equal("<div class=\"doc-language lang-es\"></div><p>x</p>", DocumentLanguages.WithMarker("<p>x</p>", Spanish));
		Assert.Equal("<p>x</p>", DocumentLanguages.WithoutMarker("<div class=\"doc-language lang-es\"></div><p>x</p>"));
	}

	[Theory]
	[InlineData("en-US", "Yes", "No")]
	[InlineData("es-US", "S\u00ed", "No")]
	[InlineData("es-MX", "S\u00ed", "No")]
	[InlineData("fr-CA", "Oui", "Non")]
	[InlineData("de-DE", "Yes", "No")]
	public void Yes_and_no_print_in_the_culture_language(string culture, string yes, string no)
	{
		Assert.Equal(yes, YesNoValue.Word(true, CultureInfo.GetCultureInfo(culture)));
		Assert.Equal(no, YesNoValue.Word(false, CultureInfo.GetCultureInfo(culture)));
	}

	// ---- language stores ----------------------------------------------------------------------------------------------

	[Fact]
	public async Task Each_language_has_its_own_versions_of_the_same_name()
	{
		var templates = _stores.Templates;
		await templates.SaveDraftAsync("notice", EmptyProject, "<p>Notice</p>", "", null);
		await templates.PublishAsync("notice", 1);
		await templates.SaveDraftAsync("notice", EmptyProject, "<p>Notice 2</p>", "", null);
		await templates.Language("es").SaveDraftAsync("notice", EmptyProject, "<p>Aviso</p>", "", null);

		Assert.Equal([1, 2], (await templates.ListVersionsAsync("notice")).Select(v => v.Version));
		Assert.Equal([1], (await templates.Language("es").ListVersionsAsync("notice")).Select(v => v.Version));
		Assert.Equal("<p>Aviso</p>", (await templates.Language("es").GetAsync("notice"))!.Html);
		Assert.Null(await templates.Language("es").GetPublishedAsync("notice"));
		Assert.Empty(await templates.Language("fr").ListVersionsAsync("notice"));
		// translations aren't separate templates
		Assert.Equal(["notice"], (await templates.ListAsync()).Select(t => t.Name));
		Assert.True(File.Exists(Path.Combine(_stores.TemplatesRoot, "_lang", "es", "notice.json")));
	}

	[Fact]
	public void English_is_the_store_itself_and_stores_are_reused()
	{
		var templates = _stores.Templates;
		Assert.Same(templates, templates.Language(null));
		Assert.Same(templates, templates.Language(""));
		Assert.Same(templates, templates.Language("en"));
		Assert.Same(templates.Language("es"), templates.Language("es"));
		Assert.NotSame(templates.Language("es"), templates.Language("fr"));
		Assert.Throws<ArgumentException>(() => templates.Language("de"));
	}

	[Fact]
	public async Task Exists_says_whether_a_store_has_the_name()
	{
		Assert.False(_stores.Templates.Exists("notice"));
		await _stores.Templates.SaveDraftAsync("notice", EmptyProject, "<p>x</p>", "", null);
		Assert.True(_stores.Templates.Exists("notice"));
		Assert.False(_stores.Templates.Language("es").Exists("notice"));
		Assert.False(_stores.Templates.Exists("../notice"));
	}

	// ---- clause translations ---------------------------------------------------------------------------------------

	private int PublishSpanishClause(string name, string html, string css = "")
	{
		var store = _stores.Clauses.Versions.Language("es");
		var version = store.SaveDraftAsync(name, EmptyProject, html, css, null).GetAwaiter().GetResult().Version;
		store.PublishAsync(name, version).GetAwaiter().GetResult();
		return version;
	}

	[Fact]
	public void A_Spanish_document_uses_the_Spanish_clause_when_it_is_published()
	{
		_stores.Publish("excl", "<p>Exclusion</p>");
		PublishSpanishClause("excl", "<p>Exclusi\u00f3n</p>");
		var reference = new ClauseReference("excl", null);

		Assert.Equal("<p>Exclusion</p>", _stores.Clauses.Resolve(reference)!.Html);
		var spanish = _stores.Clauses.ResolveIn(reference, Spanish);
		Assert.Equal("<p>Exclusi\u00f3n</p>", spanish.Version!.Html);
		Assert.Equal("es", spanish.Language.Code);
		// French has no translation: English
		var french = _stores.Clauses.ResolveIn(reference, DocumentLanguages.Find("fr"));
		Assert.Equal("<p>Exclusion</p>", french.Version!.Html);
		Assert.Equal("en", french.Language.Code);
	}

	[Fact]
	public void A_Spanish_draft_clause_is_not_printed_the_English_one_is()
	{
		_stores.Publish("excl", "<p>Exclusion</p>");
		_stores.Clauses.Versions.Language("es").SaveDraftAsync("excl", EmptyProject, "<p>Borrador</p>", "", null).GetAwaiter().GetResult();
		var resolved = _stores.Clauses.ResolveIn(new ClauseReference("excl", null), Spanish);
		Assert.Equal("<p>Exclusion</p>", resolved.Version!.Html);
		Assert.Equal("en", resolved.Language.Code);
	}

	[Fact]
	public void A_pinned_version_is_the_Spanish_one_when_Spanish_has_it()
	{
		_stores.Publish("excl", "<p>One</p>");
		_stores.Publish("excl", "<p>Two</p>");
		PublishSpanishClause("excl", "<p>Uno</p>");
		Assert.Equal("<p>Uno</p>", _stores.Clauses.Resolve(new ClauseReference("excl", 1), Spanish)!.Html);
		// Spanish has no v2: the English v2
		Assert.Equal("<p>Two</p>", _stores.Clauses.Resolve(new ClauseReference("excl", 2), Spanish)!.Html);
	}

	[Fact]
	public void Used_and_CssFor_follow_the_document_language()
	{
		_stores.Publish("outer", "<div>{% include 'inner' %}</div>", ".outer-en{}");
		_stores.Publish("inner", "<p>inner</p>", ".inner-en{}");
		PublishSpanishClause("outer", "<div>{% include 'inner' %}</div>", ".outer-es{}");

		var used = _stores.Clauses.Used("{% include 'outer' %}", Spanish);
		Assert.Equal(["outer:es", "inner:en"], used.Select(u => u.Reference.Name + ":" + u.Language.Code));
		var css = _stores.Clauses.CssFor("{% include 'outer' %}", Spanish);
		Assert.Contains(".outer-es{}", css);
		Assert.Contains(".inner-en{}", css);
		Assert.DoesNotContain(".outer-en{}", css);
		Assert.Contains(".outer-en{}", _stores.Clauses.CssFor("{% include 'outer' %}"));
	}

	[Fact]
	public void The_Spanish_file_provider_serves_the_Spanish_clause()
	{
		_stores.Publish("excl", "<p>Exclusion</p>");
		PublishSpanishClause("excl", "<p>Exclusi\u00f3n</p>");
		using var reader = new StreamReader(_stores.Clauses.FileProviderFor(Spanish).GetFileInfo("excl.liquid").CreateReadStream());
		Assert.Equal("<p>Exclusi\u00f3n</p>", reader.ReadToEnd());
		Assert.Same(_stores.Clauses.FileProvider, _stores.Clauses.FileProviderFor(DocumentLanguages.English));
	}
}
