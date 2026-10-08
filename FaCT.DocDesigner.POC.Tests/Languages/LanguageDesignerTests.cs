using System.Net.Http.Json;
using System.Text.Json;
using PuppeteerSharp;

namespace FaCT.DocDesigner.POC.Tests.Languages;

/// <summary>Translating a template in the designer: the Language selector, saving and publishing per language, the Languages dialog.</summary>
[Collection("Isolated designer")]
public sealed class LanguageDesignerTests(IsolatedDesignerFixture fixture)
{
	private const string Editor = "grapesjs.editors[0]";

	private IPage Page => fixture.Page;

	private static string Unique(string name) => name + "-" + Guid.NewGuid().ToString("N")[..8];

	private Task<T> EvalAsync<T>(string script) => Page.EvaluateExpressionAsync<T>(script);

	private Task WaitAsync(string predicate, params object[] args) =>
		Page.WaitForFunctionAsync(predicate, new WaitForFunctionOptions { Timeout = 30_000 }, args);

	private async Task<string> ActAsync(string script)
	{
		await Page.EvaluateExpressionAsync("document.getElementById('status').textContent = ''");
		await Page.EvaluateExpressionAsync(script);
		await WaitAsync("() => { const t = document.getElementById('status').textContent; return t.length > 0 && !t.endsWith('...'); }");
		return await EvalAsync<string>("document.getElementById('status').textContent");
	}

	private Task<string> SetNameAsync(string name) =>
		ActAsync($"(() => {{ {Editor}.Modal.close(); const k = document.getElementById('docKind'); if (k.value !== 'templates') {{ k.value = 'templates'; k.dispatchEvent(new Event('change')); }} " +
			$"const n = document.getElementById('templateName'); n.value = '{name}'; n.dispatchEvent(new Event('change')); }})()");

	private async Task SetModelAsync(string json)
	{
		await Page.EvaluateExpressionAsync("document.getElementById('btnEditModel').click()");
		await WaitAsync("() => !!document.querySelector('.gjs-mdl-content textarea.model-editor')");
		await Page.EvaluateFunctionAsync("m => document.querySelector('.gjs-mdl-content textarea.model-editor').value = m", json);
		await ActAsync("document.querySelector('.gjs-mdl-content .model-actions button.primary').click()");
	}

	private Task<string> SwitchAsync(string code) =>
		ActAsync($"(() => {{ const s = document.getElementById('docLanguage'); s.value = '{code}'; s.dispatchEvent(new Event('change')); }})()");

	private Task<string> SaveAsync() => ActAsync("document.getElementById('btnSave').click()");

	private Task<string> FieldTextAsync() =>
		EvalAsync<string>($"{Editor}.Canvas.getDocument().querySelector('.df').textContent");

	private Task<string> ParagraphTextAsync() =>
		EvalAsync<string>($"{Editor}.Canvas.getDocument().getElementById('t').textContent");

	private Task TranslateAsync(string words) =>
		Page.EvaluateFunctionAsync("w => { const p = grapesjs.editors[0].getWrapper().find('#t')[0]; p.components(w + ' <span class=\"df\" data-field=\"policy.renewal\"></span>'); }", words);

	private async Task<string> StartEnglishAsync()
	{
		var name = Unique("notice");
		await SetNameAsync(name);
		await SetModelAsync("""{ "policy": { "number": "CPP1", "renewal": true }, "insured": { "name": "Acme" } }""");
		await Page.EvaluateFunctionAsync("h => { const e = grapesjs.editors[0]; e.select(null); e.setComponents(h); e.setStyle(''); }",
			"<p id=\"t\">Renewal <span class=\"df\" data-field=\"policy.renewal\"></span></p>");
		Assert.Equal("Saved draft v1.", await SaveAsync());
		return name;
	}

	[Fact]
	public async Task The_toolbar_has_a_language_selector_and_the_menu_a_languages_item()
	{
		Assert.Equal(["", "es", "fr"], await EvalAsync<string[]>("[...document.getElementById('docLanguage').options].map(o => o.value)"));
		Assert.Equal(["English", "Spanish", "French (Canada)"], await EvalAsync<string[]>("[...document.getElementById('docLanguage').options].map(o => o.textContent)"));
		Assert.Equal("Languages…", await EvalAsync<string>("document.getElementById('btnLanguages').textContent.trim()"));
	}

	[Fact]
	public async Task Translating_into_Spanish_keeps_the_fields_and_saves_Spanish_v1()
	{
		var name = await StartEnglishAsync();
		Assert.Equal("Yes", await FieldTextAsync());

		var status = await SwitchAsync("es");
		Assert.StartsWith("No Spanish version yet: the canvas keeps the current text.", status);
		// the canvas is marked Spanish and the field sample prints in Spanish
		Assert.Equal("es", await EvalAsync<string>($"{Editor}.getWrapper().findType('language-ref')[0].get('language')"));
		Assert.Contains("class=\"doc-language lang-es\"", await EvalAsync<string>($"{Editor}.getHtml()"));
		await WaitAsync($"() => {Editor}.Canvas.getDocument().querySelector('.df').textContent === 'S\u00ed'");

		await TranslateAsync("Renovaci\u00f3n");
		Assert.Equal("Saved draft Spanish v1. Same data fields as English.", await SaveAsync());
		Assert.Equal("v1 Draft \u00b7 ES", await EvalAsync<string>("document.getElementById('versionBadge').textContent"));

		var spanish = await fixture.Http.GetFromJsonAsync<JsonElement>($"api/templates/{name}/versions/1?lang=es");
		Assert.Contains("Renovaci\u00f3n", spanish.GetProperty("html").GetString());
		Assert.Contains("class=\"doc-language lang-es\"", spanish.GetProperty("html").GetString());
		// English is untouched
		var english = await fixture.Http.GetFromJsonAsync<JsonElement>($"api/templates/{name}/versions/1");
		Assert.DoesNotContain("doc-language", english.GetProperty("html").GetString());
		Assert.Contains("Renewal", english.GetProperty("html").GetString());
	}

	[Fact]
	public async Task Switching_language_opens_that_languages_newest_version()
	{
		var name = await StartEnglishAsync();
		await SwitchAsync("es");
		await TranslateAsync("Renovaci\u00f3n");
		await SaveAsync();

		Assert.Equal("Opened English v1 (Draft).", await SwitchAsync(""));
		Assert.Equal("Renewal Yes", await ParagraphTextAsync());
		Assert.Equal(0, await EvalAsync<int>($"{Editor}.getWrapper().findType('language-ref').length"));
		Assert.Equal("v1 Draft", await EvalAsync<string>("document.getElementById('versionBadge').textContent"));

		Assert.Equal("Opened Spanish v1 (Draft).", await SwitchAsync("es"));
		Assert.Equal("Renovaci\u00f3n S\u00ed", await ParagraphTextAsync());
		Assert.Equal(1, await EvalAsync<int>($"{Editor}.getWrapper().findType('language-ref').length"));
		// a new name starts in English
		await SetNameAsync(Unique("other"));
		Assert.Equal("", await EvalAsync<string>("document.getElementById('docLanguage').value"));
		Assert.Contains(name, name);
	}

	[Fact]
	public async Task The_Spanish_preview_prints_in_Spanish()
	{
		await StartEnglishAsync();
		await SwitchAsync("es");
		await TranslateAsync("Renovaci\u00f3n");
		var html = await EvalAsync<string>($"{Editor}.getHtml()");
		var css = await EvalAsync<string>($"{Editor}.getCss()");
		var response = await fixture.Http.PostAsJsonAsync("api/render", new { html, css, data = new { policy = new { renewal = true } } });
		Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
		var text = string.Join(' ', PdfText.Extract(await response.Content.ReadAsByteArrayAsync()).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
		Assert.Contains("Renovaci\u00f3n S\u00ed", text);
		Assert.Contains("P\u00e1gina 1 de 1", text);
	}

	[Fact]
	public async Task Publishing_in_Spanish_publishes_the_Spanish_version()
	{
		var name = await StartEnglishAsync();
		await SwitchAsync("es");
		await TranslateAsync("Renovaci\u00f3n");
		await SaveAsync();
		Assert.Equal("Spanish v1 is now published.", await ActAsync("document.getElementById('btnPublish').click()"));

		Assert.Equal(1, (await fixture.Http.GetFromJsonAsync<JsonElement>($"api/templates/{name}/versions?lang=es"))[0].GetProperty("version").GetInt32());
		Assert.Equal("Published", (await fixture.Http.GetFromJsonAsync<JsonElement>($"api/templates/{name}/versions?lang=es"))[0].GetProperty("status").GetString());
		Assert.Equal("Draft", (await fixture.Http.GetFromJsonAsync<JsonElement>($"api/templates/{name}/versions"))[0].GetProperty("status").GetString());
		var pdf = await fixture.Http.PostAsync($"api/templates/{name}/pdf?lang=es", null);
		Assert.Equal("es", pdf.Headers.GetValues("X-Template-Language").Single());
	}

	[Fact]
	public async Task The_languages_dialog_shows_versions_and_the_field_check()
	{
		var name = await StartEnglishAsync();
		await SwitchAsync("es");
		// the translation drops the field
		await Page.EvaluateExpressionAsync($"{Editor}.getWrapper().find('#t')[0].components('Renovaci\u00f3n')");
		Assert.Equal("Saved draft Spanish v1. Missing 1 field(s) the English version prints: policy.renewal.", await SaveAsync());

		await ActAsync("document.getElementById('btnLanguages').click()");
		await WaitAsync("() => document.querySelectorAll('.languages .language-row').length === 3");
		Assert.Equal("Languages of " + name, await EvalAsync<string>("document.querySelector('.gjs-mdl-title').textContent"));
		var rows = await EvalAsync<string[]>(
			"[...document.querySelectorAll('.languages .language-row')].map(r => [...r.cells].slice(0, 4).map(c => c.textContent).join('|'))");
		Assert.Equal([
			"English|v1|\u2014|The data fields translations use.",
			"Spanish (open)|v1|\u2014|Missing 1 field(s) the English version prints: policy.renewal.",
			"French (Canada)|\u2014|\u2014|Not started."
		], rows);
		Assert.Equal(["Open", "Start"], await EvalAsync<string[]>("[...document.querySelectorAll('.languages .language-open')].map(b => b.textContent)"));

		Assert.Equal("Opened English v1 (Draft).", await ActAsync("document.querySelector('.languages tr[data-language=\"en\"] .language-open').click()"));
		Assert.Equal("", await EvalAsync<string>("document.getElementById('docLanguage').value"));
	}

	[Fact]
	public async Task A_Spanish_template_shows_the_Spanish_clause_on_the_canvas()
	{
		var clause = Unique("excl");
		foreach (var (lang, html) in new[] { ("", "<p>Exclusion</p>"), ("es", "<p>Exclusi\u00f3n</p>") })
		{
			var query = lang.Length == 0 ? "" : "?lang=" + lang;
			(await fixture.Http.PutAsJsonAsync($"api/clauses/{clause}/draft{query}", new { project = new { }, html, css = "" })).EnsureSuccessStatusCode();
			(await fixture.Http.PostAsync($"api/clauses/{clause}/versions/1/publish{query}", null)).EnsureSuccessStatusCode();
		}
		await StartEnglishAsync();
		await Page.EvaluateFunctionAsync("c => grapesjs.editors[0].getWrapper().append({ type: 'clause', clause: c })", clause);
		await WaitAsync($"() => {Editor}.Canvas.getDocument().body.textContent.includes('Exclusion')");
		await SwitchAsync("es");
		await WaitAsync($"() => {Editor}.Canvas.getDocument().body.textContent.includes('Exclusi\u00f3n')");
	}

	[Fact]
	public async Task Field_usage_opens_a_Spanish_hit_in_Spanish()
	{
		var name = await StartEnglishAsync();
		await SwitchAsync("es");
		await TranslateAsync("Renovaci\u00f3n");
		await SaveAsync();
		await SwitchAsync("");

		await Page.EvaluateExpressionAsync("document.getElementById('btnFieldUsage').click()");
		await WaitAsync("() => !!document.getElementById('usagePath')");
		await Page.EvaluateExpressionAsync("document.getElementById('usagePath').value = 'policy.renewal'");
		await ActAsync("document.getElementById('usageSearch').click()");
		await WaitAsync($"() => !!document.querySelector('.usage-row[data-doc=\"templates/{name}/1/es\"]')");
		Assert.Equal(name + " (Spanish)", await EvalAsync<string>($"document.querySelector('.usage-row[data-doc=\"templates/{name}/1/es\"]').cells[1].textContent"));

		var status = await ActAsync($"document.querySelector('.usage-row[data-doc=\"templates/{name}/1/es\"] .usage-open').click()");
		Assert.Equal($"Opened {name} (Spanish) v1 (Draft).", status);
		Assert.Equal("es", await EvalAsync<string>("document.getElementById('docLanguage').value"));
		Assert.Equal("Renovaci\u00f3n S\u00ed", await ParagraphTextAsync());
	}

	[Fact]
	public async Task The_gallery_says_which_languages_a_template_is_also_in()
	{
		var name = await StartEnglishAsync();
		await SwitchAsync("es");
		await SaveAsync();
		await Page.EvaluateExpressionAsync("document.getElementById('btnGallery').click()");
		await WaitAsync($"() => !!document.querySelector('.gallery-card[data-doc=\"templates/{name}\"]')");
		Assert.EndsWith("\u00b7 also in Spanish", await EvalAsync<string>($"document.querySelector('.gallery-card[data-doc=\"templates/{name}\"] .gallery-meta').textContent"));
		await Page.EvaluateExpressionAsync($"{Editor}.Modal.close()");
	}
}
