using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using FaCT.DocDesigner.POC.Import;
using FaCT.DocDesigner.POC.Rendering;
using FaCT.DocDesigner.POC.Templates;
using FaCT.DocDesigner.POC.Tests.Import;
using Microsoft.Extensions.DependencyInjection;

namespace FaCT.DocDesigner.POC.Tests.Help;

/// <summary>
/// The User Guide content (Help/user-guide.json): well formed for the template, and kept up to date with the checklist —
/// a finished feature that no section covers fails here.
/// </summary>
public sealed class UserGuideContentTests
{
	private static readonly JsonElement Guide = JsonDocument.Parse(File.ReadAllText(UserGuidePaths.Content)).RootElement.Clone();

	private static IEnumerable<JsonElement> Chapters => Guide.GetProperty("chapters").EnumerateArray();

	private static IEnumerable<JsonElement> Sections => Chapters.SelectMany(c => c.GetProperty("sections").EnumerateArray());

	private static IReadOnlyList<int> ChecklistItems(bool done) =>
		File.ReadAllLines(UserGuidePaths.Checklist)
			.Select(l => Regex.Match(l, done ? @"^- \[x\] (\d+)\." : @"^- \[ \] (\d+)\."))
			.Where(m => m.Success)
			.Select(m => int.Parse(m.Groups[1].Value))
			.ToList();

	[Fact]
	public void The_guide_has_a_title_page()
	{
		var guide = Guide.GetProperty("guide");
		foreach (var key in new[] { "title", "subtitle", "edition", "summary", "audience" })
		{
			Assert.False(string.IsNullOrWhiteSpace(guide.GetProperty(key).GetString()), key);
		}
		Assert.Matches(@"^\d{4}-\d{2}-\d{2}$", guide.GetProperty("updated").GetString());
	}

	[Fact]
	public void Every_chapter_has_a_title_summary_intro_and_sections()
	{
		Assert.True(Chapters.Count() >= 5);
		foreach (var chapter in Chapters)
		{
			var title = chapter.GetProperty("title").GetString();
			Assert.False(string.IsNullOrWhiteSpace(title));
			Assert.False(string.IsNullOrWhiteSpace(chapter.GetProperty("summary").GetString()), title);
			Assert.False(string.IsNullOrWhiteSpace(chapter.GetProperty("intro").GetString()), title);
			Assert.NotEmpty(chapter.GetProperty("sections").EnumerateArray());
		}
		Assert.Equal(Chapters.Count(), Chapters.Select(c => c.GetProperty("title").GetString()).Distinct().Count());
	}

	[Fact]
	public void Every_section_has_the_same_shape_so_the_template_binds_to_all_of_them()
	{
		foreach (var section in Sections)
		{
			var title = section.GetProperty("title").GetString();
			Assert.False(string.IsNullOrWhiteSpace(title));
			Assert.Equal(JsonValueKind.Array, section.GetProperty("paragraphs").ValueKind);
			Assert.NotEmpty(section.GetProperty("paragraphs").EnumerateArray());
			Assert.All(section.GetProperty("paragraphs").EnumerateArray(), p => Assert.False(string.IsNullOrWhiteSpace(p.GetString()), title));
			Assert.Equal(JsonValueKind.Array, section.GetProperty("steps").ValueKind);
			Assert.All(section.GetProperty("steps").EnumerateArray(), s => Assert.False(string.IsNullOrWhiteSpace(s.GetString()), title));
			Assert.Equal(JsonValueKind.String, section.GetProperty("tip").ValueKind);
			Assert.Equal(JsonValueKind.String, section.GetProperty("note").ValueKind);
			Assert.Equal(JsonValueKind.Array, section.GetProperty("covers").ValueKind);
		}
	}

	[Fact]
	public void Section_titles_are_unique_within_their_chapter()
	{
		foreach (var chapter in Chapters)
		{
			var titles = chapter.GetProperty("sections").EnumerateArray().Select(s => s.GetProperty("title").GetString()).ToList();
			Assert.Equal(titles.Count, titles.Distinct().Count());
		}
	}

	[Fact]
	public void Every_finished_checklist_item_is_covered_by_the_guide()
	{
		var covered = Sections.SelectMany(s => s.GetProperty("covers").EnumerateArray().Select(c => c.GetInt32())).ToHashSet();
		var internalItems = Guide.GetProperty("maintenance").GetProperty("internalChecklistItems").EnumerateArray().Select(i => i.GetInt32()).ToHashSet();
		var missing = ChecklistItems(done: true).Where(i => !covered.Contains(i) && !internalItems.Contains(i)).ToList();
		Assert.True(missing.Count == 0,
			"Checklist item(s) " + string.Join(", ", missing) + " are done but the User Guide doesn't cover them yet. " +
			"Add or update a section in FaCT.DocDesigner.POC/Help/user-guide.json and list the item in its \"covers\".");
	}

	[Fact]
	public void The_guide_only_documents_finished_features()
	{
		var done = ChecklistItems(done: true).ToHashSet();
		var early = Sections
			.SelectMany(s => s.GetProperty("covers").EnumerateArray().Select(c => (Item: c.GetInt32(), Section: s.GetProperty("title").GetString())))
			.Where(c => !done.Contains(c.Item))
			.ToList();
		Assert.True(early.Count == 0, "The guide covers unfinished checklist items: " + string.Join(", ", early.Select(e => $"{e.Item} ({e.Section})")));
	}

	[Fact]
	public void The_checklist_is_readable()
	{
		Assert.Contains(1, ChecklistItems(done: true));
		Assert.Contains(9, ChecklistItems(done: false));
	}

	[Fact]
	public void The_guide_explains_how_to_keep_it_up_to_date()
	{
		Assert.Contains("covers", Guide.GetProperty("maintenance").GetProperty("howToUpdate").GetString());
	}
}

/// <summary>The published user-guide template (made with the designer) rendering the guide content.</summary>
public sealed class UserGuideTemplateTests(ImportAppFactory factory) : IClassFixture<ImportAppFactory>
{
	private static readonly JsonElement Guide = JsonDocument.Parse(File.ReadAllText(UserGuidePaths.Content)).RootElement.Clone();

	private static TemplateVersion Published() =>
		new TemplateStore(UserGuidePaths.Templates).GetPublishedAsync(UserGuidePaths.TemplateName).GetAwaiter().GetResult()
		?? throw new InvalidOperationException("App_Data/templates/user-guide.json has no published version. Run the UserGuideBuilder (BUILD_USER_GUIDE=1).");

	private async Task<string> ComposeAsync()
	{
		var template = Published();
		var result = await factory.Services.GetRequiredService<DocumentComposer>().ComposeAsync(template.Html, template.Css, Guide);
		Assert.True(result.Error is null, result.Error);
		return System.Net.WebUtility.HtmlDecode(result.Html!);
	}

	[Fact]
	public void The_template_was_made_in_the_designer_and_reopens_there()
	{
		var template = Published();
		Assert.Equal(JsonValueKind.Object, template.Project.ValueKind);
		Assert.True(template.Project.GetProperty("pages").GetArrayLength() > 0);
		Assert.Contains("data-repeat", template.Html);
		Assert.Equal(JsonValueKind.Object, template.Model?.ValueKind);
	}

	[Fact]
	public void The_template_only_reads_fields_the_guide_content_has()
	{
		var known = new HashSet<string>(StringComparer.Ordinal);
		void Walk(JsonElement element, string path)
		{
			if (path.Length > 0) known.Add(path);
			if (element.ValueKind == JsonValueKind.Object)
			{
				foreach (var p in element.EnumerateObject()) Walk(p.Value, path.Length == 0 ? p.Name : path + "." + p.Name);
			}
			else if (element.ValueKind == JsonValueKind.Array)
			{
				foreach (var item in element.EnumerateArray()) Walk(item, path + "[]");
			}
		}
		Walk(Guide, "");
		// brand.* (logos) is supplied by the renderer, not the message.
		var unknown = FieldUsage.References(Published().Html).Select(r => r.Path)
			.Where(p => !known.Contains(p) && !p.StartsWith("brand.", StringComparison.Ordinal)).Distinct().ToList();
		Assert.True(unknown.Count == 0, "The template reads fields the guide content doesn't have: " + string.Join(", ", unknown));
	}

	[Fact]
	public async Task Every_chapter_section_paragraph_and_step_is_printed()
	{
		var html = await ComposeAsync();
		Assert.Contains(Guide.GetProperty("guide").GetProperty("title").GetString()!, html);
		foreach (var chapter in Guide.GetProperty("chapters").EnumerateArray())
		{
			Assert.Contains(chapter.GetProperty("title").GetString()!, html);
			Assert.Contains(chapter.GetProperty("intro").GetString()!, html);
			foreach (var section in chapter.GetProperty("sections").EnumerateArray())
			{
				Assert.Contains(section.GetProperty("title").GetString()!, html);
				foreach (var p in section.GetProperty("paragraphs").EnumerateArray()) Assert.Contains(p.GetString()!, html);
				foreach (var s in section.GetProperty("steps").EnumerateArray()) Assert.Contains(s.GetString()!, html);
			}
		}
	}

	[Fact]
	public async Task Tips_notes_and_how_to_boxes_appear_only_where_the_content_has_them()
	{
		var html = await ComposeAsync();
		var sections = Guide.GetProperty("chapters").EnumerateArray().SelectMany(c => c.GetProperty("sections").EnumerateArray()).ToList();
		Assert.Equal(sections.Count(s => s.GetProperty("tip").GetString()!.Length > 0), Regex.Matches(html, "class=\"[^\"]*guide-tip").Count);
		Assert.Equal(sections.Count(s => s.GetProperty("note").GetString()!.Length > 0), Regex.Matches(html, "class=\"[^\"]*guide-note").Count);
		Assert.Equal(sections.Count(s => s.GetProperty("steps").GetArrayLength() > 0), Regex.Matches(html, "class=\"[^\"]*guide-howto").Count);
	}

	[Fact]
	public async Task Liquid_written_in_the_guide_is_printed_not_run()
	{
		var html = await ComposeAsync();
		Assert.Contains("{% include 'name' %}", html);
		Assert.Contains("{{ policy.number }}", html);
	}
}

/// <summary>/api/help: the guide content and the guide PDF rendered from the published template.</summary>
public sealed class HelpEndpointTests(ImportAppFactory factory) : IClassFixture<ImportAppFactory>
{
	[Fact]
	public async Task The_guide_pdf_is_rendered_from_the_published_template()
	{
		using var response = await factory.CreateClient().GetAsync("/api/help/user-guide.pdf");
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
		Assert.StartsWith("inline", response.Content.Headers.ContentDisposition?.ToString());
		var pdf = await response.Content.ReadAsByteArrayAsync();
		var text = Regex.Replace(PdfText.Extract(pdf), @"\s+", " ");
		var guide = JsonDocument.Parse(await File.ReadAllTextAsync(UserGuidePaths.Content)).RootElement;
		Assert.Contains("MOE Document Designer", text);
		Assert.Contains("CONTENTS", text.ToUpperInvariant());
		foreach (var chapter in guide.GetProperty("chapters").EnumerateArray())
		{
			Assert.Contains(chapter.GetProperty("title").GetString()!.ToUpperInvariant(), text.ToUpperInvariant());
		}
		// The cover and contents, then every chapter on a new page.
		Assert.True(PageImages.PageCount(pdf) > guide.GetProperty("chapters").GetArrayLength());
	}

	[Fact]
	public async Task The_guide_content_is_served_as_json()
	{
		using var response = await factory.CreateClient().GetAsync("/api/help/user-guide.json");
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
		Assert.Equal("User Guide", json.GetProperty("guide").GetProperty("subtitle").GetString());
	}
}
