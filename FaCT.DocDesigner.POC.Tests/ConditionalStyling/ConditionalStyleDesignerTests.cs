using System.Net.Http.Json;
using System.Text.Json.Nodes;
using PuppeteerSharp;
using UglyToad.PdfPig;

namespace FaCT.DocDesigner.POC.Tests.ConditionalStyling;

/// <summary>The Conditional Styling dialog: rules, export, the canvas preview from test data, lists and round trips.</summary>
[Collection("Isolated designer")]
public sealed class ConditionalStyleDesignerTests(IsolatedDesignerFixture fixture)
{
	private const string Model = """
		{ "policy": { "status": "void", "lossRatio": 1.25, "number": "CPP1" },
		  "claims": [ { "number": "C-1", "open": true }, { "number": "C-2", "open": false } ] }
		""";

	private const string Editor = "grapesjs.editors[0]";
	private const string Note = Editor + ".getWrapper().find('#note')[0]";

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

	private async Task StartAsync(string html = "<p id=\"note\">Policy note</p>")
	{
		await Page.EvaluateExpressionAsync($"{Editor}.Modal.close()");
		if (await EvalAsync<string>("document.getElementById('docKind').value") != "templates")
		{
			await ActAsync("(() => { const k = document.getElementById('docKind'); k.value = 'templates'; k.dispatchEvent(new Event('change')); })()");
		}
		await ActAsync("(() => { const s = document.getElementById('scenarioSelect'); s.value = ''; s.dispatchEvent(new Event('change')); })()");
		await Page.EvaluateExpressionAsync("document.getElementById('btnEditModel').click()");
		await WaitAsync("() => !!document.querySelector('.gjs-mdl-content textarea.model-editor')");
		await Page.EvaluateFunctionAsync("m => document.querySelector('.gjs-mdl-content textarea.model-editor').value = m", Model);
		await ActAsync("document.querySelector('.gjs-mdl-content .model-actions button.primary').click()");
		await Page.EvaluateFunctionAsync("h => { const e = grapesjs.editors[0]; e.select(null); e.setComponents(h); e.setStyle(''); }", html);
	}

	private async Task OpenAsync(string selector = "#note")
	{
		await Page.EvaluateFunctionAsync($"s => {Editor}.select({Editor}.getWrapper().find(s)[0])", selector);
		await Page.EvaluateExpressionAsync("document.getElementById('btnCondStyle').click()");
		await WaitAsync("() => !!document.getElementById('csApply')");
	}

	private Task SetAsync(string id, string value) =>
		Page.EvaluateFunctionAsync("(id, v) => { const el = document.getElementById(id); el.value = v; el.dispatchEvent(new Event('input')); el.dispatchEvent(new Event('change')); }", id, value);

	private async Task RuleAsync(int i, string style, string field, string op, string value = "")
	{
		await SetAsync("csStyle" + i, style);
		await SetAsync("csField" + i, field);
		await SetAsync("csOperator" + i, op);
		await SetAsync("csValue" + i, value);
	}

	private Task<string> HtmlAsync() => EvalAsync<string>($"{Editor}.getHtml()");

	/// <summary>The exported capture for the rules, and that the class attribute prints it.</summary>
	private static void AssertStyled(string html, string rules, string element = "<p id=\"note\" class=\"{{ cs_")
	{
		var capture = System.Text.RegularExpressions.Regex.Match(html, @"\{% capture (cs_\w+) %\}(.*?)\{% endcapture %\}");
		Assert.True(capture.Success, html);
		Assert.Equal(rules, capture.Groups[2].Value);
		Assert.Contains(element, html);
		Assert.Contains("{{ " + capture.Groups[1].Value + " }}", html);
	}

	private Task<string[]> CanvasClassesAsync(string selector = "#note") =>
		Page.EvaluateFunctionAsync<string[]>($"s => [...{Editor}.getWrapper().find(s)[0].view.el.classList]", selector);

	[Fact]
	public async Task The_menu_has_conditional_styling()
	{
		await StartAsync();
		Assert.Equal("Conditional Styling…", await EvalAsync<string>("document.getElementById('btnCondStyle').textContent.trim()"));
		Assert.Equal("Author", await EvalAsync<string>("document.getElementById('btnCondStyle').getAttribute('data-requires')"));
	}

	[Fact]
	public async Task Nothing_selected_explains_what_to_do()
	{
		await StartAsync();
		Assert.Equal("Select an element on the page first, then choose Conditional Styling.",
			await ActAsync($"{Editor}.select(null); document.getElementById('btnCondStyle').click()"));
		Assert.False(await EvalAsync<bool>("!!document.getElementById('csApply')"));
	}

	[Fact]
	public async Task A_new_element_has_no_rules()
	{
		await StartAsync();
		await OpenAsync();
		Assert.Equal("Conditional styling", await EvalAsync<string>("document.querySelector('.gjs-mdl-title').textContent"));
		Assert.Equal(0, await EvalAsync<int>("document.querySelectorAll('.cs-rule').length"));
		Assert.Equal("No rules yet. Click Add Rule.", await EvalAsync<string>("document.querySelector('.cs-none').textContent"));
	}

	[Fact]
	public async Task A_rule_is_exported_as_liquid_in_the_class_attribute()
	{
		await StartAsync();
		await OpenAsync();
		await Page.EvaluateExpressionAsync("document.getElementById('csAdd').click()");
		await RuleAsync(0, "red-text", "policy.status", "eq", "void");
		var status = await ActAsync("document.getElementById('csApply').click()");
		Assert.Equal("Conditional styling: Red text if policy.status equals \"void\".", status);
		AssertStyled(await HtmlAsync(), "{% if policy.status == 'void' %}cs-red-text{% endif %}");
	}

	[Fact]
	public async Task The_canvas_shows_the_style_the_test_data_gives()
	{
		await StartAsync();
		await OpenAsync();
		await Page.EvaluateExpressionAsync("document.getElementById('csAdd').click()");
		await RuleAsync(0, "red-text", "policy.status", "eq", "void");
		await Page.EvaluateExpressionAsync("document.getElementById('csAdd').click()");
		await RuleAsync(1, "bold", "policy.lossRatio", "lt", "1");
		await ActAsync("document.getElementById('csApply').click()");
		var classes = await CanvasClassesAsync();
		Assert.Contains("cs-red-text", classes);
		Assert.DoesNotContain("cs-bold", classes);
		Assert.Contains("Red text if policy.status", await EvalAsync<string>($"{Note}.view.el.getAttribute('data-cond-style')"));
		Assert.Equal("rgb(217, 52, 43)", await EvalAsync<string>($"getComputedStyle({Note}.view.el).color"));
	}

	[Fact]
	public async Task Numbers_compare_as_numbers()
	{
		await StartAsync();
		await OpenAsync();
		await Page.EvaluateExpressionAsync("document.getElementById('csAdd').click()");
		await RuleAsync(0, "highlight-red", "policy.lossRatio", "gt", "1");
		await ActAsync("document.getElementById('csApply').click()");
		AssertStyled(await HtmlAsync(), "{% if policy.lossRatio > 1 %}cs-highlight-red{% endif %}");
		Assert.Contains("cs-highlight-red", await CanvasClassesAsync());
	}

	[Fact]
	public async Task Values_cannot_break_the_attribute_or_the_liquid()
	{
		await StartAsync();
		await OpenAsync();
		await Page.EvaluateExpressionAsync("document.getElementById('csAdd').click()");
		await RuleAsync(0, "bold", "policy.status", "eq", "a\"b' %}{{ x }}<script>");
		await ActAsync("document.getElementById('csApply').click()");
		var html = await HtmlAsync();
		AssertStyled(html, "{% if policy.status == 'ab  x script' %}cs-bold{% endif %}");
		Assert.DoesNotContain("<script", html);
	}

	[Fact]
	public async Task Every_rule_needs_a_field()
	{
		await StartAsync();
		await OpenAsync();
		await Page.EvaluateExpressionAsync("document.getElementById('csAdd').click()");
		await Page.EvaluateExpressionAsync("document.getElementById('csApply').click()");
		Assert.Equal("Choose the field for every rule (or remove the rule).", await EvalAsync<string>("document.getElementById('csError').textContent"));
		Assert.Null(await EvalAsync<object?>($"{Note}.get('condStyles')"));
	}

	[Fact]
	public async Task The_value_box_is_only_for_comparisons()
	{
		await StartAsync();
		await OpenAsync();
		await Page.EvaluateExpressionAsync("document.getElementById('csAdd').click()");
		await SetAsync("csOperator0", "eq");
		Assert.False(await EvalAsync<bool>("document.getElementById('csValue0').closest('label').hidden"));
		await SetAsync("csOperator0", "blank");
		Assert.True(await EvalAsync<bool>("document.getElementById('csValue0').closest('label').hidden"));
	}

	[Fact]
	public async Task Rules_can_be_removed_one_by_one_or_all_at_once()
	{
		await StartAsync();
		await OpenAsync();
		await Page.EvaluateExpressionAsync("document.getElementById('csAdd').click()");
		await RuleAsync(0, "bold", "policy.status", "present");
		await Page.EvaluateExpressionAsync("document.getElementById('csAdd').click()");
		await RuleAsync(1, "italic", "policy.number", "eq", "CPP1");
		await Page.EvaluateExpressionAsync("document.querySelectorAll('.cs-remove')[0].click()");
		Assert.Equal(1, await EvalAsync<int>("document.querySelectorAll('.cs-rule').length"));
		Assert.Equal("policy.number", await EvalAsync<string>("document.getElementById('csField0').value"));
		await ActAsync("document.getElementById('csApply').click()");
		var html = await HtmlAsync();
		Assert.Contains("cs-italic", html);
		Assert.DoesNotContain("cs-bold", html);

		await OpenAsync();
		Assert.Equal(1, await EvalAsync<int>("document.querySelectorAll('.cs-rule').length"));
		Assert.Equal("Conditional styling removed.", await ActAsync("document.getElementById('csClear').click()"));
		Assert.DoesNotContain("{%", await HtmlAsync());
		Assert.DoesNotContain("cs-italic", await CanvasClassesAsync());
		Assert.Null(await EvalAsync<string?>($"{Note}.view.el.getAttribute('data-cond-style')"));
	}

	[Fact]
	public async Task At_most_ten_rules()
	{
		await StartAsync();
		await OpenAsync();
		for (var i = 0; i < 10; i++)
		{
			await Page.EvaluateExpressionAsync("document.getElementById('csAdd').click()");
		}
		Assert.Equal(10, await EvalAsync<int>("document.querySelectorAll('.cs-rule').length"));
		Assert.True(await EvalAsync<bool>("document.getElementById('csAdd').disabled"));
	}

	[Fact]
	public async Task Inside_a_list_the_item_fields_are_offered_and_checked_per_item()
	{
		await StartAsync("<p>Claims</p>");
		await Page.EvaluateExpressionAsync($$"""
			(() => {
				const e = {{Editor}};
				const repeat = e.getWrapper().append({ type: 'repeat' })[0];
				repeat.set('collection', 'claims');
				repeat.set('alias', 'claim');
				repeat.components('<p id="item">Claim</p>');
			})()
			""");
		await OpenAsync("#item");
		await Page.EvaluateExpressionAsync("document.getElementById('csAdd').click()");
		var fields = await EvalAsync<string[]>("[...document.getElementById('csField0').options].map(o => o.value)");
		Assert.Contains("claim.open", fields);
		Assert.Contains("claim.number", fields);
		Assert.Contains("policy.status", fields);
		Assert.DoesNotContain("claims.open", fields);
		await RuleAsync(0, "highlight-yellow", "claim.open", "eq", "true");
		await ActAsync("document.getElementById('csApply').click()");
		var html = await HtmlAsync();
		Assert.Contains("{% for claim in claims %}", html);
		AssertStyled(html, "{% if claim.open == true %}cs-highlight-yellow{% endif %}", "<p id=\"item\" class=\"{{ cs_");
		Assert.Contains("cs-highlight-yellow", await CanvasClassesAsync("#item"));
	}

	[Fact]
	public async Task Rules_survive_saving_and_loading_the_project()
	{
		await StartAsync();
		await Page.EvaluateExpressionAsync($"{Note}.set('condStyles', [{{ field: 'policy.status', operator: 'ne', value: 'active', style: 'strike' }}])");
		var before = await HtmlAsync();
		AssertStyled(before, "{% if policy.status != 'active' %}cs-strike{% endif %}");
		await Page.EvaluateExpressionAsync($"(() => {{ const e = {Editor}; const d = JSON.parse(JSON.stringify(e.getProjectData())); e.setComponents('<p>x</p>'); e.loadProjectData(d); }})()");
		await WaitAsync($"() => !!{Note}");
		AssertStyled(await HtmlAsync(), "{% if policy.status != 'active' %}cs-strike{% endif %}");
		Assert.Contains("cs-strike", await CanvasClassesAsync());
	}

	[Fact]
	public async Task Applying_counts_as_an_unsaved_change()
	{
		await StartAsync();
		var before = await EvalAsync<int>($"{Editor}.getDirtyCount()");
		await Page.EvaluateExpressionAsync($"{Note}.set('condStyles', [{{ field: 'policy.status', operator: 'present', value: '', style: 'bold' }}])");
		Assert.True(await EvalAsync<int>($"{Editor}.getDirtyCount()") > before);
	}

	[Fact]
	public async Task What_the_designer_exports_prints_the_style()
	{
		await StartAsync();
		await Page.EvaluateExpressionAsync($"{Note}.set('condStyles', [{{ field: 'policy.status', operator: 'eq', value: 'void', style: 'red-text' }}])");
		var html = await HtmlAsync();
		using var response = await fixture.Http.PostAsJsonAsync("api/render", new JsonObject
		{
			["html"] = html,
			["css"] = "",
			["data"] = new JsonObject { ["policy"] = new JsonObject { ["status"] = "void" } }
		});
		Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
		using var document = PdfDocument.Open(await response.Content.ReadAsByteArrayAsync());
		var (r, g, b) = document.GetPage(1).Letters.First(l => l.Value == "P").Color.ToRGBValues();
		Assert.Equal((0xD9, 0x34, 0x2B), ((int)Math.Round(r * 255), (int)Math.Round(g * 255), (int)Math.Round(b * 255)));
	}
}
