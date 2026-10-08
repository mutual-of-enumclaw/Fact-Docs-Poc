using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FaCT.DocDesigner.POC.Embedding;
using FaCT.DocDesigner.POC.Templates;
using FaCT.DocDesigner.POC.Tests.Clauses;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PuppeteerSharp;

namespace FaCT.DocDesigner.POC.Tests.Embedding;

public sealed class TemplateDetailsRuleTests
{
	[Theory]
	[InlineData(null, null, null, null, null)]
	[InlineData("Business Auto Declarations", "DA 00 93", "12/24", "Policy form", "Declarations")]
	[InlineData(null, "MCP 1001", "10 26", null, null)]
	[InlineData(null, "IL-00.17", null, "Correspondence", "Letters")]
	public void Valid_details_have_no_problem(string? title, string? code, string? edition, string? type, string? category)
	{
		Assert.Null(new TemplateDetails(title, code, edition, type, category).Problem());
	}

	[Theory]
	[InlineData(null, "DA 00 93", "13/24", null, null, "edition")]
	[InlineData(null, "DA 00 93", "2024", null, null, "edition")]
	[InlineData(null, "<b>x</b>", null, null, null, "form code")]
	[InlineData(null, "ABCDEFGHIJKLMNOPQRSTUVWXYZ", null, null, null, "form code")]
	[InlineData(null, null, null, "Spreadsheet", null, "type")]
	[InlineData(null, null, null, null, "Misc", "category")]
	public void Invalid_details_say_what_is_wrong(string? title, string? code, string? edition, string? type, string? category, string what)
	{
		Assert.Contains(what, new TemplateDetails(title, code, edition, type, category).Problem(), StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public void A_long_title_is_refused()
	{
		Assert.NotNull(new TemplateDetails(new string('x', TemplateDetails.MaxTitle + 1)).Problem());
	}

	[Fact]
	public void Normalized_trims_and_drops_empty_values()
	{
		Assert.Equal(new TemplateDetails("Dec", "DA 00 93"), new TemplateDetails(" Dec ", "DA 00 93 ", "", "  ", null).Normalized());
		Assert.Null(new TemplateDetails("", " ").Normalized());
	}

	[Theory]
	[InlineData("https://commercial.moe.com", "https://commercial.moe.com")]
	[InlineData("https://commercial.moe.com/", "https://commercial.moe.com")]
	[InlineData("https://web.moe.com:8443", "https://web.moe.com:8443")]
	[InlineData("http://localhost:8080", "http://localhost:8080")]
	[InlineData("http://127.0.0.1:5173", "http://127.0.0.1:5173")]
	[InlineData("http://commercial.moe.com", null)]
	[InlineData("https://commercial.moe.com/app", null)]
	[InlineData("https://*.moe.com", null)]
	[InlineData("javascript:alert(1)", null)]
	[InlineData("*", null)]
	[InlineData("", null)]
	public void Only_plain_https_origins_or_local_http_are_allowed(string value, string? expected)
	{
		Assert.Equal(expected, EmbedOptions.Normalize(value));
	}
}

/// <summary>The designer in memory with temp stores and Commercial Web (plus some invalid entries) allowed to embed it.</summary>
public sealed class EmbedAppFactory : WebApplicationFactory<Program>
{
	internal TempStores Folder { get; } = new();

	protected override void ConfigureWebHost(IWebHostBuilder builder)
	{
		builder.UseEnvironment("Development");
		builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
		{
			["Templates:Root"] = Folder.TemplatesRoot,
			["Clauses:Root"] = Folder.ClausesRoot,
			["Scenarios:Root"] = Folder.ScenariosRoot,
			["Comments:Root"] = Folder.CommentsRoot,
			["Audit:Root"] = Folder.AuditRoot,
			["Embed:AllowedOrigins:0"] = "https://commercial.example.com/",
			["Embed:AllowedOrigins:1"] = "http://insecure.example.com",
			["Embed:AllowedOrigins:2"] = "https://*.example.com"
		}));
	}

	protected override void Dispose(bool disposing)
	{
		base.Dispose(disposing);
		if (disposing) Folder.Dispose();
	}
}

public sealed class EmbedAndDetailsEndpointTests(EmbedAppFactory factory) : IClassFixture<EmbedAppFactory>
{
	private readonly HttpClient _client = factory.CreateClient();

	private static object Draft(object? details) => new
	{
		project = new { },
		html = "<body><p>Declarations</p></body>",
		css = "",
		model = new { policy = new { number = "CAP 1" } },
		details
	};

	[Fact]
	public async Task Only_valid_allowed_origins_are_published()
	{
		var config = await _client.GetFromJsonAsync<JsonElement>("/api/embed");
		Assert.Equal(["https://commercial.example.com"], config.GetProperty("allowedOrigins").EnumerateArray().Select(e => e.GetString()));
	}

	[Theory]
	[InlineData("/")]
	[InlineData("/index.html")]
	[InlineData("/api/features")]
	public async Task Only_this_site_and_the_allowed_origins_may_frame_the_pages(string path)
	{
		using var response = await _client.GetAsync(path);
		Assert.Equal("frame-ancestors 'self' https://commercial.example.com", response.Headers.GetValues("Content-Security-Policy").Single());
	}

	[Fact]
	public async Task Details_are_saved_with_the_version_and_listed()
	{
		var name = "dec-" + Guid.NewGuid().ToString("N")[..8];
		using var saved = await _client.PutAsJsonAsync($"/api/templates/{name}/draft",
			Draft(new { title = " Business Auto Declarations ", formCode = "DA 00 93", edition = "12/24", type = "Policy form", category = "Declarations" }));
		Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

		var version = await _client.GetFromJsonAsync<JsonElement>($"/api/templates/{name}/versions/1");
		var details = version.GetProperty("details");
		Assert.Equal("Business Auto Declarations", details.GetProperty("title").GetString());
		Assert.Equal("DA 00 93", details.GetProperty("formCode").GetString());
		Assert.Equal("12/24", details.GetProperty("edition").GetString());

		var versions = await _client.GetFromJsonAsync<JsonElement>($"/api/templates/{name}/versions");
		Assert.Equal("Declarations", versions[0].GetProperty("details").GetProperty("category").GetString());

		var list = await _client.GetFromJsonAsync<JsonElement>("/api/templates");
		var entry = list.EnumerateArray().Single(t => t.GetProperty("name").GetString() == name);
		Assert.Equal("Draft", entry.GetProperty("latestStatus").GetString());
		Assert.Equal("DA 00 93", entry.GetProperty("details").GetProperty("formCode").GetString());
	}

	[Fact]
	public async Task Invalid_details_are_refused_and_nothing_is_saved()
	{
		var name = "dec-" + Guid.NewGuid().ToString("N")[..8];
		using var response = await _client.PutAsJsonAsync($"/api/templates/{name}/draft", Draft(new { edition = "2024" }));
		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		Assert.Contains("edition", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
		Assert.Empty(await _client.GetFromJsonAsync<JsonElement[]>($"/api/templates/{name}/versions") ?? []);
	}

	[Fact]
	public async Task A_template_without_details_still_saves()
	{
		var name = "dec-" + Guid.NewGuid().ToString("N")[..8];
		using var response = await _client.PutAsJsonAsync($"/api/templates/{name}/draft", Draft(null));
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		var version = await _client.GetFromJsonAsync<JsonElement>($"/api/templates/{name}/versions/1");
		Assert.True(!version.TryGetProperty("details", out var d) || d.ValueKind == JsonValueKind.Null);
	}

	[Fact]
	public async Task The_choices_come_from_the_server()
	{
		var options = await _client.GetFromJsonAsync<JsonElement>("/api/template-details");
		Assert.Contains("Policy form", options.GetProperty("types").EnumerateArray().Select(e => e.GetString()));
		Assert.Contains("Declarations", options.GetProperty("categories").EnumerateArray().Select(e => e.GetString()));
	}

	[Theory]
	[InlineData("https://commercial.example.com", true)]
	[InlineData("https://evil.example.com", false)]
	[InlineData("http://insecure.example.com", false)]
	public async Task Only_allowed_host_apps_may_call_the_api_from_their_pages(string origin, bool allowed)
	{
		using var preflight = new HttpRequestMessage(HttpMethod.Options, "/api/templates/x/pdf");
		preflight.Headers.Add("Origin", origin);
		preflight.Headers.Add("Access-Control-Request-Method", "POST");
		preflight.Headers.Add("Access-Control-Request-Headers", "content-type,authorization,x-user-id");
		using var response = await _client.SendAsync(preflight);
		Assert.Equal(allowed, response.Headers.TryGetValues("Access-Control-Allow-Origin", out var values) && values.Single() == origin);

		using var get = new HttpRequestMessage(HttpMethod.Get, "/api/templates");
		get.Headers.Add("Origin", origin);
		using var listed = await _client.SendAsync(get);
		Assert.Equal(allowed, listed.Headers.Contains("Access-Control-Allow-Origin"));
	}

	[Fact]
	public async Task Document_models_come_from_the_models_folder_with_their_samples()
	{
		var models = await _client.GetFromJsonAsync<JsonElement>("/api/document-models");
		var dec = models.EnumerateArray().Single(m => m.GetProperty("name").GetString() == "commercial-auto-dec");
		Assert.Equal("Commercial Auto Declarations - document model", dec.GetProperty("title").GetString());
		Assert.True(dec.GetProperty("hasSample").GetBoolean());

		var sample = await _client.GetFromJsonAsync<JsonElement>("/api/document-models/commercial-auto-dec/sample");
		Assert.Equal("Apex Plumbing LLC", sample.GetProperty("quote").GetProperty("namedInsured").GetProperty("name").GetString());
		var schema = await _client.GetFromJsonAsync<JsonElement>("/api/document-models/commercial-auto-dec/schema");
		Assert.Equal("object", schema.GetProperty("type").GetString());

		Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/api/document-models/commercial-auto-dec/other")).StatusCode);
		Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/api/document-models/..%2F..%2Fsecrets/sample")).StatusCode);
		Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/api/document-models/no-such-model/sample")).StatusCode);
	}
}

/// <summary>
/// A designer that a host page on another origin may embed: http://127.0.0.1:{port}, a small web app started by the test
/// that serves the host page (request interception can't serve a page whose frames load from another origin).
/// </summary>
public sealed class EmbedDesignerFixture : IsolatedDesignerFixture
{
	private readonly int _hostPort = DesignerServer.FreePort();
	private WebApplication? _host;

	public string HostOrigin => $"http://127.0.0.1:{_hostPort}";

	/// <summary>The host page HTML, by path (e.g. "/host.html").</summary>
	public Dictionary<string, string> HostPages { get; } = [];

	protected override IEnumerable<string> Settings => [$"--Embed:AllowedOrigins:0={HostOrigin}"];

	public async Task EnsureHostAsync()
	{
		if (_host is not null) return;
		var builder = WebApplication.CreateSlimBuilder();
		builder.WebHost.UseUrls(HostOrigin);
		builder.Logging.ClearProviders();
		_host = builder.Build();
		_host.MapGet("/{page}", (string page) => HostPages.TryGetValue("/" + page, out var html)
			? Results.Content(html, "text/html")
			: Results.NotFound());
		await _host.StartAsync();
	}

	public override async Task DisposeAsync()
	{
		if (_host is not null) await _host.DisposeAsync();
		await base.DisposeAsync();
	}
}

[CollectionDefinition("Embed designer")]
public sealed class EmbedDesignerCollection : ICollectionFixture<EmbedDesignerFixture>;

/// <summary>The template details panel, and the designer embedded in a host app and driven by window messages.</summary>
[Collection("Embed designer")]
public sealed class EmbedDesignerTests(EmbedDesignerFixture fixture)
{
	private static readonly WaitForFunctionOptions Wait = new() { Timeout = 30_000 };

	private IPage Page => fixture.Page;

	// The host page: an iframe with the designer, a log of the designer's messages, and send(command) -> result.
	private string HostPage(string designerUrl) => $$"""
		<!DOCTYPE html><html><body>
		<iframe id="designer" src="{{designerUrl}}" style="width:1400px;height:900px"></iframe>
		<script>
		window.messages = [];
		const target = new URL('{{designerUrl}}').origin;
		window.addEventListener('message', e => { if (e.origin === target && e.data && e.data.source === 'moe-designer') window.messages.push(e.data); });
		let next = 0;
		window.send = (type, body) => new Promise(resolve => {
		  const id = 'c' + (++next);
		  const listener = e => { if (e.data && e.data.type === 'result' && e.data.id === id) { window.removeEventListener('message', listener); resolve(e.data); } };
		  window.addEventListener('message', listener);
		  document.getElementById('designer').contentWindow.postMessage(Object.assign({ source: 'moe-designer-host', type, id }, body || {}), target);
		});
		</script></body></html>
		""";

	private async Task<IPage> OpenHostAsync(string designerUrl)
	{
		await fixture.EnsureHostAsync();
		var path = "/host-" + Guid.NewGuid().ToString("N")[..8] + ".html";
		fixture.HostPages[path] = HostPage(designerUrl);
		var host = await Page.Browser.NewPageAsync();
		host.Dialog += async (_, e) => await e.Dialog.Accept();
		await host.GoToAsync(fixture.HostOrigin + path);
		return host;
	}

	private static async Task<JsonElement> SendAsync(IPage host, string type, object? body = null)
	{
		var result = await host.EvaluateFunctionAsync<JsonElement>("(t, b) => window.send(t, b)", type, body ?? new { });
		return result;
	}

	private IFrame DesignerFrame(IPage host) => host.Frames.Single(f => f.Url.StartsWith(fixture.BaseUri.ToString(), StringComparison.Ordinal));


	[Fact]
	public async Task The_host_creates_fills_saves_and_publishes_a_template()
	{
		var host = await OpenHostAsync(fixture.BaseUri + "?embed=1");
		try
		{
			await host.WaitForFunctionAsync("() => window.messages.some(m => m.type === 'ready')", Wait);
			var ready = await host.EvaluateExpressionAsync<JsonElement>("window.messages.find(m => m.type === 'ready')");
			Assert.Equal("", ready.GetProperty("state").GetProperty("name").GetString());
			Assert.False(await DesignerFrame(host).EvaluateExpressionAsync<bool>("getComputedStyle(document.querySelector('.app-bar')).display !== 'none'"));

			var name = "embedded-" + Guid.NewGuid().ToString("N")[..8];
			var created = await SendAsync(host, "new", new
			{
				name,
				model = new { policy = new { number = "CAP 0000001 00", premium = 100 } },
				details = new { title = "Business Auto Declarations", formCode = "DA 00 93", edition = "12/24" }
			});
			Assert.True(created.GetProperty("ok").GetBoolean(), created.GetProperty("message").GetString());
			Assert.Equal(name, created.GetProperty("state").GetProperty("name").GetString());
			Assert.Equal("DA 00 93", created.GetProperty("state").GetProperty("details").GetProperty("formCode").GetString());

			var designer = DesignerFrame(host);
			await designer.EvaluateExpressionAsync("grapesjs.editors[0].setComponents('<p>Policy <span class=\"df\" data-field=\"policy.number\"></span></p>')");
			await designer.WaitForFunctionAsync("() => document.querySelector('#gjs iframe').contentDocument.body.textContent.includes('CAP 0000001 00')", Wait);

			// a real record replaces the sample values
			var record = await SendAsync(host, "setData", new { data = new { policy = new { number = "CAP 0501396 00", premium = 2462 } }, label = "Policy CAP 0501396 00" });
			Assert.True(record.GetProperty("ok").GetBoolean());
			Assert.Equal("Policy CAP 0501396 00", record.GetProperty("state").GetProperty("record").GetString());
			await designer.WaitForFunctionAsync("() => document.querySelector('#gjs iframe').contentDocument.body.textContent.includes('CAP 0501396 00')", Wait);

			var saved = await SendAsync(host, "save");
			Assert.True(saved.GetProperty("ok").GetBoolean(), saved.GetProperty("message").GetString());
			Assert.Equal(1, saved.GetProperty("state").GetProperty("version").GetInt32());
			Assert.Equal("Draft", saved.GetProperty("state").GetProperty("status").GetString());
			Assert.False(saved.GetProperty("state").GetProperty("dirty").GetBoolean());

			var stored = await fixture.Http.GetFromJsonAsync<JsonElement>($"api/templates/{name}/versions/1");
			Assert.Equal("Business Auto Declarations", stored.GetProperty("details").GetProperty("title").GetString());
			Assert.Equal("CAP 0000001 00", stored.GetProperty("model").GetProperty("policy").GetProperty("number").GetString());

			var published = await SendAsync(host, "publish");
			Assert.True(published.GetProperty("ok").GetBoolean(), published.GetProperty("message").GetString());
			Assert.Equal("Published", published.GetProperty("state").GetProperty("status").GetString());

			var opened = await SendAsync(host, "open", new { name });
			Assert.True(opened.GetProperty("ok").GetBoolean(), opened.GetProperty("message").GetString());
			Assert.Equal("Published", opened.GetProperty("state").GetProperty("status").GetString());

			// state messages follow edits
			await host.EvaluateExpressionAsync("window.messages = []");
			await designer.EvaluateExpressionAsync("grapesjs.editors[0].getWrapper().append('<p>More</p>')");
			await host.WaitForFunctionAsync("() => window.messages.some(m => m.type === 'state' && m.state.dirty)", Wait);
		}
		finally
		{
			await host.CloseAsync();
		}
	}

	[Fact]
	public async Task Bad_commands_are_refused_with_a_reason()
	{
		var host = await OpenHostAsync(fixture.BaseUri + "?embed=1");
		try
		{
			await host.WaitForFunctionAsync("() => window.messages.some(m => m.type === 'ready')", Wait);
			var unknown = await SendAsync(host, "deleteEverything");
			Assert.False(unknown.GetProperty("ok").GetBoolean());
			Assert.Contains("Unknown command", unknown.GetProperty("message").GetString());

			var missing = await SendAsync(host, "open", new { name = "no-such-template-" + Guid.NewGuid().ToString("N")[..6] });
			Assert.False(missing.GetProperty("ok").GetBoolean());
			Assert.Contains("no saved versions", missing.GetProperty("message").GetString());

			var badName = await SendAsync(host, "open", new { name = "../etc/passwd" });
			Assert.False(badName.GetProperty("ok").GetBoolean());

			var badData = await SendAsync(host, "setData", new { data = "not an object" });
			Assert.False(badData.GetProperty("ok").GetBoolean());
		}
		finally
		{
			await host.CloseAsync();
		}
	}

	[Fact]
	public async Task Unsaved_changes_are_not_thrown_away_by_the_host()
	{
		var host = await OpenHostAsync(fixture.BaseUri + "?embed=1");
		try
		{
			await host.WaitForFunctionAsync("() => window.messages.some(m => m.type === 'ready')", Wait);
			var name = "embedded-" + Guid.NewGuid().ToString("N")[..8];
			Assert.True((await SendAsync(host, "new", new { name })).GetProperty("ok").GetBoolean());
			await DesignerFrame(host).EvaluateExpressionAsync("grapesjs.editors[0].setComponents('<p>Work in progress</p>')");

			var refused = await SendAsync(host, "new", new { name = name + "-2" });
			Assert.False(refused.GetProperty("ok").GetBoolean());
			Assert.Contains("unsaved changes", refused.GetProperty("message").GetString());

			var forced = await SendAsync(host, "new", new { name = name + "-2", discardChanges = true });
			Assert.True(forced.GetProperty("ok").GetBoolean());
		}
		finally
		{
			await host.CloseAsync();
		}
	}

	[Fact]
	public async Task Opened_on_its_own_the_embedded_page_says_it_must_be_embedded()
	{
		var page = await Page.Browser.NewPageAsync();
		try
		{
			await page.GoToAsync(fixture.BaseUri + "?embed=1");
			await page.WaitForFunctionAsync("() => document.getElementById('status').textContent.includes('Embed:AllowedOrigins')", Wait);
		}
		finally
		{
			await page.CloseAsync();
		}
	}

	[Fact]
	public async Task A_page_on_another_origin_cannot_frame_the_designer()
	{
		// the host app's address with another host name: same server, but not an allowed origin
		await fixture.EnsureHostAsync();
		fixture.HostPages["/evil.html"] = $"<iframe id=\"d\" src=\"{fixture.BaseUri}?embed=1\"></iframe>";
		var other = await Page.Browser.NewPageAsync();
		try
		{
			await other.GoToAsync(fixture.HostOrigin.Replace("127.0.0.1", "localhost") + "/evil.html");
			await Task.Delay(2000);
			// blocked by frame-ancestors: the frame never runs the designer
			Assert.Contains(other.Frames, f => f.Url.StartsWith("chrome-error:", StringComparison.Ordinal));
			Assert.DoesNotContain(other.Frames, f => f.Url.StartsWith(fixture.BaseUri.ToString(), StringComparison.Ordinal));
		}
		finally
		{
			await other.CloseAsync();
		}
	}

	[Fact]
	public async Task The_settings_tab_edits_the_template_details_when_nothing_is_selected()
	{
		var name = "details-" + Guid.NewGuid().ToString("N")[..8];
		await Page.EvaluateFunctionAsync("n => { const i = document.getElementById('templateName'); i.value = n; i.dispatchEvent(new Event('change')); }", name);
		await Page.WaitForFunctionAsync("n => document.getElementById('tdName').value === n", Wait, name);
		await Page.EvaluateExpressionAsync("grapesjs.editors[0].select(null)");
		Assert.False(await Page.EvaluateExpressionAsync<bool>("document.getElementById('templateDetails').hidden"));
		Assert.Contains("Policy form", await Page.EvaluateExpressionAsync<string[]>("Array.from(document.querySelectorAll('#tdType option')).map(o => o.value)"));

		async Task TypeAsync(string id, string value) =>
			await Page.EvaluateFunctionAsync("(id, v) => { const el = document.getElementById(id); el.value = v; el.dispatchEvent(new Event(el.tagName === 'SELECT' ? 'change' : 'input')); }", id, value);
		await TypeAsync("tdEdition", "2024");
		Assert.Contains("MM/YY", await Page.EvaluateExpressionAsync<string>("document.getElementById('tdError').textContent"));
		await TypeAsync("tdTitle", "Business Auto Declarations");
		await TypeAsync("tdFormCode", "DA 00 93");
		await TypeAsync("tdEdition", "12/24");
		await TypeAsync("tdType", "Policy form");
		await TypeAsync("tdCategory", "Declarations");
		Assert.Equal("", await Page.EvaluateExpressionAsync<string>("document.getElementById('tdError').textContent"));
		Assert.Contains("dirty", await Page.EvaluateExpressionAsync<string>("document.getElementById('versionBadge').className"));

		await Page.EvaluateExpressionAsync("document.getElementById('status').textContent = ''");
		await Page.EvaluateExpressionAsync("document.getElementById('btnSave').click()");
		await Page.WaitForFunctionAsync("() => document.getElementById('status').textContent.startsWith('Saved')", Wait);
		var stored = await fixture.Http.GetFromJsonAsync<JsonElement>($"api/templates/{name}/versions/1");
		Assert.Equal("Declarations", stored.GetProperty("details").GetProperty("category").GetString());

		// selecting an element shows its settings instead; opening the version brings the details back
		await Page.EvaluateExpressionAsync("grapesjs.editors[0].select(grapesjs.editors[0].getWrapper().components().at(0))");
		Assert.True(await Page.EvaluateExpressionAsync<bool>("document.getElementById('templateDetails').hidden"));
		await TypeAsync("tdFormCode", "");
		await Page.EvaluateExpressionAsync("grapesjs.editors[0].select(null)");
		await Page.EvaluateExpressionAsync("document.getElementById('status').textContent = ''");
		await Page.EvaluateExpressionAsync("document.getElementById('btnOpen').click()");
		await Page.WaitForFunctionAsync("() => document.getElementById('status').textContent.startsWith('Opened')", Wait);
		Assert.Equal("DA 00 93", await Page.EvaluateExpressionAsync<string>("document.getElementById('tdFormCode').value"));
	}
}
