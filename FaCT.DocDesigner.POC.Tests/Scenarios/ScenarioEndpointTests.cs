using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FaCT.DocDesigner.POC.Tests.Clauses;

namespace FaCT.DocDesigner.POC.Tests.Scenarios;

/// <summary>/api/{kind}/{name}/scenarios and Preview all (/api/render/scenarios).</summary>
public sealed class ScenarioEndpointTests(ClauseAppFactory factory) : IClassFixture<ClauseAppFactory>
{
	private readonly HttpClient _client = factory.CreateClient();

	private static string Unique(string name) => name + "-" + Guid.NewGuid().ToString("N")[..8];

	private static string Url(string template, string? scenario = null, string kind = "templates") =>
		$"/api/{kind}/{template}/scenarios" + (scenario is null ? "" : "/" + Uri.EscapeDataString(scenario));

	private async Task<HttpResponseMessage> PutAsync(string template, string scenario, object? data, string kind = "templates") =>
		await _client.PutAsJsonAsync(Url(template, scenario, kind), new { data });

	private async Task<JsonElement[]> ListAsync(string template, string kind = "templates") =>
		(await _client.GetFromJsonAsync<JsonElement[]>(Url(template, kind: kind)))!;

	private static async Task<string> ErrorAsync(HttpResponseMessage response) =>
		(await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()!;

	private static string Words(byte[] pdf) => Regex.Replace(PdfText.Extract(pdf), @"\s+", " ");

	[Fact]
	public async Task A_new_template_has_no_scenarios()
	{
		Assert.Empty(await ListAsync(Unique("t")));
	}

	[Fact]
	public async Task Save_list_replace_and_delete_scenarios()
	{
		var t = Unique("t");
		var saved = await PutAsync(t, "Many locations", new { locations = new[] { new { name = "North" }, new { name = "South" } } });
		Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
		var body = await saved.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal("Many locations", body.GetProperty("name").GetString());
		Assert.Equal(2, body.GetProperty("data").GetProperty("locations").GetArrayLength());

		(await PutAsync(t, "Minimal", new { policy = new { number = "M1" } })).EnsureSuccessStatusCode();
		var list = await ListAsync(t);
		Assert.Equal(["Many locations", "Minimal"], list.Select(s => s.GetProperty("name").GetString()));

		(await PutAsync(t, "Minimal", new { policy = new { number = "M2" } })).EnsureSuccessStatusCode();
		list = await ListAsync(t);
		Assert.Equal(2, list.Length);
		Assert.Equal("M2", list[1].GetProperty("data").GetProperty("policy").GetProperty("number").GetString());

		Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync(Url(t, "Many locations"))).StatusCode);
		Assert.Equal(HttpStatusCode.NotFound, (await _client.DeleteAsync(Url(t, "Many locations"))).StatusCode);
		Assert.Equal(["Minimal"], (await ListAsync(t)).Select(s => s.GetProperty("name").GetString()));
	}

	[Fact]
	public async Task Clauses_have_their_own_scenarios()
	{
		var name = Unique("same");
		(await PutAsync(name, "For template", new { a = 1 })).EnsureSuccessStatusCode();
		(await PutAsync(name, "For clause", new { b = 2 }, "clauses")).EnsureSuccessStatusCode();
		Assert.Equal(["For template"], (await ListAsync(name)).Select(s => s.GetProperty("name").GetString()));
		Assert.Equal(["For clause"], (await ListAsync(name, "clauses")).Select(s => s.GetProperty("name").GetString()));
	}

	[Theory]
	[InlineData("[1,2]")]
	[InlineData("\"text\"")]
	[InlineData("42")]
	[InlineData("null")]
	public async Task Scenario_data_must_be_an_object(string json)
	{
		var content = new StringContent("{\"data\":" + json + "}", System.Text.Encoding.UTF8, "application/json");
		var response = await _client.PutAsync(Url(Unique("t"), "Bad"), content);
		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		Assert.Equal("Scenario data must be a JSON object.", await ErrorAsync(response));
	}

	[Fact]
	public async Task Missing_data_is_rejected()
	{
		var response = await _client.PutAsJsonAsync(Url(Unique("t"), "Empty"), new { });
		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
	}

	[Fact]
	public async Task Scenario_data_over_2_MB_is_rejected()
	{
		var response = await PutAsync(Unique("t"), "Huge", new { blob = new string('x', 2 * 1024 * 1024) });
		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		Assert.Equal("Scenario data is too large (max 2 MB).", await ErrorAsync(response));
	}

	[Theory]
	[InlineData(" leading")]
	[InlineData("trailing ")]
	[InlineData("dot.name")]
	[InlineData("-dash")]
	[InlineData("semi;colon")]
	public async Task Invalid_scenario_names_are_rejected(string scenario)
	{
		var t = Unique("t");
		var put = await PutAsync(t, scenario, new { a = 1 });
		Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
		Assert.StartsWith("Scenario names may contain", await ErrorAsync(put));
		Assert.Equal(HttpStatusCode.BadRequest, (await _client.DeleteAsync(Url(t, scenario))).StatusCode);
		Assert.Empty(await ListAsync(t));
	}

	[Theory]
	[InlineData("GET", "/api/templates/bad.name/scenarios")]
	[InlineData("PUT", "/api/templates/bad.name/scenarios/S")]
	[InlineData("DELETE", "/api/templates/bad.name/scenarios/S")]
	public async Task Invalid_template_names_are_rejected(string method, string url)
	{
		var request = new HttpRequestMessage(new HttpMethod(method), url);
		if (method == "PUT") request.Content = JsonContent.Create(new { data = new { a = 1 } });
		Assert.Equal(HttpStatusCode.BadRequest, (await _client.SendAsync(request)).StatusCode);
	}

	[Theory]
	[InlineData("GET", "/api/widgets/t/scenarios")]
	[InlineData("PUT", "/api/widgets/t/scenarios/S")]
	[InlineData("DELETE", "/api/widgets/t/scenarios/S")]
	public async Task Unknown_kinds_are_not_found(string method, string url)
	{
		var request = new HttpRequestMessage(new HttpMethod(method), url);
		if (method == "PUT") request.Content = JsonContent.Create(new { data = new { a = 1 } });
		Assert.Equal(HttpStatusCode.NotFound, (await _client.SendAsync(request)).StatusCode);
	}

	[Fact]
	public async Task The_51st_scenario_is_refused()
	{
		var t = Unique("t");
		for (var i = 0; i < 50; i++) (await PutAsync(t, "S" + i, new { i })).EnsureSuccessStatusCode();
		var response = await PutAsync(t, "One more", new { i = 51 });
		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		Assert.Contains("at most 50", await ErrorAsync(response));
	}

	[Fact]
	public async Task Existing_template_endpoints_still_work_beside_scenarios()
	{
		// /api/templates/{name}/versions must not be swallowed by /api/{kind}/{name}/scenarios.
		var t = Unique("t");
		Assert.Equal(0, (await _client.GetFromJsonAsync<JsonElement>($"/api/templates/{t}/versions")).GetArrayLength());
		Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/api/templates")).StatusCode);
		Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/api/clauses")).StatusCode);
	}

	// ---- Preview all ---------------------------------------------------------------------------------------------

	private const string Template =
		"<h1>Policy {{ policy.number }}</h1>{% for location in locations %}<p style=\"page-break-after:always\">Location {{ location.name }}</p>{% endfor %}<p>End of document</p>";

	private static JsonObject Scenario(string name, JsonNode? data) => new() { ["name"] = name, ["data"] = data };

	private async Task<JsonElement[]> RenderAllAsync(params JsonObject[] scenarios)
	{
		var response = await _client.PostAsJsonAsync("/api/render/scenarios", new JsonObject
		{
			["html"] = Template,
			["css"] = "",
			["scenarios"] = new JsonArray(scenarios.Select(s => (JsonNode)s).ToArray())
		});
		Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
		return (await response.Content.ReadFromJsonAsync<JsonElement[]>())!;
	}

	[Fact]
	public async Task Preview_all_renders_one_pdf_per_scenario_with_its_own_data()
	{
		var results = await RenderAllAsync(
			Scenario("Minimal", JsonNode.Parse("""{"policy":{"number":"MIN-1"},"locations":[]}""")),
			Scenario("Three locations", JsonNode.Parse("""{"policy":{"number":"BIG-3"},"locations":[{"name":"North"},{"name":"South"},{"name":"East"}]}""")));

		Assert.Equal(["Minimal", "Three locations"], results.Select(r => r.GetProperty("name").GetString()));
		Assert.All(results, r => Assert.Equal(JsonValueKind.Null, r.GetProperty("error").ValueKind));

		var minimal = Words(Convert.FromBase64String(results[0].GetProperty("pdf").GetString()!));
		Assert.Contains("Policy MIN-1", minimal);
		Assert.DoesNotContain("Location", minimal);
		Assert.Equal(1, results[0].GetProperty("pages").GetInt32());

		var big = Words(Convert.FromBase64String(results[1].GetProperty("pdf").GetString()!));
		Assert.Contains("Policy BIG-3", big);
		Assert.Contains("Location North", big);
		Assert.Contains("Location East", big);
		Assert.True(results[1].GetProperty("pages").GetInt32() >= 3, "pages: " + results[1].GetProperty("pages"));
	}

	[Fact]
	public async Task A_bad_scenario_fails_alone()
	{
		var results = await RenderAllAsync(
			Scenario("Not an object", JsonNode.Parse("[1,2,3]")),
			Scenario("Good", JsonNode.Parse("""{"policy":{"number":"OK-1"}}""")),
			Scenario("No data", null));
		Assert.Equal("Scenario data must be a JSON object.", results[0].GetProperty("error").GetString());
		Assert.Equal(JsonValueKind.Null, results[0].GetProperty("pdf").ValueKind);
		Assert.Equal(0, results[0].GetProperty("pages").GetInt32());
		Assert.Equal(JsonValueKind.Null, results[1].GetProperty("error").ValueKind);
		Assert.Contains("Policy OK-1", Words(Convert.FromBase64String(results[1].GetProperty("pdf").GetString()!)));
		Assert.Equal("Scenario data must be a JSON object.", results[2].GetProperty("error").GetString());
	}

	[Fact]
	public async Task Template_errors_are_reported_for_every_scenario()
	{
		var response = await _client.PostAsJsonAsync("/api/render/scenarios", new JsonObject
		{
			["html"] = "{% include 'no-such-clause' %}",
			["scenarios"] = new JsonArray(Scenario("A", new JsonObject()), Scenario("B", new JsonObject()))
		});
		var results = (await response.Content.ReadFromJsonAsync<JsonElement[]>())!;
		Assert.Equal(2, results.Length);
		Assert.All(results, r => Assert.Contains("no-such-clause", r.GetProperty("error").GetString()));
	}

	[Fact]
	public async Task Scenario_values_are_encoded_in_the_pdf_html()
	{
		var results = await RenderAllAsync(Scenario("Injection", JsonNode.Parse("""{"policy":{"number":"<script>alert(1)</script>"}}""")));
		Assert.Contains("<script>alert(1)</script>", Words(Convert.FromBase64String(results[0].GetProperty("pdf").GetString()!)));
	}

	[Theory]
	[InlineData(0)]
	[InlineData(21)]
	public async Task Preview_all_takes_1_to_20_scenarios(int count)
	{
		var response = await _client.PostAsJsonAsync("/api/render/scenarios", new JsonObject
		{
			["html"] = "<p>x</p>",
			["scenarios"] = new JsonArray(Enumerable.Range(0, count).Select(i => (JsonNode)Scenario("S" + i, new JsonObject())).ToArray())
		});
		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		Assert.Equal("Send between 1 and 20 scenarios.", await ErrorAsync(response));
	}

	[Fact]
	public async Task Preview_all_without_a_scenario_list_is_rejected()
	{
		var response = await _client.PostAsJsonAsync("/api/render/scenarios", new JsonObject { ["html"] = "<p>x</p>" });
		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
	}
}
