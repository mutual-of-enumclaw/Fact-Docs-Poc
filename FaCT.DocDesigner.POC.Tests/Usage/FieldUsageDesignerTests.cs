using System.Net.Http.Json;
using System.Text.Json;
using PuppeteerSharp;

namespace FaCT.DocDesigner.POC.Tests.Usage;

/// <summary>Field usage in the designer: the Model panel search, per-property usage buttons, and opening a result.</summary>
[Collection("Isolated designer")]
public sealed class FieldUsageDesignerTests(IsolatedDesignerFixture fixture)
{
	private IPage Page => fixture.Page;

	private static string Unique(string name) => name + Guid.NewGuid().ToString("N")[..8];

	private Task<T> EvalAsync<T>(string script) => Page.EvaluateExpressionAsync<T>(script);

	private Task WaitAsync(string predicate) =>
		Page.WaitForFunctionAsync(predicate, new WaitForFunctionOptions { Timeout = 30_000 });

	private async Task<string> ActAsync(string script)
	{
		await Page.EvaluateExpressionAsync("document.getElementById('status').textContent = ''");
		await Page.EvaluateExpressionAsync(script);
		await WaitAsync("() => { const t = document.getElementById('status').textContent; return t.length > 0 && !t.endsWith('...'); }");
		return await EvalAsync<string>("document.getElementById('status').textContent");
	}

	private async Task PublishAsync(string kind, string name, string html)
	{
		var saved = await fixture.Http.PutAsJsonAsync($"api/{kind}/{name}/draft", new { project = new { }, html, css = "" });
		saved.EnsureSuccessStatusCode();
		var version = (await saved.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("version").GetInt32();
		(await fixture.Http.PostAsync($"api/{kind}/{name}/versions/{version}/publish", null)).EnsureSuccessStatusCode();
	}

	private async Task SetModelAsync(string json)
	{
		await Page.EvaluateExpressionAsync("document.getElementById('btnEditModel').click()");
		await WaitAsync("() => !!document.querySelector('.gjs-mdl-content textarea.model-editor')");
		await Page.EvaluateFunctionAsync("m => document.querySelector('.gjs-mdl-content textarea.model-editor').value = m", json);
		await ActAsync("document.querySelector('.gjs-mdl-content .model-actions button.primary').click()");
	}

	private async Task<string> SearchAsync(string path, bool allVersions = false)
	{
		await Page.EvaluateFunctionAsync("(p, all) => { document.getElementById('usagePath').value = p; document.getElementById('usageAll').checked = all; }", path, allVersions);
		return await ActAsync("document.getElementById('usageSearch').click()");
	}

	private Task<string[]> RowsAsync() =>
		EvalAsync<string[]>("[...document.querySelectorAll('#usageResults .usage-row')].map(r => r.getAttribute('data-doc'))");

	private Task CloseAsync() => Page.EvaluateExpressionAsync("grapesjs.editors[0].Modal.close()");

	[Fact]
	public async Task The_model_panel_search_finds_templates_and_clauses()
	{
		var field = Unique("acct");
		var template = Unique("tpl");
		var clause = Unique("cl");
		await PublishAsync("templates", template, $"<p>{{{{ {field}.number }}}}</p>");
		await PublishAsync("clauses", clause, $"<p>{{{{ {field}.number | upcase }}}}</p>");

		await Page.EvaluateExpressionAsync("document.getElementById('btnFieldUsage').click()");
		await WaitAsync("() => !!document.getElementById('usagePath')");
		Assert.Equal("Field usage", await EvalAsync<string>("document.querySelector('.gjs-mdl-title').textContent"));
		Assert.Equal("", await EvalAsync<string>("document.getElementById('usagePath').value"));

		var status = await SearchAsync(field + ".number");
		Assert.StartsWith($"\"{field}.number\" is used in 2 document versions.", status);
		Assert.Equal([$"clauses/{clause}/1", $"templates/{template}/1"], (await RowsAsync()).OrderBy(r => r));
		Assert.Equal(["Clause", "Template"], (await EvalAsync<string[]>(
			"[...document.querySelectorAll('#usageResults .usage-row')].map(r => r.cells[0].textContent)")).OrderBy(k => k));
		Assert.Contains("v1 Published", await EvalAsync<string>("document.querySelector('#usageResults .usage-row').textContent"));

		// Suggestions come from every path in use.
		await WaitAsync($"() => [...document.querySelectorAll('#usagePaths option')].some(o => o.value === '{field}.number')");
		await CloseAsync();
	}

	[Fact]
	public async Task Nothing_found_and_bad_paths_are_explained()
	{
		await Page.EvaluateExpressionAsync("document.getElementById('btnFieldUsage').click()");
		await WaitAsync("() => !!document.getElementById('usagePath')");
		var field = Unique("none");
		Assert.StartsWith($"\"{field}\" is used in 0 document versions.", await SearchAsync(field));
		Assert.Empty(await RowsAsync());

		var status = await SearchAsync("not a path");
		Assert.Contains("Enter a data path", status);
		Assert.True(await EvalAsync<bool>("document.getElementById('status').classList.contains('error')"));
		await CloseAsync();
	}

	[Fact]
	public async Task The_unsaved_canvas_is_counted()
	{
		var field = Unique("cv");
		await Page.EvaluateFunctionAsync("h => grapesjs.editors[0].setComponents(h)",
			$"<p>{{{{ {field}.a }}}}</p><p>{{% if {field}.b %}}x{{% endif %}}</p>");
		await Page.EvaluateExpressionAsync("document.getElementById('btnFieldUsage').click()");
		await WaitAsync("() => !!document.getElementById('usagePath')");
		Assert.Contains("This canvas (unsaved): 2 places.", await SearchAsync(field));
		await CloseAsync();
	}

	[Fact]
	public async Task A_model_property_searches_for_itself_with_its_list_path()
	{
		var list = Unique("sites");
		var template = Unique("tpl");
		await PublishAsync("templates", template, $"{{% for site in {list} %}}<p>{{{{ site.city }}}}</p>{{% endfor %}}");
		await SetModelAsync($$"""{ "{{list}}": [ { "city": "Seattle" } ] }""");

		// The item property's button (path written with the alias the designer gives the list).
		var itemPath = await EvalAsync<string>(
			$"[...document.querySelectorAll('.mt-usage')].map(b => b.getAttribute('data-path')).find(p => p.endsWith('.city'))");
		await ActAsync($"document.querySelector('.mt-usage[data-path=\"{itemPath}\"]').click()");
		Assert.Equal($"{list}[].city", await EvalAsync<string>("document.getElementById('usagePath').value"));
		Assert.Equal([$"templates/{template}/1"], await RowsAsync());
		Assert.Contains($"{list}[].city", await EvalAsync<string>("document.querySelector('#usageResults .usage-row').cells[3].textContent"));
		await CloseAsync();
	}

	[Fact]
	public async Task Templates_using_a_field_through_a_clause_say_so()
	{
		var field = Unique("via");
		var clause = Unique("cl");
		var template = Unique("tpl");
		await PublishAsync("clauses", clause, $"<p>{{{{ {field}.name }}}}</p>");
		await PublishAsync("templates", template, $"<h1>Doc</h1>{{% include '{clause}' %}}");
		await Page.EvaluateExpressionAsync("document.getElementById('btnFieldUsage').click()");
		await WaitAsync("() => !!document.getElementById('usagePath')");
		await SearchAsync(field + ".name");
		Assert.Equal($"{clause} v1", await EvalAsync<string>(
			$"document.querySelector('.usage-row[data-doc=\"templates/{template}/1\"]').cells[4].textContent"));
		await CloseAsync();
	}

	[Fact]
	public async Task All_versions_includes_retired_ones()
	{
		var field = Unique("old");
		var template = Unique("tpl");
		await PublishAsync("templates", template, $"<p>{{{{ {field} }}}}</p>");
		await PublishAsync("templates", template, "<p>No longer</p>");
		await Page.EvaluateExpressionAsync("document.getElementById('btnFieldUsage').click()");
		await WaitAsync("() => !!document.getElementById('usagePath')");
		await SearchAsync(field);
		Assert.Empty(await RowsAsync());
		await SearchAsync(field, allVersions: true);
		Assert.Equal([$"templates/{template}/1"], await RowsAsync());
		Assert.Contains("v1 Retired", await EvalAsync<string>("document.querySelector('#usageResults .usage-row').textContent"));
		await CloseAsync();
	}

	[Fact]
	public async Task Open_loads_the_found_version_into_the_designer()
	{
		var field = Unique("open");
		var clause = Unique("cl");
		// A real designer project, so the version opens with its content.
		await Page.EvaluateFunctionAsync("h => grapesjs.editors[0].setComponents(h)", $"<p class=\"found\">Found {{{{ {field} }}}}</p>");
		var project = await EvalAsync<string>("JSON.stringify(grapesjs.editors[0].getProjectData())");
		var html = await EvalAsync<string>("grapesjs.editors[0].getWrapper().getInnerHTML()");
		await Page.EvaluateFunctionAsync("h => grapesjs.editors[0].setComponents(h)", "<p>Something else</p>");
		var put = await fixture.Http.PutAsync($"api/clauses/{clause}/draft", new StringContent(
			JsonSerializer.Serialize(new { project = JsonDocument.Parse(project).RootElement, html, css = "" }), System.Text.Encoding.UTF8, "application/json"));
		put.EnsureSuccessStatusCode();
		(await fixture.Http.PostAsync($"api/clauses/{clause}/versions/1/publish", null)).EnsureSuccessStatusCode();

		await Page.EvaluateExpressionAsync("document.getElementById('btnFieldUsage').click()");
		await WaitAsync("() => !!document.getElementById('usagePath')");
		await SearchAsync(field);

		var status = await ActAsync($"document.querySelector('.usage-row[data-doc=\"clauses/{clause}/1\"] .usage-open').click()");
		Assert.Equal($"Opened clause {clause} v1 (Published).", status);
		Assert.False(await EvalAsync<bool>("grapesjs.editors[0].Modal.isOpen()"));
		Assert.Equal("clauses", await EvalAsync<string>("document.getElementById('docKind').value"));
		Assert.Equal("Clause", await EvalAsync<string>("document.getElementById('templateNameLabel').textContent"));
		Assert.Equal(clause, await EvalAsync<string>("document.getElementById('templateName').value"));
		Assert.Contains("Found", await EvalAsync<string>("grapesjs.editors[0].getHtml()"));
		Assert.DoesNotContain("Something else", await EvalAsync<string>("grapesjs.editors[0].getHtml()"));
		Assert.Equal("v1 Published", await EvalAsync<string>("document.getElementById('versionBadge').textContent"));
		await ActAsync("(() => { const k = document.getElementById('docKind'); k.value = 'templates'; k.dispatchEvent(new Event('change')); })()");
	}
}
