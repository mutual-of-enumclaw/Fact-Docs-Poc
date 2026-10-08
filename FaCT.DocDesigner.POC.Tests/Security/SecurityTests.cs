using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FaCT.DocDesigner.POC.Security;
using FaCT.DocDesigner.POC.Tests.Clauses;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using PuppeteerSharp;

namespace FaCT.DocDesigner.POC.Tests.Security;

public sealed class AuditLogTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "audit-tests-" + Guid.NewGuid().ToString("N"));
	private readonly AuditLog _log;

	public AuditLogTests() => _log = new AuditLog(_root);

	public void Dispose()
	{
		try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
	}

	[Fact]
	public async Task Entries_come_back_newest_first_and_filter()
	{
		await _log.AppendAsync("Ann", "draft.saved", "templates", "a", 1);
		await _log.AppendAsync("Pat", "version.published", "templates", "a", 1, "first");
		await _log.AppendAsync("Ann", "draft.saved", "templates", "b", 1);
		await _log.AppendAsync("Rita", "comment.added", "clauses", "a", detail: "c1");

		var all = await _log.ReadAsync();
		Assert.Equal(["comment.added", "draft.saved", "version.published", "draft.saved"], all.Select(e => e.Action));
		Assert.Equal(["version.published", "draft.saved"], (await _log.ReadAsync("templates", "a")).Select(e => e.Action));
		Assert.Equal(2, (await _log.ReadAsync(user: "ann")).Count);
		Assert.Equal(["comment.added"], (await _log.ReadAsync(action: "comment.")).Select(e => e.Action));
		Assert.Single(await _log.ReadAsync(take: 1));
		Assert.Equal("first", all[2].Detail);
		Assert.True(all[0].TimeUtc >= all[1].TimeUtc);
	}

	[Fact]
	public async Task Last_saved_by_finds_who_saved_a_version()
	{
		await _log.AppendAsync("Ann", "draft.saved", "templates", "a", 1);
		await _log.AppendAsync("Lee", "draft.saved", "templates", "a", 1);
		await _log.AppendAsync("Ann", "draft.saved", "templates", "a", 2);
		Assert.Equal("Lee", await _log.LastSavedByAsync("templates", "a", 1));
		Assert.Equal("Ann", await _log.LastSavedByAsync("templates", "a", 2));
		Assert.Null(await _log.LastSavedByAsync("templates", "a", 3));
		Assert.Null(await _log.LastSavedByAsync("clauses", "a", 1));
	}

	[Fact]
	public async Task Long_details_are_cut_and_an_empty_log_reads_empty()
	{
		Assert.Empty(await _log.ReadAsync());
		await _log.AppendAsync("A", "x", detail: new string('d', 900));
		Assert.Equal(500, (await _log.ReadAsync()).Single().Detail!.Length);
	}

	[Fact]
	public async Task The_log_is_one_json_object_per_line()
	{
		await _log.AppendAsync("A", "one");
		await _log.AppendAsync("B", "two");
		var lines = await File.ReadAllLinesAsync(Path.Combine(_root, "audit.jsonl"));
		Assert.Equal(2, lines.Length);
		Assert.All(lines, l => Assert.Equal(JsonValueKind.Object, JsonDocument.Parse(l).RootElement.ValueKind));
	}

	[Fact]
	public async Task Concurrent_appends_are_all_kept()
	{
		await Task.WhenAll(Enumerable.Range(0, 50).Select(i => _log.AppendAsync("U" + i, "a")));
		Assert.Equal(50, (await _log.ReadAsync(take: 100)).Count);
	}
}

/// <summary>The designer with security on: sign-in, roles per action, a second person to publish, audit.</summary>
public sealed class SecureAppFactory : WebApplicationFactory<Program>
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
			["Blocks:Root"] = Folder.BlocksRoot,
			["Comments:Root"] = Folder.CommentsRoot,
			["Spelling:Root"] = Folder.SpellingRoot,
			["Audit:Root"] = Folder.AuditRoot,
			["Themes:Root"] = Folder.ThemesRoot,
			["Security:Enabled"] = "true",
			["Security:RequireSecondPersonToPublish"] = "true"
		}));
	}

	protected override void Dispose(bool disposing)
	{
		base.Dispose(disposing);
		if (disposing) Folder.Dispose();
	}
}

public sealed class SecurityEndpointTests(SecureAppFactory factory) : IClassFixture<SecureAppFactory>
{
	private static string Unique(string name) => name + "-" + Guid.NewGuid().ToString("N")[..8];

	private static object Draft(string html) => new { project = new { }, html, css = "" };

	private HttpClient Anonymous() => factory.CreateClient();

	private async Task<HttpClient> SignInAsync(string id)
	{
		var client = factory.CreateClient();
		var response = await client.PostAsJsonAsync("/api/signin", new { id });
		Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
		return client;
	}

	private static async Task<string> ErrorAsync(HttpResponseMessage response) =>
		(await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()!;

	[Fact]
	public async Task Without_signing_in_only_the_page_version_users_and_me_answer()
	{
		var client = Anonymous();
		var me = await client.GetAsync("/api/me");
		Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
		Assert.True((await me.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("securityEnabled").GetBoolean());
		Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/version")).StatusCode);
		Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/")).StatusCode);
		Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/designer.js")).StatusCode);
		foreach (var url in new[] { "/api/templates", "/api/gallery", "/api/sample-data", "/api/audit", "/api/blocks" })
		{
			var response = await client.GetAsync(url);
			Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
			Assert.Equal("Sign in to use the designer.", await ErrorAsync(response));
		}
		Assert.Equal(HttpStatusCode.Unauthorized, (await client.PutAsJsonAsync($"/api/templates/{Unique("t")}/draft", Draft("<p/>"))).StatusCode);
	}

	[Fact]
	public async Task Users_can_be_listed_for_sign_in()
	{
		var users = (await Anonymous().GetFromJsonAsync<JsonElement[]>("/api/users"))!;
		Assert.Contains(users, u => u.GetProperty("id").GetString() == "ann" && u.GetProperty("roles").EnumerateArray().Single().GetString() == "Author");
		Assert.Contains(users, u => u.GetProperty("id").GetString() == "viewer" && u.GetProperty("roles").GetArrayLength() == 0);
	}

	[Fact]
	public async Task Sign_in_sets_a_strict_http_only_cookie_and_me_reports_the_roles()
	{
		var client = Anonymous();
		var response = await client.PostAsJsonAsync("/api/signin", new { id = "PAT" });
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		var cookie = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("designer.auth=", StringComparison.Ordinal)).ToLowerInvariant();
		Assert.Contains("httponly", cookie);
		Assert.Contains("samesite=strict", cookie);

		var me = await client.GetFromJsonAsync<JsonElement>("/api/me");
		Assert.Equal("Pat Publisher", me.GetProperty("name").GetString());
		Assert.Equal(["Reviewer", "Publisher"], me.GetProperty("roles").EnumerateArray().Select(r => r.GetString()));
		Assert.True(me.GetProperty("requireSecondPersonToPublish").GetBoolean());

		Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/signout", null)).StatusCode);
		Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/me")).StatusCode);
	}

	[Theory]
	[InlineData("nobody")]
	[InlineData("")]
	[InlineData(null)]
	public async Task Unknown_users_cannot_sign_in(string? id)
	{
		var response = await Anonymous().PostAsJsonAsync("/api/signin", new { id });
		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
	}

	[Fact]
	public async Task Authors_save_but_cannot_publish_and_publishers_publish()
	{
		var name = Unique("t");
		var ann = await SignInAsync("ann");
		Assert.Equal(HttpStatusCode.OK, (await ann.PutAsJsonAsync($"/api/templates/{name}/draft", Draft("<p>Ann</p>"))).StatusCode);
		var denied = await ann.PostAsync($"/api/templates/{name}/versions/1/publish", null);
		Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
		Assert.Equal("Your role doesn't allow this.", await ErrorAsync(denied));

		var pat = await SignInAsync("pat");
		Assert.Equal(HttpStatusCode.Forbidden, (await pat.PutAsJsonAsync($"/api/templates/{name}/draft", Draft("<p>Pat</p>"))).StatusCode);
		Assert.Equal(HttpStatusCode.OK, (await pat.PostAsync($"/api/templates/{name}/versions/1/publish", null)).StatusCode);
	}

	[Fact]
	public async Task Someone_else_must_publish_what_you_saved()
	{
		var name = Unique("t");
		var lee = await SignInAsync("lead");
		(await lee.PutAsJsonAsync($"/api/templates/{name}/draft", Draft("<p>Lee</p>"))).EnsureSuccessStatusCode();
		var own = await lee.PostAsync($"/api/templates/{name}/versions/1/publish", null);
		Assert.Equal(HttpStatusCode.Forbidden, own.StatusCode);
		Assert.Equal("Someone else must publish v1: you saved it.", await ErrorAsync(own));

		var pat = await SignInAsync("pat");
		Assert.Equal(HttpStatusCode.OK, (await pat.PostAsync($"/api/templates/{name}/versions/1/publish", null)).StatusCode);
	}

	[Fact]
	public async Task Clauses_follow_the_same_roles()
	{
		var name = Unique("c");
		var rita = await SignInAsync("rita");
		Assert.Equal(HttpStatusCode.Forbidden, (await rita.PutAsJsonAsync($"/api/clauses/{name}/draft", Draft("<p/>"))).StatusCode);
		var ann = await SignInAsync("ann");
		(await ann.PutAsJsonAsync($"/api/clauses/{name}/draft", Draft("<p>Clause</p>"))).EnsureSuccessStatusCode();
		Assert.Equal(HttpStatusCode.Forbidden, (await ann.PostAsync($"/api/clauses/{name}/versions/1/publish", null)).StatusCode);
		var pat = await SignInAsync("pat");
		Assert.Equal(HttpStatusCode.OK, (await pat.PostAsync($"/api/clauses/{name}/versions/1/publish", null)).StatusCode);
	}

	[Fact]
	public async Task Everyone_signed_in_can_read_and_render()
	{
		var name = Unique("t");
		var ann = await SignInAsync("ann");
		(await ann.PutAsJsonAsync($"/api/templates/{name}/draft", Draft("<p>Readable</p>"))).EnsureSuccessStatusCode();
		var val = await SignInAsync("viewer");
		Assert.Equal(HttpStatusCode.OK, (await val.GetAsync($"/api/templates/{name}/versions/1")).StatusCode);
		Assert.Equal(HttpStatusCode.OK, (await val.GetAsync("/api/gallery")).StatusCode);
		Assert.Equal(HttpStatusCode.OK, (await val.GetAsync($"/api/templates/{name}/preview.html")).StatusCode);
		Assert.Equal(HttpStatusCode.OK, (await val.PostAsJsonAsync("/api/render", new { html = "<p>x</p>", css = "" })).StatusCode);
	}

	[Fact]
	public async Task Changes_need_the_author_role()
	{
		var name = Unique("t");
		var ann = await SignInAsync("ann");
		(await ann.PutAsJsonAsync($"/api/templates/{name}/draft", Draft("<p>x</p>"))).EnsureSuccessStatusCode();
		var rita = await SignInAsync("rita");
		var refused = new[]
		{
			await rita.PutAsJsonAsync($"/api/templates/{name}/scenarios/S", new { data = new { a = 1 } }),
			await rita.PutAsJsonAsync("/api/blocks/b", new { label = "B", components = new { } }),
			await rita.PostAsJsonAsync($"/api/templates/{name}/duplicate", new { newName = Unique("copy") }),
			await rita.PostAsJsonAsync("/api/spelling/words", new { word = "Zorbex" }),
			await rita.DeleteAsync($"/api/templates/{name}/draft"),
			await rita.PostAsync("/api/import", new ByteArrayContent([1]))
		};
		Assert.All(refused, r => Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode));
		Assert.Equal(HttpStatusCode.OK, (await ann.PutAsJsonAsync($"/api/templates/{name}/scenarios/S", new { data = new { a = 1 } })).StatusCode);
	}

	[Fact]
	public async Task Comments_need_a_role_and_are_by_the_signed_in_person()
	{
		var name = Unique("t");
		var val = await SignInAsync("viewer");
		Assert.Equal(HttpStatusCode.Forbidden,
			(await val.PostAsJsonAsync($"/api/templates/{name}/comments", new { anchor = "abcdefgh", author = "Val", text = "x" })).StatusCode);
		Assert.Equal(HttpStatusCode.OK, (await val.GetAsync($"/api/templates/{name}/comments")).StatusCode);

		var rita = await SignInAsync("rita");
		var added = await rita.PostAsJsonAsync($"/api/templates/{name}/comments", new { anchor = "abcdefgh", author = "Someone Else", text = "Looks good" });
		var comment = await added.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal("Rita Reviewer", comment.GetProperty("author").GetString());
		// The name is taken from the sign-in even when the page sends none.
		var reply = await rita.PostAsJsonAsync($"/api/templates/{name}/comments/{comment.GetProperty("id").GetString()}/replies", new { text = "Reply" });
		Assert.Equal("Rita Reviewer", (await reply.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("replies")[0].GetProperty("author").GetString());
	}

	[Fact]
	public async Task The_audit_log_records_who_did_what()
	{
		var name = Unique("t");
		var ann = await SignInAsync("ann");
		(await ann.PutAsJsonAsync($"/api/templates/{name}/draft", Draft("<p>v1</p>"))).EnsureSuccessStatusCode();
		await ann.PostAsync($"/api/templates/{name}/versions/1/publish", null); // refused: Author
		var pat = await SignInAsync("pat");
		(await pat.PostAsync($"/api/templates/{name}/versions/1/publish", null)).EnsureSuccessStatusCode();
		(await ann.PutAsJsonAsync($"/api/templates/{name}/draft", Draft("<p>v2</p>"))).EnsureSuccessStatusCode();
		(await ann.PutAsJsonAsync($"/api/templates/{name}/scenarios/Minimal", new { data = new { a = 1 } })).EnsureSuccessStatusCode();
		(await pat.PostAsync($"/api/templates/{name}/versions/2/publish", null)).EnsureSuccessStatusCode();
		(await pat.PostAsync($"/api/templates/{name}/versions/1/publish", null)).EnsureSuccessStatusCode();

		var history = (await pat.GetFromJsonAsync<JsonElement[]>($"/api/audit?kind=templates&name={name}"))!;
		Assert.Equal(
			[("Pat Publisher", "version.rolled-back", 1), ("Pat Publisher", "version.published", 2), ("Ann Author", "scenario.saved", 0),
			 ("Ann Author", "draft.saved", 2), ("Pat Publisher", "version.published", 1), ("Ann Author", "draft.saved", 1)],
			history.Select(e => (e.GetProperty("user").GetString(), e.GetProperty("action").GetString(),
				e.GetProperty("version").ValueKind == JsonValueKind.Number ? e.GetProperty("version").GetInt32() : 0)));
		Assert.Equal("v2 retired", history[0].GetProperty("detail").GetString());

		var denied = (await pat.GetFromJsonAsync<JsonElement[]>("/api/audit?user=Ann%20Author&action=access.denied"))!;
		Assert.Contains(denied, e => e.GetProperty("detail").GetString() == $"POST /api/templates/{name}/versions/1/publish");
		var signIns = (await pat.GetFromJsonAsync<JsonElement[]>("/api/audit?action=user.signed-in&take=5"))!;
		Assert.InRange(signIns.Length, 1, 5);
	}
}

/// <summary>With security off (the default) everyone is the local designer with every role; changes are still audited.</summary>
public sealed class OpenSecurityTests(ClauseAppFactory factory) : IClassFixture<ClauseAppFactory>
{
	[Fact]
	public async Task Everyone_is_the_local_designer_with_every_role()
	{
		var client = factory.CreateClient();
		var me = await client.GetFromJsonAsync<JsonElement>("/api/me");
		Assert.Equal(SecurityOptions.LocalUserName, me.GetProperty("name").GetString());
		Assert.Equal(DesignerRoles.All, me.GetProperty("roles").EnumerateArray().Select(r => r.GetString()!));
		Assert.False(me.GetProperty("securityEnabled").GetBoolean());
		Assert.Empty((await client.GetFromJsonAsync<JsonElement[]>("/api/users"))!);
		Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/signin", new { id = "ann" })).StatusCode);
	}

	[Fact]
	public async Task Changes_are_audited_as_the_local_designer_and_comments_keep_the_typed_name()
	{
		var client = factory.CreateClient();
		var name = "open-" + Guid.NewGuid().ToString("N")[..8];
		(await client.PutAsJsonAsync($"/api/templates/{name}/draft", new { project = new { }, html = "<p/>", css = "" })).EnsureSuccessStatusCode();
		(await client.PostAsync($"/api/templates/{name}/versions/1/publish", null)).EnsureSuccessStatusCode();
		var history = (await client.GetFromJsonAsync<JsonElement[]>($"/api/audit?kind=templates&name={name}"))!;
		Assert.All(history, e => Assert.Equal(SecurityOptions.LocalUserName, e.GetProperty("user").GetString()));
		Assert.Equal(["version.published", "draft.saved"], history.Select(e => e.GetProperty("action").GetString()));

		var comment = await (await client.PostAsJsonAsync($"/api/templates/{name}/comments", new { anchor = "abcdefgh", author = "Typed Name", text = "x" }))
			.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal("Typed Name", comment.GetProperty("author").GetString());
	}
}

/// <summary>A designer with sign-in on and "a second person must publish".</summary>
public sealed class SecureDesignerFixture : IsolatedDesignerFixture
{
	protected override IEnumerable<string> Settings => ["--Security:Enabled=true", "--Security:RequireSecondPersonToPublish=true"];

	protected override string Ready => "() => !!document.getElementById('signin')";
}

[CollectionDefinition("Secure designer")]
public sealed class SecureDesignerCollection : ICollectionFixture<SecureDesignerFixture>;

/// <summary>Signing in, what each role can do in the designer, and the History dialog.</summary>
[Collection("Secure designer")]
public sealed class SecureDesignerTests(SecureDesignerFixture fixture)
{
	private IPage Page => fixture.Page;

	private Task<T> EvalAsync<T>(string script) => Page.EvaluateExpressionAsync<T>(script);

	private Task WaitAsync(string predicate) => Page.WaitForFunctionAsync(predicate, new WaitForFunctionOptions { Timeout = 60_000 });

	private async Task<string> ActAsync(string script)
	{
		await Page.EvaluateExpressionAsync("document.getElementById('status').textContent = ''");
		await Page.EvaluateExpressionAsync(script);
		await WaitAsync("() => { const t = document.getElementById('status').textContent; return t.length > 0 && !t.endsWith('...'); }");
		return await EvalAsync<string>("document.getElementById('status').textContent");
	}

	/// <summary>Signs out (if needed), then signs in as the user through the sign-in screen.</summary>
	private async Task SignInAsync(string id)
	{
		await Page.EvaluateExpressionAsync("fetch('/api/signout', { method: 'POST' })");
		await Page.GoToAsync(fixture.BaseUri.ToString(), WaitUntilNavigation.Networkidle0);
		await WaitAsync("() => !!document.getElementById('signin')");
		var navigation = Page.WaitForNavigationAsync(new NavigationOptions { WaitUntil = [WaitUntilNavigation.Networkidle0] });
		await Page.EvaluateFunctionAsync("id => document.querySelector('.signin-user[data-user=\"' + id + '\"]').click()", id);
		await navigation;
		await WaitAsync("() => window.grapesjs && grapesjs.editors.length > 0 && document.getElementById('status').textContent.length > 0");
	}

	private async Task<string> NewTemplateAsync(string html)
	{
		var name = "sec-" + Guid.NewGuid().ToString("N")[..8];
		await ActAsync($"(() => {{ const n = document.getElementById('templateName'); n.value = '{name}'; n.dispatchEvent(new Event('change')); }})()");
		await Page.EvaluateFunctionAsync("h => grapesjs.editors[0].setComponents(h)", html);
		return name;
	}

	[Fact]
	public async Task The_sign_in_screen_lists_the_users_with_their_roles()
	{
		await Page.EvaluateExpressionAsync("fetch('/api/signout', { method: 'POST' })");
		await Page.GoToAsync(fixture.BaseUri.ToString(), WaitUntilNavigation.Networkidle0);
		await WaitAsync("() => !!document.getElementById('signin')");
		Assert.Contains("Ann Author", await EvalAsync<string>("document.getElementById('signin').textContent"));
		Assert.Equal("View only", await EvalAsync<string>("document.querySelector('.signin-user[data-user=viewer] small').textContent"));
		Assert.Equal("Publisher, Reviewer", await EvalAsync<string>("document.querySelector('.signin-user[data-user=pat] small').textContent"));
		// Nothing of the designer loads before signing in.
		Assert.True(await EvalAsync<bool>("!window.grapesjs || grapesjs.editors.length === 0"));
	}

	[Fact]
	public async Task An_author_can_save_but_not_publish()
	{
		await SignInAsync("ann");
		Assert.Equal("Ann Author \u00b7 Author", await EvalAsync<string>("document.getElementById('userChip').textContent"));
		Assert.False(await EvalAsync<bool>("document.getElementById('btnSignOut').hidden"));
		Assert.True(await EvalAsync<bool>("document.getElementById('btnPublish').disabled"));
		Assert.Contains("needs the Publisher role", await EvalAsync<string>("document.getElementById('btnPublish').title"));
		Assert.False(await EvalAsync<bool>("document.getElementById('btnSave').disabled"));

		await NewTemplateAsync("<p>Ann's draft</p>");
		Assert.Equal("Saved draft v1.", await ActAsync("document.getElementById('btnSave').click()"));
		// Still not publishable after saving.
		Assert.True(await EvalAsync<bool>("document.getElementById('btnPublish').disabled"));
	}

	[Fact]
	public async Task A_reviewer_comments_as_themselves_but_cannot_save()
	{
		await SignInAsync("rita");
		Assert.True(await EvalAsync<bool>("document.getElementById('btnSave').disabled"));
		Assert.True(await EvalAsync<bool>("document.getElementById('btnImportDocument').disabled"));
		Assert.False(await EvalAsync<bool>("document.getElementById('btnAddComment').disabled"));
		Assert.Equal("Rita Reviewer", await EvalAsync<string>("document.getElementById('commentAuthor').value"));
		Assert.True(await EvalAsync<bool>("document.getElementById('commentAuthor').readOnly"));

		await NewTemplateAsync("<h2>Heading</h2>");
		await Page.EvaluateExpressionAsync("(() => { const e = grapesjs.editors[0]; e.select(e.getWrapper().find('h2')[0]); })()");
		await Page.EvaluateExpressionAsync("document.getElementById('commentText').value = 'Please reword'");
		Assert.Equal("Comment added.", await ActAsync("document.getElementById('btnAddComment').click()"));
		Assert.StartsWith("Rita Reviewer", await EvalAsync<string>("document.querySelector('#commentList .comment-meta').textContent"));
	}

	[Fact]
	public async Task A_viewer_can_look_but_not_change_or_comment()
	{
		await SignInAsync("viewer");
		Assert.Equal("Val Viewer \u00b7 View only", await EvalAsync<string>("document.getElementById('userChip').textContent"));
		foreach (var id in new[] { "btnSave", "btnPublish", "btnAddComment", "btnDuplicate", "btnSaveBlock" })
		{
			Assert.True(await EvalAsync<bool>($"document.getElementById('{id}').disabled"), id);
		}
		Assert.False(await EvalAsync<bool>("document.getElementById('btnPreview').disabled"));
	}

	[Fact]
	public async Task The_person_who_saved_cannot_publish_and_history_shows_who_did_what()
	{
		await SignInAsync("lead");
		var name = await NewTemplateAsync("<p>Lead's change</p>");
		Assert.Equal("Saved draft v1.", await ActAsync("document.getElementById('btnSave').click()"));
		var status = await ActAsync("document.getElementById('btnPublish').click()");
		Assert.Contains("Someone else must publish v1: you saved it.", status);

		await SignInAsync("pat");
		await ActAsync($"(() => {{ const n = document.getElementById('templateName'); n.value = '{name}'; n.dispatchEvent(new Event('change')); }})()");
		await ActAsync("document.getElementById('btnOpen').click()");
		Assert.Equal("v1 is now published.", await ActAsync("document.getElementById('btnPublish').click()"));

		await ActAsync("document.getElementById('btnHistory').click()");
		var rows = await EvalAsync<string[]>("[...document.querySelectorAll('.history-row')].map(r => [...r.cells].map(c => c.textContent).join(' | '))");
		Assert.Contains(rows, r => r.Contains("Pat Publisher | Published | v1", StringComparison.Ordinal));
		Assert.Contains(rows, r => r.Contains("Lee Lead | Was refused | v1 | publish own changes", StringComparison.Ordinal));
		Assert.Contains(rows, r => r.Contains("Lee Lead | Saved draft | v1", StringComparison.Ordinal));
		Assert.Equal("History of " + name, await EvalAsync<string>("document.querySelector('.gjs-mdl-title').textContent"));

		await ActAsync("(() => { const t = document.getElementById('historyAll'); t.checked = true; t.dispatchEvent(new Event('change')); })()");
		Assert.Equal("All activity", await EvalAsync<string>("document.querySelector('.gjs-mdl-title').textContent"));
		Assert.Contains(await EvalAsync<string[]>("[...document.querySelectorAll('.history-row')].map(r => r.getAttribute('data-action'))"), a => a == "user.signed-in");
		await Page.EvaluateExpressionAsync("grapesjs.editors[0].Modal.close()");
	}

	[Fact]
	public async Task Signing_out_returns_to_the_sign_in_screen()
	{
		await SignInAsync("ann");
		var navigation = Page.WaitForNavigationAsync(new NavigationOptions { WaitUntil = [WaitUntilNavigation.Networkidle0] });
		await Page.EvaluateExpressionAsync("document.getElementById('btnSignOut').click()");
		await navigation;
		await WaitAsync("() => !!document.getElementById('signin')");
	}
}
