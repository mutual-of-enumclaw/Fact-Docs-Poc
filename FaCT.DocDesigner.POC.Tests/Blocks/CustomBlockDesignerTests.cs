using System.Net.Http.Json;
using System.Text.Json;
using PuppeteerSharp;

namespace FaCT.DocDesigner.POC.Tests.Blocks;

/// <summary>Saving a selection as a block in the designer, dropping it into other documents, and managing saved blocks.</summary>
[Collection("Isolated designer")]
public sealed class CustomBlockDesignerTests(IsolatedDesignerFixture fixture)
{
	private const string Canvas =
		"<p>Before</p><div class=\"fancy\"><p id=\"pid\">Policy <span class=\"df\" data-field=\"policy.number\"></span></p><p>Second line</p></div>";

	private const string Styles = ".fancy{border:3px solid rgb(1, 2, 3);padding:4px;} #pid{color:rgb(4, 5, 6);}";

	private IPage Page => fixture.Page;

	private static string Unique(string label) => label + " " + Guid.NewGuid().ToString("N")[..6];

	private static string Slug(string label) => System.Text.RegularExpressions.Regex.Replace(label.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');

	private Task<T> EvalAsync<T>(string script) => Page.EvaluateExpressionAsync<T>(script);

	private Task WaitAsync(string predicate) => Page.WaitForFunctionAsync(predicate, new WaitForFunctionOptions { Timeout = 30_000 });

	private async Task<string> ActAsync(string script)
	{
		await Page.EvaluateExpressionAsync("document.getElementById('status').textContent = ''");
		await Page.EvaluateExpressionAsync(script);
		await WaitAsync("() => { const t = document.getElementById('status').textContent; return t.length > 0 && !t.endsWith('...'); }");
		return await EvalAsync<string>("document.getElementById('status').textContent");
	}

	private async Task CanvasAsync(string html, string css)
	{
		await Page.EvaluateFunctionAsync("(h, c) => { const e = grapesjs.editors[0]; e.select(null); e.setComponents(h); e.setStyle(c); }", html, css);
	}

	private Task SelectFancyAsync() =>
		Page.EvaluateExpressionAsync("(() => { const e = grapesjs.editors[0]; e.select(e.getWrapper().find('.fancy')[0]); })()");

	/// <summary>Saves the selection through the Save as block dialog; returns the status message.</summary>
	private async Task<string> SaveSelectionAsync(string label, string category)
	{
		await Page.EvaluateExpressionAsync("document.getElementById('btnSaveBlock').click()");
		await WaitAsync("() => !!document.getElementById('blockLabel')");
		await Page.EvaluateFunctionAsync("(l, c) => { document.getElementById('blockLabel').value = l; document.getElementById('blockCategory').value = c; }", label, category);
		return await ActAsync("document.getElementById('blockSave').click()");
	}

	private async Task<JsonElement> SavedAsync(string name) =>
		(await fixture.Http.GetFromJsonAsync<JsonElement[]>("api/blocks"))!.Single(b => b.GetProperty("name").GetString() == name);

	/// <summary>What dropping the block does: its content is added and the block:drag:stop handlers run.</summary>
	private Task<string> DropAsync(string name) => EvalAsync<string>(
		$"(() => {{ const e = grapesjs.editors[0]; const b = e.Blocks.get('custom-{name}'); " +
		"const c = e.getWrapper().append(JSON.parse(JSON.stringify(b.get('content'))))[0]; e.trigger('block:drag:stop', c, b); return c.getId(); })()");

	[Fact]
	public async Task The_selection_toolbar_has_save_as_block()
	{
		await CanvasAsync(Canvas, Styles);
		await SelectFancyAsync();
		await WaitAsync("() => !!document.querySelector('.gjs-toolbar .tb-save-block')");
		Assert.Equal("Save as block", await EvalAsync<string>("document.querySelector('.gjs-toolbar .tb-save-block').title"));
		// GrapesJS toolbar buttons act on mousedown.
		await Page.EvaluateExpressionAsync("document.querySelector('.gjs-toolbar .tb-save-block').dispatchEvent(new MouseEvent('mousedown', { bubbles: true }))");
		await WaitAsync("() => !!document.getElementById('blockLabel')");
		Assert.Equal("Save as block", await EvalAsync<string>("document.querySelector('.gjs-mdl-title').textContent"));
		Assert.Equal("My blocks", await EvalAsync<string>("document.getElementById('blockCategory').value"));
		await Page.EvaluateExpressionAsync("document.getElementById('blockCancel').click()");
	}

	[Fact]
	public async Task Saving_needs_a_selection()
	{
		await CanvasAsync(Canvas, Styles);
		var status = await ActAsync("document.getElementById('btnSaveBlock').click()");
		Assert.StartsWith("Select something on the page first", status);
		Assert.False(await EvalAsync<bool>("grapesjs.editors[0].Modal.isOpen()"));
	}

	[Fact]
	public async Task A_saved_block_keeps_content_styles_and_fields_without_ids()
	{
		var label = Unique("Summary box");
		await CanvasAsync(Canvas, Styles);
		await SelectFancyAsync();
		var status = await SaveSelectionAsync(label, "Test blocks");
		Assert.Equal($"Saved block \"{label}\". Find it under Test blocks in the Blocks panel.", status);

		var saved = await SavedAsync(Slug(label));
		var json = saved.GetProperty("components").GetRawText();
		Assert.DoesNotContain("\"id\"", json);
		Assert.Contains("policy.number", json);
		Assert.Contains("rgb(4, 5, 6)", json);
		Assert.Contains(".fancy", saved.GetProperty("css").GetString());
		Assert.DoesNotContain("#pid", saved.GetProperty("css").GetString());

		Assert.Equal(label, await EvalAsync<string>($"grapesjs.editors[0].Blocks.get('custom-{Slug(label)}').get('attributes').title.replace(' (saved block)', '')"));
		await WaitAsync($"() => !!document.querySelector('#blocks [data-custom-block=\"{Slug(label)}\"]')");
		Assert.Contains("Test blocks", await EvalAsync<string>(
			$"document.querySelector('#blocks [data-custom-block=\"{Slug(label)}\"]').closest('.gjs-block-category').textContent"));
	}

	[Fact]
	public async Task Dropping_a_block_into_another_document_brings_its_styles_and_fields()
	{
		var label = Unique("Styled box");
		await CanvasAsync(Canvas, Styles);
		await SelectFancyAsync();
		await SaveSelectionAsync(label, "Test blocks");

		// A different document without those styles.
		await CanvasAsync("<p>Another document</p>", "");
		var first = await DropAsync(Slug(label));
		var second = await DropAsync(Slug(label));
		Assert.NotEqual(first, second);

		Assert.Equal(2, await EvalAsync<int>("grapesjs.editors[0].getWrapper().find('.fancy').length"));
		Assert.Equal("rgb(1, 2, 3)", await EvalAsync<string>(
			"grapesjs.editors[0].Canvas.getWindow().getComputedStyle(grapesjs.editors[0].getWrapper().find('.fancy')[1].getEl()).borderTopColor"));
		Assert.Equal(["rgb(4, 5, 6)", "rgb(4, 5, 6)"], await EvalAsync<string[]>(
			"grapesjs.editors[0].getWrapper().find('.fancy').map(f => grapesjs.editors[0].Canvas.getWindow().getComputedStyle(f.components().at(0).getEl()).color)"));
		Assert.Equal(2, await EvalAsync<int>("grapesjs.editors[0].getWrapper().findType('data-field').length"));
		Assert.Contains("{{ policy.number }}", await EvalAsync<string>("grapesjs.editors[0].getHtml()"));

		// The copies are independent: restyling one leaves the other alone.
		await Page.EvaluateExpressionAsync("grapesjs.editors[0].getWrapper().find('.fancy')[0].components().at(0).addStyle({ color: 'rgb(9, 9, 9)' })");
		Assert.Equal(["rgb(9, 9, 9)", "rgb(4, 5, 6)"], await EvalAsync<string[]>(
			"grapesjs.editors[0].getWrapper().find('.fancy').map(f => grapesjs.editors[0].Canvas.getWindow().getComputedStyle(f.components().at(0).getEl()).color)"));
	}

	[Fact]
	public async Task Saving_the_same_name_replaces_the_block()
	{
		var label = Unique("Replace me");
		await CanvasAsync(Canvas, Styles);
		await SelectFancyAsync();
		await SaveSelectionAsync(label, "Test blocks");
		await CanvasAsync("<div class=\"fancy\"><p>Version two</p></div>", ".fancy{color:red}");
		await SelectFancyAsync();
		await SaveSelectionAsync(label, "Other category");

		var blocks = (await fixture.Http.GetFromJsonAsync<JsonElement[]>("api/blocks"))!.Where(b => b.GetProperty("name").GetString() == Slug(label)).ToList();
		var block = Assert.Single(blocks);
		Assert.Equal("Other category", block.GetProperty("category").GetString());
		Assert.Contains("Version two", block.GetProperty("components").GetRawText());
		Assert.Equal(1, await EvalAsync<int>($"document.querySelectorAll('#blocks [data-custom-block=\"{Slug(label)}\"]').length"));
	}

	[Fact]
	public async Task A_name_is_required()
	{
		await CanvasAsync(Canvas, Styles);
		await SelectFancyAsync();
		await Page.EvaluateExpressionAsync("document.getElementById('btnSaveBlock').click()");
		await WaitAsync("() => !!document.getElementById('blockLabel')");
		await Page.EvaluateExpressionAsync("document.getElementById('blockSave').click()");
		Assert.Equal("Give the block a name.", await EvalAsync<string>("document.getElementById('blockError').textContent"));
		await Page.EvaluateExpressionAsync("document.getElementById('blockCancel').click()");
	}

	[Fact]
	public async Task Block_names_are_shown_as_text()
	{
		var label = "<img src=x onerror=\"window.__blockXss=1\">Evil " + Guid.NewGuid().ToString("N")[..6];
		await CanvasAsync(Canvas, Styles);
		await SelectFancyAsync();
		await SaveSelectionAsync(label, "<b>Bold</b> category");
		var name = Slug(label);
		await WaitAsync($"() => !!document.querySelector('#blocks [data-custom-block=\"{name}\"]')");
		Assert.True(await EvalAsync<bool>($"!document.querySelector('#blocks [data-custom-block=\"{name}\"] img')"));
		Assert.Contains("<img", await EvalAsync<string>($"document.querySelector('#blocks [data-custom-block=\"{name}\"]').textContent"));
		Assert.True(await EvalAsync<bool>("window.__blockXss === undefined"));
		Assert.True(await EvalAsync<bool>("![...document.querySelectorAll('#blocks .gjs-block-category b')].length"));
	}

	[Fact]
	public async Task Manage_saved_blocks_lists_and_deletes()
	{
		var label = Unique("Delete me");
		await CanvasAsync(Canvas, Styles);
		await SelectFancyAsync();
		await SaveSelectionAsync(label, "Test blocks");
		var name = Slug(label);

		var status = await ActAsync("document.getElementById('btnManageBlocks').click()");
		Assert.Matches(@"^\d+ saved blocks?\.$", status);
		Assert.Equal(label, await EvalAsync<string>($"document.querySelector('.custom-block-row[data-block=\"{name}\"] .scenario-name').textContent"));

		Assert.Equal($"Deleted block \"{label}\".", await ActAsync($"document.querySelector('.custom-block-row[data-block=\"{name}\"] .custom-block-delete').click()"));
		Assert.Equal(0, await EvalAsync<int>($"document.querySelectorAll('.custom-block-row[data-block=\"{name}\"]').length"));
		Assert.Equal(0, await EvalAsync<int>($"document.querySelectorAll('#blocks [data-custom-block=\"{name}\"]').length"));
		Assert.DoesNotContain((await fixture.Http.GetFromJsonAsync<JsonElement[]>("api/blocks"))!, b => b.GetProperty("name").GetString() == name);
		await Page.EvaluateExpressionAsync("grapesjs.editors[0].Modal.close()");
	}

	[Fact]
	public async Task Blocks_saved_by_others_appear_when_the_designer_opens()
	{
		var name = "shared-" + Guid.NewGuid().ToString("N")[..8];
		(await fixture.Http.PutAsJsonAsync($"api/blocks/{name}", new
		{
			label = "Shared signature",
			category = "Signatures",
			components = new { tagName = "div", classes = new[] { "sig" }, components = new object[] { new { type = "text", tagName = "p", content = "Authorized representative" } } },
			css = ".sig{border-top:1px solid rgb(0, 0, 0);}"
		})).EnsureSuccessStatusCode();

		await Page.ReloadAsync(null, [WaitUntilNavigation.Networkidle0]);
		await WaitAsync("() => window.grapesjs && grapesjs.editors.length > 0 && document.getElementById('status').textContent.length > 0");
		await WaitAsync($"() => !!document.querySelector('#blocks [data-custom-block=\"{name}\"]')");
		Assert.Contains("Signatures", await EvalAsync<string>($"document.querySelector('#blocks [data-custom-block=\"{name}\"]').closest('.gjs-block-category').textContent"));

		await CanvasAsync("<p>Letter</p>", "");
		await DropAsync(name);
		Assert.Contains("Authorized representative", await EvalAsync<string>("grapesjs.editors[0].getHtml()"));
		Assert.Contains(".sig", await EvalAsync<string>("grapesjs.editors[0].getCss()"));
	}
}
