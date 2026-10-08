using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using FaCT.DocDesigner.POC.Rendering;
using FaCT.DocDesigner.POC.Templates;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FaCT.DocDesigner.POC.Tests.Clauses;

/// <summary>The designer in memory with its template and clause stores in a temp folder.</summary>
public sealed class ClauseAppFactory : WebApplicationFactory<Program>
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
			["Themes:Root"] = Folder.ThemesRoot
		}));
	}

	protected override void Dispose(bool disposing)
	{
		base.Dispose(disposing);
		if (disposing) Folder.Dispose();
	}
}

/// <summary>Templates including clauses: what the composer renders (published, pinned, nested, CSS, errors).</summary>
public sealed class ClauseCompositionTests(ClauseAppFactory factory) : IClassFixture<ClauseAppFactory>
{
	private static readonly JsonElement Data = JsonDocument.Parse("""
		{ "policy": { "number": "CPP1234567", "insured": "Acme <b>Bakery</b>" },
		  "locations": [ { "name": "North" }, { "name": "South" } ] }
		""").RootElement.Clone();

	private ClauseStore Store => factory.Services.GetRequiredService<ClauseStore>();
	private DocumentComposer Composer => factory.Services.GetRequiredService<DocumentComposer>();

	// Every test uses its own clause names: the fixture's store is shared by the class.
	private static string Unique(string name) => name + "-" + Guid.NewGuid().ToString("N")[..8];

	private int Publish(string name, string html, string css = "")
	{
		var version = Store.Versions.SaveDraftAsync(name, JsonDocument.Parse("{}").RootElement, html, css, null).GetAwaiter().GetResult().Version;
		Store.Versions.PublishAsync(name, version).GetAwaiter().GetResult();
		return version;
	}

	private int Draft(string name, string html) =>
		Store.Versions.SaveDraftAsync(name, JsonDocument.Parse("{}").RootElement, html, "", null).GetAwaiter().GetResult().Version;

	private async Task<string> ComposeAsync(string html, string css = "")
	{
		var result = await Composer.ComposeAsync(html, css, Data);
		Assert.True(result.Error is null, result.Error);
		return result.Html!;
	}

	private async Task<string> ErrorAsync(string html)
	{
		var result = await Composer.ComposeAsync(html, "", Data);
		Assert.Null(result.Html);
		return result.Error!;
	}

	[Fact]
	public async Task A_published_clause_renders_where_it_is_included_with_the_templates_data()
	{
		var std = Unique("std");
		Publish(std, "<p class=\"std\">Policy {{ policy.number }} is subject to the standard exclusions.</p>");
		var html = await ComposeAsync($"<h1>Declarations</h1>{{% include '{std}' %}}<p>End</p>");
		Assert.Contains("<p class=\"std\">Policy CPP1234567 is subject to the standard exclusions.</p>", html);
		Assert.True(html.IndexOf("Declarations", StringComparison.Ordinal) < html.IndexOf("standard exclusions", StringComparison.Ordinal));
		Assert.True(html.IndexOf("standard exclusions", StringComparison.Ordinal) < html.IndexOf("End", StringComparison.Ordinal));
	}

	[Fact]
	public async Task A_clause_inside_a_loop_sees_the_loop_variable()
	{
		var loc = Unique("loc");
		Publish(loc, "<li>{{ location.name }}</li>");
		var html = await ComposeAsync($"<ul>{{% for location in locations %}}{{% include '{loc}' %}}{{% endfor %}}</ul>");
		Assert.Contains("<li>North</li><li>South</li>", html);
	}

	[Fact]
	public async Task Clause_values_are_html_encoded_like_the_templates()
	{
		var who = Unique("who");
		Publish(who, "<p>{{ policy.insured }}</p>");
		var html = await ComposeAsync($"{{% include '{who}' %}}");
		Assert.Contains("Acme &lt;b&gt;Bakery&lt;/b&gt;", html);
		Assert.DoesNotContain("<b>Bakery</b>", html);
	}

	[Fact]
	public async Task Clause_markup_is_sanitized_like_the_templates()
	{
		var bad = Unique("bad");
		Publish(bad, "<p onclick=\"alert(1)\">Text</p><script>alert(2)</script><img src=\"x\" onerror=\"alert(3)\">");
		var html = await ComposeAsync($"{{% include '{bad}' %}}");
		Assert.Contains("Text", html);
		Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain("onclick", html, StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain("onerror", html, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task Publishing_a_new_clause_version_changes_every_template_that_uses_it()
	{
		var std = Unique("std");
		Publish(std, "<p>Wording v1</p>");
		var template = $"{{% include '{std}' %}}";
		Assert.Contains("Wording v1", await ComposeAsync(template));

		Publish(std, "<p>Wording v2</p>");
		var html = await ComposeAsync(template);
		Assert.Contains("Wording v2", html);
		Assert.DoesNotContain("Wording v1", html);
	}

	[Fact]
	public async Task Rolling_a_clause_back_renders_the_older_wording_again()
	{
		var std = Unique("std");
		Publish(std, "<p>Wording v1</p>");
		Publish(std, "<p>Wording v2</p>");
		var template = $"{{% include '{std}' %}}";
		Assert.Contains("Wording v2", await ComposeAsync(template));
		await Store.Versions.PublishAsync(std, 1);
		Assert.Contains("Wording v1", await ComposeAsync(template));
	}

	[Fact]
	public async Task A_newer_draft_is_never_rendered()
	{
		var std = Unique("std");
		Publish(std, "<p>Published wording</p>");
		Draft(std, "<p>Draft wording</p>");
		var html = await ComposeAsync($"{{% include '{std}' %}}");
		Assert.Contains("Published wording", html);
		Assert.DoesNotContain("Draft wording", html);
	}

	[Fact]
	public async Task A_pinned_include_keeps_its_version_after_newer_ones_are_published()
	{
		var std = Unique("std");
		Publish(std, "<p>Wording v1</p>");
		Publish(std, "<p>Wording v2</p>");
		Publish(std, "<p>Wording v3</p>");
		var html = await ComposeAsync($"<div>{{% include '{std}@1' %}}</div><div>{{% include '{std}' %}}</div><div>{{% include '{std}@2' %}}</div>");
		Assert.Contains("<div><p>Wording v1</p></div><div><p>Wording v3</p></div><div><p>Wording v2</p></div>", html);
	}

	[Fact]
	public async Task Render_tag_works_too()
	{
		var std = Unique("std");
		Publish(std, "<p>Static wording</p>");
		var html = await ComposeAsync($"{{% render '{std}' %}}");
		Assert.Contains("<p>Static wording</p>", html);
	}

	[Fact]
	public async Task Clauses_can_include_other_clauses()
	{
		var inner = Unique("inner");
		var outer = Unique("outer");
		Publish(inner, "<em>{{ policy.number }}</em>");
		Publish(outer, $"<p>Outer {{% include '{inner}' %}} done</p>");
		Assert.Contains("<p>Outer <em>CPP1234567</em> done</p>", await ComposeAsync($"{{% include '{outer}' %}}"));
	}

	[Fact]
	public async Task Clause_css_is_added_to_the_document_including_nested_clauses()
	{
		var inner = Unique("inner");
		var outer = Unique("outer");
		Publish(inner, "<p class=\"in\">i</p>", ".in{color:#123456}");
		Publish(outer, $"{{% include '{inner}' %}}", ".out{color:#654321}");
		var html = await ComposeAsync($"{{% include '{outer}' %}}", ".tpl{color:#abcdef}");
		var head = html[..html.IndexOf("</head>", StringComparison.Ordinal)];
		Assert.Contains(".tpl{color:#abcdef}", head);
		Assert.Contains(".out{color:#654321}", head);
		Assert.Contains(".in{color:#123456}", head);
	}

	[Fact]
	public async Task Clause_css_cannot_close_the_style_element()
	{
		var evil = Unique("evil");
		Publish(evil, "<p>x</p>", ".a{}</style><script>alert(1)</script>");
		var html = await ComposeAsync($"{{% include '{evil}' %}}");
		// What is left of the tag is inert text inside the one style element.
		Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "</style", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
		var body = html[html.IndexOf("<body>", StringComparison.Ordinal)..];
		Assert.DoesNotContain("<script", body, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task A_clause_that_was_never_published_is_a_clear_error()
	{
		var missing = Unique("missing");
		var error = await ErrorAsync($"<p>Before</p>{{% include '{missing}' %}}");
		Assert.Equal($"Clause '{missing}' has no published version (or the pinned version doesn't exist).", error);
	}

	[Fact]
	public async Task A_draft_only_clause_or_pinned_draft_is_a_clear_error()
	{
		var draft = Unique("draft");
		Draft(draft, "<p>Not yet</p>");
		Assert.Contains($"'{draft}'", await ErrorAsync($"{{% include '{draft}' %}}"));
		Assert.Contains($"'{draft}@1'", await ErrorAsync($"{{% include '{draft}@1' %}}"));
	}

	[Fact]
	public async Task A_pinned_version_that_does_not_exist_is_a_clear_error()
	{
		var std = Unique("std");
		Publish(std, "<p>x</p>");
		Assert.Contains($"'{std}@7'", await ErrorAsync($"{{% include '{std}@7' %}}"));
	}

	[Fact]
	public async Task A_missing_nested_clause_is_named_in_the_error()
	{
		var outer = Unique("outer");
		var gone = Unique("gone");
		Publish(outer, $"<p>{{% include '{gone}' %}}</p>");
		Assert.Contains($"'{gone}'", await ErrorAsync($"{{% include '{outer}' %}}"));
	}

	[Fact]
	public async Task Includes_outside_the_clause_store_are_not_found()
	{
		Assert.Contains("could not be found", await ErrorAsync("{% include '../templates/workbench-results' %}"));
		Assert.Contains("could not be found", await ErrorAsync("{% include 'C:/Windows/win.ini' %}"));
	}

	[Fact]
	public async Task Clauses_including_each_other_fail_instead_of_hanging()
	{
		var a = Unique("a");
		var b = Unique("b");
		// Saved straight to the store: the API refuses to save these (see the endpoint tests).
		Publish(a, $"<p>a</p>{{% include '{b}' %}}");
		Publish(b, $"<p>b</p>{{% include '{a}' %}}");
		var task = Composer.ComposeAsync($"{{% include '{a}' %}}", "", Data);
		var finished = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(30)));
		Assert.Same(task, finished);
		var result = await task;
		Assert.Null(result.Html);
		Assert.StartsWith("The template could not be rendered:", result.Error);
	}

	[Fact]
	public async Task Templates_without_clauses_render_as_before()
	{
		var html = await ComposeAsync("<p>{{ policy.number }}</p>", ".x{color:red}");
		Assert.Contains("<p>CPP1234567</p>", html);
		Assert.Contains(".x{color:red}", html);
	}
}

/// <summary>/api/clauses: the clause lifecycle, preview content, self-include checks, usage and rendering.</summary>
public sealed class ClauseEndpointTests(ClauseAppFactory factory) : IClassFixture<ClauseAppFactory>
{
	private readonly HttpClient _client = factory.CreateClient();

	private static string Unique(string name) => name + "-" + Guid.NewGuid().ToString("N")[..8];

	private static object Body(string html, string css = "") => new { project = new { pages = Array.Empty<object>() }, html, css };

	private async Task<JsonElement> SaveAsync(string kind, string name, string html, string css = "")
	{
		var response = await _client.PutAsJsonAsync($"/api/{kind}/{name}/draft", Body(html, css));
		Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
		return await response.Content.ReadFromJsonAsync<JsonElement>();
	}

	private async Task<int> PublishAsync(string kind, string name, string html, string css = "")
	{
		var version = (await SaveAsync(kind, name, html, css)).GetProperty("version").GetInt32();
		var response = await _client.PostAsync($"/api/{kind}/{name}/versions/{version}/publish", null);
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		return version;
	}

	private static async Task<string> ErrorAsync(HttpResponseMessage response) =>
		(await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()!;

	/// <summary>The PDF's text with runs of whitespace collapsed.</summary>
	private static string Words(byte[] pdf) => System.Text.RegularExpressions.Regex.Replace(PdfText.Extract(pdf), @"\s+", " ");

	[Fact]
	public async Task Save_publish_and_list_a_clause()
	{
		var name = Unique("std");
		var saved = await SaveAsync("clauses", name, "<p>Standard</p>", ".std{}");
		Assert.Equal(1, saved.GetProperty("version").GetInt32());
		Assert.Equal("Draft", saved.GetProperty("status").GetString());

		var listed = await _client.GetFromJsonAsync<JsonElement>("/api/clauses");
		var entry = listed.EnumerateArray().Single(c => c.GetProperty("name").GetString() == name);
		Assert.Equal(JsonValueKind.Null, entry.GetProperty("publishedVersion").ValueKind);

		var published = await _client.PostAsync($"/api/clauses/{name}/versions/1/publish", null);
		Assert.Equal("Published", (await published.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());

		listed = await _client.GetFromJsonAsync<JsonElement>("/api/clauses");
		entry = listed.EnumerateArray().Single(c => c.GetProperty("name").GetString() == name);
		Assert.Equal(1, entry.GetProperty("publishedVersion").GetInt32());

		var versions = await _client.GetFromJsonAsync<JsonElement>($"/api/clauses/{name}/versions");
		Assert.Equal("Published", versions[0].GetProperty("status").GetString());

		var version = await _client.GetFromJsonAsync<JsonElement>($"/api/clauses/{name}/versions/1");
		Assert.Equal("<p>Standard</p>", version.GetProperty("html").GetString());
		Assert.Equal(".std{}", version.GetProperty("css").GetString());
	}

	[Fact]
	public async Task Clauses_and_templates_are_separate_lists()
	{
		var name = Unique("sep");
		await SaveAsync("clauses", name, "<p>c</p>");
		var templates = await _client.GetFromJsonAsync<JsonElement>("/api/templates");
		Assert.DoesNotContain(templates.EnumerateArray(), t => t.GetProperty("name").GetString() == name);
		Assert.Equal(0, (await _client.GetFromJsonAsync<JsonElement>($"/api/templates/{name}/versions")).GetArrayLength());
	}

	[Fact]
	public async Task Saving_again_updates_the_draft_and_discard_removes_it()
	{
		var name = Unique("std");
		await PublishAsync("clauses", name, "<p>v1</p>");
		Assert.Equal(2, (await SaveAsync("clauses", name, "<p>v2a</p>")).GetProperty("version").GetInt32());
		Assert.Equal(2, (await SaveAsync("clauses", name, "<p>v2b</p>")).GetProperty("version").GetInt32());
		Assert.Equal("<p>v2b</p>", (await _client.GetFromJsonAsync<JsonElement>($"/api/clauses/{name}/versions/2")).GetProperty("html").GetString());

		Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"/api/clauses/{name}/draft")).StatusCode);
		Assert.Equal(HttpStatusCode.NotFound, (await _client.DeleteAsync($"/api/clauses/{name}/draft")).StatusCode);
		Assert.Equal(1, (await _client.GetFromJsonAsync<JsonElement>($"/api/clauses/{name}/versions")).GetArrayLength());
	}

	[Fact]
	public async Task Content_returns_what_an_include_renders()
	{
		var name = Unique("std");
		await PublishAsync("clauses", name, "<p>v1</p>", ".one{}");
		await PublishAsync("clauses", name, "<p>v2</p>", ".two{}");
		await SaveAsync("clauses", name, "<p>v3 draft</p>");

		var latest = await _client.GetFromJsonAsync<JsonElement>($"/api/clauses/{name}/content");
		Assert.Equal(2, latest.GetProperty("version").GetInt32());
		Assert.Equal("<p>v2</p>", latest.GetProperty("html").GetString());
		Assert.Equal(".two{}", latest.GetProperty("css").GetString());
		Assert.Equal("Published", latest.GetProperty("status").GetString());

		var pinned = await _client.GetFromJsonAsync<JsonElement>($"/api/clauses/{name}/content?version=1");
		Assert.Equal("<p>v1</p>", pinned.GetProperty("html").GetString());
		Assert.Equal("Retired", pinned.GetProperty("status").GetString());

		var draft = await _client.GetAsync($"/api/clauses/{name}/content?version=3");
		Assert.Equal(HttpStatusCode.NotFound, draft.StatusCode);
		Assert.Equal($"Clause '{name}' has no version 3 that can be used.", await ErrorAsync(draft));
	}

	[Fact]
	public async Task Content_of_an_unpublished_clause_is_not_found_with_a_reason()
	{
		var name = Unique("draft");
		await SaveAsync("clauses", name, "<p>d</p>");
		var response = await _client.GetAsync($"/api/clauses/{name}/content");
		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
		Assert.Equal($"Clause '{name}' has no published version.", await ErrorAsync(response));
		Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/clauses/{Unique("none")}/content")).StatusCode);
	}

	[Theory]
	[InlineData("GET", "/api/clauses/bad name/versions")]
	[InlineData("GET", "/api/clauses/bad.name/versions/1")]
	[InlineData("GET", "/api/clauses/bad.name/content")]
	[InlineData("GET", "/api/clauses/bad.name/usage")]
	[InlineData("PUT", "/api/clauses/bad.name/draft")]
	[InlineData("DELETE", "/api/clauses/bad.name/draft")]
	[InlineData("POST", "/api/clauses/bad.name/versions/1/publish")]
	public async Task Invalid_clause_names_are_rejected(string method, string url)
	{
		var request = new HttpRequestMessage(new HttpMethod(method), url);
		if (method == "PUT") request.Content = JsonContent.Create(Body("<p>x</p>"));
		var response = await _client.SendAsync(request);
		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
	}

	[Fact]
	public async Task Unknown_versions_are_not_found()
	{
		var name = Unique("std");
		await SaveAsync("clauses", name, "<p>x</p>");
		Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/clauses/{name}/versions/9")).StatusCode);
		Assert.Equal(HttpStatusCode.NotFound, (await _client.PostAsync($"/api/clauses/{name}/versions/9/publish", null)).StatusCode);
	}

	[Fact]
	public async Task A_model_that_is_not_an_object_is_rejected()
	{
		var response = await _client.PutAsJsonAsync($"/api/clauses/{Unique("m")}/draft", new { project = new { }, html = "<p/>", css = "", model = new[] { 1 } });
		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
	}

	[Fact]
	public async Task A_clause_cannot_include_itself()
	{
		var name = Unique("self");
		foreach (var html in new[] { $"{{% include '{name}' %}}", $"<p>{{% include '{name}@1' %}}</p>", $"{{%- render \"{name}\" -%}}" })
		{
			var response = await _client.PutAsJsonAsync($"/api/clauses/{name}/draft", Body(html));
			Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
			Assert.Equal($"Clause '{name}' can't include itself (directly or through another clause).", await ErrorAsync(response));
		}
		Assert.Equal(0, (await _client.GetFromJsonAsync<JsonElement>($"/api/clauses/{name}/versions")).GetArrayLength());
	}

	[Fact]
	public async Task A_clause_cannot_include_itself_through_another_clause()
	{
		var a = Unique("a");
		var b = Unique("b");
		await PublishAsync("clauses", b, $"<p>b</p>{{% include '{a}' %}}");
		var response = await _client.PutAsJsonAsync($"/api/clauses/{a}/draft", Body($"{{% include '{b}' %}}"));
		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

		// Including an unrelated clause is fine.
		var c = Unique("c");
		await PublishAsync("clauses", c, "<p>c</p>");
		await SaveAsync("clauses", a, $"{{% include '{c}' %}}");
	}

	[Fact]
	public async Task Templates_may_include_clauses_freely_even_with_the_same_name()
	{
		var name = Unique("same");
		await PublishAsync("clauses", name, "<p>Clause text</p>");
		await SaveAsync("templates", name, $"{{% include '{name}' %}}");
	}

	[Fact]
	public async Task Usage_lists_template_versions_that_include_the_clause()
	{
		var clause = Unique("std");
		var t1 = Unique("tpl");
		var t2 = Unique("tpl");
		var t3 = Unique("tpl");
		await PublishAsync("clauses", clause, "<p>Std</p>");
		await PublishAsync("templates", t1, $"{{% include '{clause}' %}}");
		await SaveAsync("templates", t1, "<p>v2 no longer uses it</p>");
		await SaveAsync("templates", t2, $"{{% include '{clause}@1' %}}{{% include '{clause}' %}}");
		await SaveAsync("templates", t3, "<p>unrelated {% include 'other' %}</p>");

		var usage = await _client.GetFromJsonAsync<JsonElement>($"/api/clauses/{clause}/usage");
		var rows = usage.EnumerateArray().ToList();
		Assert.Equal(2, rows.Count);
		var first = rows.Single(r => r.GetProperty("template").GetString() == t1);
		Assert.Equal(1, first.GetProperty("version").GetInt32());
		Assert.Equal("Published", first.GetProperty("status").GetString());
		Assert.True(first.GetProperty("latest").GetBoolean());
		Assert.Equal(0, first.GetProperty("pinned").GetArrayLength());
		var second = rows.Single(r => r.GetProperty("template").GetString() == t2);
		Assert.Equal("Draft", second.GetProperty("status").GetString());
		Assert.True(second.GetProperty("latest").GetBoolean());
		Assert.Equal([1], second.GetProperty("pinned").EnumerateArray().Select(p => p.GetInt32()));
	}

	[Fact]
	public async Task Usage_of_an_unused_clause_is_empty()
	{
		Assert.Equal(0, (await _client.GetFromJsonAsync<JsonElement>($"/api/clauses/{Unique("unused")}/usage")).GetArrayLength());
	}

	[Fact]
	public async Task Render_preview_prints_the_clause_in_the_pdf()
	{
		var clause = Unique("std");
		await PublishAsync("clauses", clause, "<p>Exclusion wording for {{ policy.number }}</p>");
		var response = await _client.PostAsJsonAsync("/api/render", new JsonObject
		{
			["html"] = $"<h1>Declarations</h1>{{% include '{clause}' %}}",
			["css"] = "",
			["data"] = new JsonObject { ["policy"] = new JsonObject { ["number"] = "CPP7654321" } }
		});
		Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
		var text = Words(await response.Content.ReadAsByteArrayAsync());
		Assert.True(text.Contains("Declarations") && text.Contains("Exclusion wording for CPP7654321"), text);
	}

	[Fact]
	public async Task Render_preview_with_a_missing_clause_explains_why()
	{
		var clause = Unique("missing");
		var response = await _client.PostAsJsonAsync("/api/render", new JsonObject { ["html"] = $"{{% include '{clause}' %}}", ["css"] = "" });
		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		Assert.Contains($"Clause '{clause}' has no published version", await ErrorAsync(response));
	}

	[Fact]
	public async Task Published_template_render_uses_the_latest_published_clause()
	{
		var clause = Unique("std");
		var template = Unique("tpl");
		await PublishAsync("clauses", clause, "<p>Clause wording one</p>");
		await PublishAsync("templates", template, $"<h1>Doc</h1>{{% include '{clause}' %}}");

		var first = await _client.PostAsync($"/api/templates/{template}/pdf", null);
		Assert.True(first.IsSuccessStatusCode, await first.Content.ReadAsStringAsync());
		Assert.Contains("Clause wording one", Words(await first.Content.ReadAsByteArrayAsync()));

		await PublishAsync("clauses", clause, "<p>Clause wording two</p>");
		var second = await _client.PostAsync($"/api/templates/{template}/pdf", null);
		var text = Words(await second.Content.ReadAsByteArrayAsync());
		Assert.Contains("Clause wording two", text);
		Assert.DoesNotContain("Clause wording one", text);
	}
}
