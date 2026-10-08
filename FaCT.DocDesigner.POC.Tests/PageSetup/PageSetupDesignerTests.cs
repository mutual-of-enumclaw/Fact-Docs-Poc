using System.Net.Http.Json;
using System.Text.Json.Nodes;
using PuppeteerSharp;
using UglyToad.PdfPig;

namespace FaCT.DocDesigner.POC.Tests.PageSetup;

/// <summary>The Page Setup dialog: defaults, applying, tokens, variants, validation, the canvas width and export.</summary>
[Collection("Isolated designer")]
public sealed class PageSetupDesignerTests(IsolatedDesignerFixture fixture)
{
	private const string Model = """
		{ "policy": { "number": "CPP1234567", "insured": "Acme Bakery" } }
		""";

	private const string Editor = "grapesjs.editors[0]";
	private const string SetupComponent = Editor + ".getWrapper().findType('page-setup')[0]";

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

	/// <summary>A fresh template canvas with the test model and the given HTML.</summary>
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
		await Page.EvaluateExpressionAsync("document.getElementById('btnPageSetup').click()");
		await WaitAsync("() => !!document.getElementById('psApply')");
	}

	private Task SetAsync(string id, string value) =>
		Page.EvaluateFunctionAsync("(id, v) => { const el = document.getElementById(id); el.value = v; el.dispatchEvent(new Event('input')); el.dispatchEvent(new Event('change')); }", id, value);

	private Task CheckAsync(string id, bool value) =>
		Page.EvaluateFunctionAsync("(id, v) => { const el = document.getElementById(id); el.checked = v; el.dispatchEvent(new Event('change')); }", id, value);

	private Task<string> ValueAsync(string id) => Page.EvaluateFunctionAsync<string>("id => document.getElementById(id).value", id);

	private Task<bool> HiddenAsync(string id) => Page.EvaluateFunctionAsync<bool>("id => document.getElementById(id).hidden", id);

	private Task<string> ApplyAsync() => ActAsync("document.getElementById('psApply').click()");

	private Task<string> HtmlAsync() => EvalAsync<string>($"{Editor}.getHtml()");

	private Task<int> SetupCountAsync() => EvalAsync<int>($"{Editor}.getWrapper().findType('page-setup').length");

	private Task<string> DeviceWidthAsync() => EvalAsync<string>($"{Editor}.Devices.getSelected().get('width')");

	// ---- Opening -------------------------------------------------------------------------------------------------

	[Fact]
	public async Task The_menu_has_a_page_setup_item()
	{
		await StartAsync();
		Assert.Equal("Page Setup…", await EvalAsync<string>("document.getElementById('btnPageSetup').textContent.trim()"));
		Assert.False(await EvalAsync<bool>("document.getElementById('btnPageSetup').disabled"));
	}

	[Fact]
	public async Task A_template_without_a_setup_opens_with_the_standard_page()
	{
		await StartAsync();
		await OpenAsync();
		Assert.Equal("Page setup", await EvalAsync<string>("document.querySelector('.gjs-mdl-title').textContent"));
		Assert.Equal("letter", await ValueAsync("psSize"));
		Assert.Equal("portrait", await ValueAsync("psOrientation"));
		Assert.Equal(["0.5", "0.5", "0.6", "0.5"], [await ValueAsync("psMarginTop"), await ValueAsync("psMarginRight"), await ValueAsync("psMarginBottom"), await ValueAsync("psMarginLeft")]);
		Assert.Equal("10", await ValueAsync("psFontSize"));
		Assert.Equal("Mutual Of Enumclaw", await ValueAsync("psFooterLeft"));
		Assert.Equal("", await ValueAsync("psFooterCenter"));
		Assert.Equal("Page [Page] of [Pages]", await ValueAsync("psFooterRight"));
		Assert.Equal("", await ValueAsync("psHeaderLeft"));
		Assert.True(await HiddenAsync("psReset"));
		Assert.True(await HiddenAsync("psFirstHeaderLeft"));
		Assert.True(await HiddenAsync("psEvenFooterRight"));
		Assert.Contains("no page setup yet", await EvalAsync<string>("document.querySelector('.page-setup-dialog .model-help').textContent"));
		Assert.Equal(0, await SetupCountAsync());
	}

	[Fact]
	public async Task Cancel_changes_nothing()
	{
		await StartAsync();
		await OpenAsync();
		await SetAsync("psSize", "legal");
		await Page.EvaluateExpressionAsync("document.getElementById('psCancel').click()");
		Assert.Equal(0, await SetupCountAsync());
		Assert.False(await EvalAsync<bool>("!!document.querySelector('.page-setup-dialog')"));
	}

	[Fact]
	public async Task The_paper_sizes_offered()
	{
		await StartAsync();
		await OpenAsync();
		Assert.Equal(["letter", "legal", "a4"], await EvalAsync<string[]>("[...document.getElementById('psSize').options].map(o => o.value)"));
		Assert.Equal(["portrait", "landscape"], await EvalAsync<string[]>("[...document.getElementById('psOrientation').options].map(o => o.value)"));
	}

	// ---- Applying ------------------------------------------------------------------------------------------------

	[Fact]
	public async Task Applying_adds_one_hidden_setup_that_exports_its_settings()
	{
		await StartAsync();
		await OpenAsync();
		await SetAsync("psSize", "legal");
		await SetAsync("psOrientation", "landscape");
		await SetAsync("psMarginTop", "1");
		await SetAsync("psMarginLeft", "0.75");
		await SetAsync("psFontSize", "9");
		await SetAsync("psHeaderLeft", "Declarations");
		var status = await ApplyAsync();
		Assert.Equal("Page setup: Legal (8.5 × 14 in), landscape. Preview to see the header and footer.", status);
		Assert.Equal(1, await SetupCountAsync());
		Assert.Equal(0, await EvalAsync<int>($"{Editor}.getWrapper().components().indexOf({SetupComponent})"));

		var html = await HtmlAsync();
		Assert.Contains("class=\"doc-setup ds-size-legal ds-orient-landscape ds-margins-1_0.5_0.6_0.75 ds-font-9\"", html);
		Assert.Contains("<div class=\"doc-hf doc-hf-header doc-hf-default\"><span class=\"doc-hf-left\">Declarations</span><span class=\"doc-hf-center\"></span><span class=\"doc-hf-right\"></span></div>", html);
		Assert.Contains("<div class=\"doc-hf doc-hf-footer doc-hf-default\"><span class=\"doc-hf-left\">Mutual Of Enumclaw</span><span class=\"doc-hf-center\"></span><span class=\"doc-hf-right\">Page <span class=\"doc-pageno\"></span> of <span class=\"doc-pagecount\"></span></span></div>", html);
		Assert.DoesNotContain("doc-hf-first", html);
		Assert.DoesNotContain("doc-hf-even", html);
		Assert.True(html.IndexOf("doc-setup", StringComparison.Ordinal) < html.IndexOf("Body", StringComparison.Ordinal));
	}

	[Fact]
	public async Task Applying_again_updates_the_same_setup()
	{
		await StartAsync();
		await OpenAsync();
		await ApplyAsync();
		await OpenAsync();
		Assert.False(await HiddenAsync("psReset"));
		Assert.DoesNotContain("no page setup yet", await EvalAsync<string>("document.querySelector('.page-setup-dialog .model-help').textContent"));
		await SetAsync("psSize", "a4");
		await SetAsync("psFooterLeft", "Changed");
		await ApplyAsync();
		Assert.Equal(1, await SetupCountAsync());
		var html = await HtmlAsync();
		Assert.Contains("ds-size-a4", html);
		Assert.DoesNotContain("ds-size-letter", html);
		Assert.Contains("<span class=\"doc-hf-left\">Changed</span>", html);
	}

	[Fact]
	public async Task The_dialog_reopens_with_the_applied_values()
	{
		await StartAsync();
		await OpenAsync();
		await SetAsync("psOrientation", "landscape");
		await SetAsync("psMarginBottom", "1.25");
		await SetAsync("psHeaderCenter", "Policy {policy.number}");
		await CheckAsync("psDifferentFirst", true);
		await SetAsync("psFirstFooterCenter", "Cover");
		await ApplyAsync();
		await OpenAsync();
		Assert.Equal("landscape", await ValueAsync("psOrientation"));
		Assert.Equal("1.25", await ValueAsync("psMarginBottom"));
		Assert.Equal("Policy {policy.number}", await ValueAsync("psHeaderCenter"));
		Assert.True(await EvalAsync<bool>("document.getElementById('psDifferentFirst').checked"));
		Assert.False(await HiddenAsync("psFirstFooterCenter"));
		Assert.Equal("Cover", await ValueAsync("psFirstFooterCenter"));
	}

	[Fact]
	public async Task Applying_counts_as_an_unsaved_change()
	{
		await StartAsync();
		await Page.EvaluateExpressionAsync($"{Editor}.getModel().set('changesCount', 0)");
		await OpenAsync();
		await ApplyAsync();
		Assert.True(await EvalAsync<int>($"{Editor}.getDirtyCount()") > 0);
		await Page.EvaluateExpressionAsync($"{Editor}.getModel().set('changesCount', 0)");
		await OpenAsync();
		await SetAsync("psFontSize", "12");
		await ApplyAsync();
		Assert.True(await EvalAsync<int>($"{Editor}.getDirtyCount()") > 0);
	}

	[Fact]
	public async Task The_setup_is_invisible_and_cannot_be_selected_or_moved()
	{
		await StartAsync();
		await OpenAsync();
		await SetAsync("psHeaderLeft", "Hidden header text");
		await ApplyAsync();
		Assert.Equal("none", await EvalAsync<string>($"getComputedStyle({SetupComponent}.view.el).display"));
		Assert.Equal("", await EvalAsync<string>($"{SetupComponent}.view.el.textContent"));
		Assert.False(await EvalAsync<bool>($"{SetupComponent}.get('selectable')"));
		Assert.False(await EvalAsync<bool>($"{SetupComponent}.get('layerable')"));
		Assert.False(await EvalAsync<bool>($"{SetupComponent}.get('draggable')"));
		Assert.False(await EvalAsync<bool>($"{SetupComponent}.get('copyable')"));
		Assert.DoesNotContain("Hidden header text", await EvalAsync<string>($"{Editor}.Canvas.getBody().innerText"));
	}

	// ---- Header/footer text and tokens -----------------------------------------------------------------------------

	[Fact]
	public async Task Tokens_and_fields_export_as_page_numbers_date_logo_and_liquid()
	{
		await StartAsync();
		await OpenAsync();
		await SetAsync("psHeaderLeft", "[Logo]");
		await SetAsync("psHeaderCenter", "Policy {policy.number} for {policy.insured}");
		await SetAsync("psHeaderRight", "Printed [Date]");
		await SetAsync("psFooterRight", "[Page]/[Pages]");
		await ApplyAsync();
		var html = await HtmlAsync();
		Assert.Contains("<span class=\"doc-hf-left\"><img class=\"doc-hf-logo\" src=\"{{ brand.logos.horizontal_4color }}\" alt=\"Mutual of Enumclaw\"></span>", html);
		Assert.Contains("<span class=\"doc-hf-center\">Policy {{ policy.number }} for {{ policy.insured }}</span>", html);
		Assert.Contains("<span class=\"doc-hf-right\">Printed {{ 'now' | date: '%m/%d/%Y' }}</span>", html);
		Assert.Contains("<span class=\"doc-hf-right\"><span class=\"doc-pageno\"></span>/<span class=\"doc-pagecount\"></span></span>", html);
	}

	[Fact]
	public async Task Other_text_is_encoded_and_cannot_become_liquid_or_markup()
	{
		await StartAsync();
		await OpenAsync();
		await SetAsync("psHeaderLeft", "<b>Bold</b> & {{ policy.number }} {% if x %} {not a.path!}");
		await ApplyAsync();
		var html = await HtmlAsync();
		Assert.Contains("<span class=\"doc-hf-left\">&lt;b&gt;Bold&lt;/b&gt; &amp; &#123;&#123; policy.number &#125;&#125; &#123;% if x %&#125; &#123;not a.path!&#125;</span>", html);
		Assert.DoesNotContain("<b>Bold", html);
	}

	[Fact]
	public async Task Token_buttons_insert_at_the_cursor_of_the_last_used_box()
	{
		await StartAsync();
		await OpenAsync();
		await SetAsync("psHeaderCenter", "Sheet  total");
		await Page.EvaluateExpressionAsync("(() => { const b = document.getElementById('psHeaderCenter'); b.focus(); b.setSelectionRange(6, 6); })()");
		await Page.EvaluateExpressionAsync("document.querySelector('.ps-token[data-token=\"[Page]\"]').click()");
		Assert.Equal("Sheet [Page] total", await ValueAsync("psHeaderCenter"));
		await Page.EvaluateExpressionAsync("(() => { const b = document.getElementById('psHeaderCenter'); b.setSelectionRange(b.value.length, b.value.length); })()");
		await Page.EvaluateExpressionAsync("document.querySelector('.ps-token[data-token=\"[Pages]\"]').click()");
		Assert.Equal("Sheet [Page] total[Pages]", await ValueAsync("psHeaderCenter"));
		Assert.Equal(["[Page]", "[Pages]", "[Date]", "[Logo]"], await EvalAsync<string[]>("[...document.querySelectorAll('.ps-token')].map(b => b.dataset.token)"));
	}

	[Fact]
	public async Task Tokens_go_to_the_footer_page_number_box_when_no_box_was_used()
	{
		await StartAsync();
		await OpenAsync();
		await SetAsync("psFooterRight", "");
		await Page.EvaluateExpressionAsync("document.querySelector('.ps-token[data-token=\"[Date]\"]').click()");
		Assert.Equal("[Date]", await ValueAsync("psFooterRight"));
	}

	[Fact]
	public async Task The_field_picker_lists_the_model_and_inserts_a_field()
	{
		await StartAsync();
		await OpenAsync();
		var options = await EvalAsync<string[]>("[...document.getElementById('psField').options].map(o => o.value)");
		Assert.Contains("policy.number", options);
		Assert.Contains("policy.insured", options);
		Assert.Equal("", options[0]);
		await Page.EvaluateExpressionAsync("document.getElementById('psHeaderLeft').focus()");
		await SetAsync("psField", "policy.number");
		Assert.Equal("{policy.number}", await ValueAsync("psHeaderLeft"));
		Assert.Equal("", await ValueAsync("psField"));
	}

	// ---- Variants --------------------------------------------------------------------------------------------------

	[Fact]
	public async Task Different_first_page_reveals_and_exports_its_own_header_and_footer()
	{
		await StartAsync();
		await OpenAsync();
		await CheckAsync("psDifferentFirst", true);
		foreach (var id in new[] { "psFirstHeaderLeft", "psFirstHeaderCenter", "psFirstHeaderRight", "psFirstFooterLeft", "psFirstFooterCenter", "psFirstFooterRight" })
		{
			Assert.False(await HiddenAsync(id), id);
		}
		Assert.True(await HiddenAsync("psEvenHeaderLeft"));
		await SetAsync("psFirstHeaderCenter", "Cover page");
		await ApplyAsync();
		var html = await HtmlAsync();
		Assert.Contains("<div class=\"doc-hf doc-hf-header doc-hf-first\"><span class=\"doc-hf-left\"></span><span class=\"doc-hf-center\">Cover page</span><span class=\"doc-hf-right\"></span></div>", html);
		Assert.Contains("<div class=\"doc-hf doc-hf-footer doc-hf-first\">", html);
		Assert.DoesNotContain("doc-hf-even", html);
	}

	[Fact]
	public async Task Different_odd_and_even_reveals_and_exports_the_even_page_variant()
	{
		await StartAsync();
		await OpenAsync();
		await CheckAsync("psDifferentEven", true);
		Assert.False(await HiddenAsync("psEvenFooterLeft"));
		Assert.True(await HiddenAsync("psFirstFooterLeft"));
		await SetAsync("psEvenFooterLeft", "Page [Page]");
		await ApplyAsync();
		var html = await HtmlAsync();
		Assert.Contains("<div class=\"doc-hf doc-hf-footer doc-hf-even\"><span class=\"doc-hf-left\">Page <span class=\"doc-pageno\"></span></span>", html);
		Assert.Contains("doc-hf-header doc-hf-even", html);
		Assert.DoesNotContain("doc-hf-first", html);
	}

	[Fact]
	public async Task Turning_a_variant_off_drops_it_but_keeps_its_text_for_later()
	{
		await StartAsync();
		await OpenAsync();
		await CheckAsync("psDifferentFirst", true);
		await SetAsync("psFirstHeaderLeft", "Kept");
		await ApplyAsync();
		await OpenAsync();
		await CheckAsync("psDifferentFirst", false);
		Assert.True(await HiddenAsync("psFirstHeaderLeft"));
		await ApplyAsync();
		Assert.DoesNotContain("doc-hf-first", await HtmlAsync());
		await OpenAsync();
		Assert.Equal("Kept", await ValueAsync("psFirstHeaderLeft"));
	}

	// ---- Validation ------------------------------------------------------------------------------------------------

	[Theory]
	[InlineData("psMarginTop", "4", "The top margin must be between 0 and 3 inches.")]
	[InlineData("psMarginLeft", "-1", "The left margin must be between 0 and 3 inches.")]
	[InlineData("psMarginRight", "", "The right margin must be between 0 and 3 inches.")]
	[InlineData("psFontSize", "40", "Header/footer text must be between 6 and 16 pt.")]
	public async Task Out_of_range_values_are_refused(string id, string value, string error)
	{
		await StartAsync();
		await OpenAsync();
		await SetAsync(id, value);
		await Page.EvaluateExpressionAsync("document.getElementById('psApply').click()");
		Assert.Equal(error, await EvalAsync<string>("document.getElementById('psError').textContent"));
		Assert.Equal(0, await SetupCountAsync());
		Assert.True(await EvalAsync<bool>("!!document.querySelector('.page-setup-dialog')"));
	}

	[Fact]
	public async Task Several_problems_are_listed_together()
	{
		await StartAsync();
		await OpenAsync();
		await SetAsync("psMarginTop", "5");
		await SetAsync("psMarginBottom", "abc");
		await Page.EvaluateExpressionAsync("document.getElementById('psApply').click()");
		Assert.Equal("The top margin must be between 0 and 3 inches. The bottom margin must be between 0 and 3 inches.",
			await EvalAsync<string>("document.getElementById('psError').textContent"));

		// Fixing them applies.
		await SetAsync("psMarginTop", "3");
		await SetAsync("psMarginBottom", "3");
		await ApplyAsync();
		Assert.Contains("ds-margins-3_0.5_3_0.5", await HtmlAsync());
	}

	// ---- Canvas width ----------------------------------------------------------------------------------------------

	[Theory]
	[InlineData("letter", "portrait", "0.5", "0.5", "720px")]
	[InlineData("letter", "landscape", "0.5", "0.5", "960px")]
	[InlineData("legal", "portrait", "1", "1", "624px")]
	[InlineData("legal", "landscape", "0.5", "0.5", "1248px")]
	[InlineData("a4", "portrait", "0.5", "0.5", "698px")]
	[InlineData("a4", "landscape", "0.5", "0.5", "1026px")]
	public async Task The_canvas_is_as_wide_as_the_pages_text_area(string size, string orientation, string left, string right, string width)
	{
		await StartAsync();
		await OpenAsync();
		await SetAsync("psSize", size);
		await SetAsync("psOrientation", orientation);
		await SetAsync("psMarginLeft", left);
		await SetAsync("psMarginRight", right);
		await ApplyAsync();
		Assert.Equal(width, await DeviceWidthAsync());
		Assert.Equal("page", await EvalAsync<string>($"{Editor}.getDevice()"));
	}

	[Fact]
	public async Task Use_standard_setup_removes_it_and_restores_the_letter_canvas()
	{
		await StartAsync();
		await OpenAsync();
		await SetAsync("psOrientation", "landscape");
		await ApplyAsync();
		Assert.Equal("960px", await DeviceWidthAsync());
		await OpenAsync();
		var status = await ActAsync("document.getElementById('psReset').click()");
		Assert.Equal("Page setup removed: the standard Letter page and footer are used.", status);
		Assert.Equal(0, await SetupCountAsync());
		Assert.DoesNotContain("doc-setup", await HtmlAsync());
		Assert.Equal("letter", await EvalAsync<string>($"{Editor}.getDevice()"));
	}

	[Fact]
	public async Task Removing_the_setup_any_other_way_restores_the_letter_canvas()
	{
		await StartAsync();
		await OpenAsync();
		await SetAsync("psSize", "a4");
		await ApplyAsync();
		Assert.Equal("page", await EvalAsync<string>($"{Editor}.getDevice()"));
		await Page.EvaluateExpressionAsync($"{SetupComponent}.remove()");
		await WaitAsync($"() => {Editor}.getWrapper().findType('page-setup').length === 0 && {Editor}.getDevice() === 'letter'");
	}

	// ---- Loading ---------------------------------------------------------------------------------------------------

	[Fact]
	public async Task The_setup_survives_saving_and_loading_the_project()
	{
		await StartAsync();
		await OpenAsync();
		await SetAsync("psSize", "legal");
		await SetAsync("psFooterCenter", "{policy.number} [Page]");
		await CheckAsync("psDifferentEven", true);
		await SetAsync("psEvenHeaderRight", "Even side");
		await ApplyAsync();
		var before = await HtmlAsync();

		await Page.EvaluateExpressionAsync($"(() => {{ const e = {Editor}; const d = JSON.parse(JSON.stringify(e.getProjectData())); e.setComponents('<p>x</p>'); e.loadProjectData(d); }})()");
		await WaitAsync($"() => {Editor}.getWrapper().findType('page-setup').length === 1");
		Assert.Equal(before, await HtmlAsync());
		await OpenAsync();
		Assert.Equal("legal", await ValueAsync("psSize"));
		Assert.Equal("{policy.number} [Page]", await ValueAsync("psFooterCenter"));
		Assert.Equal("Even side", await ValueAsync("psEvenHeaderRight"));
	}

	[Fact]
	public async Task Exported_markup_is_read_back_into_a_setup()
	{
		var markup = Setup.Markup("a4", "landscape", "1_0.75_1.5_0.25", "12",
			Setup.Slots("header", "default", "Policy {{ policy.number }}", "", "Printed {{ 'now' | date: '%m/%d/%Y' }}") +
			Setup.Slots("footer", "default", "<img class=\"doc-hf-logo\" src=\"x\">", "", Setup.PageOfPages) +
			Setup.Slots("header", "first", "Cover") + Setup.Slots("footer", "first"));
		await StartAsync(markup + "<p>Body</p>");
		Assert.Equal(1, await SetupCountAsync());
		await OpenAsync();
		Assert.Equal("a4", await ValueAsync("psSize"));
		Assert.Equal("landscape", await ValueAsync("psOrientation"));
		Assert.Equal(["1", "0.75", "1.5", "0.25"], [await ValueAsync("psMarginTop"), await ValueAsync("psMarginRight"), await ValueAsync("psMarginBottom"), await ValueAsync("psMarginLeft")]);
		Assert.Equal("12", await ValueAsync("psFontSize"));
		Assert.Equal("Policy {policy.number}", await ValueAsync("psHeaderLeft"));
		Assert.Equal("Printed [Date]", await ValueAsync("psHeaderRight"));
		Assert.Equal("[Logo]", await ValueAsync("psFooterLeft"));
		Assert.Equal("Page [Page] of [Pages]", await ValueAsync("psFooterRight"));
		Assert.True(await EvalAsync<bool>("document.getElementById('psDifferentFirst').checked"));
		Assert.False(await EvalAsync<bool>("document.getElementById('psDifferentEven').checked"));
		Assert.Equal("Cover", await ValueAsync("psFirstHeaderLeft"));
	}

	// ---- Clauses and end to end ------------------------------------------------------------------------------------

	[Fact]
	public async Task Clauses_have_no_page_setup()
	{
		await StartAsync();
		await ActAsync("(() => { const k = document.getElementById('docKind'); k.value = 'clauses'; k.dispatchEvent(new Event('change')); })()");
		try
		{
			Assert.True(await EvalAsync<bool>("document.getElementById('btnPageSetup').disabled"));
		}
		finally
		{
			await ActAsync("(() => { const k = document.getElementById('docKind'); k.value = 'templates'; k.dispatchEvent(new Event('change')); })()");
		}
		Assert.False(await EvalAsync<bool>("document.getElementById('btnPageSetup').disabled"));
	}

	[Fact]
	public async Task What_the_designer_exports_prints_with_its_page_setup()
	{
		await StartAsync("<p>Designer body</p>");
		await OpenAsync();
		await SetAsync("psSize", "legal");
		await SetAsync("psHeaderLeft", "Policy {policy.number}");
		await SetAsync("psFooterLeft", "");
		await SetAsync("psFooterRight", "Page [Page] of [Pages]");
		await ApplyAsync();
		var html = await HtmlAsync();
		var css = await EvalAsync<string>($"{Editor}.getCss()");
		using var response = await fixture.Http.PostAsJsonAsync("api/render", new JsonObject
		{
			["html"] = html,
			["css"] = css,
			["data"] = new JsonObject { ["policy"] = new JsonObject { ["number"] = "CPP9999999" } }
		});
		Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
		using var document = PdfDocument.Open(await response.Content.ReadAsByteArrayAsync());
		var page = document.GetPage(1);
		Assert.Equal(612, page.Width, 1.0);
		Assert.Equal(1008, page.Height, 1.0);
		var text = string.Join(" ", page.GetWords().Select(w => w.Text));
		Assert.Contains("Policy CPP9999999", text);
		Assert.Contains("Page 1 of 1", text);
		Assert.Contains("Designer body", text);
		Assert.DoesNotContain("Mutual Of Enumclaw", text);
	}
}
