using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FaCT.DocDesigner.POC.Tests.Clauses;
using PuppeteerSharp;

namespace FaCT.DocDesigner.POC.Tests.Gallery;

/// <summary>/api/gallery, /api/{kind}/{name}/preview.html and /api/{kind}/{name}/duplicate.</summary>
public sealed class GalleryEndpointTests(ClauseAppFactory factory) : IClassFixture<ClauseAppFactory>
{
	private readonly HttpClient _client = factory.CreateClient();

	private static string Unique(string name) => name + "-" + Guid.NewGuid().ToString("N")[..8];

	private async Task<int> SaveAsync(string kind, string name, string html, object? model = null)
	{
		var response = await _client.PutAsJsonAsync($"/api/{kind}/{name}/draft", new { project = new { pages = new[] { new { id = "p" } } }, html, css = ".x{color:red}", model });
		response.EnsureSuccessStatusCode();
		return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("version").GetInt32();
	}

	private async Task<int> PublishAsync(string kind, string name, string html, object? model = null)
	{
		var version = await SaveAsync(kind, name, html, model);
		(await _client.PostAsync($"/api/{kind}/{name}/versions/{version}/publish", null)).EnsureSuccessStatusCode();
		return version;
	}

	private async Task<JsonElement> EntryAsync(string kind, string name) =>
		(await _client.GetFromJsonAsync<JsonElement[]>("/api/gallery"))!.Single(e => e.GetProperty("kind").GetString() == kind && e.GetProperty("name").GetString() == name);

	private async Task<HttpResponseMessage> DuplicateAsync(string kind, string name, object body) =>
		await _client.PostAsJsonAsync($"/api/{kind}/{name}/duplicate", body);

	private static async Task<string> ErrorAsync(HttpResponseMessage response) =>
		(await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()!;

	[Fact]
	public async Task The_gallery_lists_templates_and_clauses_with_their_state()
	{
		var t = Unique("t");
		var c = Unique("c");
		await PublishAsync("templates", t, "<p>v1</p>");
		await SaveAsync("templates", t, "<p>v2</p>");
		await PublishAsync("clauses", c, "<p>clause</p>");
		(await _client.PutAsJsonAsync($"/api/templates/{t}/scenarios/Minimal", new { data = new { a = 1 } })).EnsureSuccessStatusCode();
		(await _client.PostAsJsonAsync($"/api/templates/{t}/comments", new { anchor = "abcdefgh", author = "A", text = "open" })).EnsureSuccessStatusCode();
		var resolved = await (await _client.PostAsJsonAsync($"/api/templates/{t}/comments", new { anchor = "abcdefgh", author = "A", text = "done" })).Content.ReadFromJsonAsync<JsonElement>();
		await _client.PostAsJsonAsync($"/api/templates/{t}/comments/{resolved.GetProperty("id").GetString()}/resolved", new { resolved = true });

		var template = await EntryAsync("templates", t);
		Assert.Equal(2, template.GetProperty("latestVersion").GetInt32());
		Assert.Equal("Draft", template.GetProperty("latestStatus").GetString());
		Assert.Equal(1, template.GetProperty("publishedVersion").GetInt32());
		Assert.Equal(2, template.GetProperty("versions").GetInt32());
		Assert.Equal(1, template.GetProperty("scenarios").GetInt32());
		Assert.Equal(1, template.GetProperty("openComments").GetInt32());
		Assert.True(DateTimeOffset.Parse(template.GetProperty("savedUtc").GetString()!) > DateTimeOffset.UtcNow.AddMinutes(-5));

		var clause = await EntryAsync("clauses", c);
		Assert.Equal("Published", clause.GetProperty("latestStatus").GetString());

		var onlyClauses = (await _client.GetFromJsonAsync<JsonElement[]>("/api/gallery?kind=clauses"))!;
		Assert.All(onlyClauses, e => Assert.Equal("clauses", e.GetProperty("kind").GetString()));
		Assert.Equal(HttpStatusCode.BadRequest, (await _client.GetAsync("/api/gallery?kind=widgets")).StatusCode);
	}

	[Fact]
	public async Task The_gallery_is_sorted_by_name()
	{
		var names = (await _client.GetFromJsonAsync<JsonElement[]>("/api/gallery"))!.Select(e => e.GetProperty("name").GetString()!).ToList();
		Assert.Equal(names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase), names);
	}

	[Fact]
	public async Task Preview_renders_the_published_version_with_its_own_data()
	{
		var t = Unique("t");
		await PublishAsync("templates", t, "<p>Published for {{ policy.number }}</p>", new { policy = new { number = "PV-1" } });
		await SaveAsync("templates", t, "<p>Draft wording</p>");
		using var response = await _client.GetAsync($"/api/templates/{t}/preview.html");
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
		Assert.Contains("default-src 'none'", response.Headers.GetValues("Content-Security-Policy").Single());
		Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
		var html = await response.Content.ReadAsStringAsync();
		Assert.Contains("Published for PV-1", html);
		Assert.Contains(".x{color:red}", html);

		var draft = await _client.GetStringAsync($"/api/templates/{t}/preview.html?version=2");
		Assert.Contains("Draft wording", draft);
	}

	[Fact]
	public async Task Preview_of_a_never_published_template_is_the_newest_version()
	{
		var t = Unique("t");
		await SaveAsync("templates", t, "<p>Only a draft</p>");
		Assert.Contains("Only a draft", await _client.GetStringAsync($"/api/templates/{t}/preview.html"));
	}

	[Fact]
	public async Task Preview_of_a_template_that_cannot_render_says_why_as_text()
	{
		var t = Unique("t");
		await SaveAsync("templates", t, "{% include 'no-such-clause' %}");
		var html = await _client.GetStringAsync($"/api/templates/{t}/preview.html");
		Assert.Contains("no-such-clause", html);
		Assert.DoesNotContain("<script", html);
	}

	[Fact]
	public async Task Preview_errors()
	{
		Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/templates/{Unique("none")}/preview.html")).StatusCode);
		Assert.Equal(HttpStatusCode.BadRequest, (await _client.GetAsync("/api/templates/bad.name/preview.html")).StatusCode);
		Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/api/widgets/x/preview.html")).StatusCode);
	}

	[Fact]
	public async Task Duplicate_copies_the_published_version_as_a_new_draft()
	{
		var t = Unique("t");
		var copy = Unique("copy");
		await PublishAsync("templates", t, "<p>Published</p>", new { policy = new { number = "M-1" } });
		await SaveAsync("templates", t, "<p>Newer draft</p>");
		(await _client.PutAsJsonAsync($"/api/templates/{t}/scenarios/Minimal", new { data = new { a = 1 } })).EnsureSuccessStatusCode();
		(await _client.PostAsJsonAsync($"/api/templates/{t}/comments", new { anchor = "abcdefgh", author = "A", text = "not copied" })).EnsureSuccessStatusCode();

		var response = await DuplicateAsync("templates", t, new { newName = copy });
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		var result = await response.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(copy, result.GetProperty("name").GetString());
		Assert.Equal(1, result.GetProperty("version").GetInt32());
		Assert.Equal("Draft", result.GetProperty("status").GetString());
		Assert.Equal(1, result.GetProperty("from").GetProperty("version").GetInt32());
		Assert.Equal(1, result.GetProperty("scenariosCopied").GetInt32());

		var copied = await _client.GetFromJsonAsync<JsonElement>($"/api/templates/{copy}/versions/1");
		Assert.Equal("<p>Published</p>", copied.GetProperty("html").GetString());
		Assert.Equal(".x{color:red}", copied.GetProperty("css").GetString());
		Assert.Equal("M-1", copied.GetProperty("model").GetProperty("policy").GetProperty("number").GetString());
		Assert.Equal("p", copied.GetProperty("project").GetProperty("pages")[0].GetProperty("id").GetString());
		Assert.Equal(["Minimal"], (await _client.GetFromJsonAsync<JsonElement[]>($"/api/templates/{copy}/scenarios"))!.Select(s => s.GetProperty("name").GetString()));
		Assert.Empty((await _client.GetFromJsonAsync<JsonElement[]>($"/api/templates/{copy}/comments"))!);

		// The original is untouched.
		Assert.Equal(2, (await _client.GetFromJsonAsync<JsonElement[]>($"/api/templates/{t}/versions"))!.Length);
	}

	[Fact]
	public async Task Duplicate_can_copy_a_chosen_version_without_scenarios()
	{
		var t = Unique("t");
		var copy = Unique("copy");
		await PublishAsync("templates", t, "<p>One</p>");
		await SaveAsync("templates", t, "<p>Two</p>");
		(await _client.PutAsJsonAsync($"/api/templates/{t}/scenarios/Minimal", new { data = new { a = 1 } })).EnsureSuccessStatusCode();
		var response = await DuplicateAsync("templates", t, new { newName = copy, version = 2, includeScenarios = false });
		Assert.Equal(0, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("scenariosCopied").GetInt32());
		Assert.Equal("<p>Two</p>", (await _client.GetFromJsonAsync<JsonElement>($"/api/templates/{copy}/versions/1")).GetProperty("html").GetString());
		Assert.Empty((await _client.GetFromJsonAsync<JsonElement[]>($"/api/templates/{copy}/scenarios"))!);
	}

	[Fact]
	public async Task Duplicate_of_a_never_published_template_copies_the_newest()
	{
		var t = Unique("t");
		var copy = Unique("copy");
		await SaveAsync("templates", t, "<p>Draft only</p>");
		await DuplicateAsync("templates", t, new { newName = copy });
		Assert.Equal("<p>Draft only</p>", (await _client.GetFromJsonAsync<JsonElement>($"/api/templates/{copy}/versions/1")).GetProperty("html").GetString());
	}

	[Fact]
	public async Task Clauses_duplicate_into_clauses()
	{
		var c = Unique("c");
		var copy = Unique("copy");
		await PublishAsync("clauses", c, "<p>Clause</p>");
		(await DuplicateAsync("clauses", c, new { newName = copy })).EnsureSuccessStatusCode();
		Assert.Single((await _client.GetFromJsonAsync<JsonElement[]>($"/api/clauses/{copy}/versions"))!);
		Assert.Empty((await _client.GetFromJsonAsync<JsonElement[]>($"/api/templates/{copy}/versions"))!);
	}

	[Fact]
	public async Task Duplicate_refuses_an_existing_name()
	{
		var t = Unique("t");
		var other = Unique("other");
		await PublishAsync("templates", t, "<p>A</p>");
		await SaveAsync("templates", other, "<p>B</p>");
		var response = await DuplicateAsync("templates", t, new { newName = other });
		Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
		Assert.Equal($"'{other}' already exists. Choose another name.", await ErrorAsync(response));
		Assert.Equal("<p>B</p>", (await _client.GetFromJsonAsync<JsonElement>($"/api/templates/{other}/versions/1")).GetProperty("html").GetString());
	}

	[Theory]
	[InlineData("")]
	[InlineData("bad name")]
	[InlineData("../x")]
	public async Task Duplicate_refuses_bad_names(string newName)
	{
		var t = Unique("t");
		await SaveAsync("templates", t, "<p>A</p>");
		var response = await DuplicateAsync("templates", t, new { newName });
		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		Assert.StartsWith("The new name may only contain", await ErrorAsync(response));
	}

	[Fact]
	public async Task Duplicate_of_missing_templates_or_versions_is_not_found()
	{
		var t = Unique("t");
		Assert.Equal(HttpStatusCode.NotFound, (await DuplicateAsync("templates", t, new { newName = Unique("x") })).StatusCode);
		await SaveAsync("templates", t, "<p>A</p>");
		var response = await DuplicateAsync("templates", t, new { newName = Unique("x"), version = 9 });
		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
		Assert.Contains("no v9 version", await ErrorAsync(response));
		Assert.Equal(HttpStatusCode.NotFound, (await DuplicateAsync("widgets", t, new { newName = "x" })).StatusCode);
	}
}

/// <summary>The gallery and duplicating in the designer.</summary>
[Collection("Isolated designer")]
public sealed class GalleryDesignerTests(IsolatedDesignerFixture fixture)
{
	private IPage Page => fixture.Page;

	private static string Unique(string name) => name + "-" + Guid.NewGuid().ToString("N")[..8];

	private Task<T> EvalAsync<T>(string script) => Page.EvaluateExpressionAsync<T>(script);

	private Task WaitAsync(string predicate) => Page.WaitForFunctionAsync(predicate, new WaitForFunctionOptions { Timeout = 30_000 });

	private async Task<string> ActAsync(string script)
	{
		await Page.EvaluateExpressionAsync("document.getElementById('status').textContent = ''");
		await Page.EvaluateExpressionAsync(script);
		await WaitAsync("() => { const t = document.getElementById('status').textContent; return t.length > 0 && !t.endsWith('...'); }");
		return await EvalAsync<string>("document.getElementById('status').textContent");
	}

	/// <summary>A template saved (and optionally published) through the designer, so it has a real project.</summary>
	private async Task<string> DesignAsync(string html, bool publish)
	{
		var name = Unique("tpl");
		await ActAsync("(() => { const k = document.getElementById('docKind'); k.value = 'templates'; k.dispatchEvent(new Event('change')); })()");
		await ActAsync($"(() => {{ const n = document.getElementById('templateName'); n.value = '{name}'; n.dispatchEvent(new Event('change')); }})()");
		await Page.EvaluateFunctionAsync("h => { const e = grapesjs.editors[0]; e.select(null); e.setComponents(h); e.setStyle(''); }", html);
		await ActAsync(publish ? "document.getElementById('btnPublish').click()" : "document.getElementById('btnSave').click()");
		return name;
	}

	private async Task<string> OpenGalleryAsync()
	{
		var status = await ActAsync("document.getElementById('btnGallery').click()");
		await WaitAsync("() => !!document.querySelector('.gallery .gallery-grid')");
		return status;
	}

	private Task<string[]> CardsAsync() =>
		EvalAsync<string[]>("[...document.querySelectorAll('.gallery .gallery-card')].map(c => c.getAttribute('data-doc'))");

	private Task SetAsync(string id, string value) => Page.EvaluateFunctionAsync(
		"(id, v) => { const el = document.getElementById(id); el.value = v; el.dispatchEvent(new Event(el.tagName === 'SELECT' ? 'change' : 'input')); }", id, value);

	[Fact]
	public async Task The_gallery_shows_cards_with_thumbnails_and_state()
	{
		var name = await DesignAsync("<h1>Gallery heading</h1><p>Body text</p>", publish: true);
		var status = await OpenGalleryAsync();
		Assert.EndsWith("in the gallery.", status);
		Assert.Contains($"templates/{name}", await CardsAsync());
		var card = $"document.querySelector('.gallery-card[data-doc=\"templates/{name}\"]')";
		Assert.Equal("v1 Published", await EvalAsync<string>(card + ".querySelector('.gallery-status').textContent"));
		Assert.Equal("Template", await EvalAsync<string>(card + ".querySelector('.gallery-kind').textContent"));
		Assert.Equal("", await EvalAsync<string>(card + ".querySelector('iframe').getAttribute('sandbox')"));
		Assert.EndsWith($"/api/templates/{name}/preview.html", await EvalAsync<string>(card + ".querySelector('iframe').src"));
		await Page.EvaluateExpressionAsync("grapesjs.editors[0].Modal.close()");

		// The thumbnail page itself shows the document.
		Assert.Contains("Gallery heading", await fixture.Http.GetStringAsync($"api/templates/{name}/preview.html"));
	}

	[Fact]
	public async Task Find_kind_and_sort_narrow_the_gallery()
	{
		var first = await DesignAsync("<p>First</p>", publish: false);
		var second = await DesignAsync("<p>Second</p>", publish: false);
		await OpenGalleryAsync();

		await SetAsync("galleryFilter", second);
		Assert.Equal([$"templates/{second}"], await CardsAsync());
		await SetAsync("galleryFilter", "zzz-nothing");
		Assert.Equal("Nothing matches.", await EvalAsync<string>("document.querySelector('.gallery .pane-empty').textContent"));
		await SetAsync("galleryFilter", "");

		await SetAsync("galleryKind", "clauses");
		Assert.DoesNotContain(await CardsAsync(), c => c.StartsWith("templates/", StringComparison.Ordinal));
		await SetAsync("galleryKind", "");

		await SetAsync("gallerySort", "recent");
		var recent = await CardsAsync();
		Assert.True(Array.IndexOf(recent, $"templates/{second}") < Array.IndexOf(recent, $"templates/{first}"));
		await Page.EvaluateExpressionAsync("grapesjs.editors[0].Modal.close()");
	}

	[Fact]
	public async Task Open_loads_the_newest_version()
	{
		var name = await DesignAsync("<p>Open me</p>", publish: true);
		await Page.EvaluateExpressionAsync("grapesjs.editors[0].setComponents('<p>Elsewhere</p>')");
		await ActAsync("(() => { const n = document.getElementById('templateName'); n.value = 'something-else'; n.dispatchEvent(new Event('change')); })()");
		await OpenGalleryAsync();
		var status = await ActAsync($"document.querySelector('.gallery-card[data-doc=\"templates/{name}\"] .gallery-open').click()");
		Assert.Equal($"Opened {name} v1 (Published).", status);
		Assert.Equal(name, await EvalAsync<string>("document.getElementById('templateName').value"));
		Assert.Contains("Open me", await EvalAsync<string>("grapesjs.editors[0].getHtml()"));
	}

	[Fact]
	public async Task Duplicate_from_the_gallery_opens_the_copy()
	{
		var name = await DesignAsync("<p>Copy this wording</p>", publish: true);
		await OpenGalleryAsync();
		await Page.EvaluateExpressionAsync($"document.querySelector('.gallery-card[data-doc=\"templates/{name}\"] .gallery-duplicate').click()");
		await WaitAsync("() => !!document.getElementById('duplicateName')");
		Assert.Equal(name + "-copy", await EvalAsync<string>("document.getElementById('duplicateName').value"));
		Assert.Equal(["v1 (Published)"], await EvalAsync<string[]>("[...document.getElementById('duplicateVersion').options].map(o => o.textContent)"));
		Assert.True(await EvalAsync<bool>("document.getElementById('duplicateScenarios').checked"));

		var status = await ActAsync("document.getElementById('duplicateGo').click()");
		Assert.Equal($"Duplicated {name} v1 as {name}-copy (draft v1).", status);
		Assert.Equal(name + "-copy", await EvalAsync<string>("document.getElementById('templateName').value"));
		Assert.Equal("v1 Draft", await EvalAsync<string>("document.getElementById('versionBadge').textContent"));
		Assert.Contains("Copy this wording", await EvalAsync<string>("grapesjs.editors[0].getHtml()"));
	}

	[Fact]
	public async Task Duplicating_to_a_taken_name_explains()
	{
		var name = await DesignAsync("<p>A</p>", publish: false);
		var taken = await DesignAsync("<p>B</p>", publish: false);
		await OpenGalleryAsync();
		await Page.EvaluateExpressionAsync($"document.querySelector('.gallery-card[data-doc=\"templates/{name}\"] .gallery-duplicate').click()");
		await WaitAsync("() => !!document.getElementById('duplicateName')");
		await Page.EvaluateFunctionAsync("n => document.getElementById('duplicateName').value = n", taken);
		await Page.EvaluateExpressionAsync("document.getElementById('duplicateGo').click()");
		await WaitAsync("() => document.getElementById('duplicateError').textContent.length > 0");
		Assert.Contains("already exists", await EvalAsync<string>("document.getElementById('duplicateError').textContent"));

		await Page.EvaluateExpressionAsync("document.getElementById('duplicateName').value = 'bad name!'");
		await Page.EvaluateExpressionAsync("document.getElementById('duplicateGo').click()");
		Assert.StartsWith("The new name may only contain", await EvalAsync<string>("document.getElementById('duplicateError').textContent"));
		await Page.EvaluateExpressionAsync("document.getElementById('duplicateCancel').click()");
	}

	[Fact]
	public async Task The_menu_duplicates_the_open_template_offering_both_versions()
	{
		var name = await DesignAsync("<p>Published text</p>", publish: true);
		await Page.EvaluateExpressionAsync("grapesjs.editors[0].setComponents('<p>Draft text</p>')");
		await ActAsync("document.getElementById('btnSave').click()");
		await Page.EvaluateExpressionAsync("grapesjs.editors[0].setComponents('<p>Unsaved text</p>')");

		await ActAsync("document.getElementById('btnDuplicate').click()");
		await WaitAsync("() => !!document.getElementById('duplicateName')");
		Assert.Equal(["v1 (Published)", "v2 (Draft, newest)"], await EvalAsync<string[]>("[...document.getElementById('duplicateVersion').options].map(o => o.textContent)"));
		Assert.Contains("Unsaved changes on the canvas are not included.", await EvalAsync<string>("document.querySelector('.duplicate .model-help').textContent"));
		await Page.EvaluateExpressionAsync("document.getElementById('duplicateVersion').value = '2'");
		var status = await ActAsync("document.getElementById('duplicateGo').click()");
		Assert.Equal($"Duplicated {name} v2 as {name}-copy (draft v1).", status);
		var html = await EvalAsync<string>("grapesjs.editors[0].getHtml()");
		Assert.Contains("Draft text", html);
		Assert.DoesNotContain("Unsaved text", html);
	}

	[Fact]
	public async Task The_menu_needs_a_saved_version()
	{
		await ActAsync($"(() => {{ const n = document.getElementById('templateName'); n.value = '{Unique("unsaved")}'; n.dispatchEvent(new Event('change')); }})()");
		var status = await ActAsync("document.getElementById('btnDuplicate').click()");
		Assert.StartsWith("Save Draft first", status);
	}
}
