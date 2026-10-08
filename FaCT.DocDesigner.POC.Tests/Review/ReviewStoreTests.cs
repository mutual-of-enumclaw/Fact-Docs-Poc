using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FaCT.DocDesigner.POC.Review;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace FaCT.DocDesigner.POC.Tests.Review;

/// <summary>Builds review case folders in the layout the review page reads.</summary>
internal sealed class ReviewFolder : IDisposable
{
	public ReviewFolder()
	{
		Root = Directory.CreateTempSubdirectory("review-store-").FullName;
		Cases = Path.Combine(Root, "cases");
		Directory.CreateDirectory(Cases);
	}

	public string Root { get; }
	public string Cases { get; }
	public string Decisions => Path.Combine(Root, "decisions.json");

	public ReviewStore Store() => new(new ReviewSource("import", Cases, n => Path.Combine(Cases, n, "Original.pdf"), Decisions));

	public string AddCase(string name, double score, int overlays = 0, bool original = true, string text = "rendered text")
	{
		var folder = Path.Combine(Cases, name);
		Directory.CreateDirectory(folder);
		File.WriteAllText(Path.Combine(folder, "Diff.txt"), FormattableString.Invariant(
			$"{name}: DIFFERENT  score {score * 100:0.0}%\npages golden 2  html 3\nwords golden 100  html 90  missing 12  extra 2\n\ndetails"));
		File.WriteAllBytes(Path.Combine(folder, "Html.pdf"), "%PDF-1.7 render"u8.ToArray());
		if (original) File.WriteAllBytes(Path.Combine(folder, "Original.pdf"), "%PDF-1.7 original"u8.ToArray());
		File.WriteAllText(Path.Combine(folder, "Template.html"), "<p id=\"iabcde\">x</p>");
		File.WriteAllText(Path.Combine(folder, "HtmlSnapshot.txt"), text);
		for (var i = 1; i <= overlays; i++) File.WriteAllBytes(Path.Combine(folder, $"Overlay-p{i}.png"), [0x89, 0x50]);
		return folder;
	}

	public void Dispose()
	{
		try { Directory.Delete(Root, recursive: true); }
		catch (IOException) { /* best effort */ }
	}
}

public sealed class ReviewStoreTests : IDisposable
{
	private readonly ReviewFolder _folder = new();

	public void Dispose() => _folder.Dispose();

	[Fact]
	public async Task Cases_are_listed_lowest_score_first_with_their_diff_head()
	{
		_folder.AddCase("b-case", 0.85, overlays: 2);
		_folder.AddCase("a-case", 0.40, original: false);
		Directory.CreateDirectory(Path.Combine(_folder.Cases, "no-diff"));

		var cases = await _folder.Store().ListAsync();

		Assert.Equal(["a-case", "b-case"], cases.Select(c => c.Case));
		var b = cases[1];
		Assert.Equal(0.85, b.Score);
		Assert.False(b.Match);
		Assert.Equal(2, b.GoldenPages);
		Assert.Equal(3, b.HtmlPages);
		Assert.Equal(12, b.Missing);
		Assert.Equal(2, b.Extra);
		Assert.True(b.HasGoldenPdf);
		Assert.True(b.HasHtmlPdf);
		Assert.Equal(2, b.Overlays);
		Assert.False(cases[0].HasGoldenPdf);
		Assert.Equal(0, cases[0].Overlays);
	}

	[Fact]
	public async Task A_missing_folder_lists_nothing()
	{
		var store = new ReviewStore(new ReviewSource("import", Path.Combine(_folder.Root, "absent"), n => n, _folder.Decisions));
		Assert.Empty(await store.ListAsync());
	}

	[Theory]
	[InlineData("golden.pdf", "Original.pdf", "application/pdf")]
	[InlineData("html.pdf", "Html.pdf", "application/pdf")]
	[InlineData("diff", "Diff.txt", "text/plain; charset=utf-8")]
	[InlineData("overlay-1.png", "Overlay-p1.png", "image/png")]
	[InlineData("overlay-2.png", "Overlay-p2.png", "image/png")]
	public void Known_artifacts_are_served(string artifact, string file, string contentType)
	{
		var folder = _folder.AddCase("c", 0.5, overlays: 2);
		var found = _folder.Store().Artifact("c", artifact);
		Assert.NotNull(found);
		Assert.Equal(Path.Combine(folder, file), found.Value.Path);
		Assert.Equal(contentType, found.Value.ContentType);
	}

	[Theory]
	[InlineData("c", "overlay-3.png")]
	[InlineData("c", "overlay-0.png")]
	[InlineData("c", "overlay-21.png")]
	[InlineData("c", "overlay-01.png")]
	[InlineData("c", "../Diff.txt")]
	[InlineData("c", "Template.html")]
	[InlineData("..", "diff")]
	[InlineData("missing", "diff")]
	[InlineData("C", "diff")]
	public void Unknown_cases_and_artifacts_are_not_served(string caseName, string artifact)
	{
		_folder.AddCase("c", 0.5, overlays: 2);
		Assert.Null(_folder.Store().Artifact(caseName, artifact));
	}

	[Fact]
	public async Task Decisions_are_saved_pinned_to_the_render_and_go_stale_when_it_changes()
	{
		var folder = _folder.AddCase("c", 0.6);
		var store = _folder.Store();

		var decision = await store.DecideAsync("c", new ReviewRequest(ReviewStatus.Approved, "Pat", "Logo is a vector; fine."));

		Assert.NotNull(decision);
		Assert.Equal(ReviewStatus.Approved, decision.Status);
		Assert.Equal("Pat", decision.Reviewer);
		Assert.Equal(0.6, decision.Score);
		Assert.NotEmpty(decision.Fingerprint);
		Assert.Contains("\"c\"", await File.ReadAllTextAsync(_folder.Decisions));

		var listed = Assert.Single(await store.ListAsync());
		Assert.Equal(ReviewStatus.Approved, listed.Decision!.Status);
		Assert.False(listed.Stale);

		await File.WriteAllTextAsync(Path.Combine(folder, "HtmlSnapshot.txt"), "a different render");
		Assert.True(Assert.Single(await store.ListAsync()).Stale);
	}

	[Fact]
	public async Task Random_editor_ids_do_not_make_a_decision_stale()
	{
		var folder = _folder.AddCase("c", 0.6);
		var store = _folder.Store();
		await store.DecideAsync("c", new ReviewRequest(ReviewStatus.Rejected, "Pat", "wrong"));

		await File.WriteAllTextAsync(Path.Combine(folder, "Template.html"), "<p id=\"izzzzz\">x</p>");

		Assert.False(Assert.Single(await store.ListAsync()).Stale);
	}

	[Fact]
	public async Task Decisions_can_be_cleared()
	{
		_folder.AddCase("c", 0.6);
		var store = _folder.Store();
		await store.DecideAsync("c", new ReviewRequest(ReviewStatus.Rejected, null, "x"));

		Assert.True(await store.ClearAsync("c"));
		Assert.False(await store.ClearAsync("c"));
		Assert.Null(Assert.Single(await store.ListAsync()).Decision);
	}

	[Fact]
	public async Task Unknown_cases_cannot_be_decided()
	{
		Assert.Null(await _folder.Store().DecideAsync("nope", new ReviewRequest(ReviewStatus.Approved, null, null)));
		Assert.False(File.Exists(_folder.Decisions));
	}

	[Fact]
	public async Task Long_reviewer_names_and_notes_are_clipped()
	{
		_folder.AddCase("c", 0.6);
		var decision = await _folder.Store().DecideAsync("c", new ReviewRequest(ReviewStatus.Approved, new string('r', 300), new string('n', 5000)));
		Assert.Equal(100, decision!.Reviewer.Length);
		Assert.Equal(2000, decision.Note.Length);
	}
}

/// <summary>The designer with review sources pointed at temp folders.</summary>
public sealed class ReviewAppFactory : WebApplicationFactory<Program>
{
	internal ReviewFolder Imports { get; } = new();
	internal ReviewFolder GhostDraft { get; } = new();

	protected override void ConfigureWebHost(IWebHostBuilder builder)
	{
		builder.UseEnvironment("Development");
		builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
		{
			["ImportReview:OutputRoot"] = Imports.Cases,
			["ImportReview:DecisionsPath"] = Imports.Decisions,
			["GhostDraftReview:OutputRoot"] = GhostDraft.Root,
			["GhostDraftReview:DecisionsPath"] = GhostDraft.Decisions,
			["GhostDraftReview:FormsCsv"] = Path.Combine(GhostDraft.Root, "forms.csv")
		}));
	}

	protected override void Dispose(bool disposing)
	{
		base.Dispose(disposing);
		if (disposing)
		{
			Imports.Dispose();
			GhostDraft.Dispose();
		}
	}
}

public sealed class ReviewEndpointTests : IClassFixture<ReviewAppFactory>
{
	private readonly ReviewAppFactory _factory;
	private readonly HttpClient _client;

	public ReviewEndpointTests(ReviewAppFactory factory)
	{
		_factory = factory;
		_client = factory.CreateClient();
		factory.Imports.AddCase("import-case", 0.42, overlays: 1);
		// GhostDraft cases live in {root}/html-snapshots with golden PDFs in {root}/serverxml/{case}/GhostDraft.pdf.
		var gdCases = Path.Combine(factory.GhostDraft.Root, "html-snapshots");
		Directory.CreateDirectory(Path.Combine(gdCases, "gd-case"));
		File.WriteAllText(Path.Combine(gdCases, "gd-case", "Diff.txt"), "gd-case: MATCH  score 97.5%\npages golden 1  html 1\nwords missing 0  extra 0");
	}

	[Fact]
	public async Task Without_a_source_the_ghostdraft_cases_are_listed()
	{
		var cases = await _client.GetFromJsonAsync<JsonElement>("/api/review/cases");
		Assert.Equal("gd-case", Assert.Single(cases.EnumerateArray()).GetProperty("case").GetString());
	}

	[Fact]
	public async Task The_import_source_lists_import_cases_with_overlays()
	{
		var cases = await _client.GetFromJsonAsync<JsonElement>("/api/review/cases?source=import");
		var c = Assert.Single(cases.EnumerateArray());
		Assert.Equal("import-case", c.GetProperty("case").GetString());
		Assert.Equal(0.42, c.GetProperty("score").GetDouble());
		Assert.Equal(1, c.GetProperty("overlays").GetInt32());
		Assert.True(c.GetProperty("hasGoldenPdf").GetBoolean());
	}

	[Theory]
	[InlineData("golden.pdf", "application/pdf")]
	[InlineData("html.pdf", "application/pdf")]
	[InlineData("diff", "text/plain")]
	[InlineData("overlay-1.png", "image/png")]
	public async Task Import_artifacts_are_served_with_their_type(string artifact, string mediaType)
	{
		using var response = await _client.GetAsync($"/api/review/cases/import-case/{artifact}?source=import");
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		Assert.Equal(mediaType, response.Content.Headers.ContentType!.MediaType);
	}

	[Fact]
	public async Task Import_cases_are_not_visible_through_the_ghostdraft_source()
	{
		using var response = await _client.GetAsync("/api/review/cases/import-case/diff");
		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
	}

	[Theory]
	[InlineData("GET", "/api/review/cases?source=bogus")]
	[InlineData("GET", "/api/review/cases/import-case/diff?source=bogus")]
	[InlineData("DELETE", "/api/review/cases/import-case?source=bogus")]
	public async Task Unknown_sources_are_rejected(string method, string url)
	{
		using var response = await _client.SendAsync(new HttpRequestMessage(new HttpMethod(method), url));
		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
	}

	[Fact]
	public async Task Import_decisions_go_to_the_import_decisions_file()
	{
		using var put = await _client.PutAsJsonAsync("/api/review/cases/import-case?source=import",
			new { status = "Approved", reviewer = "Pat", note = "Vector logo; acceptable." });
		Assert.Equal(HttpStatusCode.OK, put.StatusCode);
		Assert.Contains("import-case", await File.ReadAllTextAsync(_factory.Imports.Decisions));
		Assert.False(File.Exists(_factory.GhostDraft.Decisions));

		using var delete = await _client.DeleteAsync("/api/review/cases/import-case?source=import");
		Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
	}

	[Fact]
	public void The_store_registry_maps_sources()
	{
		var stores = (ReviewStores)_factory.Services.GetService(typeof(ReviewStores))!;
		Assert.Same(stores.GhostDraft, stores.Get(null));
		Assert.Same(stores.GhostDraft, stores.Get(""));
		Assert.Same(stores.GhostDraft, stores.Get("ghostdraft"));
		Assert.Same(stores.Import, stores.Get("import"));
		Assert.Null(stores.Get("Import"));
		Assert.Equal("import", stores.Import.Name);
	}
}
