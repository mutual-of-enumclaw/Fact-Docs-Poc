using System.Text.Json;
using FaCT.DocDesigner.POC.Templates;

namespace FaCT.DocDesigner.POC.Tests.Scenarios;

public sealed class ScenarioStoreTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "scenario-tests-" + Guid.NewGuid().ToString("N"));
	private readonly ScenarioStore _store;

	public ScenarioStoreTests() => _store = new ScenarioStore(_root);

	public void Dispose()
	{
		try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
	}

	private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

	[Theory]
	[InlineData("Minimal")]
	[InlineData("Many locations")]
	[InlineData("no-claims_2")]
	[InlineData("1")]
	[InlineData("A")]
	[InlineData("Ends with dash-")]
	[InlineData("Ends with underscore_")]
	public void Valid_scenario_names(string name) => Assert.True(ScenarioStore.IsValidScenarioName(name));

	[Theory]
	[InlineData("")]
	[InlineData(" Leading space")]
	[InlineData("Trailing space ")]
	[InlineData("-starts with dash")]
	[InlineData("_starts with underscore")]
	[InlineData("dots.not.allowed")]
	[InlineData("slash/name")]
	[InlineData("back\\slash")]
	[InlineData("..")]
	[InlineData("tab\tname")]
	[InlineData("new\nline")]
	[InlineData("émoji")]
	public void Invalid_scenario_names(string name) => Assert.False(ScenarioStore.IsValidScenarioName(name));

	[Fact]
	public void Scenario_names_are_at_most_64_characters()
	{
		Assert.True(ScenarioStore.IsValidScenarioName(new string('a', 64)));
		Assert.False(ScenarioStore.IsValidScenarioName(new string('a', 65)));
	}

	[Fact]
	public void Kinds_are_templates_and_clauses_only()
	{
		Assert.True(ScenarioStore.IsValidKind("templates"));
		Assert.True(ScenarioStore.IsValidKind("clauses"));
		Assert.False(ScenarioStore.IsValidKind("Templates"));
		Assert.False(ScenarioStore.IsValidKind("../templates"));
		Assert.False(ScenarioStore.IsValidKind(""));
	}

	[Fact]
	public async Task A_template_without_scenarios_has_an_empty_list()
	{
		Assert.Empty(await _store.ListAsync("templates", "nothing-here"));
	}

	[Fact]
	public async Task Saved_scenarios_list_in_the_order_they_were_added_with_their_data()
	{
		await _store.SaveAsync("templates", "t", "Minimal", Json("""{"policy":{"number":"A1"}}"""));
		await _store.SaveAsync("templates", "t", "Many locations", Json("""{"locations":[{"name":"N"},{"name":"S"}]}"""));
		var list = await _store.ListAsync("templates", "t");
		Assert.Equal(["Minimal", "Many locations"], list.Select(s => s.Name));
		Assert.Equal("A1", list[0].Data.GetProperty("policy").GetProperty("number").GetString());
		Assert.Equal(2, list[1].Data.GetProperty("locations").GetArrayLength());
		Assert.True(list[0].SavedUtc > DateTimeOffset.UtcNow.AddMinutes(-1));
	}

	[Fact]
	public async Task Saving_an_existing_name_replaces_it_in_place_ignoring_case()
	{
		await _store.SaveAsync("templates", "t", "First", Json("""{"v":1}"""));
		await _store.SaveAsync("templates", "t", "Second", Json("""{"v":2}"""));
		await _store.SaveAsync("templates", "t", "first", Json("""{"v":3}"""));
		var list = await _store.ListAsync("templates", "t");
		Assert.Equal(["first", "Second"], list.Select(s => s.Name));
		Assert.Equal(3, list[0].Data.GetProperty("v").GetInt32());
	}

	[Fact]
	public async Task Delete_removes_one_scenario_ignoring_case()
	{
		await _store.SaveAsync("templates", "t", "Keep", Json("{}"));
		await _store.SaveAsync("templates", "t", "Drop", Json("{}"));
		Assert.True(await _store.DeleteAsync("templates", "t", "drop"));
		Assert.False(await _store.DeleteAsync("templates", "t", "Drop"));
		Assert.Equal(["Keep"], (await _store.ListAsync("templates", "t")).Select(s => s.Name));
	}

	[Fact]
	public async Task Deleting_the_last_scenario_removes_the_file()
	{
		await _store.SaveAsync("templates", "t", "Only", Json("{}"));
		var file = Path.Combine(_root, "templates", "t.json");
		Assert.True(File.Exists(file));
		await _store.DeleteAsync("templates", "t", "Only");
		Assert.False(File.Exists(file));
		Assert.False(await _store.DeleteAsync("templates", "never", "x"));
	}

	[Fact]
	public async Task Templates_and_clauses_with_the_same_name_have_their_own_scenarios()
	{
		await _store.SaveAsync("templates", "same", "T", Json("{}"));
		await _store.SaveAsync("clauses", "same", "C", Json("{}"));
		Assert.Equal(["T"], (await _store.ListAsync("templates", "same")).Select(s => s.Name));
		Assert.Equal(["C"], (await _store.ListAsync("clauses", "same")).Select(s => s.Name));
	}

	[Fact]
	public async Task A_template_can_have_at_most_the_maximum_number_of_scenarios()
	{
		for (var i = 0; i < ScenarioStore.MaxScenarios; i++) await _store.SaveAsync("templates", "t", "S" + i, Json("{}"));
		var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _store.SaveAsync("templates", "t", "One more", Json("{}")));
		Assert.Contains(ScenarioStore.MaxScenarios.ToString(), ex.Message);
		// Replacing one is still allowed.
		await _store.SaveAsync("templates", "t", "S0", Json("""{"x":1}"""));
		Assert.Equal(ScenarioStore.MaxScenarios, (await _store.ListAsync("templates", "t")).Count);
	}

	[Theory]
	[InlineData("templates", "../escape")]
	[InlineData("templates", "a/b")]
	[InlineData("templates", "")]
	[InlineData("../x", "t")]
	[InlineData("other", "t")]
	public async Task Invalid_kinds_and_names_never_touch_the_file_system(string kind, string name)
	{
		await Assert.ThrowsAsync<ArgumentException>(() => _store.ListAsync(kind, name));
		await Assert.ThrowsAsync<ArgumentException>(() => _store.SaveAsync(kind, name, "S", Json("{}")));
		await Assert.ThrowsAsync<ArgumentException>(() => _store.DeleteAsync(kind, name, "S"));
	}

	[Fact]
	public async Task Scenarios_survive_a_new_store_instance()
	{
		await _store.SaveAsync("templates", "t", "Persisted", Json("""{"a":[1,2,3],"b":{"c":"d"}}"""));
		var reopened = await new ScenarioStore(_root).ListAsync("templates", "t");
		Assert.Equal("""{"a":[1,2,3],"b":{"c":"d"}}""", reopened.Single().Data.GetRawText().Replace(" ", "").Replace("\r", "").Replace("\n", ""));
	}

	[Fact]
	public async Task Concurrent_saves_keep_every_scenario()
	{
		await Task.WhenAll(Enumerable.Range(0, 20).Select(i => _store.SaveAsync("templates", "busy", "S" + i, Json("{}"))));
		Assert.Equal(20, (await _store.ListAsync("templates", "busy")).Count);
	}
}
