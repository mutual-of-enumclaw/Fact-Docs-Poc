using System.Globalization;
using System.Net;
using System.Text;
using FaCT.DocDesigner.POC.Legacy;
using UglyToad.PdfPig;
using UglyToad.PdfPig.AcroForms;
using UglyToad.PdfPig.AcroForms.Fields;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Exceptions;
using UglyToad.PdfPig.Graphics.Colors;

namespace FaCT.DocDesigner.POC.Import;

/// <summary>Pages to import (1-based, inclusive); both null = the whole document.</summary>
public sealed record PdfImportOptions(int? FirstPage = null, int? LastPage = null)
{
	public static readonly PdfImportOptions All = new();

	/// <summary>Parses the import endpoint's ?pages= value: "12" or "5-40" (empty = all pages).</summary>
	public static bool TryParsePages(string? value, out PdfImportOptions options)
	{
		options = All;
		if (string.IsNullOrWhiteSpace(value)) return true;
		var parts = value.Split('-', StringSplitOptions.TrimEntries);
		if (parts.Length > 2 || parts.Any(p => !int.TryParse(p, System.Globalization.NumberStyles.None, CultureInfo.InvariantCulture, out _)))
		{
			return false;
		}
		var first = int.Parse(parts[0], CultureInfo.InvariantCulture);
		options = new PdfImportOptions(first, parts.Length == 2 ? int.Parse(parts[1], CultureInfo.InvariantCulture) : first);
		return true;
	}
}

/// <summary>A PDF longer than <see cref="PdfImporter.MaxPages"/> was sent without a page range.</summary>
public sealed class PdfTooLongException(int pageCount)
	: DocumentImportException($"The PDF has {pageCount} pages; at most {PdfImporter.MaxPages} can be imported at once. Choose a page range.")
{
	public int PageCount { get; } = pageCount;
}

/// <summary>
/// Converts a PDF into fixed Letter form pages (the same layout as imported Documaker forms): each line of text, rule,
/// box, shading, image and AcroForm fill-in field at its position on the page. Text is not reflowed.
/// The page HTML is written in the fact-pdf-tools "emit-html" shape and passed through the
/// <see cref="LegacyFormImporter"/>, so PDFs get the same allow-list, pt=>px conversion and asset store as legacy forms.
/// Fonts are not embedded; each run gets the closest standard family (sans, serif or mono) with its weight and style.
/// </summary>
public sealed class PdfImporter(LegacyFormImporter legacy)
{
	public const int MaxPages = 100;

	private const double LetterWidth = 612, LetterHeight = 792;
	// With line-height equal to the font size, a line box's baseline sits ~0.845em below its top for common fonts.
	private const double BaselineFromTop = 0.845;
	private const double ThinPt = 2.5;

	public DocumentImportResult Import(byte[] bytes, PdfImportOptions? options = null)
	{
		options ??= PdfImportOptions.All;
		PdfDocument document;
		try
		{
			document = PdfDocument.Open(bytes, new ParsingOptions { UseLenientParsing = true, SkipMissingFonts = true });
		}
		catch (PdfDocumentEncryptedException ex)
		{
			throw new DocumentImportException("The PDF is password protected. Remove the password and import it again.", ex);
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			throw new DocumentImportException("The file could not be read as a PDF.", ex);
		}

		using (document)
		{
			var total = document.NumberOfPages;
			var ranged = options.FirstPage is not null || options.LastPage is not null;
			var firstPage = options.FirstPage ?? 1;
			var lastPage = options.LastPage ?? total;
			if (!ranged && total > MaxPages)
			{
				throw new PdfTooLongException(total);
			}
			if (firstPage < 1 || lastPage < firstPage || lastPage > total)
			{
				throw new DocumentImportException($"Pages {firstPage}-{lastPage} are not in the PDF; it has {total} page(s).");
			}
			if (lastPage - firstPage + 1 > MaxPages)
			{
				throw new DocumentImportException($"At most {MaxPages} pages can be imported at once; pages {firstPage}-{lastPage} are {lastPage - firstPage + 1}.");
			}

			var notes = new List<string>();
			if (ranged) notes.Add($"Imported pages {firstPage}-{lastPage} of {total}.");
			var scaled = new List<int>();
			var rotated = new List<int>();
			var scanned = new List<int>();
			var unreadable = new List<int>();
			int verticalText = 0, curves = 0, unsupportedImages = 0, buttons = 0;
			var fieldNames = new List<string>();

			AcroForm? form = null;
			var fieldsByPage = new Dictionary<int, List<FormField>>();
			try
			{
				if (document.TryGetForm(out var f))
				{
					form = f;
					foreach (var field in FormFields(f))
					{
						if (field.Field.PageNumber is not { } page) continue;
						if (!fieldsByPage.TryGetValue(page, out var list)) fieldsByPage[page] = list = [];
						list.Add(field);
					}
				}
			}
			catch (Exception ex) when (ex is not OutOfMemoryException)
			{
				notes.Add("The PDF's form fields could not be read; the pages were imported without them.");
			}
			if (HasXfa(document))
			{
				notes.Add(fieldsByPage.Count > 0
					? "The PDF also carries an XFA (Adobe LiveCycle) form; its standard form fields were imported."
					: "The PDF's form is XFA only (a dynamic Adobe LiveCycle form), so its fields can't be imported: save it from Acrobat as a standard fillable PDF first.");
			}

			var html = new StringBuilder("<!DOCTYPE html><html><body>");
			var partial = new SortedDictionary<string, List<int>>(StringComparer.Ordinal);
			var embedded = new EmbeddedFonts(document);
			for (var number = firstPage; number <= lastPage; number++)
			{
				html.Append("<section class=\"form-page\">");
				Page page;
				try
				{
					page = document.GetPage(number);
				}
				catch (Exception ex) when (ex is not OutOfMemoryException)
				{
					unreadable.Add(number);
					html.Append("</section>");
					continue;
				}

				// Each part is read on its own: content PdfPig can't parse costs that part of the page, not the page.
				var pageNumber = number;
				bool Read(string part, Action step)
				{
					try
					{
						step();
						return true;
					}
					catch (Exception ex) when (ex is not OutOfMemoryException)
					{
						if (!partial.TryGetValue(part, out var pages)) partial[part] = pages = [];
						pages.Add(pageNumber);
						return false;
					}
				}

				var result = new PageWriter(page, html, embedded);
				if (result.Scale < 0.999) scaled.Add(number);
				if (page.Rotation.Value != 0) rotated.Add(number);

				int images = 0, texts = 0;
				Read("images", () => images = result.Images(ref unsupportedImages));
				Read("lines and shapes", () => result.Shapes(ref curves));
				// Without the page's fonts the text still imports, in standard families.
				try { embedded.Collect(page); }
				catch (Exception ex) when (ex is not OutOfMemoryException) { /* standard families */ }
				var textRead = Read("text", () => texts = result.Text(ref verticalText));
				if (textRead && texts == 0 && images > 0) scanned.Add(number);
				if (form is not null && fieldsByPage.TryGetValue(pageNumber, out var pageFields))
				{
					Read("form fields", () =>
					{
						foreach (var name in result.Fields(pageFields, ref buttons))
						{
							if (!fieldNames.Contains(name)) fieldNames.Add(name);
						}
					});
				}
				html.Append("</section>");
			}
			// The fonts the text uses, in the emit-html shape the legacy importer registers in the asset store.
			html.Append(embedded.FaceCss());
			html.Append("</body></html>");

			var imported = legacy.Import(html.ToString());

			if (embedded.Used > 0) notes.Add($"{embedded.Used} font(s) embedded in the PDF are used for its text.");
			if (embedded.Rejected > 0) notes.Add($"{embedded.Rejected} embedded font(s) can't be used in a browser (no Unicode character map, or characters missing) and were replaced by a standard family.");
			if (imported.MissingFonts.Count > 0) notes.Add($"Font(s) {string.Join(", ", imported.MissingFonts)} could not be stored and were replaced by a standard family.");

			if (scaled.Count > 0) notes.Add($"Page(s) {Pages(scaled)} are larger than Letter and were scaled to fit.");
			if (rotated.Count > 0) notes.Add($"Page(s) {Pages(rotated)} are rotated in the PDF and were imported unrotated.");
			if (scanned.Count > 0) notes.Add($"Page(s) {Pages(scanned)} have no text layer (scanned?). Their text is part of an image; OCR is not supported.");
			if (unreadable.Count > 0) notes.Add($"Page(s) {Pages(unreadable)} could not be read and were imported blank.");
			foreach (var (part, pages) in partial)
			{
				notes.Add($"The {part} on page(s) {Pages(pages)} could not be read and are missing.");
			}
			if (verticalText > 0) notes.Add($"{verticalText} character(s) of rotated or vertical text were skipped.");
			if (curves > 0) notes.Add($"{curves} curved or diagonal shape(s) (circles, logos drawn as vectors) were skipped.");
			if (unsupportedImages > 0) notes.Add($"{unsupportedImages} image(s) in an encoding that can't be converted were skipped.");
			if (buttons > 0) notes.Add($"{buttons} signature/button field(s) were skipped.");
			if (imported.CheckBoxes > 0) notes.Add($"{imported.CheckBoxes} check box(es) print an X when the data they are mapped to says so (Checked when, in the field's settings).");
			if (imported.Fields > 0) notes.Add("Form fields are not mapped yet: use Suggest mappings in the Model panel, or select each one and choose a property.");

			return new DocumentImportResult(
				"pdf",
				imported.Html,
				string.Empty,
				imported.Pages,
				new Dictionary<string, int>
				{
					["texts"] = imported.Texts,
					["fields"] = imported.Fields,
					["checkboxes"] = imported.CheckBoxes,
					["shapes"] = imported.Shapes,
					["images"] = imported.Images,
					["skipped"] = imported.Skipped
				},
				fieldNames,
				null,
				notes);
		}
	}

	private static string Pages(List<int> pages) => string.Join(", ", pages);

	/// <summary>A fill-in field (a terminal field of the form) and the name it is imported under.</summary>
	public sealed record FormField(AcroFieldBase Field, string Name);

	/// <summary>
	/// Every fill-in field of the form, including those nested under parent fields: forms made with Adobe LiveCycle
	/// (IRS forms, for example) keep all their fields under one "topmostSubform[0]" parent. A field is named by its own
	/// name ("f1_01[0]"), or by its path below the top parent when that name is used more than once.
	/// </summary>
	public static IReadOnlyList<FormField> FormFields(AcroForm form)
	{
		var found = new List<(AcroFieldBase Field, List<string> Path)>();
		void Walk(AcroFieldBase field, List<string> path, int depth)
		{
			var own = field.Information?.PartialName;
			var here = string.IsNullOrEmpty(own) ? path : [.. path, own];
			if (field is AcroNonTerminalField parent && depth < 32)
			{
				foreach (var child in parent.Children) Walk(child, here, depth + 1);
			}
			else
			{
				found.Add((field, here));
			}
		}
		foreach (var field in form.Fields) Walk(field, [], 0);

		var counts = found.Where(f => f.Path.Count > 0).GroupBy(f => f.Path[^1]).ToDictionary(g => g.Key, g => g.Count());
		var index = 0;
		return found.Select(f =>
		{
			index++;
			if (f.Path.Count == 0) return new FormField(f.Field, "field" + index);
			var name = f.Path[^1];
			// a repeated name is told apart by the parents below the top one ("Page2[0].f1_01[0]")
			if (counts[name] > 1) name = string.Join(".", f.Path.Skip(f.Path.Count > 1 ? 1 : 0));
			return new FormField(f.Field, name);
		}).ToList();
	}

	/// <summary>Whether the PDF has an XFA (Adobe LiveCycle) form description next to (or instead of) its AcroForm.</summary>
	private static bool HasXfa(PdfDocument document)
	{
		try
		{
			var catalog = document.Structure.Catalog.CatalogDictionary;
			if (!catalog.TryGet(UglyToad.PdfPig.Tokens.NameToken.Create("AcroForm"), out var token)) return false;
			var acroForm = token is UglyToad.PdfPig.Tokens.IndirectReferenceToken reference
				? document.Structure.GetObject(reference.Data).Data as UglyToad.PdfPig.Tokens.DictionaryToken
				: token as UglyToad.PdfPig.Tokens.DictionaryToken;
			return acroForm?.ContainsKey(UglyToad.PdfPig.Tokens.NameToken.Create("XFA")) == true;
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			return false;
		}
	}

	/// <summary>Closest standard family, and bold/italic, from a PDF font name such as "ABCDEF+Arial-BoldItalicMT".</summary>
	public static (string Family, bool Bold, bool Italic) MapFont(string? fontName)
	{
		var name = fontName ?? string.Empty;
		if (name.Length > 7 && name[6] == '+') name = name[7..];
		var lower = name.ToLowerInvariant();
		var bold = lower.Contains("bold") || lower.Contains("black") || lower.Contains("heavy") || lower.Contains("demi");
		var italic = lower.Contains("italic") || lower.Contains("oblique");
		var family =
			lower.Contains("courier") || lower.Contains("mono") || lower.Contains("consol") ? LegacyFormImporter.MonoFontStack
			: !lower.Contains("sans") && (lower.Contains("times") || lower.Contains("serif") || lower.Contains("georgia") || lower.Contains("garamond") ||
				lower.Contains("cambria") || lower.Contains("minion") || lower.Contains("palatino") || lower.Contains("bookman") || lower.Contains("century"))
				? LegacyFormImporter.SerifFontStack
			: LegacyFormImporter.SansFontStack;
		return (family, bold, italic);
	}

	/// <summary>Writes one page's elements in PDF points, top-left origin, scaled to fit a Letter sheet.</summary>
	private sealed class PageWriter
	{
		private readonly Page _page;
		private readonly StringBuilder _html;
		private readonly PdfRectangle _box;
		private readonly EmbeddedFonts? _fonts;
		private Dictionary<string, string?> _families = [];
		// the page's text as written, for the labels of form fields
		private readonly List<TextRun> _runs = [];

		public PageWriter(Page page, StringBuilder html, EmbeddedFonts? fonts = null)
		{
			_page = page;
			_html = html;
			_fonts = fonts;
			_box = page.CropBox.Bounds;
			var width = _box.Width > 0 ? _box.Width : LetterWidth;
			var height = _box.Height > 0 ? _box.Height : LetterHeight;
			Scale = Math.Min(1, Math.Min(LetterWidth / width, LetterHeight / height));
		}

		public double Scale { get; }

		private double X(double x) => (x - _box.Left) * Scale;
		private double Y(double y) => (_box.Top - y) * Scale;

		// ---- Text ------------------------------------------------------------------------------------------------

		public int Text(ref int vertical)
		{
			var letters = new List<Letter>();
			foreach (var letter in _page.Letters)
			{
				if (string.IsNullOrWhiteSpace(letter.Value)) continue;
				if (letter.TextOrientation != TextOrientation.Horizontal) { vertical++; continue; }
				if (Size(letter) <= 0) continue;
				letters.Add(letter);
			}

			var runs = 0;
			// Each font's embedded program, if it covers everything the font prints on this page.
			_families = letters.GroupBy(l => l.FontName ?? string.Empty).ToDictionary(
				g => g.Key, g => _fonts?.Resolve(g.Key, string.Concat(g.Select(l => l.Value))));
			foreach (var line in Lines(letters))
			{
				var run = new List<Letter>();
				foreach (var letter in line)
				{
					if (run.Count > 0 && StartsNewRun(run[^1], letter))
					{
						WriteRun(run);
						runs++;
						run.Clear();
					}
					run.Add(letter);
				}
				if (run.Count > 0)
				{
					WriteRun(run);
					runs++;
				}
			}
			return runs;
		}

		private static double Size(Letter letter) => letter.PointSize > 0 ? letter.PointSize : letter.FontSize;

		/// <summary>Letters grouped into lines (top to bottom), each ordered left to right.</summary>
		private static IEnumerable<List<Letter>> Lines(List<Letter> letters)
		{
			var lines = new List<(double Baseline, List<Letter> Letters)>();
			foreach (var letter in letters.OrderByDescending(l => l.StartBaseLine.Y).ThenBy(l => l.StartBaseLine.X))
			{
				var tolerance = Math.Max(0.5, Size(letter) * 0.2);
				if (lines.Count > 0 && Math.Abs(lines[^1].Baseline - letter.StartBaseLine.Y) <= tolerance)
				{
					lines[^1].Letters.Add(letter);
				}
				else
				{
					lines.Add((letter.StartBaseLine.Y, [letter]));
				}
			}
			return lines.Select(l => l.Letters.OrderBy(x => x.StartBaseLine.X).ToList());
		}

		private static bool StartsNewRun(Letter previous, Letter next)
		{
			if (StyleKey(previous) != StyleKey(next)) return true;
			var gap = next.StartBaseLine.X - previous.EndBaseLine.X;
			var size = Size(next);
			// A wide gap is a new column or table cell; a backwards step is overprinted text.
			return gap > size * 2.0 || gap < -size * 0.5;
		}

		private static string StyleKey(Letter letter) =>
			letter.FontName + "|" + Math.Round(Size(letter), 1).ToString(CultureInfo.InvariantCulture) + "|" + ColorOf(letter.Color);

		private void WriteRun(List<Letter> run)
		{
			var first = run[0];
			var text = new StringBuilder(first.Value);
			for (var i = 1; i < run.Count; i++)
			{
				var gap = run[i].StartBaseLine.X - run[i - 1].EndBaseLine.X;
				if (gap > Size(run[i]) * 0.15) text.Append(' ');
				text.Append(run[i].Value);
			}

			var size = Size(first) * Scale;
			_runs.Add(new TextRun(Printable(text.ToString()), first.StartBaseLine.X, run[^1].EndBaseLine.X,
				first.StartBaseLine.Y - Size(first) * 0.2, first.StartBaseLine.Y + Size(first) * 0.8));
			var (family, bold, italic) = MapFont(first.FontName);
			if (_families.GetValueOrDefault(first.FontName ?? string.Empty) is { } embedded) family = "'" + embedded + "', " + family;
			_html.Append("<span class=\"abs\" style=\"left:").Append(Pt(X(first.StartBaseLine.X)))
				.Append(";top:").Append(Pt(Y(first.StartBaseLine.Y) - size * BaselineFromTop))
				.Append(";font-size:").Append(Pt(size))
				.Append(";line-height:").Append(Pt(size))
				.Append(";font-family:").Append(family);
			if (bold) _html.Append(";font-weight:bold");
			if (italic) _html.Append(";font-style:italic");
			if (ColorOf(first.Color) is { } color) _html.Append(";color:").Append(color);
			_html.Append("\">").Append(WebUtility.HtmlEncode(Printable(text.ToString()))).Append("</span>");
		}

		private static string Printable(string text) => string.Concat(text.Where(c => !char.IsControl(c)));

		// ---- Shapes ----------------------------------------------------------------------------------------------

		public void Shapes(ref int skipped)
		{
			foreach (var path in _page.Paths)
			{
				if (path.IsClipping || !(path.IsFilled || path.IsStroked)) continue;
				var lineWidth = Math.Max(path.LineWidth, 0.25) * Scale;
				foreach (var subpath in path)
				{
					var curved = subpath.Commands.Any(c => c is PdfSubpath.BezierCurve);
					var bounds = subpath.GetBoundingRectangle();
					if (bounds is not { } b) continue;
					double left = X(b.Left), top = Y(b.Top), width = b.Width * Scale, height = b.Height * Scale;

					if (curved)
					{
						// Small filled round shapes are list bullets; other curves can't be drawn with boxes.
						if (path.IsFilled && width is > 0.5 and <= 10 && height is > 0.5 and <= 10)
						{
							Shape("bullet", left, top, width, height, ColorOf(path.FillColor));
						}
						else
						{
							skipped++;
						}
						continue;
					}

					if (path.IsFilled && !IsWhite(path.FillColor) && (width > 0.1 || height > 0.1))
					{
						var fill = ColorOf(path.FillColor) ?? "rgb(0, 0, 0)";
						if (Math.Min(width, height) <= ThinPt) Shape("rule", left, top, Math.Max(width, 0.5), Math.Max(height, 0.5), fill);
						else Shape("shade", left, top, width, height, fill);
					}

					if (path.IsStroked && !IsWhite(path.StrokeColor))
					{
						var stroke = ColorOf(path.StrokeColor);
						if (subpath.IsDrawnAsRectangle && width > ThinPt && height > ThinPt)
						{
							_html.Append("<div class=\"box\" style=\"left:").Append(Pt(left)).Append(";top:").Append(Pt(top))
								.Append(";width:").Append(Pt(width)).Append(";height:").Append(Pt(height))
								.Append(";border-width:").Append(Pt(lineWidth)).Append("\"></div>");
							continue;
						}
						foreach (var line in subpath.Commands.OfType<PdfSubpath.Line>())
						{
							double x1 = X(line.From.X), y1 = Y(line.From.Y), x2 = X(line.To.X), y2 = Y(line.To.Y);
							if (Math.Abs(y1 - y2) < 0.5 && Math.Abs(x1 - x2) >= 0.5)
							{
								Shape("rule", Math.Min(x1, x2), y1 - lineWidth / 2, Math.Abs(x2 - x1), lineWidth, stroke);
							}
							else if (Math.Abs(x1 - x2) < 0.5 && Math.Abs(y1 - y2) >= 0.5)
							{
								Shape("rule", x1 - lineWidth / 2, Math.Min(y1, y2), lineWidth, Math.Abs(y2 - y1), stroke);
							}
							else if (Math.Abs(x1 - x2) >= 0.5 || Math.Abs(y1 - y2) >= 0.5)
							{
								skipped++;
							}
						}
					}
				}
			}
		}

		private void Shape(string kind, double left, double top, double width, double height, string? color)
		{
			_html.Append("<div class=\"").Append(kind).Append("\" style=\"left:").Append(Pt(left)).Append(";top:").Append(Pt(top))
				.Append(";width:").Append(Pt(width)).Append(";height:").Append(Pt(height));
			if (color is not null) _html.Append(";background:").Append(color);
			_html.Append("\"></div>");
		}

		// ---- Images ----------------------------------------------------------------------------------------------

		public int Images(ref int unsupported)
		{
			var count = 0;
			foreach (var image in _page.GetImages())
			{
				if (image.IsImageMask || ImageBytes(image) is not var (type, data))
				{
					unsupported++;
					continue;
				}

				var b = image.Bounds;
				_html.Append("<img class=\"img\" src=\"data:image/").Append(type).Append(";base64,").Append(Convert.ToBase64String(data))
					.Append("\" style=\"left:").Append(Pt(X(b.Left))).Append(";top:").Append(Pt(Y(b.Top)))
					.Append(";width:").Append(Pt(b.Width * Scale)).Append(";height:").Append(Pt(b.Height * Scale)).Append("\">");
				count++;
			}
			return count;
		}

		/// <summary>PNG for decodable images, the stream itself for JPEG (DCTDecode), otherwise null.</summary>
		private static (string Type, byte[] Data)? ImageBytes(IPdfImage image)
		{
			try
			{
				if (image.TryGetPng(out var png)) return ("png", png);
			}
			catch (Exception ex) when (ex is not OutOfMemoryException)
			{
				// Unsupported filter or colour space; a JPEG stream can still be used as-is.
			}
			var raw = image.RawMemory.Span;
			return raw.Length > 3 && raw[0] == 0xFF && raw[1] == 0xD8 && raw[2] == 0xFF ? ("jpeg", raw.ToArray()) : null;
		}

		// ---- Form fields -----------------------------------------------------------------------------------------

		public IEnumerable<string> Fields(IEnumerable<FormField> fields, ref int buttons)
		{
			var names = new List<string>();
			foreach (var (field, name) in fields)
			{
				if (field.FieldType is AcroFieldType.Signature or AcroFieldType.PushButton or AcroFieldType.Unknown)
				{
					buttons++;
					continue;
				}
				if (field.Bounds is not { } b) continue;

				names.Add(name);
				var height = b.Height * Scale;
				_html.Append("<span class=\"abs field\" data-field=\"").Append(WebUtility.HtmlEncode(name)).Append('"');
				// A check box prints a mark when the data says so (designer: legacy field with "Checked when").
				var checkBox = field.FieldType is AcroFieldType.Checkbox or AcroFieldType.Checkboxes;
				if (checkBox)
				{
					_html.Append(" data-kind=\"checkbox\"");
				}
				// The words printed next to it: how Suggest mappings matches fields with meaningless names.
				if (FieldLabels.Find(_runs, b, checkBox) is { } label)
				{
					_html.Append(" data-label=\"").Append(WebUtility.HtmlEncode(label)).Append('"');
				}
				if (field is AcroTextField { MaxLength: > 0 } text)
				{
					_html.Append(" data-maxlen=\"").Append(text.MaxLength!.Value.ToString(CultureInfo.InvariantCulture)).Append('"');
				}
				// a check box's mark fills most of its box
				_html.Append(" style=\"left:").Append(Pt(X(b.Left))).Append(";top:").Append(Pt(Y(b.Top)))
					.Append(";width:").Append(Pt(b.Width * Scale)).Append(";height:").Append(Pt(height))
					.Append(";font-size:").Append(Pt(checkBox ? Math.Clamp(height * 0.9, 5, 14) : Math.Clamp(height * 0.7, 6, 12))).Append("\"></span>");
			}
			return names;
		}

		// ---- Helpers ---------------------------------------------------------------------------------------------

		private static bool IsWhite(IColor? color)
		{
			if (color is null) return false;
			var (r, g, b) = color.ToRGBValues();
			return r > 0.98 && g > 0.98 && b > 0.98;
		}

		/// <summary>rgb() for a colour, or null for black/unknown (the form page's default).</summary>
		private static string? ColorOf(IColor? color)
		{
			if (color is null) return null;
			var (r, g, b) = color.ToRGBValues();
			if (r < 0.02 && g < 0.02 && b < 0.02) return null;
			return string.Create(CultureInfo.InvariantCulture, $"rgb({Channel(r)}, {Channel(g)}, {Channel(b)})");
		}

		private static int Channel(double value) => (int)Math.Round(Math.Clamp(value, 0, 1) * 255);

		private static string Pt(double value) =>
			Math.Round(value, 2).ToString("0.##", CultureInfo.InvariantCulture) + "pt";
	}
}
