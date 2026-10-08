using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using FaCT.DocDesigner.POC.Templates;
using PuppeteerSharp;

namespace FaCT.DocDesigner.POC.Tests.Help;

/// <summary>Where the User Guide lives in the repo.</summary>
internal static class UserGuidePaths
{
	public static string Project => Path.Combine(GoldenCases.RepoRoot, "FaCT.DocDesigner.POC");
	public static string Content => Path.Combine(Project, "Help", "user-guide.json");
	public static string Templates => Path.Combine(Project, "App_Data", "templates");
	public static string Checklist => Path.Combine(GoldenCases.RepoRoot, "DESIGNER-CHECKLIST.md");
	public static string OutputPdf => Path.Combine(GoldenCases.RepoRoot, "output", "user-guide", "MOE-Document-Designer-User-Guide.pdf");
	public const string TemplateName = "user-guide";
}

/// <summary>
/// Builds the "user-guide" template WITH the designer: drives the designer UI (model from Help/user-guide.json, the
/// layout below built from the designer's own Repeat / Show If / Data Field components), saves and publishes it, then
/// copies the published version into App_Data/templates as a new version and writes the PDF to output/user-guide.
/// Opt-in: set BUILD_USER_GUIDE=1. After that, change the layout in the designer (open template "user-guide") and the
/// content in Help/user-guide.json; this builder is only needed to recreate the layout from scratch.
/// </summary>
[Collection("Isolated designer")]
public sealed class UserGuideBuilder(IsolatedDesignerFixture fixture)
{
	private static JsonObject Field(string path, string format = "") => new() { ["type"] = "data-field", ["field"] = path, ["format"] = format };

	private static JsonObject Text(string tag, string[] classes, params JsonNode[] parts) => new()
	{
		["type"] = "text",
		["tagName"] = tag,
		["classes"] = new JsonArray(classes.Select(c => (JsonNode)c).ToArray()),
		["components"] = new JsonArray(parts)
	};

	private static JsonObject Words(string text) => new() { ["type"] = "textnode", ["content"] = text };

	private static JsonObject Box(string tag, string[] classes, params JsonNode[] children) => new()
	{
		["tagName"] = tag,
		["classes"] = new JsonArray(classes.Select(c => (JsonNode)c).ToArray()),
		["components"] = new JsonArray(children)
	};

	private static JsonObject Repeat(string list, string[] classes, params JsonNode[] children) => new()
	{
		["type"] = "repeat",
		["listPath"] = list,
		["classes"] = new JsonArray(classes.Select(c => (JsonNode)c).ToArray()),
		["components"] = new JsonArray(children)
	};

	private static JsonObject ShowIf(string field, params JsonNode[] children) => new()
	{
		["type"] = "conditional",
		["field"] = field,
		["operator"] = "present",
		["components"] = new JsonArray(children)
	};

	/// <summary>The guide's layout: cover, contents, then each chapter on a new page with its sections.</summary>
	internal static JsonArray Layout() =>
	[
		Box("div", ["doc-header"], new JsonObject { ["type"] = "brand-logo" }, Text("h1", [], Field("guide.title"))),
		Text("div", ["moe-leadin"], Field("guide.subtitle")),
		Text("p", ["moe-intro"], Field("guide.summary")),
		Text("p", ["guide-meta"], Words("Who it is for: "), Field("guide.audience")),
		Text("p", ["guide-meta"], Words("Edition: "), Field("guide.edition"), Words(" \u00b7 Updated "), Field("guide.updated", "shortdate")),
		Text("h2", [], Words("Contents")),
		Repeat("chapters", ["guide-toc"],
			Box("div", ["guide-toc-row"],
				Text("span", ["guide-toc-title"], Field("chapter.title")),
				Text("span", ["guide-toc-summary"], Field("chapter.summary")))),
		Repeat("chapters", ["guide-chapters"],
			new JsonObject { ["type"] = "page-break" },
			Text("h2", ["guide-chapter-title"], Field("chapter.title")),
			Text("p", ["moe-intro"], Field("chapter.intro")),
			Repeat("chapter.sections", ["guide-section"],
				Text("h3", [], Field("section.title")),
				Repeat("section.paragraphs", ["guide-paragraphs"], Text("p", [], Field("paragraph"))),
				ShowIf("section.steps",
					Box("div", ["moe-card", "moe-card--accent", "guide-howto"],
						Box("div", ["moe-card-title"], Text("span", [], Words("How to"))),
						Repeat("section.steps", ["guide-steps"], Text("div", ["guide-step"], Field("step"))))),
				ShowIf("section.tip",
					Text("div", ["moe-callout", "guide-tip"], Words("Tip: "), Field("section.tip"))),
				ShowIf("section.note",
					Text("div", ["guide-note"], Words("Note: "), Field("section.note")))))
	];

	internal const string Css =
		".guide-meta{margin:2px 0;color:#4f5b52;font-size:10pt;}" +
		".guide-toc{counter-reset:toc;margin-top:8px;}" +
		".guide-toc-row{display:flex;gap:12px;padding:6px 0;border-bottom:1px solid #C1C9BF;}" +
		".guide-toc-row::before{counter-increment:toc;content:counter(toc) \".\";font-weight:700;min-width:22px;color:#00573F;}" +
		".guide-toc-title{font-weight:600;min-width:210px;}" +
		".guide-toc-summary{color:#4f5b52;}" +
		".guide-chapters{counter-reset:chapter;}" +
		".guide-chapter-title::before{counter-increment:chapter;content:\"Chapter \" counter(chapter) \": \";}" +
		".guide-section{margin-bottom:10px;}" +
		".guide-section h3{margin:18px 0 6px;color:#00573F;}" +
		".guide-paragraphs p{margin:0 0 8px;line-height:1.45;}" +
		".guide-howto{margin:8px 0;break-inside:avoid;}" +
		".guide-steps{counter-reset:step;}" +
		".guide-step{display:flex;gap:8px;margin:4px 0;}" +
		".guide-step::before{counter-increment:step;content:counter(step) \".\";font-weight:700;min-width:18px;color:#00573F;}" +
		".guide-tip{margin:8px 0;}" +
		".guide-note{margin:8px 0;padding:8px 12px;border-left:4px solid #8C5A00;background:#FFF4E0;}";

	private IPage Page => fixture.Page;

	private async Task<string> ActAsync(string script)
	{
		await Page.EvaluateExpressionAsync("document.getElementById('status').textContent = ''");
		await Page.EvaluateExpressionAsync(script);
		await Page.WaitForFunctionAsync("() => { const t = document.getElementById('status').textContent; return t.length > 0 && !t.endsWith('...'); }",
			new WaitForFunctionOptions { Timeout = 60_000 });
		return await Page.EvaluateExpressionAsync<string>("document.getElementById('status').textContent");
	}

	[Fact]
	public async Task Build_the_user_guide_template_with_the_designer()
	{
		if (Environment.GetEnvironmentVariable("BUILD_USER_GUIDE") != "1") return;
		var content = JsonNode.Parse(await File.ReadAllTextAsync(UserGuidePaths.Content))!.AsObject();
		content.Remove("maintenance");

		// 1. In the designer: a new template named user-guide, its model = the guide content.
		await ActAsync("(() => { const k = document.getElementById('docKind'); k.value = 'templates'; k.dispatchEvent(new Event('change')); })()");
		await ActAsync($"(() => {{ const n = document.getElementById('templateName'); n.value = '{UserGuidePaths.TemplateName}'; n.dispatchEvent(new Event('change')); }})()");
		await Page.EvaluateExpressionAsync("document.getElementById('btnEditModel').click()");
		await Page.WaitForFunctionAsync("() => !!document.querySelector('.gjs-mdl-content textarea.model-editor')");
		await Page.EvaluateFunctionAsync("m => document.querySelector('.gjs-mdl-content textarea.model-editor').value = m", content.ToJsonString());
		await ActAsync("document.querySelector('.gjs-mdl-content .model-actions button.primary').click()");

		// 2. The layout, from the designer's own components, and its styles.
		await Page.EvaluateFunctionAsync("(layout, css) => { const e = grapesjs.editors[0]; e.setComponents(JSON.parse(layout)); e.setStyle(css); }",
			Layout().ToJsonString(), Css);
		// Bindings are validated on a short timer after changes.
		await Task.Delay(600);
		var problems = await Page.EvaluateExpressionAsync<string>("document.getElementById('modelProblems').textContent");
		Assert.True(string.IsNullOrWhiteSpace(problems), "Binding problems: " + problems);

		// 3. Save and publish through the toolbar.
		Assert.Equal("v1 is now published.", await ActAsync("document.getElementById('btnPublish').click()"));
		var built = await fixture.Http.GetFromJsonAsync<JsonElement>($"api/templates/{UserGuidePaths.TemplateName}/versions/1");

		// 4. Into the real template store as a new published version (unless nothing changed).
		var store = new TemplateStore(UserGuidePaths.Templates);
		var live = await store.GetPublishedAsync(UserGuidePaths.TemplateName);
		var html = built.GetProperty("html").GetString()!;
		var css = built.GetProperty("css").GetString()!;
		if (live is null || live.Html != html || live.Css != css)
		{
			var draft = await store.SaveDraftAsync(UserGuidePaths.TemplateName, built.GetProperty("project"), html, css, built.GetProperty("model"));
			await store.PublishAsync(UserGuidePaths.TemplateName, draft.Version);
		}

		// 5. The PDF, for a quick look.
		using var response = await fixture.Http.PostAsJsonAsync("api/render", new JsonObject { ["html"] = html, ["css"] = css, ["data"] = content });
		Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
		Directory.CreateDirectory(Path.GetDirectoryName(UserGuidePaths.OutputPdf)!);
		var pdf = await response.Content.ReadAsByteArrayAsync();
		await File.WriteAllBytesAsync(UserGuidePaths.OutputPdf, pdf);
		// Ink previews of the pages (black and white) for a quick look without a PDF viewer.
		var pages = Import.PageImages.Render(pdf);
		for (var i = 0; i < pages.Count; i++)
		{
			await File.WriteAllBytesAsync(Path.Combine(Path.GetDirectoryName(UserGuidePaths.OutputPdf)!, $"page-{i + 1}.png"),
				Import.PageImages.OverlayPng(pages[i], pages[i]));
		}
	}
}
