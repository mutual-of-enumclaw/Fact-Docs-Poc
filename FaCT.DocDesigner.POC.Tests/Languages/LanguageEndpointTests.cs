using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FaCT.DocDesigner.POC.Tests.Clauses;
using FaCT.DocDesigner.POC.Tests.Security;

namespace FaCT.DocDesigner.POC.Tests.Languages;

/// <summary>Language versions through the API: saving, publishing, rendering (with English fallback), the field check.</summary>
public sealed class LanguageEndpointTests(ClauseAppFactory factory) : IClassFixture<ClauseAppFactory>
{
	private const string Marker = "<div class=\"doc-language lang-es\"></div>";

	private readonly HttpClient _client = factory.CreateClient();

	private static string Unique(string name) => name + "-" + Guid.NewGuid().ToString("N")[..8];

	private static object Draft(string html, object? model = null) => new { project = new { }, html, css = "", model };

	private async Task<JsonElement> SaveAsync(string name, string html, string? lang = null, string kind = "templates", object? model = null)
	{
		var response = await _client.PutAsJsonAsync($"/api/{kind}/{name}/draft" + (lang is null ? "" : "?lang=" + lang), Draft(html, model));
		Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
		return await response.Content.ReadFromJsonAsync<JsonElement>();
	}

	private async Task PublishAsync(string name, int version, string? lang = null, string kind = "templates")
	{
		var response = await _client.PostAsync($"/api/{kind}/{name}/versions/{version}/publish" + (lang is null ? "" : "?lang=" + lang), null);
		Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
	}

	private async Task<string> HtmlAsync(string name, int version, string? lang = null, string kind = "templates") =>
		(await _client.GetFromJsonAsync<JsonElement>($"/api/{kind}/{name}/versions/{version}" + (lang is null ? "" : "?lang=" + lang))).GetProperty("html").GetString()!;

	private static async Task<string> ErrorAsync(HttpResponseMessage response) =>
		(await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()!;

	private static string Words(byte[] pdf) => string.Join(' ', PdfText.Extract(pdf).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

	private async Task<JsonElement> LanguageAsync(string name, string code, string kind = "templates") =>
		(await _client.GetFromJsonAsync<JsonElement>($"/api/{kind}/{name}/languages")).EnumerateArray().Single(l => l.GetProperty("code").GetString() == code);

	private static string[] Strings(JsonElement array) => array.EnumerateArray().Select(e => e.GetString()!).ToArray();

	// ---- saving ------------------------------------------------------------------------------------------------------

	[Fact]
	public async Task A_translation_needs_the_English_version_first()
	{
		var name = Unique("notice");
		var response = await _client.PutAsJsonAsync($"/api/templates/{name}/draft?lang=es", Draft("<p>Aviso</p>"));
		Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
		Assert.Equal($"Save the English version of '{name}' first: the Spanish version uses its data fields.", await ErrorAsync(response));
	}

	[Fact]
	public async Task Each_language_has_its_own_versions()
	{
		var name = Unique("notice");
		await SaveAsync(name, "<body><p>Notice</p></body>");
		await PublishAsync(name, 1);
		await SaveAsync(name, "<body><p>Notice v2</p></body>");
		var spanish = await SaveAsync(name, "<body><p>Aviso</p></body>", "es");
		Assert.Equal(1, spanish.GetProperty("version").GetInt32());
		Assert.Equal("Draft", spanish.GetProperty("status").GetString());

		var englishVersions = await _client.GetFromJsonAsync<JsonElement>($"/api/templates/{name}/versions");
		var spanishVersions = await _client.GetFromJsonAsync<JsonElement>($"/api/templates/{name}/versions?lang=es");
		Assert.Equal(2, englishVersions.GetArrayLength());
		Assert.Equal(1, spanishVersions.GetArrayLength());
		Assert.Equal(0, (await _client.GetFromJsonAsync<JsonElement>($"/api/templates/{name}/versions?lang=fr")).GetArrayLength());
		Assert.Equal("<body><p>Notice</p></body>", await HtmlAsync(name, 1));
		Assert.Equal("<body>" + Marker + "<p>Aviso</p></body>", await HtmlAsync(name, 1, "es"));
		Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/templates/{name}/versions/2?lang=es")).StatusCode);
	}

	[Fact]
	public async Task The_server_keeps_the_marker_in_step_with_the_language_saved()
	{
		var name = Unique("notice");
		// a Spanish marker saved as English is removed; a French marker saved as Spanish becomes Spanish
		await SaveAsync(name, "<body>" + Marker + "<p>Notice</p></body>");
		Assert.Equal("<body><p>Notice</p></body>", await HtmlAsync(name, 1));
		await SaveAsync(name, "<body><div class=\"doc-language lang-fr\"></div><p>Aviso</p></body>", "es");
		Assert.Equal("<body>" + Marker + "<p>Aviso</p></body>", await HtmlAsync(name, 1, "es"));
	}

	[Theory]
	[InlineData("GET", "/api/templates/{0}/versions?lang=de")]
	[InlineData("GET", "/api/templates/{0}/versions/1?lang=de")]
	[InlineData("PUT", "/api/templates/{0}/draft?lang=de")]
	[InlineData("DELETE", "/api/templates/{0}/draft?lang=de")]
	[InlineData("POST", "/api/templates/{0}/versions/1/publish?lang=de")]
	[InlineData("POST", "/api/templates/{0}/pdf?lang=de")]
	[InlineData("GET", "/api/templates/{0}/export.docx?lang=de")]
	[InlineData("GET", "/api/templates/{0}/export.html?lang=de")]
	[InlineData("GET", "/api/clauses/{0}/versions?lang=de")]
	[InlineData("GET", "/api/clauses/{0}/content?lang=de")]
	[InlineData("PUT", "/api/clauses/{0}/draft?lang=ES")]
	[InlineData("POST", "/api/templates/{0}/diff?lang=de")]
	public async Task An_unknown_language_is_refused(string method, string url)
	{
		var request = new HttpRequestMessage(new HttpMethod(method), string.Format(url, Unique("x")));
		if (method is "PUT" or "POST") request.Content = JsonContent.Create(new { project = new { }, html = "<p>x</p>", css = "" });
		var response = await _client.SendAsync(request);
		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		Assert.Contains("isn't a document language", await ErrorAsync(response));
	}

	[Fact]
	public async Task Discarding_the_Spanish_draft_leaves_English_alone()
	{
		var name = Unique("notice");
		await SaveAsync(name, "<p>Notice</p>");
		await SaveAsync(name, "<p>Aviso</p>", "es");
		Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"/api/templates/{name}/draft?lang=es")).StatusCode);
		Assert.Equal(0, (await _client.GetFromJsonAsync<JsonElement>($"/api/templates/{name}/versions?lang=es")).GetArrayLength());
		Assert.Equal(1, (await _client.GetFromJsonAsync<JsonElement>($"/api/templates/{name}/versions")).GetArrayLength());
	}

	[Fact]
	public async Task Saves_and_publishes_of_a_translation_are_audited_with_the_language()
	{
		var name = Unique("notice");
		await SaveAsync(name, "<p>Notice</p>");
		await SaveAsync(name, "<p>Aviso</p>", "es");
		await PublishAsync(name, 1, "es");
		var entries = await _client.GetFromJsonAsync<JsonElement>($"/api/audit?kind=templates&name={name}");
		var rows = entries.EnumerateArray().Select(e => e.GetProperty("action").GetString() + "|" + (e.TryGetProperty("detail", out var d) ? d.GetString() : null)).ToList();
		Assert.Equal(["version.published|Spanish", "draft.saved|Spanish", "draft.saved|"], rows);
	}

	// ---- rendering -----------------------------------------------------------------------------------------------------

	[Fact]
	public async Task The_published_Spanish_version_renders_in_Spanish()
	{
		var name = Unique("notice");
		await SaveAsync(name, "<body><p>Renewal: {{ renewal }}</p></body>");
		await PublishAsync(name, 1);
		await SaveAsync(name, "<body><p>Renovaci\u00f3n: {{ renewal }}</p></body>", "es");
		await PublishAsync(name, 1, "es");

		var response = await _client.PostAsJsonAsync($"/api/templates/{name}/pdf?lang=es", new { renewal = true });
		Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
		Assert.Equal("es", response.Headers.GetValues("X-Template-Language").Single());
		var text = Words(await response.Content.ReadAsByteArrayAsync());
		Assert.Contains("Renovaci\u00f3n: S\u00ed", text);
		Assert.Contains("P\u00e1gina 1 de 1", text);

		var english = await _client.PostAsJsonAsync($"/api/templates/{name}/pdf", new { renewal = true });
		Assert.Equal("en", english.Headers.GetValues("X-Template-Language").Single());
		Assert.Contains("Renewal: Yes", Words(await english.Content.ReadAsByteArrayAsync()));
	}

	[Fact]
	public async Task Without_a_published_Spanish_version_the_English_one_goes_out_and_says_so()
	{
		var name = Unique("notice");
		await SaveAsync(name, "<body><p>Renewal: {{ renewal }}</p></body>");
		await PublishAsync(name, 1);
		await SaveAsync(name, "<body><p>Renovaci\u00f3n: {{ renewal }}</p></body>", "es");

		var response = await _client.PostAsJsonAsync($"/api/templates/{name}/pdf?lang=es", new { renewal = true });
		Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
		Assert.Equal("en; requested es", response.Headers.GetValues("X-Template-Language").Single());
		Assert.Equal("1 (Published)", response.Headers.GetValues("X-Template-Version").Single());
		Assert.Contains("Renewal: Yes", Words(await response.Content.ReadAsByteArrayAsync()));
	}

	[Fact]
	public async Task A_Spanish_draft_can_be_proofed_by_version_and_is_stamped_draft()
	{
		var name = Unique("notice");
		await SaveAsync(name, "<p>Notice</p>");
		await SaveAsync(name, "<p>Aviso</p>", "es");
		var response = await _client.PostAsync($"/api/templates/{name}/pdf?lang=es&version=1", null);
		Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
		Assert.Equal("DRAFT", response.Headers.GetValues("X-Watermark").Single());
		Assert.Equal("es", response.Headers.GetValues("X-Template-Language").Single());
		Assert.Contains("Aviso", Words(await response.Content.ReadAsByteArrayAsync()));
		// a version Spanish doesn't have is not found (no fallback for a pinned version)
		Assert.Equal(HttpStatusCode.NotFound, (await _client.PostAsync($"/api/templates/{name}/pdf?lang=es&version=2", null)).StatusCode);
	}

	[Fact]
	public async Task Nothing_published_in_any_language_is_a_conflict()
	{
		var name = Unique("notice");
		await SaveAsync(name, "<p>Notice</p>");
		await SaveAsync(name, "<p>Aviso</p>", "es");
		Assert.Equal(HttpStatusCode.Conflict, (await _client.PostAsync($"/api/templates/{name}/pdf?lang=es", null)).StatusCode);
	}

	[Fact]
	public async Task The_Spanish_version_renders_with_its_own_model_when_no_data_is_sent()
	{
		var name = Unique("notice");
		await SaveAsync(name, "<p>{{ insured }}</p>", model: new { insured = "Acme" });
		await SaveAsync(name, "<p>Asegurado {{ insured }}</p>", "es", model: new { insured = "Panader\u00eda Acme" });
		await PublishAsync(name, 1, "es");
		var text = Words(await (await _client.PostAsync($"/api/templates/{name}/pdf?lang=es", null)).Content.ReadAsByteArrayAsync());
		Assert.Contains("Asegurado Panader\u00eda Acme", text);
	}

	[Fact]
	public async Task Exports_use_the_language_version()
	{
		var name = Unique("notice");
		await SaveAsync(name, "<p>Notice {{ policy.number }}</p>");
		await SaveAsync(name, "<p>Aviso {{ policy.number }}</p>", "es");

		var html = await _client.GetStringAsync($"/api/templates/{name}/export.html?lang=es");
		Assert.Contains("<html lang=\"es\">", html);
		Assert.Contains("Aviso {{ policy.number }}", html);

		var docx = await _client.GetAsync($"/api/templates/{name}/export.docx?lang=es");
		Assert.True(docx.IsSuccessStatusCode, await docx.Content.ReadAsStringAsync());
		var warnings = JsonSerializer.Deserialize<string[]>(Encoding.UTF8.GetString(Convert.FromBase64String(docx.Headers.GetValues("X-Export-Warnings").Single())))!;
		Assert.Contains(warnings, w => w.StartsWith("This is the Spanish version:", StringComparison.Ordinal));
		var english = await _client.GetAsync($"/api/templates/{name}/export.docx");
		Assert.False(english.Headers.Contains("X-Export-Warnings") &&
			Encoding.UTF8.GetString(Convert.FromBase64String(english.Headers.GetValues("X-Export-Warnings").Single())).Contains("Spanish", StringComparison.Ordinal));
	}

	[Fact]
	public async Task Compare_with_published_compares_with_the_published_Spanish_version()
	{
		var name = Unique("notice");
		await SaveAsync(name, "<body><p>Notice</p></body>");
		await PublishAsync(name, 1);
		await SaveAsync(name, "<body><p>Aviso</p></body>", "es");
		await PublishAsync(name, 1, "es");

		var response = await _client.PostAsJsonAsync($"/api/templates/{name}/diff?lang=es", new { html = "<body>" + Marker + "<p>Aviso nuevo</p></body>", css = "" });
		var diff = await response.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(1, diff.GetProperty("against").GetProperty("version").GetInt32());
		var lines = diff.GetProperty("text").GetProperty("lines").EnumerateArray()
			.Select(l => l.GetProperty("type").GetString() + ":" + (l.TryGetProperty("text", out var t) ? t.GetString() : "")).ToList();
		Assert.Contains("removed:Aviso", lines);
		Assert.Contains("added:Aviso nuevo", lines);
	}

	// ---- the field check -------------------------------------------------------------------------------------------------

	[Fact]
	public async Task Languages_lists_every_language_with_its_versions()
	{
		var name = Unique("notice");
		await SaveAsync(name, "<p>{{ policy.number }}</p>");
		await PublishAsync(name, 1);
		await SaveAsync(name, "<p>{{ policy.number }}</p>", "es");

		var languages = await _client.GetFromJsonAsync<JsonElement>($"/api/templates/{name}/languages");
		Assert.Equal(["en", "es", "fr"], languages.EnumerateArray().Select(l => l.GetProperty("code").GetString()));
		var english = languages[0];
		Assert.True(english.GetProperty("isEnglish").GetBoolean());
		Assert.Equal("English", english.GetProperty("name").GetString());
		Assert.Equal(1, english.GetProperty("latestVersion").GetInt32());
		Assert.Equal(1, english.GetProperty("publishedVersion").GetInt32());
		var spanish = languages[1];
		Assert.Equal("Spanish", spanish.GetProperty("name").GetString());
		Assert.Equal("es-US", spanish.GetProperty("culture").GetString());
		Assert.Equal(1, spanish.GetProperty("latestVersion").GetInt32());
		Assert.Equal(JsonValueKind.Null, spanish.GetProperty("publishedVersion").ValueKind);
		Assert.Empty(Strings(spanish.GetProperty("missingFields")));
		Assert.Empty(Strings(spanish.GetProperty("extraFields")));
		var french = languages[2];
		Assert.Equal(JsonValueKind.Null, french.GetProperty("latestVersion").ValueKind);
		Assert.Equal(JsonValueKind.Null, french.GetProperty("missingFields").ValueKind);
	}

	[Fact]
	public async Task The_field_check_lists_fields_a_translation_misses_or_adds()
	{
		var name = Unique("notice");
		await SaveAsync(name, "<p>{{ policy.number }} {{ insured.name }} {% if policy.renewal %}R{% endif %} {{ brand.name }}</p>");
		await SaveAsync(name, "<p>{{ policy.number }} {{ insured.nombre }} {% for c in coverages %}{{ c.name }}{% endfor %}</p>", "es");

		var spanish = await LanguageAsync(name, "es");
		Assert.Equal(["insured.name", "policy.renewal"], Strings(spanish.GetProperty("missingFields")));
		Assert.Equal(["coverages", "coverages[].name", "insured.nombre"], Strings(spanish.GetProperty("extraFields")));
	}

	[Fact]
	public async Task The_field_check_counts_fields_of_included_clauses_and_names_clauses_printed_in_English()
	{
		var name = Unique("notice");
		var translated = Unique("excl");
		var untranslated = Unique("excl");
		await SaveAsync(translated, "<p>{{ policy.number }}</p>", kind: "clauses");
		await PublishAsync(translated, 1, kind: "clauses");
		await SaveAsync(translated, "<p>N.\u00ba {{ policy.number }}</p>", "es", "clauses");
		await PublishAsync(translated, 1, "es", "clauses");
		await SaveAsync(untranslated, "<p>{{ insured.name }}</p>", kind: "clauses");
		await PublishAsync(untranslated, 1, kind: "clauses");

		await SaveAsync(name, $"{{% include '{translated}' %}}{{% include '{untranslated}' %}}");
		await SaveAsync(name, $"{{% include '{translated}' %}}{{% include '{untranslated}' %}}", "es");

		var spanish = await LanguageAsync(name, "es");
		Assert.Empty(Strings(spanish.GetProperty("missingFields")));
		Assert.Equal([untranslated], Strings(spanish.GetProperty("englishClauses")));
	}

	// ---- clause translations -----------------------------------------------------------------------------------------

	[Fact]
	public async Task Clause_translations_have_their_own_versions_and_no_marker()
	{
		var name = Unique("excl");
		var refused = await _client.PutAsJsonAsync($"/api/clauses/{name}/draft?lang=es", Draft("<p>Exclusi\u00f3n</p>"));
		Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);

		await SaveAsync(name, "<p>Exclusion</p>", kind: "clauses");
		await PublishAsync(name, 1, kind: "clauses");
		await SaveAsync(name, Marker + "<p>Exclusi\u00f3n</p>", "es", "clauses");
		Assert.Equal("<p>Exclusi\u00f3n</p>", await HtmlAsync(name, 1, "es", "clauses"));

		// a Spanish template sees the English clause until the Spanish one is published
		var before = await _client.GetFromJsonAsync<JsonElement>($"/api/clauses/{name}/content?lang=es");
		Assert.Equal("<p>Exclusion</p>", before.GetProperty("html").GetString());
		Assert.Equal("en", before.GetProperty("language").GetString());
		await PublishAsync(name, 1, "es", "clauses");
		var after = await _client.GetFromJsonAsync<JsonElement>($"/api/clauses/{name}/content?lang=es");
		Assert.Equal("<p>Exclusi\u00f3n</p>", after.GetProperty("html").GetString());
		Assert.Equal("es", after.GetProperty("language").GetString());
		Assert.Equal("<p>Exclusion</p>", (await _client.GetFromJsonAsync<JsonElement>($"/api/clauses/{name}/content")).GetProperty("html").GetString());

		Assert.Equal(1, (await _client.GetFromJsonAsync<JsonElement>($"/api/clauses/{name}/versions?lang=es")).GetArrayLength());
		// the published Spanish clause is not a draft: nothing to discard
		Assert.Equal(HttpStatusCode.NotFound, (await _client.DeleteAsync($"/api/clauses/{name}/draft?lang=es")).StatusCode);
	}

	[Fact]
	public async Task Languages_is_only_for_templates_and_clauses()
	{
		Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/api/blocks/x/languages")).StatusCode);
		Assert.Equal(HttpStatusCode.BadRequest, (await _client.GetAsync("/api/templates/bad%20name/languages")).StatusCode);
	}

	// ---- translations elsewhere: field usage, clause usage, gallery, duplicate -----------------------------------------

	[Fact]
	public async Task Field_usage_search_finds_translations_and_says_which_language()
	{
		var name = Unique("notice");
		var field = "lang" + Guid.NewGuid().ToString("N")[..8];
		await SaveAsync(name, $"<p>{{{{ {field}.shared }}}}</p>");
		await SaveAsync(name, $"<p>{{{{ {field}.shared }}}} {{{{ {field}.spanish }}}}</p>", "es");

		var shared = await _client.PostAsJsonAsync("/api/usage/fields", new { path = field + ".shared" });
		var hits = (await shared.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("documents").EnumerateArray().ToList();
		Assert.Equal(["|1", "es|1"], hits.Select(h => (h.GetProperty("language").GetString() ?? "") + "|" + h.GetProperty("version").GetInt32()));
		Assert.All(hits, h => Assert.Equal(name, h.GetProperty("name").GetString()));

		var spanishOnly = await _client.PostAsJsonAsync("/api/usage/fields", new { path = field + ".spanish" });
		var spanishHits = (await spanishOnly.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("documents").EnumerateArray().ToList();
		Assert.Equal("es", Assert.Single(spanishHits).GetProperty("language").GetString());

		// the index counts the document once, whatever its languages
		var index = await _client.GetFromJsonAsync<JsonElement>("/api/usage/fields");
		Assert.Equal(1, index.EnumerateArray().Single(e => e.GetProperty("path").GetString() == field + ".shared").GetProperty("documents").GetInt32());
		Assert.Equal(1, index.EnumerateArray().Single(e => e.GetProperty("path").GetString() == field + ".spanish").GetProperty("documents").GetInt32());
	}

	[Fact]
	public async Task Field_usage_through_clauses_names_the_clause_translation()
	{
		var name = Unique("notice");
		var clause = Unique("excl");
		var field = "lang" + Guid.NewGuid().ToString("N")[..8];
		await SaveAsync(clause, $"<p>{{{{ {field} }}}}</p>", kind: "clauses");
		await PublishAsync(clause, 1, kind: "clauses");
		await SaveAsync(clause, $"<p>N.\u00ba {{{{ {field} }}}}</p>", "es", "clauses");
		await PublishAsync(clause, 1, "es", "clauses");
		await SaveAsync(name, $"{{% include '{clause}' %}}");
		await SaveAsync(name, $"{{% include '{clause}' %}}", "es");

		var response = await _client.PostAsJsonAsync("/api/usage/fields", new { path = field, kind = "templates" });
		var hits = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("documents").EnumerateArray().ToList();
		Assert.Equal([$"{clause} v1", $"{clause} v1 (es)"], hits.Select(h => Strings(h.GetProperty("via")).Single()));
	}

	[Fact]
	public async Task Clause_usage_includes_translated_templates()
	{
		var name = Unique("notice");
		var clause = Unique("excl");
		await SaveAsync(clause, "<p>x</p>", kind: "clauses");
		await SaveAsync(name, "<p>no clause</p>");
		await SaveAsync(name, $"{{% include '{clause}' %}}", "es");

		var usage = await _client.GetFromJsonAsync<JsonElement>($"/api/clauses/{clause}/usage");
		var entry = Assert.Single(usage.EnumerateArray());
		Assert.Equal(name, entry.GetProperty("template").GetString());
		Assert.Equal("es", entry.GetProperty("language").GetString());
	}

	[Fact]
	public async Task The_gallery_lists_a_documents_translations()
	{
		var name = Unique("notice");
		await SaveAsync(name, "<p>Notice</p>");
		await SaveAsync(name, "<p>Aviso</p>", "es");
		var gallery = await _client.GetFromJsonAsync<JsonElement>("/api/gallery?kind=templates");
		var entry = gallery.EnumerateArray().Single(e => e.GetProperty("name").GetString() == name);
		Assert.Equal(["es"], Strings(entry.GetProperty("languages")));
		// translations are not separate documents
		Assert.Single(gallery.EnumerateArray(), e => e.GetProperty("name").GetString() == name);
	}

	[Fact]
	public async Task Duplicating_copies_each_translation_as_its_draft_v1()
	{
		var name = Unique("notice");
		var copy = Unique("copy");
		await SaveAsync(name, "<p>Notice</p>");
		await SaveAsync(name, "<p>Aviso v1</p>", "es");
		await PublishAsync(name, 1, "es");
		await SaveAsync(name, "<p>Aviso v2</p>", "es");

		var response = await _client.PostAsJsonAsync($"/api/templates/{name}/duplicate", new { newName = copy });
		Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
		Assert.Equal(["es"], Strings((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("languagesCopied")));

		var versions = await _client.GetFromJsonAsync<JsonElement>($"/api/templates/{copy}/versions?lang=es");
		Assert.Equal(1, versions.GetArrayLength());
		Assert.Equal("Draft", versions[0].GetProperty("status").GetString());
		// the published Spanish version is the one copied
		Assert.Contains("Aviso v1", await HtmlAsync(copy, 1, "es"));
		Assert.Equal(0, (await _client.GetFromJsonAsync<JsonElement>($"/api/templates/{copy}/versions?lang=fr")).GetArrayLength());

		var audit = await _client.GetFromJsonAsync<JsonElement>($"/api/audit?name={copy}&action=template.duplicated");
		Assert.EndsWith(" with Spanish", audit[0].GetProperty("detail").GetString());
	}

	[Fact]
	public async Task A_name_left_with_only_a_translation_is_taken()
	{
		var name = Unique("notice");
		var taken = Unique("taken");
		await SaveAsync(name, "<p>Notice</p>");
		await SaveAsync(taken, "<p>Old</p>");
		await SaveAsync(taken, "<p>Viejo</p>", "es");
		Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"/api/templates/{taken}/draft")).StatusCode);

		var response = await _client.PostAsJsonAsync($"/api/templates/{name}/duplicate", new { newName = taken });
		Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
	}
}

/// <summary>"A second person must publish" tells the languages apart (their version numbers overlap).</summary>
public sealed class LanguagePublishSecurityTests(SecureAppFactory factory) : IClassFixture<SecureAppFactory>
{
	private async Task<HttpClient> SignInAsync(string id)
	{
		var client = factory.CreateClient();
		var response = await client.PostAsJsonAsync("/api/signin", new { id });
		Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
		return client;
	}

	[Fact]
	public async Task Saving_Spanish_v1_doesnt_block_publishing_English_v1_and_vice_versa()
	{
		var name = "notice-" + Guid.NewGuid().ToString("N")[..8];
		var ann = await SignInAsync("ann");
		var lee = await SignInAsync("lead");
		Assert.True((await ann.PutAsJsonAsync($"/api/templates/{name}/draft", new { project = new { }, html = "<p>Notice</p>", css = "" })).IsSuccessStatusCode);
		Assert.True((await lee.PutAsJsonAsync($"/api/templates/{name}/draft?lang=es", new { project = new { }, html = "<p>Aviso</p>", css = "" })).IsSuccessStatusCode);

		// Lee saved Spanish v1 last, but Ann saved English v1: Lee may publish English v1.
		Assert.Equal(HttpStatusCode.OK, (await lee.PostAsync($"/api/templates/{name}/versions/1/publish", null)).StatusCode);
		// Lee saved Spanish v1: someone else must publish it.
		var own = await lee.PostAsync($"/api/templates/{name}/versions/1/publish?lang=es", null);
		Assert.Equal(HttpStatusCode.Forbidden, own.StatusCode);
	}
}
