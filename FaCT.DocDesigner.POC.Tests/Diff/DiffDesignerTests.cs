using System.Net.Http.Json;
using System.Text.Json;
using PuppeteerSharp;

namespace FaCT.DocDesigner.POC.Tests.Diff;

/// <summary>Diff before publish in the designer: publishing over a published version, and Compare with Published.</summary>
[Collection("Isolated designer")]
public sealed class DiffDesignerTests(IsolatedDesignerFixture fixture)
{
	private IPage Page => fixture.Page;

	private static string Unique(string name) => name + "-" + Guid.NewGuid().ToString("N")[..8];

	private Task<T> EvalAsync<T>(string script) => Page.EvaluateExpressionAsync<T>(script);

	private Task WaitAsync(string predicate) =>
		Page.WaitForFunctionAsync(predicate, new WaitForFunctionOptions { Timeout = 60_000 });

	private async Task<string> ActAsync(string script)
	{
		await Page.EvaluateExpressionAsync("document.getElementById('status').textContent = ''");
		await Page.EvaluateExpressionAsync(script);
		await WaitAsync("() => { const t = document.getElementById('status').textContent; return t.length > 0 && !t.endsWith('...'); }");
		return await EvalAsync<string>("document.getElementById('status').textContent");
	}

	private Task SetCanvasAsync(string html) => Page.EvaluateFunctionAsync("h => grapesjs.editors[0].setComponents(h)", html);

	/// <summary>A new template whose v1 (the given HTML) is published through the designer.</summary>
	private async Task<string> PublishedTemplateAsync(string html)
	{
		var name = Unique("tpl");
		await ActAsync("(() => { const k = document.getElementById('docKind'); k.value = 'templates'; k.dispatchEvent(new Event('change')); })()");
		await ActAsync($"(() => {{ const n = document.getElementById('templateName'); n.value = '{name}'; n.dispatchEvent(new Event('change')); }})()");
		await SetCanvasAsync(html);
		// First publish: nothing to compare with, so a plain confirmation (accepted by the fixture).
		Assert.Equal("v1 is now published.", await ActAsync("document.getElementById('btnPublish').click()"));
		Assert.False(await EvalAsync<bool>("grapesjs.editors[0].Modal.isOpen()"));
		return name;
	}

	private async Task StartPublishAsync()
	{
		await Page.EvaluateExpressionAsync("document.getElementById('btnPublish').click()");
		await WaitAsync("() => !!document.getElementById('diffPublish')");
	}

	private Task<string> PaneTextAsync(string pane) =>
		EvalAsync<string>($"document.querySelector('.diff-pane[data-pane={pane}]').textContent");

	private async Task<string[]> VersionStatusesAsync(string name) =>
		(await fixture.Http.GetFromJsonAsync<JsonElement[]>($"api/templates/{name}/versions"))!.Select(v => v.GetProperty("status").GetString()!).ToArray();

	[Fact]
	public async Task Publishing_over_a_published_version_shows_the_changes_first()
	{
		var name = await PublishedTemplateAsync("<h1>Declarations</h1><p>Old exclusion wording</p>");
		await SetCanvasAsync("<h1>Declarations</h1><p>New exclusion wording</p>");
		await StartPublishAsync();

		Assert.Equal("Review changes before publishing", await EvalAsync<string>("document.querySelector('.gjs-mdl-title').textContent"));
		var summary = await EvalAsync<string>("document.querySelector('.diff-summary').textContent");
		Assert.StartsWith("Compared with v1 (Published): printed text +1 \u22121 lines, HTML +1 \u22121 lines", summary);
		Assert.Contains("Rendered with the model example data.", summary);
		Assert.Equal("Publish v2", await EvalAsync<string>("document.getElementById('diffPublish').textContent"));

		// The printed-text tab is shown first, with the change marked.
		Assert.False(await EvalAsync<bool>("document.querySelector('.diff-pane[data-pane=text]').hidden"));
		Assert.Equal("+ New exclusion wording", await EvalAsync<string>("document.querySelector('.diff-pane[data-pane=text] .diff-added').textContent"));
		Assert.Equal("\u2212 Old exclusion wording", await EvalAsync<string>("document.querySelector('.diff-pane[data-pane=text] .diff-removed').textContent"));
		Assert.Contains("<p>New exclusion wording</p>", await PaneTextAsync("html"));

		Assert.Equal("v2 is now published.", await ActAsync("document.getElementById('diffPublish').click()"));
		Assert.Equal(["Retired", "Published"], await VersionStatusesAsync(name));
	}

	[Fact]
	public async Task Cancelling_the_review_keeps_the_published_version()
	{
		var name = await PublishedTemplateAsync("<p>Keep me</p>");
		await SetCanvasAsync("<p>Not yet</p>");
		await StartPublishAsync();
		Assert.Equal("Publish cancelled.", await ActAsync("document.getElementById('diffCancel').click()"));
		// The draft was saved, but v1 is still the published one.
		Assert.Equal(["Published", "Draft"], await VersionStatusesAsync(name));
	}

	[Fact]
	public async Task Closing_the_review_cancels_too()
	{
		var name = await PublishedTemplateAsync("<p>Keep me</p>");
		await SetCanvasAsync("<p>Changed</p>");
		await StartPublishAsync();
		Assert.Equal("Publish cancelled.", await ActAsync("grapesjs.editors[0].Modal.close()"));
		Assert.Equal(["Published", "Draft"], await VersionStatusesAsync(name));
	}

	[Fact]
	public async Task The_tabs_switch_between_text_pdfs_html_and_css()
	{
		await PublishedTemplateAsync("<p class=\"x\">Same text</p>");
		// Same markup again (an edit) with a new style rule.
		await SetCanvasAsync("<p class=\"x\">Same text</p>");
		await Page.EvaluateExpressionAsync("grapesjs.editors[0].setStyle('.x{color:#c00000}')");
		await StartPublishAsync();

		Assert.Equal(["text", "pdf", "html", "css"], await EvalAsync<string[]>(
			"[...document.querySelectorAll('.diff-tab')].map(t => t.getAttribute('data-pane'))"));
		Assert.Contains("no changes", await EvalAsync<string>("document.querySelector('.diff-tab[data-pane=text]').textContent"));
		Assert.Equal("No changes.", await PaneTextAsync("text"));
		Assert.Contains("+1", await EvalAsync<string>("document.querySelector('.diff-tab[data-pane=css]').textContent"));

		await Page.EvaluateExpressionAsync("document.querySelector('.diff-tab[data-pane=css]').click()");
		Assert.True(await EvalAsync<bool>("document.querySelector('.diff-pane[data-pane=text]').hidden"));
		Assert.False(await EvalAsync<bool>("document.querySelector('.diff-pane[data-pane=css]').hidden"));
		Assert.True(await EvalAsync<bool>("document.querySelector('.diff-tab[data-pane=css]').classList.contains('active')"));
		Assert.Contains(".x{", await EvalAsync<string>("document.querySelector('.diff-pane[data-pane=css] .diff-added').textContent"));

		await Page.EvaluateExpressionAsync("document.querySelector('.diff-tab[data-pane=pdf]').click()");
		Assert.StartsWith("blob:", await EvalAsync<string>("document.getElementById('diffPublishedPdf').src"));
		Assert.StartsWith("blob:", await EvalAsync<string>("document.getElementById('diffCurrentPdf').src"));
		Assert.Equal(["Published v1 \u00b7 1 page", "This version \u00b7 1 page"], await EvalAsync<string[]>(
			"[...document.querySelectorAll('.diff-pdfs h4')].map(h => h.textContent)"));

		await ActAsync("document.getElementById('diffCancel').click()");
	}

	[Fact]
	public async Task Compare_with_published_reports_nothing_to_compare_for_a_new_template()
	{
		await ActAsync("(() => { const k = document.getElementById('docKind'); k.value = 'templates'; k.dispatchEvent(new Event('change')); })()");
		var name = Unique("new");
		await ActAsync($"(() => {{ const n = document.getElementById('templateName'); n.value = '{name}'; n.dispatchEvent(new Event('change')); }})()");
		Assert.Equal($"Nothing is published for \"{name}\" yet.", await ActAsync("document.getElementById('btnDiff').click()"));
		Assert.False(await EvalAsync<bool>("grapesjs.editors[0].Modal.isOpen()"));
	}

	[Fact]
	public async Task Compare_with_published_shows_the_diff_without_publishing()
	{
		var name = await PublishedTemplateAsync("<p>Original</p>");
		Assert.Equal("No differences from v1 (Published).", await ActAsync("document.getElementById('btnDiff').click()"));
		await Page.EvaluateExpressionAsync("grapesjs.editors[0].Modal.close()");

		await SetCanvasAsync("<p>Original</p><p>Extra paragraph</p>");
		var status = await ActAsync("document.getElementById('btnDiff').click()");
		Assert.StartsWith("Compared with v1 (Published): printed text +1 \u22120 line,", status);
		Assert.Equal("Compare with published", await EvalAsync<string>("document.querySelector('.gjs-mdl-title').textContent"));
		Assert.False(await EvalAsync<bool>("!!document.getElementById('diffPublish')"));
		Assert.Contains("Extra paragraph", await PaneTextAsync("text"));
		await Page.EvaluateExpressionAsync("grapesjs.editors[0].Modal.close()");
		Assert.Equal(["Published"], await VersionStatusesAsync(name));
	}

	[Fact]
	public async Task The_diff_renders_with_the_chosen_test_scenario()
	{
		var name = await PublishedTemplateAsync("<p>Policy <span class=\"df\" data-field=\"policy.number\"></span></p>");
		(await fixture.Http.PutAsJsonAsync($"api/templates/{name}/scenarios/Renewal", new { data = new { policy = new { number = "REN-77" } } })).EnsureSuccessStatusCode();
		await ActAsync("document.getElementById('templateName').dispatchEvent(new Event('change'))");
		await ActAsync("(() => { const s = document.getElementById('scenarioSelect'); s.value = 'Renewal'; s.dispatchEvent(new Event('change')); })()");
		await Page.EvaluateExpressionAsync("grapesjs.editors[0].getWrapper().append('<p>Renewal notice</p>')");

		await ActAsync("document.getElementById('btnDiff').click()");
		Assert.Contains("Rendered with scenario \"Renewal\".", await EvalAsync<string>("document.querySelector('.diff-summary').textContent"));
		Assert.Contains("REN-77", await PaneTextAsync("text"));
		await Page.EvaluateExpressionAsync("grapesjs.editors[0].Modal.close()");
		await ActAsync("(() => { const s = document.getElementById('scenarioSelect'); s.value = ''; s.dispatchEvent(new Event('change')); })()");
	}

	[Fact]
	public async Task A_version_that_cannot_render_is_explained_in_the_review()
	{
		await PublishedTemplateAsync("<p>Fine</p>");
		await SetCanvasAsync("<p>Fine</p><p>{% include 'missing-clause' %}</p>");
		var status = await ActAsync("document.getElementById('btnDiff').click()");
		Assert.Contains("printed text could not be compared", status);
		Assert.Contains("missing-clause", await PaneTextAsync("text"));
		await Page.EvaluateExpressionAsync("document.querySelector('.diff-tab[data-pane=pdf]').click()");
		Assert.Contains("missing-clause", await EvalAsync<string>("document.querySelector('.diff-pdfs .model-error').textContent"));
		await Page.EvaluateExpressionAsync("grapesjs.editors[0].Modal.close()");
	}
}
