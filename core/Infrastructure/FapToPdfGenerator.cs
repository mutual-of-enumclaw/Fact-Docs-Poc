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

    /// <summary>Generate PDF and return it as a byte array (for streaming to the browser).
    /// <paramref name="overrides"/> (field name → <see cref="FieldOverride"/>) tunes per-field
    /// font size / max length / bold at build time.</summary>
    public (byte[] PdfBytes, IReadOnlyList<string> FieldNames) GeneratePdfBytes(
        FapParseResult fapResult, DdtParseResult? ddtResult, FapPageInfo? pageInfo = null,
        IReadOnlyDictionary<string, FieldOverride>? overrides = null)
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

            // Obstacles that a field must not expand into (static text / flowed text / lines on
            // this page), as PDF-point bounds (Left, Top, Right, Bottom). Used to size each field
            // to the available blank space to its right without overlapping other wording.
            var obstacles = new List<(float L, float T, float R, float B)>();
            foreach (var st in fapResult.StaticTexts.Where(e => e.PageIndex == i))
                obstacles.Add((ToX(st.Position.Col1, pi), ToY(st.Position.Row1, pi), ToX(st.Position.Col2, pi), ToY(st.Position.Row2, pi)));
            foreach (var ta in fapResult.TextAreas.Where(e => e.PageIndex == i))
                obstacles.Add((ToX(ta.Position.Col1, pi), ToY(ta.Position.Row1, pi), ToX(ta.Position.Col2, pi), ToY(ta.Position.Row2, pi)));
            foreach (var ln in fapResult.Lines.Where(e => e.PageIndex == i))
                obstacles.Add((ToX(ln.Position.Col1, pi), ToY(ln.Position.Row1, pi), ToX(ln.Position.Col2, pi), ToY(ln.Position.Row2, pi)));

            fieldNames.AddRange(RenderFields(doc, pages[i], pageFields, ddtLookup, pi, usedNames, fonts, fapResult.InlineFieldPositions, overrides, obstacles));
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
        // Each M,TT token is fully pre-positioned by Documaker. We apply a small per-character
        // spacing correction so Spire.PDF renders each token at Documaker's exact FXR-measured
        // width, not at the FAP slot width (which can extend to end-of-line padding).
        //
        // KEY: use FxrFont.MeasureFap(text) as the target — that's the tight text width from
        // Documaker's own character-advance tables. Using (Col2-Col1) instead would expand
        // line-padded tokens like "Mutual of Enumclaw..." into "M u t u a l  o f ..."
        //
        // For multi-column areas (overlapping row bands) the left column is clip-fenced so
        // metric variance doesn't bleed into the adjacent column.
        foreach (var area in areas)
        {
            if (area.Tokens.Count == 0) continue;

            float areaX = ToX(area.Position.Col1, pi);
            float areaY = ToY(area.Position.Row1, pi);
            float areaW = (area.Position.Col2 - area.Position.Col1) * Scale;
            float areaH = (area.Position.Row2 - area.Position.Row1) * Scale;

            // Clip only columns that have another area to their RIGHT in the same row band.
            // Right-most columns and single-column areas are not clipped.
            bool needsClip = areaW > 1f && areaH > 1f && areas.Any(other =>
                !ReferenceEquals(other, area) &&
                other.Tokens.Count > 0 &&
                other.Position.Col1 > area.Position.Col1 &&
                other.Position.Row1 < area.Position.Row2 &&
                other.Position.Row2 > area.Position.Row1);

            PdfGraphicsState? state = needsClip ? canvas.Save() : null;
            if (needsClip)
            {
                // Add a small right-pad (1.5pt) to absorb accumulated floating-point rounding
                // across a full line of tokens without cutting off the last character.
                var clipPath = new PdfPath();
                clipPath.AddRectangle(new RectangleF(areaX, areaY, areaW + 50 * Scale, areaH));
                canvas.SetClip(clipPath);
            }

            foreach (var token in area.Tokens)
            {
                // IsFieldPlaceholder tokens are inline "X" markers for A,T1 field anchors.
                // Skip them — the PDF form field widget renders in their place.
                if (token.IsFieldPlaceholder || string.IsNullOrEmpty(token.Text)) continue;

                float x = ToX(token.Position.Col1, pi);
                float y = ToY(token.Position.Row1, pi);
                var font = fonts.Get(token.FontId, token.Position.Row2 - token.Position.Row1);

                // Use FXR-measured width as target (not FAP slot Col2-Col1).
                // FXR gives the tight text-advance sum from Documaker's own glyph tables.
                // This avoids spurious expansion on line-padded tokens AND keeps the
                // correction small (< 1.5pt/char) so it only compensates metric variance.
                float naturalW = font.MeasureString(token.Text).Width;
                var fxrFont = fonts.GetFxr(token.FontId);
                float fxrW = fxrFont != null ? fxrFont.MeasureFap(token.Text) * Scale : 0;

                if (fxrW > 0.5f && token.Text.Length > 0)
                {
                    float charSpacing = (fxrW - naturalW) / token.Text.Length;
                    // Only apply when the correction is small — larger values indicate a
                    // font substitution mismatch (e.g. condensed vs. regular) that charSpacing
                    // can't fix cleanly. In those cases natural rendering is less distorted.
                    if (Math.Abs(charSpacing) <= 1.5f)
                    {
                        canvas.DrawString(token.Text, font, PdfBrushes.Black, x, y,
                            new PdfStringFormat { CharacterSpacing = charSpacing });
                        continue;
                    }
                }

                canvas.DrawString(token.Text, font, PdfBrushes.Black, x, y);
            }

            if (state != null) canvas.Restore(state);
        }
    }

    private List<string> RenderFields(PdfDocument doc, PdfPageBase page, IReadOnlyList<FapField> fields,
        Dictionary<string, DdtFieldRule> ddtLookup, FapPageInfo pi, HashSet<string> usedNames, FontCache fonts,
        IReadOnlyDictionary<string, (int Row1, int Col1, int Row2, int Col2)>? inlinePositions = null,
        IReadOnlyDictionary<string, FieldOverride>? overrides = null,
        IReadOnlyList<(float L, float T, float R, float B)>? obstacles = null)
    {
        var fieldNames = new List<string>();
        foreach (var field in fields)
        {
            // Prefer the inline position (from A,T1 anchor in M,TT flow) when available.
            // The F, field's explicit position is sometimes a phantom data-capture position
            // in the left margin rather than the true visual position on the form.
            (int Row1, int Col1, int Row2, int Col2) inlinePos = default;
            bool hasInline = inlinePositions != null && inlinePositions.TryGetValue(field.Name, out inlinePos);
            var pos = hasInline ? inlinePos : field.Position;

            float x = ToX(pos.Col1, pi), y = ToY(pos.Row1, pi);
            float realH = (pos.Row2 - pos.Row1) * Scale;
            float h = realH > 1f ? realH : 12f;

            float w;
            if (hasInline)
            {
                // Use the actual Col2 from the extended inline position. The parser extends
                // Col2 through blank continuation tokens so this is the true visual width
                // Documaker allocated for the field value (not just the 1-char X placeholder).
                float realW = (pos.Col2 - pos.Col1) * Scale;
                w = realW > 1f ? realW : h;
            }
            else
            {
                // Use the field's FAP rect. Do NOT pad narrow fields — a too-wide
                // checkbox/option field bleeds its background over adjacent labels.
                float realW = (field.Position.Col2 - field.Position.Col1) * Scale;
                w = realW > 1f ? realW : h;
            }

            var name = field.Name;
            if (usedNames.Contains(name)) { int s = 2; while (usedNames.Contains($"{name}_{s}")) s++; name = $"{name}_{s}"; }
            usedNames.Add(name);

            // Per-field override (keyed by the field's FAP name). Falls back to FXR/FAP defaults.
            FieldOverride? ov = null;
            overrides?.TryGetValue(field.Name, out ov);

            // Intended point size: override, else the FXR size for the field's FontId, else a
            // height-derived fallback. This is the size the value should render at.
            var fx = fonts.GetFxr(field.FontAttributes.FontId);
            float fallbackPt = Math.Clamp((field.Position.Row2 - field.Position.Row1) * Scale, 6f, 24f);
            float basePt = ov?.PointSize ?? (fx is { PointSize: > 0 } ? fx.PointSize : fallbackPt);

            PdfFontBase font = ov?.PointSize is float ps && ps > 0
                ? fonts.GetAtSize(field.FontAttributes.FontId, ps, ov.Bold)
                : fonts.Get(field.FontAttributes.FontId, field.Position.Row2 - field.Position.Row1);

            // The flattened AcroForm field auto-sizes its text to the box height (it ignores the
            // nominal font), so a short FAP rect renders the value tiny (a 10pt policy number came
            // out ~3.8pt in a 9.4pt box). Empirically rendered ≈ 0.6 x boxHeight, so size the box
            // to ~1.75x the intended point size — centered on the original rect — to land the value
            // back near its intended size (≈10-11pt) without overshooting.
            float minH = basePt * 1.75f;
            if (h < minH) { y -= (minH - h) / 2f; h = minH; }

            // Legacy forms often define a field ~1 char wide and let the value overflow right;
            // an AcroForm field clips to its box instead, so a long value gets cut off. Expand the
            // box rightward into the blank space on this row — but stop just before the next piece
            // of wording / line so it never overlaps other content.
            if (obstacles != null)
            {
                float centerY = y + h / 2f;
                const float tol = 2f;

                // (a) A short prefix the form prints at the start of the blank (e.g. "$") can sit
                // just inside the field box; left-aligned text would render on top of it. Start the
                // value just after such a prefix.
                float leftStart = x;
                foreach (var o in obstacles)
                {
                    bool onRow = o.T - tol <= centerY && o.B + tol >= centerY;
                    bool shortPrefix = (o.R - o.L) <= 14f;            // e.g. "$", ":", "#"
                    bool atFieldStart = o.L >= x - 2f && o.L < x + 20f;
                    if (onRow && shortPrefix && atFieldStart && o.R + 2f > leftStart)
                        leftStart = o.R + 2f;
                }
                float dx = leftStart - x;
                if (dx > 0 && dx < w) { x = leftStart; w -= dx; }

                // (b) Expand right into the blank space up to the next obstacle on this row, so a
                // long value isn't clipped — but never past the next wording/line.
                float availRight = pi.PageWidth * Scale - 4f; // page right margin
                foreach (var o in obstacles)
                {
                    bool onRow = o.T - tol <= centerY && o.B + tol >= centerY;
                    if (onRow && o.L > x + 2f && o.L < availRight)
                        availRight = o.L;
                }
                float expanded = availRight - 3f - x; // leave a small gap before the neighbor
                if (expanded > w) w = expanded;
            }

            var tb = new PdfTextBoxField(page, name)
            {
                Bounds = new RectangleF(x, y, w, h),
                Font = font,
                // Transparent background so the field never paints over the static
                // form text/labels behind it (Documaker form fields are transparent).
                BackColor = new PdfRGBColor(),
                // Thin light-blue outline so the editable region is identifiable to a
                // user without obscuring the underlying form text.
                BorderWidth = 0.75f,
                BorderStyle = PdfBorderStyle.Solid,
                BorderColor = new PdfRGBColor(Color.FromArgb(80, 140, 215)),
            };

            // Max length: override wins, else the FAP field's intended character length.
            int maxLen = ov?.MaxLength ?? field.Length;
            if (maxLen > 0) tb.MaxLength = maxLen;

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
                    if (form.FieldsWidget[i] is PdfTextBoxFieldWidget tb)
                    {
                        if (values.TryGetValue(tb.Name, out var v) && v != null)
                        {
                            // The field's MaxLength is a UI cap for interactive typing; it must not
                            // truncate a value we supply here (e.g. a 10-char date in an 8-char
                            // field). Clear it before writing so the value renders in full. The box
                            // is already sized to the available space at build time (RenderFields).
                            tb.MaxLength = 0;
                            tb.Text = v;
                        }

                        // When flattening to a final document, strip the editable-field border so
                        // it isn't baked into the page as a visible box. The light-blue outline is
                        // only a UI hint for the interactive (non-flattened) fill flow.
                        if (flatten)
                            tb.BorderWidth = 0;
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
    /// Enumerate the fillable AcroForm fields of an already-built PDF template, without any
    /// FAP parsing. Used by the request-time /api/templates/{form}/{ed}/fields endpoint to
    /// drive a data-entry UI. Field names match the keys <see cref="FillFields"/> expects.
    /// </summary>
    public IReadOnlyList<TemplateField> ReadTemplateFields(byte[] pdfBytes)
    {
        using var ms = new MemoryStream(pdfBytes);
        var doc = new PdfDocument(ms);
        try
        {
            var result = new List<TemplateField>();
            if (doc.Form is PdfFormWidget form)
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < form.FieldsWidget.Count; i++)
                {
                    var field = form.FieldsWidget[i];
                    var name = field.Name;
                    if (string.IsNullOrEmpty(name) || !seen.Add(name)) continue;

                    if (field is PdfTextBoxFieldWidget tb)
                        result.Add(new TemplateField(name, "text", tb.MaxLength));
                    else
                        result.Add(new TemplateField(name, "other", 0));
                }
            }
            return result;
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

    /// <summary>Return the FXR font record (with exact per-character advances) for a font ID.</summary>
    public FxrFont? GetFxr(int fontId) => _fxr.Resolve(fontId);

    public PdfFontBase Get(int fontId, int rowHeightFap)
    {
        if (_cache.TryGetValue(fontId, out var cached)) return cached;
        var font = Build(fontId, rowHeightFap);
        _cache[fontId] = font;
        return font;
    }

    /// <summary>
    /// Build a font for a field at an explicit point size (per-field override), keeping the FXR
    /// typeface and italic, with an optional bold override. Not cached — override fields are few.
    /// </summary>
    public PdfFontBase GetAtSize(int fontId, float pointSize, bool? boldOverride)
    {
        var fx = _fxr.Resolve(fontId);
        string typeface = fx?.Typeface ?? "Arial";
        bool bold = boldOverride ?? fx?.Bold ?? false;
        bool italic = fx?.Italic ?? false;

        var style = System.Drawing.FontStyle.Regular;
        if (bold) style |= System.Drawing.FontStyle.Bold;
        if (italic) style |= System.Drawing.FontStyle.Italic;
        try
        {
            var sysFont = new System.Drawing.Font(MapFamily(typeface), pointSize, style);
            return new PdfTrueTypeFont(sysFont, true);
        }
        catch
        {
            return new PdfFont(MapBase14(typeface), pointSize, bold ? PdfFontStyle.Bold : PdfFontStyle.Regular);
        }
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
