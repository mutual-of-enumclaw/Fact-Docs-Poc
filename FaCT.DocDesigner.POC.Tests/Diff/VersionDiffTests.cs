using FaCT.DocDesigner.POC.Templates;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace FaCT.DocDesigner.POC.Tests.Diff;

public sealed class VersionDiffTests
{
	private static string Lines(params string[] lines) => string.Join('\n', lines);

	[Fact]
	public void Identical_text_has_no_lines_and_no_counts()
	{
		var diff = VersionDiff.Lines(Lines("a", "b", "c"), Lines("a", "b", "c"));
		Assert.True(diff.Identical);
		Assert.Empty(diff.Lines);
		Assert.Equal(0, diff.Added);
		Assert.Equal(0, diff.Removed);
	}

	[Fact]
	public void Changed_lines_are_a_removal_and_an_addition()
	{
		var diff = VersionDiff.Lines(Lines("a", "old", "c"), Lines("a", "new", "c"));
		Assert.False(diff.Identical);
		Assert.Equal(1, diff.Added);
		Assert.Equal(1, diff.Removed);
		Assert.Equal(
			[("same", "a"), ("removed", "old"), ("added", "new"), ("same", "c")],
			diff.Lines.Select(l => (l.Type, l.Text!)));
	}

	[Fact]
	public void Added_and_removed_lines_are_counted()
	{
		var diff = VersionDiff.Lines(Lines("a", "b"), Lines("a", "b", "c", "d"));
		Assert.Equal(2, diff.Added);
		Assert.Equal(0, diff.Removed);
		diff = VersionDiff.Lines(Lines("a", "b", "c"), Lines("a"));
		Assert.Equal(0, diff.Added);
		Assert.Equal(2, diff.Removed);
	}

	[Fact]
	public void Everything_new_compared_with_nothing()
	{
		var diff = VersionDiff.Lines("", Lines("x", "y"));
		Assert.Equal(2, diff.Added);
		Assert.All(diff.Lines, l => Assert.Equal("added", l.Type));
	}

	[Fact]
	public void Long_unchanged_stretches_collapse_to_context_around_changes()
	{
		var before = Enumerable.Range(1, 30).Select(i => "line " + i).ToArray();
		var after = before.ToArray();
		after[14] = "changed 15";
		var diff = VersionDiff.Lines(Lines(before), Lines(after));

		// Leading unchanged run: only the Context lines before the change are kept.
		Assert.Equal("skip", diff.Lines[0].Type);
		Assert.Equal(14 - VersionDiff.Context, diff.Lines[0].Count);
		Assert.Equal(["line 12", "line 13", "line 14"], diff.Lines.Skip(1).Take(3).Select(l => l.Text));
		Assert.Equal("removed", diff.Lines[4].Type);
		Assert.Equal("added", diff.Lines[5].Type);
		Assert.Equal(["line 16", "line 17", "line 18"], diff.Lines.Skip(6).Take(3).Select(l => l.Text));
		Assert.Equal("skip", diff.Lines[^1].Type);
		Assert.Equal(15 - VersionDiff.Context, diff.Lines[^1].Count);
		Assert.Equal(10, diff.Lines.Count);
	}

	[Fact]
	public void Short_unchanged_runs_between_changes_are_kept_whole()
	{
		var diff = VersionDiff.Lines(Lines("x1", "s1", "s2", "s3", "s4", "s5", "x2"), Lines("y1", "s1", "s2", "s3", "s4", "s5", "y2"));
		Assert.DoesNotContain(diff.Lines, l => l.Type == "skip");
		Assert.Equal(5, diff.Lines.Count(l => l.Type == "same"));
	}

	[Fact]
	public void Long_unchanged_runs_between_changes_keep_context_on_both_sides()
	{
		var middle = Enumerable.Range(1, 20).Select(i => "m" + i).ToArray();
		var diff = VersionDiff.Lines(Lines(["a", .. middle, "b"]), Lines(["A", .. middle, "B"]));
		var skip = Assert.Single(diff.Lines, l => l.Type == "skip");
		Assert.Equal(20 - 2 * VersionDiff.Context, skip.Count);
		Assert.Equal(2 * VersionDiff.Context, diff.Lines.Count(l => l.Type == "same"));
	}

	[Fact]
	public void Html_is_compared_one_tag_per_line()
	{
		Assert.Equal(Lines("<body>", "<h1>Title</h1>", "<p>Text <b>bold</b>", "</p>", "</body>"),
			VersionDiff.HtmlLines("<body>\r\n  <h1>Title</h1>   <p>Text <b>bold</b></p></body>"));
		Assert.Equal("", VersionDiff.HtmlLines(""));
	}

	[Fact]
	public void A_wording_change_is_one_html_line()
	{
		var diff = VersionDiff.Lines(VersionDiff.HtmlLines("<h1>A</h1><p>Old wording</p><p>Z</p>"), VersionDiff.HtmlLines("<h1>A</h1><p>New wording</p><p>Z</p>"));
		Assert.Equal(1, diff.Added);
		Assert.Equal(1, diff.Removed);
		Assert.Equal("<p>New wording</p>", diff.Lines.Single(l => l.Type == "added").Text);
	}

	[Fact]
	public void Css_is_compared_one_rule_per_line()
	{
		Assert.Equal(Lines(".a{color:red;}", ".b{margin:0}", "@media print{.c{x:y}", "}"),
			VersionDiff.CssLines(".a{color:red;} .b{margin:0}\n@media print{.c{x:y}}"));
		var diff = VersionDiff.Lines(VersionDiff.CssLines(".a{color:red;}.b{margin:0}"), VersionDiff.CssLines(".a{color:blue;}.b{margin:0}"));
		Assert.Equal(1, diff.Added);
		Assert.Equal(1, diff.Removed);
	}

	private static byte[] Pdf(params string[][] pages)
	{
		var builder = new PdfDocumentBuilder();
		var font = builder.AddStandard14Font(Standard14Font.Helvetica);
		foreach (var lines in pages)
		{
			var page = builder.AddPage(PageSize.Letter);
			var y = 700;
			foreach (var line in lines)
			{
				page.AddText(line, 12, new PdfPoint(72, y), font);
				y -= 20;
			}
		}
		return builder.Build();
	}

	[Fact]
	public void Pdf_text_is_read_line_by_line_with_page_markers()
	{
		var pdf = Pdf(["Declarations page", "Policy CPP1"], ["Second page text"]);
		Assert.Equal(Lines("Declarations page", "Policy CPP1", "--- Page 2 ---", "Second page text", ""), VersionDiff.PdfLines(pdf));
		Assert.Equal(2, VersionDiff.PageCount(pdf));
	}

	[Fact]
	public void Printed_text_diff_finds_the_changed_line_and_a_new_page()
	{
		var before = VersionDiff.PdfLines(Pdf(["Title", "Old exclusion"]));
		var after = VersionDiff.PdfLines(Pdf(["Title", "New exclusion"], ["Appendix"]));
		var diff = VersionDiff.Lines(before, after);
		Assert.Equal(["New exclusion", "--- Page 2 ---", "Appendix"], diff.Lines.Where(l => l.Type == "added").Select(l => l.Text));
		Assert.Equal(["Old exclusion"], diff.Lines.Where(l => l.Type == "removed").Select(l => l.Text));
	}
}
