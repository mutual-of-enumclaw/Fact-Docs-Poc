using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PuppeteerSharp;

namespace FaCT.DocDesigner.POC.Tests.Calculations;

/// <summary>Calculated fields in the designer: the builder, live results, the canvas value, validation and export.</summary>
[Collection("Isolated designer")]
public sealed class CalculatedFieldDesignerTests(IsolatedDesignerFixture fixture)
{
	private const string Model = """
		{ "policy": { "premium": 1200.5, "fees": 100, "effective": "2026-01-01", "expiration": "2026-03-02" },
		  "claims": [ { "amount": 10 }, { "amount": 5 } ] }
		""";

	private const string First = "grapesjs.editors[0].getWrapper().findType('calc-field')[0]";

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

	/// <summary>A fresh canvas with the test model, the given HTML, and the model example as test data.</summary>
	private async Task StartAsync(string html)
	{
		await ActAsync("(() => { const s = document.getElementById('scenarioSelect'); s.value = ''; s.dispatchEvent(new Event('change')); })()");
		await Page.EvaluateExpressionAsync("document.getElementById('btnEditModel').click()");
		await WaitAsync("() => !!document.querySelector('.gjs-mdl-content textarea.model-editor')");
		await Page.EvaluateFunctionAsync("m => document.querySelector('.gjs-mdl-content textarea.model-editor').value = m", Model);
		await ActAsync("document.querySelector('.gjs-mdl-content .model-actions button.primary').click()");
		await Page.EvaluateFunctionAsync("h => { const e = grapesjs.editors[0]; e.select(null); e.setComponents(h); e.setStyle(''); }", html);
	}

	private async Task OpenBuilderAsync()
	{
		await Page.EvaluateExpressionAsync($"grapesjs.editors[0].select({First})");
		await WaitAsync("() => !!document.querySelector('.calc-trait .calc-edit')");
		await Page.EvaluateExpressionAsync("document.querySelector('.calc-trait .calc-edit').click()");
		await WaitAsync("() => !!document.getElementById('calcExpression')");
	}

	private async Task TypeAsync(string expression)
	{
		await Page.EvaluateFunctionAsync("x => { const a = document.getElementById('calcExpression'); a.value = x; a.dispatchEvent(new Event('input')); }", expression);
	}

	private Task WaitForResultAsync(string text) =>
		WaitAsync("t => document.getElementById('calcResult').textContent.includes(t)", text);

	private Task WaitForCanvasAsync(string text) =>
		WaitAsync($"t => {{ const c = {First}; return !!c && !!c.view && c.view.el.textContent === t; }}", text);

	private Task<string> ProblemsAsync() => EvalAsync<string>("document.getElementById('modelProblems').textContent");

	[Fact]
	public async Task The_block_is_in_the_data_category()
	{
		Assert.Equal("Calculated Field", await EvalAsync<string>("grapesjs.editors[0].Blocks.get('calc-field').get('label')"));
	}

	[Fact]
	public async Task Building_a_calculation_shows_the_result_and_applies_it()
	{
		await StartAsync("<p>Total due: <span class=\"df df-calc\" data-expression=\"\"></span></p>");
		await OpenBuilderAsync();
		Assert.Equal("Calculated field", await EvalAsync<string>("document.querySelector('.gjs-mdl-title').textContent"));
		Assert.True(await EvalAsync<bool>("document.getElementById('calcApply').disabled"));

		await Page.EvaluateExpressionAsync("document.getElementById('calcFormat').value = 'currency'");
		await TypeAsync("policy.premium + policy.fees");
		await WaitForResultAsync("= $1,300.50");
		Assert.Contains("with the model example data", await EvalAsync<string>("document.getElementById('calcResult').textContent"));
		Assert.False(await EvalAsync<bool>("document.getElementById('calcApply').disabled"));

		Assert.Equal("Calculation applied.", await ActAsync("document.getElementById('calcApply').click()"));
		Assert.Equal("policy.premium + policy.fees", await EvalAsync<string>($"{First}.get('expression')"));
		Assert.Equal("currency", await EvalAsync<string>($"{First}.get('format')"));
		await WaitForCanvasAsync("$1,300.50");

		var html = await EvalAsync<string>("grapesjs.editors[0].getHtml()");
		Assert.Contains("data-expression=\"policy.premium + policy.fees\"", html);
		Assert.Contains("{%- assign calc_t1 = policy.premium | plus: policy.fees -%}", html);
		Assert.Contains("{{ calc_result | currency }}", html);

		using var response = await fixture.Http.PostAsJsonAsync("api/render", new JsonObject
		{
			["html"] = html,
			["css"] = await EvalAsync<string>("grapesjs.editors[0].getCss()"),
			["data"] = JsonNode.Parse(Model)
		});
		Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
		Assert.Contains("Total due: $1,300.50", Regex.Replace(PdfText.Extract(await response.Content.ReadAsByteArrayAsync()), @"\s+", " "));
	}

	[Fact]
	public async Task Mistakes_are_explained_with_a_pointer_and_cannot_be_applied()
	{
		await StartAsync("<p><span class=\"df df-calc\" data-expression=\"\"></span></p>");
		await OpenBuilderAsync();
		await TypeAsync("policy.premium +");
		await WaitAsync("() => document.getElementById('calcError').textContent.length > 0");
		var error = await EvalAsync<string>("document.getElementById('calcError').textContent");
		Assert.StartsWith("The calculation ends too early", error);
		Assert.EndsWith("\n" + new string(' ', "policy.premium +".Length) + "^", error);
		Assert.True(await EvalAsync<bool>("document.getElementById('calcApply').disabled"));
		await Page.EvaluateExpressionAsync("document.getElementById('calcCancel').click()");
		Assert.Equal("", await EvalAsync<string>($"{First}.get('expression')"));
	}

	[Fact]
	public async Task Fields_and_functions_are_inserted_by_clicking()
	{
		await StartAsync("<p><span class=\"df df-calc\" data-expression=\"\"></span></p>");
		await OpenBuilderAsync();
		await Page.EvaluateExpressionAsync("document.querySelector('.calc-fn[data-function=sum]').click()");
		await Page.EvaluateExpressionAsync("document.querySelector('.calc-field-pick[data-path=\"claims\"]').click()");
		Assert.Equal("sum(claims)", await EvalAsync<string>("document.getElementById('calcExpression').value"));
		// sum(claims) totals a list of objects: point it at the amount.
		await TypeAsync("sum(claims.amount)");
		await WaitForResultAsync("= 15");
		await Page.EvaluateExpressionAsync("document.getElementById('calcExpression').setSelectionRange(18, 18)");
		await Page.EvaluateExpressionAsync("document.querySelector('.calc-op[data-insert=\"\u00d7\"]').click()");
		await Page.EvaluateExpressionAsync("(() => { const a = document.getElementById('calcExpression'); a.setSelectionRange(a.value.length, a.value.length); })()");
		await Page.EvaluateExpressionAsync("document.querySelector('.calc-field-pick[data-path=\"policy.fees\"]').click()");
		Assert.Equal("sum(claims.amount) \u00d7 policy.fees", await EvalAsync<string>("document.getElementById('calcExpression').value"));
		await WaitForResultAsync("= 1500");
		await Page.EvaluateExpressionAsync("document.getElementById('calcCancel').click()");
	}

	[Fact]
	public async Task The_field_filter_narrows_the_list()
	{
		await StartAsync("<p><span class=\"df df-calc\" data-expression=\"\"></span></p>");
		await OpenBuilderAsync();
		await Page.EvaluateExpressionAsync("(() => { const f = document.getElementById('calcFieldFilter'); f.value = 'fee'; f.dispatchEvent(new Event('input')); })()");
		Assert.Equal(["policy.fees"], await EvalAsync<string[]>("[...document.querySelectorAll('.calc-field-pick')].map(b => b.getAttribute('data-path'))"));
		await Page.EvaluateExpressionAsync("document.getElementById('calcCancel').click()");
	}

	[Fact]
	public async Task Calculations_in_saved_html_become_calculated_fields_again()
	{
		await StartAsync("<p><span class=\"df df-calc\" data-expression=\"days_between(policy.effective, policy.expiration)\" data-format=\"number\"></span> days</p>");
		Assert.Equal(1, await EvalAsync<int>("grapesjs.editors[0].getWrapper().findType('calc-field').length"));
		await WaitForCanvasAsync("60");
		Assert.Equal("number", await EvalAsync<string>($"{First}.get('format')"));
		Assert.Contains("{{ calc_result | number }}", await EvalAsync<string>("grapesjs.editors[0].getHtml()"));
	}

	[Fact]
	public async Task Unknown_fields_are_binding_problems()
	{
		await StartAsync("<p><span class=\"df df-calc\" data-expression=\"policy.nothing * 2\"></span></p>");
		await WaitAsync("() => document.getElementById('modelProblems').textContent.includes('policy.nothing')");
		Assert.Contains("Calculation: \"policy.nothing\" is not in the data model.", await ProblemsAsync());
		Assert.True(await EvalAsync<bool>($"{First}.view.el.classList.contains('binding-bad')"));
	}

	[Fact]
	public async Task Invalid_saved_calculations_are_binding_problems()
	{
		await StartAsync("<p><span class=\"df df-calc\" data-expression=\"policy.fees +\"></span></p>");
		await WaitAsync("() => document.getElementById('modelProblems').textContent.includes('ends too early')");
		Assert.Equal("", await EvalAsync<string>("grapesjs.editors[0].getHtml().match(/calc_result/) ? 'exported' : ''"));
	}

	[Fact]
	public async Task Item_fields_work_inside_a_repeat_and_show_the_first_item()
	{
		await StartAsync("<p>x</p>");
		await Page.EvaluateExpressionAsync(
			"(() => { const r = grapesjs.editors[0].getWrapper().append({ type: 'repeat' })[0]; r.set('collection', 'claims'); " +
			"r.components().reset(); r.append({ type: 'calc-field', expression: 'claim.amount * 2' }); })()");
		await WaitForCanvasAsync("20");
		Assert.DoesNotContain("claim.amount", await ProblemsAsync());

		// The same calculation outside the repeat is a problem.
		await Page.EvaluateExpressionAsync("grapesjs.editors[0].getWrapper().append({ type: 'calc-field', expression: 'claim.amount * 3' })");
		await WaitAsync("() => document.getElementById('modelProblems').textContent.includes('only works inside')");
	}

	[Fact]
	public async Task The_canvas_value_follows_the_test_scenario()
	{
		await StartAsync("<p><span class=\"df df-calc\" data-expression=\"policy.premium * 2\"></span></p>");
		await WaitForCanvasAsync("2401");
		var name = await EvalAsync<string>("document.getElementById('templateName').value");
		(await fixture.Http.PutAsJsonAsync($"api/templates/{name}/scenarios/Big", new { data = new { policy = new { premium = 5000 } } })).EnsureSuccessStatusCode();
		await ActAsync("document.getElementById('templateName').dispatchEvent(new Event('change'))");
		await ActAsync("(() => { const s = document.getElementById('scenarioSelect'); s.value = 'Big'; s.dispatchEvent(new Event('change')); })()");
		await WaitForCanvasAsync("10000");
		await ActAsync("(() => { const s = document.getElementById('scenarioSelect'); s.value = ''; s.dispatchEvent(new Event('change')); })()");
		await WaitForCanvasAsync("2401");
	}

	[Fact]
	public async Task Field_display_modes_show_the_expression_and_the_liquid()
	{
		await StartAsync("<p><span class=\"df df-calc\" data-expression=\"policy.fees / 4\"></span></p>");
		await WaitForCanvasAsync("25");
		try
		{
			await Page.EvaluateExpressionAsync("(() => { const m = document.getElementById('fieldMode'); m.value = 'name'; m.dispatchEvent(new Event('change')); })()");
			Assert.Equal("= policy.fees / 4", await EvalAsync<string>($"{First}.view.el.getAttribute('data-full') || {First}.view.el.textContent"));
			await Page.EvaluateExpressionAsync("(() => { const m = document.getElementById('fieldMode'); m.value = 'liquid'; m.dispatchEvent(new Event('change')); })()");
			Assert.Contains("{{ calc_result }}", await EvalAsync<string>($"{First}.view.el.textContent"));
		}
		finally
		{
			await Page.EvaluateExpressionAsync("(() => { const m = document.getElementById('fieldMode'); m.value = 'sample'; m.dispatchEvent(new Event('change')); })()");
		}
	}

	[Fact]
	public async Task Dropping_the_block_opens_the_builder()
	{
		await StartAsync("<p>x</p>");
		await Page.EvaluateExpressionAsync(
			"(() => { const e = grapesjs.editors[0]; const b = e.Blocks.get('calc-field'); const c = e.getWrapper().append(b.get('content'))[0]; e.trigger('block:drag:stop', c, b); })()");
		await WaitAsync("() => !!document.getElementById('calcExpression')");
		Assert.Equal("calc-field", await EvalAsync<string>("grapesjs.editors[0].getSelected().get('type')"));
		await Page.EvaluateExpressionAsync("document.getElementById('calcCancel').click()");
		await WaitAsync("() => document.getElementById('modelProblems').textContent.includes('no calculation yet')");
	}
}
