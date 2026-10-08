using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FaCT.DocDesigner.POC.Tests.Import;

namespace FaCT.DocDesigner.POC.Tests.Mapping;

/// <summary>POST /api/mapping/suggest.</summary>
public sealed class MappingEndpointTests(ImportAppFactory factory) : IClassFixture<ImportAppFactory>
{
	private async Task<HttpResponseMessage> PostAsync(object body) =>
		await factory.CreateClient().PostAsJsonAsync("/api/mapping/suggest", body);

	[Fact]
	public async Task Suggestions_come_back_per_field_with_candidates_and_a_recommendation()
	{
		using var response = await PostAsync(new
		{
			fields = new[] { "POLICY_NO", "Name", "Signature" },
			paths = new object[]
			{
				new { path = "policy.number", kind = "text" },
				new { path = "insured.name", kind = "text" },
				new { path = "agent.name", kind = "text" }
			}
		});

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		var json = await response.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(3, json.GetArrayLength());

		var policy = json[0];
		Assert.Equal("POLICY_NO", policy.GetProperty("field").GetString());
		Assert.Equal("policy.number", policy.GetProperty("recommended").GetString());
		Assert.Equal(1.0, policy.GetProperty("candidates")[0].GetProperty("score").GetDouble());

		Assert.Equal(JsonValueKind.Null, json[1].GetProperty("recommended").ValueKind);
		Assert.Equal(2, json[1].GetProperty("candidates").GetArrayLength());

		Assert.Equal(0, json[2].GetProperty("candidates").GetArrayLength());
	}

	[Fact]
	public async Task Missing_lists_are_treated_as_empty()
	{
		using var response = await PostAsync(new { });
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		Assert.Equal(0, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetArrayLength());
	}

	[Fact]
	public async Task Too_many_fields_are_rejected()
	{
		using var response = await PostAsync(new { fields = Enumerable.Range(0, 2001).Select(i => "f" + i), paths = Array.Empty<object>() });
		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
	}

	[Fact]
	public async Task Too_many_paths_are_rejected()
	{
		using var response = await PostAsync(new { fields = new[] { "a" }, paths = Enumerable.Range(0, 5001).Select(i => new { path = "p" + i }) });
		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
	}

	[Fact]
	public async Task Overlong_names_are_rejected()
	{
		using var response = await PostAsync(new { fields = new[] { new string('a', 201) }, paths = new[] { new { path = "a" } } });
		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
	}

	[Fact]
	public async Task Paths_without_a_path_are_rejected()
	{
		using var response = await PostAsync(new { fields = new[] { "a" }, paths = new[] { new { kind = "text" } } });
		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
	}

	[Fact]
	public async Task Only_json_is_accepted()
	{
		using var content = new StringContent("fields=a", Encoding.UTF8, "application/x-www-form-urlencoded");
		using var response = await factory.CreateClient().PostAsync("/api/mapping/suggest", content);
		Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
	}

	[Fact]
	public async Task Printed_labels_match_fields_with_meaningless_names()
	{
		using var response = await PostAsync(new
		{
			fields = new[] { "f1_01[0]", "f1_02[0]", "f1_03[0]" },
			labels = new[] { "Policy Number", null, "" },
			paths = new object[] { new { path = "policy.number", kind = "text" }, new { path = "insured.name", kind = "text" } }
		});
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		var json = await response.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal("policy.number", json[0].GetProperty("recommended").GetString());
		Assert.Equal("Policy Number", json[0].GetProperty("label").GetString());
		Assert.Equal(0.95, json[0].GetProperty("candidates")[0].GetProperty("score").GetDouble());
		Assert.Equal(JsonValueKind.Null, json[1].GetProperty("label").ValueKind);
		Assert.Equal(0, json[1].GetProperty("candidates").GetArrayLength());
		Assert.Equal(JsonValueKind.Null, json[2].GetProperty("label").ValueKind);
	}

	[Fact]
	public async Task Fewer_labels_than_fields_is_fine()
	{
		using var response = await PostAsync(new
		{
			fields = new[] { "x1", "Insured Name" },
			labels = new[] { "Policy Number" },
			paths = new object[] { new { path = "policy.number" }, new { path = "insured.name" } }
		});
		var json = await response.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal("policy.number", json[0].GetProperty("recommended").GetString());
		Assert.Equal("insured.name", json[1].GetProperty("recommended").GetString());
	}

	[Fact]
	public async Task More_labels_than_fields_or_overlong_labels_are_rejected()
	{
		using var extra = await PostAsync(new { fields = new[] { "a" }, labels = new[] { "x", "y" }, paths = new[] { new { path = "a" } } });
		Assert.Equal(HttpStatusCode.BadRequest, extra.StatusCode);
		using var longLabel = await PostAsync(new { fields = new[] { "a" }, labels = new[] { new string('a', 201) }, paths = new[] { new { path = "a" } } });
		Assert.Equal(HttpStatusCode.BadRequest, longLabel.StatusCode);
	}
}
