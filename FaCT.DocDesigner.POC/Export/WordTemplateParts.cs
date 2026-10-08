using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;

namespace FaCT.DocDesigner.POC.Export;

/// <summary>
/// The Word styles, footer and picture helpers behind the Word template export. The styles are the brand look from
/// <c>moe-document.css</c> (Figtree, the MOE greens, shaded table headers with banded rows) as real Word styles, so a
/// template owner can restyle the exported file in Word and DocGen keeps that formatting when it fills the template.
/// </summary>
internal static class WordTemplateParts
{
	public const string Font = "Figtree";
	public const string Green = "144835";
	public const string Apple = "93CA15";
	public const string Alpine = "448843";
	public const string Ink = "2D2927";
	public const string Mist = "F1F2F2";
	public const string Rule = "D0D4E3";
	public const string Sunshine = "F0B52B";

	/// <summary>7.5 inches, between the PDF's 0.5 inch side margins on Letter paper, in twentieths of a point.</summary>
	public const int ContentWidth = 10800;

	public static void AddStyles(MainDocumentPart main)
	{
		var part = main.AddNewPart<StyleDefinitionsPart>();

		static Style ParagraphStyle(string id, string name, StyleParagraphProperties paragraph, StyleRunProperties run, string basedOn = "Normal") =>
			new(new StyleName { Val = name }, new BasedOn { Val = basedOn }, new NextParagraphStyle { Val = "Normal" }, new PrimaryStyle(), paragraph, run)
			{ Type = StyleValues.Paragraph, StyleId = id };

		part.Styles = new Styles(
			new DocDefaults(
				new RunPropertiesDefault(new RunPropertiesBaseStyle(
					new RunFonts { Ascii = Font, HighAnsi = Font, ComplexScript = Font, EastAsia = Font },
					new Color { Val = Ink },
					new FontSize { Val = "21" },
					new FontSizeComplexScript { Val = "21" },
					new Languages { Val = "en-US" })),
				new ParagraphPropertiesDefault(new ParagraphPropertiesBaseStyle(
					new SpacingBetweenLines { After = "60", Line = "264", LineRule = LineSpacingRuleValues.Auto }))),

			new Style(new StyleName { Val = "Normal" }, new PrimaryStyle()) { Type = StyleValues.Paragraph, Default = true, StyleId = "Normal" },

			// the document title next to the logo, right aligned
			ParagraphStyle("MoeTitle", "Moe Title",
				new StyleParagraphProperties(new SpacingBetweenLines { After = "0" }, new Justification { Val = JustificationValues.Right }),
				new StyleRunProperties(new Bold(), new Color { Val = Green }, new FontSize { Val = "40" })),

			// a title on its own
			ParagraphStyle("MoeH1", "Moe Heading 1",
				new StyleParagraphProperties(new KeepNext(), new SpacingBetweenLines { After = "120" }),
				new StyleRunProperties(new Bold(), new Color { Val = Green }, new FontSize { Val = "40" })),

			// section headings: uppercase alpine green
			ParagraphStyle("MoeHeading", "Moe Heading",
				new StyleParagraphProperties(new KeepNext(), new SpacingBetweenLines { Before = "300", After = "100" }),
				new StyleRunProperties(new Bold(), new Caps(), new Color { Val = Alpine }, new Spacing { Val = 8 }, new FontSize { Val = "25" })),

			ParagraphStyle("MoeSubheading", "Moe Subheading",
				new StyleParagraphProperties(new KeepNext(), new SpacingBetweenLines { Before = "120", After = "60" }),
				new StyleRunProperties(new Bold(), new Caps(), new Color { Val = Alpine }, new Spacing { Val = 6 }, new FontSize { Val = "21" })),

			ParagraphStyle("MoeLocation", "Moe Card Title",
				new StyleParagraphProperties(new KeepNext(), new SpacingBetweenLines { After = "60" }),
				new StyleRunProperties(new Bold(), new Color { Val = Green }, new FontSize { Val = "23" })),

			// grey callout with a sunshine bar on the left
			ParagraphStyle("MoeCallout", "Moe Callout",
				new StyleParagraphProperties(
					new ParagraphBorders(new LeftBorder { Val = BorderValues.Single, Size = 24, Space = 6, Color = Sunshine }),
					new Shading { Val = ShadingPatternValues.Clear, Fill = Mist },
					new SpacingBetweenLines { Before = "60", After = "140" },
					new Indentation { Left = "140", Right = "140" }),
				new StyleRunProperties()),

			ParagraphStyle("MoeCell", "Moe Table Text",
				new StyleParagraphProperties(new SpacingBetweenLines { Before = "50", After = "50" }),
				new StyleRunProperties(new FontSize { Val = "20" })),

			ParagraphStyle("MoeCellRight", "Moe Table Number",
				new StyleParagraphProperties(new SpacingBetweenLines { Before = "50", After = "50" }, new Justification { Val = JustificationValues.Right }),
				new StyleRunProperties(new FontSize { Val = "20" }),
				basedOn: "MoeCell"),

			ParagraphStyle("MoeFooter", "Moe Footer",
				new StyleParagraphProperties(
					new Tabs(new TabStop { Val = TabStopValues.Right, Position = ContentWidth }),
					new SpacingBetweenLines { After = "0" }),
				new StyleRunProperties(new Color { Val = Green }, new FontSize { Val = "20" })),

			// bold green lead-in used for "Policy:", "Fire score:" and so on
			new Style(new StyleName { Val = "Moe Label" }, new PrimaryStyle(),
				new StyleRunProperties(new Bold(), new Color { Val = Green }))
			{ Type = StyleValues.Character, StyleId = "MoeLabel" },

			// data tables: dark green header with white text, light banded rows
			new Style(
				new StyleName { Val = "Moe Table" },
				new PrimaryStyle(),
				new StyleTableProperties(
					new TableStyleRowBandSize { Val = 1 },
					new TableBorders(
						new BottomBorder { Val = BorderValues.Single, Size = 4, Color = Rule },
						new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4, Color = Rule }),
					new TableCellMarginDefault(
						new TopMargin { Width = "20", Type = TableWidthUnitValues.Dxa },
						new TableCellLeftMargin { Width = 110, Type = TableWidthValues.Dxa },
						new BottomMargin { Width = "20", Type = TableWidthUnitValues.Dxa },
						new TableCellRightMargin { Width = 110, Type = TableWidthValues.Dxa })),
				new TableStyleProperties(
					new RunPropertiesBaseStyle(new Bold(), new Color { Val = "FFFFFF" }),
					new TableStyleConditionalFormattingTableCellProperties(new Shading { Val = ShadingPatternValues.Clear, Fill = Green }))
				{ Type = TableStyleOverrideValues.FirstRow },
				new TableStyleProperties(
					new TableStyleConditionalFormattingTableCellProperties(new Shading { Val = ShadingPatternValues.Clear, Fill = Mist }))
				{ Type = TableStyleOverrideValues.Band2Horizontal })
			{ Type = StyleValues.Table, StyleId = "MoeTable" });

		part.Styles.Save();
	}

	/// <summary>Footer: the company name on the left and "Page X of Y" on the right, like the PDF footer.</summary>
	public static FooterPart AddFooter(MainDocumentPart main)
	{
		var part = main.AddNewPart<FooterPart>();
		var paragraph = new Paragraph(new ParagraphProperties(new ParagraphStyleId { Val = "MoeFooter" }));
		paragraph.Append(Text("Mutual Of Enumclaw"));
		paragraph.Append(new Run(new TabChar()));
		paragraph.Append(Text("Page "));
		paragraph.Append(Field("PAGE"));
		paragraph.Append(Text(" of "));
		paragraph.Append(Field("NUMPAGES"));
		part.Footer = new Footer(paragraph);
		part.Footer.Save();
		return part;
	}

	// The PDF's standard page: Letter, 0.5 inch margins (0.6 at the bottom, where the footer sits).
	public static SectionProperties Section(MainDocumentPart main, FooterPart footer) => new(
		new FooterReference { Type = HeaderFooterValues.Default, Id = main.GetIdOfPart(footer) },
		new PageSize { Width = 12240, Height = 15840 },
		new PageMargin { Top = 720, Right = 720, Bottom = 864, Left = 720, Header = 360, Footer = 360, Gutter = 0 });

	public static Run Text(string text) => new(new Text(text) { Space = SpaceProcessingModeValues.Preserve });

	public static IEnumerable<Run> Field(string instruction)
	{
		yield return new Run(new FieldChar { FieldCharType = FieldCharValues.Begin });
		yield return new Run(new FieldCode($" {instruction} ") { Space = SpaceProcessingModeValues.Preserve });
		yield return new Run(new FieldChar { FieldCharType = FieldCharValues.Separate });
		yield return new Run(new Text("1"));
		yield return new Run(new FieldChar { FieldCharType = FieldCharValues.End });
	}

	/// <summary>An inline picture, widthInches wide with the height that keeps the picture's shape, stored in the owner
	/// (the document, or the header / footer it sits in).</summary>
	public static Drawing Picture(OpenXmlPartContainer owner, byte[] bytes, PartTypeInfo type, int pixelWidth, int pixelHeight, double widthInches, uint id, string name)
	{
		var part = owner switch
		{
			MainDocumentPart main => main.AddImagePart(type),
			HeaderPart header => header.AddImagePart(type),
			FooterPart footer => footer.AddImagePart(type),
			_ => throw new ArgumentException("Pictures go in the document, a header or a footer.", nameof(owner))
		};
		using (var stream = new MemoryStream(bytes))
		{
			part.FeedData(stream);
		}

		var cx = (long)(widthInches * 914400);
		var cy = cx * pixelHeight / pixelWidth;
		var relationshipId = owner.GetIdOfPart(part);

		return new Drawing(new DW.Inline(
			new DW.Extent { Cx = cx, Cy = cy },
			new DW.EffectExtent { LeftEdge = 0, TopEdge = 0, RightEdge = 0, BottomEdge = 0 },
			new DW.DocProperties { Id = id, Name = name },
			new DW.NonVisualGraphicFrameDrawingProperties(new A.GraphicFrameLocks { NoChangeAspect = true }),
			new A.Graphic(new A.GraphicData(
				new PIC.Picture(
					new PIC.NonVisualPictureProperties(new PIC.NonVisualDrawingProperties { Id = 0U, Name = name }, new PIC.NonVisualPictureDrawingProperties()),
					new PIC.BlipFill(new A.Blip { Embed = relationshipId }, new A.Stretch(new A.FillRectangle())),
					new PIC.ShapeProperties(
						new A.Transform2D(new A.Offset { X = 0, Y = 0 }, new A.Extents { Cx = cx, Cy = cy }),
						new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle })))
			{ Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture" }))
		{ DistanceFromTop = 0U, DistanceFromBottom = 0U, DistanceFromLeft = 0U, DistanceFromRight = 0U });
	}

	/// <summary>Type and pixel size of a PNG, JPEG or GIF (read from its header), or false for anything else.</summary>
	public static bool TryReadImage(byte[] b, out PartTypeInfo type, out int width, out int height)
	{
		type = ImagePartType.Png;
		width = height = 0;

		if (b.Length > 24 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47)
		{
			width = (b[16] << 24) | (b[17] << 16) | (b[18] << 8) | b[19];
			height = (b[20] << 24) | (b[21] << 16) | (b[22] << 8) | b[23];
		}
		else if (b.Length > 10 && b[0] == 'G' && b[1] == 'I' && b[2] == 'F')
		{
			type = ImagePartType.Gif;
			width = b[6] | (b[7] << 8);
			height = b[8] | (b[9] << 8);
		}
		else if (b.Length > 10 && b[0] == 0xFF && b[1] == 0xD8)
		{
			type = ImagePartType.Jpeg;
			var i = 2;
			while (i + 9 < b.Length)
			{
				if (b[i] != 0xFF)
				{
					i++;
					continue;
				}

				var marker = b[i + 1];
				if (marker is 0xD8 or 0x01 || marker is >= 0xD0 and <= 0xD7)
				{
					i += 2;
					continue;
				}

				if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
				{
					height = (b[i + 5] << 8) | b[i + 6];
					width = (b[i + 7] << 8) | b[i + 8];
					break;
				}

				i += 2 + ((b[i + 2] << 8) | b[i + 3]);
			}
		}

		return width > 0 && height > 0;
	}
}
