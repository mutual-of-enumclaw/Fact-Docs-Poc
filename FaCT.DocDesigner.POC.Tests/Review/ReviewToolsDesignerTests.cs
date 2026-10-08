using System.Net.Http.Json;
using System.Text.Json;
using PuppeteerSharp;

namespace FaCT.DocDesigner.POC.Tests.Review;

/// <summary>Find and replace, spell check and reviewer comments in the designer.</summary>
[Collection("Isolated designer")]
public sealed class ReviewToolsDesignerTests(IsolatedDesignerFixture fixture)
{
	private const string Page1 =
		"<h2>Policy Coverage</h2>" +
		"<p>The policy covers the insured. Coverage applies to each policy.</p>" +
		"<p>Policyholder notice: <span class=\"df\" data-field=\"policy.number\"></span></p>";

	private IPage Page => fixture.Page;

	private static string Unique(string name) => name + "-" + Guid.NewGuid().ToString("N")[..8];

	private Task<T> EvalAsync<T>(string script) => Page.EvaluateExpressionAsync<T>(script);

	private Task WaitAsync(string predicate, params object[] args) =>
		Page.WaitForFunctionAsync(predicate, new WaitForFunctionOptions { Timeout = 30_000 }, args);

	private async Task<string> ActAsync(string script)
	{
		await Page.EvaluateExpressionAsync("document.getElementById('status').textContent = ''");
		await Page.EvaluateExpressionAsync(script);
		await WaitAsync("() => { const t = document.getElementById('status').textContent; return t.length > 0 && !t.endsWith('...'); }");
		return await EvalAsync<string>("document.getElementById('status').textContent");
	}

	private async Task NewTemplateAsync(string html)
	{
		await ActAsync("(() => { const k = document.getElementById('docKind'); k.value = 'templates'; k.dispatchEvent(new Event('change')); })()");
		await ActAsync($"(() => {{ const n = document.getElementById('templateName'); n.value = '{Unique("tpl")}'; n.dispatchEvent(new Event('change')); }})()");
		await Page.EvaluateFunctionAsync("h => { const e = grapesjs.editors[0]; e.select(null); e.setComponents(h); e.setStyle(''); }", html);
	}

	private Task<string> HtmlAsync() => EvalAsync<string>("grapesjs.editors[0].getHtml()");

	private Task CloseModalAsync() => Page.EvaluateExpressionAsync("grapesjs.editors[0].Modal.close()");

	// ---- Find and replace ------------------------------------------------------------------------------------------

	private async Task OpenFindAsync(string text, bool matchCase = false, bool wholeWord = false)
	{
		await Page.EvaluateExpressionAsync("document.getElementById('btnFind').click()");
		await WaitAsync("() => !!document.getElementById('findText')");
		await Page.EvaluateFunctionAsync(
			"(t, c, w) => { const c1 = document.getElementById('findCase'); c1.checked = c; const w1 = document.getElementById('findWord'); w1.checked = w; " +
			"const f = document.getElementById('findText'); f.value = t; f.dispatchEvent(new Event('input')); }", text, matchCase, wholeWord);
	}

	private Task<string> FindCountAsync() => EvalAsync<string>("document.getElementById('findCount').textContent");

	[Fact]
	public async Task Find_counts_matches_in_the_page_text_only()
	{
		await NewTemplateAsync(Page1);
		await OpenFindAsync("policy");
		// "Policy" heading, "policy covers", "each policy", "Policyholder" (not whole word) — never the data field.
		Assert.Equal("4 matches", await FindCountAsync());

		await OpenFindAsync("policy", wholeWord: true);
		Assert.Equal("3 matches", await FindCountAsync());

		await OpenFindAsync("Policy", matchCase: true);
		Assert.Equal("2 matches", await FindCountAsync());

		await OpenFindAsync("number");
		Assert.Equal("No matches in the page text.", await FindCountAsync());
		Assert.True(await EvalAsync<bool>("document.getElementById('replaceAll').disabled"));
		await CloseModalAsync();
	}

	[Fact]
	public async Task Next_and_previous_select_each_match()
	{
		await NewTemplateAsync(Page1);
		await OpenFindAsync("coverage");
		await Page.EvaluateExpressionAsync("document.getElementById('findNext').click()");
		Assert.Equal("h2", await EvalAsync<string>("grapesjs.editors[0].getSelected().get('tagName')"));
		await Page.EvaluateExpressionAsync("document.getElementById('findNext').click()");
		Assert.Equal("2 of 2 matches", await FindCountAsync());
		Assert.Equal("p", await EvalAsync<string>("grapesjs.editors[0].getSelected().get('tagName')"));
		await Page.EvaluateExpressionAsync("document.getElementById('findNext').click()");
		Assert.Equal("1 of 2 matches", await FindCountAsync());
		await Page.EvaluateExpressionAsync("document.getElementById('findPrev').click()");
		Assert.Equal("2 of 2 matches", await FindCountAsync());
		await CloseModalAsync();
	}

	[Fact]
	public async Task Replace_all_changes_the_text_but_not_data_fields()
	{
		await NewTemplateAsync(Page1);
		await OpenFindAsync("policy", wholeWord: true);
		await Page.EvaluateExpressionAsync("document.getElementById('replaceText').value = 'contract'");
		Assert.Equal("Replaced 3 occurrences.", await ActAsync("document.getElementById('replaceAll').click()"));
		await CloseModalAsync();

		var html = await HtmlAsync();
		Assert.Contains("contract Coverage", html);
		Assert.Contains("The contract covers", html);
		Assert.Contains("each contract.", html);
		Assert.Contains("Policyholder notice", html);
		Assert.Contains("{{ policy.number }}", html);
		Assert.True(await EvalAsync<int>("grapesjs.editors[0].getDirtyCount()") > 0);
	}

	[Fact]
	public async Task Replace_changes_one_match_at_a_time()
	{
		await NewTemplateAsync("<p>alpha beta alpha</p><p>alpha</p>");
		await OpenFindAsync("alpha", matchCase: true);
		await Page.EvaluateExpressionAsync("document.getElementById('replaceText').value = 'gamma'");
		Assert.Equal("Replaced 1 occurrence.", await ActAsync("document.getElementById('replaceOne').click()"));
		Assert.Equal("1 of 2 matches", await FindCountAsync());
		await CloseModalAsync();
		Assert.Contains("gamma beta alpha", await HtmlAsync());
	}

	[Fact]
	public async Task Replacements_cannot_add_template_code()
	{
		await NewTemplateAsync("<p>Hello world</p>");
		await OpenFindAsync("world");
		await Page.EvaluateExpressionAsync("document.getElementById('replaceText').value = '{{ secret }}'");
		await Page.EvaluateExpressionAsync("document.getElementById('replaceAll').click()");
		Assert.Contains("can't contain {{", await EvalAsync<string>("document.getElementById('findError').textContent"));
		await CloseModalAsync();
		Assert.Contains("Hello world", await HtmlAsync());
	}

	[Fact]
	public async Task Ctrl_F_opens_find()
	{
		await NewTemplateAsync("<p>Hello</p>");
		await Page.EvaluateExpressionAsync("document.activeElement && document.activeElement.blur()");
		await Page.Keyboard.DownAsync("Control");
		await Page.Keyboard.PressAsync("f");
		await Page.Keyboard.UpAsync("Control");
		await WaitAsync("() => !!document.getElementById('findText')");
		await CloseModalAsync();
	}

	// ---- Spelling ----------------------------------------------------------------------------------------------------

	private async Task<string> OpenSpellingAsync()
	{
		var status = await ActAsync("document.getElementById('btnSpelling').click()");
		await WaitAsync("() => !!document.querySelector('.spelling')");
		return status;
	}

	private Task<string[]> SpellWordsAsync() =>
		EvalAsync<string[]>("[...document.querySelectorAll('.spelling .spell-row')].map(r => r.getAttribute('data-word'))");

	[Fact]
	public async Task Spell_check_lists_misspelled_page_words_only()
	{
		await NewTemplateAsync(
			"<p>We recieve the insurence policy for Enumclaw. CPP and TIV are fine. {{ polcy.nmbr }}</p>" +
			"<p><span class=\"df\" data-field=\"policy.nmbrx\"></span> recieve again</p>");
		Assert.Equal("2 possible spelling mistakes.", await OpenSpellingAsync());
		Assert.Equal(["recieve", "insurence"], await SpellWordsAsync());
		Assert.Equal("2\u00d7", await EvalAsync<string>("document.querySelector('.spell-row[data-word=recieve] .spell-count').textContent"));
		Assert.Contains("receive", await EvalAsync<string[]>("[...document.querySelectorAll('.spell-row[data-word=recieve] .spell-suggestion')].map(b => b.textContent)"));
		await CloseModalAsync();
	}

	[Fact]
	public async Task A_suggestion_replaces_the_word_everywhere()
	{
		await NewTemplateAsync("<p>We recieve it.</p><p>They recieve it too. Unrecieved stays.</p>");
		await OpenSpellingAsync();
		var status = await ActAsync("[...document.querySelectorAll('.spell-row[data-word=recieve] .spell-suggestion')].find(b => b.textContent === 'receive').click()");
		Assert.Equal("Replaced \"recieve\" with \"receive\" (2\u00d7).", status);
		Assert.DoesNotContain("recieve", await SpellWordsAsync());
		await CloseModalAsync();
		var html = await HtmlAsync();
		Assert.Contains("We receive it.", html);
		Assert.Contains("They receive it too.", html);
		Assert.Contains("Unrecieved", html);
	}

	[Fact]
	public async Task Ignore_hides_a_word_for_this_session()
	{
		var word = "Blorptastic";
		await NewTemplateAsync($"<p>{word} wording</p>");
		await OpenSpellingAsync();
		Assert.Contains(word, await SpellWordsAsync());
		await Page.EvaluateExpressionAsync($"document.querySelector('.spell-row[data-word={word}] .spell-ignore').click()");
		Assert.Equal("No spelling mistakes found in the page text.", await EvalAsync<string>("document.querySelector('.spell-summary').textContent"));
		await CloseModalAsync();
		Assert.Equal("No spelling mistakes found.", await OpenSpellingAsync());
		await CloseModalAsync();
	}

	[Fact]
	public async Task Add_to_dictionary_is_shared_and_remembered()
	{
		var word = "Zentrovia" + Guid.NewGuid().ToString("N")[..4].Replace("0", "q").Replace("1", "w").Replace("2", "e").Replace("3", "r")
			.Replace("4", "t").Replace("5", "y").Replace("6", "u").Replace("7", "i").Replace("8", "o").Replace("9", "p");
		await NewTemplateAsync($"<p>{word} Insurance</p>");
		await OpenSpellingAsync();
		var status = await ActAsync($"document.querySelector('.spell-row[data-word=\"{word}\"] .spell-add').click()");
		Assert.Equal($"\"{word}\" added to the shared dictionary.", status);
		Assert.Contains(word, (await fixture.Http.GetFromJsonAsync<string[]>("api/spelling/words"))!);
		await CloseModalAsync();
		Assert.Equal("No spelling mistakes found.", await OpenSpellingAsync());
		await CloseModalAsync();
	}

	// ---- Comments ----------------------------------------------------------------------------------------------------

	private async Task SelectAsync(string selector) =>
		await Page.EvaluateFunctionAsync("s => { const e = grapesjs.editors[0]; e.select(e.getWrapper().find(s)[0]); }", selector);

	private async Task<string> AddCommentAsync(string text, string author = "Ann Reviewer")
	{
		await Page.EvaluateFunctionAsync("(a, t) => { const n = document.getElementById('commentAuthor'); n.value = a; n.dispatchEvent(new Event('change')); document.getElementById('commentText').value = t; }", author, text);
		return await ActAsync("document.getElementById('btnAddComment').click()");
	}

	private Task<string[]> CommentTextsAsync() =>
		EvalAsync<string[]>("[...document.querySelectorAll('#commentList .comment .comment-text')].map(c => c.textContent)");

	[Fact]
	public async Task Comments_are_added_to_the_selected_element_and_marked_on_the_page()
	{
		await NewTemplateAsync(Page1);
		await Page.EvaluateExpressionAsync("document.querySelector('.md-tab[data-tab=tabComments]').click()");
		Assert.Equal("No comments yet.", await EvalAsync<string>("document.querySelector('#commentList .pane-empty').textContent"));

		await SelectAsync("h2");
		Assert.Equal("Comment added.", await AddCommentAsync("Use title case"));
		Assert.Equal(["Use title case"], await CommentTextsAsync());
		Assert.Equal("Comments (1)", await EvalAsync<string>("document.getElementById('commentsTabLabel').textContent"));
		Assert.StartsWith("Text: Policy Coverage", await EvalAsync<string>("document.querySelector('#commentList .comment-target').textContent"));
		Assert.StartsWith("Ann Reviewer \u00b7", await EvalAsync<string>("document.querySelector('#commentList .comment-meta').textContent"));
		Assert.Equal("1", await EvalAsync<string>("grapesjs.editors[0].getWrapper().find('h2')[0].getEl().getAttribute('data-comments')"));
		Assert.Equal("", await EvalAsync<string>("document.getElementById('commentText').value"));

		// The comment is not part of the document.
		Assert.DoesNotContain("Use title case", await HtmlAsync());
		Assert.DoesNotContain("data-comments", await HtmlAsync());

		// Clicking the element name selects it.
		await Page.EvaluateExpressionAsync("grapesjs.editors[0].select(null)");
		await Page.EvaluateExpressionAsync("document.querySelector('#commentList .comment-target').click()");
		Assert.Equal("h2", await EvalAsync<string>("grapesjs.editors[0].getSelected().get('tagName')"));
	}

	[Fact]
	public async Task Commenting_needs_a_selection_a_name_and_text()
	{
		await NewTemplateAsync(Page1);
		Assert.Contains("Select an element", await AddCommentAsync("x"));
		await SelectAsync("h2");
		Assert.Contains("Enter your name", await AddCommentAsync("x", author: ""));
		Assert.Contains("Write the comment", await AddCommentAsync("   "));
		Assert.True(await EvalAsync<bool>("document.getElementById('status').classList.contains('error')"));
	}

	[Fact]
	public async Task Reply_resolve_reopen_and_delete()
	{
		await NewTemplateAsync(Page1);
		await Page.EvaluateExpressionAsync("document.querySelector('.md-tab[data-tab=tabComments]').click()");
		await SelectAsync("p");
		await AddCommentAsync("Shorter please");

		await Page.EvaluateExpressionAsync("document.querySelector('#commentList .comment-reply-toggle').click()");
		await Page.EvaluateExpressionAsync("document.querySelector('#commentList .comment-reply-text').value = 'Shortened in v2'");
		Assert.Equal("Reply added.", await ActAsync("document.querySelector('#commentList .comment-reply-send').click()"));
		Assert.Equal("Ann Reviewer: Shortened in v2", await EvalAsync<string>("document.querySelector('#commentList .comment-reply').textContent"));

		Assert.Equal("Comment resolved.", await ActAsync("document.querySelector('#commentList .comment-resolve').click()"));
		Assert.Empty(await CommentTextsAsync());
		Assert.Equal("Comments", await EvalAsync<string>("document.getElementById('commentsTabLabel').textContent"));
		Assert.Null(await EvalAsync<string?>("grapesjs.editors[0].getWrapper().find('p')[0].getEl().getAttribute('data-comments')"));

		await Page.EvaluateExpressionAsync("(() => { const f = document.getElementById('commentFilter'); f.value = 'all'; f.dispatchEvent(new Event('change')); })()");
		Assert.Equal(["Shorter please"], await CommentTextsAsync());
		Assert.Contains("Resolved by Ann Reviewer", await EvalAsync<string>("document.querySelector('#commentList .comment-meta').textContent"));
		Assert.Equal("Comment reopened.", await ActAsync("document.querySelector('#commentList .comment-resolve').click()"));
		Assert.Equal("Comments (1)", await EvalAsync<string>("document.getElementById('commentsTabLabel').textContent"));

		Assert.Equal("Comment deleted.", await ActAsync("document.querySelector('#commentList .comment-delete').click()"));
		Assert.Empty(await CommentTextsAsync());
		await Page.EvaluateExpressionAsync("(() => { const f = document.getElementById('commentFilter'); f.value = 'open'; f.dispatchEvent(new Event('change')); })()");
	}

	[Fact]
	public async Task The_selection_toolbar_opens_comments()
	{
		await NewTemplateAsync(Page1);
		await Page.EvaluateExpressionAsync("document.querySelector('.md-tab[data-tab=tabSettings]').click()");
		await SelectAsync("h2");
		await WaitAsync("() => !!document.querySelector('.gjs-toolbar .tb-add-comment')");
		await Page.EvaluateExpressionAsync("document.querySelector('.gjs-toolbar .tb-add-comment').dispatchEvent(new MouseEvent('mousedown', { bubbles: true }))");
		Assert.False(await EvalAsync<bool>("document.getElementById('tabComments').hidden"));
		Assert.Equal("commentText", await EvalAsync<string>("document.activeElement.id"));
	}

	[Fact]
	public async Task Comments_stay_on_their_element_after_saving_and_reopening()
	{
		await NewTemplateAsync(Page1);
		var name = await EvalAsync<string>("document.getElementById('templateName').value");
		await SelectAsync("p");
		await AddCommentAsync("Check this paragraph");
		// Adding a comment doesn't count as a change to the template.
		Assert.Equal("Saved draft v1.", await ActAsync("document.getElementById('btnSave').click()"));

		// Something else on the canvas, then reopen v1: the comment finds its paragraph by its saved anchor.
		await Page.EvaluateExpressionAsync("grapesjs.editors[0].setComponents('<p>Other</p><p>Content</p>')");
		await ActAsync("document.getElementById('btnOpen').click()");
		await WaitAsync("() => grapesjs.editors[0].getWrapper().find('p').length > 0 && !!grapesjs.editors[0].getWrapper().find('p')[0].getEl().getAttribute('data-comments')");
		Assert.Contains("The policy covers", await EvalAsync<string>("grapesjs.editors[0].Canvas.getDocument().querySelector('[data-comments]').textContent"));

		var saved = (await fixture.Http.GetFromJsonAsync<JsonElement[]>($"api/templates/{name}/comments"))!;
		Assert.Equal("Check this paragraph", Assert.Single(saved).GetProperty("text").GetString());
	}

	[Fact]
	public async Task Comments_on_removed_elements_say_so()
	{
		await NewTemplateAsync(Page1);
		await Page.EvaluateExpressionAsync("document.querySelector('.md-tab[data-tab=tabComments]').click()");
		await SelectAsync("h2");
		await AddCommentAsync("Remove this heading?");
		await Page.EvaluateExpressionAsync("(() => { const e = grapesjs.editors[0]; e.select(null); e.getWrapper().find('h2')[0].remove(); })()");
		await WaitAsync("() => !!document.querySelector('#commentList .comment-orphan')");
		Assert.EndsWith("(no longer on the page)", await EvalAsync<string>("document.querySelector('#commentList .comment-orphan').textContent"));
	}

	[Fact]
	public async Task A_comment_whose_anchor_was_never_saved_finds_its_element_by_position()
	{
		await NewTemplateAsync(Page1);
		var name = await EvalAsync<string>("document.getElementById('templateName').value");
		(await fixture.Http.PostAsJsonAsync($"api/templates/{name}/comments",
			new { anchor = "zzzzzzzz", path = "0", element = "Text: Policy Coverage", author = "Ann", text = "From another reviewer" })).EnsureSuccessStatusCode();
		await ActAsync("document.getElementById('templateName').dispatchEvent(new Event('change'))");
		await Page.EvaluateFunctionAsync("h => grapesjs.editors[0].setComponents(h)", Page1);
		await Page.EvaluateExpressionAsync("document.querySelector('.md-tab[data-tab=tabComments]').click()");
		await ActAsync("document.getElementById('templateName').dispatchEvent(new Event('change'))");
		Assert.Equal(1, await EvalAsync<int>("document.querySelectorAll('#commentList .comment-target').length"));
		Assert.Equal("1", await EvalAsync<string>("grapesjs.editors[0].getWrapper().find('h2')[0].getEl().getAttribute('data-comments')"));
	}

	[Fact]
	public async Task Comment_text_is_shown_as_text()
	{
		await NewTemplateAsync(Page1);
		await Page.EvaluateExpressionAsync("document.querySelector('.md-tab[data-tab=tabComments]').click()");
		await SelectAsync("h2");
		await AddCommentAsync("<img src=x onerror=\"window.__commentXss=1\">", author: "<b>Mallory</b>");
		Assert.True(await EvalAsync<bool>("!document.querySelector('#commentList img') && !document.querySelector('#commentList .comment-meta b')"));
		Assert.True(await EvalAsync<bool>("window.__commentXss === undefined"));
	}
}
