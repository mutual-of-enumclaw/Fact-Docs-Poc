using FapPdfTools.Server.Models;

namespace FapPdfTools.Server.Infrastructure;

/// <summary>
/// Converts between <see cref="FormDefinition"/> (PDF-point coords) and
/// <see cref="FapParseResult"/> (FAP 2400-DPI integer coords) so authored forms
/// can be rendered by the existing <see cref="FapToPdfGenerator"/> pipeline.
/// </summary>
public static class FapFormDefinitionConverter
{
	private const float Scale = 72f / 2400f;     // FAP units â†’ PDF points
	private const float InvScale = 2400f / 72f;  // PDF points â†’ FAP units

	/// <summary>
	/// Convert a <see cref="FormDefinition"/> to the <see cref="FapParseResult"/>
	/// shape expected by <see cref="FapToPdfGenerator.GeneratePdfBytes"/>.
	/// </summary>
	public static (FapParseResult FapResult, FapPageInfo PageInfo) ToFapParseResult(FormDefinition def)
	{
		int F(float v) => (int)Math.Round(v * InvScale);

		var pageInfo = new FapPageInfo(2400, 0, 0, F(def.PageWidth), F(def.PageHeight));
		var pageInfos = Enumerable.Repeat(pageInfo, Math.Max(1, def.PageCount)).ToList();

		var fields = def.Fields.Select((f, i) => new FapField(
			f.Name, f.MaxLength,
			(F(f.Y), F(f.X), F(f.Y + f.Height), F(f.X + f.Width)),
			(f.FontId, 0, 0, 0),
			i, Math.Max(0, f.Page - 1))).ToList();

		var staticTexts = def.StaticTexts.Select((t, i) => new FapStaticText(
			t.Text, t.Text.Length,
			(F(t.Y), F(t.X), F(t.Y + t.Height), F(t.X + t.Width)),
			(t.FontId, 0, 0, 0),
			i, Math.Max(0, t.Page - 1))).ToList();

		var lines = def.Lines.Select((l, i) => new FapLine(
			(F(l.Y1), F(l.X1), F(l.Y2), F(l.X2)),
			(int)Math.Round(l.LineWidth * InvScale), l.Style, i, Math.Max(0, l.Page - 1))).ToList();

		var fapResult = new FapParseResult(
			def.Name, Math.Max(1, def.PageCount), fields, staticTexts, lines, [])
		{
			PageInfos = pageInfos,
		};

		return (fapResult, pageInfo);
	}

	/// <summary>
	/// Seed a <see cref="FormDefinition"/> from a parsed FAP file. All FAP-unit
	/// coordinates are converted to PDF points (round-trip safe with ToFapParseResult).
	/// </summary>
	public static FormDefinition FromFapParseResult(
		FapParseResult fap,
		string name,
		string? sourceFormNumber = null,
		string? sourceEditionDate = null)
	{
		var pi = fap.PageInfos.Count > 0
			? fap.PageInfos[0]
			: new FapPageInfo(2400, 0, 0, 20400, 26400);

		float Px(int col) => (col - pi.OriginCol) * Scale;
		float Py(int row) => (row - pi.OriginRow) * Scale;

		var fields = fap.Fields.Select(f => new FormDefinitionField(
			f.Name, f.PageIndex + 1,
			Px(f.Position.Col1), Py(f.Position.Row1),
			Math.Max(1f, (f.Position.Col2 - f.Position.Col1) * Scale),
			Math.Max(1f, (f.Position.Row2 - f.Position.Row1) * Scale),
			f.FontAttributes.FontId, 0f, false, f.Length)).ToList();

		var staticTexts = fap.StaticTexts.Select(t => new FormDefinitionStaticText(
			t.Text, t.PageIndex + 1,
			Px(t.Position.Col1), Py(t.Position.Row1),
			Math.Max(1f, (t.Position.Col2 - t.Position.Col1) * Scale),
			Math.Max(1f, (t.Position.Row2 - t.Position.Row1) * Scale),
			t.FontAttributes.FontId, 0f, false)).ToList();

		// FAP text areas contain pre-positioned word tokens â€” each token becomes its own static text element.
		var textAreaTokens = fap.TextAreas
			.SelectMany(area => area.Tokens.Select(token => new FormDefinitionStaticText(
				token.Text, area.PageIndex + 1,
				Px(token.Position.Col1), Py(token.Position.Row1),
				Math.Max(1f, (token.Position.Col2 - token.Position.Col1) * Scale),
				Math.Max(1f, (token.Position.Row2 - token.Position.Row1) * Scale),
				token.FontId, 0f, token.IsBold)))
			.ToList();

		var allStaticTexts = staticTexts.Concat(textAreaTokens).ToList();

		var lines = fap.Lines.Select(l => new FormDefinitionLine(
			l.PageIndex + 1,
			Px(l.Position.Col1), Py(l.Position.Row1),
			Px(l.Position.Col2), Py(l.Position.Row2),
			Math.Max(0.5f, l.Width * Scale), l.Style)).ToList();

		return new FormDefinition
		{
			Name = name,
			SourceFormNumber = sourceFormNumber,
			SourceEditionDate = sourceEditionDate,
			PageCount = Math.Max(1, fap.PageCount),
			PageWidth = pi.PageWidth * Scale,
			PageHeight = pi.PageHeight * Scale,
			Fields = fields,
			StaticTexts = allStaticTexts,
			Lines = lines,
		};
	}
}
