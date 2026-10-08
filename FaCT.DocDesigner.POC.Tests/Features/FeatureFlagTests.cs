using System.Net.Http.Json;
using System.Text.Json;
using FaCT.DocDesigner.POC.Features;
using FaCT.DocDesigner.POC.Tests.Clauses;
using Microsoft.Extensions.Configuration;
using PuppeteerSharp;

namespace FaCT.DocDesigner.POC.Tests.Features;

/// <summary>Which features are on: defaults, single flags, profiles.</summary>
public sealed class FeatureFlagTests
{
	private static FeatureFlags Flags(params (string Key, string Value)[] settings) =>
		new(new ConfigurationBuilder().AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value))).Build());

	[Fact]
	public void Every_feature_is_on_by_default()
	{
		var flags = Flags();
		Assert.Null(flags.Profile);
		Assert.All(flags.All(), f => Assert.True(f.Value, f.Key));
		Assert.Equal(FeatureFlags.Known.Count, flags.All().Count);
	}

	[Fact]
	public void A_feature_can_be_switched_off()
	{
		var flags = Flags(("Features:Languages", "false"), ("Features:Themes", "False"));
		Assert.False(flags.IsEnabled("Languages"));
		Assert.False(flags.IsEnabled("Themes"));
		Assert.True(flags.IsEnabled("Gallery"));
	}

	[Fact]
	public void A_profile_switches_its_set_and_a_direct_flag_wins()
	{
		var flags = Flags(("Features:Profile", "demo"), ("FeatureProfiles:demo:Languages", "false"), ("FeatureProfiles:demo:Export", "false"),
			("Features:Export", "true"), ("FeatureProfiles:other:Gallery", "false"));
		Assert.Equal("demo", flags.Profile);
		Assert.False(flags.IsEnabled("Languages"));
		Assert.True(flags.IsEnabled("Export"));
		Assert.True(flags.IsEnabled("Gallery"));
	}

	[Theory]
	[InlineData("no")]
	[InlineData("0")]
	[InlineData("")]
	public void Values_that_are_not_true_or_false_leave_the_feature_on(string value) =>
		Assert.True(Flags(("Features:Languages", value)).IsEnabled("Languages"));

	[Fact]
	public void An_unknown_profile_changes_nothing() =>
		Assert.All(Flags(("Features:Profile", "nope")).All(), f => Assert.True(f.Value));

	[Fact]
	public void The_shipped_demo_profile_keeps_the_core_features_and_hides_some()
	{
		// The demo set is tuned by hand before a demo: only check its shape, not which advanced features it hides.
		var settings = new ConfigurationBuilder()
			.AddJsonFile(Path.Combine(GoldenCases.RepoRoot, "FaCT.DocDesigner.POC", "appsettings.json"))
			.AddInMemoryCollection([new("Features:Profile", "demo")])
			.Build();
		var flags = new FeatureFlags(settings).All();
		Assert.Contains(flags, f => !f.Value);
		foreach (var on in new[] { "Gallery", "Scenarios", "ImportDocument", "CompareWithPublished" })
		{
			Assert.True(flags[on], on);
		}
		// every name in the profile is a known feature
		foreach (var name in settings.GetSection("FeatureProfiles:demo").GetChildren().Select(c => c.Key))
		{
			Assert.Contains(FeatureFlags.Known, f => f.Name == name);
		}
		// without the profile the same file keeps everything on
		Assert.All(new FeatureFlags(new ConfigurationBuilder()
			.AddJsonFile(Path.Combine(GoldenCases.RepoRoot, "FaCT.DocDesigner.POC", "appsettings.json")).Build()).All(), f => Assert.True(f.Value, f.Key));
	}

	[Fact]
	public void Every_flagged_control_in_the_designer_names_a_known_feature()
	{
		var html = File.ReadAllText(Path.Combine(GoldenCases.RepoRoot, "FaCT.DocDesigner.POC", "wwwroot", "index.html"));
		var used = System.Text.RegularExpressions.Regex.Matches(html, "data-feature=\"([A-Za-z]+)\"").Select(m => m.Groups[1].Value).Distinct().ToList();
		Assert.NotEmpty(used);
		Assert.All(used, name => Assert.Contains(FeatureFlags.Known, f => f.Name == name));
	}
}

/// <summary>GET /api/features.</summary>
public sealed class FeatureEndpointTests(ClauseAppFactory factory) : IClassFixture<ClauseAppFactory>
{
	[Fact]
	public async Task The_designer_reads_every_feature()
	{
		var json = await factory.CreateClient().GetFromJsonAsync<JsonElement>("/api/features");
		Assert.Equal(JsonValueKind.Null, json.GetProperty("profile").ValueKind);
		var features = json.GetProperty("features");
		Assert.Equal(FeatureFlags.Known.Select(f => f.Name), features.EnumerateObject().Select(p => p.Name));
		Assert.All(features.EnumerateObject(), p => Assert.True(p.Value.GetBoolean(), p.Name));
	}
}

/// <summary>A designer with a fixed set of features off (not the demo profile, which is tuned by hand).</summary>
public sealed class DemoDesignerFixture : IsolatedDesignerFixture
{
	private static readonly string[] Off =
	[
		"Languages", "Themes", "Watermarks", "ConditionalStyling", "Visuals", "CalculatedFields", "CustomBlocks", "FieldUsage",
		"Export", "LiquidView", "ImportGhostDraft", "ImportLegacy", "MaterialBlocks", "Clauses", "StylePanels", "ReviewToLibrary"
	];

	protected override IEnumerable<string> Settings => ["--Features:Profile=test", .. Off.Select(f => $"--Features:{f}=false")];
}

[CollectionDefinition("Demo designer")]
public sealed class DemoDesignerCollection : ICollectionFixture<DemoDesignerFixture>;

/// <summary>What the designer hides when features are off.</summary>
[Collection("Demo designer")]
public sealed class FeatureFlagDesignerTests(DemoDesignerFixture fixture)
{
	private IPage Page => fixture.Page;

	private Task<T> EvalAsync<T>(string script) => Page.EvaluateExpressionAsync<T>(script);

	private Task<bool> HiddenAsync(string selector) =>
		EvalAsync<bool>($"(() => {{ const el = document.querySelector('{selector}'); return !!el && el.classList.contains('feature-off') && getComputedStyle(el).display === 'none'; }})()");

	[Theory]
	[InlineData("#btnLanguages")]
	[InlineData("#btnTheme")]
	[InlineData("#btnWatermark")]
	[InlineData("#btnCondStyle")]
	[InlineData("#btnExportDocx")]
	[InlineData("#btnExportHtml")]
	[InlineData("#btnExportHtmlData")]
	[InlineData("#btnLiquid")]
	[InlineData("#btnSaveBlock")]
	[InlineData("#btnManageBlocks")]
	[InlineData("#btnImportGhostDraft")]
	[InlineData("#btnImportLegacy")]
	[InlineData("#btnFieldUsage")]
	[InlineData("#fieldMode option[value=liquid]")]
	[InlineData("label[data-feature=Languages]")]
	[InlineData("label[data-feature=Clauses]")]
	[InlineData("[data-tab=tabStyles]")]
	[InlineData("[data-tab=tabLayers]")]
	public async Task Controls_of_features_that_are_off_are_hidden(string selector) =>
		Assert.True(await HiddenAsync(selector), selector + " is visible");

	[Theory]
	[InlineData("#btnGallery")]
	[InlineData("#btnImportDocument")]
	[InlineData("#btnPageSetup")]
	[InlineData("#btnScenarios")]
	[InlineData("#btnDiff")]
	[InlineData("#btnFind")]
	[InlineData("#btnSpelling")]
	[InlineData("#btnHistory")]
	[InlineData("#btnDuplicate")]
	[InlineData("[data-tab=tabComments]")]
	public async Task Controls_of_features_that_are_on_stay(string selector) =>
		Assert.False(await HiddenAsync(selector), selector + " is hidden");

	[Fact]
	public async Task Blocks_of_features_that_are_off_are_removed()
	{
		var blocks = await EvalAsync<string[]>("grapesjs.editors[0].Blocks.getAll().map(b => b.getId())");
		foreach (var gone in new[] { "clause", "calc-field", "barcode", "chart", "signature", "md-icon", "md-card-filled" })
		{
			Assert.DoesNotContain(gone, blocks);
		}
		foreach (var kept in new[] { "text", "heading", "data-table", "repeat", "show-if", "page-break" })
		{
			Assert.Contains(kept, blocks);
		}
	}

	[Fact]
	public async Task The_review_page_hides_Add_to_Designer_Library_when_it_is_off()
	{
		var page = await Page.Browser.NewPageAsync();
		try
		{
			await page.GoToAsync(new Uri(fixture.BaseUri, "review.html").ToString(), WaitUntilNavigation.DOMContentLoaded);
			// the page asks for the flags, then leaves the button hidden
			await page.WaitForFunctionAsync("() => document.getElementById('btnAddToLibrary').dataset.flags === 'applied'", new WaitForFunctionOptions { Timeout = 30_000 });
			Assert.True(await page.EvaluateExpressionAsync<bool>("document.getElementById('btnAddToLibrary').hidden"));
		}
		finally
		{
			await page.CloseAsync();
		}
	}

	[Fact]
	public async Task The_server_reports_the_profile()
	{
		var json = await fixture.Http.GetFromJsonAsync<JsonElement>("api/features");
		Assert.Equal("test", json.GetProperty("profile").GetString());
		Assert.False(json.GetProperty("features").GetProperty("Clauses").GetBoolean());
		Assert.True(json.GetProperty("features").GetProperty("Gallery").GetBoolean());
	}

	[Fact]
	public async Task The_gallery_shows_templates_only_when_clauses_are_off()
	{
		(await fixture.Http.PutAsJsonAsync("api/templates/flag-template/draft", new { project = new { }, html = "<p>t</p>", css = "" })).EnsureSuccessStatusCode();
		(await fixture.Http.PutAsJsonAsync("api/clauses/flag-clause/draft", new { project = new { }, html = "<p>c</p>", css = "" })).EnsureSuccessStatusCode();
		await Page.EvaluateExpressionAsync("document.getElementById('btnGallery').click()");
		await Page.WaitForFunctionAsync("() => !!document.querySelector('.gallery-card[data-doc=\"templates/flag-template\"]')", new WaitForFunctionOptions { Timeout = 30_000 });
		Assert.Equal(0, await EvalAsync<int>("document.querySelectorAll('.gallery-card[data-doc^=\"clauses/\"]').length"));
		Assert.True(await HiddenAsync("#galleryKind"));
		await Page.EvaluateExpressionAsync("grapesjs.editors[0].Modal.close()");
	}
}
