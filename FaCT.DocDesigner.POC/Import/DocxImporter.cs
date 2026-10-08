using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using FaCT.DocDesigner.POC.Legacy;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using V = DocumentFormat.OpenXml.Vml;

namespace FaCT.DocDesigner.POC.Import;

/// <summary>
/// Converts a Word (.docx) document into flowing designer HTML: headings, paragraphs, lists, tables, images, links and
/// text boxes. MERGEFIELDs and tagged content controls become Data Fields ({{ path }}) and a sample model is built for
/// them. Output is generated, never copied: every text run is encoded (braces too, so wording is never read as Liquid),
/// and only validated style values, http(s)/mailto links and PNG/JPEG/GIF images are written.
/// Images go to the <see cref="LegacyAssetStore"/> like legacy form images (URL on the canvas, inlined in PDFs).
/// </summary>
public sealed partial class DocxImporter(LegacyAssetStore assets)
{
	private const int MaxDepth = 16;
	private const long MaxImageBytes = 20 * 1024 * 1024;
	private const double EmuPerPx = 9525;

	private static readonly HashSet<string> ReservedRoots = new(StringComparer.OrdinalIgnoreCase)
	{
		"brand", "empty", "blank", "nil", "null", "true", "false", "forloop"
	};

	private static readonly Dictionary<string, string> HighlightColors = new(StringComparer.OrdinalIgnoreCase)
	{
		["yellow"] = "#FFFF00", ["green"] = "#00FF00", ["cyan"] = "#00FFFF", ["magenta"] = "#FF00FF",
		["blue"] = "#0000FF", ["red"] = "#FF0000", ["darkBlue"] = "#000080", ["darkCyan"] = "#008080",
		["darkGreen"] = "#008000", ["darkMagenta"] = "#800080", ["darkRed"] = "#800000", ["darkYellow"] = "#808000",
		["darkGray"] = "#808080", ["lightGray"] = "#C0C0C0", ["black"] = "#000000", ["white"] = "#FFFFFF"
	};

	public DocumentImportResult Import(byte[] bytes, DocxImportOptions? options = null)
	{
		DocumentImport.CheckDocxPackage(bytes);

		WordprocessingDocument document;
		try
		{
			document = WordprocessingDocument.Open(new MemoryStream(bytes, writable: false), isEditable: false);
		}
		catch (Exception ex) when (ex is OpenXmlPackageException or InvalidDataException or XmlException or IOException or FileFormatException)
		{
			throw new DocumentImportException("The file could not be read as a Word document.", ex);
		}

		using (document)
		{
			try
			{
				return ConvertDocument(document, options ?? DocxImportOptions.Default);
			}
			catch (Exception ex) when (ex is XmlException or OpenXmlPackageException or InvalidDataException)
			{
				throw new DocumentImportException("The Word document is damaged and could not be read.", ex);
			}
		}
	}

	private DocumentImportResult ConvertDocument(WordprocessingDocument document, DocxImportOptions options)
	{
		var main = document.MainDocumentPart;
		var body = main?.Document?.Body ?? throw new DocumentImportException("The Word document has no body.");
		var converter = new Converter(main, assets, options);
		var html = new StringBuilder();
		converter.ApplyPlaceholders(body);
		converter.Blocks(body.ChildElements, html, 0);
		var section = body.Elements<SectionProperties>().LastOrDefault();
		var (header, footer) = converter.HeaderAndFooter(section);
		var running = header is not null || footer is not null;
		converter.AddDocumentNotes(running);

		var pages = int.TryParse(document.ExtendedFilePropertiesPart?.Properties?.Pages?.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var p) && p > 0
			? p
			: converter.Counts.GetValueOrDefault("pageBreaks") + 1;

		var geometry = running ? PageGeometry.Of(section) : null;
		return new DocumentImportResult(
			"docx",
			geometry is null ? html.ToString() : RunningLayout(html.ToString(), header, footer, geometry),
			geometry is null ? string.Empty : RunningCss(geometry),
			pages,
			converter.Counts,
			converter.Fields,
			converter.Fields.Count == 0 ? null : converter.BuildModel(),
			converter.Notes);
	}

	/// <summary>Word page margins and header/footer distances, in points.</summary>
	private sealed record PageGeometry(double Top, double Bottom, double Left, double Right, double Header, double Footer)
	{
		public static PageGeometry Of(SectionProperties? section)
		{
			var margin = section?.GetFirstChild<PageMargin>();
			static double Twips(long? value, double fallback) => value is { } v ? Math.Clamp(Math.Abs(v) / 20.0, 0, 288) : fallback;
			return new PageGeometry(
				Twips(margin?.Top?.Value, 72), Twips(margin?.Bottom?.Value, 72),
				Twips(margin?.Left?.Value, 72), Twips(margin?.Right?.Value, 72),
				Twips(margin?.Header?.Value, 36), Twips(margin?.Footer?.Value, 36));
		}
	}

	/// <summary>
	/// The body in the renderer's running-header layout (the same one GhostDraft imports use): Chrome repeats the table's
	/// header row on every printed page, and the footer (.gd-pdffoot) is moved into Chrome's own page footer, where
	/// .gd-pageno / .gd-pagecount become the page number and count. The footer's geometry rides in class names because
	/// the render sanitizer drops data-* attributes.
	/// </summary>
	private static string RunningLayout(string body, string? header, string? footer, PageGeometry page)
	{
		var html = new StringBuilder("<div class=\"gd-doc gd-flow gd-run\"><table class=\"gd-runt\"><thead><tr><td>");
		if (header is not null) html.Append("<div class=\"gd-header\">").Append(header).Append("</div>");
		html.Append("<span class=\"gd-pgmark\"></span></td></tr></thead><tfoot><tr><td>");
		if (footer is not null)
		{
			html.Append("<div class=\"gd-footer gd-pdffoot gd-fy-").Append(Num(page.Footer)).Append(" gd-mb-").Append(Num(page.Bottom))
				.Append(" gd-ml-").Append(Num(page.Left)).Append(" gd-mr-").Append(Num(page.Right)).Append("\">")
				.Append(footer).Append("</div>");
		}
		html.Append("</td></tr></tfoot><tbody><tr><td>").Append(body).Append("</td></tr></tbody></table></div>");
		return html.ToString();
	}

	// Page margin = Word's header/footer distance; the header/footer bands keep the body at Word's body margins.
	private static string RunningCss(PageGeometry page) =>
		$"@page gdrun{{margin:{Num(page.Header)}pt 0 {Num(page.Footer)}pt 0;}}\n" +
		$".gd-flow.gd-run{{page:gdrun;padding:0 {Num(page.Right)}pt 0 {Num(page.Left)}pt;}}\n" +
		".gd-doc table.gd-runt{width:100%;border-collapse:collapse;table-layout:fixed;}\n" +
		".gd-doc table.gd-runt>*>tr>td{padding:0;}\n" +
		$".gd-doc table.gd-runt>thead>tr>td{{height:{Num(Math.Max(page.Top - page.Header, 0))}pt;vertical-align:top;position:relative;}}\n" +
		$".gd-doc table.gd-runt>tfoot>tr>td{{height:{Num(Math.Max(page.Bottom - page.Footer, 0))}pt;vertical-align:bottom;position:relative;}}\n" +
		".gd-pgmark{position:absolute;left:0;top:0;font-size:2pt;line-height:1;color:#fff;white-space:nowrap;}\n";

	private static string Num(double value) => Math.Round(value, 2).ToString("0.##", CultureInfo.InvariantCulture);

	/// <summary>
	/// Turns a Word merge field / content control name into a Liquid path of [A-Za-z0-9_] segments, or null.
	/// "Insured Name" => Insured_Name, "policy.2nd" => policy._2nd, "brand" => field_brand (brand is the template's own data).
	/// </summary>
	public static string? ToFieldPath(string? name)
	{
		if (string.IsNullOrWhiteSpace(name)) return null;
		var segments = name.Split('.', StringSplitOptions.RemoveEmptyEntries)
			.Select(s => InvalidPathChars().Replace(s.Trim(), "_").Trim('_'))
			.Where(s => s.Length > 0)
			.Select(s => char.IsAsciiDigit(s[0]) ? "_" + s : s)
			.ToList();
		if (segments.Count == 0) return null;
		if (ReservedRoots.Contains(segments[0])) segments[0] = "field_" + segments[0];
		var path = string.Join('.', segments);
		return path.Length <= 100 ? path : null;
	}

	/// <summary>The field name of a MERGEFIELD instruction (e.g. <c> MERGEFIELD "Insured Name" \* MERGEFORMAT </c>), or null.</summary>
	public static string? MergeFieldName(string? instruction) =>
		instruction is not null && MergeField().Match(instruction) is { Success: true } m ? m.Groups["name"].Value : null;

	/// <summary>
	/// The Data Field format matching a merge field's switches: <c>\* Upper</c> =&gt; upcase, a date picture (<c>\@</c>)
	/// =&gt; shortdate, a number picture (<c>\#</c>) =&gt; currency / dollars / decimal / number. Null otherwise.
	/// </summary>
	public static string? MergeFieldFormat(string? instruction)
	{
		if (instruction is null) return null;
		if (UpperSwitch().IsMatch(instruction)) return "upcase";
		if (DateSwitch().IsMatch(instruction)) return "shortdate";
		if (NumberSwitch().Match(instruction) is { Success: true } number)
		{
			var picture = number.Groups[1].Value;
			var cents = picture.Contains(".00", StringComparison.Ordinal);
			return picture.Contains('$') ? (cents ? "currency" : "dollars") : cents ? "decimal" : "number";
		}
		return null;
	}

	private static string Encode(string text) =>
		MultiSpace().Replace(DocumentImport.EncodeText(text),
			m => " " + string.Concat(Enumerable.Repeat("&nbsp;", m.Length - 1)));

	// Attribute values: the designer writes them back unescaped, so braces are dropped rather than encoded.
	private static string EncodeAttribute(string text) => WebUtility.HtmlEncode(text.Replace("{", string.Empty).Replace("}", string.Empty));

	private static string? HexColor(string? value) =>
		value is not null && HexPattern().IsMatch(value) ? "#" + value.ToUpperInvariant() : null;

	private static bool On(OnOffType? toggle) => toggle is not null && (toggle.Val is null || toggle.Val.Value);

	private static string Pt(double value) => Math.Round(value, 2).ToString(CultureInfo.InvariantCulture) + "pt";

	private sealed class Converter(MainDocumentPart main, LegacyAssetStore assets, DocxImportOptions options)
	{
		private readonly WordPlaceholders _placeholders = new(options.Placeholders);
		private readonly Dictionary<string, string?> _fieldFormats = [];
		private readonly Dictionary<string, Style> _styles = main.StyleDefinitionsPart?.Styles?.Elements<Style>()
			.Where(s => s.StyleId?.Value is not null)
			.GroupBy(s => s.StyleId!.Value!)
			.ToDictionary(g => g.Key, g => g.First()) ?? [];
		private readonly Numbering? _numbering = main.NumberingDefinitionsPart?.Numbering;
		private readonly List<FieldState> _fieldStack = [];
		private readonly HashSet<string> _notes = [];
		private readonly List<string> _fields = [];
		private int _floatingImages, _unsupportedImages, _unsafeLinks, _tooDeep;
		private bool _trackedChanges;
		// The part being converted: images and links are relationships of the body, a header or a footer.
		private OpenXmlPartContainer _part = main;
		private bool _inHeaderFooter;

		public Dictionary<string, int> Counts { get; } = new()
		{
			["paragraphs"] = 0, ["headings"] = 0, ["listItems"] = 0, ["tables"] = 0, ["images"] = 0,
			["fields"] = 0, ["links"] = 0, ["textBoxes"] = 0, ["pageBreaks"] = 0,
			["headers"] = 0, ["footers"] = 0, ["pageNumbers"] = 0, ["placeholders"] = 0
		};

		public IReadOnlyList<string> Fields => _fields;

		public IReadOnlyList<string> Notes
		{
			get
			{
				var notes = _notes.ToList();
				if (_floatingImages > 0) notes.Add($"{_floatingImages} floating image(s) were placed in line with the text.");
				if (_unsupportedImages > 0) notes.Add($"{_unsupportedImages} image(s) in formats other than PNG, JPEG or GIF (e.g. EMF, WMF, SVG) were skipped.");
				if (_unsafeLinks > 0) notes.Add($"{_unsafeLinks} link(s) that were not http, https or mailto were imported as plain text.");
				if (_trackedChanges) notes.Add("Tracked changes were accepted (insertions kept, deletions dropped).");
				if (_tooDeep > 0) notes.Add("Content nested more than " + MaxDepth + " levels deep was skipped.");
				if (_placeholders.BracketCandidates > 0)
				{
					notes.Add($"{_placeholders.BracketCandidates} [bracketed] phrase(s) look like placeholders. Import again with [Name] placeholders turned on to make them data fields.");
				}
				return notes;
			}
		}

		// ---- Blocks ----------------------------------------------------------------------------------------------

		/// <summary>Turns typed placeholders under <paramref name="root"/> into merge fields (in memory) before conversion.</summary>
		public void ApplyPlaceholders(OpenXmlElement root)
		{
			_placeholders.Apply(root);
			Counts["placeholders"] = _placeholders.Converted;
		}

		public void Blocks(IEnumerable<OpenXmlElement> elements, StringBuilder html, int depth)
		{
			if (depth > MaxDepth)
			{
				_tooDeep++;
				return;
			}

			var list = new ListWriter(html);
			foreach (var element in Flatten(elements))
			{
				switch (element)
				{
					case Paragraph paragraph when ListInfo(paragraph) is { } item:
						var writer = Inline(paragraph, depth);
						if (writer.BreakBefore) { list.Close(); PageBreak(html); }
						Counts["listItems"]++;
						list.Item(item.NumId, item.Level, item.Ordered, NextNumber(item.NumId, item.Level), writer.Html.Length == 0 ? "&nbsp;" : writer.Html.ToString());
						FlushTextBoxes(writer, html, depth);
						if (writer.BreakAfter) { list.Close(); PageBreak(html); }
						break;
					case Paragraph paragraph:
						list.Close();
						WriteParagraph(paragraph, html, depth);
						break;
					case Table table:
						list.Close();
						WriteTable(table, html, depth);
						break;
				}
			}
			list.Close();
		}

		// Content controls and custom XML wrap blocks; their content is imported in place.
		private static IEnumerable<OpenXmlElement> Flatten(IEnumerable<OpenXmlElement> elements)
		{
			foreach (var element in elements)
			{
				switch (element)
				{
					case SdtBlock sdt when sdt.SdtContentBlock is { } content:
						foreach (var inner in Flatten(content.ChildElements)) yield return inner;
						break;
					case CustomXmlBlock custom:
						foreach (var inner in Flatten(custom.ChildElements)) yield return inner;
						break;
					default:
						yield return element;
						break;
				}
			}
		}

		private void WriteParagraph(Paragraph paragraph, StringBuilder html, int depth)
		{
			var properties = paragraph.ParagraphProperties;
			var writer = Inline(paragraph, depth);
			if (On(properties?.PageBreakBefore) || writer.BreakBefore) PageBreak(html);

			var tag = HeadingTag(properties?.ParagraphStyleId?.Val?.Value) ?? "p";
			Counts[tag == "p" ? "paragraphs" : "headings"]++;

			html.Append('<').Append(tag);
			var style = new StringBuilder();
			switch (properties?.Justification?.Val?.InnerText)
			{
				case "center": style.Append("text-align:center;"); break;
				case "right" or "end": style.Append("text-align:right;"); break;
				case "both" or "distribute": style.Append("text-align:justify;"); break;
			}
			var indent = properties?.Indentation;
			if (int.TryParse(indent?.Left?.Value ?? indent?.Start?.Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var twips) && twips > 0)
			{
				style.Append("margin-left:").Append(Pt(Math.Min(twips, 14400) / 20.0)).Append(';');
			}
			if (style.Length > 0) html.Append(" style=\"").Append(style).Append('"');
			html.Append('>');
			html.Append(writer.Html.Length == 0 ? "&nbsp;" : writer.Html.ToString());
			html.Append("</").Append(tag).Append('>');

			FlushTextBoxes(writer, html, depth);
			if (writer.BreakAfter) PageBreak(html);
		}

		private void PageBreak(StringBuilder html)
		{
			Counts["pageBreaks"]++;
			html.Append("<div class=\"page-break\"></div>");
		}

		private void FlushTextBoxes(InlineWriter writer, StringBuilder html, int depth)
		{
			foreach (var box in writer.TextBoxes)
			{
				Counts["textBoxes"]++;
				html.Append("<div class=\"doc-box\">");
				Blocks(box.ChildElements, html, depth + 1);
				html.Append("</div>");
			}
		}

		/// <summary>h1-h3 for Title / Heading n styles (also styles based on them), otherwise null.</summary>
		private string? HeadingTag(string? styleId)
		{
			for (var i = 0; styleId is not null && i < 10; i++)
			{
				var name = _styles.TryGetValue(styleId, out var style) ? style.StyleName?.Val?.Value ?? styleId : styleId;
				if (name.Equals("Title", StringComparison.OrdinalIgnoreCase)) return "h1";
				if (HeadingName().Match(name) is { Success: true } m)
				{
					return int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) switch { 1 => "h1", 2 => "h2", _ => "h3" };
				}
				styleId = style?.BasedOn?.Val?.Value;
			}
			return null;
		}

		// ---- Lists -----------------------------------------------------------------------------------------------

		// Word numbers a list (numId) continuously, even when other paragraphs or tables interrupt it; a level restarts
		// after an item at a higher level.
		private readonly Dictionary<int, int[]> _listCounters = [];

		private int[] NextNumber(int numId, int level)
		{
			if (!_listCounters.TryGetValue(numId, out var counters)) _listCounters[numId] = counters = new int[9];
			counters[level]++;
			Array.Clear(counters, level + 1, counters.Length - level - 1);
			return (int[])counters.Clone();
		}

		private (int NumId, int Level, bool Ordered)? ListInfo(Paragraph paragraph)
		{
			var numbering = paragraph.ParagraphProperties?.NumberingProperties;
			// List styles (e.g. "List Bullet") carry the numbering on the style instead of the paragraph.
			var styleId = paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
			for (var i = 0; numbering?.NumberingId is null && styleId is not null && i < 10; i++)
			{
				if (!_styles.TryGetValue(styleId, out var style)) break;
				numbering = style.StyleParagraphProperties?.NumberingProperties ?? numbering;
				styleId = style.BasedOn?.Val?.Value;
			}

			var numId = numbering?.NumberingId?.Val?.Value;
			if (numId is null or 0) return null;
			var level = Math.Clamp(numbering?.NumberingLevelReference?.Val?.Value ?? 0, 0, 8);

			var instance = _numbering?.Elements<NumberingInstance>().FirstOrDefault(n => n.NumberID?.Value == numId);
			var abstractId = instance?.AbstractNumId?.Val?.Value;
			var definition = _numbering?.Elements<AbstractNum>().FirstOrDefault(a => a.AbstractNumberId?.Value == abstractId);
			var format = definition?.Elements<Level>().FirstOrDefault(l => l.LevelIndex?.Value == level)?.NumberingFormat?.Val?.InnerText;
			return (numId.Value, level, format is not null and not "bullet" and not "none");
		}

		private sealed class ListWriter(StringBuilder html)
		{
			private readonly Stack<(bool Ordered, int NumId)> _open = new();

			/// <param name="numbers">The list's running number at each level (Word numbering), for lists that resume.</param>
			public void Item(int numId, int level, bool ordered, int[] numbers, string content)
			{
				while (_open.Count > level + 1) CloseTop();
				// Another list (or bullets vs numbers) at the same level is a new list.
				if (_open.Count == level + 1 && _open.Peek() != (ordered, numId)) CloseTop();
				if (_open.Count == level + 1) html.Append("</li>");
				while (_open.Count < level + 1)
				{
					// A list that resumes after an interruption starts at its running number; a list re-opened only to hold
					// a deeper item continues the item it belongs to.
					var number = numbers[_open.Count];
					var start = ordered && number > 1 ? " start=\"" + number + "\"" : string.Empty;
					html.Append(ordered ? "<ol" + start + ">" : "<ul>");
					_open.Push((ordered, numId));
					// Skipped levels still need an item to hold the nested list; it has no text, so no marker.
					if (_open.Count < level + 1) html.Append("<li style=\"list-style:none\">");
				}
				html.Append("<li>").Append(content);
			}

			public void Close()
			{
				while (_open.Count > 0) CloseTop();
			}

			private void CloseTop() => html.Append("</li>").Append(_open.Pop().Ordered ? "</ol>" : "</ul>");
		}

		// ---- Tables ----------------------------------------------------------------------------------------------

		private void WriteTable(Table table, StringBuilder html, int depth)
		{
			if (depth > MaxDepth)
			{
				_tooDeep++;
				return;
			}
			Counts["tables"]++;

			var rows = table.Elements<TableRow>()
				.Concat(table.Elements<SdtRow>().SelectMany(r => r.SdtContentRow?.Elements<TableRow>() ?? []))
				.ToList();
			var grid = rows.Select(GridCells).ToList();

			html.Append("<table class=\"doc-table ").Append(HasBorders(table) ? "tbl-b-all" : "tbl-b-none").Append("\">");
			var headerRows = rows.TakeWhile(IsHeaderRow).Count();
			for (var r = 0; r < rows.Count; r++)
			{
				if (r == 0) html.Append(headerRows > 0 ? "<thead>" : "<tbody>");
				if (r == headerRows && r > 0) html.Append("</thead><tbody>");
				var header = r < headerRows;
				html.Append("<tr>");
				foreach (var (cell, column, span) in grid[r])
				{
					var merge = cell.TableCellProperties?.VerticalMerge;
					if (merge is not null && merge.Val?.InnerText is null or "continue") continue;

					var rowSpan = 1;
					if (merge is not null)
					{
						for (var next = r + 1; next < rows.Count; next++)
						{
							var below = grid[next].FirstOrDefault(c => c.Column == column).Cell?.TableCellProperties?.VerticalMerge;
							if (below is null || below.Val?.InnerText is not (null or "continue")) break;
							rowSpan++;
						}
					}

					var tag = header ? "th" : "td";
					html.Append('<').Append(tag);
					if (span > 1) html.Append(" colspan=\"").Append(span).Append('"');
					if (rowSpan > 1) html.Append(" rowspan=\"").Append(rowSpan).Append('"');
					var style = new StringBuilder();
					if (HexColor(cell.TableCellProperties?.Shading?.Fill?.Value) is { } fill) style.Append("background:").Append(fill).Append(';');
					switch (cell.TableCellProperties?.TableCellVerticalAlignment?.Val?.InnerText)
					{
						case "center": style.Append("vertical-align:middle;"); break;
						case "bottom": style.Append("vertical-align:bottom;"); break;
					}
					if (style.Length > 0) html.Append(" style=\"").Append(style).Append('"');
					html.Append('>');
					Blocks(cell.ChildElements, html, depth + 1);
					html.Append("</").Append(tag).Append('>');
				}
				html.Append("</tr>");
			}
			if (rows.Count > 0) html.Append(headerRows == rows.Count ? "</thead>" : "</tbody>");
			html.Append("</table>");
		}

		private static bool IsHeaderRow(TableRow row) =>
			row.TableRowProperties?.GetFirstChild<TableHeader>() is { } header && header.Val?.InnerText is null or "on" or "true" or "1";

		private static List<(TableCell Cell, int Column, int Span)> GridCells(TableRow row)
		{
			var cells = new List<(TableCell, int, int)>();
			var column = row.TableRowProperties?.GetFirstChild<GridBefore>()?.Val?.Value ?? 0;
			var rowCells = row.Elements<TableCell>().Concat(row.Elements<SdtCell>().SelectMany(c => c.SdtContentCell?.Elements<TableCell>() ?? []));
			foreach (var cell in rowCells)
			{
				var span = Math.Clamp(cell.TableCellProperties?.GridSpan?.Val?.Value ?? 1, 1, 63);
				cells.Add((cell, column, span));
				column += span;
			}
			return cells;
		}

		private bool HasBorders(Table table)
		{
			var properties = table.GetFirstChild<TableProperties>();
			if (properties?.TableBorders is { } borders)
			{
				return borders.ChildElements.OfType<BorderType>().Any(b => b.Val?.InnerText is not (null or "nil" or "none"));
			}
			var styleId = properties?.TableStyle?.Val?.Value;
			for (var i = 0; styleId is not null && i < 10; i++)
			{
				if (!_styles.TryGetValue(styleId, out var style)) return styleId.Contains("Grid", StringComparison.OrdinalIgnoreCase);
				if (style.StyleTableProperties?.TableBorders is { } styleBorders)
				{
					return styleBorders.ChildElements.OfType<BorderType>().Any(b => b.Val?.InnerText is not (null or "nil" or "none"));
				}
				styleId = style.BasedOn?.Val?.Value;
			}
			return false;
		}

		// ---- Inline content --------------------------------------------------------------------------------------

		private InlineWriter Inline(OpenXmlElement paragraph, int depth)
		{
			var writer = new InlineWriter();
			InlineChildren(paragraph.ChildElements, writer, depth);
			writer.Flush();
			return writer;
		}

		private void InlineChildren(IEnumerable<OpenXmlElement> children, InlineWriter writer, int depth)
		{
			foreach (var child in children)
			{
				switch (child)
				{
					case Run run:
						WriteRun(run, writer);
						break;
					case Hyperlink link:
						WriteLink(link, writer, depth);
						break;
					case SimpleField field:
						var shown = field.Descendants<RunProperties>().FirstOrDefault();
						if (_placeholders.TryGetField(field, out var placeholder))
						{
							DataField(placeholder.Path, placeholder.Format, shown, writer);
						}
						else if (!ReplaceField(field.Instruction?.Value, shown, writer))
						{
							InlineChildren(field.ChildElements, writer, depth);
						}
						break;
					case SdtRun sdt:
						var properties = sdt.SdtProperties;
						var tagName = properties?.GetFirstChild<Tag>()?.Val?.Value ?? properties?.GetFirstChild<SdtAlias>()?.Val?.Value;
						if (ToFieldPath(tagName) is { } tagPath)
						{
							DataField(tagPath, null, sdt.SdtContentRun?.Descendants<RunProperties>().FirstOrDefault(), writer);
						}
						else if (sdt.SdtContentRun is { } content)
						{
							InlineChildren(content.ChildElements, writer, depth);
						}
						break;
					case InsertedRun or MoveToRun:
						_trackedChanges = true;
						InlineChildren(child.ChildElements, writer, depth);
						break;
					case DeletedRun or MoveFromRun:
						_trackedChanges = true;
						break;
					case CustomXmlRun:
						InlineChildren(child.ChildElements, writer, depth);
						break;
				}
			}
		}

		private void WriteLink(Hyperlink link, InlineWriter writer, int depth)
		{
			var href = link.Id?.Value is { } id ? _part.HyperlinkRelationships.FirstOrDefault(r => r.Id == id)?.Uri : null;
			var safe = href is { IsAbsoluteUri: true } && href.Scheme is "http" or "https" or "mailto";
			if (!safe)
			{
				if (href is not null) _unsafeLinks++;
				InlineChildren(link.ChildElements, writer, depth);
				return;
			}
			Counts["links"]++;
			writer.Flush();
			writer.Html.Append("<a href=\"").Append(EncodeAttribute(href!.AbsoluteUri)).Append("\">");
			InlineChildren(link.ChildElements, writer, depth);
			writer.Flush();
			writer.Html.Append("</a>");
		}

		private void DataField(string path, string? format, RunProperties? shown, InlineWriter writer)
		{
			Counts["fields"]++;
			if (!_fields.Contains(path))
			{
				_fields.Add(path);
				_fieldFormats[path] = format;
			}
			writer.Flush();
			// The field prints with the formatting of the text it replaces (e.g. a bold «InsuredName»).
			var (open, close) = RunFormat(shown);
			writer.Html.Append(open).Append("<span class=\"df\" data-field=\"").Append(path).Append('"');
			if (format is not null) writer.Html.Append(" data-format=\"").Append(format).Append('"');
			writer.Html.Append(">{{ ").Append(path);
			if (format is not null) writer.Html.Append(" | ").Append(format);
			writer.Html.Append(" }}</span>").Append(close);
			writer.HasContent = true;
		}

		private bool InFieldCodeOrMergeResult => _fieldStack.Any(f => !f.InResult || f.Replaced);

		/// <summary>
		/// Writes what replaces a field's displayed result: a Data Field for MERGEFIELD, and in headers/footers the
		/// renderer's page number / page count for PAGE and NUMPAGES. False for other fields (their result is kept).
		/// </summary>
		private bool ReplaceField(string? instruction, RunProperties? shown, InlineWriter writer)
		{
			if (MergeFieldName(instruction) is { } name && ToFieldPath(name) is { } path)
			{
				DataField(path, MergeFieldFormat(instruction), shown, writer);
				return true;
			}
			if (_inHeaderFooter && instruction is not null && PageField().Match(instruction) is { Success: true } page)
			{
				Counts["pageNumbers"]++;
				writer.Flush();
				writer.Html.Append(page.Groups[1].Value.Equals("PAGE", StringComparison.OrdinalIgnoreCase)
					? "<span class=\"gd-pageno\">1</span>"
					: "<span class=\"gd-pagecount\">1</span>");
				writer.HasContent = true;
				return true;
			}
			return false;
		}

		private void WriteRun(Run run, InlineWriter writer)
		{
			var (open, close) = RunFormat(run.RunProperties);
			foreach (var child in run.ChildElements)
			{
				switch (child)
				{
					case FieldChar fieldChar:
						OnFieldChar(fieldChar, run.RunProperties, writer);
						break;
					case FieldCode code:
						if (_fieldStack.Count > 0 && !_fieldStack[^1].InResult) _fieldStack[^1].Instruction.Append(code.Text);
						break;
					case Text text when !InFieldCodeOrMergeResult:
						if (text.Text.Length > 0) writer.Write(open, close, Encode(text.Text));
						break;
					case TabChar when !InFieldCodeOrMergeResult:
						writer.Write(open, close, "&emsp;");
						break;
					case NoBreakHyphen when !InFieldCodeOrMergeResult:
						writer.Write(open, close, "&#8209;");
						break;
					case Break br when !InFieldCodeOrMergeResult:
						switch (br.Type?.InnerText)
						{
							case "page":
								if (writer.HasContent) writer.BreakAfter = true; else writer.BreakBefore = true;
								break;
							case "column":
								break;
							default:
								writer.Flush();
								writer.Html.Append("<br>");
								break;
						}
						break;
					case CarriageReturn when !InFieldCodeOrMergeResult:
						writer.Flush();
						writer.Html.Append("<br>");
						break;
					case Drawing drawing when !InFieldCodeOrMergeResult:
						WriteDrawing(drawing, writer);
						break;
					case Picture picture when !InFieldCodeOrMergeResult:
						WritePicture(picture, writer);
						break;
					case AlternateContent alternate when !InFieldCodeOrMergeResult:
						// Newer markup (Choice) is preferred; the VML Fallback is used only when Choice has no drawing.
						var choice = alternate.GetFirstChild<AlternateContentChoice>();
						var chosen = choice is not null && choice.Descendants<Drawing>().Any()
							? choice
							: (OpenXmlElement?)alternate.GetFirstChild<AlternateContentFallback>();
						if (chosen is not null)
						{
							foreach (var inner in chosen.Descendants().Where(e => e is Drawing or Picture))
							{
								if (inner is Drawing d) WriteDrawing(d, writer); else WritePicture((Picture)inner, writer);
							}
						}
						break;
				}
			}
		}

		private void OnFieldChar(FieldChar fieldChar, RunProperties? runProperties, InlineWriter writer)
		{
			switch (fieldChar.FieldCharType?.InnerText)
			{
				case "begin":
					if (_fieldStack.Count < 32) _fieldStack.Add(new FieldState { Shown = runProperties });
					break;
				case "separate" when _fieldStack.Count > 0:
					var current = _fieldStack[^1];
					current.InResult = true;
					// The cached result of a merge field is «Name»; the Data Field replaces it.
					if (!_fieldStack.Take(_fieldStack.Count - 1).Any(f => !f.InResult || f.Replaced) &&
						ReplaceField(current.Instruction.ToString(), current.Shown, writer))
					{
						current.Replaced = true;
					}
					break;
				case "end" when _fieldStack.Count > 0:
					var ended = _fieldStack[^1];
					_fieldStack.RemoveAt(_fieldStack.Count - 1);
					// A field without a separate has no cached result.
					if (!ended.InResult && !InFieldCodeOrMergeResult)
					{
						ReplaceField(ended.Instruction.ToString(), ended.Shown, writer);
					}
					break;
			}
		}

		private sealed class FieldState
		{
			public StringBuilder Instruction { get; } = new();
			public RunProperties? Shown { get; init; }
			public bool InResult { get; set; }
			public bool Replaced { get; set; }
		}

		private static (string Open, string Close) RunFormat(RunProperties? properties)
		{
			if (properties is null) return (string.Empty, string.Empty);
			var open = new StringBuilder();
			var close = new StringBuilder();
			void Wrap(string tag)
			{
				open.Append('<').Append(tag).Append('>');
				close.Insert(0, "</" + tag + ">");
			}

			if (On(properties.Bold)) Wrap("strong");
			if (On(properties.Italic)) Wrap("em");
			if (properties.Underline?.Val?.InnerText is { } underline && underline != "none") Wrap("u");
			if (On(properties.Strike) || On(properties.DoubleStrike)) Wrap("s");
			switch (properties.VerticalTextAlignment?.Val?.InnerText)
			{
				case "superscript": Wrap("sup"); break;
				case "subscript": Wrap("sub"); break;
			}

			var style = new StringBuilder();
			if (HexColor(properties.Color?.Val?.Value) is { } color) style.Append("color:").Append(color).Append(';');
			if (int.TryParse(properties.FontSize?.Val?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var halfPoints) && halfPoints > 0)
			{
				style.Append("font-size:").Append(Pt(Math.Clamp(halfPoints / 2.0, 4, 96))).Append(';');
			}
			var highlight = properties.Highlight?.Val?.InnerText is { } h && HighlightColors.TryGetValue(h, out var named) ? named
				: HexColor(properties.Shading?.Fill?.Value);
			if (highlight is not null) style.Append("background:").Append(highlight).Append(';');
			if (On(properties.Caps)) style.Append("text-transform:uppercase;");
			if (style.Length > 0)
			{
				open.Append("<span style=\"").Append(style).Append("\">");
				close.Insert(0, "</span>");
			}
			return (open.ToString(), close.ToString());
		}

		// ---- Images and text boxes -------------------------------------------------------------------------------

		private void WriteDrawing(Drawing drawing, InlineWriter writer)
		{
			if (drawing.Descendants<A.Blip>().FirstOrDefault()?.Embed?.Value is { } id)
			{
				if (drawing.GetFirstChild<DW.Anchor>() is not null) _floatingImages++;
				var extent = drawing.Descendants<DW.Extent>().FirstOrDefault();
				var alt = drawing.Descendants<DW.DocProperties>().FirstOrDefault()?.Description?.Value;
				WriteImage(id, extent?.Cx?.Value / EmuPerPx, alt, writer);
				return;
			}
			foreach (var box in drawing.Descendants<TextBoxContent>())
			{
				writer.TextBoxes.Add(box);
			}
		}

		// Legacy VML picture (older documents / Fallback markup).
		private void WritePicture(Picture picture, InlineWriter writer)
		{
			if (picture.Descendants<V.ImageData>().FirstOrDefault()?.RelationshipId?.Value is { } id)
			{
				WriteImage(id, null, picture.Descendants<V.ImageData>().First().Title?.Value, writer);
				return;
			}
			foreach (var box in picture.Descendants<TextBoxContent>())
			{
				writer.TextBoxes.Add(box);
			}
		}

		private void WriteImage(string relationshipId, double? widthPx, string? alt, InlineWriter writer)
		{
			var part = _part.TryGetPartById(relationshipId, out var found) ? found as ImagePart : null;
			var extension = part?.ContentType switch
			{
				"image/png" => "png",
				"image/jpeg" or "image/jpg" => "jpg",
				"image/gif" => "gif",
				_ => null
			};
			byte[]? bytes = null;
			if (part is not null && extension is not null)
			{
				using var stream = part.GetStream(FileMode.Open, FileAccess.Read);
				using var buffer = new MemoryStream();
				stream.CopyTo(buffer);
				bytes = buffer.Length <= MaxImageBytes ? buffer.ToArray() : null;
			}
			// The asset is served with an image content type, so the bytes must really be that image.
			if (bytes is null || !MatchesSignature(bytes, extension!))
			{
				_unsupportedImages++;
				return;
			}

			Counts["images"]++;
			var file = assets.Save(bytes, extension!);
			writer.Flush();
			writer.Html.Append("<img src=\"").Append(LegacyAssetStore.UrlPrefix).Append(file).Append("\" alt=\"").Append(EncodeAttribute(alt ?? string.Empty)).Append('"');
			var style = "max-width:100%;height:auto;";
			if (widthPx is > 0) style = "width:" + Math.Round(widthPx.Value, 1).ToString(CultureInfo.InvariantCulture) + "px;" + style;
			writer.Html.Append(" style=\"").Append(style).Append("\">");
			writer.HasContent = true;
		}

		private static bool MatchesSignature(byte[] bytes, string extension) => extension switch
		{
			"png" => bytes.AsSpan().StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }),
			"jpg" => bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xD8, 0xFF }),
			"gif" => bytes.AsSpan().StartsWith("GIF8"u8),
			_ => false
		};

		private sealed class InlineWriter
		{
			private string? _open;
			private string _close = string.Empty;

			public StringBuilder Html { get; } = new();
			public List<TextBoxContent> TextBoxes { get; } = [];
			public bool HasContent { get; set; }
			public bool BreakBefore { get; set; }
			public bool BreakAfter { get; set; }

			/// <summary>Writes encoded text; consecutive runs with the same formatting share one set of tags.</summary>
			public void Write(string open, string close, string encoded)
			{
				if (_open != open)
				{
					Flush();
					Html.Append(open);
					_open = open;
					_close = close;
				}
				// Runs are encoded one at a time: "{" ending one run and "{" starting the next must not meet.
				if (Html.Length > 0 && Html[^1] == '{' && encoded.Length > 0 && encoded[0] is '{' or '%')
				{
					Html.Append(DocumentImport.LiquidBreak);
				}
				Html.Append(encoded);
				HasContent = true;
			}

			public void Flush()
			{
				if (_open is null) return;
				Html.Append(_close);
				_open = null;
			}
		}

		// ---- Document-level notes and model ----------------------------------------------------------------------

		public void AddDocumentNotes(bool runningHeaderFooter)
		{
			if (!runningHeaderFooter && main.HeaderParts.Concat<OpenXmlPart>(main.FooterParts).Any(p => p.RootElement?.InnerText.Trim().Length > 0))
			{
				_notes.Add("Page headers and footers were not imported (the document's last section doesn't use them); documents print with the MOE footer.");
			}
			if (main.FootnotesPart?.Footnotes?.Elements<Footnote>().Count(f => f.Id?.Value > 0) > 0 ||
				main.EndnotesPart?.Endnotes?.Elements<Endnote>().Count(e => e.Id?.Value > 0) > 0)
			{
				_notes.Add("Footnotes and endnotes were not imported.");
			}
			if (main.WordprocessingCommentsPart?.Comments?.HasChildren == true)
			{
				_notes.Add("Comments were not imported.");
			}
			if (main.Document?.Body?.Descendants<SectionProperties>().Any(s => s.GetFirstChild<Columns>()?.ColumnCount?.Value > 1) == true)
			{
				_notes.Add("Multi-column sections were imported as a single column.");
			}
		}

		// ---- Headers and footers ---------------------------------------------------------------------------------

		/// <summary>
		/// The default header and footer of the document's last section (Word applies it to the whole document when there
		/// is one section), converted like the body. Null when the section has none or it is blank.
		/// </summary>
		public (string? Header, string? Footer) HeaderAndFooter(SectionProperties? section)
		{
			if (section is null) return (null, null);

			var header = Convert(section.Elements<HeaderReference>(), "headers");
			var footer = Convert(section.Elements<FooterReference>(), "footers");
			if (header is null && footer is null) return (null, null);

			if (On(section.GetFirstChild<TitlePage>()))
			{
				_notes.Add("The first page's own header/footer was not imported; the main header/footer prints on every page.");
			}
			if (On(main.DocumentSettingsPart?.Settings?.GetFirstChild<EvenAndOddHeaders>()))
			{
				_notes.Add("Separate even-page headers/footers were not imported; the odd-page ones print on every page.");
			}
			if (main.Document?.Body?.Descendants<SectionProperties>().Count() > 1)
			{
				_notes.Add("The document has several sections; the last section's header/footer prints on every page.");
			}
			return (header, footer);

			string? Convert(IEnumerable<HeaderFooterReferenceType> references, string count)
			{
				var reference = references.FirstOrDefault(r => r.Type?.InnerText is null or "default");
				if (reference?.Id?.Value is not { } id || !main.TryGetPartById(id, out var part) || part.RootElement is not { } root) return null;
				// A header of only empty paragraphs is dropped (Word's own blank default).
				if (!root.Descendants<Text>().Any(t => t.Text.Trim().Length > 0) && !root.Descendants<Drawing>().Any() &&
					!root.Descendants<Picture>().Any() && !root.Descendants<FieldCode>().Any() && !root.Descendants<SimpleField>().Any())
				{
					return null;
				}

				_part = part;
				_inHeaderFooter = true;
				_fieldStack.Clear();
				try
				{
					ApplyPlaceholders(root);
					var html = new StringBuilder();
					Blocks(root.ChildElements, html, 0);
					Counts[count]++;
					return html.ToString();
				}
				finally
				{
					_part = main;
					_inHeaderFooter = false;
					_fieldStack.Clear();
				}
			}
		}

		/// <summary>Sample data for the merge fields: nested objects for dotted paths, "«Name»" placeholder values.</summary>
		public Dictionary<string, object?> BuildModel()
		{
			var model = new Dictionary<string, object?>();
			foreach (var path in _fields)
			{
				var segments = path.Split('.');
				var node = model;
				var ok = true;
				for (var i = 0; i < segments.Length - 1 && ok; i++)
				{
					if (!node.TryGetValue(segments[i], out var next))
					{
						next = new Dictionary<string, object?>();
						node[segments[i]] = next;
					}
					if (next is Dictionary<string, object?> child) node = child;
					else ok = false;
				}
				if (ok && !node.ContainsKey(segments[^1]))
				{
					// Numbers and dates for formatted fields, so the preview shows the format working.
					node[segments[^1]] = _fieldFormats.GetValueOrDefault(path) switch
					{
						"currency" or "dollars" or "number" or "decimal" => 1234.5m,
						"percent" => 0.125m,
						"shortdate" => "2026-01-15",
						_ => "\u00AB" + segments[^1] + "\u00BB"
					};
				}
				else if (!ok || node[segments[^1]] is Dictionary<string, object?>)
				{
					_notes.Add($"Merge field '{path}' clashes with another field's path; its sample value was not added to the model.");
				}
			}
			return model;
		}
	}

	[GeneratedRegex(@"^\s*MERGEFIELD\s+(?:""(?<name>[^""]+)""|(?<name>[^\s\\""]+))", RegexOptions.IgnoreCase)]
	private static partial Regex MergeField();

	[GeneratedRegex(@"^\s*(PAGE|NUMPAGES|SECTIONPAGES)\b", RegexOptions.IgnoreCase)]
	private static partial Regex PageField();

	[GeneratedRegex(@"\\\*\s*Upper\b", RegexOptions.IgnoreCase)]
	private static partial Regex UpperSwitch();

	[GeneratedRegex(@"\\@")]
	private static partial Regex DateSwitch();

	[GeneratedRegex(@"\\#\s*(""[^""]*""|\S+)")]
	private static partial Regex NumberSwitch();

	[GeneratedRegex(@"[^A-Za-z0-9_]+")]
	private static partial Regex InvalidPathChars();

	[GeneratedRegex(@"^[0-9A-Fa-f]{6}$")]
	private static partial Regex HexPattern();

	[GeneratedRegex(@"^heading\s*([1-9])$", RegexOptions.IgnoreCase)]
	private static partial Regex HeadingName();

	[GeneratedRegex(@"  +")]
	private static partial Regex MultiSpace();
}
