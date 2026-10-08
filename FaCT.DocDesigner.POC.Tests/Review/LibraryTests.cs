using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FaCT.DocDesigner.POC.Review;
using FaCT.DocDesigner.POC.Tests.Clauses;
using PuppeteerSharp;

namespace FaCT.DocDesigner.POC.Tests.Review;

/// <summary>A GhostDraft review case with its forms.csv row and its gd2designer conversion, in temp folders.</summary>
internal sealed class LibraryFolder : IDisposable
{
	public const string Header = "\"LOB\",\"Package\",\"PackageVersion\",\"GhostDraftForm\",\"FormCode\",\"Edition\"";

	public LibraryFolder()
	{
		Root = Directory.CreateTempSubdirectory("review-library-").FullName;
		Directory.CreateDirectory(Cases);
		Directory.CreateDirectory(Batch);
		File.WriteAllText(FormsCsv, Header + "\n");
	}

	public string Root { get; }
	public string Cases => Path.Combine(Root, "html-snapshots");
	public string Batch => Path.Combine(Root, "batch");
	public string FormsCsv => Path.Combine(Root, "forms.csv");
	public string Decisions => Path.Combine(Root, "decisions.json");

	public GhostDraftConversions Conversions() => new(FormsCsv, Batch);

	/// <summary>A case folder (as the golden-snapshot tests write it).</summary>
	public void AddCase(string caseName)
	{
		Directory.CreateDirectory(Path.Combine(Cases, caseName));
		File.WriteAllText(Path.Combine(Cases, caseName, "Diff.txt"), caseName + ": MATCH  score 99.0%\npages golden 1  html 1\nwords missing 0  extra 0");
	}

	/// <summary>The forms.csv row and the conversion of a GhostDraft form; returns the conversion's path.</summary>
	public string AddForm(string formCode, string edition, string gdForm, string fileName, string? json = null)
	{
		File.AppendAllText(FormsCsv, $"\"CPP,FRM\",\"ISO Commercial Auto Project\",\"2512.0\",\"{gdForm}\",\"{formCode}\",\"{edition}\"\n");
		var path = Path.Combine(Batch, fileName);
		File.WriteAllText(path, json ?? Conversion(fileName[..^5], gdForm));
		return path;
	}

	public static string Conversion(string name, string gdForm, string text = "Converted endorsement") => JsonSerializer.Serialize(new
	{
		name,
		title = gdForm,
		source = @"C:\gd\Templates\" + gdForm + ".gd",
		components = new object[] { new { type = "text", tagName = "p", content = text } },
		css = "",
		model = new { policy = new { number = "CPP1234567" } },
		report = new { counts = new { paragraphs = 1 }, notes = Array.Empty<string>() }
	});

	public void Dispose()
	{
		try { Directory.Delete(Root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
	}
}

/// <summary>Finding the designer conversion of a GhostDraft review case.</summary>
public sealed class GhostDraftConversionTests : IDisposable
{
	private readonly LibraryFolder _folder = new();

	public void Dispose() => _folder.Dispose();

	[Fact]
	public void A_case_finds_its_conversion_through_forms_csv_and_the_source_it_records()
	{
		_folder.AddForm("CA0101", "1120", "CA 01 01 11 20 Other Form", "CA-01-01-11-20-Other-Form.json");
		var path = _folder.AddForm("CA0301", "1013", "CA 03 01 10 13 Deductible Liability Coverage", "CA-03-01-10-13-Deductible-Liability-Coverage.json");
		Assert.Equal(path, _folder.Conversions().PathFor("CA0301_1013"));
		// cases with a suffix (several data sets for one form) use the same conversion
		Assert.Equal(path, _folder.Conversions().PathFor("CA0301_1013_scenario2"));
	}

	[Theory]
	[InlineData("CA0301")]
	[InlineData("CA0399_1013")]
	[InlineData("")]
	public void Cases_without_a_conversion_find_nothing(string caseName)
	{
		_folder.AddForm("CA0301", "1013", "CA 03 01 10 13 Deductible", "CA-03-01.json");
		Assert.Null(_folder.Conversions().PathFor(caseName));
	}

	[Fact]
	public void A_form_whose_conversion_is_missing_finds_nothing()
	{
		_folder.AddForm("CA0301", "1013", "CA 03 01 10 13 Deductible", "CA-03-01.json", LibraryFolder.Conversion("x", "Some other form"));
		File.WriteAllText(Path.Combine(_folder.Batch, "broken.json"), "{ not json");
		File.WriteAllText(Path.Combine(_folder.Batch, "list.json"), "[1, 2]");
		Assert.Null(_folder.Conversions().PathFor("CA0301_1013"));
	}

	[Fact]
	public void Missing_folders_find_nothing()
	{
		Assert.Null(new GhostDraftConversions(Path.Combine(_folder.Root, "none.csv"), _folder.Batch).PathFor("CA0301_1013"));
		Assert.Null(new GhostDraftConversions(_folder.FormsCsv, Path.Combine(_folder.Root, "none")).PathFor("CA0301_1013"));
	}

	[Theory]
	[InlineData("a,b,c", new[] { "a", "b", "c" })]
	[InlineData("\"CPP,FRM\",x", new[] { "CPP,FRM", "x" })]
	[InlineData("\"say \"\"hi\"\"\",", new[] { "say \"hi\"", "" })]
	[InlineData("", new[] { "" })]
	public void Csv_lines_are_split_with_quotes(string line, string[] expected) =>
		Assert.Equal(expected, GhostDraftConversions.SplitCsvLine(line));
}

/// <summary>GET /api/review/cases/{name}/designer.</summary>
public sealed class LibraryEndpointTests : IClassFixture<ReviewAppFactory>
{
	private readonly ReviewAppFactory _factory;
	private readonly HttpClient _client;

	public LibraryEndpointTests(ReviewAppFactory factory)
	{
		_factory = factory;
		_client = factory.CreateClient();
		var root = factory.GhostDraft.Root;
		var cases = Path.Combine(root, "html-snapshots");
		foreach (var name in new[] { "CA0301_1013", "CA0999_1013" })
		{
			Directory.CreateDirectory(Path.Combine(cases, name));
			File.WriteAllText(Path.Combine(cases, name, "Diff.txt"), name + ": MATCH  score 99.0%\npages golden 1  html 1\nwords missing 0  extra 0");
		}
		Directory.CreateDirectory(Path.Combine(root, "batch"));
		File.WriteAllText(Path.Combine(root, "forms.csv"), LibraryFolder.Header + "\n" +
			"\"CPP,FRM\",\"ISO\",\"1\",\"CA 03 01 10 13 Deductible Liability Coverage\",\"CA0301\",\"1013\"\n");
		File.WriteAllText(Path.Combine(root, "batch", "CA-03-01-10-13-Deductible-Liability-Coverage.json"),
			LibraryFolder.Conversion("CA-03-01-10-13-Deductible-Liability-Coverage", "CA 03 01 10 13 Deductible Liability Coverage"));
	}

	[Fact]
	public async Task A_case_returns_its_conversion()
	{
		var response = await _client.GetAsync("/api/review/cases/CA0301_1013/designer");
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
		var json = await response.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal("CA-03-01-10-13-Deductible-Liability-Coverage", json.GetProperty("name").GetString());
		Assert.Equal(1, json.GetProperty("components").GetArrayLength());
	}

	[Fact]
	public async Task A_case_without_a_conversion_says_so()
	{
		var response = await _client.GetAsync("/api/review/cases/CA0999_1013/designer");
		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
		Assert.Contains("No designer conversion", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
	}

	[Theory]
	[InlineData("nope")]
	[InlineData("..")]
	[InlineData("ca0301_1013")]
	public async Task Unknown_cases_are_not_found(string name)
	{
		var response = await _client.GetAsync($"/api/review/cases/{Uri.EscapeDataString(name)}/designer");
		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
	}

	[Fact]
	public async Task The_review_page_has_the_button()
	{
		var html = await _client.GetStringAsync("/review.html");
		Assert.Contains("id=\"btnAddToLibrary\"", html);
		Assert.Contains("Add to Designer Library", html);
	}
}

/// <summary>A designer whose GhostDraft review reads temp folders with one case and its conversion.</summary>
public sealed class LibraryDesignerFixture : IsolatedDesignerFixture
{
	internal LibraryFolder Library { get; } = new();

	public LibraryDesignerFixture()
	{
		Library.AddCase("CA0301_1013");
		Library.AddForm("CA0301", "1013", "CA 03 01 10 13 Deductible Liability Coverage", "CA-03-01-10-13-Deductible-Liability-Coverage.json");
	}

	protected override IEnumerable<string> Settings =>
	[
		$"--GhostDraftReview:OutputRoot={Library.Root}",
		$"--GhostDraftReview:DecisionsPath={Library.Decisions}",
		$"--GhostDraftReview:FormsCsv={Library.FormsCsv}",
		$"--GhostDraftReview:TemplatesRoot={Library.Batch}"
	];
}

[CollectionDefinition("Library designer")]
public sealed class LibraryDesignerCollection : ICollectionFixture<LibraryDesignerFixture>;

/// <summary>Add to Designer Library: from the review page to a saved draft template.</summary>
[Collection("Library designer")]
public sealed class LibraryDesignerTests(LibraryDesignerFixture fixture)
{
	private const string Name = "CA-03-01-10-13-Deductible-Liability-Coverage";

	private Task WaitAsync(IPage page, string predicate) =>
		page.WaitForFunctionAsync(predicate, new WaitForFunctionOptions { Timeout = 60_000 });

	private static Task<T> EvalAsync<T>(IPage page, string script) => page.EvaluateExpressionAsync<T>(script);

	[Fact]
	public async Task The_button_opens_the_designer_which_saves_the_form_as_a_draft_template()
	{
		var review = fixture.Page;
		await review.GoToAsync(new Uri(fixture.BaseUri, "review.html").ToString(), WaitUntilNavigation.Networkidle0);
		await WaitAsync(review, "() => document.getElementById('caseName').textContent === 'CA0301_1013'");
		await WaitAsync(review, "() => document.getElementById('btnAddToLibrary').dataset.flags === 'applied' && !document.getElementById('btnAddToLibrary').hidden");

		// the tab the review page opens (matched by opener too: the designer drops ?addToLibrary from its address at once)
		var opened = review.Browser.WaitForTargetAsync(t => t.Url.Contains("addToLibrary=CA0301_1013", StringComparison.Ordinal)
				|| (t.Type == TargetType.Page && t.Opener is { } opener && ReferenceEquals(opener, review.Target)),
			new WaitForOptions { Timeout = 30_000 });
		await review.EvaluateExpressionAsync("document.getElementById('btnAddToLibrary').click()");
		var designer = await (await opened).PageAsync();
		try
		{
			await WaitAsync(designer, "() => document.getElementById('status') && document.getElementById('status').textContent.startsWith('Added ')");
			var status = await EvalAsync<string>(designer, "document.getElementById('status').textContent");
			Assert.Equal($"Added CA0301_1013 to the library as template \"{Name}\": Saved draft v1. Check it with Preview PDF, then Publish.", status);
			Assert.Equal(Name, await EvalAsync<string>(designer, "document.getElementById('templateName').value"));
			Assert.Contains("Converted endorsement", await EvalAsync<string>(designer, "grapesjs.editors[0].getHtml()"));
			// the address no longer adds it again on reload
			Assert.DoesNotContain("addToLibrary", designer.Url);

			var saved = await fixture.Http.GetFromJsonAsync<JsonElement>($"api/templates/{Name}/versions/1");
			Assert.Equal("Draft", saved.GetProperty("status").GetString());
			Assert.Contains("Converted endorsement", saved.GetProperty("html").GetString());
			Assert.Equal("CPP1234567", saved.GetProperty("model").GetProperty("policy").GetProperty("number").GetString());
		}
		finally
		{
			await designer.CloseAsync();
		}
	}

	[Fact]
	public async Task A_case_without_a_conversion_shows_why()
	{
		fixture.Library.AddCase("CA0999_1013");
		var page = await fixture.Page.Browser.NewPageAsync();
		try
		{
			await page.GoToAsync(new Uri(fixture.BaseUri, "?addToLibrary=CA0999_1013").ToString(), WaitUntilNavigation.Networkidle0);
			await WaitAsync(page, "() => document.getElementById('status').textContent.includes('failed')");
			Assert.Contains("No designer conversion of 'CA0999_1013'", await EvalAsync<string>(page, "document.getElementById('status').textContent"));
		}
		finally
		{
			await page.CloseAsync();
		}
	}
}
