using System.Net.Http.Json;
using System.Text.Json;
using PuppeteerSharp;

namespace FaCT.DocDesigner.POC.Tests.Scenarios;

/// <summary>Test-data scenarios in the designer: managing them, showing one on the canvas, Preview PDF and Preview all.</summary>
[Collection("Isolated designer")]
public sealed class ScenarioDesignerTests(IsolatedDesignerFixture fixture)
{
	private const string Model = """
		{ "policy": { "number": "EX-100", "insured": "Example Insured" },
		  "locations": [ { "name": "Example Location" } ] }
		""";

	private const string Canvas =
		"<p>Policy <span class=\"df\" data-field=\"policy.number\"></span> for <span class=\"df\" data-field=\"policy.insured\"></span></p>";

	private IPage Page => fixture.Page;

	private static string Unique(string name) => name + "-" + Guid.NewGuid().ToString("N")[..8];

	private Task<T> EvalAsync<T>(string script) => Page.EvaluateExpressionAsync<T>(script);

	private Task WaitAsync(string predicate, params object[] args) =>
		Page.WaitForFunctionAsync(predicate, new WaitForFunctionOptions { Timeout = 30_000 }, args);

	private Task<string> StatusAsync() => EvalAsync<string>("document.getElementById('status').textContent");

	private async Task<string> ActAsync(string script)
	{
		await Page.EvaluateExpressionAsync("document.getElementById('status').textContent = ''");
		await Page.EvaluateExpressionAsync(script);
		await WaitAsync("() => { const t = document.getElementById('status').textContent; return t.length > 0 && !t.endsWith('...'); }");
		return await StatusAsync();
	}

	private Task Click(string selector) => Page.EvaluateFunctionAsync("s => document.querySelector(s).click()", selector);

	/// <summary>A new template (own name, so its own scenarios) with the test model and two bound fields.</summary>
	private async Task<string> NewTemplateAsync(string? canvas = null)
	{
		var name = Unique("tpl");
		await ActAsync("(() => { const k = document.getElementById('docKind'); k.value = 'templates'; k.dispatchEvent(new Event('change')); })()");
		await ActAsync($"(() => {{ const n = document.getElementById('templateName'); n.value = '{name}'; n.dispatchEvent(new Event('change')); }})()");
		await Click("#btnEditModel");
		await WaitAsync("() => !!document.querySelector('.gjs-mdl-content textarea.model-editor')");
		await Page.EvaluateFunctionAsync("m => document.querySelector('.gjs-mdl-content textarea.model-editor').value = m", Model);
		await ActAsync("document.querySelector('.gjs-mdl-content .model-actions button.primary').click()");
		await Page.EvaluateFunctionAsync("html => grapesjs.editors[0].setComponents(html)", canvas ?? Canvas);
		await WaitAsync("() => grapesjs.editors[0].getWrapper().findType('data-field').every(c => c.view && c.view.el.textContent.length > 0)");
		return name;
	}

	private Task<string> FieldTextAsync(int index) =>
		EvalAsync<string>($"grapesjs.editors[0].getWrapper().findType('data-field')[{index}].view.el.textContent");

	private Task SaveScenarioViaApiAsync(string template, string scenario, string json) =>
		fixture.Http.PutAsync($"api/templates/{template}/scenarios/{Uri.EscapeDataString(scenario)}",
			new StringContent("{\"data\":" + json + "}", System.Text.Encoding.UTF8, "application/json"))
			.ContinueWith(t => t.Result.EnsureSuccessStatusCode());

	/// <summary>Reloads the scenario list the way changing the template name does.</summary>
	private Task<string> ReloadScenariosAsync() =>
		ActAsync("document.getElementById('templateName').dispatchEvent(new Event('change'))");

	private Task<string[]> SelectOptionsAsync() =>
		EvalAsync<string[]>("[...document.getElementById('scenarioSelect').options].map(o => o.textContent)");

	private Task<string> UseScenarioAsync(string name) => ActAsync(
		$"(() => {{ const s = document.getElementById('scenarioSelect'); s.value = {JsonSerializer.Serialize(name)}; s.dispatchEvent(new Event('change')); }})()");

	private async Task OpenManagerAsync()
	{
		await Click("#btnScenarios");
		await WaitAsync("() => !!document.querySelector('.gjs-mdl-content .scenario-manager')");
	}

	private async Task<string> SaveInEditorAsync(string name, string json)
	{
		await WaitAsync("() => !!document.getElementById('scenarioSave')");
		await Page.EvaluateFunctionAsync("(n, d) => { document.getElementById('scenarioName').value = n; document.getElementById('scenarioData').value = d; }", name, json);
		await Click("#scenarioSave");
		await WaitAsync("() => !!document.querySelector('.gjs-mdl-content .scenario-manager') || document.getElementById('scenarioError').textContent.length > 0");
		return await EvalAsync<string>("document.getElementById('scenarioError') ? document.getElementById('scenarioError').textContent : ''");
	}

	private Task CloseModalAsync() => Page.EvaluateExpressionAsync("grapesjs.editors[0].Modal.close()");

	[Fact]
	public async Task The_toolbar_starts_on_the_model_example()
	{
		await NewTemplateAsync();
		Assert.Equal(["Model example"], await SelectOptionsAsync());
		Assert.Equal("", await EvalAsync<string>("document.getElementById('scenarioSelect').value"));
		Assert.Equal("EX-100", await FieldTextAsync(0));
	}

	[Fact]
	public async Task A_new_scenario_starts_from_the_current_data_and_is_saved_for_the_template()
	{
		var template = await NewTemplateAsync();
		await OpenManagerAsync();
		await Click("#scenarioNew");
		await WaitAsync("() => !!document.getElementById('scenarioData')");
		var prefilled = JsonDocument.Parse(await EvalAsync<string>("document.getElementById('scenarioData').value")).RootElement;
		Assert.Equal("EX-100", prefilled.GetProperty("policy").GetProperty("number").GetString());

		Assert.Equal("", await SaveInEditorAsync("Small business", """{"policy":{"number":"SB-1","insured":"Corner Shop"}}"""));
		Assert.Equal(1, await EvalAsync<int>("document.querySelectorAll('.scenario-row[data-scenario=\"Small business\"]').length"));
		Assert.StartsWith("Saved scenario \"Small business\".", await StatusAsync());
		await CloseModalAsync();

		Assert.Equal(["Model example", "Small business"], await SelectOptionsAsync());
		var saved = await fixture.Http.GetFromJsonAsync<JsonElement[]>($"api/templates/{template}/scenarios");
		Assert.Equal("SB-1", saved!.Single().GetProperty("data").GetProperty("policy").GetProperty("number").GetString());
	}

	[Fact]
	public async Task Choosing_a_scenario_shows_its_values_on_the_canvas_and_reports_gaps()
	{
		var template = await NewTemplateAsync();
		await SaveScenarioViaApiAsync(template, "No insured", """{"policy":{"number":"NI-7"}}""");
		await ReloadScenariosAsync();

		var status = await UseScenarioAsync("No insured");
		Assert.Equal("NI-7", await FieldTextAsync(0));
		Assert.Equal("Showing scenario \"No insured\". No value for 1 field(s): policy.insured.", status);

		status = await UseScenarioAsync("");
		Assert.Equal("EX-100", await FieldTextAsync(0));
		Assert.Equal("Example Insured", await FieldTextAsync(1));
		Assert.Equal("Showing the model example data.", status);
	}

	[Fact]
	public async Task Preview_pdf_uses_the_chosen_scenario()
	{
		var template = await NewTemplateAsync();
		await SaveScenarioViaApiAsync(template, "Preview me", """{"policy":{"number":"PV-42","insured":"Preview Co"}}""");
		await ReloadScenariosAsync();
		await UseScenarioAsync("Preview me");

		var posted = new TaskCompletionSource<string>();
		void OnRequest(object? sender, RequestEventArgs e)
		{
			if (e.Request.Url.EndsWith("/api/render", StringComparison.Ordinal)) posted.TrySetResult(e.Request.PostData?.ToString() ?? "");
		}
		Page.Request += OnRequest;
		try
		{
			await ActAsync("document.getElementById('btnPreview').click()");
			var body = JsonDocument.Parse(await posted.Task.WaitAsync(TimeSpan.FromSeconds(30))).RootElement;
			Assert.Equal("PV-42", body.GetProperty("data").GetProperty("policy").GetProperty("number").GetString());
			Assert.Contains("Preview me", await EvalAsync<string>("document.querySelector('.gjs-mdl-title').textContent"));
		}
		finally
		{
			Page.Request -= OnRequest;
			await CloseModalAsync();
			await UseScenarioAsync("");
		}
	}

	[Fact]
	public async Task Editing_renames_and_deleting_removes_a_scenario()
	{
		var template = await NewTemplateAsync();
		await SaveScenarioViaApiAsync(template, "Draft name", """{"policy":{"number":"DN-1"}}""");
		await ReloadScenariosAsync();
		await UseScenarioAsync("Draft name");

		await OpenManagerAsync();
		await Click(".scenario-row[data-scenario=\"Draft name\"] .scenario-edit");
		Assert.Contains("DN-1", await EvalAsync<string>("(() => { const d = document.getElementById('scenarioData'); return d ? d.value : ''; })()"));
		Assert.Equal("", await SaveInEditorAsync("Final name", """{"policy":{"number":"FN-2"}}"""));

		var names = (await fixture.Http.GetFromJsonAsync<JsonElement[]>($"api/templates/{template}/scenarios"))!.Select(s => s.GetProperty("name").GetString());
		Assert.Equal(["Final name"], names);
		// The renamed scenario stays the one shown.
		Assert.Equal("Final name", await EvalAsync<string>("document.getElementById('scenarioSelect').value"));
		Assert.Equal("FN-2", await FieldTextAsync(0));

		await ActAsync("document.querySelector('.scenario-row[data-scenario=\"Final name\"] .scenario-delete').click()");
		Assert.Equal("Deleted scenario \"Final name\".", await StatusAsync());
		await CloseModalAsync();
		Assert.Empty((await fixture.Http.GetFromJsonAsync<JsonElement[]>($"api/templates/{template}/scenarios"))!);
		Assert.Equal(["Model example"], await SelectOptionsAsync());
		Assert.Equal("EX-100", await FieldTextAsync(0));
	}

	[Theory]
	[InlineData("-bad name", "{}", "Name:")]
	[InlineData("Bad.name", "{}", "Name:")]
	[InlineData("Fine", "{not json", "Not valid JSON")]
	[InlineData("Fine", "[1,2]", "must be a JSON object")]
	public async Task The_scenario_editor_explains_bad_input(string name, string json, string error)
	{
		var template = await NewTemplateAsync();
		await OpenManagerAsync();
		await Click("#scenarioNew");
		Assert.Contains(error, await SaveInEditorAsync(name, json));
		await CloseModalAsync();
		Assert.Empty((await fixture.Http.GetFromJsonAsync<JsonElement[]>($"api/templates/{template}/scenarios"))!);
	}

	[Fact]
	public async Task Scenarios_belong_to_the_template_name()
	{
		var first = await NewTemplateAsync();
		await SaveScenarioViaApiAsync(first, "Only for first", "{}");
		await ReloadScenariosAsync();
		await UseScenarioAsync("Only for first");
		Assert.Equal(["Model example", "Only for first"], await SelectOptionsAsync());

		await NewTemplateAsync();
		Assert.Equal(["Model example"], await SelectOptionsAsync());
		Assert.Equal("", await EvalAsync<string>("document.getElementById('scenarioSelect').value"));
	}

	[Fact]
	public async Task Preview_all_renders_the_example_and_every_scenario()
	{
		var template = await NewTemplateAsync();
		await SaveScenarioViaApiAsync(template, "Second", """{"policy":{"number":"S-2","insured":"Second Co"}}""");
		await SaveScenarioViaApiAsync(template, "Third", """{"policy":{"number":"T-3"}}""");
		await ReloadScenariosAsync();
		await OpenManagerAsync();

		var status = await ActAsync("document.getElementById('scenarioPreviewAll').click()");
		Assert.Equal("Rendered 3 of 3 scenario(s).", status);
		Assert.Equal(["Model example", "Second", "Third"], await EvalAsync<string[]>(
			"[...document.querySelectorAll('.scenario-result')].map(b => b.getAttribute('data-scenario'))"));
		Assert.Equal(3, await EvalAsync<int>("document.querySelectorAll('.scenario-result.ok').length"));
		Assert.StartsWith("1 page", await EvalAsync<string>("document.querySelector('.scenario-result small').textContent"));
		Assert.Contains("no value: policy.insured", await EvalAsync<string>(
			"document.querySelector('.scenario-result[data-scenario=\"Third\"] small').textContent"));

		// The first result is shown; clicking another shows its PDF.
		Assert.True(await EvalAsync<bool>("document.querySelector('.scenario-result').classList.contains('active')"));
		var firstSrc = await EvalAsync<string>("document.getElementById('scenarioPdf').src");
		Assert.StartsWith("blob:", firstSrc);
		await Click(".scenario-result[data-scenario=\"Third\"]");
		Assert.NotEqual(firstSrc, await EvalAsync<string>("document.getElementById('scenarioPdf').src"));
		Assert.True(await EvalAsync<bool>("document.querySelector('.scenario-result[data-scenario=\"Third\"]').classList.contains('active')"));
		Assert.EndsWith("Third.pdf", await EvalAsync<string>("document.querySelector('.scenario-preview .pdf-links a[download]').download"));
		await CloseModalAsync();
	}

	[Fact]
	public async Task Preview_all_shows_why_a_scenario_failed()
	{
		await NewTemplateAsync("<p>{% include 'missing-clause' %}</p>");
		await OpenManagerAsync();
		var status = await ActAsync("document.getElementById('scenarioPreviewAll').click()");
		Assert.Equal("Rendered 0 of 1 scenario(s); 1 failed.", status);
		Assert.Equal(1, await EvalAsync<int>("document.querySelectorAll('.scenario-result.failed').length"));
		Assert.Contains("missing-clause", await EvalAsync<string>("document.querySelector('.scenario-preview .model-error').textContent"));
		Assert.True(await EvalAsync<bool>("document.getElementById('scenarioPdf').hidden"));
		await CloseModalAsync();
	}

	[Fact]
	public async Task Scenarios_with_an_empty_list_are_reported_as_such()
	{
		var template = await NewTemplateAsync();
		await Page.EvaluateExpressionAsync(
			"(() => { const r = grapesjs.editors[0].getWrapper().append({ type: 'repeat' })[0]; r.set('collection', 'locations'); " +
			"r.components().reset(); r.append({ type: 'data-field', field: 'location.name' }); })()");
		await SaveScenarioViaApiAsync(template, "No locations", """{"policy":{"number":"NL-1","insured":"Nowhere"},"locations":[]}""");
		await ReloadScenariosAsync();
		var status = await UseScenarioAsync("No locations");
		Assert.Equal("Showing scenario \"No locations\". Empty list(s): locations.", status);
		await UseScenarioAsync("");
	}
}
