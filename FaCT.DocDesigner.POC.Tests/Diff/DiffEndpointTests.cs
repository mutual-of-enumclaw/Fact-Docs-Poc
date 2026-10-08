using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using FaCT.DocDesigner.POC.Tests.Clauses;

namespace FaCT.DocDesigner.POC.Tests.Diff;

/// <summary>POST /api/{kind}/{name}/diff: the canvas against the published (or a given) version.</summary>
public sealed class DiffEndpointTests(ClauseAppFactory factory) : IClassFixture<ClauseAppFactory>
{
	private readonly HttpClient _client = factory.CreateClient();

	private static string Unique(string name) => name + "-" + Guid.NewGuid().ToString("N")[..8];

	private async Task<int> SaveAsync(string kind, string name, string html, string css = "", object? model = null)
	{
		var response = await _client.PutAsJsonAsync($"/api/{kind}/{name}/draft", new { project = new { }, html, css, model });
		Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
		return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("version").GetInt32();
	}

	private async Task<int> PublishAsync(string kind, string name, string html, string css = "", object? model = null)
	{
		var version = await SaveAsync(kind, name, html, css, model);
		(await _client.PostAsync($"/api/{kind}/{name}/versions/{version}/publish", null)).EnsureSuccessStatusCode();
		return version;
	}

	private async Task<HttpResponseMessage> PostDiffAsync(string kind, string name, string html, string css = "", JsonNode? data = null, string query = "")
	{
		var body = new JsonObject { ["html"] = html, ["css"] = css };
		if (data is not null) body["data"] = data;
		return await _client.PostAsJsonAsync($"/api/{kind}/{name}/diff{query}", body);
	}

	private async Task<JsonElement> DiffAsync(string kind, string name, string html, string css = "", JsonNode? data = null, string query = "")
	{
		var response = await PostDiffAsync(kind, name, html, css, data, query);
		Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
		return await response.Content.ReadFromJsonAsync<JsonElement>();
	}

	private static IEnumerable<string> LinesOf(JsonElement diff, string type) =>
		diff.GetProperty("lines").EnumerateArray().Where(l => l.GetProperty("type").GetString() == type).Select(l => l.GetProperty("text").GetString()!);

	[Fact]
	public async Task Nothing_published_means_nothing_to_compare()
	{
		var name = Unique("t");
		await SaveAsync("templates", name, "<p>draft</p>");
		var d = await DiffAsync("templates", name, "<p>x</p>");
		Assert.Equal(JsonValueKind.Null, d.GetProperty("against").ValueKind);
		Assert.Equal(JsonValueKind.Null, d.GetProperty("html").ValueKind);
		Assert.Equal(JsonValueKind.Null, d.GetProperty("text").ValueKind);
	}

	[Fact]
	public async Task A_wording_change_shows_in_the_html_and_the_printed_text()
	{
		var name = Unique("t");
		await PublishAsync("templates", name, "<h1>Declarations</h1><p>Old exclusion wording</p><p>Footer</p>");
		var d = await DiffAsync("templates", name, "<h1>Declarations</h1><p>New exclusion wording</p><p>Footer</p>");

		Assert.Equal(1, d.GetProperty("against").GetProperty("version").GetInt32());
		Assert.Equal("Published", d.GetProperty("against").GetProperty("status").GetString());
		Assert.False(d.GetProperty("identical").GetBoolean());

		var html = d.GetProperty("html");
		Assert.Equal(1, html.GetProperty("added").GetInt32());
		Assert.Equal(1, html.GetProperty("removed").GetInt32());
		Assert.Equal(["<p>New exclusion wording</p>"], LinesOf(html, "added"));
		Assert.Equal(["<p>Old exclusion wording</p>"], LinesOf(html, "removed"));

		var text = d.GetProperty("text");
		Assert.Equal(["New exclusion wording"], LinesOf(text, "added"));
		Assert.Equal(["Old exclusion wording"], LinesOf(text, "removed"));
		Assert.True(d.GetProperty("css").GetProperty("identical").GetBoolean());

		foreach (var side in new[] { "published", "current" })
		{
			var s = d.GetProperty(side);
			Assert.Equal(JsonValueKind.Null, s.GetProperty("error").ValueKind);
			Assert.Equal(1, s.GetProperty("pages").GetInt32());
			Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString(Convert.FromBase64String(s.GetProperty("pdf").GetString()!), 0, 4));
		}
	}

	[Fact]
	public async Task The_same_template_is_identical()
	{
		var name = Unique("t");
		const string html = "<h1>Same</h1><p>{{ policy.number }}</p>";
		await PublishAsync("templates", name, html, ".a{color:red}");
		var d = await DiffAsync("templates", name, html, ".a{color:red}", JsonNode.Parse("""{"policy":{"number":"P1"}}"""));
		Assert.True(d.GetProperty("identical").GetBoolean());
		Assert.True(d.GetProperty("text").GetProperty("identical").GetBoolean());
		Assert.Equal(0, d.GetProperty("html").GetProperty("lines").GetArrayLength());
	}

	[Fact]
	public async Task Css_changes_are_counted_and_listed()
	{
		var name = Unique("t");
		await PublishAsync("templates", name, "<p class=\"a\">x</p>", ".a{color:red;}.b{margin:0}");
		var d = await DiffAsync("templates", name, "<p class=\"a\">x</p>", ".a{color:blue;}.b{margin:0}");
		var css = d.GetProperty("css");
		Assert.Equal([".a{color:blue;}"], LinesOf(css, "added"));
		Assert.Equal([".a{color:red;}"], LinesOf(css, "removed"));
		Assert.True(d.GetProperty("html").GetProperty("identical").GetBoolean());
		Assert.False(d.GetProperty("identical").GetBoolean());
	}

	[Fact]
	public async Task Both_versions_render_with_the_same_given_data()
	{
		var name = Unique("t");
		await PublishAsync("templates", name, "<p>Policy {{ policy.number }}</p>");
		var d = await DiffAsync("templates", name, "<p>Policy {{ policy.number }} renewed</p>", data: JsonNode.Parse("""{"policy":{"number":"SAME-9"}}"""));
		Assert.Equal(["Policy SAME-9 renewed"], LinesOf(d.GetProperty("text"), "added"));
		Assert.Equal(["Policy SAME-9"], LinesOf(d.GetProperty("text"), "removed"));
	}

	[Fact]
	public async Task Without_data_the_published_versions_example_data_is_used()
	{
		var name = Unique("t");
		await PublishAsync("templates", name, "<p>Policy {{ policy.number }}</p>", model: new { policy = new { number = "MODEL-1" } });
		var d = await DiffAsync("templates", name, "<p>Policy {{ policy.number }}!</p>");
		Assert.Equal(["Policy MODEL-1!"], LinesOf(d.GetProperty("text"), "added"));
	}

	[Fact]
	public async Task A_new_page_shows_in_the_page_counts_and_text()
	{
		var name = Unique("t");
		await PublishAsync("templates", name, "<p>Page one</p>");
		var d = await DiffAsync("templates", name, "<p style=\"page-break-after:always\">Page one</p><p>Page two</p>");
		Assert.Equal(1, d.GetProperty("published").GetProperty("pages").GetInt32());
		Assert.Equal(2, d.GetProperty("current").GetProperty("pages").GetInt32());
		Assert.Contains("--- Page 2 ---", LinesOf(d.GetProperty("text"), "added"));
		Assert.Contains("Page two", LinesOf(d.GetProperty("text"), "added"));
	}

	[Fact]
	public async Task Against_compares_with_a_specific_version()
	{
		var name = Unique("t");
		await PublishAsync("templates", name, "<p>Version one</p>");
		await PublishAsync("templates", name, "<p>Version two</p>");
		var d = await DiffAsync("templates", name, "<p>Version one</p>", query: "?against=1");
		Assert.Equal(1, d.GetProperty("against").GetProperty("version").GetInt32());
		Assert.Equal("Retired", d.GetProperty("against").GetProperty("status").GetString());
		Assert.True(d.GetProperty("identical").GetBoolean());

		d = await DiffAsync("templates", name, "<p>Version one</p>");
		Assert.Equal(2, d.GetProperty("against").GetProperty("version").GetInt32());
		Assert.Equal(["Version one"], LinesOf(d.GetProperty("text"), "added"));
	}

	[Fact]
	public async Task A_version_that_does_not_exist_is_not_found()
	{
		var name = Unique("t");
		await PublishAsync("templates", name, "<p>x</p>");
		Assert.Equal(HttpStatusCode.NotFound, (await PostDiffAsync("templates", name, "<p>x</p>", query: "?against=9")).StatusCode);
	}

	[Fact]
	public async Task A_side_that_cannot_render_is_reported_and_the_markup_is_still_compared()
	{
		var name = Unique("t");
		await PublishAsync("templates", name, "<p>Works</p>");
		var d = await DiffAsync("templates", name, "<p>Works</p>{% include 'no-such-clause' %}");
		Assert.Equal(JsonValueKind.Null, d.GetProperty("text").ValueKind);
		Assert.Equal(JsonValueKind.Null, d.GetProperty("published").GetProperty("error").ValueKind);
		Assert.Contains("no-such-clause", d.GetProperty("current").GetProperty("error").GetString());
		Assert.Equal(JsonValueKind.Null, d.GetProperty("current").GetProperty("pdf").ValueKind);
		Assert.Equal(1, d.GetProperty("html").GetProperty("added").GetInt32());
	}

	[Fact]
	public async Task Clauses_are_compared_against_their_published_version()
	{
		var name = Unique("c");
		await PublishAsync("clauses", name, "<p>Clause wording one</p>");
		var d = await DiffAsync("clauses", name, "<p>Clause wording two</p>");
		Assert.Equal(1, d.GetProperty("against").GetProperty("version").GetInt32());
		Assert.Equal(["Clause wording two"], LinesOf(d.GetProperty("text"), "added"));
		// A template of the same name is a different document.
		var t = await DiffAsync("templates", name, "<p>Clause wording two</p>");
		Assert.Equal(JsonValueKind.Null, t.GetProperty("against").ValueKind);
	}

	[Fact]
	public async Task Templates_compare_with_the_published_clause_content_they_include()
	{
		var clause = Unique("c");
		var name = Unique("t");
		await PublishAsync("clauses", clause, "<p>Shared wording v1</p>");
		await PublishAsync("templates", name, $"<h1>Doc</h1>{{% include '{clause}' %}}");
		await PublishAsync("clauses", clause, "<p>Shared wording v2</p>");
		// Same template markup: both sides include the latest published clause, so the printed text matches.
		var d = await DiffAsync("templates", name, $"<h1>Doc</h1>{{% include '{clause}' %}}");
		Assert.True(d.GetProperty("identical").GetBoolean());
		Assert.True(d.GetProperty("text").GetProperty("identical").GetBoolean());
		// Pinning the old clause version is a visible change.
		d = await DiffAsync("templates", name, $"<h1>Doc</h1>{{% include '{clause}@1' %}}");
		Assert.Equal(["Shared wording v1"], LinesOf(d.GetProperty("text"), "added"));
	}

	[Fact]
	public async Task Long_documents_are_collapsed_around_the_changes()
	{
		var name = Unique("t");
		var paragraphs = Enumerable.Range(1, 40).Select(i => $"<p>Paragraph {i}</p>").ToList();
		await PublishAsync("templates", name, string.Concat(paragraphs));
		paragraphs[20] = "<p>Paragraph twenty-one rewritten</p>";
		var d = await DiffAsync("templates", name, string.Concat(paragraphs));
		var lines = d.GetProperty("html").GetProperty("lines").EnumerateArray().ToList();
		Assert.True(lines.Count < 15, "lines: " + lines.Count);
		Assert.Equal(2, lines.Count(l => l.GetProperty("type").GetString() == "skip"));
	}

	[Theory]
	[InlineData("/api/templates/bad.name/diff", HttpStatusCode.BadRequest)]
	[InlineData("/api/widgets/t/diff", HttpStatusCode.NotFound)]
	public async Task Bad_targets_are_rejected(string url, HttpStatusCode expected)
	{
		var response = await _client.PostAsJsonAsync(url, new { html = "<p/>", css = "" });
		Assert.Equal(expected, response.StatusCode);
	}
}
