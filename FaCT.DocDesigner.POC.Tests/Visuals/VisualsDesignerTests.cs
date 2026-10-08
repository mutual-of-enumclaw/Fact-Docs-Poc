using System.Net.Http.Json;
using System.Text.Json.Nodes;
using PuppeteerSharp;
using ZXing;

namespace FaCT.DocDesigner.POC.Tests.Visuals;

/// <summary>Barcode / QR Code, Chart and Signature blocks in the designer: export, settings, canvas previews.</summary>
[Collection("Isolated designer")]
public sealed class VisualsDesignerTests(IsolatedDesignerFixture fixture)
{
	private const string Model = """
		{ "policy": { "number": "CPP1234567", "signer": "Pat Smith", "role": "Owner", "signed": "2026-07-01",
		              "signature": "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==" },
		  "losses": [ { "year": "2023", "amount": 1000 }, { "year": "2024", "amount": 2500 }, { "year": "2025", "amount": 500 } ],
		  "vehicles": [ { "vin": "1HGCM82633A004352", "value": 30000, "make": "Honda" } ] }
		""";

	private const string Editor = "grapesjs.editors[0]";

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

	private async Task StartAsync()
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
		await Page.EvaluateExpressionAsync($"(() => {{ const e = {Editor}; e.select(null); e.setComponents('<p>Start</p>'); e.setStyle(''); }})()");
	}

	/// <summary>Appends a component and returns the script expression for it.</summary>
	private async Task<string> AddAsync(string json)
	{
		await Page.EvaluateExpressionAsync($"{Editor}.getWrapper().append({json})");
		return $"{Editor}.getWrapper().components().last()";
	}

	private Task<string> HtmlAsync() => EvalAsync<string>($"{Editor}.getHtml()");

	[Fact]
	public async Task The_blocks_are_in_the_data_category()
	{
		await StartAsync();
		foreach (var (id, label) in new[] { ("barcode", "Barcode / QR Code"), ("chart", "Chart"), ("signature", "Signature") })
		{
			Assert.Equal(label, await EvalAsync<string>($"{Editor}.Blocks.get('{id}').get('label')"));
			Assert.Equal("Data", await EvalAsync<string>($"(() => {{ const c = {Editor}.Blocks.get('{id}').get('category'); return typeof c === 'string' ? c : c.id || c.get('id'); }})()"));
		}
	}

	// ---- barcode ---------------------------------------------------------------------------------------------------

	[Fact]
	public async Task A_qr_code_exports_the_barcode_filter_and_previews_on_the_canvas()
	{
		await StartAsync();
		var bc = await AddAsync("{ type: 'barcode', field: 'policy.number' }");
		var html = await HtmlAsync();
		Assert.Matches("\\{% if policy.number != blank %\\}<span class=\"doc-barcode doc-barcode-2d\" id=\"[^\"]+\">\\{\\{ policy.number \\| barcode: \"qr\" \\}\\}</span>\\{% endif %\\}", html);
		Assert.Contains("width:1in", await EvalAsync<string>($"{Editor}.getCss()"));
		await WaitAsync($"() => !!{bc}.view.el.querySelector('svg.doc-code-qr path')");
	}

	[Fact]
	public async Task Linear_codes_get_a_bar_height_and_the_value_underneath()
	{
		await StartAsync();
		var bc = await AddAsync("{ type: 'barcode', field: 'policy.number', kind: 'code128', size: 3, height: 'l' }");
		var html = await HtmlAsync();
		Assert.Contains("class=\"doc-barcode doc-barcode-1d doc-barcode-h-l\"", html);
		Assert.Contains("{{ policy.number | barcode: \"code128\" }}<span class=\"doc-barcode-text\">{{ policy.number }}</span>", html);
		Assert.Contains("width:3in", await EvalAsync<string>($"{Editor}.getCss()"));
		await WaitAsync($"() => !!{bc}.view.el.querySelector('svg.doc-code-code128')");

		await Page.EvaluateExpressionAsync($"{bc}.set('showText', false)");
		Assert.DoesNotContain("doc-barcode-text", await HtmlAsync());
	}

	[Fact]
	public async Task Changing_the_kind_redraws_the_preview()
	{
		await StartAsync();
		var bc = await AddAsync("{ type: 'barcode', field: 'policy.number' }");
		await WaitAsync($"() => !!{bc}.view.el.querySelector('svg.doc-code-qr')");
		await Page.EvaluateExpressionAsync($"{bc}.set('kind', 'datamatrix')");
		await WaitAsync($"() => !!{bc}.view.el.querySelector('svg.doc-code-datamatrix')");
		Assert.Contains("barcode: \"datamatrix\"", await HtmlAsync());
	}

	[Fact]
	public async Task Unknown_kinds_and_bad_sizes_fall_back()
	{
		await StartAsync();
		await AddAsync("{ type: 'barcode', field: 'policy.number', kind: 'upc', size: 99 }");
		Assert.Contains("barcode: \"qr\"", await HtmlAsync());
		Assert.Contains("width:7.5in", await EvalAsync<string>($"{Editor}.getCss()"));
	}

	[Fact]
	public async Task A_barcode_with_no_field_exports_nothing_inside()
	{
		await StartAsync();
		var bc = await AddAsync("{ type: 'barcode', field: '' }");
		Assert.DoesNotContain("barcode:", await HtmlAsync());
		await WaitAsync($"() => {bc}.view.el.textContent.includes('choose what to show')");
	}

	// ---- chart -----------------------------------------------------------------------------------------------------

	[Fact]
	public async Task A_new_chart_picks_a_list_with_numbers()
	{
		await StartAsync();
		var chart = await AddAsync("{ type: 'chart' }");
		Assert.Equal("losses", await EvalAsync<string>($"{chart}.get('list')"));
		Assert.Equal("year", await EvalAsync<string>($"{chart}.get('label')"));
		Assert.Equal("amount", await EvalAsync<string>($"{chart}.get('value')"));
		Assert.Contains("<div class=\"doc-chart\">", await HtmlAsync());
		Assert.Contains("{{ losses | chart: \"column\", \"year\", \"amount\" }}", await HtmlAsync());
		await WaitAsync($"() => {chart}.view.el.querySelectorAll('path.chart-bar').length === 3");
	}

	[Fact]
	public async Task Chart_settings_change_the_export_and_the_preview()
	{
		await StartAsync();
		var chart = await AddAsync("{ type: 'chart', list: 'losses', label: 'year', value: 'amount' }");
		await Page.EvaluateExpressionAsync($"{chart}.set({{ chartType: 'pie', format: 'currency', title: 'Losses <by> year' }})");
		var html = await HtmlAsync();
		Assert.Contains("<div class=\"doc-chart-title\">Losses &lt;by&gt; year</div>{{ losses | chart: \"pie\", \"year\", \"amount\", \"currency\" }}", html);
		await WaitAsync($"() => {chart}.view.el.querySelectorAll('path.chart-slice').length === 3");
		Assert.Equal("Losses <by> year", await EvalAsync<string>($"{chart}.view.el.querySelector('.doc-chart-title').textContent"));
	}

	[Fact]
	public async Task Choosing_another_list_picks_its_fields()
	{
		await StartAsync();
		var chart = await AddAsync("{ type: 'chart', list: 'losses', label: 'year', value: 'amount' }");
		await Page.EvaluateExpressionAsync($"{chart}.set('list', 'vehicles')");
		Assert.Equal("vin", await EvalAsync<string>($"{chart}.get('label')"));
		Assert.Equal("value", await EvalAsync<string>($"{chart}.get('value')"));
		Assert.Contains("{{ vehicles | chart: \"column\", \"vin\", \"value\" }}", await HtmlAsync());
	}

	[Fact]
	public async Task The_chart_settings_offer_the_lists_and_their_fields()
	{
		await StartAsync();
		var chart = await AddAsync("{ type: 'chart', list: 'losses', label: 'year', value: 'amount' }");
		await Page.EvaluateExpressionAsync($"{Editor}.select({chart})");
		await WaitAsync("() => !!document.querySelector('select.list-select') && !!document.querySelector('select.chart-field')");
		Assert.Equal(["losses", "vehicles"], await EvalAsync<string[]>("[...document.querySelector('select.list-select').options].map(o => o.value)"));
		var fieldSelects = await EvalAsync<string[][]>("[...document.querySelectorAll('select.chart-field')].map(s => [...s.options].map(o => o.value))");
		Assert.Equal(["year", "amount"], fieldSelects[0]);
		Assert.Equal(["amount"], fieldSelects[1]);
	}

	// ---- signature -------------------------------------------------------------------------------------------------

	[Fact]
	public async Task A_new_signature_block_has_a_line_an_anchor_a_role_and_a_date_line()
	{
		await StartAsync();
		var sig = await AddAsync("{ type: 'signature-block' }");
		var html = await HtmlAsync();
		Assert.Contains("<div class=\"doc-signature-line\"><span class=\"doc-esign-anchor\">\\s1\\</span></div>", html);
		Assert.Contains("<div class=\"doc-signature-label\">Authorized Representative</div>", html);
		Assert.Contains("<div class=\"doc-signature-date\">Date: ____________________</div>", html);
		await WaitAsync($"() => {sig}.view.el.textContent.includes('Authorized Representative')");
	}

	[Fact]
	public async Task Signature_fields_and_dates()
	{
		await StartAsync();
		var sig = await AddAsync("{ type: 'signature-block', nameField: 'policy.signer', titleField: 'policy.role', label: 'Insured', dateMode: 'field', dateField: 'policy.signed', imageField: 'policy.signature', anchor: '' }");
		var html = await HtmlAsync();
		Assert.Contains("{% if policy.signature != blank %}<img class=\"doc-signature-image\" src=\"{{ policy.signature }}\" alt=\"Signature\">{% endif %}", html);
		Assert.Contains("<div class=\"doc-signature-line\"></div>", html);
		Assert.Contains("<div class=\"doc-signature-name\">{{ policy.signer }}</div><div class=\"doc-signature-title\">{{ policy.role }}</div>", html);
		Assert.Contains("<div class=\"doc-signature-date\">Date: {{ policy.signed | shortdate }}</div>", html);
		await WaitAsync($"() => {sig}.view.el.textContent.includes('Pat Smith') && {sig}.view.el.textContent.includes('07/01/2026')");

		await Page.EvaluateExpressionAsync($"{sig}.set('dateMode', 'today')");
		Assert.Contains("Date: {{ 'now' | date: '%m/%d/%Y' }}", await HtmlAsync());
		await Page.EvaluateExpressionAsync($"{sig}.set('dateMode', 'none')");
		Assert.DoesNotContain("doc-signature-date", await HtmlAsync());
	}

	[Fact]
	public async Task Signature_text_cannot_become_markup_or_liquid()
	{
		await StartAsync();
		await AddAsync("{ type: 'signature-block', label: '<b>{{ x }}</b>', anchor: '\"><script>', nameField: 'bad path!' }");
		var html = await HtmlAsync();
		Assert.Contains("&lt;b&gt;&#123;&#123; x &#125;&#125;&lt;/b&gt;", html);
		Assert.DoesNotContain("<script", html);
		Assert.DoesNotContain("bad path", html);
	}

	[Fact]
	public async Task The_signature_settings_offer_fields_by_kind()
	{
		await StartAsync();
		var sig = await AddAsync("{ type: 'signature-block' }");
		await Page.EvaluateExpressionAsync($"{Editor}.select({sig})");
		await WaitAsync("() => document.querySelectorAll('select.path-select').length === 4");
		var options = await EvalAsync<string[][]>("[...document.querySelectorAll('select.path-select')].map(s => [...s.options].map(o => o.value))");
		Assert.Contains("policy.signer", options[0]);
		Assert.DoesNotContain("policy.signature", options[0]);
		Assert.Contains("policy.signed", options[2]);
		Assert.DoesNotContain("policy.signer", options[2]);
		Assert.Equal(["", "policy.signature"], options[3]);
	}

	// ---- end to end ----------------------------------------------------------------------------------------------

	[Fact]
	public async Task What_the_designer_exports_prints_a_scannable_code_a_chart_and_a_signature()
	{
		await StartAsync();
		await Page.EvaluateExpressionAsync($"{Editor}.setComponents('')");
		await AddAsync("{ type: 'barcode', field: 'policy.number', size: 1.5 }");
		var html = await HtmlAsync();
		var css = await EvalAsync<string>($"{Editor}.getCss()");
		var data = JsonNode.Parse(Model);
		using (var response = await fixture.Http.PostAsJsonAsync("api/render", new JsonObject
		{
			["html"] = PageSetup.Setup.Markup(inner: PageSetup.Setup.Slots("footer", "default")) + html,
			["css"] = css,
			["data"] = data!.DeepClone()
		}))
		{
			Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
			Assert.Equal("CPP1234567", BarcodeReader.Read(await response.Content.ReadAsByteArrayAsync(), BarcodeFormat.QR_CODE));
		}

		await Page.EvaluateExpressionAsync($"{Editor}.setComponents('')");
		await AddAsync("{ type: 'chart', list: 'losses', label: 'year', value: 'amount', format: 'dollars', title: 'Losses by year' }");
		await AddAsync("{ type: 'signature-block', nameField: 'policy.signer', label: 'Insured' }");
		using var second = await fixture.Http.PostAsJsonAsync("api/render", new JsonObject
		{
			["html"] = await HtmlAsync(),
			["css"] = await EvalAsync<string>($"{Editor}.getCss()"),
			["data"] = data.DeepClone()
		});
		Assert.True(second.IsSuccessStatusCode, await second.Content.ReadAsStringAsync());
		var text = System.Text.RegularExpressions.Regex.Replace(PdfText.Extract(await second.Content.ReadAsByteArrayAsync()), @"\s+", " ");
		foreach (var expected in new[] { "Losses by year", "2024", "$2,500", "Pat Smith", "Insured" })
		{
			Assert.Contains(expected, text);
		}
	}
}
