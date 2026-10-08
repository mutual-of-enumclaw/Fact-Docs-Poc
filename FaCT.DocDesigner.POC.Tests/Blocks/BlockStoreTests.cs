using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FaCT.DocDesigner.POC.Templates;
using FaCT.DocDesigner.POC.Tests.Clauses;

namespace FaCT.DocDesigner.POC.Tests.Blocks;

public sealed class BlockStoreTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "block-tests-" + Guid.NewGuid().ToString("N"));
	private readonly BlockStore _store;

	public BlockStoreTests() => _store = new BlockStore(_root);

	public void Dispose()
	{
		try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
	}

	private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

	[Fact]
	public async Task An_empty_library_has_no_blocks()
	{
		Assert.Empty(await _store.ListAsync());
	}

	[Fact]
	public async Task Saved_blocks_keep_their_content_and_list_by_category_then_label()
	{
		await _store.SaveAsync("zeta", "Zeta box", "Boxes", Json("""{"tagName":"div","classes":["z"]}"""), ".z{color:red}");
		await _store.SaveAsync("alpha", "Alpha box", "Boxes", Json("""[{"type":"text","content":"A"}]"""), "");
		await _store.SaveAsync("sig", "Signature", null, Json("""{"tagName":"div"}"""), null);

		var list = await _store.ListAsync();
		Assert.Equal(["Alpha box", "Zeta box", "Signature"], list.Select(b => b.Label));
		Assert.Equal(["Boxes", "Boxes", BlockStore.DefaultCategory], list.Select(b => b.Category));
		var zeta = list.Single(b => b.Name == "zeta");
		Assert.Equal(".z{color:red}", zeta.Css);
		Assert.Equal("z", zeta.Components.GetProperty("classes")[0].GetString());
		Assert.Equal("", list.Single(b => b.Name == "sig").Css);
		Assert.Equal(JsonValueKind.Array, list.Single(b => b.Name == "alpha").Components.ValueKind);
	}

	[Fact]
	public async Task Saving_the_same_name_replaces_the_block()
	{
		await _store.SaveAsync("box", "Box", "A", Json("{}"), "");
		await _store.SaveAsync("box", "Box v2", "B", Json("""{"tagName":"section"}"""), ".x{}");
		var block = Assert.Single(await _store.ListAsync());
		Assert.Equal("Box v2", block.Label);
		Assert.Equal("B", block.Category);
		Assert.Equal("section", block.Components.GetProperty("tagName").GetString());
	}

	[Fact]
	public async Task Labels_and_categories_are_trimmed_and_blank_categories_use_the_default()
	{
		var block = await _store.SaveAsync("b", "  Spaced  ", "   ", Json("{}"), "");
		Assert.Equal("Spaced", block.Label);
		Assert.Equal(BlockStore.DefaultCategory, block.Category);
	}

	[Fact]
	public async Task Delete_removes_a_block()
	{
		await _store.SaveAsync("gone", "Gone", null, Json("{}"), "");
		Assert.True(await _store.DeleteAsync("gone"));
		Assert.False(await _store.DeleteAsync("gone"));
		Assert.Empty(await _store.ListAsync());
	}

	[Fact]
	public async Task The_library_has_a_maximum_size_but_replacing_is_still_allowed()
	{
		for (var i = 0; i < BlockStore.MaxBlocks; i++) await _store.SaveAsync("b" + i, "B" + i, null, Json("{}"), "");
		var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _store.SaveAsync("one-more", "One more", null, Json("{}"), ""));
		Assert.Contains(BlockStore.MaxBlocks.ToString(), ex.Message);
		await _store.SaveAsync("b0", "B0 again", null, Json("{}"), "");
		Assert.Equal(BlockStore.MaxBlocks, (await _store.ListAsync()).Count);
	}

	[Theory]
	[InlineData("../escape")]
	[InlineData("a/b")]
	[InlineData("")]
	[InlineData("dot.name")]
	public async Task Invalid_names_never_touch_the_file_system(string name)
	{
		await Assert.ThrowsAsync<ArgumentException>(() => _store.SaveAsync(name, "X", null, Json("{}"), ""));
		await Assert.ThrowsAsync<ArgumentException>(() => _store.DeleteAsync(name));
	}

	[Fact]
	public async Task Blocks_survive_a_new_store_instance()
	{
		await _store.SaveAsync("kept", "Kept", "Cat", Json("""{"tagName":"div","components":[{"type":"data-field","field":"policy.number"}]}"""), ".k{}");
		var block = Assert.Single(await new BlockStore(_root).ListAsync());
		Assert.Equal("policy.number", block.Components.GetProperty("components")[0].GetProperty("field").GetString());
	}
}

/// <summary>/api/blocks.</summary>
public sealed class BlockEndpointTests(ClauseAppFactory factory) : IClassFixture<ClauseAppFactory>
{
	private readonly HttpClient _client = factory.CreateClient();

	private static string Unique(string name) => name + "-" + Guid.NewGuid().ToString("N")[..8];

	private static async Task<string> ErrorAsync(HttpResponseMessage response) =>
		(await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()!;

	[Fact]
	public async Task Save_list_and_delete_a_block()
	{
		var name = Unique("summary");
		var saved = await _client.PutAsJsonAsync($"/api/blocks/{name}", new
		{
			label = "Policy summary",
			category = "Summaries",
			components = new { tagName = "div", classes = new[] { "summary" }, components = new object[] { new { type = "data-field", field = "policy.number" } } },
			css = ".summary{border:1px solid #000;}"
		});
		Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
		var body = await saved.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(name, body.GetProperty("name").GetString());
		Assert.Equal("Summaries", body.GetProperty("category").GetString());

		var list = await _client.GetFromJsonAsync<JsonElement[]>("/api/blocks");
		var block = list!.Single(b => b.GetProperty("name").GetString() == name);
		Assert.Equal("Policy summary", block.GetProperty("label").GetString());
		Assert.Equal(".summary{border:1px solid #000;}", block.GetProperty("css").GetString());
		Assert.Equal("policy.number", block.GetProperty("components").GetProperty("components")[0].GetProperty("field").GetString());

		Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"/api/blocks/{name}")).StatusCode);
		Assert.Equal(HttpStatusCode.NotFound, (await _client.DeleteAsync($"/api/blocks/{name}")).StatusCode);
		Assert.DoesNotContain((await _client.GetFromJsonAsync<JsonElement[]>("/api/blocks"))!, b => b.GetProperty("name").GetString() == name);
	}

	[Fact]
	public async Task A_missing_category_uses_my_blocks()
	{
		var name = Unique("plain");
		var saved = await _client.PutAsJsonAsync($"/api/blocks/{name}", new { label = "Plain", components = new[] { new { type = "text", content = "x" } } });
		Assert.Equal("My blocks", (await saved.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("category").GetString());
	}

	[Theory]
	[InlineData(null, "Give the block a name")]
	[InlineData("", "Give the block a name")]
	[InlineData("   ", "Give the block a name")]
	public async Task A_label_is_required(string? label, string error)
	{
		var response = await _client.PutAsJsonAsync($"/api/blocks/{Unique("b")}", new { label, components = new { tagName = "div" } });
		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		Assert.StartsWith(error, await ErrorAsync(response));
	}

	[Fact]
	public async Task Labels_and_categories_have_length_limits()
	{
		var tooLong = await _client.PutAsJsonAsync($"/api/blocks/{Unique("b")}", new { label = new string('x', 61), components = new { } });
		Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
		var category = await _client.PutAsJsonAsync($"/api/blocks/{Unique("b")}", new { label = "Ok", category = new string('c', 41), components = new { } });
		Assert.Equal("Category names are at most 40 characters.", await ErrorAsync(category));
	}

	[Theory]
	[InlineData("null")]
	[InlineData("\"text\"")]
	[InlineData("42")]
	public async Task Content_must_be_component_json(string components)
	{
		var content = new StringContent("{\"label\":\"X\",\"components\":" + components + "}", System.Text.Encoding.UTF8, "application/json");
		var response = await _client.PutAsync($"/api/blocks/{Unique("b")}", content);
		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		Assert.Equal("The block has no content.", await ErrorAsync(response));
	}

	[Fact]
	public async Task Blocks_over_1_MB_are_refused()
	{
		var response = await _client.PutAsJsonAsync($"/api/blocks/{Unique("big")}", new { label = "Big", components = new { tagName = "div" }, css = new string('x', 1024 * 1024) });
		Assert.Equal("The block is too large (max 1 MB).", await ErrorAsync(response));
	}

	[Theory]
	[InlineData("PUT", "/api/blocks/bad.name")]
	[InlineData("DELETE", "/api/blocks/bad.name")]
	public async Task Invalid_names_are_rejected(string method, string url)
	{
		var request = new HttpRequestMessage(new HttpMethod(method), url);
		if (method == "PUT") request.Content = JsonContent.Create(new { label = "X", components = new { } });
		Assert.Equal(HttpStatusCode.BadRequest, (await _client.SendAsync(request)).StatusCode);
	}
}
