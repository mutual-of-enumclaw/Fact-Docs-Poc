using System.Drawing;
using Microsoft.Extensions.Logging;
using Spire.Pdf;
using Spire.Pdf.Fields;
using Spire.Pdf.Graphics;
using Spire.Pdf.Widget;
using FapPdfTools.Server.Models;

namespace FapPdfTools.Server.Infrastructure;

public record FapPageInfo(int Resolution, int OriginRow, int OriginCol, int PageWidth, int PageHeight);

public class FapToPdfGenerator
{
    private readonly ILogger<FapToPdfGenerator> _logger;
    private readonly FxrFontLibrary _fxr;

    private const float FapDpi = 2400f;
    private const float PdfPointsPerInch = 72f;
    private const float Scale = PdfPointsPerInch / FapDpi;

    private static readonly FapPageInfo DefaultPageInfo = new(2400, 0, 0, 20400, 26400);
    private static readonly HashSet<int> BoldFontIds = [14110, 14112, 14116, 14118];

    public FapToPdfGenerator(ILogger<FapToPdfGenerator> logger, FxrFontLibrary fxr)
    {
        _logger = logger;
        _fxr = fxr;
    }

    public (string OutputPath, IReadOnlyList<string> FieldNames) GeneratePdf(
        FapParseResult fapResult, DdtParseResult? ddtResult,
        string outputPath, FapPageInfo? pageInfo = null)
    {
        var (pdfBytes, fieldNames) = GeneratePdfBytes(fapResult, ddtResult, pageInfo);

        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllBytes(outputPath, pdfBytes);

        _logger.LogInformation("Generated PDF {Path} with {Fields} fields, {Pages} pages",
            outputPath, fieldNames.Count, fapResult.PageCount);

        return (outputPath, fieldNames);
    }

    /// <summary>Render an authored <see cref="FormDefinition"/> to a fillable PDF.</summary>
    public (byte[] PdfBytes, IReadOnlyList<string> FieldNames) GeneratePdfBytes(
        FapPdfTools.Server.Models.FormDefinition def)
    {
        var (fapResult, _) = FapFormDefinitionConverter.ToFapParseResult(def);
        return GeneratePdfBytes(fapResult, null, null);
    }

    /// <summary>Generate PDF and return it as a byte array (for streaming to the browser).</summary>
    public (byte[] PdfBytes, IReadOnlyList<string> FieldNames) GeneratePdfBytes(
        FapParseResult fapResult, DdtParseResult? ddtResult, FapPageInfo? pageInfo = null)
    {
        var ddtLookup = BuildDdtLookup(ddtResult);
        var doc = new PdfDocument();
        var fonts = new FontCache(_fxr);

        // Determine how many pages to create
        int totalPages = Math.Max(1, fapResult.PageCount);

        // Create all pages up front, using per-page dimensions when available.
        // Use zero margins so the canvas is not clipped to Spire's default page
        // margins — FAP content is positioned in absolute, full-page coordinates,
        // and any non-zero margin would clip the right/bottom edges of the form.
        var pages = new List<PdfPageBase>();
        for (int i = 0; i < totalPages; i++)
        {
            var pi = GetPageInfo(fapResult, i, pageInfo);
            float w = pi.PageWidth * Scale;
            float h = pi.PageHeight * Scale;
            pages.Add(doc.Pages.Add(new SizeF(w, h), new PdfMargins(0)));
        }

        // Render elements grouped by page
        for (int i = 0; i < totalPages; i++)
        {
            var pi = GetPageInfo(fapResult, i, pageInfo);
            var page = pages[i];

            RenderLines(page.Canvas, fapResult.Lines.Where(e => e.PageIndex == i).ToList(), pi);
            RenderStaticTexts(page.Canvas, fapResult.StaticTexts.Where(e => e.PageIndex == i).ToList(), pi, fonts);
            RenderTextAreas(page.Canvas, fapResult.TextAreas.Where(e => e.PageIndex == i).ToList(), pi, fonts);
        }

        // Render fields across all pages (needs doc-level form)
        var fieldNames = new List<string>();
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < totalPages; i++)
        {
            var pi = GetPageInfo(fapResult, i, pageInfo);
            var pageFields = fapResult.Fields.Where(e => e.PageIndex == i).ToList();
            fieldNames.AddRange(RenderFields(doc, pages[i], pageFields, ddtLookup, pi, usedNames, fonts));
        }

        using var ms = new MemoryStream();
        doc.SaveToStream(ms);
        doc.Close();
        doc.Dispose();

        return (ms.ToArray(), fieldNames);
    }

    /// <summary>Get page info for a specific page index, falling back to defaults.</summary>
    private static FapPageInfo GetPageInfo(FapParseResult fapResult, int pageIndex, FapPageInfo? fallback)
    {
        if (fapResult.PageInfos.Count > pageIndex)
            return fapResult.PageInfos[pageIndex];
        return fallback ?? DefaultPageInfo;
    }

    public static FapPageInfo? ParseHLine(string fapFilePath)
    {
        if (!File.Exists(fapFilePath)) return null;
        using var reader = new StreamReader(fapFilePath);
        var firstLine = reader.ReadLine();
        if (firstLine == null || !firstLine.StartsWith("H,", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            var rest = firstLine[2..];
            var ci = rest.IndexOf(',');
            var resolution = int.Parse(rest[..ci]);
            var op1 = rest.IndexOf('(');
            var cp1 = rest.IndexOf(')');
            var originParts = rest[(op1 + 1)..cp1].Split(',');
            var op2 = rest.IndexOf('(', cp1);
            var cp2 = rest.IndexOf(')', op2);
            var bp = rest[(op2 + 1)..cp2].Split(',');
            return new FapPageInfo(resolution, int.Parse(originParts[0]), int.Parse(originParts[1]),
                int.Parse(bp[3]), int.Parse(bp[2]));
        }
        catch { return null; }
    }

    // -----------------------------------------------------------------------
    // Rendering
    // -----------------------------------------------------------------------

    private void RenderLines(PdfCanvas canvas, IReadOnlyList<FapLine> lines, FapPageInfo pi)
    {
        foreach (var line in lines)
        {
            var (row1, col1, row2, col2) = line.Position;
            float x1 = ToX(col1, pi), y1 = ToY(row1, pi), x2 = ToX(col2, pi), y2 = ToY(row2, pi);
            var pen = new PdfPen(PdfBrushes.Black, Math.Max(0.5f, line.Width * Scale));
            if (Math.Abs(row1 - row2) <= 10 && Math.Abs(col1 - col2) > 10) canvas.DrawLine(pen, x1, y1, x2, y1);
            else if (Math.Abs(col1 - col2) <= 10 && Math.Abs(row1 - row2) > 10) canvas.DrawLine(pen, x1, y1, x1, y2);
            else canvas.DrawRectangle(pen, x1, y1, x2 - x1, y2 - y1);
        }
    }

    private void RenderStaticTexts(PdfCanvas canvas, IReadOnlyList<FapStaticText> texts, FapPageInfo pi, FontCache fonts)
    {
        foreach (var text in texts)
        {
            float x = ToX(text.Position.Col1, pi), y = ToY(text.Position.Row1, pi);
            canvas.DrawString(text.Text, fonts.Get(text.FontAttributes.FontId, text.Position.Row2 - text.Position.Row1),
                PdfBrushes.Black, x, y);
        }
    }

    private void RenderTextAreas(PdfCanvas canvas, IReadOnlyList<FapTextArea> areas, FapPageInfo pi, FontCache fonts)
    {
        // Each M,TT token is a fully pre-positioned word. Draw it at its own
        // (Col1, Row1) with its own font so columns and mixed bold/regular runs on
        // the same visual line don't collapse to the left margin and overlap.
        foreach (var area in areas)
        {
            foreach (var token in area.Tokens)
            {
                if (string.IsNullOrEmpty(token.Text)) continue;
                float x = ToX(token.Position.Col1, pi);
                float y = ToY(token.Position.Row1, pi);
                var font = fonts.Get(token.FontId, token.Position.Row2 - token.Position.Row1);
                canvas.DrawString(token.Text, font, PdfBrushes.Black, x, y);
            }
        }
    }

    private List<string> RenderFields(PdfDocument doc, PdfPageBase page, IReadOnlyList<FapField> fields,
        Dictionary<string, DdtFieldRule> ddtLookup, FapPageInfo pi, HashSet<string> usedNames, FontCache fonts)
    {
        var fieldNames = new List<string>();
        foreach (var field in fields)
        {
            float x = ToX(field.Position.Col1, pi), y = ToY(field.Position.Row1, pi);
            // Use the field's true FAP width/height. Do NOT pad narrow fields out to an
            // artificial minimum — a too-wide checkbox/option field bleeds its (opaque)
            // background over the static label that sits immediately to its right.
            float realH = (field.Position.Row2 - field.Position.Row1) * Scale;
            float realW = (field.Position.Col2 - field.Position.Col1) * Scale;
            float h = realH > 1f ? realH : 12f;
            float w = realW > 1f ? realW : h;   // square fallback for zero-width fields

            var name = field.Name;
            if (usedNames.Contains(name)) { int s = 2; while (usedNames.Contains($"{name}_{s}")) s++; name = $"{name}_{s}"; }
            usedNames.Add(name);

            var tb = new PdfTextBoxField(page, name)
            {
                Bounds = new RectangleF(x, y, w, h),
                Font = fonts.Get(field.FontAttributes.FontId, field.Position.Row2 - field.Position.Row1),
                // Transparent background so the field never paints over the static
                // form text/labels behind it (Documaker form fields are transparent).
                BackColor = new PdfRGBColor(),
                // Thin light-blue outline so the editable region is identifiable to a
                // user without obscuring the underlying form text.
                BorderWidth = 0.75f,
                BorderStyle = PdfBorderStyle.Solid,
                BorderColor = new PdfRGBColor(Color.FromArgb(80, 140, 215)),
            };
            if (ddtLookup.TryGetValue(field.Name, out var rule)) tb.ToolTip = $"{rule.Method}: {rule.Source}";
            doc.Form.Fields.Add(tb);
            fieldNames.Add(name);
        }
        return fieldNames;
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static float ToX(int col, FapPageInfo pi) => (col - pi.OriginCol) * Scale;
    private static float ToY(int row, FapPageInfo pi) => (row - pi.OriginRow) * Scale;

    private static Dictionary<string, DdtFieldRule> BuildDdtLookup(DdtParseResult? ddt)
    {
        if (ddt == null) return [];
        var lookup = new Dictionary<string, DdtFieldRule>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in ddt.FieldRules) lookup.TryAdd(r.FieldName, r);
        return lookup;
    }

    // -----------------------------------------------------------------------
    // Field population
    // -----------------------------------------------------------------------

    /// <summary>
    /// Populate AcroForm text fields in an already-generated PDF. When <paramref name="flatten"/>
    /// is true the values are burned into the page (interactive widgets removed), producing
    /// output that looks like Documaker's printed form rather than a fillable PDF.
    /// </summary>
    public byte[] FillFields(byte[] pdfBytes, IReadOnlyDictionary<string, string> values, bool flatten)
    {
        using var ms = new MemoryStream(pdfBytes);
        var doc = new PdfDocument(ms);
        try
        {
            if (doc.Form is PdfFormWidget form)
            {
                for (int i = 0; i < form.FieldsWidget.Count; i++)
                {
                    if (form.FieldsWidget[i] is PdfTextBoxFieldWidget tb &&
                        values.TryGetValue(tb.Name, out var v) && v != null)
                    {
                        tb.Text = v;
                    }
                }
                if (flatten) form.IsFlatten = true;
            }

            using var outMs = new MemoryStream();
            doc.SaveToStream(outMs);
            return outMs.ToArray();
        }
        finally
        {
            doc.Close();
            doc.Dispose();
        }
    }

    /// <summary>
    /// Describe every fillable field (name, page, PDF-point bounds, FXR font, DDT rule) so a
    /// UI can render an input per field. Names are de-duplicated the same way as RenderFields.
    /// </summary>
    public IReadOnlyList<FieldDescriptor> GetFieldDescriptors(
        FapParseResult fap, DdtParseResult? ddt, FapPageInfo? fallback)
    {
        var ddtLookup = BuildDdtLookup(ddt);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<FieldDescriptor>();
        int totalPages = Math.Max(1, fap.PageCount);

        for (int i = 0; i < totalPages; i++)
        {
            var pi = GetPageInfo(fap, i, fallback);
            foreach (var field in fap.Fields.Where(f => f.PageIndex == i))
            {
                var name = field.Name;
                if (used.Contains(name)) { int s = 2; while (used.Contains($"{name}_{s}")) s++; name = $"{name}_{s}"; }
                used.Add(name);

                var fx = _fxr.Resolve(field.FontAttributes.FontId);
                ddtLookup.TryGetValue(field.Name, out var rule);

                float fh = (field.Position.Row2 - field.Position.Row1) * Scale;
                float fw = (field.Position.Col2 - field.Position.Col1) * Scale;
                float h = fh > 1f ? fh : 12f;
                float w = fw > 1f ? fw : h;

                list.Add(new FieldDescriptor(
                    name, i + 1,
                    ToX(field.Position.Col1, pi), ToY(field.Position.Row1, pi),
                    w, h,
                    field.FontAttributes.FontId, fx?.PointSize ?? 0f, fx?.Bold ?? false,
                    field.Length, rule?.Method ?? string.Empty, rule?.Source ?? string.Empty));
            }
        }
        return list;
    }
}

/// <summary>
/// Per-document cache of PDF fonts resolved from the FXR. Embeds TrueType system fonts
/// (Arial / Times New Roman / Courier New) at the exact FXR point size and style so the
/// output matches Documaker's own LPDF driver, instead of approximating size from row height.
/// </summary>
internal sealed class FontCache
{
    private readonly FxrFontLibrary _fxr;
    private readonly Dictionary<int, PdfFontBase> _cache = [];

    public FontCache(FxrFontLibrary fxr) => _fxr = fxr;

    public PdfFontBase Get(int fontId, int rowHeightFap)
    {
        if (_cache.TryGetValue(fontId, out var cached)) return cached;
        var font = Build(fontId, rowHeightFap);
        _cache[fontId] = font;
        return font;
    }

    private PdfFontBase Build(int fontId, int rowHeightFap)
    {
        var fx = _fxr.Resolve(fontId);
        if (fx != null && fx.PointSize > 0)
        {
            var style = System.Drawing.FontStyle.Regular;
            if (fx.Bold) style |= System.Drawing.FontStyle.Bold;
            if (fx.Italic) style |= System.Drawing.FontStyle.Italic;
            try
            {
                var sysFont = new System.Drawing.Font(MapFamily(fx.Typeface), fx.PointSize, style);
                return new PdfTrueTypeFont(sysFont, true);
            }
            catch
            {
                return new PdfFont(MapBase14(fx.Typeface), fx.PointSize,
                    fx.Bold ? PdfFontStyle.Bold : PdfFontStyle.Regular);
            }
        }

        // No FXR entry: approximate the point size from the row height.
        float pts = Math.Clamp(rowHeightFap * (72f / 2400f), 6f, 24f);
        return new PdfFont(PdfFontFamily.Helvetica, pts, PdfFontStyle.Regular);
    }

    private static string MapFamily(string typeface)
    {
        if (typeface.Contains("Times", StringComparison.OrdinalIgnoreCase)) return "Times New Roman";
        if (typeface.Contains("Courier", StringComparison.OrdinalIgnoreCase)) return "Courier New";
        // Arial / Univers / Helvetica / anything else -> Arial (metric-compatible).
        return "Arial";
    }

    private static PdfFontFamily MapBase14(string typeface)
    {
        if (typeface.Contains("Times", StringComparison.OrdinalIgnoreCase)) return PdfFontFamily.TimesRoman;
        if (typeface.Contains("Courier", StringComparison.OrdinalIgnoreCase)) return PdfFontFamily.Courier;
        return PdfFontFamily.Helvetica;
    }
}
