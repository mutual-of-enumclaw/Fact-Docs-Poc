using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FaCT.DocDesigner.POC.Spelling;
using FaCT.DocDesigner.POC.Templates;
using FaCT.DocDesigner.POC.Tests.Clauses;
using FaCT.DocDesigner.POC.Tests.Help;

namespace FaCT.DocDesigner.POC.Tests.Review;

/// <summary>The spell checker: en_US dictionary, insurance terms, the shared custom dictionary and what is skipped.</summary>
public sealed class SpellCheckerTests : IDisposable
{
	private readonly string _custom = Path.Combine(Path.GetTempPath(), "spelling-tests-" + Guid.NewGuid().ToString("N"));
	private readonly SpellChecker _spelling;

	public SpellCheckerTests() => _spelling = new SpellChecker(UserGuidePaths.Project, _custom);

	public void Dispose()
	{
		try { Directory.Delete(_custom, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
	}

	[Theory]
	[InlineData("receive")]
	[InlineData("Receive")]
	[InlineData("insured")]
	[InlineData("insured's")]
	[InlineData("insured\u2019s")]
	[InlineData("deductible")]
	[InlineData("follow-up")]
	[InlineData("re-issue")]
	[InlineData("Enumclaw")]
	[InlineData("coinsurance")]
	[InlineData("Coinsurance")]
	[InlineData("policyholder's")]
	[InlineData("subrogation")]
	public void Known_words_are_correct(string word) => Assert.True(_spelling.IsCorrect(word), word);

	[Theory]
	[InlineData("recieve")]
	[InlineData("insurence")]
	[InlineData("polcy")]
	[InlineData("teh")]
	[InlineData("Enumclow")]
	public void Misspellings_are_found(string word) => Assert.False(_spelling.IsCorrect(word), word);

	[Theory]
	[InlineData("CPP")]
	[InlineData("TIV")]
	[InlineData("XYZQ")]
	[InlineData("ABCDE")]
	[InlineData("B2B")]
	[InlineData("3rd")]
	[InlineData("a")]
	public void Acronyms_numbers_and_single_letters_are_not_checked(string word) => Assert.True(_spelling.IsCorrect(word), word);

	[Fact]
	public void Long_all_caps_words_are_checked()
	{
		Assert.True(_spelling.IsCorrect("DECLARATIONS"));
		Assert.False(_spelling.IsCorrect("DECLERATIONS"));
	}

	[Fact]
	public void Check_lists_each_misspelling_once_with_suggestions()
	{
		var result = _spelling.Check(["We", "recieve", "the", "recieve", "insurence", "policy"]);
		Assert.Equal(["recieve", "insurence"], result.Select(m => m.Word));
		Assert.Contains("receive", result[0].Suggestions);
		Assert.Contains("insurance", result[1].Suggestions);
		Assert.All(result, m => Assert.True(m.Suggestions.Count <= 5));
	}

	[Fact]
	public void Added_words_are_correct_and_shared()
	{
		Assert.False(_spelling.IsCorrect("Fluxcapacitor"));
		Assert.True(_spelling.AddWordAsync("Fluxcapacitor").GetAwaiter().GetResult());
		Assert.False(_spelling.AddWordAsync("Fluxcapacitor").GetAwaiter().GetResult());
		Assert.True(_spelling.IsCorrect("Fluxcapacitor"));
		Assert.Equal(["Fluxcapacitor"], _spelling.CustomWords());

		// Another designer process sees it too.
		var other = new SpellChecker(UserGuidePaths.Project, _custom);
		Assert.True(other.IsCorrect("Fluxcapacitor"));
	}

	[Fact]
	public void A_lower_case_custom_word_also_accepts_its_capitalized_form()
	{
		_spelling.AddWordAsync("zorbing").GetAwaiter().GetResult();
		Assert.True(_spelling.IsCorrect("Zorbing"));
	}

	[Theory]
	[InlineData("word", true)]
	[InlineData("insured's", true)]
	[InlineData("re-issue", true)]
	[InlineData("caf\u00e9", true)]
	[InlineData("", false)]
	[InlineData("two words", false)]
	[InlineData("<b>", false)]
	[InlineData("-start", false)]
	[InlineData("end'", false)]
	[InlineData("x1", false)]
	public void Only_single_words_can_be_added(string word, bool ok) => Assert.Equal(ok, SpellChecker.IsWord(word));

	[Fact]
	public void Words_longer_than_60_characters_are_not_words()
	{
		Assert.False(SpellChecker.IsWord(new string('a', 61)));
	}
}

public sealed class CommentStoreTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "comment-tests-" + Guid.NewGuid().ToString("N"));
	private readonly CommentStore _store;

	public CommentStoreTests() => _store = new CommentStore(_root);

	public void Dispose()
	{
		try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
	}

	[Fact]
	public async Task Add_reply_resolve_reopen_and_delete()
	{
		var comment = await _store.AddAsync("templates", "t", "abc12345", "0.2", "Heading: Declarations", "Ann", "Use the new title");
		Assert.Equal(12, comment.Id.Length);
		Assert.False(comment.Resolved);
		Assert.Empty(comment.Replies);

		var replied = await _store.ReplyAsync("templates", "t", comment.Id, "Bob", "Done in v3");
		Assert.Equal("Done in v3", Assert.Single(replied!.Replies).Text);

		var resolved = await _store.ResolveAsync("templates", "t", comment.Id, true, "Ann");
		Assert.True(resolved!.Resolved);
		Assert.Equal("Ann", resolved.ResolvedBy);
		Assert.NotNull(resolved.ResolvedUtc);

		var reopened = await _store.ResolveAsync("templates", "t", comment.Id, false, "Bob");
		Assert.False(reopened!.Resolved);
		Assert.Null(reopened.ResolvedBy);

		var listed = Assert.Single(await _store.ListAsync("templates", "t"));
		Assert.Equal("abc12345", listed.Anchor);
		Assert.Equal("0.2", listed.Path);
		Assert.Equal("Heading: Declarations", listed.Element);

		Assert.True(await _store.DeleteAsync("templates", "t", comment.Id));
		Assert.False(await _store.DeleteAsync("templates", "t", comment.Id));
		Assert.Empty(await _store.ListAsync("templates", "t"));
	}

	[Fact]
	public async Task Unknown_comments_are_null()
	{
		Assert.Null(await _store.ReplyAsync("templates", "t", "nope", "A", "x"));
		Assert.Null(await _store.ResolveAsync("templates", "t", "nope", true, "A"));
	}

	[Fact]
	public async Task Templates_and_clauses_have_their_own_comments()
	{
		await _store.AddAsync("templates", "same", "aaaaaaaa", "", "", "A", "on template");
		await _store.AddAsync("clauses", "same", "bbbbbbbb", "", "", "A", "on clause");
		Assert.Equal(["on template"], (await _store.ListAsync("templates", "same")).Select(c => c.Text));
		Assert.Equal(["on clause"], (await _store.ListAsync("clauses", "same")).Select(c => c.Text));
	}

	[Fact]
	public async Task Comments_and_replies_have_limits()
	{
		var first = await _store.AddAsync("templates", "busy", "aaaaaaaa", "", "", "A", "0");
		for (var i = 1; i < CommentStore.MaxComments; i++) await _store.AddAsync("templates", "busy", "aaaaaaaa", "", "", "A", i.ToString());
		await Assert.ThrowsAsync<InvalidOperationException>(() => _store.AddAsync("templates", "busy", "aaaaaaaa", "", "", "A", "one more"));
		for (var i = 0; i < CommentStore.MaxReplies; i++) await _store.ReplyAsync("templates", "busy", first.Id, "B", "r" + i);
		await Assert.ThrowsAsync<InvalidOperationException>(() => _store.ReplyAsync("templates", "busy", first.Id, "B", "one more"));
	}

	[Theory]
	[InlineData("templates", "../x")]
	[InlineData("widgets", "t")]
	public async Task Invalid_targets_never_touch_the_file_system(string kind, string name)
	{
		await Assert.ThrowsAsync<ArgumentException>(() => _store.ListAsync(kind, name));
		await Assert.ThrowsAsync<ArgumentException>(() => _store.AddAsync(kind, name, "aaaaaaaa", "", "", "A", "x"));
	}
}

/// <summary>/api/spelling and /api/{kind}/{name}/comments.</summary>
public sealed class SpellingAndCommentEndpointTests(ClauseAppFactory factory) : IClassFixture<ClauseAppFactory>
{
	private readonly HttpClient _client = factory.CreateClient();

	private static string Unique(string name) => name + "-" + Guid.NewGuid().ToString("N")[..8];

	private static async Task<string> ErrorAsync(HttpResponseMessage response) =>
		(await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()!;

	[Fact]
	public async Task Spell_check_returns_misspellings_with_suggestions()
	{
		var response = await _client.PostAsJsonAsync("/api/spelling/check", new { words = new[] { "Coverage", "recieve", "CPP", "<script>", "insurence" } });
		var result = (await response.Content.ReadFromJsonAsync<JsonElement[]>())!;
		Assert.Equal(["recieve", "insurence"], result.Select(m => m.GetProperty("word").GetString()));
		Assert.Contains("receive", result[0].GetProperty("suggestions").EnumerateArray().Select(s => s.GetString()));
	}

	[Fact]
	public async Task Too_many_words_are_refused()
	{
		var response = await _client.PostAsJsonAsync("/api/spelling/check", new { words = Enumerable.Repeat("word", SpellChecker.MaxWords + 1) });
		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
	}

	[Fact]
	public async Task Words_added_to_the_dictionary_are_listed_and_accepted()
	{
		var word = "Zqwlorbe" + new string('x', 2);
		Assert.Equal(HttpStatusCode.OK, (await _client.PostAsJsonAsync("/api/spelling/words", new { word })).StatusCode);
		Assert.Contains(word, (await _client.GetFromJsonAsync<string[]>("/api/spelling/words"))!);
		var check = await _client.PostAsJsonAsync("/api/spelling/check", new { words = new[] { word } });
		Assert.Empty((await check.Content.ReadFromJsonAsync<JsonElement[]>())!);
	}

	[Theory]
	[InlineData("two words")]
	[InlineData("")]
	[InlineData("<b>x</b>")]
	public async Task Only_words_can_be_added(string word)
	{
		var response = await _client.PostAsJsonAsync("/api/spelling/words", new { word });
		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
	}

	[Fact]
	public async Task Comment_lifecycle()
	{
		var t = Unique("t");
		var added = await _client.PostAsJsonAsync($"/api/templates/{t}/comments",
			new { anchor = "abc12345", path = "0.1", element = "Text: Hello", author = "Ann", text = "Typo here" });
		Assert.Equal(HttpStatusCode.OK, added.StatusCode);
		var id = (await added.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();

		(await _client.PostAsJsonAsync($"/api/templates/{t}/comments/{id}/replies", new { author = "Bob", text = "Fixed" })).EnsureSuccessStatusCode();
		var resolved = await _client.PostAsJsonAsync($"/api/templates/{t}/comments/{id}/resolved", new { resolved = true, author = "Ann" });
		Assert.True((await resolved.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("resolved").GetBoolean());

		var list = (await _client.GetFromJsonAsync<JsonElement[]>($"/api/templates/{t}/comments"))!;
		var comment = Assert.Single(list);
		Assert.Equal("Typo here", comment.GetProperty("text").GetString());
		Assert.Equal("Fixed", comment.GetProperty("replies")[0].GetProperty("text").GetString());
		Assert.Equal("Ann", comment.GetProperty("resolvedBy").GetString());
		Assert.Equal("0.1", comment.GetProperty("path").GetString());

		Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"/api/templates/{t}/comments/{id}")).StatusCode);
		Assert.Equal(HttpStatusCode.NotFound, (await _client.DeleteAsync($"/api/templates/{t}/comments/{id}")).StatusCode);
	}

	[Theory]
	[InlineData(null, "Ann", "text", "Select an element")]
	[InlineData("BAD!", "Ann", "text", "Select an element")]
	[InlineData("abc12345", "", "text", "Enter your name")]
	[InlineData("abc12345", "Ann", "  ", "Write a comment")]
	public async Task Comments_need_an_element_a_name_and_text(string? anchor, string author, string text, string error)
	{
		var response = await _client.PostAsJsonAsync($"/api/templates/{Unique("t")}/comments", new { anchor, author, text });
		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		Assert.StartsWith(error, await ErrorAsync(response));
	}

	[Fact]
	public async Task Long_comments_and_names_are_refused()
	{
		var t = Unique("t");
		Assert.StartsWith("Write a comment", await ErrorAsync(await _client.PostAsJsonAsync($"/api/templates/{t}/comments",
			new { anchor = "abc12345", author = "A", text = new string('x', 2001) })));
		Assert.StartsWith("Enter your name", await ErrorAsync(await _client.PostAsJsonAsync($"/api/templates/{t}/comments",
			new { anchor = "abc12345", author = new string('a', 61), text = "x" })));
	}

	[Fact]
	public async Task A_bad_path_is_dropped_and_long_element_names_are_cut()
	{
		var t = Unique("t");
		var response = await _client.PostAsJsonAsync($"/api/templates/{t}/comments",
			new { anchor = "abc12345", path = "../etc", element = new string('e', 300), author = "A", text = "x" });
		var comment = await response.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal("", comment.GetProperty("path").GetString());
		Assert.Equal(120, comment.GetProperty("element").GetString()!.Length);
	}

	[Fact]
	public async Task Unknown_comments_and_targets()
	{
		var t = Unique("t");
		Assert.Equal(HttpStatusCode.NotFound, (await _client.PostAsJsonAsync($"/api/templates/{t}/comments/nope/replies", new { author = "A", text = "x" })).StatusCode);
		Assert.Equal(HttpStatusCode.NotFound, (await _client.PostAsJsonAsync($"/api/templates/{t}/comments/nope/resolved", new { resolved = true })).StatusCode);
		Assert.Equal(HttpStatusCode.BadRequest, (await _client.GetAsync("/api/templates/bad.name/comments")).StatusCode);
		Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/api/widgets/t/comments")).StatusCode);
	}
}
