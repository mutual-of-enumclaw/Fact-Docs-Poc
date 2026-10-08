using PuppeteerSharp;

namespace FaCT.DocDesigner.POC.Tests.Formats;

/// <summary>The Format trait and the Custom format dialog: number builder, dates, languages, masks, export and canvas.</summary>
[Collection("Isolated designer")]
public sealed class FormatDesignerTests(IsolatedDesignerFixture fixture)
{
	private const string Model = """
		{ "policy": { "premium": 1200.5, "fees": 0, "effective": "2026-07-01", "phone": "5551234567", "insured": "Acme Bakery" },
		  "claims": [ { "amount": 10 }, { "amount": 5 } ] }
		""";

	private const string Editor = "grapesjs.editors[0]";
	private const string Field = Editor + ".getWrapper().findType('data-field')[0]";

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

	private async Task StartAsync(string field = "policy.premium")
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
		await Page.EvaluateFunctionAsync(
			"f => { const e = grapesjs.editors[0]; e.select(null); e.setComponents('<p>Value <span class=\"df\" data-field=\"' + f + '\"></span></p>'); e.setStyle(''); e.select(e.getWrapper().findType('data-field')[0]); }",
			field);
		await WaitAsync("() => !!document.querySelector('.format-trait .format-select')");
	}

	private async Task OpenCustomAsync()
	{
		await Page.EvaluateExpressionAsync("(() => { const s = document.querySelector('.format-trait .format-select'); s.value = '__custom'; s.dispatchEvent(new Event('change')); })()");
		await WaitAsync("() => !!document.getElementById('fmtApply')");
	}

	private Task SetAsync(string id, string value) =>
		Page.EvaluateFunctionAsync("(id, v) => { const el = document.getElementById(id); el.value = v; el.dispatchEvent(new Event('input')); el.dispatchEvent(new Event('change')); }", id, value);

	private Task CheckAsync(string id, bool value) =>
		Page.EvaluateFunctionAsync("(id, v) => { const el = document.getElementById(id); el.checked = v; el.dispatchEvent(new Event('change')); }", id, value);

	private Task<string> ValueAsync(string id) => Page.EvaluateFunctionAsync<string>("id => document.getElementById(id).value", id);

	private async Task<string[]> PreviewAsync(int lines)
	{
		await WaitAsync("n => document.querySelectorAll('#fmtPreview .fmt-preview-line').length === n", lines);
		return await EvalAsync<string[]>("[...document.querySelectorAll('#fmtPreview .fmt-preview-line')].map(l => l.textContent)");
	}

	private Task WaitForPreviewAsync(string text) =>
		WaitAsync("t => [...document.querySelectorAll('#fmtPreview .fmt-preview-line')].some(l => l.textContent === t)", text);

	private Task<string> HtmlAsync() => EvalAsync<string>($"{Editor}.getHtml()");

	private Task WaitForCanvasAsync(string text) =>
		WaitAsync($"t => {{ const c = {Field}; return !!c && !!c.view && c.view.el.textContent === t; }}", text);

	// ---- the trait ---------------------------------------------------------------------------------------------

	[Fact]
	public async Task The_format_list_has_the_presets_and_custom()
	{
		await StartAsync();
		var options = await EvalAsync<string[]>("[...document.querySelector('.format-trait .format-select').options].map(o => o.value)");
		Assert.Equal(["", "currency", "dollars", "percent", "number", "decimal", "shortdate", "upcase", "__custom"], options);
		Assert.True(await EvalAsync<bool>("document.querySelector('.format-trait .format-edit').hidden"));
	}

	[Fact]
	public async Task Presets_still_work()
	{
		await StartAsync();
		await Page.EvaluateExpressionAsync("(() => { const s = document.querySelector('.format-trait .format-select'); s.value = 'currency'; s.dispatchEvent(new Event('change')); })()");
		Assert.Equal("currency", await EvalAsync<string>($"{Field}.get('format')"));
		Assert.Contains("{{ policy.premium | currency }}", await HtmlAsync());
		await WaitForCanvasAsync("$1,200.50");
	}

	[Fact]
	public async Task Every_field_kind_uses_the_format_trait()
	{
		await StartAsync();
		Assert.Equal("format", await EvalAsync<string>($"{Field}.getTrait('format').get('type')"));
		await Page.EvaluateExpressionAsync($"{Editor}.getWrapper().append({{ type: 'calc-field' }})");
		Assert.Equal("format", await EvalAsync<string>($"{Editor}.getWrapper().findType('calc-field')[0].getTrait('format').get('type')"));
	}

	// ---- the dialog: numbers ------------------------------------------------------------------------------------

	[Fact]
	public async Task A_new_custom_format_starts_as_a_plain_number()
	{
		await StartAsync();
		await OpenCustomAsync();
		Assert.Equal("Custom format", await EvalAsync<string>("document.querySelector('.gjs-mdl-title').textContent"));
		Assert.Equal("number", await ValueAsync("fmtKind"));
		Assert.Equal("#,##0.00", await ValueAsync("fmtPattern"));
		Assert.Equal("1200.5", await ValueAsync("fmtSample"));
		Assert.Equal(["1200.5 \u2192 1,200.50", "-1200.5 \u2192 -1,200.50", "0 \u2192 0.00"], await PreviewAsync(3));
		Assert.Equal("", await EvalAsync<string>($"{Field}.get('format')"));
	}

	[Fact]
	public async Task The_number_builder_writes_the_pattern()
	{
		await StartAsync();
		await OpenCustomAsync();
		await SetAsync("fmtStyle", "currency");
		Assert.Equal("$#,##0.00", await ValueAsync("fmtPattern"));
		await SetAsync("fmtNegative", "parentheses");
		Assert.Equal("$#,##0.00;($#,##0.00)", await ValueAsync("fmtPattern"));
		await SetAsync("fmtZero", "None");
		Assert.Equal("$#,##0.00;($#,##0.00);'None'", await ValueAsync("fmtPattern"));
		await SetAsync("fmtDecimals", "0");
		await CheckAsync("fmtThousands", false);
		Assert.Equal("$0;($0);'None'", await ValueAsync("fmtPattern"));
		await SetAsync("fmtStyle", "percent");
		await SetAsync("fmtDecimals", "1");
		await SetAsync("fmtZero", "");
		await SetAsync("fmtNegative", "minus");
		Assert.Equal("0.0%", await ValueAsync("fmtPattern"));
		await SetAsync("fmtSample", "0.125");
		await WaitForPreviewAsync("0.125 \u2192 12.5%");
	}

	[Fact]
	public async Task Zero_text_and_parentheses_show_in_the_preview()
	{
		await StartAsync();
		await OpenCustomAsync();
		await SetAsync("fmtStyle", "currency");
		await SetAsync("fmtNegative", "parentheses");
		await SetAsync("fmtZero", "None");
		await WaitForPreviewAsync("0 \u2192 None");
		Assert.Equal(["1200.5 \u2192 $1,200.50", "-1200.5 \u2192 ($1,200.50)", "0 \u2192 None"], await PreviewAsync(3));
	}

	[Fact]
	public async Task Applying_sets_the_format_and_the_export()
	{
		await StartAsync();
		await OpenCustomAsync();
		await SetAsync("fmtStyle", "currency");
		await SetAsync("fmtNegative", "parentheses");
		await SetAsync("fmtZero", "None");
		Assert.Equal("Format: Number $#,##0.00;($#,##0.00);'None'.", await ActAsync("document.getElementById('fmtApply').click()"));
		Assert.Equal("num:en-US|$#,##0.00;($#,##0.00);'None'", await EvalAsync<string>($"{Field}.get('format')"));
		Assert.Contains("{{ policy.premium | format: \"$#,##0.00;($#,##0.00);'None'\" }}", await HtmlAsync());
		Assert.Equal("Number $#,##0.00;($#,##0.00);'None'", await EvalAsync<string>("document.querySelector('.format-trait .format-summary').textContent"));
		Assert.False(await EvalAsync<bool>("document.querySelector('.format-trait .format-edit').hidden"));
		Assert.Equal("__custom", await EvalAsync<string>("document.querySelector('.format-trait .format-select').value"));
		await WaitForCanvasAsync("$1,200.50");
	}

	[Fact]
	public async Task Zero_text_shows_on_the_canvas_for_a_zero_sample()
	{
		await StartAsync("policy.fees");
		await OpenCustomAsync();
		await SetAsync("fmtZero", "Included");
		await ActAsync("document.getElementById('fmtApply').click()");
		await WaitForCanvasAsync("Included");
	}

	[Fact]
	public async Task Reopening_restores_the_builder()
	{
		await StartAsync();
		await Page.EvaluateExpressionAsync($"{Field}.set('format', \"num:en-US|$#,##0.000;($#,##0.000);'Nil'\")");
		await Page.EvaluateExpressionAsync("document.querySelector('.format-trait .format-edit').click()");
		await WaitAsync("() => !!document.getElementById('fmtApply')");
		Assert.Equal(["number", "currency", "3", "parentheses", "Nil"],
			[await ValueAsync("fmtKind"), await ValueAsync("fmtStyle"), await ValueAsync("fmtDecimals"), await ValueAsync("fmtNegative"), await ValueAsync("fmtZero")]);
		Assert.True(await EvalAsync<bool>("document.getElementById('fmtThousands').checked"));
		Assert.Equal("$#,##0.000;($#,##0.000);'Nil'", await ValueAsync("fmtPattern"));
	}

	[Fact]
	public async Task Cancel_keeps_the_format()
	{
		await StartAsync();
		await OpenCustomAsync();
		await SetAsync("fmtStyle", "currency");
		await Page.EvaluateExpressionAsync("document.getElementById('fmtCancel').click()");
		Assert.Equal("", await EvalAsync<string>($"{Field}.get('format')"));
		Assert.Equal("", await EvalAsync<string>("document.querySelector('.format-trait .format-select').value"));
	}

	[Fact]
	public async Task A_preset_replaces_a_custom_format()
	{
		await StartAsync();
		await Page.EvaluateExpressionAsync($"{Field}.set('format', 'num:en-US|0.0')");
		await Page.EvaluateExpressionAsync("(() => { const s = document.querySelector('.format-trait .format-select'); s.value = 'dollars'; s.dispatchEvent(new Event('change')); })()");
		Assert.Equal("dollars", await EvalAsync<string>($"{Field}.get('format')"));
		Assert.Contains("{{ policy.premium | dollars }}", await HtmlAsync());
	}

	[Fact]
	public async Task Typed_patterns_cannot_break_the_template()
	{
		await StartAsync();
		await OpenCustomAsync();
		await SetAsync("fmtPattern", "0\"}} {{ evil }}");
		await ActAsync("document.getElementById('fmtApply').click()");
		var html = await HtmlAsync();
		Assert.Contains("{{ policy.premium | format: \"0  evil \" }}", html);
		Assert.DoesNotContain("{{ evil", html);
	}

	[Fact]
	public async Task An_empty_pattern_is_refused()
	{
		await StartAsync();
		await OpenCustomAsync();
		await SetAsync("fmtPattern", "  ");
		await Page.EvaluateExpressionAsync("document.getElementById('fmtApply').click()");
		await WaitAsync("() => document.getElementById('fmtError').textContent.length > 0");
		Assert.Equal("", await EvalAsync<string>($"{Field}.get('format')"));
	}

	// ---- dates and languages ------------------------------------------------------------------------------------

	[Fact]
	public async Task Date_formats_in_another_language()
	{
		await StartAsync("policy.effective");
		await OpenCustomAsync();
		await SetAsync("fmtKind", "date");
		Assert.Equal("MMMM d, yyyy", await ValueAsync("fmtPattern"));
		await WaitForPreviewAsync("2026-07-01 \u2192 July 1, 2026");
		await SetAsync("fmtDatePreset", "d 'de' MMMM 'de' yyyy");
		await SetAsync("fmtCulture", "es-US");
		await WaitForPreviewAsync("2026-07-01 \u2192 1 de julio de 2026");
		await ActAsync("document.getElementById('fmtApply').click()");
		Assert.Equal("date:es-US|d 'de' MMMM 'de' yyyy", await EvalAsync<string>($"{Field}.get('format')"));
		Assert.Contains("{{ policy.effective | format: \"d 'de' MMMM 'de' yyyy\", \"es-US\" }}", await HtmlAsync());
		await WaitForCanvasAsync("1 de julio de 2026");
	}

	// ---- masks ---------------------------------------------------------------------------------------------------

	[Fact]
	public async Task Masks_for_phone_numbers()
	{
		await StartAsync("policy.phone");
		await OpenCustomAsync();
		await SetAsync("fmtKind", "mask");
		Assert.Equal("(###) ###-####", await ValueAsync("fmtPattern"));
		Assert.True(await EvalAsync<bool>("document.getElementById('fmtCulture').closest('label').hidden"));
		await WaitForPreviewAsync("5551234567 \u2192 (555) 123-4567");
		await SetAsync("fmtMaskPreset", "***-**-####");
		await WaitForPreviewAsync("5551234567 \u2192 5551234567");
		await SetAsync("fmtMaskPreset", "(###) ###-####");
		await ActAsync("document.getElementById('fmtApply').click()");
		Assert.Equal("mask:(###) ###-####", await EvalAsync<string>($"{Field}.get('format')"));
		Assert.Contains("{{ policy.phone | mask: \"(###) ###-####\" }}", await HtmlAsync());
		await WaitForCanvasAsync("(555) 123-4567");
	}

	[Fact]
	public async Task A_mask_without_digits_is_refused()
	{
		await StartAsync("policy.phone");
		await OpenCustomAsync();
		await SetAsync("fmtKind", "mask");
		await SetAsync("fmtMaskPreset", "");
		await SetAsync("fmtPattern", "abc");
		await Page.EvaluateExpressionAsync("document.getElementById('fmtApply').click()");
		await WaitAsync("() => document.getElementById('fmtError').textContent.startsWith('A mask needs')");
		Assert.Equal("", await EvalAsync<string>($"{Field}.get('format')"));
	}

	[Fact]
	public async Task Custom_formats_survive_saving_and_loading_the_project()
	{
		await StartAsync();
		await Page.EvaluateExpressionAsync($"{Field}.set('format', \"num:fr-CA|#,##0.00\")");
		var before = await HtmlAsync();
		Assert.Contains("format: \"#,##0.00\", \"fr-CA\"", before);
		await Page.EvaluateExpressionAsync($"(() => {{ const e = {Editor}; const d = JSON.parse(JSON.stringify(e.getProjectData())); e.setComponents('<p>x</p>'); e.loadProjectData(d); }})()");
		await WaitAsync($"() => !!{Field}");
		Assert.Equal("num:fr-CA|#,##0.00", await EvalAsync<string>($"{Field}.get('format')"));
		Assert.Contains("{{ policy.premium | format: \"#,##0.00\", \"fr-CA\" }}", await HtmlAsync());
	}
}
