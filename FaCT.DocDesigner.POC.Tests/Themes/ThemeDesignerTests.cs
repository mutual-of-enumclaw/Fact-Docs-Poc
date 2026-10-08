using System.Net.Http.Json;
using System.Text.Json;
using PuppeteerSharp;

namespace FaCT.DocDesigner.POC.Tests.Themes;

/// <summary>Themes in the designer: choosing one for the template, the canvas look, and managing themes and fonts.</summary>
[Collection("Isolated designer")]
public sealed class ThemeDesignerTests(IsolatedDesignerFixture fixture)
{
	private const string Editor = "grapesjs.editors[0]";

	private IPage Page => fixture.Page;

	private static string Unique(string name) => name + "-" + Guid.NewGuid().ToString("N")[..8];

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

	private async Task StartAsync(string html = "<h1 id=\"title\">Heading</h1><p>Body</p>")
	{
		await Page.EvaluateExpressionAsync($"{Editor}.Modal.close()");
		if (await EvalAsync<string>("document.getElementById('docKind').value") != "templates")
		{
			await ActAsync("(() => { const k = document.getElementById('docKind'); k.value = 'templates'; k.dispatchEvent(new Event('change')); })()");
		}
		await Page.EvaluateFunctionAsync("h => { const e = grapesjs.editors[0]; e.select(null); e.setComponents(h); e.setStyle(''); }", html);
	}

	private async Task<string> SaveThemeAsync(string label, string? green = null, string? bodyFont = null, string? name = null)
	{
		name ??= Unique("theme");
		var colors = green is null ? new Dictionary<string, string>() : new Dictionary<string, string> { ["moe-green"] = green };
		var response = await fixture.Http.PutAsJsonAsync($"api/themes/{name}", new { label, colors, bodyFont });
		Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
		return name;
	}

	private async Task OpenPickerAsync()
	{
		await ActAsync("document.getElementById('btnTheme').click()");
		await WaitAsync("() => !!document.getElementById('themeApply')");
	}

	private async Task OpenManagerAsync()
	{
		await OpenPickerAsync();
		await ActAsync("document.getElementById('themeManage').click()");
		await WaitAsync("() => !!document.getElementById('tmSave')");
	}

	private Task SetAsync(string id, string value) =>
		Page.EvaluateFunctionAsync("(id, v) => { const el = document.getElementById(id); el.value = v; el.dispatchEvent(new Event('input')); el.dispatchEvent(new Event('change')); }", id, value);

	private Task<string> ValueAsync(string id) => Page.EvaluateFunctionAsync<string>("id => document.getElementById(id).value", id);

	private Task<string> HtmlAsync() => EvalAsync<string>($"{Editor}.getHtml()");

	private Task<string> ThemeLinkAsync() =>
		EvalAsync<string>($"(() => {{ const l = {Editor}.Canvas.getDocument().getElementById('docThemeCss'); return l ? l.getAttribute('href') : ''; }})()");

	private Task WaitForHeadingColorAsync(string rgb) =>
		WaitAsync($"c => {{ const h = {Editor}.Canvas.getDocument().getElementById('title'); return !!h && getComputedStyle(h).color === c; }}", rgb);

	private async Task<string> ErrorAsync()
	{
		await WaitAsync("() => document.getElementById('tmError').textContent.length > 0");
		return await EvalAsync<string>("document.getElementById('tmError').textContent");
	}

	// ---- Choosing a theme ----------------------------------------------------------------------------------------

	[Fact]
	public async Task The_menu_has_a_theme_item()
	{
		await StartAsync();
		Assert.Equal("Theme…", await EvalAsync<string>("document.getElementById('btnTheme').textContent.trim()"));
		Assert.False(await EvalAsync<bool>("document.getElementById('btnTheme').disabled"));
		Assert.Equal("Author", await EvalAsync<string>("document.getElementById('btnTheme').getAttribute('data-requires')"));
	}

	[Fact]
	public async Task The_picker_lists_the_standard_look_and_every_theme()
	{
		var name = await SaveThemeAsync("Picker " + Guid.NewGuid().ToString("N")[..4], "#c00000");
		await StartAsync();
		await OpenPickerAsync();
		Assert.Equal("Theme", await EvalAsync<string>("document.querySelector('.gjs-mdl-title').textContent"));
		var options = await EvalAsync<string[]>("[...document.getElementById('themeSelect').options].map(o => o.value)");
		Assert.Equal("", options[0]);
		Assert.Contains(name, options);
		Assert.Equal("", await ValueAsync("themeSelect"));
		Assert.Equal("MOE standard", await EvalAsync<string>("document.getElementById('themeSelect').options[0].textContent"));
		Assert.Equal(0, await EvalAsync<int>("document.querySelectorAll('.theme-swatch.changed').length"));

		await SetAsync("themeSelect", name);
		Assert.Equal(["moe-green"], await EvalAsync<string[]>("[...document.querySelectorAll('.theme-swatch.changed')].map(s => s.getAttribute('data-token'))"));
		var tokens = (await fixture.Http.GetFromJsonAsync<JsonElement>("api/themes")).GetProperty("tokens").GetArrayLength();
		Assert.Equal(tokens, await EvalAsync<int>("document.querySelectorAll('.theme-swatch').length"));
	}

	[Fact]
	public async Task Applying_a_theme_marks_the_template_and_restyles_the_canvas()
	{
		var name = await SaveThemeAsync("Red", "#c00000");
		await StartAsync();
		await WaitForHeadingColorAsync("rgb(20, 72, 53)");
		await OpenPickerAsync();
		await SetAsync("themeSelect", name);
		Assert.Equal("Theme: Red.", await ActAsync("document.getElementById('themeApply').click()"));

		Assert.Contains($"<div class=\"doc-theme theme-{name}\"", await HtmlAsync());
		Assert.Equal(1, await EvalAsync<int>($"{Editor}.getWrapper().findType('theme-ref').length"));
		Assert.StartsWith($"/api/themes/{name}/theme.css?v=", await ThemeLinkAsync());
		await WaitForHeadingColorAsync("rgb(192, 0, 0)");
	}

	[Fact]
	public async Task Choosing_another_theme_replaces_it_and_the_standard_look_removes_it()
	{
		var red = await SaveThemeAsync("Red", "#c00000");
		var blue = await SaveThemeAsync("Blue", "#0000c0");
		await StartAsync();
		await OpenPickerAsync();
		await SetAsync("themeSelect", red);
		await ActAsync("document.getElementById('themeApply').click()");
		await OpenPickerAsync();
		Assert.Equal(red, await ValueAsync("themeSelect"));
		await SetAsync("themeSelect", blue);
		await ActAsync("document.getElementById('themeApply').click()");
		Assert.Equal(1, await EvalAsync<int>($"{Editor}.getWrapper().findType('theme-ref').length"));
		Assert.Contains($"theme-{blue}", await HtmlAsync());
		await WaitForHeadingColorAsync("rgb(0, 0, 192)");

		await OpenPickerAsync();
		await SetAsync("themeSelect", "");
		Assert.Equal("Theme: MOE standard.", await ActAsync("document.getElementById('themeApply').click()"));
		Assert.DoesNotContain("doc-theme", await HtmlAsync());
		Assert.Equal("", await ThemeLinkAsync());
		await WaitForHeadingColorAsync("rgb(20, 72, 53)");
	}

	[Fact]
	public async Task Applying_counts_as_an_unsaved_change()
	{
		var name = await SaveThemeAsync("Dirty");
		await StartAsync();
		var before = await EvalAsync<int>($"{Editor}.getDirtyCount()");
		await OpenPickerAsync();
		await SetAsync("themeSelect", name);
		await ActAsync("document.getElementById('themeApply').click()");
		Assert.True(await EvalAsync<int>($"{Editor}.getDirtyCount()") > before);
	}

	[Fact]
	public async Task A_missing_theme_is_flagged_in_the_picker()
	{
		await StartAsync("<div class=\"doc-theme theme-deleted-one\"></div><h1 id=\"title\">Heading</h1>");
		await OpenPickerAsync();
		Assert.Equal("deleted-one", await ValueAsync("themeSelect"));
		Assert.Contains("missing", await EvalAsync<string>("document.getElementById('themeSelect').selectedOptions[0].textContent"));
	}

	[Fact]
	public async Task The_theme_survives_saving_and_loading_the_project()
	{
		var name = await SaveThemeAsync("Kept", "#00c000");
		await StartAsync($"<div class=\"doc-theme theme-{name}\"></div><h1 id=\"title\">Heading</h1>");
		Assert.Equal(1, await EvalAsync<int>($"{Editor}.getWrapper().findType('theme-ref').length"));
		var before = await HtmlAsync();
		await Page.EvaluateExpressionAsync($"(() => {{ const e = {Editor}; const d = JSON.parse(JSON.stringify(e.getProjectData())); e.setComponents('<p>x</p>'); e.loadProjectData(d); }})()");
		await WaitAsync($"() => {Editor}.getWrapper().findType('theme-ref').length === 1");
		Assert.Equal(before, await HtmlAsync());
		await OpenPickerAsync();
		Assert.Equal(name, await ValueAsync("themeSelect"));
	}

	[Fact]
	public async Task The_marker_is_hidden_and_out_of_the_way()
	{
		var name = await SaveThemeAsync("Hidden");
		await StartAsync($"<div class=\"doc-theme theme-{name}\"></div><p>Body</p>");
		const string marker = Editor + ".getWrapper().findType('theme-ref')[0]";
		Assert.Equal("none", await EvalAsync<string>($"getComputedStyle({marker}.view.el).display"));
		Assert.False(await EvalAsync<bool>($"{marker}.get('selectable')"));
		Assert.False(await EvalAsync<bool>($"{marker}.get('layerable')"));
	}

	[Fact]
	public async Task Clauses_have_no_theme()
	{
		await StartAsync();
		await ActAsync("(() => { const k = document.getElementById('docKind'); k.value = 'clauses'; k.dispatchEvent(new Event('change')); })()");
		try
		{
			Assert.True(await EvalAsync<bool>("document.getElementById('btnTheme').disabled"));
		}
		finally
		{
			await ActAsync("(() => { const k = document.getElementById('docKind'); k.value = 'templates'; k.dispatchEvent(new Event('change')); })()");
		}
	}

	// ---- Managing themes -----------------------------------------------------------------------------------------

	[Fact]
	public async Task A_new_theme_starts_from_the_standard_colors()
	{
		await StartAsync();
		await OpenManagerAsync();
		Assert.Equal("Manage themes", await EvalAsync<string>("document.querySelector('.gjs-mdl-title').textContent"));
		Assert.Equal("", await ValueAsync("tmTheme"));
		Assert.False(await EvalAsync<bool>("document.getElementById('tmName').disabled"));
		Assert.Equal("#144835", await ValueAsync("tmColor-moe-green"));
		Assert.Equal("", await ValueAsync("tmBodyFont"));
		Assert.True(await EvalAsync<bool>("document.getElementById('tmDelete').hidden"));
		Assert.Contains("Georgia", await EvalAsync<string[]>("[...document.getElementById('tmBodyFont').options].map(o => o.value)"));
	}

	[Fact]
	public async Task Saving_a_new_theme()
	{
		var name = Unique("made");
		await StartAsync();
		await OpenManagerAsync();
		await SetAsync("tmName", name);
		await SetAsync("tmLabel", "Made Here");
		await SetAsync("tmDescription", "Commercial lines");
		await SetAsync("tmColor-moe-green", "#123456");
		Assert.True(await EvalAsync<bool>("document.getElementById('tmColor-moe-green').closest('.tm-color').classList.contains('changed')"));
		await SetAsync("tmBodyFont", "Georgia");
		Assert.StartsWith("Saved theme Made Here.", await ActAsync("document.getElementById('tmSave').click()"));

		var theme = await fixture.Http.GetFromJsonAsync<JsonElement>($"api/themes/{name}");
		Assert.Equal("Commercial lines", theme.GetProperty("description").GetString());
		Assert.Equal("Georgia", theme.GetProperty("bodyFont").GetString());
		var colors = theme.GetProperty("colors").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString());
		Assert.Equal(new Dictionary<string, string?> { ["moe-green"] = "#123456" }, colors);
		Assert.Equal(name, await ValueAsync("tmTheme"));
		Assert.True(await EvalAsync<bool>("document.getElementById('tmName').disabled"));
	}

	[Fact]
	public async Task Bad_input_is_explained()
	{
		await StartAsync();
		await OpenManagerAsync();
		await SetAsync("tmName", "has space");
		await Page.EvaluateExpressionAsync("document.getElementById('tmSave').click()");
		Assert.Equal("The short name may only contain letters, numbers, \"-\" and \"_\".", await ErrorAsync());

		await SetAsync("tmName", Unique("nolabel"));
		await Page.EvaluateExpressionAsync("document.getElementById('tmError').textContent = ''; document.getElementById('tmSave').click()");
		Assert.Equal("Give the theme a name of up to 60 characters.", await ErrorAsync());

		await SetAsync("tmName", "moe");
		await SetAsync("tmLabel", "Mine");
		await Page.EvaluateExpressionAsync("document.getElementById('tmError').textContent = ''; document.getElementById('tmSave').click()");
		Assert.StartsWith("Theme names may contain", await ErrorAsync());
	}

	[Fact]
	public async Task Editing_an_existing_theme()
	{
		var name = await SaveThemeAsync("Editable", "#c00000", "Verdana");
		await StartAsync();
		await OpenManagerAsync();
		await SetAsync("tmTheme", name);
		Assert.Equal(name, await ValueAsync("tmName"));
		Assert.True(await EvalAsync<bool>("document.getElementById('tmName').disabled"));
		Assert.Equal("Editable", await ValueAsync("tmLabel"));
		Assert.Equal("#c00000", await ValueAsync("tmColor-moe-green"));
		Assert.Equal("Verdana", await ValueAsync("tmBodyFont"));
		Assert.False(await EvalAsync<bool>("document.getElementById('tmDelete').hidden"));

		// Reset puts the standard color back, so saving drops the override.
		await Page.EvaluateExpressionAsync("document.getElementById('tmColor-moe-green').closest('.tm-color').querySelector('.tm-reset').click()");
		Assert.Equal("#144835", await ValueAsync("tmColor-moe-green"));
		await ActAsync("document.getElementById('tmSave').click()");
		Assert.Equal(0, (await fixture.Http.GetFromJsonAsync<JsonElement>($"api/themes/{name}")).GetProperty("colors").EnumerateObject().Count());
	}

	[Fact]
	public async Task Saving_the_theme_in_use_restyles_the_canvas()
	{
		var name = await SaveThemeAsync("Live", "#c00000");
		await StartAsync($"<div class=\"doc-theme theme-{name}\"></div><h1 id=\"title\">Heading</h1>");
		await WaitForHeadingColorAsync("rgb(192, 0, 0)");
		var before = await ThemeLinkAsync();
		await OpenManagerAsync();
		await SetAsync("tmTheme", name);
		await SetAsync("tmColor-moe-green", "#00c000");
		await ActAsync("document.getElementById('tmSave').click()");
		Assert.NotEqual(before, await ThemeLinkAsync());
		await WaitForHeadingColorAsync("rgb(0, 192, 0)");
	}

	[Fact]
	public async Task Deleting_themes()
	{
		var unused = await SaveThemeAsync("Unused");
		var used = await SaveThemeAsync("Used");
		var template = Unique("tpl");
		(await fixture.Http.PutAsJsonAsync($"api/templates/{template}/draft", new { project = new { }, html = $"<div class=\"doc-theme theme-{used}\"></div><p>x</p>", css = "" })).EnsureSuccessStatusCode();

		await StartAsync();
		await OpenManagerAsync();
		await SetAsync("tmTheme", used);
		await Page.EvaluateExpressionAsync("document.getElementById('tmDelete').click()");
		var error = await ErrorAsync();
		Assert.StartsWith("Templates still use this theme", error);
		Assert.Contains(template, error);

		await SetAsync("tmTheme", unused);
		Assert.Equal($"Deleted theme {unused}.", await ActAsync("document.getElementById('tmDelete').click()"));
		Assert.DoesNotContain(unused, await EvalAsync<string[]>("[...document.getElementById('tmTheme').options].map(o => o.value)"));
		Assert.Equal(System.Net.HttpStatusCode.NotFound, (await fixture.Http.GetAsync($"api/themes/{unused}")).StatusCode);
	}

	[Fact]
	public async Task Uploading_a_font_and_using_it()
	{
		var family = "Ui Mono " + Guid.NewGuid().ToString("N")[..6];
		await StartAsync();
		await OpenManagerAsync();
		var file = await Page.QuerySelectorAsync("#tmFontFile");
		await file.UploadFileAsync(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "consola.ttf"));
		await WaitAsync("() => document.getElementById('tmFontFamily').value.length > 0");
		Assert.Equal("consola", await ValueAsync("tmFontFamily"));
		await SetAsync("tmFontFamily", family);
		await SetAsync("tmFontWeight", "700");
		Assert.Equal($"Uploaded {family} 700.", await ActAsync("document.getElementById('tmFontUpload').click()"));
		Assert.Contains(family, await EvalAsync<string>("document.querySelector('.tm-fonts').textContent"));
		Assert.Contains(family, await EvalAsync<string[]>("[...document.getElementById('tmBodyFont').options].map(o => o.value)"));
		Assert.Contains(family, await EvalAsync<string[]>("[...document.getElementById('tmHeadingFont').options].map(o => o.value)"));

		var name = Unique("with-font");
		await SetAsync("tmName", name);
		await SetAsync("tmLabel", "With font");
		await SetAsync("tmHeadingFont", family);
		await ActAsync("document.getElementById('tmSave').click()");
		Assert.Equal(family, (await fixture.Http.GetFromJsonAsync<JsonElement>($"api/themes/{name}")).GetProperty("headingFont").GetString());
	}

	[Fact]
	public async Task Font_upload_problems_are_explained()
	{
		await StartAsync();
		await OpenManagerAsync();
		await Page.EvaluateExpressionAsync("document.getElementById('tmFontUpload').click()");
		Assert.Equal("Choose a font file to upload.", await ErrorAsync());

		var notFont = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".ttf");
		await File.WriteAllTextAsync(notFont, "this is not a font at all");
		try
		{
			var file = await Page.QuerySelectorAsync("#tmFontFile");
			await file.UploadFileAsync(notFont);
			await SetAsync("tmFontFamily", "Not A Font");
			await Page.EvaluateExpressionAsync("document.getElementById('tmError').textContent = ''; document.getElementById('tmFontUpload').click()");
			Assert.Equal("That isn't a font file (WOFF2, WOFF, TTF or OTF).", await ErrorAsync());
		}
		finally
		{
			File.Delete(notFont);
		}
	}

	[Fact]
	public async Task Deleting_an_uploaded_font()
	{
		var family = "Gone Mono " + Guid.NewGuid().ToString("N")[..6];
		var content = new ByteArrayContent(ThemeStoreTests.Consolas);
		content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
		var font = await (await fixture.Http.PostAsync($"api/themes/fonts?family={Uri.EscapeDataString(family)}", content)).Content.ReadFromJsonAsync<JsonElement>();
		var id = font.GetProperty("id").GetString();

		await StartAsync();
		await OpenManagerAsync();
		await Page.EvaluateFunctionAsync("id => document.querySelector('.tm-font-delete[data-font-id=\"' + id + '\"]').click()", id!);
		await WaitAsync("id => !document.querySelector('.tm-font-delete[data-font-id=\"' + id + '\"]')", id!);
		Assert.DoesNotContain(family, await EvalAsync<string[]>("[...document.getElementById('tmBodyFont').options].map(o => o.value)"));
	}
}
