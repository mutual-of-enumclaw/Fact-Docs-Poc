using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PuppeteerSharp;

namespace FaCT.DocDesigner.POC.Tests.Clauses;

/// <summary>Shared clauses in the designer: Clause mode, the Clause block, canvas previews and the exported include.</summary>
[Collection("Isolated designer")]
public sealed class ClauseDesignerTests(IsolatedDesignerFixture fixture)
{
	private IPage Page => fixture.Page;

	private static string Unique(string name) => name + "-" + Guid.NewGuid().ToString("N")[..8];

	private Task<T> EvalAsync<T>(string script) => Page.EvaluateExpressionAsync<T>(script);

	private Task WaitAsync(string predicate, params object[] args) =>
		Page.WaitForFunctionAsync(predicate, new WaitForFunctionOptions { Timeout = 30_000 }, args);

	private Task<string> StatusAsync() => EvalAsync<string>("document.getElementById('status').textContent");

	/// <summary>Runs a toolbar action and returns the status message it ends with.</summary>
	private async Task<string> ActAsync(string script)
	{
		await Page.EvaluateExpressionAsync("document.getElementById('status').textContent = ''");
		await Page.EvaluateExpressionAsync(script);
		await WaitAsync("() => { const t = document.getElementById('status').textContent; return t.length > 0 && !t.endsWith('...'); }");
		return await StatusAsync();
	}

	private Task<string> ClickAsync(string id) => ActAsync($"document.getElementById('{id}').click()");

	private Task<string> SetKindAsync(string kind) => ActAsync(
		$"(() => {{ const k = document.getElementById('docKind'); k.value = '{kind}'; k.dispatchEvent(new Event('change')); }})()");

	private Task<string> SetNameAsync(string name) => ActAsync(
		$"(() => {{ const n = document.getElementById('templateName'); n.value = '{name}'; n.dispatchEvent(new Event('change')); }})()");

	/// <summary>A fresh template canvas holding just the given HTML.</summary>
	private async Task NewTemplateAsync(string html = "<p>Start</p>")
	{
		await SetKindAsync("templates");
		await SetNameAsync(Unique("tpl"));
		await Page.EvaluateFunctionAsync("html => { const e = grapesjs.editors[0]; e.setComponents(html); e.setStyle(''); }", html);
	}

	/// <summary>Drops the Clause block's content onto the canvas, selects it and picks <paramref name="clause"/> in its settings.</summary>
	private async Task InsertClauseAsync(string clause)
	{
		await Page.EvaluateExpressionAsync(
			"(() => { const e = grapesjs.editors[0]; const added = e.getWrapper().append(e.Blocks.get('clause').get('content'))[0]; e.select(added); })()");
		await WaitAsync("name => [...document.querySelectorAll('select.clause-picker option')].some(o => o.value === name)", clause);
		await Page.EvaluateFunctionAsync(
			"name => { const s = document.querySelector('select.clause-picker'); s.value = name; s.dispatchEvent(new Event('change')); }", clause);
	}

	private const string FirstClause = "grapesjs.editors[0].getWrapper().findType('clause')[0]";

	private Task WaitForCanvasTextAsync(string text) =>
		WaitAsync($"t => {{ const c = {FirstClause}; return !!c && !!c.view && c.view.el.textContent.includes(t); }}", text);

	private Task<string> HtmlAsync() => EvalAsync<string>("grapesjs.editors[0].getHtml()");

	[Fact]
	public async Task Clause_mode_saves_and_publishes_a_clause_fragment()
	{
		var name = Unique("std-exclusion");
		await SetKindAsync("clauses");
		Assert.Equal("Clause", await EvalAsync<string>("document.getElementById('templateNameLabel').textContent"));
		Assert.True(await EvalAsync<bool>("document.getElementById('btnRenderPublished').disabled"));
		Assert.Equal("New clause. Save Draft to create v1.", await SetNameAsync(name));

		await Page.EvaluateExpressionAsync(
			"(() => { const e = grapesjs.editors[0]; e.setComponents('<p class=\"std-x\">Standard exclusion wording</p>'); e.setStyle('.std-x{color:#0e7490;}'); })()");
		var status = await ClickAsync("btnPublish");
		Assert.StartsWith($"Clause {name} v1 is now published.", status);
		Assert.StartsWith("v1 Published", await EvalAsync<string>("document.getElementById('versionBadge').textContent"));

		var saved = await fixture.Http.GetFromJsonAsync<JsonElement>($"api/clauses/{name}/versions/1");
		var html = saved.GetProperty("html").GetString()!;
		var css = saved.GetProperty("css").GetString()!;
		Assert.Contains("Standard exclusion wording", html);
		Assert.DoesNotContain("<body", html, StringComparison.OrdinalIgnoreCase);
		Assert.True(Regex.IsMatch(css, @"\.std-x\s*\{\s*color:\s*(#0e7490|rgb\(14, 116, 144\))"), "css: " + css);
		Assert.DoesNotContain("box-sizing", css);
		Assert.Equal("Published", saved.GetProperty("status").GetString());
		Assert.Empty(await fixture.Http.GetFromJsonAsync<JsonElement[]>($"api/templates/{name}/versions") ?? []);

		// Back to templates: the toolbar is the template one again.
		await SetKindAsync("templates");
		Assert.Equal("Template", await EvalAsync<string>("document.getElementById('templateNameLabel').textContent"));
		Assert.False(await EvalAsync<bool>("document.getElementById('btnRenderPublished').disabled"));
	}

	[Fact]
	public async Task A_clause_cannot_be_saved_including_itself()
	{
		var name = Unique("self");
		await fixture.PublishClauseAsync(name, "<p>v1</p>");
		await SetKindAsync("clauses");
		await SetNameAsync(name);
		await Page.EvaluateFunctionAsync(
			"name => grapesjs.editors[0].setComponents('<p>x</p><div class=\"clause\" data-clause=\"' + name + '\"></div>')", name);
		var status = await ClickAsync("btnSave");
		Assert.Contains("can't include itself", status);
		Assert.True(await EvalAsync<bool>("document.getElementById('status').classList.contains('error')"));
		await SetKindAsync("templates");
	}

	[Fact]
	public async Task The_clause_block_is_in_the_data_category()
	{
		Assert.Equal("Clause", await EvalAsync<string>("grapesjs.editors[0].Blocks.get('clause').get('label')"));
		Assert.Equal("Data", await EvalAsync<string>(
			"(() => { const c = grapesjs.editors[0].Blocks.get('clause').get('category'); return typeof c === 'string' ? c : c.get('label') || c.get('id'); })()"));
	}

	[Fact]
	public async Task Inserting_a_clause_previews_it_and_exports_an_include()
	{
		var name = Unique("std");
		await fixture.PublishClauseAsync(name, "<p>Exclusion wording for {{ policy.number }}</p>");
		await NewTemplateAsync("<h1>Declarations</h1>");
		await InsertClauseAsync(name);
		await WaitForCanvasTextAsync("Exclusion wording for");

		var html = await HtmlAsync();
		Assert.Contains($"data-clause=\"{name}\"", html);
		Assert.Contains($"{{% include '{name}' %}}", html);
		Assert.DoesNotContain("data-version", html);
		// The preview is not exported: only the include.
		Assert.DoesNotContain("Exclusion wording", html);
		Assert.Equal(1, Regex.Matches(html, "include '").Count);
		Assert.Equal(name, await EvalAsync<string>($"{FirstClause}.view.el.getAttribute('data-clause-ref')"));
		Assert.True(await EvalAsync<int>("grapesjs.editors[0].getDirtyCount()") > 0);

		// What the preview / docgen renders.
		using var response = await fixture.Http.PostAsJsonAsync("api/render", new JsonObject
		{
			["html"] = html,
			["css"] = await EvalAsync<string>("grapesjs.editors[0].getCss()"),
			["data"] = new JsonObject { ["policy"] = new JsonObject { ["number"] = "CPP5550001" } }
		});
		Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
		var text = Regex.Replace(PdfText.Extract(await response.Content.ReadAsByteArrayAsync()), @"\s+", " ");
		Assert.Contains("Declarations", text);
		Assert.Contains("Exclusion wording for CPP5550001", text);
	}

	[Fact]
	public async Task Pinning_a_version_changes_the_include_and_the_preview()
	{
		var name = Unique("pin");
		await fixture.PublishClauseAsync(name, "<p>Wording one</p>");
		await fixture.PublishClauseAsync(name, "<p>Wording two</p>");
		await NewTemplateAsync();
		await InsertClauseAsync(name);
		await WaitForCanvasTextAsync("Wording two");

		await Page.EvaluateExpressionAsync($"{FirstClause}.set('pinned', '1')");
		await WaitForCanvasTextAsync("Wording one");
		var html = await HtmlAsync();
		Assert.Contains($"{{% include '{name}@1' %}}", html);
		Assert.Contains("data-version=\"1\"", html);
		Assert.Contains("(pinned)", await EvalAsync<string>($"{FirstClause}.view.el.getAttribute('title')"));

		// Not a version number: the latest published version.
		await Page.EvaluateExpressionAsync($"{FirstClause}.set('pinned', 'abc')");
		await WaitForCanvasTextAsync("Wording two");
		html = await HtmlAsync();
		Assert.Contains($"{{% include '{name}' %}}", html);
		Assert.DoesNotContain("data-version", html);

		// A version that doesn't exist says so on the canvas.
		await Page.EvaluateExpressionAsync($"{FirstClause}.set('pinned', '9')");
		await WaitForCanvasTextAsync("has no version 9");
	}

	[Fact]
	public async Task A_clause_that_is_not_published_shows_why_on_the_canvas()
	{
		var draft = Unique("draft");
		await fixture.SaveClauseAsync(draft, "<p>Draft only</p>");
		await NewTemplateAsync($"<div class=\"clause\" data-clause=\"{draft}\"></div>");
		await WaitForCanvasTextAsync("has no published version");
		Assert.DoesNotContain("Draft only", await EvalAsync<string>($"{FirstClause}.view.el.textContent"));
	}

	[Fact]
	public async Task The_picker_offers_published_clauses_only()
	{
		var published = Unique("pub");
		var draft = Unique("draft");
		await fixture.PublishClauseAsync(published, "<p>p</p>");
		await fixture.SaveClauseAsync(draft, "<p>d</p>");
		await NewTemplateAsync();
		await InsertClauseAsync(published);
		var options = await EvalAsync<string[]>("[...document.querySelector('select.clause-picker').options].map(o => o.value)");
		Assert.Contains(published, options);
		Assert.DoesNotContain(draft, options);
		Assert.Equal(published, await EvalAsync<string>("document.querySelector('select.clause-picker').value"));
	}

	[Fact]
	public async Task Saved_clause_markup_reopens_as_a_clause_component()
	{
		var name = Unique("round");
		await fixture.PublishClauseAsync(name, "<p>Round one</p>");
		await fixture.PublishClauseAsync(name, "<p>Round two</p>");
		await NewTemplateAsync($"<p>Before</p><div class=\"clause\" data-clause=\"{name}\" data-version=\"2\">{{% include '{name}@2' %}}</div><p>After</p>");
		await WaitForCanvasTextAsync("Round two");

		Assert.Equal(1, await EvalAsync<int>("grapesjs.editors[0].getWrapper().findType('clause').length"));
		Assert.Equal(name, await EvalAsync<string>($"{FirstClause}.get('clause')"));
		Assert.Equal("2", await EvalAsync<string>($"{FirstClause}.get('pinned')"));
		Assert.Equal(0, await EvalAsync<int>($"{FirstClause}.components().length"));
		var html = await HtmlAsync();
		Assert.Equal(1, Regex.Matches(html, Regex.Escape($"{{% include '{name}@2' %}}")).Count);
		Assert.True(html.IndexOf("Before", StringComparison.Ordinal) < html.IndexOf("include", StringComparison.Ordinal));
		Assert.True(html.IndexOf("include", StringComparison.Ordinal) < html.IndexOf("After", StringComparison.Ordinal));
	}

	[Fact]
	public async Task Clause_markup_with_a_bad_name_exports_nothing()
	{
		await NewTemplateAsync("<div class=\"clause\" data-clause=\"../etc\"></div><div class=\"clause\" data-clause=\"x' %}{{ 1 }}\"></div>");
		var html = await HtmlAsync();
		Assert.DoesNotContain("include", html);
		Assert.DoesNotContain("{{ 1 }}", html);
	}

	[Fact]
	public async Task The_canvas_preview_is_inert_and_brings_the_clause_css()
	{
		var name = Unique("css");
		await fixture.PublishClauseAsync(name,
			"<p class=\"cx\" onclick=\"window.parent.__clicked = 1\">Safe text</p><script>window.parent.__pwned = 1</script>" +
			"<img src=\"x\" onerror=\"window.parent.__pwned = 2\"><a href=\"javascript:window.parent.__pwned = 3\">link</a>",
			".cx{color:rgb(1, 2, 3);}");
		await NewTemplateAsync();
		await InsertClauseAsync(name);
		await WaitForCanvasTextAsync("Safe text");
		await Task.Delay(300);

		Assert.True(await EvalAsync<bool>($"{FirstClause}.view.el.querySelector('script') === null"));
		Assert.True(await EvalAsync<bool>($"!{FirstClause}.view.el.querySelector('[onclick],[onerror]')"));
		Assert.True(await EvalAsync<bool>($"!{FirstClause}.view.el.querySelector('a').hasAttribute('href')"));
		Assert.True(await EvalAsync<bool>("window.__pwned === undefined"));
		Assert.Equal(1, await EvalAsync<int>($"grapesjs.editors[0].Canvas.getDocument().querySelectorAll('style[data-clause-css=\"{name}@1\"]').length"));
		Assert.Equal("rgb(1, 2, 3)", await EvalAsync<string>(
			$"grapesjs.editors[0].Canvas.getWindow().getComputedStyle({FirstClause}.view.el.querySelector('.cx')).color"));
	}

	[Fact]
	public async Task Publishing_a_clause_in_the_designer_refreshes_previews_of_it()
	{
		var name = Unique("live");
		await fixture.PublishClauseAsync(name, "<p>Old wording</p>");
		// Edit the clause in Clause mode, with a template-style clause preview of another clause on the canvas.
		var other = Unique("other");
		await fixture.PublishClauseAsync(other, "<p>Other v1</p>");
		await SetKindAsync("clauses");
		await SetNameAsync(name);
		await Page.EvaluateFunctionAsync(
			"other => grapesjs.editors[0].setComponents('<p>New wording</p><div class=\"clause\" data-clause=\"' + other + '\"></div>')", other);
		await WaitForCanvasTextAsync("Other v1");
		await fixture.PublishClauseAsync(other, "<p>Other v2</p>");

		// v1 is published, so publishing shows the changes first.
		await Page.EvaluateExpressionAsync("document.getElementById('btnPublish').click()");
		await WaitAsync("() => !!document.getElementById('diffPublish')");
		Assert.Contains("New wording", await EvalAsync<string>("document.querySelector('.diff-pane[data-pane=text]').textContent"));
		var status = await ActAsync("document.getElementById('diffPublish').click()");
		Assert.StartsWith($"Clause {name} v2 is now published.", status);
		await WaitForCanvasTextAsync("Other v2");

		var content = await fixture.Http.GetFromJsonAsync<JsonElement>($"api/clauses/{name}/content");
		Assert.Contains("New wording", content.GetProperty("html").GetString());
		Assert.Contains($"{{% include '{other}' %}}", content.GetProperty("html").GetString());
		await SetKindAsync("templates");
	}
}
