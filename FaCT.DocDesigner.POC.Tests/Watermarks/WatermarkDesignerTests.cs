using System.Net.Http.Json;
using System.Text.Json.Nodes;
using PuppeteerSharp;
using UglyToad.PdfPig;

namespace FaCT.DocDesigner.POC.Tests.Watermarks;

/// <summary>The Watermark dialog: defaults, presets and custom text, look, conditions, the canvas and export.</summary>
[Collection("Isolated designer")]
public sealed class WatermarkDesignerTests(IsolatedDesignerFixture fixture)
{
	private const string Model = """
		{ "policy": { "number": "CPP1234567", "status": "active", "premium": 1200 } }
		""";

	private const string Editor = "grapesjs.editors[0]";
	private const string Mark = Editor + ".getWrapper().findType('watermark')[0]";

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

	private async Task StartAsync(string html = "<p>Body</p>")
	{
		await Page.EvaluateExpressionAsync($"{Editor}.Modal.close()");
		if (await EvalAsync<string>("document.getElementById('docKind').value") != "templates")
		{
			await ActAsync("(() => { const k = document.getElementById('docKind'); k.value = 'templates'; k.dispatchEvent(new Event('change')); })()");
		}
		await Page.EvaluateExpressionAsync("document.getElementById('btnEditModel').click()");
		await WaitAsync("() => !!document.querySelector('.gjs-mdl-content textarea.model-editor')");
		await Page.EvaluateFunctionAsync("m => document.querySelector('.gjs-mdl-content textarea.model-editor').value = m", Model);
		await ActAsync("document.querySelector('.gjs-mdl-content .model-actions button.primary').click()");
		await Page.EvaluateFunctionAsync("h => { const e = grapesjs.editors[0]; e.select(null); e.setComponents(h); e.setStyle(''); e.setDevice('letter'); }", html);
	}

	private async Task OpenAsync()
	{
		await Page.EvaluateExpressionAsync("document.getElementById('btnWatermark').click()");
		await WaitAsync("() => !!document.getElementById('wmApply')");
	}

	private Task SetAsync(string id, string value) =>
		Page.EvaluateFunctionAsync("(id, v) => { const el = document.getElementById(id); el.value = v; el.dispatchEvent(new Event('input')); el.dispatchEvent(new Event('change')); }", id, value);

	private Task<string> ValueAsync(string id) => Page.EvaluateFunctionAsync<string>("id => document.getElementById(id).value", id);

	/// <summary>Whether the labelled field around a control is hidden.</summary>
	private Task<bool> FieldHiddenAsync(string id) => Page.EvaluateFunctionAsync<bool>("id => document.getElementById(id).closest('label').hidden", id);

	private Task<string> ApplyAsync() => ActAsync("document.getElementById('wmApply').click()");

	private Task<string> HtmlAsync() => EvalAsync<string>($"{Editor}.getHtml()");

	private Task<int> CountAsync() => EvalAsync<int>($"{Editor}.getWrapper().findType('watermark').length");

	private const string DefaultMark =
		"<div class=\"doc-watermark wm-color-grey wm-strength-medium wm-size-medium wm-angle-diagonal\"><span class=\"doc-watermark-text\">DRAFT</span></div>";

	// ---- Opening -------------------------------------------------------------------------------------------------

	[Fact]
	public async Task The_menu_has_a_watermark_item()
	{
		await StartAsync();
		Assert.Equal("Watermark…", await EvalAsync<string>("document.getElementById('btnWatermark').textContent.trim()"));
		Assert.False(await EvalAsync<bool>("document.getElementById('btnWatermark').disabled"));
		Assert.Equal("Author", await EvalAsync<string>("document.getElementById('btnWatermark').getAttribute('data-requires')"));
	}

	[Fact]
	public async Task A_new_watermark_starts_as_a_grey_diagonal_draft_on_every_page()
	{
		await StartAsync();
		await OpenAsync();
		Assert.Equal("Watermark", await EvalAsync<string>("document.querySelector('.gjs-mdl-title').textContent"));
		Assert.Equal("DRAFT", await ValueAsync("wmPreset"));
		Assert.True(await FieldHiddenAsync("wmCustom"));
		Assert.Equal(["grey", "medium", "medium", "diagonal"],
			[await ValueAsync("wmColor"), await ValueAsync("wmStrength"), await ValueAsync("wmSize"), await ValueAsync("wmAngle")]);
		Assert.Equal("always", await ValueAsync("wmWhen"));
		Assert.True(await FieldHiddenAsync("wmField"));
		Assert.True(await FieldHiddenAsync("wmOperator"));
		Assert.True(await FieldHiddenAsync("wmValue"));
		Assert.True(await EvalAsync<bool>("document.getElementById('wmRemove').hidden"));
		Assert.Equal(["DRAFT", "SPECIMEN", "VOID", "COPY", "SAMPLE", "custom"],
			await EvalAsync<string[]>("[...document.getElementById('wmPreset').options].map(o => o.value)"));
	}

	[Fact]
	public async Task Clauses_have_no_watermark()
	{
		await StartAsync();
		await ActAsync("(() => { const k = document.getElementById('docKind'); k.value = 'clauses'; k.dispatchEvent(new Event('change')); })()");
		try
		{
			Assert.True(await EvalAsync<bool>("document.getElementById('btnWatermark').disabled"));
		}
		finally
		{
			await ActAsync("(() => { const k = document.getElementById('docKind'); k.value = 'templates'; k.dispatchEvent(new Event('change')); })()");
		}
		Assert.False(await EvalAsync<bool>("document.getElementById('btnWatermark').disabled"));
	}

	[Fact]
	public async Task Cancel_changes_nothing()
	{
		await StartAsync();
		await OpenAsync();
		await SetAsync("wmPreset", "VOID");
		await Page.EvaluateExpressionAsync("document.getElementById('wmCancel').click()");
		Assert.Equal(0, await CountAsync());
	}

	// ---- Applying ------------------------------------------------------------------------------------------------

	[Fact]
	public async Task Applying_adds_one_watermark_to_the_template()
	{
		await StartAsync();
		await OpenAsync();
		var status = await ApplyAsync();
		Assert.Equal("Watermark: DRAFT, on every page.", status);
		Assert.Equal(1, await CountAsync());
		Assert.Equal("<body><p>Body</p>" + DefaultMark + "</body>", Strip(await HtmlAsync()));
	}

	[Fact]
	public async Task Presets_and_look_are_exported_as_classes()
	{
		await StartAsync();
		await OpenAsync();
		await SetAsync("wmPreset", "VOID");
		await SetAsync("wmColor", "red");
		await SetAsync("wmStrength", "strong");
		await SetAsync("wmSize", "large");
		await SetAsync("wmAngle", "horizontal");
		await ApplyAsync();
		Assert.Contains("<div class=\"doc-watermark wm-color-red wm-strength-strong wm-size-large wm-angle-horizontal\"><span class=\"doc-watermark-text\">VOID</span></div>",
			await HtmlAsync());
	}

	[Fact]
	public async Task Editing_again_changes_the_same_watermark()
	{
		await StartAsync();
		await OpenAsync();
		await ApplyAsync();
		await OpenAsync();
		Assert.False(await EvalAsync<bool>("document.getElementById('wmRemove').hidden"));
		await SetAsync("wmPreset", "SPECIMEN");
		await SetAsync("wmColor", "blue");
		await ApplyAsync();
		Assert.Equal(1, await CountAsync());
		var html = await HtmlAsync();
		Assert.Contains(">SPECIMEN</span>", html);
		Assert.Contains("wm-color-blue", html);
		Assert.DoesNotContain("DRAFT", html);
	}

	[Fact]
	public async Task Other_text_can_be_typed()
	{
		await StartAsync();
		await OpenAsync();
		await SetAsync("wmPreset", "custom");
		Assert.False(await FieldHiddenAsync("wmCustom"));
		await SetAsync("wmCustom", "  Not   for issue ");
		await ApplyAsync();
		Assert.Contains("<span class=\"doc-watermark-text\">Not for issue</span>", await HtmlAsync());

		await OpenAsync();
		Assert.Equal("custom", await ValueAsync("wmPreset"));
		Assert.Equal("Not for issue", await ValueAsync("wmCustom"));
	}

	[Fact]
	public async Task Typed_text_cannot_become_markup_or_liquid()
	{
		await StartAsync();
		await OpenAsync();
		await SetAsync("wmPreset", "custom");
		await SetAsync("wmCustom", "<b>{{ policy.number }}</b>");
		await ApplyAsync();
		var html = await HtmlAsync();
		Assert.Contains("&lt;b&gt;&#123;&#123; policy.number &#125;&#125;&lt;/b&gt;", html);
		Assert.DoesNotContain("<b>", html);
		Assert.DoesNotContain("{{", html);
	}

	[Fact]
	public async Task Other_text_is_required()
	{
		await StartAsync();
		await OpenAsync();
		await SetAsync("wmPreset", "custom");
		await SetAsync("wmCustom", "   ");
		await Page.EvaluateExpressionAsync("document.getElementById('wmApply').click()");
		Assert.Equal("Type the watermark text.", await EvalAsync<string>("document.getElementById('wmError').textContent"));
		Assert.Equal(0, await CountAsync());
	}

	[Fact]
	public async Task Custom_text_is_at_most_40_characters()
	{
		await StartAsync();
		await OpenAsync();
		Assert.Equal(40, await EvalAsync<int>("document.getElementById('wmCustom').maxLength"));
		await Page.EvaluateExpressionAsync($"{Editor}.getWrapper().append({{ type: 'watermark', watermark: {{ text: '{new string('X', 60)}' }} }})");
		Assert.Equal(new string('X', 40), await EvalAsync<string>($"{Mark}.view.el.textContent"));
	}

	[Fact]
	public async Task Remove_takes_the_watermark_off()
	{
		await StartAsync();
		await OpenAsync();
		await ApplyAsync();
		await OpenAsync();
		Assert.Equal("Watermark removed.", await ActAsync("document.getElementById('wmRemove').click()"));
		Assert.Equal(0, await CountAsync());
		Assert.DoesNotContain("doc-watermark", await HtmlAsync());
	}

	[Fact]
	public async Task Applying_counts_as_an_unsaved_change()
	{
		await StartAsync();
		var before = await EvalAsync<int>($"{Editor}.getDirtyCount()");
		await OpenAsync();
		await ApplyAsync();
		var added = await EvalAsync<int>($"{Editor}.getDirtyCount()");
		Assert.True(added > before);
		await OpenAsync();
		await SetAsync("wmPreset", "COPY");
		await ApplyAsync();
		Assert.True(await EvalAsync<int>($"{Editor}.getDirtyCount()") > added);
	}

	// ---- Conditions ----------------------------------------------------------------------------------------------

	[Fact]
	public async Task Only_when_shows_the_condition_fields()
	{
		await StartAsync();
		await OpenAsync();
		await SetAsync("wmWhen", "condition");
		Assert.False(await FieldHiddenAsync("wmField"));
		Assert.False(await FieldHiddenAsync("wmOperator"));
		Assert.True(await FieldHiddenAsync("wmValue"));
		await SetAsync("wmOperator", "eq");
		Assert.False(await FieldHiddenAsync("wmValue"));
		await SetAsync("wmOperator", "blank");
		Assert.True(await FieldHiddenAsync("wmValue"));
		var fields = await EvalAsync<string[]>("[...document.getElementById('wmField').options].map(o => o.value)");
		Assert.Contains("policy.status", fields);
		Assert.Contains("policy.premium", fields);
	}

	[Fact]
	public async Task A_condition_needs_a_field()
	{
		await StartAsync();
		await OpenAsync();
		await SetAsync("wmWhen", "condition");
		await Page.EvaluateExpressionAsync("document.getElementById('wmApply').click()");
		Assert.Equal("Choose the field the watermark depends on.", await EvalAsync<string>("document.getElementById('wmError').textContent"));
		Assert.Equal(0, await CountAsync());
	}

	[Theory]
	[InlineData("eq", "void", "{% if policy.status == \"void\" %}")]
	[InlineData("ne", "active", "{% if policy.status != \"active\" %}")]
	[InlineData("present", "", "{% if policy.status != blank %}")]
	[InlineData("blank", "", "{% if policy.status == blank %}")]
	[InlineData("contains", "can\"cel{", "{% if policy.status contains \"cancel\" %}")]
	public async Task A_condition_wraps_the_watermark_in_liquid(string op, string value, string expected)
	{
		await StartAsync();
		await OpenAsync();
		await SetAsync("wmPreset", "VOID");
		await SetAsync("wmWhen", "condition");
		await SetAsync("wmField", "policy.status");
		await SetAsync("wmOperator", op);
		await SetAsync("wmValue", value);
		var status = await ApplyAsync();
		Assert.StartsWith("Watermark: VOID, when policy.status", status);
		var html = await HtmlAsync();
		Assert.Contains(expected + "<div class=\"doc-watermark ", html);
		Assert.EndsWith("</div>{% endif %}</body>", Strip(html));
	}

	[Fact]
	public async Task Number_fields_compare_as_numbers()
	{
		await StartAsync();
		await OpenAsync();
		await SetAsync("wmWhen", "condition");
		await SetAsync("wmField", "policy.premium");
		await SetAsync("wmOperator", "gt");
		await SetAsync("wmValue", "1000");
		await ApplyAsync();
		Assert.Contains("{% if policy.premium > 1000 %}", await HtmlAsync());
	}

	[Fact]
	public async Task Switching_back_to_every_document_drops_the_condition()
	{
		await StartAsync();
		await OpenAsync();
		await SetAsync("wmWhen", "condition");
		await SetAsync("wmField", "policy.status");
		await ApplyAsync();
		await OpenAsync();
		Assert.Equal("condition", await ValueAsync("wmWhen"));
		Assert.Equal("policy.status", await ValueAsync("wmField"));
		await SetAsync("wmWhen", "always");
		await ApplyAsync();
		Assert.DoesNotContain("{%", await HtmlAsync());
	}

	// ---- Canvas --------------------------------------------------------------------------------------------------

	[Fact]
	public async Task The_canvas_shows_the_watermark_without_getting_in_the_way()
	{
		await StartAsync();
		await OpenAsync();
		await SetAsync("wmPreset", "SPECIMEN");
		await ApplyAsync();
		Assert.Equal("SPECIMEN", await EvalAsync<string>($"{Mark}.view.el.textContent"));
		var style = await EvalAsync<string[]>($"(() => {{ const el = {Mark}.view.el; const s = el.ownerDocument.defaultView.getComputedStyle(el); return [s.position, s.pointerEvents]; }})()");
		Assert.Equal(["fixed", "none"], style);
		Assert.False(await EvalAsync<bool>($"{Mark}.get('selectable')"));
		Assert.False(await EvalAsync<bool>($"{Mark}.get('layerable')"));
		Assert.False(await EvalAsync<bool>($"{Mark}.get('draggable')"));
	}

	[Fact]
	public async Task The_canvas_updates_when_the_watermark_changes()
	{
		await StartAsync();
		await OpenAsync();
		await ApplyAsync();
		await OpenAsync();
		await SetAsync("wmPreset", "VOID");
		await SetAsync("wmWhen", "condition");
		await SetAsync("wmField", "policy.status");
		await ApplyAsync();
		await WaitAsync($"() => {Mark}.view.el.textContent === 'VOID'");
		Assert.Contains("policy.status", await EvalAsync<string>($"{Mark}.view.el.title"));
	}

	[Fact]
	public async Task Clicks_on_the_page_reach_the_content_under_the_watermark()
	{
		await StartAsync("<p id=\"under\" style=\"margin-top:300px\">Under the mark</p>");
		await OpenAsync();
		await SetAsync("wmSize", "large");
		await SetAsync("wmAngle", "horizontal");
		await ApplyAsync();
		var hit = await EvalAsync<string>(
			$"(() => {{ const doc = {Editor}.Canvas.getDocument(); const p = doc.getElementById('under'); const r = p.getBoundingClientRect(); " +
			"const el = doc.elementFromPoint(r.left + 5, r.top + r.height / 2); return el ? el.id : ''; })()");
		Assert.Equal("under", hit);
	}

	// ---- Round trips ---------------------------------------------------------------------------------------------

	[Fact]
	public async Task The_watermark_survives_saving_and_loading_the_project()
	{
		await StartAsync();
		await OpenAsync();
		await SetAsync("wmPreset", "VOID");
		await SetAsync("wmColor", "red");
		await SetAsync("wmWhen", "condition");
		await SetAsync("wmField", "policy.status");
		await SetAsync("wmOperator", "eq");
		await SetAsync("wmValue", "void");
		await ApplyAsync();
		var before = await HtmlAsync();

		await Page.EvaluateExpressionAsync($"(() => {{ const e = {Editor}; const d = JSON.parse(JSON.stringify(e.getProjectData())); e.setComponents('<p>x</p>'); e.loadProjectData(d); }})()");
		await WaitAsync($"() => {Editor}.getWrapper().findType('watermark').length === 1");
		Assert.Equal(before, await HtmlAsync());
		await OpenAsync();
		Assert.Equal(["VOID", "red", "condition", "policy.status", "eq", "void"],
			[await ValueAsync("wmPreset"), await ValueAsync("wmColor"), await ValueAsync("wmWhen"), await ValueAsync("wmField"), await ValueAsync("wmOperator"), await ValueAsync("wmValue")]);
	}

	[Fact]
	public async Task Exported_markup_is_read_back_into_a_watermark()
	{
		await StartAsync("<p>Body</p>" + WatermarkRenderTests.Mark("COPY", "green", "light", "small", "horizontal"));
		Assert.Equal(1, await CountAsync());
		await OpenAsync();
		Assert.Equal(["COPY", "green", "light", "small", "horizontal"],
			[await ValueAsync("wmPreset"), await ValueAsync("wmColor"), await ValueAsync("wmStrength"), await ValueAsync("wmSize"), await ValueAsync("wmAngle")]);
	}

	[Fact]
	public async Task Unknown_classes_fall_back_to_the_defaults()
	{
		await StartAsync("<div class=\"doc-watermark wm-color-pink wm-size-huge\"><span class=\"doc-watermark-text\">Proof</span></div>");
		Assert.Contains("<div class=\"doc-watermark wm-color-grey wm-strength-medium wm-size-medium wm-angle-diagonal\"><span class=\"doc-watermark-text\">Proof</span></div>",
			await HtmlAsync());
	}

	[Fact]
	public async Task A_render_time_stamp_is_not_a_template_watermark()
	{
		await StartAsync("<p>Body</p><div class=\"doc-watermark doc-watermark-stamp\"><span class=\"doc-watermark-text\">DRAFT</span></div>");
		Assert.Equal(0, await CountAsync());
	}

	// ---- End to end ----------------------------------------------------------------------------------------------

	[Fact]
	public async Task What_the_designer_exports_prints_the_watermark_when_the_data_says_so()
	{
		await StartAsync("<p>Designer body</p>");
		await OpenAsync();
		await SetAsync("wmPreset", "VOID");
		await SetAsync("wmWhen", "condition");
		await SetAsync("wmField", "policy.status");
		await SetAsync("wmOperator", "eq");
		await SetAsync("wmValue", "void");
		await ApplyAsync();
		var html = await HtmlAsync();

		async Task<string> LettersAsync(string status)
		{
			using var response = await fixture.Http.PostAsJsonAsync("api/render", new JsonObject
			{
				["html"] = html,
				["css"] = "",
				["data"] = new JsonObject { ["policy"] = new JsonObject { ["status"] = status } }
			});
			Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
			using var document = PdfDocument.Open(await response.Content.ReadAsByteArrayAsync());
			return string.Concat(document.GetPage(1).Letters.Select(l => l.Value));
		}

		var voided = await LettersAsync("void");
		Assert.Contains("VOID", voided);
		Assert.Contains("Designer body", voided);
		Assert.DoesNotContain("VOID", await LettersAsync("active"));
	}

	/// <summary>The exported HTML without GrapesJS ids.</summary>
	private static string Strip(string html) => System.Text.RegularExpressions.Regex.Replace(html, " id=\"[^\"]*\"", "");
}
