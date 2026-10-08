using System.Text.Json;
using FaCT.DocDesigner.POC.Legacy;
using FaCT.DocDesigner.POC.Rendering;
using FaCT.DocDesigner.POC.Tests.Clauses;
using FaCT.DocDesigner.POC.Tests.Import;
using Microsoft.Extensions.DependencyInjection;

namespace FaCT.DocDesigner.POC.Tests.Forms;

/// <summary>The checkmark filter: when a check box prints its X.</summary>
public sealed class CheckMarkTests(ClauseAppFactory factory) : IClassFixture<ClauseAppFactory>
{
	private DocumentComposer Composer => factory.Services.GetRequiredService<DocumentComposer>();

	private async Task<string> MarkAsync(string valueJson, string? when = null)
	{
		var data = JsonDocument.Parse("{ \"v\": " + valueJson + " }").RootElement;
		var liquid = "<p>[{{ v | checkmark" + (when is null ? "" : ": \"" + when + "\"") + " }}]</p>";
		var result = await Composer.ComposeAsync(liquid, "", data);
		Assert.True(result.Error is null, result.Error);
		var page = result.Html!;
		return page[(page.IndexOf("<p>[", StringComparison.Ordinal) + 4)..page.IndexOf("]</p>", StringComparison.Ordinal)];
	}

	[Theory]
	[InlineData("true")]
	[InlineData("1")]
	[InlineData("2.5")]
	[InlineData("-1")]
	[InlineData("\"Yes\"")]
	[InlineData("\"yes\"")]
	[InlineData("\" Y \"")]
	[InlineData("\"X\"")]
	[InlineData("\"on\"")]
	[InlineData("\"checked\"")]
	[InlineData("\"true\"")]
	[InlineData("\"T\"")]
	[InlineData("\"1\"")]
	[InlineData("\"S\u00ed\"")]
	[InlineData("\"Oui\"")]
	[InlineData("[false, true]")]
	public void A_yes_value_checks_the_box(string value) =>
		Assert.Equal("X", MarkAsync(value).GetAwaiter().GetResult());

	[Theory]
	[InlineData("false")]
	[InlineData("0")]
	[InlineData("null")]
	[InlineData("\"\"")]
	[InlineData("\"No\"")]
	[InlineData("\"N\"")]
	[InlineData("\"false\"")]
	[InlineData("\"0\"")]
	[InlineData("\"Corporation\"")]
	[InlineData("[]")]
	[InlineData("[false]")]
	[InlineData("{ \"a\": 1 }")]
	public void Anything_else_leaves_it_empty(string value) =>
		Assert.Equal("", MarkAsync(value).GetAwaiter().GetResult());

	[Fact]
	public async Task A_missing_value_leaves_it_empty()
	{
		var result = await Composer.ComposeAsync("<p>[{{ nothing | checkmark }}]</p>", "", JsonDocument.Parse("{}").RootElement);
		Assert.Contains("<p>[]</p>", result.Html);
	}

	[Theory]
	[InlineData("\"Corporation\"", "Corporation", "X")]
	[InlineData("\" corporation \"", "Corporation", "X")]
	[InlineData("\"Partnership\"", "Corporation", "")]
	[InlineData("3", "3", "X")]
	[InlineData("3.0", "3", "X")]
	[InlineData("4", "3", "")]
	[InlineData("\"3\"", "3", "X")]
	[InlineData("true", "true", "X")]
	[InlineData("true", "Yes", "X")]
	[InlineData("false", "false", "X")]
	[InlineData("false", "No", "X")]
	[InlineData("false", "true", "")]
	[InlineData("[\"Auto\", \"Property\"]", "Property", "X")]
	[InlineData("[\"Auto\"]", "Property", "")]
	[InlineData("null", "Corporation", "")]
	public void Checked_when_compares_with_the_value(string value, string when, string expected) =>
		Assert.Equal(expected, MarkAsync(value, when).GetAwaiter().GetResult());

	[Fact]
	public async Task A_too_long_checked_when_value_is_an_error()
	{
		var result = await Composer.ComposeAsync("<p>{{ v | checkmark: \"" + new string('a', 101) + "\" }}</p>", "", JsonDocument.Parse("{ \"v\": 1 }").RootElement);
		Assert.Contains("at most 100 characters", result.Error);
	}

	[Fact]
	public async Task The_mark_is_centered_bold_in_a_form_page_box()
	{
		var result = await Composer.ComposeAsync("<p>x</p>", "", JsonDocument.Parse("{}").RootElement);
		Assert.Contains(".form-page .field-check { display: flex; align-items: center; justify-content: center; font-weight: 700; line-height: 1; }", result.Html);
	}
}

/// <summary>The legacy importer keeps check boxes apart from text fields.</summary>
public sealed class CheckBoxImportTests : IDisposable
{
	private readonly TempAssets _assets = new();

	public void Dispose() => _assets.Dispose();

	private LegacyImportResult Import(string fields) => new LegacyFormImporter(_assets.Store).Import(
		"<html><body><section class=\"form-page\">" + fields + "</section></body></html>");

	[Fact]
	public void A_checkbox_field_gets_the_check_class()
	{
		var result = Import(
			"<span class=\"abs field\" data-field=\"Agree\" data-kind=\"checkbox\" style=\"left:10pt;top:10pt;width:8pt;height:8pt\"></span>" +
			"<span class=\"abs field\" data-field=\"Name\" style=\"left:10pt;top:30pt;width:80pt;height:12pt\"></span>");
		Assert.Equal(2, result.Fields);
		Assert.Equal(1, result.CheckBoxes);
		Assert.Contains("<span class=\"abs field field-check\" data-legacy-field=\"Agree\"", result.Html);
		Assert.Contains("<span class=\"abs field\" data-legacy-field=\"Name\"", result.Html);
	}

	[Theory]
	[InlineData("radio")]
	[InlineData("CHECKBOX")]
	[InlineData("checkbox\" onclick=\"x")]
	public void Other_kinds_stay_text_fields(string kind)
	{
		var result = Import($"<span class=\"abs field\" data-field=\"F\" data-kind=\"{kind.Replace("\"", "&quot;")}\" style=\"left:1pt\"></span>");
		Assert.Equal(0, result.CheckBoxes);
		Assert.DoesNotContain("field-check", result.Html);
		Assert.DoesNotContain("onclick", result.Html);
	}

	[Fact]
	public void A_fillable_pdf_check_box_becomes_a_check_box_field()
	{
		var result = new FaCT.DocDesigner.POC.Import.PdfImporter(new LegacyFormImporter(_assets.Store)).Import(Pdf.WithForm());
		Assert.Equal(1, result.Counts["checkboxes"]);
		Assert.Contains("<span class=\"abs field field-check\" data-legacy-field=\"Agree\"", result.Html);
		Assert.Contains("<span class=\"abs field\" data-legacy-field=\"PolicyNumber\"", result.Html);
	}
}
