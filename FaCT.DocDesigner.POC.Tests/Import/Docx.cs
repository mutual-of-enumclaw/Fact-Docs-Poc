using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;
using WPS = DocumentFormat.OpenXml.Office2010.Word.DrawingShape;

namespace FaCT.DocDesigner.POC.Tests.Import;

/// <summary>Builds small Word documents in memory with the OpenXml SDK, the way Word writes them.</summary>
internal static class Docx
{
	public static byte[] Create(Action<MainDocumentPart, Body> build, Action<WordprocessingDocument>? package = null)
	{
		using var stream = new MemoryStream();
		using (var document = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document))
		{
			var main = document.AddMainDocumentPart();
			main.Document = new Document(new Body());
			build(main, main.Document.Body!);
			package?.Invoke(document);
			main.Document.Save();
		}
		return stream.ToArray();
	}

	public static byte[] WithBody(params OpenXmlElement[] blocks) => Create((_, body) => body.Append(blocks));

	public static Paragraph P(params OpenXmlElement[] children) => new(children);

	public static Paragraph P(string text) => new(R(text));

	public static Paragraph P(ParagraphProperties properties, params OpenXmlElement[] children) =>
		new(new OpenXmlElement[] { properties }.Concat(children));

	public static Run R(string text, RunProperties? properties = null)
	{
		var run = new Run();
		if (properties is not null) run.Append(properties);
		run.Append(new Text(text) { Space = SpaceProcessingModeValues.Preserve });
		return run;
	}

	public static Paragraph Styled(string styleId, string text) =>
		P(new ParagraphProperties(new ParagraphStyleId { Val = styleId }), R(text));

	public static Style ParagraphStyle(string id, string name, string? basedOn = null, NumberingProperties? numbering = null)
	{
		var style = new Style { Type = StyleValues.Paragraph, StyleId = id };
		style.Append(new StyleName { Val = name });
		if (basedOn is not null) style.Append(new BasedOn { Val = basedOn });
		if (numbering is not null) style.Append(new StyleParagraphProperties(numbering));
		return style;
	}

	public static Style TableStyle(string id, string name, TableBorders? borders = null)
	{
		var style = new Style { Type = StyleValues.Table, StyleId = id };
		style.Append(new StyleName { Val = name });
		if (borders is not null) style.Append(new StyleTableProperties(borders));
		return style;
	}

	public static void AddStyles(MainDocumentPart main, params Style[] styles)
	{
		var part = main.StyleDefinitionsPart ?? main.AddNewPart<StyleDefinitionsPart>();
		part.Styles = new Styles(styles);
		part.Styles.Save();
	}

	/// <summary>Numbering with one definition per entry: numId = index + 1; every level uses the given format.</summary>
	public static void AddNumbering(MainDocumentPart main, params NumberFormatValues[] formats)
	{
		var part = main.AddNewPart<NumberingDefinitionsPart>();
		var numbering = new Numbering();
		for (var i = 0; i < formats.Length; i++)
		{
			var definition = new AbstractNum { AbstractNumberId = i };
			for (var level = 0; level < 9; level++)
			{
				definition.Append(new Level(new NumberingFormat { Val = formats[i] }) { LevelIndex = level });
			}
			numbering.Append(definition);
		}
		for (var i = 0; i < formats.Length; i++)
		{
			numbering.Append(new NumberingInstance(new AbstractNumId { Val = i }) { NumberID = i + 1 });
		}
		part.Numbering = numbering;
		part.Numbering.Save();
	}

	public static NumberingProperties Numbering(int numId, int level) =>
		new(new NumberingLevelReference { Val = level }, new NumberingId { Val = numId });

	public static Paragraph ListItem(int numId, int level, string text) =>
		P(new ParagraphProperties(Numbering(numId, level)), R(text));

	/// <summary>A MERGEFIELD as Word writes it: begin, instruction, separate, cached «Name» result, end.</summary>
	public static Run[] MergeField(string instruction, string cached) =>
	[
		new Run(new FieldChar { FieldCharType = FieldCharValues.Begin }),
		new Run(new FieldCode(instruction) { Space = SpaceProcessingModeValues.Preserve }),
		new Run(new FieldChar { FieldCharType = FieldCharValues.Separate }),
		R(cached),
		new Run(new FieldChar { FieldCharType = FieldCharValues.End })
	];

	public static SimpleField SimpleField(string instruction, string cached) => new(R(cached)) { Instruction = instruction };

	public static SdtRun ContentControl(string? tag, string? alias, string placeholder)
	{
		var properties = new SdtProperties();
		if (alias is not null) properties.Append(new SdtAlias { Val = alias });
		if (tag is not null) properties.Append(new Tag { Val = tag });
		return new SdtRun(properties, new SdtContentRun(R(placeholder)));
	}

	public static Hyperlink Link(MainDocumentPart main, string url, string text)
	{
		var relationship = main.AddHyperlinkRelationship(new Uri(url, UriKind.RelativeOrAbsolute), true);
		return new Hyperlink(R(text)) { Id = relationship.Id };
	}

	public static TableCell Cell(string text, TableCellProperties? properties = null)
	{
		var cell = new TableCell();
		if (properties is not null) cell.Append(properties);
		cell.Append(P(text));
		return cell;
	}

	public static TableRow Row(params TableCell[] cells) => new(cells);

	public static TableRow HeaderRow(params TableCell[] cells) => new(new OpenXmlElement[] { new TableRowProperties(new TableHeader()) }.Concat(cells));

	public static Table Table(TableProperties? properties, params TableRow[] rows)
	{
		var table = new Table();
		if (properties is not null) table.Append(properties);
		table.Append(rows);
		return table;
	}

	public static TableBorders AllBorders() => new(
		new TopBorder { Val = BorderValues.Single, Size = 4 },
		new BottomBorder { Val = BorderValues.Single, Size = 4 },
		new LeftBorder { Val = BorderValues.Single, Size = 4 },
		new RightBorder { Val = BorderValues.Single, Size = 4 },
		new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4 },
		new InsideVerticalBorder { Val = BorderValues.Single, Size = 4 });

	public static TableBorders NoBorders() => new(
		new TopBorder { Val = BorderValues.None },
		new BottomBorder { Val = BorderValues.Nil },
		new InsideHorizontalBorder { Val = BorderValues.None });

	public static string AddImage(MainDocumentPart main, PartTypeInfo type, byte[] bytes)
	{
		var part = main.AddImagePart(type);
		using (var stream = new MemoryStream(bytes)) part.FeedData(stream);
		return main.GetIdOfPart(part);
	}

	/// <summary>An inline (or floating) picture: 1 px = 9525 EMU.</summary>
	public static Run Image(string relationshipId, long widthPx, long heightPx, string? alt = null, bool floating = false)
	{
		long cx = widthPx * 9525, cy = heightPx * 9525;
		var graphic = new A.Graphic(new A.GraphicData(
			new PIC.Picture(
				new PIC.NonVisualPictureProperties(
					new PIC.NonVisualDrawingProperties { Id = 0U, Name = "image.png" },
					new PIC.NonVisualPictureDrawingProperties()),
				new PIC.BlipFill(new A.Blip { Embed = relationshipId }, new A.Stretch(new A.FillRectangle())),
				new PIC.ShapeProperties(
					new A.Transform2D(new A.Offset { X = 0, Y = 0 }, new A.Extents { Cx = cx, Cy = cy }),
					new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle })))
		{ Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture" });
		var properties = new DW.DocProperties { Id = 1U, Name = "Picture 1", Description = alt };
		OpenXmlElement container = floating
			? new DW.Anchor(
				new DW.SimplePosition { X = 0, Y = 0 },
				new DW.HorizontalPosition(new DW.PositionOffset("0")) { RelativeFrom = DW.HorizontalRelativePositionValues.Column },
				new DW.VerticalPosition(new DW.PositionOffset("0")) { RelativeFrom = DW.VerticalRelativePositionValues.Paragraph },
				new DW.Extent { Cx = cx, Cy = cy },
				new DW.WrapSquare { WrapText = DW.WrapTextValues.BothSides },
				properties,
				graphic)
			{ DistanceFromTop = 0U, DistanceFromBottom = 0U, DistanceFromLeft = 0U, DistanceFromRight = 0U, SimplePos = false, RelativeHeight = 1U, BehindDoc = false, Locked = false, LayoutInCell = true, AllowOverlap = true }
			: new DW.Inline(new DW.Extent { Cx = cx, Cy = cy }, properties, graphic);
		return new Run(new Drawing(container));
	}

	/// <summary>A floating text box (DrawingML shape with w:txbxContent) holding the given paragraphs.</summary>
	public static Run TextBox(params Paragraph[] paragraphs)
	{
		var shape = new WPS.WordprocessingShape(
			new WPS.NonVisualDrawingShapeProperties(),
			new WPS.ShapeProperties(),
			new WPS.TextBoxInfo2(new TextBoxContent(paragraphs)),
			new WPS.TextBodyProperties());
		var graphic = new A.Graphic(new A.GraphicData(shape) { Uri = "http://schemas.microsoft.com/office/word/2010/wordprocessingShape" });
		return new Run(new Drawing(new DW.Inline(
			new DW.Extent { Cx = 952500, Cy = 476250 },
			new DW.DocProperties { Id = 2U, Name = "Text Box 1" },
			graphic)));
	}

	public static Run PageBreak() => new(new Break { Type = BreakValues.Page });

	public static string AddHeader(MainDocumentPart main, params OpenXmlElement[] blocks)
	{
		var part = main.AddNewPart<HeaderPart>();
		part.Header = new Header(blocks);
		part.Header.Save();
		return main.GetIdOfPart(part);
	}

	public static string AddFooter(MainDocumentPart main, params OpenXmlElement[] blocks)
	{
		var part = main.AddNewPart<FooterPart>();
		part.Footer = new Footer(blocks);
		part.Footer.Save();
		return main.GetIdOfPart(part);
	}

	/// <summary>The body's final section properties: default header/footer and page margins (twips).</summary>
	public static SectionProperties Section(string? headerId, string? footerId,
		int top = 1440, int bottom = 1440, uint left = 1440, uint right = 1440, uint header = 720, uint footer = 720,
		bool titlePage = false)
	{
		var section = new SectionProperties();
		if (headerId is not null) section.Append(new HeaderReference { Type = HeaderFooterValues.Default, Id = headerId });
		if (footerId is not null) section.Append(new FooterReference { Type = HeaderFooterValues.Default, Id = footerId });
		section.Append(new PageSize { Width = 12240U, Height = 15840U });
		section.Append(new PageMargin { Top = top, Bottom = bottom, Left = left, Right = right, Header = header, Footer = footer, Gutter = 0U });
		if (titlePage) section.Append(new TitlePage());
		return section;
	}

	/// <summary>"Page X of Y" as Word writes it (PAGE and NUMPAGES fields).</summary>
	public static Paragraph PageXOfY() => P(new OpenXmlElement[] { R("Page ") }
		.Concat(MergeField(" PAGE ", "1"))
		.Append(R(" of "))
		.Concat(MergeField(" NUMPAGES ", "1"))
		.ToArray());
}
