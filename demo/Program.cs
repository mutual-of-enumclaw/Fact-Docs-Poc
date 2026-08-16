using System.Globalization;
using FapPdfTools.Population;
using FapPdfTools.Population.Maps;
using FapPdfTools.Server.Configuration;
using FapPdfTools.Server.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MoE.CommonDataModel;

// Spire.PDF license (same key the server uses) so output has no evaluation watermark.
Spire.Pdf.License.LicenseProvider.SetLicenseKey("vz+UTK22G7SNfgEAxsAEfRc5e5LhTNX0Na451lUUQycsqDXEltzv7inyKYc6jipGqC7pNMi+pZC5AN2B2fzZWKndQZCntRVGZ3INztr/4K8NFL+SwPuKZYvOWhKWwAFpezAZ7h+akP7zD6f8v5IXe11ROfeBEaDmUmIuaS7u3+paLmUrJDNkAF4J9sZ37Hivv2weB3SZJRCvyjce5O3bRUbrIsSxZ6LWIRiroCGjPuJcdwkEPW8U/ljOmdEvg+B+h/CCyhcOs8YdCUHsHGlcovDMONLQ0iMWeCwN5WZieSHqb5UpDaXWrlLVoF3ZMnAxk1mFHqAFwFBs7/9sswaGIehVPfMqT6FHZyBI+y+RvQjjhZ4T52/CuPgXUzCbq54s9ISwb40Jf5o5UMhvTN86zMl0CMtQYSM/AzKtpW3YR7xsVO3tUUTINrxGlCJ71tqhR7osJZtFLpqKsUv6SJ8VBW37pobQu5OYVrmoJFMNcSBIwqfRavP8AtM4xgvK6Pp8O7GDrvha1GMb317ZiwcHgWtxCs3gfwCB71cTYf7r9cnYIeq/H7VjXF/ai5BQ0Ok8NtGUMQtxiGcHlCuNShu710wzJR7E4jI5wBaBUvg5h8Plm4sEOgiqmiIRW37b5NEYnwWVbsVzxAnilHI0BgpeuhWOv42zO/H+pRMEnsN3ISaFDyckpM4YwcIu3/eDnqSYIaNZIFLQVb0yFa6JYJ5otrAZKbNiNK7rZj7MxkRIOl52KND4CGzOLtR0cBMD4tAL8/uU1LscNgoe3NTQP7MOf0w0otrqzccwrGlz8Rl9P6jlx+WKX9przXk/5B8sFjEefW8Oi+jM2w1RmwDy/nDVwh+9NmXgW4jjiBE0lcM/lidfo35hFB9VYHfZjDhqKtVHdkwm7feKCGB8qWdJkzvKKE1eKhmwG9ZCi45PKdegUpZJtsMTPDMTaQKlWwwEM3tUupdvnBHQYIwXzeqFo6DlSwNSmvlR5i2//LA15MvtppCSKj8Bj4XPb1i6Mkp7yfkHERaUsPbljWEz2WAKXZTtyVQxWC7oTMLnQ04pVAMHekpckRtl/n3rwefKQww9lQFHxCSJ3dk/fLI1SuqZmhmi4IT1nbMBxXiekTbVKfcTZDDFZtbI6WMdFjlP5OUqF2v3kd61um8ulPL7h0VAEC6l1CMhi1jqdne671Oyziuek38KIMOutOc71KUnVet9w64hO2u4Xwa2tvvaXarcyX5elK5HJA3DVGHlZyrMyFLvarvN4QWlhUhUmiWB7eGlqifZ/0fSWMIocrsl3gEslwYpSx1mLuFtmybnKeDn44pAD3yX31+IpEDrMrNAbgYMh44mzPcbICZyfq2k8sYZYFd0s1S9A0xUE2xO+nb4tEul1oku+gWorUKnsvlSB2pT+JKdddfknzCsiUtjxCMy8k1Giukd+Ols33za0GKOGWnpPlF5qjpLW8BPLvbobE913THp7lY+g8PKFYVf1xNr7jKQELMtS3GAgbLNn5jSDeTcp5/Qfvz4zU8s/vtzvJo15M65EcVd09vVKtmV7j+ktka1BMyEln1cy4NB1N9t/2UpAVUsn+EgE4Ccb+gFwiCH1ifEjxj76nr1vaR4xMBe/Js/+8yqvfDPoUNuSS63aLlWrSgfQISFBiEZOg19MJtleL62LyeurYVEH0jkvi0TqLyZ2YW6k333rSbuGFQiGWDIqyN9baJfRnWazzCcoyFQ8gHXJHZU3fI3tW84eDVY5CnJ1DoUIqwJ1pIpIa8kTuxMR7M=");

// Usage (a trailing form key BOPDEC / CA2146 / CA2151 may be added to any of these):
//   render-preview                  -> render MCS90A PDF pages to PNG for inspection
//   (no args)                       -> populate the default form from a built-in sample policy
//   <policyNumber>                  -> fetch from the default environment (dev), then populate

if (args.Length >= 1 && args[0] == "render-preview")
{
    // Optional 2nd arg: path to a PDF. Defaults to the MCS90A preview.
    var pdfPath = args.Length >= 2 ? args[1] : @"C:\src\fact-pdf-tools\output\MCS90A_preview.pdf";
    var stem = Path.Combine(Path.GetDirectoryName(pdfPath)!, Path.GetFileNameWithoutExtension(pdfPath));
    var doc = new Spire.Pdf.PdfDocument();
    doc.LoadFromFile(pdfPath);
    for (int i = 0; i < doc.Pages.Count; i++)
    {
        using var img = doc.SaveAsImage(i, 120, 120);
        var previewOutPath = $"{stem}_p{i + 1}.png";
        img.Save(previewOutPath);
        Console.WriteLine($"Saved {previewOutPath} ({img.Width}x{img.Height})");
    }
    return;
}

// emit-html <FORM> [outPath]  -> Layer-A (absolute) MoE Form HTML for a single FAP.
// Deterministic: no timestamps, all collections sorted, fonts embedded from the
// Documaker TTFs so Chromium shapes text with the same faces GENDAW32/FAP2PDF use.
if (args.Length >= 2 && args[0] == "emit-html")
{
    const string MstrRes = @"C:\src\FaCT-DocProd-Development\mstrres";
    var formName = args[1];
    var outHtml = args.Length >= 3
        ? args[2]
        : Path.Combine(@"C:\src\fact-pdf-tools\output", formName + ".html");
    var fontDir = Path.Combine(MstrRes, "Fmres", "deflib");

    var htmlOptions = Options.Create(new FormFileOptions
    {
        FormDatPath = Path.Combine(MstrRes, @"MOEC0\DEFLIB\FORM.DAT"),
        FormsDirectory = Path.Combine(MstrRes, @"MOEC0\FORMS"),
        DdtDirectory = Path.Combine(MstrRes, @"MOEC0\DDTLIB"),
        FxrPath = Path.Combine(MstrRes, @"MOEC0\DEFLIB\REL103.FXR"),
    });
    var htmlClient = new FormFileClient(htmlOptions, NullLogger<FormFileClient>.Instance);
    var htmlFonts = new FxrFontLibrary(htmlOptions, NullLogger<FxrFontLibrary>.Instance);

    var parsed = await htmlClient.ParseFapFileAsync(formName);
    if (parsed == null)
    {
        Console.Error.WriteLine($"Could not parse FAP '{formName}'.");
        return;
    }

    const float S = 72f / 2400f; // FAP units (1/2400") -> PDF points

    // --- Resolve the TTF backing an FXR typeface -------------------------------
    // NOTE: the legacy PDF channel (FAP2PDF / the LPDF driver) substitutes the
    // base-14 Helvetica for the sans faces rather than embedding the Documaker
    // TTFs, so PDF-channel parity wants the Helvetica-metric face (Arial) for
    // Univers ATT too. A print/AFP channel would use the real face. Which legacy
    // channel we are matching is therefore an explicit parity setting.
    static string TtfFor(string typeface, bool bold, bool italic)
    {
        var t = typeface.ToUpperInvariant();
        string[] set =
            t.Contains("COURIER") ? ["COURIE.TTF", "COURIEB.TTF", "COURIEI.TTF", "COURIEBI.TTF"]
            : t.Contains("TIMES") ? ["TIMES.TTF", "TIMESB.TTF", "TIMESI.TTF", "TIMESBI.TTF"]
            : ["arial.ttf", "arialbd.ttf", "ariali.ttf", "arialbi.ttf"];
        return set[(bold ? 1 : 0) + (italic ? 2 : 0)];
    }

    static string CssFamily(string typeface) =>
        "F_" + new string(typeface.Where(char.IsLetterOrDigit).ToArray());

    static string Esc(string s) => s
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    static string N(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    // --- Flatten every drawable into one ordered list --------------------------
    // (text tokens from both S,TT static texts and M,TT text areas)
    var texts = parsed.StaticTexts
        .Select(t => (t.Text, t.PageIndex, t.Position, t.FontAttributes.FontId, Bold: false))
        .Concat(parsed.TextAreas.SelectMany(a => a.Tokens
            .Where(tok => !tok.IsFieldPlaceholder)
            .Select(tok => (tok.Text, a.PageIndex, tok.Position, FontId: tok.FontId, Bold: tok.IsBold))))
        .Where(t => !string.IsNullOrWhiteSpace(t.Text))
        .OrderBy(t => t.PageIndex).ThenBy(t => t.Position.Row1).ThenBy(t => t.Position.Col1)
        .ThenBy(t => t.Text, StringComparer.Ordinal)
        .ToList();

    // --- Embed only the faces this form actually uses ---------------------------
    var usedFaces = texts
        .Select(t => htmlFonts.Resolve(t.FontId))
        .Where(f => f != null)
        .Select(f => (f!.Typeface, Bold: f.Bold, f.Italic))
        .Distinct()
        .OrderBy(f => f.Typeface, StringComparer.Ordinal).ThenBy(f => f.Bold).ThenBy(f => f.Italic)
        .ToList();

    var css = new System.Text.StringBuilder();
    foreach (var face in usedFaces)
    {
        var ttf = Path.Combine(fontDir, TtfFor(face.Typeface, face.Bold, face.Italic));
        if (!File.Exists(ttf)) { Console.Error.WriteLine($"  ! missing font {ttf}"); continue; }
        var b64 = Convert.ToBase64String(File.ReadAllBytes(ttf));
        css.Append($"@font-face{{font-family:'{CssFamily(face.Typeface)}';")
           .Append($"font-weight:{(face.Bold ? "bold" : "normal")};")
           .Append($"font-style:{(face.Italic ? "italic" : "normal")};")
           .Append($"src:url(data:font/ttf;base64,{b64}) format('truetype');}}\n");
    }

    var pi0 = parsed.PageInfos.Count > 0 ? parsed.PageInfos[0] : new FapPageInfo(2400, 0, 0, 20400, 26400);
    float pageW = pi0.PageWidth * S, pageH = pi0.PageHeight * S;

    var sb = new System.Text.StringBuilder();
    sb.Append("<!doctype html>\n<html><head><meta charset=\"utf-8\">\n<style>\n")
      .Append(css)
      .Append($"@page{{size:{N(pageW)}pt {N(pageH)}pt;margin:0}}\n")
      .Append("html,body{margin:0;padding:0;background:#fff;-webkit-print-color-adjust:exact}\n")
      .Append($".form-page{{position:relative;width:{N(pageW)}pt;height:{N(pageH)}pt;overflow:hidden;page-break-after:always}}\n")
      // Baseline calibration: Chromium's half-leading places the baseline 1.44pt
      // higher than Documaker for these faces. Measured constant, pending exact
      // ascent/descent parsing from the TTF (hhea/OS-2) — see FORM-STUDIO-PLAN §5.
      .Append(".abs{position:absolute;white-space:pre;margin:0;padding:0;transform:translateY(1.44pt)}\n")
      .Append(".rule{position:absolute;background:#000}\n")
      .Append(".box{position:absolute;border:solid #000}\n")
      .Append("</style></head><body>\n");

    for (int p = 0; p < Math.Max(1, parsed.PageCount); p++)
    {
        var pi = p < parsed.PageInfos.Count ? parsed.PageInfos[p] : pi0;
        float Px(int col) => (col - pi.OriginCol) * S;
        float Py(int row) => (row - pi.OriginRow) * S;

        sb.Append($"<section class=\"form-page\" data-page=\"{p + 1}\">\n");

        foreach (var t in texts.Where(t => t.PageIndex == p))
        {
            var f = htmlFonts.Resolve(t.FontId);
            float size = f?.PointSize > 0 ? f.PointSize : 10f;
            // Documaker's own line box, so Chromium's half-leading places the
            // baseline the same way GENDAW32 does.
            float lh = f != null && f.LineHeight > 0 ? f.LineHeight * S : size * 1.2f;
            var fam = CssFamily(f?.Typeface ?? "Arial");
            bool bold = (f?.Bold ?? false) || t.Bold;

            sb.Append($"<span class=\"abs\" style=\"left:{N(Px(t.Position.Col1))}pt;top:{N(Py(t.Position.Row1))}pt;")
              .Append($"font-family:'{fam}';font-size:{N(size)}pt;line-height:{N(lh)}pt")
              .Append(bold ? ";font-weight:bold" : "")
              .Append((f?.Italic ?? false) ? ";font-style:italic" : "")
              .Append($"\">{Esc(t.Text)}</span>\n");
        }

        foreach (var l in parsed.Lines.Where(l => l.PageIndex == p)
                     .OrderBy(l => l.Position.Row1).ThenBy(l => l.Position.Col1))
        {
            float x1 = Px(l.Position.Col1), y1 = Py(l.Position.Row1);
            float x2 = Px(l.Position.Col2), y2 = Py(l.Position.Row2);
            float thick = Math.Max(0.5f, l.Width * S);

            if (Math.Abs(y2 - y1) < 0.01f)      // horizontal rule
                sb.Append($"<div class=\"rule\" style=\"left:{N(x1)}pt;top:{N(y1)}pt;width:{N(x2 - x1)}pt;height:{N(thick)}pt\"></div>\n");
            else if (Math.Abs(x2 - x1) < 0.01f) // vertical rule
                sb.Append($"<div class=\"rule\" style=\"left:{N(x1)}pt;top:{N(y1)}pt;width:{N(thick)}pt;height:{N(y2 - y1)}pt\"></div>\n");
            else                                 // rectangle
                sb.Append($"<div class=\"box\" style=\"left:{N(x1)}pt;top:{N(y1)}pt;width:{N(x2 - x1)}pt;height:{N(y2 - y1)}pt;border-width:{N(thick)}pt\"></div>\n");
        }

        // Fields carry binding metadata but draw nothing when unfilled — matching
        // an unfilled Documaker render.
        foreach (var fld in parsed.Fields.Where(f => f.PageIndex == p)
                     .OrderBy(f => f.Position.Row1).ThenBy(f => f.Position.Col1).ThenBy(f => f.Name, StringComparer.Ordinal))
        {
            sb.Append($"<span class=\"abs field\" data-field=\"{Esc(fld.Name)}\" data-maxlen=\"{fld.Length}\" ")
              .Append($"style=\"left:{N(Px(fld.Position.Col1))}pt;top:{N(Py(fld.Position.Row1))}pt;")
              .Append($"width:{N(Px(fld.Position.Col2) - Px(fld.Position.Col1))}pt;")
              .Append($"height:{N(Py(fld.Position.Row2) - Py(fld.Position.Row1))}pt\"></span>\n");
        }

        sb.Append("</section>\n");
    }
    sb.Append("</body></html>\n");

    Directory.CreateDirectory(Path.GetDirectoryName(outHtml)!);
    File.WriteAllText(outHtml, sb.ToString());

    Console.WriteLine($"{formName}: {parsed.PageCount} page(s), {texts.Count} text runs, "
        + $"{parsed.Lines.Count} rules/boxes, {parsed.Fields.Count} fields, {usedFaces.Count} faces embedded");
    foreach (var face in usedFaces)
        Console.WriteLine($"  font: {face.Typeface}{(face.Bold ? " Bold" : "")}{(face.Italic ? " Italic" : "")} -> {TtfFor(face.Typeface, face.Bold, face.Italic)}");
    Console.WriteLine($"Wrote {outHtml} ({new FileInfo(outHtml).Length:N0} bytes)");
    return;
}

// coverage [formsDir]  -> convert every FAP form to .gd and report which convert
// clean vs. which hit unsupported constructs. Writes output\coverage-report.{md,csv}.
if (args.Length >= 1 && args[0] == "coverage")
{
    var formsDir = args.Length >= 2 ? args[1]
        : @"C:\src\FaCT-DocProd-Development\mstrres\MOEC0\FORMS";
    var covOptions = Options.Create(new FormFileOptions
    {
        FormDatPath = @"C:\src\FaCT-DocProd-Development\mstrres\MOEC0\DEFLIB\FORM.DAT",
        FormsDirectory = formsDir,
        DdtDirectory = @"C:\src\FaCT-DocProd-Development\mstrres\MOEC0\DDTLIB",
        FxrPath = @"C:\EDrive\moec0\Mstrres\MOEC0\DEFLIB\REL103.FXR",
    });
    var client = new FormFileClient(covOptions, NullLogger<FormFileClient>.Instance);
    var fonts = new FxrFontLibrary(covOptions, NullLogger<FxrFontLibrary>.Instance);

    var files = Directory.EnumerateFiles(formsDir, "*.FAP").OrderBy(f => f).ToList();
    Console.WriteLine($"Scanning {files.Count} FAP forms in {formsDir} ...");

    var rows = new List<string> { "form,pages,fields,gridRects,images,textAreas,classification,generated,gdBytes,error" };
    int ok = 0, failed = 0, withGrid = 0, withImg = 0, withFields = 0, multiPage = 0;
    var errorSamples = new List<string>();

    foreach (var file in files)
    {
        var name = Path.GetFileNameWithoutExtension(file);
        var raw = File.ReadAllLines(file);
        int pages = raw.Count(l => l.StartsWith("H,", StringComparison.OrdinalIgnoreCase));
        int fLines = raw.Count(l => l.StartsWith("F,", StringComparison.OrdinalIgnoreCase));
        int images = raw.Count(l => l.StartsWith("G,", StringComparison.OrdinalIgnoreCase)
                                 || l.StartsWith("N,", StringComparison.OrdinalIgnoreCase));
        int textAreas = raw.Count(l => l.StartsWith("M,H,", StringComparison.OrdinalIgnoreCase));
        int gridRects = raw.Count(l =>
        {
            if (!l.StartsWith("X,", StringComparison.OrdinalIgnoreCase)) return false;
            var m = System.Text.RegularExpressions.Regex.Match(l, @"^X,\((\d+),(\d+),(\d+),(\d+)\)");
            if (!m.Success) return false;
            int r1 = int.Parse(m.Groups[1].Value), c1 = int.Parse(m.Groups[2].Value),
                r2 = int.Parse(m.Groups[3].Value), c2 = int.Parse(m.Groups[4].Value);
            return Math.Abs(r2 - r1) > 40 && Math.Abs(c2 - c1) > 40; // a rectangle, not a line
        });

        string classification = "?", generated = "no", err = "";
        int gdBytes = 0;
        try
        {
            classification = await client.ClassifyFormAsync(name);
            var fap = await client.ParseFapFileAsync(name);
            if (fap == null) { err = "parse returned null"; }
            else
            {
                var gd = FapToGhostDraftGenerator.Generate(fap, name, name, isWip: false, backgroundEmf: null, fonts: fonts);
                gdBytes = System.Text.Encoding.UTF8.GetByteCount(gd);
                generated = "yes"; ok++;
            }
        }
        catch (Exception ex) { err = ex.GetType().Name + ": " + ex.Message.Replace(',', ';').Replace('\n', ' '); }

        if (generated != "yes") { failed++; if (errorSamples.Count < 25) errorSamples.Add($"{name}: {err}"); }
        if (gridRects > 0) withGrid++;
        if (images > 0) withImg++;
        if (fLines > 0) withFields++;
        if (pages > 1) multiPage++;

        rows.Add($"{name},{pages},{fLines},{gridRects},{images},{textAreas},{classification},{generated},{gdBytes},{err}");
    }

    var covOutDir = @"C:\src\fact-pdf-tools\output";
    Directory.CreateDirectory(covOutDir);
    File.WriteAllLines(Path.Combine(covOutDir, "coverage-report.csv"), rows);

    var md = new System.Text.StringBuilder();
    md.AppendLine("# FAP -> GhostDraft conversion coverage");
    md.AppendLine();
    md.AppendLine($"Source: `{formsDir}`  ");
    md.AppendLine($"Total forms scanned: **{files.Count}**  ");
    md.AppendLine($"Generated a .gd without error: **{ok}** ({100.0 * ok / Math.Max(1, files.Count):F1}%)  ");
    md.AppendLine($"Failed: **{failed}**  ");
    md.AppendLine();
    md.AppendLine("## Construct inventory");
    md.AppendLine($"- Forms with fill fields (F,): {withFields}");
    md.AppendLine($"- Forms with a bordered grid (X, rectangles): {withGrid}");
    md.AppendLine($"- Forms with images (G,/N,): {withImg}");
    md.AppendLine($"- Multi-page forms (>1 H,): {multiPage}");
    md.AppendLine();
    if (errorSamples.Count > 0)
    {
        md.AppendLine("## Failure samples");
        foreach (var e in errorSamples) md.AppendLine($"- `{e}`");
        md.AppendLine();
    }
    md.AppendLine("Per-form detail: `coverage-report.csv`.");
    File.WriteAllText(Path.Combine(covOutDir, "coverage-report.md"), md.ToString());

    Console.WriteLine($"Done. {ok}/{files.Count} generated OK, {failed} failed.");
    Console.WriteLine($"grids={withGrid} images={withImg} fields={withFields} multipage={multiPage}");
    Console.WriteLine($"Wrote {covOutDir}\\coverage-report.md and .csv");
    return;
}

// gap <FORM>  -> for one form, report each fill field's binding status against the GhostDraft
// Model Library: BOUND (a concept exists) / no concept (needs adding). The CDM side of the gap
// (is the data in MoE.CommonDataModel?) is covered by the commercial MCP form_cdm_gap_analysis.
if (args.Length >= 2 && args[0] == "gap")
{
    var formName = args[1];
    var gapOptions = Options.Create(new FormFileOptions
    {
        FormsDirectory = @"C:\src\FaCT-DocProd-Development\mstrres\MOEC0\FORMS",
        DdtDirectory = @"C:\src\FaCT-DocProd-Development\mstrres\MOEC0\DDTLIB",
        FormDatPath = @"C:\src\FaCT-DocProd-Development\mstrres\MOEC0\DEFLIB\FORM.DAT",
    });
    var gapClient = new FormFileClient(gapOptions, NullLogger<FormFileClient>.Instance);
    var lib = ModelLibrary.Load(@"C:\Users\cmorehouse\Downloads\Moe Proprietary\Resources\Model Libraries\Model Library.gdm");
    Console.WriteLine($"Model Library: {lib.Attributes.Count} bindable attributes");

    var parsed = await gapClient.ParseFapFileAsync(formName);
    if (parsed == null) { Console.WriteLine($"Form '{formName}' not found."); return; }
    var ddt = await gapClient.ParseDdtFileAsync(formName);
    var ruleByField = new Dictionary<string, DdtFieldRule>(StringComparer.OrdinalIgnoreCase);
    if (ddt != null) foreach (var r in ddt.FieldRules) ruleByField[r.FieldName] = r;

    // Classify a field's DATA PROVENANCE from its DDT rule: where the value comes from.
    static (string kind, string source) Provenance(DdtFieldRule? r)
    {
        if (r == null) return ("unknown", "(no DDT rule)");
        var m = r.Method?.ToLowerInvariant() ?? "";
        if (m == "powtype") return ("manual", "manual entry (WIP)");
        if (m == "mk_hard") return ("constant", "hard-coded: " + r.Source);
        if (r.DalFunctions.Count > 0) return ("system", "DAL: " + string.Join(",", r.DalFunctions));
        if (r.TablesReferenced.Count > 0) return ("system", "table: " + string.Join(",", r.TablesReferenced));
        return ("system", r.Method); // other computed methods (concat/movedate/etc.)
    }

    // Load the CDM HYDRATION source (fact-commercial-api GetPolicy queries + mapping). If a DB2
    // column a field needs appears here, its data is hydrated INTO the CDM => on our model.
    string hydrationText = "";
    var hydrationDirs = new[]
    {
        @"C:\src\fact-commercial-api\MoE.Commercial.Api\Components\Data\GetPolicy",
        @"C:\src\fact-commercial-api\MoE.Commercial.Data.Provider\Mapping",
    };
    foreach (var d in hydrationDirs.Where(Directory.Exists))
        foreach (var cs in Directory.EnumerateFiles(d, "*.cs"))
            hydrationText += File.ReadAllText(cs) + "\n";
    bool haveHydration = hydrationText.Length > 0;

    // Pull candidate DB2 column tokens out of a DDT rule's Source (e.g.
    // "TBLOFF,PMSP0200 SYMBOL,3,POLICY0NUM,7,MODULE,2" -> SYMBOL, POLICY0NUM, MODULE).
    var colRx = new System.Text.RegularExpressions.Regex(@"\b[A-Z][A-Z0-9]{3,}\b");
    List<string> ExtractColumns(DdtFieldRule r)
    {
        var skip = new HashSet<string>(r.TablesReferenced, StringComparer.OrdinalIgnoreCase) { "TBLOFF" };
        return colRx.Matches(r.Source ?? "")
            .Select(m => m.Value)
            .Where(t => !skip.Contains(t) && !t.All(char.IsDigit))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    var names = parsed.Fields.Select(f => f.Name).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n).ToList();
    Console.WriteLine($"\n{formName}: {names.Count} distinct fill fields  ({(ddt != null ? ddt.FieldRules.Count + " DDT rules" : "NO DDT")})");
    Console.WriteLine($"CDM hydration source: {(haveHydration ? $"{hydrationText.Length:N0} chars loaded" : "NOT FOUND (fact-commercial-api hydration unavailable)")}\n");
    Console.WriteLine($"{"FIELD",-20} {"PROVENANCE",-11} {"GD?",-4} {"ON CDM?",-14} DATA SOURCE (DDT)");
    int system = 0, manual = 0, constant = 0, bound = 0, onModel = 0, needsDal = 0;
    foreach (var n in names)
    {
        var norm = n.Contains(" #") ? n[..n.IndexOf(" #")].Trim() : n.Trim();
        var rule = ruleByField.GetValueOrDefault(n) ?? ruleByField.GetValueOrDefault(norm);
        var (kind, source) = Provenance(rule);
        if (kind == "system") system++; else if (kind == "manual") manual++; else if (kind == "constant") constant++;
        string gd = FapToGhostDraftGenerator.DescribeBinding(formName, n) != null ? "yes" : "-";
        if (gd == "yes") bound++;

        // On-CDM check (system fields only): are the field's DB2 columns hydrated into the CDM?
        string cdm = "-";
        if (kind == "system" && rule != null)
        {
            if (rule.DalFunctions.Count > 0) { cdm = "via DAL (resolve)"; needsDal++; }
            else if (!haveHydration) { cdm = "?";}
            else
            {
                var cols = ExtractColumns(rule);
                var found = cols.Where(c => hydrationText.Contains(c, StringComparison.OrdinalIgnoreCase)).ToList();
                if (cols.Count == 0) cdm = "? (no columns)";
                else if (found.Count == cols.Count) { cdm = "yes"; onModel++; }
                else if (found.Count > 0) { cdm = $"partial {found.Count}/{cols.Count}"; onModel++; }
                else cdm = "NOT hydrated";
            }
        }
        Console.WriteLine($"{n,-20} {kind,-11} {gd,-4} {cdm,-14} {source}");
    }
    Console.WriteLine($"\nProvenance: {system} system-sourced, {manual} manual, {constant} constant.");
    Console.WriteLine($"GhostDraft binding: {bound}/{names.Count} fields bound by our converter; Model Library has {lib.Attributes.Count} concepts.");
    if (haveHydration)
        Console.WriteLine($"On our model (CDM): {onModel} system field(s) have columns hydrated into the CDM; {needsDal} need DAL resolution (run commercial MCP form_resolve_dal, then re-check).");
    else
        Console.WriteLine("CDM hydration source not found — clone/checkout fact-commercial-api to enable the on-model check.");
    return;
}

// regress [capture]  -> golden-file regression for the .gd generator across a curated set of forms
// covering each layout construct. `capture` writes/updates the golden files; no arg checks current
// output against them and reports OK / CHANGED / NEW. The volatile Created timestamp is normalized out
// so diffs are meaningful. Self-contained (no GhostDraft/Commercial API needed).
if (args.Length >= 1 && args[0] == "regress")
{
    bool capture = args.Length >= 2 && args[1] == "capture";
    // Curated coverage: 2-col right-align, prose+fields, bordered grid, N-column, multi-col defs, multi-page.
    string[] regressForms = { "QTE_EA9910E", "QTE_COVER_A", "QTE_BILLINFO", "QFRM_FGL", "QFRM_FPL", "QTE_COVAUTOSYM", "QTE_FORM", "MCS90A" };
    var regOptions = Options.Create(new FormFileOptions
    {
        FormsDirectory = @"C:\src\FaCT-DocProd-Development\mstrres\MOEC0\FORMS",
        DdtDirectory = @"C:\src\FaCT-DocProd-Development\mstrres\MOEC0\DDTLIB",
        FormDatPath = @"C:\src\FaCT-DocProd-Development\mstrres\MOEC0\DEFLIB\FORM.DAT",
        FxrPath = @"C:\EDrive\moec0\Mstrres\MOEC0\DEFLIB\REL103.FXR",
    });
    var regClient = new FormFileClient(regOptions, NullLogger<FormFileClient>.Instance);
    var regFonts = new FxrFontLibrary(regOptions, NullLogger<FxrFontLibrary>.Instance);
    var goldenDir = @"C:\src\fact-pdf-tools\demo\regression\golden";
    Directory.CreateDirectory(goldenDir);

    // Normalize away the only volatile field (the Created timestamp) so diffs are real.
    static string Normalize(string gd) =>
        System.Text.RegularExpressions.Regex.Replace(gd,
            "(name=\"Created\" type=\"string\"><value>)[^<]*(</value>)", "$1$2");

    int ok = 0, changed = 0, added = 0, missing = 0;
    foreach (var f in regressForms)
    {
        var fap = await regClient.ParseFapFileAsync(f);
        if (fap == null) { Console.WriteLine($"  {f,-16} MISSING FORM"); missing++; continue; }
        var gd = Normalize(FapToGhostDraftGenerator.Generate(fap, f, f, isWip: false, backgroundEmf: null, fonts: regFonts));
        var goldenPath = Path.Combine(goldenDir, f + ".gd");
        if (capture) { File.WriteAllText(goldenPath, gd); Console.WriteLine($"  {f,-16} captured ({gd.Length:N0} chars)"); ok++; continue; }
        if (!File.Exists(goldenPath)) { Console.WriteLine($"  {f,-16} NEW (no golden — run `regress capture`)"); added++; continue; }
        var golden = File.ReadAllText(goldenPath);
        if (golden == gd) { Console.WriteLine($"  {f,-16} OK"); ok++; }
        else
        {
            changed++;
            int at = 0; while (at < golden.Length && at < gd.Length && golden[at] == gd[at]) at++;
            Console.WriteLine($"  {f,-16} CHANGED (golden {golden.Length:N0} vs now {gd.Length:N0} chars; first diff @ {at})");
        }
    }
    Console.WriteLine();
    Console.WriteLine(capture
        ? $"Captured {ok} golden files in {goldenDir}"
        : $"{ok} OK, {changed} CHANGED, {added} NEW, {missing} missing. {(changed == 0 && missing == 0 ? "No regressions." : "Review changes above.")}");
    if (!capture && changed > 0) Environment.ExitCode = 1;
    return;
}

// convert-quotes [PREFIX]  -> batch-convert every quote form (default: Q*, i.e. QTE_/QFRM_/QCPP_/QBOP_/…)
// to a .gd under output\quote-forms-gd\, and write CONVERSION-REPORT.md with per-form binding/structure stats.
if (args.Length >= 1 && args[0] == "convert-quotes")
{
    var prefix = args.Length >= 2 ? args[1] : "Q";
    var cqFormsDir = @"C:\src\FaCT-DocProd-Development\mstrres\MOEC0\FORMS";
    var cqOptions = Options.Create(new FormFileOptions
    {
        FormsDirectory = cqFormsDir,
        DdtDirectory = @"C:\src\FaCT-DocProd-Development\mstrres\MOEC0\DDTLIB",
        FormDatPath = @"C:\src\FaCT-DocProd-Development\mstrres\MOEC0\DEFLIB\FORM.DAT",
        FxrPath = @"C:\EDrive\moec0\Mstrres\MOEC0\DEFLIB\REL103.FXR",
    });
    var cqClient = new FormFileClient(cqOptions, NullLogger<FormFileClient>.Instance);
    var cqFonts = new FxrFontLibrary(cqOptions, NullLogger<FxrFontLibrary>.Instance);
    var cqOut = @"C:\src\fact-pdf-tools\output\quote-forms-gd";
    Directory.CreateDirectory(cqOut);

    static bool BraceBalanced(string gd)
    {
        int d = 0; for (int i = 0; i < gd.Length; i++)
        {
            char c = gd[i];
            if (c == '\\' && i + 1 < gd.Length && (gd[i + 1] == '{' || gd[i + 1] == '}' || gd[i + 1] == '\\')) { i++; continue; }
            if (c == '{') d++; else if (c == '}') { d--; if (d < 0) return false; }
        }
        return d == 0;
    }

    var cqFiles = Directory.EnumerateFiles(cqFormsDir, prefix + "*.FAP").OrderBy(f => f).ToList();
    Console.WriteLine($"Converting {cqFiles.Count} quote forms ('{prefix}*') ...");
    var report = new List<string> { "form,pages,fields,bound,unbound,tables,gdBytes,braceOK,error" };
    int okc = 0, failc = 0, totalFields = 0, totalBound = 0, withTables = 0, braceBad = 0;
    var fails = new List<string>();
    foreach (var file in cqFiles)
    {
        var name = Path.GetFileNameWithoutExtension(file);
        try
        {
            var fap = await cqClient.ParseFapFileAsync(name);
            if (fap == null) { failc++; fails.Add($"{name}: parse null"); report.Add($"{name},,,,,,,,parse null"); continue; }
            var gd = FapToGhostDraftGenerator.Generate(fap, name, name, isWip: false, backgroundEmf: null, fonts: cqFonts);
            File.WriteAllText(Path.Combine(cqOut, name + ".gd"), gd);
            var fields = fap.Fields.Select(f => f.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            int bnd = fields.Count(f => FapToGhostDraftGenerator.DescribeBinding(name, f) != null);
            int tbl = System.Text.RegularExpressions.Regex.Matches(gd, @"\\trowd").Count / 2;
            bool bal = BraceBalanced(gd);
            okc++; totalFields += fields.Count; totalBound += bnd; if (tbl > 0) withTables++; if (!bal) braceBad++;
            report.Add($"{name},{fap.PageCount},{fields.Count},{bnd},{fields.Count - bnd},{tbl},{gd.Length},{bal},");
        }
        catch (Exception ex) { failc++; var m = ex.GetType().Name + ": " + ex.Message.Replace(',', ';').Replace('\n', ' '); fails.Add($"{name}: {m}"); report.Add($"{name},,,,,,,,{m}"); }
    }
    File.WriteAllLines(Path.Combine(cqOut, "conversion-report.csv"), report);

    var md = new System.Text.StringBuilder();
    md.AppendLine($"# Quote form conversion ({prefix}*)\n");
    md.AppendLine($"Converted **{okc}/{cqFiles.Count}** quote forms to `.gd` in `output/quote-forms-gd/`. Failures: {failc}.\n");
    md.AppendLine($"- Total fill fields across forms: {totalFields}; already bound to a GhostDraft concept: **{totalBound}** ({(totalFields > 0 ? 100.0 * totalBound / totalFields : 0):F0}%).");
    md.AppendLine($"- Forms containing an RTF table (columns/grid): {withTables}.");
    md.AppendLine($"- Brace-balanced (structurally valid RTF): {okc - braceBad}/{okc}" + (braceBad > 0 ? $"  ⚠ {braceBad} unbalanced" : "") + ".\n");
    if (fails.Count > 0) { md.AppendLine("## Failures"); foreach (var f in fails.Take(30)) md.AppendLine($"- `{f}`"); md.AppendLine(); }
    md.AppendLine("Per-form detail: `conversion-report.csv`. (Layout fidelity for the packet chrome — images/logos — is a separate item; see ROADMAP.)");
    File.WriteAllText(Path.Combine(cqOut, "CONVERSION-REPORT.md"), md.ToString());

    Console.WriteLine($"\nConverted {okc}/{cqFiles.Count}. {totalBound}/{totalFields} fields bound. {withTables} with tables. brace-bad={braceBad}. Failures={failc}.");
    Console.WriteLine($"Wrote .gd files + CONVERSION-REPORT.md to {cqOut}");
    return;
}

// make-concept-library  -> emit an EXTENDED GhostDraft concept library (.gdm) = the real Model Library
// plus our project-owned "Quote" concept + domainModel (from ProjectConcepts, same GUIDs the converter
// binds to). Import/use this library in GhostDraft so the quote fields we bind actually resolve.
if (args.Length >= 1 && args[0] == "make-concept-library")
{
    var srcGdm = @"C:\Users\cmorehouse\Downloads\Moe Proprietary\Resources\Model Libraries\Model Library.gdm";
    if (!File.Exists(srcGdm)) { Console.WriteLine($"Base Model Library not found: {srcGdm}"); return; }
    var xml = File.ReadAllText(srcGdm);

    string Esc(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
    var attrsXml = new System.Text.StringBuilder();
    foreach (var (attr, type) in ProjectConcepts.Attributes)
    {
        attrsXml.AppendLine($"          <attribute name=\"{Esc(attr)}\" guid=\"{ProjectConcepts.AttrGuid(attr)}\" locked=\"false\" attributeState=\"RenderingBehaviour\" hiddenConceptName=\"\">");
        attrsXml.AppendLine($"            <attributeRefs>");
        attrsXml.AppendLine($"              <conceptName>{type}</conceptName>");
        attrsXml.AppendLine($"            </attributeRefs>");
        attrsXml.AppendLine($"          </attribute>");
    }
    string conceptXml =
        $"      <concept name=\"{ProjectConcepts.ConceptName}\" guid=\"{ProjectConcepts.ConceptGuid}\" locked=\"false\" isHidden=\"true\" isShared=\"false\">\n" +
        $"        <attributes>\n{attrsXml}        </attributes>\n" +
        $"      </concept>\n    ";
    string domainXml =
        $"      <domainModel name=\"{ProjectConcepts.Root}\" guid=\"{ProjectConcepts.RootGuid}\" locked=\"false\" onlyAccessableThroughSubscription=\"false\" hiddenConceptName=\"{ProjectConcepts.ConceptName}\">\n" +
        $"        <domainModelKinds>\n          <conceptName>{ProjectConcepts.ConceptName}</conceptName>\n        </domainModelKinds>\n" +
        $"      </domainModel>\n    ";

    if (xml.Contains(ProjectConcepts.ConceptName))
        Console.WriteLine("Note: base library already contains the Quote concept; re-emitting anyway.");
    // Insert before the closing tags of each section (first occurrence).
    xml = ReplaceFirst(xml, "</concepts>", conceptXml + "</concepts>");
    xml = ReplaceFirst(xml, "</domainModels>", domainXml + "</domainModels>");

    var libOutDir = @"C:\src\fact-pdf-tools\output\concept-library";
    Directory.CreateDirectory(libOutDir);
    var libOutPath = Path.Combine(libOutDir, "Model Library (with Quote).gdm");
    File.WriteAllText(libOutPath, xml);
    Console.WriteLine($"Wrote extended concept library -> {libOutPath}");
    Console.WriteLine($"Added domainModel '{ProjectConcepts.Root}' (guid {ProjectConcepts.RootGuid}) with {ProjectConcepts.Attributes.Length} attributes.");
    Console.WriteLine("Import this library in GhostDraft (or point the Model Library resource at it) so quote bindings resolve.");
    return;

    static string ReplaceFirst(string s, string find, string repl)
    { int i = s.IndexOf(find); return i < 0 ? s : s[..i] + repl + s[(i + find.Length)..]; }
}
//   <env|apiBaseUrl> <policyNumber> -> fetch from the named env (dev/dev3/tst/tst2/acc) or a full URL
const string DefaultEnvironment = "dev";
const string DefaultForm = "CA2146";

var argList = args.ToList();

// A trailing argument naming a known form selects which form to populate.
string formKey = DefaultForm;
if (argList.Count > 0 && IsKnownForm(argList[^1]))
{
	formKey = argList[^1];
	argList.RemoveAt(argList.Count - 1);
}

Policy policy;

if (argList.Count >= 1)
{
	var envOrUrl = argList.Count >= 2 ? argList[0] : DefaultEnvironment;
	var policyNumber = argList.Count >= 2 ? argList[1] : argList[0];
	var apiBaseUrl = ResolveApiBaseUrl(envOrUrl);

	Console.WriteLine($"Fetching policy '{policyNumber}' from {apiBaseUrl} ...");
	using var apiClient = new CommercialApiPolicyClient(apiBaseUrl);
	policy = await apiClient.GetPolicyAsync(policyNumber);
	Console.WriteLine($"Fetched policy '{policy.Number}'.");
}
else
{
	Console.WriteLine("No policy number supplied; using built-in sample policy.");
	policy = BuildSamplePolicy();
}

var options = new FormFileOptions
{
	FormDatPath = @"C:\EDrive\FORM.DAT",
	FormsDirectory = @"C:\EDrive\moec0\Mstrres\MOEC0\Forms",
	DdtDirectory = @"C:\EDrive\moec0\Mstrres\MOEC0\Ddtlib",
	FxrPath = @"C:\EDrive\moec0\Mstrres\MOEC0\DEFLIB\REL103.FXR"
};

var map = SelectMap(formKey);
var populator = new CdmFormPopulator(options);

Console.WriteLine($"=== Form {formKey}: resolved field values from Policy ===");
foreach (var kv in map.BuildValues(policy))
	Console.WriteLine($"  {kv.Key,-16} = {kv.Value}");

var bytes = await populator.PopulateAsync(map, policy, flatten: true);

var outDir = @"C:\src\fact-pdf-tools\output";
Directory.CreateDirectory(outDir);
var outPath = Path.Combine(outDir, $"{formKey}_populated.pdf");
File.WriteAllBytes(outPath, bytes);

Console.WriteLine();
Console.WriteLine($"Wrote {bytes.Length:N0} bytes -> {outPath}");

static bool IsKnownForm(string key)
	=> key.ToUpperInvariant() is "BOPDEC" or "CA2146" or "CA2151";

static IFormFieldMap SelectMap(string formKey) => formKey.ToUpperInvariant() switch
{
	"BOPDEC" => new BopDecPageFieldMap(),
	// MoE form CA 21 46 is the Split Underinsured Motorists Coverage Limits
	// endorsement (the ISO CA 21 51 counterpart MoE maintains).
	"CA2146" or "CA2151" => new Ca2146FieldMap(),
	_ => throw new ArgumentException($"Unknown form '{formKey}'.")
};

// Resolve a known FaCT environment name to its Commercial API URL, or pass a full URL through.
static string ResolveApiBaseUrl(string envOrUrl)
{
	if (envOrUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
		|| envOrUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
	{
		return envOrUrl;
	}

	return envOrUrl.ToLowerInvariant() switch
	{
		"dev" => "https://pointmoeapps-dev1.mutualofenumclaw.net/commercialapi",
		"dev3" => "https://pointmoeapps-dev3.mutualofenumclaw.net/commercialapi",
		"tst" => "https://pointmoeapps-tst1.mutualofenumclaw.net/commercialapi",
		"tst2" => "https://pointmoeapps-tst2.mutualofenumclaw.net/commercialapi",
		"acc" => "https://pointmoeapps-acc.mutualofenumclaw.net/commercialapi",
		_ => throw new ArgumentException(
			$"Unknown environment '{envOrUrl}'. Use dev, dev3, tst, tst2, acc, or a full URL.")
	};
}

// --- Built-in sample policy (stands in for a Commercial API GetPolicy result) ---
static Policy BuildSamplePolicy() => new()
{
	Number = "BOP-2026-000123",
	EffectiveDate = new DateTime(2026, 7, 1),
	ExpirationDate = new DateTime(2027, 7, 1),
	Premium = 4827.00m,
	State = "WA",
	Parties = new List<Party>
	{
		new InsuredParty
		{
			FullName = "Cascade Hardware & Supply LLC",
			BusinessEntity = "LLC",
			Address = new List<Address>
			{
				new Address
				{
					AddressLine = "1420 Industrial Way",
					ExtendedAddressLine = "Suite 200",
					City = "Enumclaw",
					State = "WA",
					ZipCode = "98022"
				}
			}
		},
		new AgencyParty
		{
			FullName = "Mountain View Insurance Agency",
			AgencyCode = "AG-4471",
			Phone = "(360) 825-1100",
			Address = new List<Address>
			{
				new Address
				{
					AddressLine = "55 Cole Street",
					City = "Enumclaw",
					State = "WA",
					ZipCode = "98022"
				}
			}
		}
	},
	Lines = new List<Line>
	{
		new CommercialAutoLine
		{
			Coverages = new List<Coverage>
			{
				new CommercialAutoUNCoverage
				{
					State = "WA",
					LimitType = "SPLIT",
					BodilyInjuryOnly = false,
					PerPersonLimit = 25000,
					PerAccidentLimit = 50000
				},
				new CommercialAutoUNPDCoverage
				{
					State = "WA",
					Limit = 10000
				}
			}
		}
	}
};
