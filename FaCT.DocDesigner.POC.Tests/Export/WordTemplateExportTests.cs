using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using FaCT.DocDesigner.POC.Tests.Clauses;

namespace FaCT.DocDesigner.POC.Tests.Export;

/// <summary>A designer on a free port with its stores in a temp folder; no browser needed for the export endpoints.</summary>
public sealed class ExportServerFixture : IAsyncLifetime
{
	private readonly TempStores _folder = new();
	private System.Diagnostics.Process? _server;

	public Uri BaseUri { get; } = new($"http://localhost:{DesignerServer.FreePort()}/");
	public HttpClient Http { get; private set; } = null!;

	public async Task InitializeAsync()
	{
		_server = DesignerServer.Start(BaseUri, [$"--Templates:Root={_folder.TemplatesRoot}", $"--Clauses:Root={_folder.ClausesRoot}",
			$"--Scenarios:Root={_folder.ScenariosRoot}", $"--Blocks:Root={_folder.BlocksRoot}",
			$"--Comments:Root={_folder.CommentsRoot}", $"--Spelling:Root={_folder.SpellingRoot}", $"--Audit:Root={_folder.AuditRoot}", $"--Themes:Root={_folder.ThemesRoot}"]);
		Http = new HttpClient { BaseAddress = BaseUri, Timeout = TimeSpan.FromMinutes(2) };
		await DesignerServer.WaitUntilUpAsync(Http, BaseUri, _server, TimeSpan.FromSeconds(90));
	}

	public Task DisposeAsync()
	{
		Http?.Dispose();
		if (_server is { HasExited: false })
		{
			_server.Kill(entireProcessTree: true);
		}
		_server?.Dispose();
		return Task.CompletedTask;
	}
}

/// <summary>Export of a design as a Word template (DocGen's token syntax) and as HTML.</summary>
public sealed class WordTemplateExportTests(ExportServerFixture fixture) : IClassFixture<ExportServerFixture>
{
	private sealed record Exported(string Text, string[] Paragraphs, string[] Warnings, WordprocessingDocument Document);

	private async Task<Exported> ExportDocxAsync(string html, string css = "")
	{
		using var response = await fixture.Http.PostAsJsonAsync("api/export/docx?name=sample", new { html, css });
		response.EnsureSuccessStatusCode();
		Assert.Equal("sample.docx", response.Content.Headers.ContentDisposition!.FileName!.Trim('"'));

		var warnings = response.Headers.TryGetValues("X-Export-Warnings", out var values)
			? JsonSerializer.Deserialize<string[]>(Encoding.UTF8.GetString(Convert.FromBase64String(values.Single())))!
			: [];
		var stream = new MemoryStream(await response.Content.ReadAsByteArrayAsync());
		var document = WordprocessingDocument.Open(stream, false);
		var paragraphs = document.MainDocumentPart!.Document.Body!.Descendants<Paragraph>().Select(p => p.InnerText).ToArray();
		return new Exported(string.Join("\n", paragraphs), paragraphs, warnings, document);
	}

	[Fact]
	public async Task Fields_become_docgen_tokens_with_formats()
	{
		var export = await ExportDocxAsync("""
			<h1>Results</h1>
			<p>Policy {{ policy.number }} premium {{ policy.premium | currency }} on {{ policy.effective | shortdate }} ({{ policy.rate | percent }})</p>
			""");

		Assert.Contains("Results", export.Paragraphs);
		Assert.Contains("Policy {{policy.number}} premium {{policy.premium:C2}} on {{policy.effective:MM/dd/yyyy}} ({{policy.rate:P2}})", export.Paragraphs);
		Assert.Empty(export.Warnings);
	}

	[Fact]
	public async Task Repeats_become_each_regions_with_item_relative_fields()
	{
		var export = await ExportDocxAsync("""
			{% for loc in locations %}<h2>{{ loc.address }}</h2><p>Score {{ loc.fire }}</p>{% endfor %}
			""");

		var lines = export.Paragraphs.Where(p => p.Length > 0).ToArray();
		Assert.Equal(["{{#each locations}}", "{{address}}", "Score {{fire}}", "{{/each}}"], lines);
	}

	[Fact]
	public async Task A_table_repeat_marks_the_first_data_row_and_keeps_item_fields()
	{
		var export = await ExportDocxAsync("""
			<table class="moe-table"><thead><tr><th>Claim</th><th>Loss</th></tr></thead>
			<tbody>{% for c in claims %}<tr><td>{{ c.number }}</td><td class="num">{{ c.loss | currency }}</td></tr>{% endfor %}</tbody></table>
			""");

		Assert.Contains(export.Paragraphs, p => p == "{{#each claims}}{{number}}");
		Assert.Contains("{{loss:C2}}", export.Paragraphs);
		Assert.Empty(export.Warnings);
	}

	[Fact]
	public async Task Conditions_become_if_and_unless_regions_and_else_is_the_opposite()
	{
		var export = await ExportDocxAsync("""
			{% if policy.status == "active" %}<p>Active</p>{% else %}<p>Not active</p>{% endif %}
			{% if notes != blank %}<p>{{ notes }}</p>{% endif %}
			""");

		var lines = export.Paragraphs.Where(p => p.Length > 0).ToArray();
		Assert.Equal(
			["{{#if policy.status == \"active\"}}", "Active", "{{/if}}", "{{#unless policy.status == \"active\"}}", "Not active", "{{/unless}}",
				"{{#if notes}}", "{{notes}}", "{{/if}}"],
			lines);
	}

	[Fact]
	public async Task A_data_image_becomes_an_image_token()
	{
		var export = await ExportDocxAsync("""<img src="{{ location.photo }}" style="width: 240px">""");

		Assert.Contains(export.Paragraphs, p => p.StartsWith("{{image:location.photo|w=", StringComparison.Ordinal));
	}

	[Fact]
	public async Task The_logo_is_embedded_as_a_picture()
	{
		var export = await ExportDocxAsync("""<div class="doc-header"><img class="brand-logo" src="{{ brand.logos.horizontal_4color }}"><h1>Title</h1></div>""");

		Assert.NotEmpty(export.Document.MainDocumentPart!.ImageParts);
		Assert.Contains("Title", export.Paragraphs);
	}

	[Fact]
	public async Task Unsupported_parts_are_reported_not_dropped_silently()
	{
		var export = await ExportDocxAsync("""
			<table><tbody><tr><td>Total</td><td>{{ items | sum: "amount" | currency }}</td></tr></tbody></table>
			<div class="form-page"><p>Fixed layout</p></div>
			""");

		Assert.DoesNotContain("Fixed layout", export.Text);
		Assert.Contains(export.Warnings, w => w.Contains("Fixed-layout form pages"));
		Assert.Contains(export.Warnings, w => w.Contains("sum", StringComparison.OrdinalIgnoreCase));
	}

	[Fact]
	public async Task The_html_template_keeps_placeholders_and_the_filled_html_has_the_data()
	{
		const string html = "<p>Policy {{ policy.number }}</p>";

		using var template = await fixture.Http.PostAsJsonAsync("api/export/html?name=sample", new { html, css = ".x{color:red}" });
		template.EnsureSuccessStatusCode();
		var templateText = await template.Content.ReadAsStringAsync();
		Assert.Contains("{{ policy.number }}", templateText);
		Assert.Contains(".x{color:red}", templateText);

		using var filled = await fixture.Http.PostAsJsonAsync("api/export/html?withData=true&name=sample",
			new { html, css = "", data = new { policy = new { number = "CPP42" } } });
		filled.EnsureSuccessStatusCode();
		var filledText = await filled.Content.ReadAsStringAsync();
		Assert.Contains("Policy CPP42", filledText);
		Assert.DoesNotContain("{{", filledText);
	}
}
