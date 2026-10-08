using System.Text.Json.Nodes;
using PuppeteerSharp;

namespace FaCT.DocDesigner.POC.Tests.ModelPanel;

/// <summary>
/// Fixing binding problems from the Model panel (Add to model, or the list a group's .size meant), and the + that adds
/// a property to a group, a list's items or the top level.
/// </summary>
[Collection("Isolated designer")]
public sealed class ModelQuickFixDesignerTests(IsolatedDesignerFixture fixture)
{
	private const string Model = """
		{ "policy": { "number": "P-1" },
		  "claims": [ { "amount": 10 }, { "amount": 5 } ],
		  "auto": { "other": { "flag": true, "coverages": [ { "name": "Towing" } ] } } }
		""";

	private IPage Page => fixture.Page;

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

	/// <summary>The test model, then the given components (GrapesJS definitions as JSON) and, if any, their problems.</summary>
	private async Task StartAsync(string components, bool problems = true)
	{
		await ActAsync("(() => { const s = document.getElementById('scenarioSelect'); s.value = ''; s.dispatchEvent(new Event('change')); })()");
		await Page.EvaluateFunctionAsync("() => grapesjs.editors[0].setComponents('<p>x</p>')");
		await Page.EvaluateExpressionAsync("document.getElementById('btnEditModel').click()");
		await WaitAsync("() => !!document.querySelector('.gjs-mdl-content textarea.model-editor')");
		await Page.EvaluateFunctionAsync("m => document.querySelector('.gjs-mdl-content textarea.model-editor').value = m", Model);
		await ActAsync("document.querySelector('.gjs-mdl-content .model-actions button.primary').click()");
		await Page.EvaluateFunctionAsync("c => { const e = grapesjs.editors[0]; e.select(null); e.setComponents(JSON.parse(c)); e.setStyle(''); }", components);
		// the problems are listed shortly after an edit
		if (problems) await WaitAsync("() => !!document.querySelector('#modelProblems .mp-item')");
	}

	private async Task<JsonObject> ModelAsync()
	{
		await Page.EvaluateExpressionAsync("document.getElementById('btnEditModel').click()");
		await WaitAsync("() => !!document.querySelector('.gjs-mdl-content textarea.model-editor')");
		var json = await EvalAsync<string>("document.querySelector('.gjs-mdl-content textarea.model-editor').value");
		await Page.EvaluateExpressionAsync("document.querySelector('.gjs-mdl-content .model-actions button:not(.primary)').click()");
		return JsonNode.Parse(json)!.AsObject();
	}

	private Task<string> ProblemsAsync() => EvalAsync<string>("document.getElementById('modelProblems').textContent");

	private Task<string[]> FixesAsync() =>
		EvalAsync<string[]>("Array.from(document.querySelectorAll('#modelProblems .mp-item .mp-fix')).map(b => b.textContent)");

	private Task<bool> InTreeAsync(string path) =>
		Page.EvaluateFunctionAsync<bool>("p => !!document.querySelector('#modelTree .mt-usage[data-path=\"' + p + '\"]')", path);

	[Fact]
	public async Task A_missing_field_is_added_to_its_group_with_an_example_from_its_format()
	{
		await StartAsync("""
			[ { "tagName": "p", "components": [
			    { "type": "data-field", "field": "policy.premium", "format": "currency" },
			    { "type": "data-field", "field": "policy.agentName" } ] } ]
			""");
		Assert.Contains("\"policy.premium\" is not in the data model.", await ProblemsAsync());
		Assert.Equal(["Add to model", "Add to model"], await FixesAsync());

		var status = await ActAsync("document.querySelector('#modelProblems .mp-item .mp-fix').click()");
		Assert.StartsWith("Added policy.premium to the model", status);
		var policy = (await ModelAsync())["policy"]!.AsObject();
		Assert.Equal(0, policy["premium"]!.GetValue<int>());
		Assert.Equal("P-1", policy["number"]!.GetValue<string>());
		Assert.True(await InTreeAsync("policy.premium"));
		Assert.DoesNotContain("policy.premium", await ProblemsAsync());
		Assert.Contains("policy.agentName", await ProblemsAsync());

		await ActAsync("document.querySelector('#modelProblems .mp-item .mp-fix').click()");
		Assert.Equal("Agent Name", (await ModelAsync())["policy"]!["agentName"]!.GetValue<string>());
		Assert.Equal("", await ProblemsAsync());
	}

	[Fact]
	public async Task Clicking_the_problem_still_selects_its_component()
	{
		await StartAsync("""[ { "tagName": "p", "components": [ { "type": "data-field", "field": "policy.agentName" } ] } ]""");
		await Page.EvaluateExpressionAsync("grapesjs.editors[0].select(null)");
		await Page.EvaluateExpressionAsync("document.querySelector('#modelProblems .mp-item').click()");
		Assert.Equal("policy.agentName", await EvalAsync<string>("grapesjs.editors[0].getSelected().get('field')"));
		Assert.False((await ModelAsync())["policy"]!.AsObject().ContainsKey("agentName"));
	}

	[Fact]
	public async Task A_field_of_a_list_item_is_added_to_every_item()
	{
		await StartAsync("""
			[ { "type": "repeat", "listPath": "claims", "components": [ { "type": "data-field", "field": "claim.status" } ] } ]
			""");
		Assert.Contains("\"claim.status\" is not in the data model.", await ProblemsAsync());
		await ActAsync("document.querySelector('#modelProblems .mp-item .mp-fix').click()");

		var claims = (await ModelAsync())["claims"]!.AsArray();
		Assert.Equal(2, claims.Count);
		Assert.All(claims, c => Assert.Equal("Status", c!["status"]!.GetValue<string>()));
		Assert.Equal(10, claims[0]!["amount"]!.GetValue<int>());
		Assert.Equal("", await ProblemsAsync());
	}

	[Fact]
	public async Task Add_all_creates_a_missing_list_and_its_item_fields_and_the_repeat_names_its_items_after_it()
	{
		await StartAsync("""
			[ { "type": "repeat", "listPath": "policy.drivers", "components": [
			    { "type": "data-field", "field": "driver.name" },
			    { "type": "data-field", "field": "driver.born", "format": "shortdate" } ] } ]
			""");
		Assert.Equal(1, await EvalAsync<int>("document.querySelectorAll('#mpAddAll').length"));

		var status = await ActAsync("document.getElementById('mpAddAll').click()");
		Assert.StartsWith("Added 3 properties to the model", status);
		var drivers = (await ModelAsync())["policy"]!["drivers"]!.AsArray();
		var driver = Assert.Single(drivers)!.AsObject();
		Assert.Equal("Name", driver["name"]!.GetValue<string>());
		Assert.Matches(@"^\d{4}-\d{2}-\d{2}$", driver["born"]!.GetValue<string>());
		Assert.Equal("driver", await EvalAsync<string>("grapesjs.editors[0].getWrapper().findType('repeat')[0].get('alias')"));
		Assert.Equal("", await ProblemsAsync());
	}

	[Fact]
	public async Task A_missing_list_size_makes_an_empty_list()
	{
		await StartAsync("""
			[ { "type": "conditional", "field": "policy.forms.size", "operator": "gt", "value": "0",
			    "components": [ { "tagName": "p", "content": "Has forms" } ] } ]
			""");
		await ActAsync("document.querySelector('#modelProblems .mp-item .mp-fix').click()");
		Assert.Empty((await ModelAsync())["policy"]!["forms"]!.AsArray());
		Assert.Equal("", await ProblemsAsync());
	}

	[Fact]
	public async Task The_size_of_a_group_offers_the_list_inside_it()
	{
		await StartAsync("""
			[ { "type": "conditional", "field": "auto.other.size", "operator": "lt", "value": "1",
			    "components": [ { "tagName": "p", "content": "No other coverages" } ] } ]
			""");
		Assert.Equal(["Use coverages.size"], await FixesAsync());

		var status = await ActAsync("document.querySelector('#modelProblems .mp-item .mp-fix').click()");
		Assert.Equal("Now bound to auto.other.coverages.size.", status);
		Assert.Equal("auto.other.coverages.size", await EvalAsync<string>("grapesjs.editors[0].getWrapper().findType('conditional')[0].get('field')"));
		Assert.Equal("lt", await EvalAsync<string>("grapesjs.editors[0].getWrapper().findType('conditional')[0].get('operator')"));
		Assert.Equal("", await ProblemsAsync());
		Assert.True((await ModelAsync())["auto"]!["other"]!.AsObject().ContainsKey("flag"));
	}

	[Fact]
	public async Task A_value_in_the_way_is_explained_and_nothing_changes()
	{
		await StartAsync("""[ { "tagName": "p", "components": [ { "type": "data-field", "field": "policy.number.digits" } ] } ]""");
		var status = await ActAsync("document.querySelector('#modelProblems .mp-item .mp-fix').click()");
		Assert.Equal("Could not add policy.number.digits: \"policy.number\" is a value in the model, not a group.", status);
		Assert.Equal("P-1", (await ModelAsync())["policy"]!["number"]!.GetValue<string>());
	}

	[Fact]
	public async Task A_schema_example_fills_in_values_without_hiding_the_other_properties()
	{
		// like Orbital's quote.schema.json: the root example is one commercial auto quote; the schema has every line
		const string schema = """
			{ "$schema": "http://json-schema.org/draft-07/schema#", "type": "object",
			  "examples": [ { "state": "WA", "commercialAuto": { "liabilityLimit": 1000000 } } ],
			  "properties": {
			    "state": { "type": "string" },
			    "commercialAuto": { "type": "object", "properties": {
			      "liabilityLimit": { "type": "number" }, "autoEnhancement": { "type": "string", "enum": ["basic", "special"] } } },
			    "generalLiability": { "type": "object", "properties": { "auditFrequency": { "type": "string" } } },
			    "locations": { "type": "array", "items": { "$ref": "#/definitions/location" } } },
			  "definitions": { "location": { "type": "object", "properties": { "locationId": { "type": "string" } } } } }
			""";
		await StartAsync("""[ { "tagName": "p", "content": "x" } ]""", problems: false);
		await Page.EvaluateExpressionAsync("document.getElementById('btnEditModel').click()");
		await WaitAsync("() => !!document.querySelector('.gjs-mdl-content textarea.model-editor')");
		await Page.EvaluateFunctionAsync("m => document.querySelector('.gjs-mdl-content textarea.model-editor').value = m", schema);
		Assert.StartsWith("Model updated from JSON Schema", await ActAsync("document.querySelector('.gjs-mdl-content .model-actions button.primary').click()"));

		var model = await ModelAsync();
		Assert.Equal("WA", model["state"]!.GetValue<string>());
		Assert.Equal(1000000, model["commercialAuto"]!["liabilityLimit"]!.GetValue<int>());
		Assert.Equal("basic", model["commercialAuto"]!["autoEnhancement"]!.GetValue<string>());
		Assert.Equal("text", model["generalLiability"]!["auditFrequency"]!.GetValue<string>());
		Assert.Equal("text", Assert.Single(model["locations"]!.AsArray())!["locationId"]!.GetValue<string>());
	}

	private async Task<string> AddPropertyAsync(string opener, string name, string type)
	{
		await Page.EvaluateExpressionAsync(opener);
		await WaitAsync("() => !!document.getElementById('newPropertyName')");
		await Page.EvaluateFunctionAsync("(n, t) => { document.getElementById('newPropertyName').value = n; document.getElementById('newPropertyType').value = t; }", name, type);
		await Page.EvaluateExpressionAsync("document.getElementById('status').textContent = ''");
		await Page.EvaluateExpressionAsync("document.getElementById('newPropertyAdd').click()");
		return await EvalAsync<string>("(document.querySelector('.add-property .model-error') || {}).textContent || ''");
	}

	[Fact]
	public async Task The_plus_on_groups_lists_and_the_panel_adds_properties()
	{
		await StartAsync("""[ { "tagName": "p", "content": "x" } ]""", problems: false);
		Assert.True(await EvalAsync<bool>("!!document.querySelector('#modelTree .mt-add[data-path=\"policy\"]')"));
		Assert.True(await EvalAsync<bool>("!!document.querySelector('#modelTree .mt-add[data-path=\"claims\"]')"));
		Assert.False(await EvalAsync<bool>("!!document.querySelector('#modelTree .mt-add[data-path=\"policy.number\"]')"));

		Assert.Equal("", await AddPropertyAsync("document.querySelector('#modelTree .mt-add[data-path=\"policy\"]').click()", "limit", "number"));
		Assert.Equal("Added policy.limit to the model", (await EvalAsync<string>("document.getElementById('status').textContent")).Split(" (")[0]);
		Assert.True(await InTreeAsync("policy.limit"));
		Assert.Equal("number", await EvalAsync<string>("document.querySelector('#modelTree .mt-usage[data-path=\"policy.limit\"]').parentElement.querySelector('.mt-icon').title"));

		Assert.Equal("", await AddPropertyAsync("document.querySelector('#modelTree .mt-add[data-path=\"claims\"]').click()", "paid", "yesno"));
		Assert.True(await InTreeAsync("claim.paid"));
		Assert.Equal("", await AddPropertyAsync("document.getElementById('btnAddProperty').click()", "agents", "list"));
		Assert.True(await InTreeAsync("agents"));

		var model = await ModelAsync();
		Assert.Equal(0, model["policy"]!["limit"]!.GetValue<int>());
		Assert.All(model["claims"]!.AsArray(), c => Assert.True(c!["paid"]!.GetValue<bool>()));
		Assert.Empty(model["agents"]!.AsArray());
	}

	[Fact]
	public async Task The_plus_explains_a_bad_or_taken_name_and_stays_open()
	{
		await StartAsync("""[ { "tagName": "p", "content": "x" } ]""", problems: false);
		const string open = "document.querySelector('#modelTree .mt-add[data-path=\"policy\"]').click()";
		Assert.Equal("\"2nd\" is not a usable property name (letters, numbers and _).", await AddPropertyAsync(open, "2nd", "text"));
		Assert.Equal("It is already in the model as a value.", await AddPropertyAsync(open, "number", "text"));
		Assert.Equal("Type a name.", await AddPropertyAsync(open, " ", "text"));
		Assert.True(await EvalAsync<bool>("!!document.getElementById('newPropertyName')"));
		await Page.EvaluateExpressionAsync("document.querySelector('.add-property .model-actions button:not(.primary)').click()");
		Assert.Equal("P-1", (await ModelAsync())["policy"]!["number"]!.GetValue<string>());
	}
}
