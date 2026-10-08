using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FaCT.DocDesigner.POC.Tests.Clauses;

namespace FaCT.DocDesigner.POC.Tests.Usage;

/// <summary>/api/usage/fields: search and index over a temp template/clause store (each test uses its own field names).</summary>
public sealed class FieldUsageEndpointTests(ClauseAppFactory factory) : IClassFixture<ClauseAppFactory>
{
	private readonly HttpClient _client = factory.CreateClient();

	private static string Unique(string name) => name + Guid.NewGuid().ToString("N")[..8];

	private async Task<int> SaveAsync(string kind, string name, string html)
	{
		var response = await _client.PutAsJsonAsync($"/api/{kind}/{name}/draft", new { project = new { }, html, css = "" });
		Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
		return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("version").GetInt32();
	}

	private async Task<int> PublishAsync(string kind, string name, string html)
	{
		var version = await SaveAsync(kind, name, html);
		(await _client.PostAsync($"/api/{kind}/{name}/versions/{version}/publish", null)).EnsureSuccessStatusCode();
		return version;
	}

	private async Task<JsonElement> SearchAsync(object body)
	{
		var response = await _client.PostAsJsonAsync("/api/usage/fields", body);
		Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
		return await response.Content.ReadFromJsonAsync<JsonElement>();
	}

	private static List<JsonElement> Documents(JsonElement result) => result.GetProperty("documents").EnumerateArray().ToList();

	private static string Key(JsonElement d) =>
		$"{d.GetProperty("kind").GetString()}/{d.GetProperty("name").GetString()}/v{d.GetProperty("version").GetInt32()} {d.GetProperty("status").GetString()}";

	[Fact]
	public async Task Finds_the_templates_that_use_a_field()
	{
		var field = Unique("f");
		var a = Unique("a");
		var b = Unique("b");
		var c = Unique("c");
		await PublishAsync("templates", a, $"<p>{{{{ {field}.number }}}} and {{{{ {field}.number | upcase }}}}</p>");
		await PublishAsync("templates", b, $"{{% if {field}.number %}}<p>x</p>{{% endif %}}");
		await PublishAsync("templates", c, "<p>{{ unrelated.value }}</p>");

		var result = await SearchAsync(new { path = field + ".number" });
		Assert.Equal(field + ".number", result.GetProperty("path").GetString());
		var docs = Documents(result);
		Assert.Equal([$"templates/{a}/v1 Published", $"templates/{b}/v1 Published"], docs.Select(Key).OrderBy(k => k));
		var first = docs.Single(d => d.GetProperty("name").GetString() == a);
		Assert.Equal(2, first.GetProperty("paths")[0].GetProperty("count").GetInt32());
		Assert.Equal(JsonValueKind.Null, result.GetProperty("canvas").ValueKind);
	}

	[Fact]
	public async Task A_parent_path_finds_everything_under_it()
	{
		var field = Unique("p");
		var t = Unique("t");
		await PublishAsync("templates", t, $"{{{{ {field}.number }}}}{{{{ {field}.effective | date: '%Y' }}}}{{{{ {field}x.other }}}}");
		var doc = Assert.Single(Documents(await SearchAsync(new { path = field })));
		Assert.Equal([field + ".effective", field + ".number"], doc.GetProperty("paths").EnumerateArray().Select(p => p.GetProperty("path").GetString()));
	}

	[Fact]
	public async Task List_fields_are_found_by_list_or_alias()
	{
		var list = Unique("locs");
		var t = Unique("t");
		await PublishAsync("templates", t, $"{{% for loc in {list} %}}<td>{{{{ loc.name }}}}</td>{{% endfor %}}");
		foreach (var query in new[] { list + "[].name", list + ".name", "loc.name" })
		{
			var doc = Assert.Single(Documents(await SearchAsync(new { path = query })));
			Assert.Equal(list + "[].name", doc.GetProperty("paths")[0].GetProperty("path").GetString());
		}
		// The list itself is used too (the loop).
		var whole = Assert.Single(Documents(await SearchAsync(new { path = list })));
		Assert.Equal([list, list + "[].name"], whole.GetProperty("paths").EnumerateArray().Select(p => p.GetProperty("path").GetString()));
	}

	[Fact]
	public async Task The_newest_and_the_published_version_are_searched_by_default()
	{
		var field = Unique("v");
		var t = Unique("t");
		await PublishAsync("templates", t, $"{{{{ {field}.a }}}}");            // v1: retired below
		await PublishAsync("templates", t, $"{{{{ {field}.a }}}}{{{{ {field}.b }}}}"); // v2: published
		await SaveAsync("templates", t, $"{{{{ {field}.b }}}}");             // v3: draft

		Assert.Equal([$"templates/{t}/v2 Published"], Documents(await SearchAsync(new { path = field + ".a" })).Select(Key));
		Assert.Equal([$"templates/{t}/v3 Draft", $"templates/{t}/v2 Published"], Documents(await SearchAsync(new { path = field + ".b" })).Select(Key));
		Assert.Equal([$"templates/{t}/v2 Published", $"templates/{t}/v1 Retired"],
			Documents(await SearchAsync(new { path = field + ".a", allVersions = true })).Select(Key));
	}

	[Fact]
	public async Task Clauses_are_searched_and_templates_list_the_clauses_they_use_it_through()
	{
		var field = Unique("cl");
		var clause = Unique("clause");
		var t = Unique("t");
		await PublishAsync("clauses", clause, $"<p>Insured: {{{{ {field}.name }}}}</p>");
		await PublishAsync("templates", t, $"<h1>Doc</h1>{{% include '{clause}' %}}");

		var docs = Documents(await SearchAsync(new { path = field + ".name" }));
		Assert.Equal([$"clauses/{clause}/v1 Published", $"templates/{t}/v1 Published"], docs.Select(Key).OrderBy(k => k));
		var template = docs.Single(d => d.GetProperty("kind").GetString() == "templates");
		Assert.Equal(0, template.GetProperty("paths").GetArrayLength());
		Assert.Equal([clause + " v1"], template.GetProperty("via").EnumerateArray().Select(v => v.GetString()));

		Assert.Equal(["clauses"], Documents(await SearchAsync(new { path = field + ".name", kind = "clauses" })).Select(d => d.GetProperty("kind").GetString()));
		Assert.Equal(["templates"], Documents(await SearchAsync(new { path = field + ".name", kind = "templates" })).Select(d => d.GetProperty("kind").GetString()));
	}

	[Fact]
	public async Task The_unsaved_canvas_is_counted_when_sent()
	{
		var field = Unique("cv");
		var result = await SearchAsync(new { path = field, html = $"<p>{{{{ {field}.x }}}} {{{{ {field}.x }}}} {{{{ {field}.y }}}}</p>" });
		Assert.Empty(Documents(result));
		var canvas = result.GetProperty("canvas").EnumerateArray().ToList();
		Assert.Equal([(field + ".x", 2), (field + ".y", 1)], canvas.Select(p => (p.GetProperty("path").GetString(), p.GetProperty("count").GetInt32())));
	}

	[Theory]
	[InlineData("")]
	[InlineData("policy..number")]
	[InlineData("{{ policy }}")]
	[InlineData("a b")]
	public async Task Bad_paths_are_rejected(string path)
	{
		var response = await _client.PostAsJsonAsync("/api/usage/fields", new { path });
		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		Assert.StartsWith("Enter a data path", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
	}

	[Fact]
	public async Task A_bad_kind_is_rejected()
	{
		var response = await _client.PostAsJsonAsync("/api/usage/fields", new { path = "a", kind = "widgets" });
		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
	}

	[Fact]
	public async Task The_index_lists_each_path_with_its_number_of_documents()
	{
		var field = Unique("ix");
		await PublishAsync("templates", Unique("t"), $"{{{{ {field}.shared }}}}{{{{ {field}.only }}}}");
		await PublishAsync("templates", Unique("t"), $"{{{{ {field}.shared }}}}");
		var t = Unique("t");
		await PublishAsync("templates", t, $"{{{{ {field}.shared }}}}");
		await SaveAsync("templates", t, $"{{{{ {field}.shared }}}}"); // two versions of one document count once
		await PublishAsync("clauses", Unique("c"), $"{{{{ {field}.shared }}}}");

		var index = (await _client.GetFromJsonAsync<JsonElement[]>("/api/usage/fields"))!
			.Where(e => e.GetProperty("path").GetString()!.StartsWith(field, StringComparison.Ordinal))
			.Select(e => (e.GetProperty("path").GetString(), e.GetProperty("documents").GetInt32()))
			.ToList();
		Assert.Equal([(field + ".only", 1), (field + ".shared", 4)], index);
	}
}
