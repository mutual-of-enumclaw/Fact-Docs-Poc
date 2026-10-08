using System.Globalization;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using Document = DocumentFormat.OpenXml.Wordprocessing.Document;
using AngleSharp.Html.Parser;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using FaCT.DocDesigner.POC.Templates;

namespace FaCT.DocDesigner.POC.Export;

public sealed record DocxExport(byte[] Docx, IReadOnlyList<string> Warnings);

/// <summary>
/// Turns a designer template (HTML + Liquid) into a Word template (.docx) that fact-docgen's Word renderer fills with data.
/// The data placeholders become the simple tokens DocGen understands, so the file works as a template as it is:
/// <list type="table">
/// <item><term><c>{{ policy.effectiveDate | shortdate }}</c></term><description><c>{{policy.effectiveDate:MM/dd/yyyy}}</c> (formats: currency C2, dollars C0, percent P2, number N0, decimal N2, shortdate, upcase/downcase)</description></item>
/// <item><term><c>{% for x in list %}...{% endfor %}</c></term><description>a region from <c>{{#each list}}</c> to <c>{{/each}}</c>, or for a table row, a row that starts with <c>{{#each list}}</c></description></item>
/// <item><term><c>{% if a != blank %}</c>, <c>{% if a == "ok" %}</c></term><description><c>{{#if a}}</c>, <c>{{#if a == "ok"}}</c> (and <c>{{#unless}}</c>); <c>else</c> becomes the opposite region</description></item>
/// <item><term>Data Image</term><description><c>{{image:path|w=3.2}}</c> in a paragraph of its own</description></item>
/// </list>
/// Headings, text, key/value lines, columns, cards, callouts, tables, images, clauses and page breaks are converted to
/// Word paragraphs, tables and styles in the brand look. What cannot be converted (fixed-layout form pages, calculated
/// fields, totals, conditions on table rows, and so on) is left out and listed in <see cref="DocxExport.Warnings"/>,
/// so nothing is dropped silently.
/// </summary>
public sealed partial class DocxTemplateExporter(IWebHostEnvironment environment, ClauseStore clauses)
{
	private const int MaxClauseDepth = 5;

	public DocxExport Export(string html, string? css) => new Job(environment.WebRootPath, clauses).Build(html, css);

	// ---- patterns ----------------------------------------------------------------------------------------------------

	[GeneratedRegex(@"\{%-?\s*include\s+['""](?<name>[^'""]+)['""][^%]*-?%\}")]
	private static partial Regex IncludeTag();

	[GeneratedRegex(@"\{%-?\s*(?<tag>[a-z_]+)\s*(?<args>.*?)\s*-?%\}", RegexOptions.Singleline)]
	private static partial Regex LiquidTag();

	[GeneratedRegex(@"\{\{\s*(?<expr>.*?)\s*\}\}", RegexOptions.Singleline)]
	private static partial Regex Output();

	[GeneratedRegex(@"^[A-Za-z_][\w]*(\.[A-Za-z_][\w]*|\[\d+\])*$")]
	private static partial Regex FieldPath();

	[GeneratedRegex(@"^(?<alias>\w+)\s+in\s+(?<list>[A-Za-z_][\w.\[\]]*)")]
	private static partial Regex ForArguments();

	[GeneratedRegex("""^(?<p>[A-Za-z_][\w.\[\]]*)\s*(?<op>==|!=)\s*(?:"(?<v>[^"]*)"|'(?<v>[^']*)')$""")]
	private static partial Regex TextComparison();

	[GeneratedRegex(@"^(?<p>[A-Za-z_][\w.\[\]]*)\s*(?<op>==|!=)\s*(?:blank|nil|null|empty)$")]
	private static partial Regex BlankComparison();

	[GeneratedRegex(@"^(?<p>[A-Za-z_][\w.\[\]]*)\.size\s*(?<op>>=|<=|==|!=|>|<)\s*(?<n>\d+)$")]
	private static partial Regex SizeComparison();

	[GeneratedRegex(@"^[A-Za-z_][\w.\[\]]*$")]
	private static partial Regex Truthy();

	[GeneratedRegex(@"\{\{[^}]*\|\s*sum\b")]
	private static partial Regex SumToken();

	[GeneratedRegex(@"\s+")]
	private static partial Regex Whitespace();

	[GeneratedRegex(@"\{\{\s*brand\.logos\.(?<name>\w+)\s*\}\}")]
	private static partial Regex BrandLogo();

	// [Date] in a page setup header/footer
	[GeneratedRegex(@"^'now'\s*\|\s*date:\s*'%m/%d/%Y'$")]
	private static partial Regex TodayExpression();

	// format: "pattern" or format: "pattern", "culture"
	[GeneratedRegex("""^format\s*:\s*"(?<f>[^"]*)"(?:\s*,\s*"(?<c>[^"]*)")?$""")]
	private static partial Regex CustomFormat();

	// a class attribute with conditional styling ({% if ... %}cs-style{% endif %})
	[GeneratedRegex("""class="[^"]*?\{%[^"]*" """, RegexOptions.IgnorePatternWhitespace)]
	private static partial Regex ConditionalClass();

	[GeneratedRegex(@"\{%-?\s*capture\s+cs_\w+\s*-?%\}.*?\{%-?\s*endcapture\s*-?%\}", RegexOptions.Singleline)]
	private static partial Regex StyleCapture();

	[GeneratedRegex(@"\s*\{\{\s*cs_\w+\s*\}\}")]
	private static partial Regex StyleVariable();

	[GeneratedRegex(@"^\s*\{\{\s*(?<path>[A-Za-z_][\w.\[\]]*)\s*\}\}\s*$")]
	private static partial Regex WholeField();

	private static readonly HashSet<string> BlockTags =
	[
		"div", "p", "h1", "h2", "h3", "h4", "h5", "h6", "table", "ul", "ol", "li", "hr", "section", "article", "header", "footer",
		"main", "nav", "aside", "blockquote", "pre", "figure", "img"
	];

	private readonly record struct Fmt(bool Bold = false, bool Italic = false, bool Underline = false, string? CharStyle = null, string? Color = null, int? HalfPoints = null);

	private sealed record Ctx(int Width, string Style = "Normal", bool InHeader = false)
	{
		public double Inches => Width / 1440.0;
	}

	private sealed class Region
	{
		/// <summary>for, for-else, if, unless, or skip (a condition that cannot be exported).</summary>
		public required string Kind { get; set; }
		public string Path { get; init; } = string.Empty;
		public string? Op { get; init; }
		public string? Operand { get; init; }
		public string? Alias { get; init; }
		public int RowsSeen { get; set; }
	}

	// ---- one export --------------------------------------------------------------------------------------------------

	private sealed class Job(string webRoot, ClauseStore clauses)
	{
		private readonly List<string> _warnings = [];
		private readonly HashSet<string> _warned = [];
		private readonly List<string> _aliases = [];
		private MainDocumentPart _main = null!;
		private CssRules _css = CssRules.Empty;
		private DocumentLanguage _language = DocumentLanguages.English;
		private uint _drawingId = 1;

		public DocxExport Build(string html, string? css)
		{
			_css = CssRules.Parse(css);
			_language = DocumentLanguages.Of(html);
			if (_language != DocumentLanguages.English)
			{
				Warn($"This is the {_language.Name} version: DocGen's Word filler prints Yes/No, month names and numbers without a format language in English (US).");
			}

			var source = InlineClauses(DocumentLanguages.WithoutMarker(html), 0);
			// conditional styling: {% capture cs_x %}{% if %}cs-style{% endif %}{% endcapture %} before an element and
			// {{ cs_x }} in its class attribute (or {% if %} class names written by hand)
			if (StyleCapture().IsMatch(source) || ConditionalClass().IsMatch(source))
			{
				Warn("Conditional styling isn't carried into the Word template; that content prints in its normal style.");
				source = StyleCapture().Replace(source, string.Empty);
				source = StyleVariable().Replace(source, string.Empty);
				source = ConditionalClass().Replace(source, m => m.Value[..m.Value.IndexOf("{%", StringComparison.Ordinal)].TrimEnd() + "\"");
			}
			source = LiquidTag().Replace(source, m =>
				$"<!--LQ:{m.Groups["tag"].Value}|{m.Groups["args"].Value.Trim().Replace("--", "- -")}-->");
			var document = new HtmlParser().ParseDocument("<!DOCTYPE html><html><body>" + source + "</body></html>");
			var setup = WordPageSetup.Take(document);

			using var stream = new MemoryStream();
			using (var word = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document))
			{
				_main = word.AddMainDocumentPart();
				_main.Document = new Document(new Body());
				WordTemplateParts.AddStyles(_main);

				var width = setup?.ContentWidth ?? WordTemplateParts.ContentWidth;
				var blocks = Blocks(document.Body!.ChildNodes, new Ctx(width));
				var body = _main.Document.Body!;
				body.Append(blocks);
				body.Append(setup is null
					? WordTemplateParts.Section(_main, WordTemplateParts.AddFooter(_main))
					: SetupSection(setup));
				_main.Document.Save();
			}

			return new DocxExport(stream.ToArray(), _warnings);
		}

		// ---- page setup: paper, margins and the running header / footer (first page, even pages) ----------------------

		private SectionProperties SetupSection(WordPageSetup setup)
		{
			var section = new SectionProperties();
			foreach (var variant in new[] { "default", "first", "even" })
			{
				var type = variant switch { "first" => HeaderFooterValues.First, "even" => HeaderFooterValues.Even, _ => HeaderFooterValues.Default };
				if (setup.Slots.TryGetValue("header:" + variant, out var header))
				{
					var part = _main.AddNewPart<HeaderPart>();
					part.Header = new Header(SlotParagraph(header, setup, part));
					part.Header.Save();
					section.Append(new HeaderReference { Type = type, Id = _main.GetIdOfPart(part) });
				}
				if (setup.Slots.TryGetValue("footer:" + variant, out var footer))
				{
					var part = _main.AddNewPart<FooterPart>();
					part.Footer = new Footer(SlotParagraph(footer, setup, part));
					part.Footer.Save();
					section.Append(new FooterReference { Type = type, Id = _main.GetIdOfPart(part) });
				}
			}

			section.Append(new PageSize
			{
				Width = (uint)setup.WidthTwips,
				Height = (uint)setup.HeightTwips,
				Orient = setup.Landscape ? PageOrientationValues.Landscape : PageOrientationValues.Portrait
			});
			var (top, right, bottom, left) = setup.MarginTwips;
			section.Append(new PageMargin
			{
				Top = top, Right = (uint)right, Bottom = bottom, Left = (uint)left,
				Header = (uint)Math.Min(360, top / 2), Footer = (uint)Math.Min(360, bottom / 2), Gutter = 0
			});
			if (setup.Slots.Keys.Any(k => k.EndsWith(":first", StringComparison.Ordinal)))
			{
				section.Append(new TitlePage());
			}
			if (setup.Slots.Keys.Any(k => k.EndsWith(":even", StringComparison.Ordinal)))
			{
				var settings = _main.DocumentSettingsPart ?? _main.AddNewPart<DocumentSettingsPart>();
				settings.Settings ??= new Settings();
				settings.Settings.Append(new EvenAndOddHeaders());
				settings.Settings.Save();
			}
			return section;
		}

		// One header/footer line: left, center and right parts on tab stops across the text width.
		private Paragraph SlotParagraph(IElement line, WordPageSetup setup, OpenXmlPartContainer part)
		{
			var paragraph = new Paragraph(new ParagraphProperties(
				new Tabs(
					new TabStop { Val = TabStopValues.Center, Position = setup.ContentWidth / 2 },
					new TabStop { Val = TabStopValues.Right, Position = setup.ContentWidth }),
				new SpacingBetweenLines { Before = "0", After = "0" }));
			var slots = new[] { "left", "center", "right" };
			for (var i = 0; i < slots.Length; i++)
			{
				if (i > 0) paragraph.Append(new Run(new TabChar()));
				if (line.QuerySelector(".doc-hf-" + slots[i]) is { } slot)
				{
					paragraph.Append(SlotRuns(slot, setup, part));
				}
			}

			// the brand footer look, at the setup's text size
			foreach (var run in paragraph.Descendants<Run>())
			{
				run.RunProperties ??= new RunProperties();
				run.RunProperties.Append(new Color { Val = WordTemplateParts.Green }, new FontSize { Val = ((int)Math.Round(setup.FontSize * 2)).ToString(CultureInfo.InvariantCulture) });
			}
			return paragraph;
		}

		private IEnumerable<OpenXmlElement> SlotRuns(INode node, WordPageSetup setup, OpenXmlPartContainer part)
		{
			foreach (var child in node.ChildNodes)
			{
				switch (child)
				{
					case IText text:
						var at = 0;
						foreach (Match m in Output().Matches(text.Data))
						{
							if (m.Index > at) yield return WordTemplateParts.Text(text.Data[at..m.Index]);
							at = m.Index + m.Length;
							var expression = m.Groups["expr"].Value;
							if (TodayExpression().IsMatch(expression))
							{
								foreach (var run in WordTemplateParts.Field("DATE \\@ \"MM/dd/yyyy\"")) yield return run;
							}
							else if (Token(expression) is { Length: > 0 } token)
							{
								yield return WordTemplateParts.Text(token);
							}
						}
						if (at < text.Data.Length) yield return WordTemplateParts.Text(text.Data[at..]);
						break;
					case IElement e when e.ClassList.Contains("doc-pageno"):
						foreach (var run in WordTemplateParts.Field("PAGE")) yield return run;
						break;
					case IElement e when e.ClassList.Contains("doc-pagecount"):
						foreach (var run in WordTemplateParts.Field("NUMPAGES")) yield return run;
						break;
					case IElement e when e.LocalName == "img":
						if (HeaderPicture(e, setup, part) is { } picture) yield return picture;
						break;
					case IElement e:
						foreach (var run in SlotRuns(e, setup, part)) yield return run;
						break;
				}
			}
		}

		// The logo in a header or footer: as tall as two lines of its text, like the PDF.
		private Run? HeaderPicture(IElement img, WordPageSetup setup, OpenXmlPartContainer part)
		{
			var logo = BrandLogo().Match(img.GetAttribute("src") ?? string.Empty);
			var file = logo.Success ? Path.Combine(webRoot, "brand", "logos", "moe-logo-" + logo.Groups["name"].Value.Replace('_', '-') + ".png") : null;
			if (file is null || !File.Exists(file))
			{
				Warn("Only the MOE logo can be shown in a Word header or footer; another picture there was left out.");
				return null;
			}
			var bytes = File.ReadAllBytes(file);
			if (!WordTemplateParts.TryReadImage(bytes, out var type, out var pixelWidth, out var pixelHeight))
			{
				return null;
			}
			var heightInches = setup.FontSize * 2 / 72.0;
			return new Run(WordTemplateParts.Picture(part, bytes, type, pixelWidth, pixelHeight, heightInches * pixelWidth / pixelHeight, _drawingId++, "MOE logo"));
		}

		private void Warn(string message)
		{
			if (_warned.Add(message))
			{
				_warnings.Add(message);
			}
		}

		// ---- clauses: shared pieces are copied in, so the Word file is complete on its own ------------------------------

		private string InlineClauses(string html, int depth) =>
			IncludeTag().Replace(html, m =>
			{
				var name = m.Groups["name"].Value;
				if (depth >= MaxClauseDepth)
				{
					Warn($"The clause '{name}' is included too deeply (clauses including clauses) and was left out.");
					return string.Empty;
				}

				var reference = ClauseStore.Parse(name);
				if (reference is null || clauses.Resolve(reference, _language) is not { } clause)
				{
					Warn($"The clause '{name}' has no published version (or the pinned version does not exist), so it was left out.");
					return string.Empty;
				}

				return InlineClauses(clause.Html, depth + 1);
			});

		// ---- block content ------------------------------------------------------------------------------------------------

		private List<OpenXmlElement> Blocks(IEnumerable<INode> nodes, Ctx ctx)
		{
			var output = new List<OpenXmlElement>();
			var inline = new List<INode>();
			var regions = new Stack<Region>();

			void FlushInline()
			{
				if (inline.Count == 0)
				{
					return;
				}

				if (ParagraphFrom(inline, ctx, ctx.Style) is { } paragraph)
				{
					output.Add(paragraph);
				}

				inline.Clear();
			}

			foreach (var node in nodes)
			{
				switch (node)
				{
					case IComment comment when comment.Data.StartsWith("LQ:", StringComparison.Ordinal):
						FlushInline();
						Control(comment.Data[3..], regions, output);
						break;
					case IComment:
						break;
					case IElement element when IsBlock(element):
						FlushInline();
						output.AddRange(Element(element, ctx));
						break;
					case IText text when string.IsNullOrWhiteSpace(text.Data) && inline.Count == 0:
						break;
					default:
						inline.Add(node);
						break;
				}
			}

			FlushInline();

			while (regions.Count > 0)
			{
				Warn("A repeat or condition in the design is not closed; it was closed at the end of its section.");
				Close(regions.Pop(), output);
			}

			return Tidy(output, endWithParagraph: true);
		}

		private static bool IsBlock(IElement element) => BlockTags.Contains(element.LocalName);

		// ---- Liquid control tags => region markers ---------------------------------------------------------------------

		private void Control(string raw, Stack<Region> regions, List<OpenXmlElement> output)
		{
			var bar = raw.IndexOf('|');
			var tag = (bar < 0 ? raw : raw[..bar]).Trim().ToLowerInvariant();
			var args = bar < 0 ? string.Empty : raw[(bar + 1)..].Trim();

			switch (tag)
			{
				case "for":
				{
					var m = ForArguments().Match(args);
					if (!m.Success)
					{
						Warn($"The repeat '{args}' could not be read, so its content is shown once.");
						regions.Push(new Region { Kind = "skip" });
						return;
					}

					var list = Rewrite(m.Groups["list"].Value);
					output.Add(Marker($"{{{{#each {list}}}}}"));
					regions.Push(new Region { Kind = "for", Path = list, Alias = m.Groups["alias"].Value });
					_aliases.Add(m.Groups["alias"].Value);
					return;
				}

				case "if":
				case "unless":
				{
					var condition = Condition(args, tag == "unless");
					if (condition is null)
					{
						Warn($"The condition '{args}' can't be exported to Word, so its content is always shown.");
						regions.Push(new Region { Kind = "skip" });
						return;
					}

					var region = new Region { Kind = condition.Value.Kind, Path = condition.Value.Path, Op = condition.Value.Op, Operand = condition.Value.Operand };
					output.Add(Marker(Open(region)));
					regions.Push(region);
					return;
				}

				case "else":
				case "elsif":
				case "elseif":
				{
					if (tag != "else")
					{
						Warn("An 'else if' in the design was treated as 'otherwise'; check that part of the Word template.");
					}

					if (regions.Count == 0)
					{
						return;
					}

					var top = regions.Pop();
					switch (top.Kind)
					{
						case "for":
							// the "when empty" branch: show it only when the list has nothing in it
							EndMarker(output, "{{/each}}");
							_aliases.RemoveAt(_aliases.Count - 1);
							var empty = new Region { Kind = "for-else", Path = top.Path };
							output.Add(Marker($"{{{{#unless {top.Path}}}}}"));
							regions.Push(empty);
							return;
						case "if":
						case "unless":
							EndMarker(output, top.Kind == "if" ? "{{/if}}" : "{{/unless}}");
							var opposite = new Region
							{
								Kind = top.Kind == "if" ? "unless" : "if",
								Path = top.Path,
								Op = top.Op,
								Operand = top.Operand
							};
							output.Add(Marker(Open(opposite)));
							regions.Push(opposite);
							return;
						default:
							regions.Push(top);
							return;
					}
				}

				case "endfor":
				case "endif":
				case "endunless":
				{
					if (regions.Count == 0)
					{
						return;
					}

					Close(regions.Pop(), output);
					return;
				}

				case "endcapture":
				case "endcomment":
				case "endcase":
					return;

				default:
					Warn($"The Liquid tag '{{% {tag} %}}' isn't supported in Word templates and was left out.");
					return;
			}
		}

		private void Close(Region region, List<OpenXmlElement> output)
		{
			switch (region.Kind)
			{
				case "for":
					EndMarker(output, "{{/each}}");
					_aliases.RemoveAt(_aliases.Count - 1);
					break;
				case "for-else":
				case "unless":
					EndMarker(output, "{{/unless}}");
					break;
				case "if":
					EndMarker(output, "{{/if}}");
					break;
			}
		}

		private static string Open(Region region) =>
			"{{#" + region.Kind + " " + region.Path + (region.Op is null ? string.Empty : $" {region.Op} \"{region.Operand}\"") + "}}";

		private static void EndMarker(List<OpenXmlElement> output, string text) => output.Add(Marker(text));

		private static Paragraph Marker(string text) =>
			new(new Run(new Text(text) { Space = SpaceProcessingModeValues.Preserve }));

		private (string Kind, string Path, string? Op, string? Operand)? Condition(string args, bool unless)
		{
			var text = args.Trim();
			var plain = unless ? "unless" : "if";

			var compare = TextComparison().Match(text);
			if (compare.Success)
			{
				return (plain, Rewrite(compare.Groups["p"].Value), compare.Groups["op"].Value, compare.Groups["v"].Value);
			}

			var blank = BlankComparison().Match(text);
			if (blank.Success)
			{
				// "x == blank" is true when x is empty
				var testsEmpty = blank.Groups["op"].Value == "==";
				return (testsEmpty ^ unless ? "unless" : "if", Rewrite(blank.Groups["p"].Value), null, null);
			}

			var size = SizeComparison().Match(text);
			if (size.Success)
			{
				var n = int.Parse(size.Groups["n"].Value, CultureInfo.InvariantCulture);
				bool? nonEmpty = (size.Groups["op"].Value, n) switch
				{
					(">", 0) or (">=", 1) or ("!=", 0) => true,
					("==", 0) or ("<", 1) or ("<=", 0) => false,
					_ => null
				};

				return nonEmpty is null ? null : (nonEmpty.Value ^ unless ? "if" : "unless", Rewrite(size.Groups["p"].Value), null, null);
			}

			return Truthy().IsMatch(text) ? (plain, Rewrite(text), null, null) : null;
		}

		// A path inside a repeat is written relative to the item: "claim.claimNumber" => "claimNumber", "claim" => "this".
		private string Rewrite(string path)
		{
			path = path.Trim();
			for (var i = _aliases.Count - 1; i >= 0; i--)
			{
				var alias = _aliases[i];
				var rest = path == alias ? "this" : path.StartsWith(alias + ".", StringComparison.Ordinal) ? path[(alias.Length + 1)..] : null;
				if (rest is null)
				{
					continue;
				}

				if (i != _aliases.Count - 1)
				{
					Warn($"A field of the outer repeat '{alias}' is used inside an inner repeat. Word templates find it by name, so the inner items must not have a field with the same name.");
				}

				return rest;
			}

			return path;
		}

		// ---- {{ expressions }} => tokens -------------------------------------------------------------------------------------

		private string Tokens(string text) => Output().Replace(text, m => Token(m.Groups["expr"].Value));

		private string Token(string expression)
		{
			var parts = SplitFilters(expression);
			var path = parts[0].Trim();
			if (!FieldPath().IsMatch(path))
			{
				Warn($"The expression '{{{{ {expression.Trim()} }}}}' isn't a plain field, so it was left out of the Word template.");
				return string.Empty;
			}

			string? format = null;
			foreach (var filter in parts.Skip(1))
			{
				var name = filter.Split(':', 2)[0].Trim().ToLowerInvariant();
				switch (name)
				{
					case "currency": format = "C2"; break;
					case "dollars": format = "C0"; break;
					case "percent": format = "P2"; break;
					case "number": format = "N0"; break;
					case "decimal": format = "N2"; break;
					case "shortdate": format = "MM/dd/yyyy"; break;
					case "upcase": format = "upper"; break;
					case "downcase": format = "lower"; break;
					case "format":
						var custom = CustomFormat().Match(filter.Trim());
						if (!custom.Success || Rendering.ValueFormats.FormatProblem(custom.Groups["f"].Value) is not null)
						{
							Warn($"The format '{filter.Trim()}' can't be used in a Word template and was ignored.");
							break;
						}
						format = custom.Groups["f"].Value;
						if (custom.Groups["c"].Success && custom.Groups["c"].Value != Rendering.ValueFormats.DefaultCulture)
						{
							Warn($"DocGen formats numbers and dates in US English, so the '{custom.Groups["c"].Value}' format '{format}' prints in US English in the Word template.");
						}
						break;
					case "mask":
						Warn("Masks (such as phone numbers) aren't supported by DocGen's Word templates yet; the value prints without the mask.");
						break;
					case "barcode":
						Warn("Barcodes and QR codes can't be exported to Word templates yet, so they were left out.");
						return string.Empty;
					case "chart":
						Warn("Charts can't be exported to Word templates yet, so they were left out.");
						return string.Empty;
					case "sum":
						Warn("Totals (the sum filter) aren't supported in Word templates yet, so the total was left out.");
						return string.Empty;
					case "calc_round":
					case "calc_value":
						Warn("Calculated fields can't be exported to Word; the calculation was left out.");
						return string.Empty;
					default:
						Warn($"The filter '{name}' isn't supported in Word templates and was ignored.");
						break;
				}
			}

			return "{{" + Rewrite(path) + (format is null ? string.Empty : ":" + format) + "}}";
		}

		private static List<string> SplitFilters(string expression)
		{
			var parts = new List<string>();
			var current = new System.Text.StringBuilder();
			char quote = '\0';
			foreach (var ch in expression)
			{
				if (quote != '\0')
				{
					if (ch == quote)
					{
						quote = '\0';
					}
				}
				else if (ch is '"' or '\'')
				{
					quote = ch;
				}
				else if (ch == '|')
				{
					parts.Add(current.ToString());
					current.Clear();
					continue;
				}

				current.Append(ch);
			}

			parts.Add(current.ToString());
			return parts;
		}

		// ---- paragraphs and runs ---------------------------------------------------------------------------------------------

		private Paragraph? ParagraphFrom(IEnumerable<INode> nodes, Ctx ctx, string style, IElement? source = null,
			string? bulletText = null, int? indentTwips = null)
		{
			var fmt = new Fmt();
			var align = (JustificationValues?)null;
			if (source is not null)
			{
				fmt = ApplyCss(source, fmt);
				align = Alignment(source);
			}

			var runs = new List<OpenXmlElement>();
			if (bulletText is not null)
			{
				runs.Add(MakeRun(bulletText, new Fmt(Bold: true, Color: WordTemplateParts.Alpine)));
			}

			foreach (var node in nodes)
			{
				Runs(node, fmt, runs, ctx);
			}

			TidyRuns(runs, hasBullet: bulletText is not null);
			if (runs.Count == 0)
			{
				return null;
			}

			var properties = new ParagraphProperties(new ParagraphStyleId { Val = style });
			if (indentTwips is { } indent)
			{
				properties.Append(new Indentation { Left = indent.ToString(CultureInfo.InvariantCulture) });
			}

			if (align is { } justification)
			{
				properties.Append(new Justification { Val = justification });
			}

			var paragraph = new Paragraph(properties);
			paragraph.Append(runs);
			return paragraph;
		}

		private void Runs(INode node, Fmt fmt, List<OpenXmlElement> runs, Ctx ctx)
		{
			switch (node)
			{
				case IText text:
				{
					var converted = Tokens(Whitespace().Replace(text.Data, " "));
					if (converted.Length > 0)
					{
						runs.Add(MakeRun(converted, fmt));
					}

					return;
				}

				case IComment comment when comment.Data.StartsWith("LQ:", StringComparison.Ordinal):
					Warn("A condition inside a line of text can't be exported to Word; that text is always shown.");
					return;

				case IElement element:
				{
					switch (element.LocalName)
					{
						case "br":
							runs.Add(new Run(new Break()));
							return;
						case "svg":
						case "script":
						case "style":
							return;
						case "img":
							if (ImageContent(element, ctx) is { } picture)
							{
								runs.Add(picture);
							}

							return;
					}

					var next = ApplyCss(element, fmt);
					if (element.LocalName is "strong" or "b") next = next with { Bold = true };
					if (element.LocalName is "em" or "i") next = next with { Italic = true };
					if (element.LocalName == "u") next = next with { Underline = true };

					var isLabel = element.ClassList.Contains("kv-label");
					if (isLabel) next = next with { CharStyle = "MoeLabel" };

					var before = runs.Count;
					foreach (var child in element.ChildNodes)
					{
						Runs(child, next, runs, ctx);
					}

					// the gap the stylesheet puts after a label ("Policy:" "CPP123")
					if (isLabel && runs.Count > before)
					{
						runs.Add(MakeRun(" ", fmt));
					}

					return;
				}
			}
		}

		private static Run MakeRun(string text, Fmt fmt)
		{
			var run = new Run();
			if (fmt != default)
			{
				var properties = new RunProperties();
				if (fmt.CharStyle is not null) properties.Append(new RunStyle { Val = fmt.CharStyle });
				if (fmt.Bold) properties.Append(new Bold());
				if (fmt.Italic) properties.Append(new Italic());
				if (fmt.Color is not null) properties.Append(new Color { Val = fmt.Color });
				if (fmt.HalfPoints is { } size) properties.Append(new FontSize { Val = size.ToString(CultureInfo.InvariantCulture) });
				if (fmt.Underline) properties.Append(new Underline { Val = UnderlineValues.Single });
				if (properties.HasChildren) run.Append(properties);
			}

			run.Append(new Text(text) { Space = SpaceProcessingModeValues.Preserve });
			return run;
		}

		// Collapses spaces that sit next to each other across runs, trims the line, and drops empty runs.
		private static void TidyRuns(List<OpenXmlElement> runs, bool hasBullet)
		{
			var previousEndsWithSpace = hasBullet; // the bullet carries its own spacing
			foreach (var run in runs.OfType<Run>().ToList())
			{
				var text = run.GetFirstChild<Text>();
				if (text is null)
				{
					previousEndsWithSpace = false;
					continue;
				}

				var value = text.Text;
				if (previousEndsWithSpace && value.StartsWith(' '))
				{
					value = value.TrimStart(' ');
				}

				if (value.Length == 0)
				{
					runs.Remove(run);
					continue;
				}

				text.Text = value;
				previousEndsWithSpace = value.EndsWith(' ');
			}

			var texts = runs.OfType<Run>().Select(r => r.GetFirstChild<Text>()).Where(t => t is not null).ToList();
			if (texts.Count > 0 && !hasBullet)
			{
				texts[0]!.Text = texts[0]!.Text.TrimStart(' ');
			}

			if (texts.Count > 0)
			{
				texts[^1]!.Text = texts[^1]!.Text.TrimEnd(' ');
			}

			foreach (var run in runs.OfType<Run>().Where(r => r.GetFirstChild<Text>() is { Text.Length: 0 }).ToList())
			{
				runs.Remove(run);
			}

			if (runs.OfType<Run>().All(r => r.GetFirstChild<Text>() is null && r.GetFirstChild<Drawing>() is null && r.GetFirstChild<Break>() is null))
			{
				runs.Clear();
			}
		}

		private Fmt ApplyCss(IElement element, Fmt fmt)
		{
			var rules = _css.For(element);
			if (rules.TryGetValue("font-weight", out var weight) && (weight.Equals("bold", StringComparison.OrdinalIgnoreCase) || (int.TryParse(weight, out var n) && n >= 600)))
			{
				fmt = fmt with { Bold = true };
			}

			if (rules.TryGetValue("font-style", out var style) && style.Equals("italic", StringComparison.OrdinalIgnoreCase))
			{
				fmt = fmt with { Italic = true };
			}

			if (rules.TryGetValue("text-decoration", out var decoration) && decoration.Contains("underline", StringComparison.OrdinalIgnoreCase))
			{
				fmt = fmt with { Underline = true };
			}

			if (CssRules.HexColor(rules.GetValueOrDefault("color")) is { } color)
			{
				fmt = fmt with { Color = color };
			}

			if (CssRules.Points(rules.GetValueOrDefault("font-size")) is { } points && points is > 4 and < 96)
			{
				fmt = fmt with { HalfPoints = (int)Math.Round(points * 2) };
			}

			return fmt;
		}

		private JustificationValues? Alignment(IElement element)
		{
			var align = _css.For(element).GetValueOrDefault("text-align")?.ToLowerInvariant();
			if (align is null && (element.ClassList.Contains("num") || element.ClassList.Contains("text-end") || element.ClassList.Contains("text-right")))
			{
				align = "right";
			}

			return align switch
			{
				"right" or "end" => JustificationValues.Right,
				"center" => JustificationValues.Center,
				_ => null
			};
		}

		// ---- elements ----------------------------------------------------------------------------------------------------------

		private IEnumerable<OpenXmlElement> Element(IElement e, Ctx ctx)
		{
			var classes = e.ClassList;

			if (classes.Contains("form-page") || classes.Contains("legacy-page"))
			{
				Warn("Fixed-layout form pages can't be exported to a Word template; the page was left out.");
				return [];
			}

			if (classes.Contains("doc-theme"))
			{
				Warn("The theme's colours and fonts aren't carried into the Word template; it uses the standard brand styles.");
				return [];
			}

			if (classes.Contains("doc-watermark"))
			{
				Warn("The watermark isn't carried into the Word template.");
				return [];
			}

			if (classes.Contains("page-break"))
			{
				return [new Paragraph(new Run(new Break { Type = BreakValues.Page }))];
			}

			// signature line: an empty paragraph with a rule under it (the e-signature anchor is left out)
			if (classes.Contains("doc-signature-line"))
			{
				if (e.QuerySelector(".doc-esign-anchor") is not null)
				{
					Warn("E-signature anchors (such as \\s1\\) aren't carried into the Word template; add them in Word if the signing service needs them.");
				}
				return [new Paragraph(new ParagraphProperties(
					new ParagraphBorders(new BottomBorder { Val = BorderValues.Single, Size = 6, Space = 1, Color = WordTemplateParts.Ink }),
					new SpacingBetweenLines { Before = "480", After = "40" },
					new Indentation { Right = Math.Max(0, ctx.Width - 4680).ToString(CultureInfo.InvariantCulture) }))];
			}

			if (classes.Contains("doc-header")) return HeaderFrom(e, ctx);
			if (classes.Contains("row") || classes.Contains("layout-row")) return ColumnsFrom(e, ctx);
			if (classes.Contains("moe-card") || classes.Contains("doc-box") || classes.Any(c => c.StartsWith("md-card", StringComparison.Ordinal)))
			{
				return CardFrom(e, ctx, classes.Contains("moe-card--accent"));
			}

			if (classes.Contains("moe-callout") || classes.Contains("md-banner")) return Blocks(e.ChildNodes, ctx with { Style = "MoeCallout" });
			if (classes.Contains("moe-card-title")) return One(ParagraphFrom(e.ChildNodes, ctx, "MoeLocation", e));
			if (classes.Contains("kv")) return One(ParagraphFrom(e.ChildNodes, ctx, ctx.Style, e));
			if (classes.Contains("chip")) return One(ParagraphFrom(e.ChildNodes, ctx, ctx.Style, e, bulletText: "•  "));

			switch (e.LocalName)
			{
				case "h1": return One(ParagraphFrom(e.ChildNodes, ctx, ctx.InHeader ? "MoeTitle" : "MoeH1", e));
				case "h2": return One(ParagraphFrom(e.ChildNodes, ctx, "MoeHeading", e));
				case "h3":
				case "h4":
				case "h5":
				case "h6": return One(ParagraphFrom(e.ChildNodes, ctx, "MoeSubheading", e));
				case "table": return TableFrom(e, ctx);
				case "img": return One(ImageParagraph(e, ctx));
				case "hr": return [Rule()];
				case "ul":
				case "ol": return ListFrom(e, ctx);
				case "li": return One(ParagraphFrom(e.ChildNodes, ctx, ctx.Style, e, bulletText: "•  ", indentTwips: 360));
			}

			// p, div, section ...: a container when it holds blocks or conditions, otherwise one paragraph
			var holdsBlocks = e.ChildNodes.Any(n => n is IElement child && IsBlock(child) || n is IComment { Data: var data } && data.StartsWith("LQ:", StringComparison.Ordinal));
			return holdsBlocks ? Blocks(e.ChildNodes, ctx) : One(ParagraphFrom(e.ChildNodes, ctx, ctx.Style, e));
		}

		private static IEnumerable<OpenXmlElement> One(Paragraph? paragraph) => paragraph is null ? [] : [paragraph];

		private static Paragraph Rule() => new(new ParagraphProperties(
			new ParagraphBorders(new BottomBorder { Val = BorderValues.Single, Size = 6, Space = 1, Color = WordTemplateParts.Rule }),
			new SpacingBetweenLines { Before = "60", After = "120" }));

		private IEnumerable<OpenXmlElement> ListFrom(IElement list, Ctx ctx)
		{
			var hasLiquid = list.ChildNodes.Any(n => n is IComment c && c.Data.StartsWith("LQ:", StringComparison.Ordinal));
			if (list.LocalName == "ol" && !hasLiquid)
			{
				var items = new List<OpenXmlElement>();
				var number = 1;
				foreach (var item in list.Children.Where(c => c.LocalName == "li"))
				{
					if (ParagraphFrom(item.ChildNodes, ctx, ctx.Style, item, bulletText: $"{number}.  ", indentTwips: 360) is { } paragraph)
					{
						items.Add(paragraph);
						number++;
					}
				}

				return items;
			}

			return Blocks(list.ChildNodes, ctx);
		}

		// ---- layout ----------------------------------------------------------------------------------------------------------------

		private IEnumerable<OpenXmlElement> HeaderFrom(IElement header, Ctx ctx)
		{
			var logoWidth = Math.Min(3500, ctx.Width / 3);
			var left = new List<INode>();
			var right = new List<INode>();
			foreach (var node in header.ChildNodes)
			{
				var holdsImage = node is IElement e && (e.LocalName == "img" || e.QuerySelector("img") is not null);
				(holdsImage ? left : right).Add(node);
			}

			var leftContent = Blocks(left, ctx with { Width = logoWidth });
			var rightContent = Blocks(right, ctx with { Width = ctx.Width - logoWidth, InHeader = true });

			var table = new Table(
				new TableProperties(
					new TableWidth { Width = "5000", Type = TableWidthUnitValues.Pct },
					new TableBorders(new BottomBorder { Val = BorderValues.Single, Size = 32, Color = WordTemplateParts.Apple, Space = 0 }),
					new TableLayout { Type = TableLayoutValues.Fixed },
					new TableCellMarginDefault(
						new TopMargin { Width = "0", Type = TableWidthUnitValues.Dxa },
						new TableCellLeftMargin { Width = 0, Type = TableWidthValues.Dxa },
						new BottomMargin { Width = "160", Type = TableWidthUnitValues.Dxa },
						new TableCellRightMargin { Width = 0, Type = TableWidthValues.Dxa })),
				new TableGrid(new GridColumn { Width = logoWidth.ToString(CultureInfo.InvariantCulture) }, new GridColumn { Width = (ctx.Width - logoWidth).ToString(CultureInfo.InvariantCulture) }));

			table.Append(new TableRow(
				Cell(logoWidth, leftContent, middle: true),
				Cell(ctx.Width - logoWidth, rightContent, middle: true)));
			return [table];
		}

		private IEnumerable<OpenXmlElement> ColumnsFrom(IElement row, Ctx ctx)
		{
			var columns = row.Children.ToList();
			if (columns.Count == 0)
			{
				return Blocks(row.ChildNodes, ctx);
			}

			var widths = Distribute(ctx.Width, columns.Select(c => CssRules.Percent(_css.For(c).GetValueOrDefault("flex-basis") ?? _css.For(c).GetValueOrDefault("width"))).ToList());
			var table = new Table(
				new TableProperties(
					new TableWidth { Width = "5000", Type = TableWidthUnitValues.Pct },
					new TableLayout { Type = TableLayoutValues.Fixed },
					new TableCellMarginDefault(
						new TopMargin { Width = "0", Type = TableWidthUnitValues.Dxa },
						new TableCellLeftMargin { Width = 0, Type = TableWidthValues.Dxa },
						new BottomMargin { Width = "0", Type = TableWidthUnitValues.Dxa },
						new TableCellRightMargin { Width = 120, Type = TableWidthValues.Dxa })),
				new TableGrid(widths.Select(w => new GridColumn { Width = w.ToString(CultureInfo.InvariantCulture) })));

			var tableRow = new TableRow();
			for (var i = 0; i < columns.Count; i++)
			{
				tableRow.Append(Cell(widths[i], Blocks(columns[i].ChildNodes, ctx with { Width = Math.Max(1000, widths[i] - 120) })));
			}

			table.Append(tableRow);
			return [table];
		}

		private static List<int> Distribute(int total, List<double?> percents)
		{
			var fixedShare = percents.Where(p => p is not null).Sum(p => p!.Value);
			var unspecified = percents.Count(p => p is null);
			var remaining = Math.Max(0, 100 - fixedShare);
			return percents.Select(p => (int)(total * (p ?? (unspecified == 0 ? 0 : remaining / unspecified)) / Math.Max(100, fixedShare + (unspecified == 0 ? 0 : remaining)))).ToList();
		}

		private static TableCell Cell(int width, List<OpenXmlElement> content, bool middle = false)
		{
			var properties = new TableCellProperties(new TableCellWidth { Width = width.ToString(CultureInfo.InvariantCulture), Type = TableWidthUnitValues.Dxa });
			if (middle)
			{
				properties.Append(new TableCellVerticalAlignment { Val = TableVerticalAlignmentValues.Center });
			}

			var cell = new TableCell(properties);
			cell.Append(content.Count == 0 ? [new Paragraph()] : content);
			return cell;
		}

		private IEnumerable<OpenXmlElement> CardFrom(IElement card, Ctx ctx, bool accent)
		{
			const int margin = 140;
			var content = Blocks(card.ChildNodes, ctx with { Width = ctx.Width - 2 * margin });

			var table = new Table(
				new TableProperties(
					new TableWidth { Width = "5000", Type = TableWidthUnitValues.Pct },
					new TableBorders(
						new TopBorder { Val = BorderValues.Single, Size = (uint)(accent ? 24 : 6), Color = accent ? WordTemplateParts.Apple : WordTemplateParts.Rule },
						new LeftBorder { Val = BorderValues.Single, Size = 6, Color = WordTemplateParts.Rule },
						new BottomBorder { Val = BorderValues.Single, Size = 6, Color = WordTemplateParts.Rule },
						new RightBorder { Val = BorderValues.Single, Size = 6, Color = WordTemplateParts.Rule }),
					new TableLayout { Type = TableLayoutValues.Fixed },
					new TableCellMarginDefault(
						new TopMargin { Width = margin.ToString(CultureInfo.InvariantCulture), Type = TableWidthUnitValues.Dxa },
						new TableCellLeftMargin { Width = margin, Type = TableWidthValues.Dxa },
						new BottomMargin { Width = margin.ToString(CultureInfo.InvariantCulture), Type = TableWidthUnitValues.Dxa },
						new TableCellRightMargin { Width = margin, Type = TableWidthValues.Dxa })),
				new TableGrid(new GridColumn { Width = ctx.Width.ToString(CultureInfo.InvariantCulture) }));

			table.Append(new TableRow(Cell(ctx.Width, content)));
			return [table];
		}

		// ---- tables ------------------------------------------------------------------------------------------------------------------

		private IEnumerable<OpenXmlElement> TableFrom(IElement table, Ctx ctx)
		{
			var rows = new List<TableRow>();
			var after = new List<OpenXmlElement>();
			var regions = new Stack<Region>();
			var widths = ColumnWidths(table, ctx.Width);

			foreach (var section in table.Children.Where(c => c.LocalName is "thead" or "tbody" or "tfoot" or "tr"))
			{
				var sectionName = section.LocalName;
				var items = sectionName == "tr" ? new List<INode> { section } : section.ChildNodes.ToList();
				foreach (var node in items)
				{
					if (node is IComment comment && comment.Data.StartsWith("LQ:", StringComparison.Ordinal))
					{
						RowControl(comment.Data[3..], regions);
						continue;
					}

					if (node is not IElement tr || tr.LocalName != "tr")
					{
						continue;
					}

					var isHeader = sectionName == "thead" || (rows.Count == 0 && tr.Children.All(c => c.LocalName == "th"));
					RowFrom(tr, isHeader, sectionName == "tfoot", widths, regions, rows, after, ctx);
				}
			}

			while (regions.Count > 0)
			{
				var left = regions.Pop();
				if (left.Kind == "for")
				{
					_aliases.RemoveAt(_aliases.Count - 1);
				}
			}

			var plain = table.ClassList.Contains("doc-table") && !table.ClassList.Contains("moe-table");
			var properties = new TableProperties();
			if (!plain)
			{
				properties.Append(new TableStyle { Val = "MoeTable" });
			}

			properties.Append(new TableWidth { Width = "5000", Type = TableWidthUnitValues.Pct });
			if (plain)
			{
				properties.Append(PlainBorders(table));
			}

			properties.Append(new TableLayout { Type = TableLayoutValues.Fixed });
			properties.Append(new TableLook { Val = "04A0", FirstRow = !plain, LastRow = false, FirstColumn = false, LastColumn = false, NoHorizontalBand = false, NoVerticalBand = true });

			var wordTable = new Table(properties, new TableGrid(widths.Select(w => new GridColumn { Width = w.ToString(CultureInfo.InvariantCulture) })));
			wordTable.Append(rows);

			var result = new List<OpenXmlElement> { wordTable };
			result.AddRange(after);
			return result;
		}

		private static TableBorders PlainBorders(IElement table)
		{
			var c = table.ClassList;
			BorderType Line() => new TopBorder { Val = BorderValues.Single, Size = 6, Color = "000000" };
			if (c.Contains("tbl-b-none"))
			{
				return new TableBorders();
			}

			if (c.Contains("tbl-b-all"))
			{
				return new TableBorders(
					new TopBorder { Val = BorderValues.Single, Size = 6, Color = "000000" },
					new LeftBorder { Val = BorderValues.Single, Size = 6, Color = "000000" },
					new BottomBorder { Val = BorderValues.Single, Size = 6, Color = "000000" },
					new RightBorder { Val = BorderValues.Single, Size = 6, Color = "000000" },
					new InsideHorizontalBorder { Val = BorderValues.Single, Size = 6, Color = "000000" },
					new InsideVerticalBorder { Val = BorderValues.Single, Size = 6, Color = "000000" });
			}

			if (c.Contains("tbl-b-rows"))
			{
				return new TableBorders(
					new BottomBorder { Val = BorderValues.Single, Size = 6, Color = "000000" },
					new InsideHorizontalBorder { Val = BorderValues.Single, Size = 6, Color = "000000" });
			}

			return new TableBorders(
				new TopBorder { Val = BorderValues.Single, Size = 6, Color = WordTemplateParts.Rule },
				new BottomBorder { Val = BorderValues.Single, Size = 6, Color = WordTemplateParts.Rule },
				new InsideHorizontalBorder { Val = BorderValues.Single, Size = 6, Color = WordTemplateParts.Rule });
		}

		private List<int> ColumnWidths(IElement table, int total)
		{
			var first = table.QuerySelectorAll("tr").FirstOrDefault();
			if (first is null)
			{
				return [total];
			}

			var cells = first.Children.Where(c => c.LocalName is "td" or "th").ToList();
			return Distribute(total, cells.Select(c => CssRules.Percent(_css.For(c).GetValueOrDefault("width"))).ToList());
		}

		private void RowControl(string raw, Stack<Region> regions)
		{
			var bar = raw.IndexOf('|');
			var tag = (bar < 0 ? raw : raw[..bar]).Trim().ToLowerInvariant();
			var args = bar < 0 ? string.Empty : raw[(bar + 1)..].Trim();

			switch (tag)
			{
				case "for":
				{
					var m = ForArguments().Match(args);
					if (!m.Success)
					{
						Warn($"The repeat '{args}' could not be read, so its rows are shown once.");
						regions.Push(new Region { Kind = "skip" });
						return;
					}

					var list = Rewrite(m.Groups["list"].Value);
					regions.Push(new Region { Kind = "for", Path = list, Alias = m.Groups["alias"].Value });
					_aliases.Add(m.Groups["alias"].Value);
					return;
				}

				case "if":
				case "unless":
					Warn("A condition around a table row can't be exported to Word, so the row is always shown.");
					regions.Push(new Region { Kind = "skip" });
					return;

				case "else":
				case "elsif":
				case "elseif":
					if (regions.Count > 0 && regions.Peek().Kind == "for")
					{
						var top = regions.Pop();
						_aliases.RemoveAt(_aliases.Count - 1);
						regions.Push(new Region { Kind = "for-else", Path = top.Path });
					}

					return;

				case "endfor":
				case "endif":
				case "endunless":
					if (regions.Count > 0)
					{
						var top = regions.Pop();
						if (top.Kind == "for")
						{
							_aliases.RemoveAt(_aliases.Count - 1);
						}
					}

					return;
			}
		}

		private void RowFrom(IElement tr, bool isHeader, bool isFooter, List<int> widths, Stack<Region> regions,
			List<TableRow> rows, List<OpenXmlElement> after, Ctx ctx)
		{
			if (isFooter && SumToken().IsMatch(tr.InnerHtml))
			{
				Warn("The totals row (a sum of a column) isn't supported in Word templates yet, so it was left out.");
				return;
			}

			var top = regions.Count > 0 ? regions.Peek() : null;

			// the "when empty" row of a repeat becomes a line under the table, shown only when the list is empty
			if (top is { Kind: "for-else" })
			{
				var text = Whitespace().Replace(tr.TextContent, " ").Trim();
				if (text.Length > 0)
				{
					after.Add(Marker($"{{{{#unless {top.Path}}}}}"));
					after.Add(new Paragraph(new ParagraphProperties(new ParagraphStyleId { Val = "Normal" }), MakeRun(Tokens(text), default)));
					after.Add(Marker("{{/unless}}"));
				}

				return;
			}

			var cells = tr.Children.Where(c => c.LocalName is "td" or "th").ToList();
			var row = new TableRow();
			var properties = new TableRowProperties(new CantSplit());
			if (isHeader)
			{
				properties.Append(new TableHeader());
			}

			row.Append(properties);

			var column = 0;
			foreach (var cell in cells)
			{
				var span = int.TryParse(cell.GetAttribute("colspan"), out var s) && s > 1 ? s : 1;
				var width = Enumerable.Range(column, span).Where(i => i < widths.Count).Sum(i => widths[i]);
				column += span;

				var numeric = cell.ClassList.Contains("num") || Alignment(cell) == JustificationValues.Right;
				var content = Blocks(cell.ChildNodes, new Ctx(Math.Max(600, width - 220), numeric ? "MoeCellRight" : "MoeCell"));
				var wordCell = Cell(Math.Max(600, width), content);
				if (span > 1)
				{
					wordCell.TableCellProperties!.Append(new GridSpan { Val = span });
				}

				row.Append(wordCell);
			}

			// a row inside a repeat starts with {{#each list}}: DocGen repeats that row for every item
			if (top is { Kind: "for" } && !isHeader && row.Elements<TableCell>().FirstOrDefault() is { } first)
			{
				if (top.RowsSeen++ == 0)
				{
					var paragraph = first.Elements<Paragraph>().First();
					var marker = new Run(new Text($"{{{{#each {top.Path}}}}}") { Space = SpaceProcessingModeValues.Preserve });
					if (paragraph.ParagraphProperties is { } pPr)
					{
						pPr.InsertAfterSelf(marker);
					}
					else
					{
						paragraph.PrependChild(marker);
					}
				}
				else
				{
					Warn("Only the first row of a repeat is repeated in the Word template; the other rows of that repeat are shown once.");
				}
			}

			rows.Add(row);
		}

		// ---- images ---------------------------------------------------------------------------------------------------------------------

		private Paragraph? ImageParagraph(IElement img, Ctx ctx)
		{
			var content = ImageContent(img, ctx);
			return content is null ? null : new Paragraph(new ParagraphProperties(new ParagraphStyleId { Val = ctx.Style }), content);
		}

		private Run? ImageContent(IElement img, Ctx ctx)
		{
			var src = img.GetAttribute("src") ?? string.Empty;
			var rules = _css.For(img);
			var cssWidth = CssRules.Points(rules.GetValueOrDefault("width")) is { } pt ? pt / 72.0 : (double?)null;

			// the MOE logo: a fixed picture in the template (the PNG that sits next to the SVG)
			var logo = BrandLogo().Match(src);
			if (logo.Success)
			{
				var file = Path.Combine(webRoot, "brand", "logos", "moe-logo-" + logo.Groups["name"].Value.Replace('_', '-') + ".png");
				if (!File.Exists(file))
				{
					Warn($"The logo '{logo.Groups["name"].Value}' has no PNG version for Word, so it was left out.");
					return null;
				}

				var width = cssWidth ?? (img.ClassList.Contains("brand-logo--stacked") ? 110 / 96.0 : 190 / 96.0);
				return Picture(File.ReadAllBytes(file), Math.Min(width, ctx.Inches), "MOE logo");
			}

			// a Data Image: an image from the document data, filled in by DocGen
			var field = WholeField().Match(src);
			if (field.Success)
			{
				var path = Rewrite(field.Groups["path"].Value);
				var width = Math.Min(cssWidth ?? ctx.Inches, Math.Min(ctx.Inches, 6.5));
				return new Run(new Text($"{{{{image:{path}|w={width.ToString("0.##", CultureInfo.InvariantCulture)}}}}}") { Space = SpaceProcessingModeValues.Preserve });
			}

			// a picture that is part of the design itself
			if (src.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase) && src.Contains(";base64,", StringComparison.Ordinal))
			{
				try
				{
					var bytes = Convert.FromBase64String(src[(src.IndexOf(',') + 1)..]);
					if (WordTemplateParts.TryReadImage(bytes, out _, out var pixelWidth, out _))
					{
						return Picture(bytes, Math.Min(cssWidth ?? pixelWidth / 96.0, ctx.Inches), "Picture");
					}
				}
				catch (FormatException)
				{
				}
			}

			Warn("A picture that isn't a PNG, JPEG or GIF (for example an SVG) or that is loaded from a web address can't be exported to Word, so it was left out.");
			return null;
		}

		private Run? Picture(byte[] bytes, double widthInches, string name)
		{
			if (!WordTemplateParts.TryReadImage(bytes, out var type, out var pixelWidth, out var pixelHeight))
			{
				Warn("A picture could not be read, so it was left out.");
				return null;
			}

			return new Run(WordTemplateParts.Picture(_main, bytes, type, pixelWidth, pixelHeight, widthInches, _drawingId++, name));
		}

		// ---- spacing -----------------------------------------------------------------------------------------------------------------------

		// Two tables with nothing between them are one table in Word, and region markers disappear when the template is filled,
		// so a table followed by another table, a marker, or the end of its container gets a small spacer paragraph.
		private static List<OpenXmlElement> Tidy(List<OpenXmlElement> items, bool endWithParagraph)
		{
			var result = new List<OpenXmlElement>();
			for (var i = 0; i < items.Count; i++)
			{
				result.Add(items[i]);
				if (items[i] is Table)
				{
					var next = i + 1 < items.Count ? items[i + 1] : null;
					if (next is null ? endWithParagraph : next is Table || IsMarker(next))
					{
						result.Add(Spacer());
					}
				}
			}

			if (endWithParagraph && result.Count == 0)
			{
				result.Add(new Paragraph());
			}

			return result;
		}

		private static bool IsMarker(OpenXmlElement element) =>
			element is Paragraph p && p.InnerText.StartsWith("{{", StringComparison.Ordinal)
			&& (p.InnerText.StartsWith("{{#", StringComparison.Ordinal) || p.InnerText.StartsWith("{{/", StringComparison.Ordinal));

		private static Paragraph Spacer() => new(new ParagraphProperties(
			new SpacingBetweenLines { Before = "0", After = "0", Line = "60", LineRule = LineSpacingRuleValues.Exact }));
	}
}
