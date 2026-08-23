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

    // NOTE: the per-FontId (font-calibration.json) and per-form (form-calibration.json)
    // baseline tables that used to be loaded here are GONE, and tools/calibrate.py with
    // them. They existed to absorb a residual we now know was the box height -- text is
    // anchored to the bottom of its declared box, see the baseline comment in the text
    // loop below. Those tables were measured against the old top-anchored model, so
    // applying them now would actively corrupt the geometry rather than refine it.

    // Which legacy channel we are matching is an explicit parity setting (section 10), so
    // the FXR advance-width correction is switchable: FS_FXR_ADVANCES=0 turns it off.
    // ON is correct for the PDF channel and it is not close -- measured 2026-08-22 at glyph
    // level, turning it off doubled the horizontal error (A0238C 25.9% -> 52.1% of glyphs
    // beyond 1pt, EB2410A 7.6% -> 16.8%). The tempting argument that it should be
    // unnecessary -- FAP2PDF substitutes base-14 Helvetica, and Arial is metric-compatible
    // with Helvetica, so our natural advances should already match -- is WRONG in practice.
    bool FxrAdvanceCorrection = Environment.GetEnvironmentVariable("FS_FXR_ADVANCES") != "0";

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
        // Documaker's dingbat face. It has no bold/italic variants, and it is the one
        // typeface where substituting Arial is plainly wrong rather than a deliberate
        // channel choice: the legacy PDF embeds DocuDings and we were rendering its
        // symbols as Latin letters. All three gates missed it -- Tier 1 compares character
        // CODES, Tier 2 compares positions, and non-text ink masks text out -- so nothing
        // we measure can see a wrong glyph SHAPE.
        if (t.Contains("DOCUDING")) return "DocuDing.TTF";
        string[] set =
            t.Contains("COURIER") ? ["COURIE.TTF", "COURIEB.TTF", "COURIEI.TTF", "COURIEBI.TTF"]
            : t.Contains("TIMES") ? ["TIMES.TTF", "TIMESB.TTF", "TIMESI.TTF", "TIMESBI.TTF"]
            : ["arial.ttf", "arialbd.ttf", "ariali.ttf", "arialbi.ttf"];
        return set[(bold ? 1 : 0) + (italic ? 2 : 0)];
    }

    // --- Documaker .LOG raster -> PNG ----------------------------------------
    // Format, worked out from the files themselves and verified by eye on all three
    // depths present in the library:
    //   header  " rows,cols,bytesPerRow,dpi,bpp,0,...,paletteSize"
    //   palette paletteSize lines of "r,g,b"   (only when paletteSize is non-zero)
    //   data    hex, a row split over several lines, continued lines ending in a
    //           backslash
    // bytesPerRow is the row STRIDE and is padded -- it runs a byte beyond
    // ceil(cols*bpp/8) on many files -- so it is used as given, never recomputed.
    // 24bpp is BGR in file order; 1bpp is INK-set, so a set bit is BLACK.
    // All 76 assets on disk decode.
    static (int W, int H, byte[] Rgb)? DecodeLog(string path)
    {
        try
        {
            var text = File.ReadAllText(path, System.Text.Encoding.ASCII);
            var lines = text.Split('\n');
            var hf = lines[0].Split(',');
            int rows = int.Parse(hf[0].Trim()), cols = int.Parse(hf[1].Trim());
            int bpr = int.Parse(hf[2].Trim()), bpp = int.Parse(hf[4].Trim());
            int npal = int.TryParse(hf[^1].Trim().Trim('"').Trim(), out var np) ? np : 0;
            if (cols <= 0 || rows <= 0 || bpr < (cols * bpp + 7) / 8) return null;

            int li = 1;
            var pal = new List<(byte R, byte G, byte B)>();
            for (int i = 0; i < npal && li < lines.Length; i++, li++)
            {
                var pp = lines[li].Split(',');
                if (pp.Length >= 3
                    && byte.TryParse(pp[0].Trim(), out var pr)
                    && byte.TryParse(pp[1].Trim(), out var pg2)
                    && byte.TryParse(pp[2].Trim(), out var pb)) pal.Add((pr, pg2, pb));
            }

            var hex = new System.Text.StringBuilder();
            for (; li < lines.Length; li++)
                hex.Append(lines[li].Trim('\r').TrimEnd('\\'));
            var hs = hex.ToString();
            int nbytes = hs.Length / 2;
            if (nbytes < rows * bpr) return null;
            var data = new byte[nbytes];
            for (int i = 0; i < nbytes; i++)
                data[i] = Convert.ToByte(hs.Substring(i * 2, 2), 16);

            var rgb = new byte[rows * cols * 3];
            for (int y = 0; y < rows; y++)
            {
                int src = y * bpr;
                for (int x = 0; x < cols; x++)
                {
                    int d = (y * cols + x) * 3;
                    if (bpp == 24)
                    {
                        rgb[d] = data[src + x * 3 + 2];
                        rgb[d + 1] = data[src + x * 3 + 1];
                        rgb[d + 2] = data[src + x * 3];
                    }
                    else if (bpp == 8)
                    {
                        byte v = data[src + x];
                        if (pal.Count > v) { rgb[d] = pal[v].R; rgb[d + 1] = pal[v].G; rgb[d + 2] = pal[v].B; }
                        else rgb[d] = rgb[d + 1] = rgb[d + 2] = v;
                    }
                    else if (bpp == 1)
                    {
                        int bit = (data[src + (x >> 3)] >> (7 - (x & 7))) & 1;
                        byte v = bit != 0 ? (byte)0 : (byte)255;
                        rgb[d] = rgb[d + 1] = rgb[d + 2] = v;
                    }
                    else return null;
                }
            }
            return (cols, rows, rgb);
        }
        catch { return null; }
    }

    static byte[] WritePng(int w, int h, byte[] rgb)
    {
        static uint Crc(byte[] b)
        {
            uint c = 0xFFFFFFFFu;
            foreach (var x in b)
            {
                c ^= x;
                for (int k = 0; k < 8; k++) c = (c >> 1) ^ (0xEDB88320u & (uint)(-(c & 1)));
            }
            return c ^ 0xFFFFFFFFu;
        }
        static byte[] Be(uint v) => [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];
        static byte[] Chunk(string tag, byte[] payload)
        {
            var t = System.Text.Encoding.ASCII.GetBytes(tag);
            var body = t.Concat(payload).ToArray();
            return Be((uint)payload.Length).Concat(body).Concat(Be(Crc(body))).ToArray();
        }

        var raw = new byte[h * (w * 3 + 1)];
        for (int y = 0; y < h; y++)
        {
            raw[y * (w * 3 + 1)] = 0;                       // filter: none
            Array.Copy(rgb, y * w * 3, raw, y * (w * 3 + 1) + 1, w * 3);
        }
        // zlib wrapper around raw deflate: 2-byte header + adler32 trailer.
        byte[] deflated;
        using (var ms = new MemoryStream())
        {
            using (var ds = new System.IO.Compression.DeflateStream(
                       ms, System.IO.Compression.CompressionLevel.Optimal, true))
                ds.Write(raw, 0, raw.Length);
            deflated = ms.ToArray();
        }
        uint a = 1, b2 = 0;
        foreach (var x in raw) { a = (a + x) % 65521; b2 = (b2 + a) % 65521; }
        var z = new byte[] { 0x78, 0x9C }.Concat(deflated).Concat(Be((b2 << 16) | a)).ToArray();

        var ihdr = Be((uint)w).Concat(Be((uint)h))
            .Concat(new byte[] { 8, 2, 0, 0, 0 }).ToArray();
        return new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }
            .Concat(Chunk("IHDR", ihdr))
            .Concat(Chunk("IDAT", z))
            .Concat(Chunk("IEND", [])).ToArray();
    }

    static string CssFamily(string typeface) =>
        "F_" + new string(typeface.Where(char.IsLetterOrDigit).ToArray());

    static string Esc(string s) => s
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    static string N(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    // Measured from the legacy renders one style at a time -- see the shaded-box comment
    // in the line loop. Returns null for any style not measured, so it renders as an
    // outline and shows up as a gap rather than as invented shading.
    static float? ShadeFor(int style) => style switch
    {
        7 => 0.85f,
        8 => 0.75f,
        9 => 0.65f,
        10 => 0.55f,
        12 => 0.788f,
        _ => null,
    };

    // --- TrueType vertical metrics -------------------------------------------
    // Chromium places the baseline inside the line box using the font's own
    // ascent/descent, so a fixed offset can never be right across font sizes
    // (half-leading scales with line-height). Read the real values instead.
    // DirectWrite -- and therefore Blink on Windows -- reports OS/2
    // usWinAscent/usWinDescent, so prefer those and fall back to hhea.
    // Returned as em fractions.
    static (float Ascent, float Descent) TtfMetrics(string path)
    {
        var b = File.ReadAllBytes(path);
        ushort U16(int o) => (ushort)((b[o] << 8) | b[o + 1]);
        short S16(int o) => (short)((b[o] << 8) | b[o + 1]);
        int U32(int o) => (b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3];

        int head = 0, os2 = 0, hhea = 0, numTables = U16(4);
        for (int i = 0; i < numTables; i++)
        {
            int rec = 12 + i * 16;
            var tag = System.Text.Encoding.ASCII.GetString(b, rec, 4);
            int off = U32(rec + 8);
            if (tag == "head") head = off;
            else if (tag == "OS/2") os2 = off;
            else if (tag == "hhea") hhea = off;
        }

        float upem = head != 0 ? U16(head + 18) : 2048f;
        if (upem <= 0) upem = 2048f;

        if (os2 != 0)
            return (U16(os2 + 74) / upem, U16(os2 + 76) / upem);
        if (hhea != 0)
            return (S16(hhea + 4) / upem, -S16(hhea + 6) / upem);
        return (0.905f, 0.212f); // Arial-ish fallback
    }

    // --- TrueType horizontal advances (cmap format 4 + hmtx) ------------------
    // Documaker laid every form out using the FXR width table. Where the face we
    // substitute has different advances, text drifts progressively along a run --
    // defect 3. To correct it we need the substituted font's REAL advances, so read
    // hmtx and map characters through cmap. Returns advance per char code, in em.
    static float[] TtfAdvances(string path)
    {
        var b = File.ReadAllBytes(path);
        ushort U16(int o) => (ushort)((b[o] << 8) | b[o + 1]);
        int U32(int o) => (b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3];

        int head = 0, hhea = 0, hmtx = 0, maxp = 0, cmap = 0, numTables = U16(4);
        for (int i = 0; i < numTables; i++)
        {
            int rec = 12 + i * 16;
            switch (System.Text.Encoding.ASCII.GetString(b, rec, 4))
            {
                case "head": head = U32(rec + 8); break;
                case "hhea": hhea = U32(rec + 8); break;
                case "hmtx": hmtx = U32(rec + 8); break;
                case "maxp": maxp = U32(rec + 8); break;
                case "cmap": cmap = U32(rec + 8); break;
            }
        }

        var adv = new float[256];
        if (head == 0 || hhea == 0 || hmtx == 0 || maxp == 0 || cmap == 0) return adv;

        float upem = U16(head + 18);
        if (upem <= 0) upem = 2048f;
        int numH = U16(hhea + 34);
        int numGlyphs = U16(maxp + 4);

        // Locate a Windows Unicode BMP cmap subtable (3,1), else (3,0).
        int sub = 0, nSub = U16(cmap + 2);
        for (int i = 0; i < nSub; i++)
        {
            int rec = cmap + 4 + i * 8;
            int plat = U16(rec), enc = U16(rec + 2);
            if (plat == 3 && (enc == 1 || enc == 0)) { sub = cmap + U32(rec + 4); if (enc == 1) break; }
        }
        if (sub == 0 || U16(sub) != 4) return adv;

        int segX2 = U16(sub + 6), seg = segX2 / 2;
        int endO = sub + 14, startO = endO + segX2 + 2, deltaO = startO + segX2, rangeO = deltaO + segX2;

        int GlyphFor(int ch)
        {
            for (int s = 0; s < seg; s++)
            {
                if (ch > U16(endO + s * 2)) continue;
                int start = U16(startO + s * 2);
                if (ch < start) return 0;
                int ro = U16(rangeO + s * 2);
                if (ro == 0) return (ch + (short)U16(deltaO + s * 2)) & 0xFFFF;
                int gi = rangeO + s * 2 + ro + (ch - start) * 2;
                int g = gi + 1 < b.Length ? U16(gi) : 0;
                return g == 0 ? 0 : (g + (short)U16(deltaO + s * 2)) & 0xFFFF;
            }
            return 0;
        }

        for (int ch = 32; ch < 256; ch++)
        {
            int g = GlyphFor(ch);
            if (g <= 0 || g >= numGlyphs) continue;
            int idx = Math.Min(g, numH - 1);
            adv[ch] = U16(hmtx + idx * 4) / upem;
        }
        return adv;
    }

    // --- Flatten every drawable into one ordered list --------------------------
    // (text tokens from both S,TT static texts and M,TT text areas)
    var texts = parsed.StaticTexts
        .Select(t => (t.Text, t.PageIndex, t.Position, t.FontAttributes.FontId, Bold: false, Kind: "s"))
        .Concat(parsed.TextAreas.SelectMany(a => a.Tokens
            .Where(tok => !tok.IsFieldPlaceholder)
            .Select(tok => (tok.Text, a.PageIndex, tok.Position, FontId: tok.FontId, Bold: tok.IsBold, Kind: "m"))))
        // Keep whitespace-only tokens. Documaker emits the inter-word space as its own
        // positioned token; dropping it merged adjacent words ("Agreement under" ->
        // "Agreementunder") both visually where runs abut and in extracted text.
        .Where(t => t.Text.Length > 0)
        .OrderBy(t => t.PageIndex).ThenBy(t => t.Position.Row1).ThenBy(t => t.Position.Col1)
        .ThenBy(t => t.Text, StringComparer.Ordinal)
        .ToList();

    // --- Embed only the faces this form actually uses ---------------------------
    var missingImages = new List<string>();
    var usedFaces = texts
        .Select(t => htmlFonts.Resolve(t.FontId))
        .Where(f => f != null)
        .Select(f => (f!.Typeface, Bold: f.Bold, f.Italic))
        .Distinct()
        .OrderBy(f => f.Typeface, StringComparer.Ordinal).ThenBy(f => f.Bold).ThenBy(f => f.Italic)
        .ToList();

    var css = new System.Text.StringBuilder();
    var faceMetrics = new Dictionary<string, (float Ascent, float Descent)>(StringComparer.Ordinal);
    var faceAdvances = new Dictionary<string, float[]>(StringComparer.Ordinal);
    if (missingImages.Count > 0)
        Console.Error.WriteLine($"  WARNING: {missingImages.Count} image(s) not resolved: "
            + string.Join(", ", missingImages.Distinct().OrderBy(x => x, StringComparer.Ordinal)));
    foreach (var face in usedFaces)
    {
        var ttf = Path.Combine(fontDir, TtfFor(face.Typeface, face.Bold, face.Italic));
        if (!File.Exists(ttf)) { Console.Error.WriteLine($"  ! missing font {ttf}"); continue; }
        faceMetrics[$"{CssFamily(face.Typeface)}|{face.Bold}|{face.Italic}"] = TtfMetrics(ttf);
        faceAdvances[$"{CssFamily(face.Typeface)}|{face.Bold}|{face.Italic}"] = TtfAdvances(ttf);
        var b64 = Convert.ToBase64String(File.ReadAllBytes(ttf));
        css.Append($"@font-face{{font-family:'{CssFamily(face.Typeface)}';")
           .Append($"font-weight:{(face.Bold ? "bold" : "normal")};")
           .Append($"font-style:{(face.Italic ? "italic" : "normal")};")
           .Append($"src:url(data:font/ttf;base64,{b64}) format('truetype');}}\n");
    }

    var pi0 = parsed.PageInfos.Count > 0 ? parsed.PageInfos[0] : new FapPageInfo(2400, 0, 0, 20400, 26400);

    // A FAP's H, record declares the extent of the SECTION, not of the page. About a
    // third of the library is composable fragments (headers, footers, totals, QCPP_*/
    // QFRM_*) that declare e.g. 612x14pt; Documaker composes them onto the printer
    // page. Honouring the declared height literally produced sliver pages -- see
    // FORM-STUDIO-PLAN section 11, defect 2. A FAP that declares MORE than the page
    // keeps its own extent.
    float declW = pi0.PageWidth * S, declH = pi0.PageHeight * S;
    bool landscape = declW > declH && declW > 612f;
    float pageW = Math.Max(declW, landscape ? 792f : 612f);
    float pageH = Math.Max(declH, landscape ? 612f : 792f);

    var sb = new System.Text.StringBuilder();
    sb.Append("<!doctype html>\n<html><head><meta charset=\"utf-8\">\n<style>\n")
      .Append(css)
      .Append($"@page{{size:{N(pageW)}pt {N(pageH)}pt;margin:0}}\n")
      .Append("html,body{margin:0;padding:0;background:#fff;-webkit-print-color-adjust:exact}\n")
      .Append($".form-page{{position:relative;width:{N(pageW)}pt;height:{N(pageH)}pt;overflow:hidden;page-break-after:always}}\n")
      // No global baseline nudge: each run's top is solved from the real font
      // metrics + the FXR ascent at emit time (see the text-run loop below).
      .Append(".abs{position:absolute;white-space:pre;margin:0;padding:0}\n")
      .Append(".rule{position:absolute;background:#000}\n")
      // border-box: the FAP rectangle IS the outer edge, so the border must sit inside it.
      // With the default content-box the border was added OUTSIDE, drawing every box ~1pt
      // too large and putting its bottom edge ~1.4pt below the legacy rule.
      .Append(".box{position:absolute;border:solid #000;box-sizing:border-box}\n")
      .Append(".shade{position:absolute}\n")
      .Append(".bullet{position:absolute;background:#000;border-radius:50%}\n")
      .Append(".img{position:absolute}\n")
      .Append("</style></head><body>\n");

    for (int p = 0; p < Math.Max(1, parsed.PageCount); p++)
    {
        var pi = p < parsed.PageInfos.Count ? parsed.PageInfos[p] : pi0;
        // Do NOT subtract the H-record origin. Full-page forms declare (0,0) so it never
        // mattered, but composable fragments declare (98,0) -- and measuring the offset
        // against the legacy render shows Documaker does not treat that 98 as a shift to
        // remove (it is a print-margin descriptor). Subtracting it put every fragment
        // ~2.94pt too high, which is the whole offset for DEXOTHA (96 FAP measured) and
        // NR10otHD_B (92). See FORM-STUDIO-PLAN section 15.
        float Px(int col) => col * S;
        float Py(int row) => row * S;

        sb.Append($"<section class=\"form-page\" data-page=\"{p + 1}\">\n");

        // IMAGES. A G, record names a Documaker .LOG raster that lives beside the FAP
        // files; DecodeLog turns it into RGB and WritePng wraps it for the browser. They
        // are emitted before the text so artwork sits behind it, as the shading does.
        //
        // Both resource trees are searched because they are meant to be identical copies
        // and neither is complete on its own. An unresolved or undecodable image is
        // reported on stderr rather than silently skipped -- a missing logo is invisible
        // to every gate we have.
        foreach (var im in parsed.Images.Where(i => i.PageIndex == p)
                     .OrderBy(i => i.Position.Row1).ThenBy(i => i.Position.Col1)
                     .ThenBy(i => i.Name, StringComparer.Ordinal))
        {
            string? logPath = null;
            foreach (var dir in new[] { Path.Combine(MstrRes, "AGCYLNK", "FORMS"),
                                        Path.Combine(MstrRes, "MOEC0", "FORMS") })
            {
                var cand = Path.Combine(dir, im.Name + ".LOG");
                if (File.Exists(cand)) { logPath = cand; break; }
            }
            if (logPath == null) { missingImages.Add(im.Name); continue; }
            var dec = DecodeLog(logPath);
            if (dec == null) { missingImages.Add(im.Name + " (undecodable)"); continue; }

            var png = WritePng(dec.Value.W, dec.Value.H, dec.Value.Rgb);
            sb.Append($"<img class=\"img\" src=\"data:image/png;base64,{Convert.ToBase64String(png)}\" ")
              .Append($"style=\"left:{N(Px(im.Position.Col1))}pt;top:{N(Py(im.Position.Row1))}pt;")
              .Append($"width:{N(Px(im.Position.Col2) - Px(im.Position.Col1))}pt;")
              .Append($"height:{N(Py(im.Position.Row2) - Py(im.Position.Row1))}pt\">\n");
        }

        // SHADED BOXES, emitted FIRST so they sit behind everything else. A non-zero
        // style on an X, record means the rectangle is FILLED, not outlined -- confirmed
        // geometrically: every non-zero-style record matches a filled rectangle in the
        // legacy PDF at identical coordinates (BOPSECT3 7,830pt2 and STF2920214 11,988pt2
        // both exact). We drew them as hollow outlines, which is why BOPSECT3 reproduced
        // only 16.2% of the legacy's non-glyph ink.
        //
        // Order matters and is not cosmetic: these are absolutely positioned, so emitting
        // them alongside the other lines (i.e. after the text) painted the shading OVER the
        // text it is supposed to sit behind. Documaker lays the band down first.
        //
        // The shade is a MEASURED TABLE, deliberately not a formula. Styles 7-10 look
        // perfectly linear (grey = 1.55 - style/10) and style 10 confirmed it exactly at
        // 0.55 -- but style 12 measures 0.788 where that formula predicts 0.35, so it is
        // presumably a hatch pattern rather than a grey level. Extrapolating would have
        // been wrong. An unmeasured style falls through to the outline path rather than
        // being guessed at (determinism rule 4).
        foreach (var l in parsed.Lines.Where(l => l.PageIndex == p && l.Style != 0)
                     .OrderBy(l => l.Position.Row1).ThenBy(l => l.Position.Col1))
        {
            if (ShadeFor(l.Style) is not float shade) continue;
            var g = (int)Math.Round(shade * 255);
            sb.Append($"<div class=\"shade\" style=\"left:{N(Px(l.Position.Col1))}pt;top:{N(Py(l.Position.Row1))}pt;")
              .Append($"width:{N(Px(l.Position.Col2) - Px(l.Position.Col1))}pt;")
              .Append($"height:{N(Py(l.Position.Row2) - Py(l.Position.Row1))}pt;")
              .Append($"background:rgb({g},{g},{g})\"></div>\n");
        }

        foreach (var t in texts.Where(t => t.PageIndex == p))
        {
            var f = htmlFonts.Resolve(t.FontId);
            float size = f?.PointSize > 0 ? f.PointSize : 10f;
            var fam = CssFamily(f?.Typeface ?? "Arial");
            bool bold = (f?.Bold ?? false) || t.Bold;
            bool italic = f?.Italic ?? false;
            var faceKey = $"{fam}|{bold}|{italic}";

            // BASELINE ANCHOR: Documaker puts the baseline on the BOTTOM edge of the
            // declared box, not the top. Measured over 17,193 legacy text records across
            // 109 forms: baseline - Py(row2) is -0.09pt with a per-font stdev of 0.06,
            // and 17,191 of them land within 0.5pt of it. The distance from row1, by
            // contrast, swings 3.9-16.8pt because it absorbs the box height.
            //
            // This is what every earlier baseline experiment was missing. The offset
            // looked per-form and bimodal (FORM-STUDIO-PLAN sections 12-15) only because
            // a form tends to use one box height throughout, so "which form" stood in for
            // "how tall is the box". It is not a font property, so no per-FontId or
            // per-form scalar could ever have described it -- which is exactly why eight
            // calibration variants all measured the same.
            //
            // Chromium places the baseline inside a line box at
            //     top + halfLeading + ascent  ==  top + (lineHeight + ascent - descent)/2
            // so solve that for top. Ascent/descent come from the substituted face's own
            // tables; any residual is a constant per (face, size, line-height) and shows
            // up as a uniform offset rather than the form-dependent scatter we had before.
            // Chromium does not place the baseline exactly where the box model predicts
            // from the face's hhea/OS-2 ascent: measured over 76,499 runs it sits 0.600pt
            // HIGHER (pooled median; p5..p95 = -0.95..-0.20, i.e. systematically one-sided).
            // The offset is a size-independent constant, not an em fraction -- consistent
            // with the ascent being rounded to whole device pixels (1px = 0.75pt at 96dpi)
            // rather than with a metrics error, which would scale with point size.
            //
            // This is a property of the RENDERER, not of the document, so it is pinned
            // alongside the Chromium build (determinism rule 5) rather than calibrated per
            // form or per font. Measured from our own output -- no legacy oracle involved.
            // A per-face table and even a per-document one were both simulated and neither
            // beat this single constant (107/109 either way), so there is nothing to gain
            // from a finer key.
            const float BaselineBelowRow2 = 0.09f;
            const float ChromiumBaselineBias = 0.60f;
            float lh = f != null && f.LineHeight > 0 ? f.LineHeight * S : size * 1.2f;
            var (ascEm, descEm) = faceMetrics.TryGetValue(faceKey, out var fm)
                ? fm : (0.905f, 0.212f);

            // ADVANCE WIDTHS -- the run-level ADDITIVE correction below is the incumbent
            // because it measures best, NOT because it is the truest model. Four mechanisms
            // have been tried; see FORM-STUDIO-PLAN section 19 for the numbers.
            //
            // What is actually true about the legacy render, both measured directly:
            //   * The correction is MULTIPLICATIVE. Per-character advances of two legacy
            //     spans in one face are related by a pure ratio, cv = 0.0000 (exact); the
            //     additive model's cv is 0.15-0.40.
            //   * Documaker fits each token to ITS DECLARED BOX: legacy width / (col2-col1)
            //     has median 0.9996-1.0057, 89-93% of records inside 2%.
            //
            // And yet BOTH faithful implementations measured worse at the gate than this
            // additive approximation -- scaling the point size by the FXR ratio was a wash,
            // and a box-fit scaleX was much worse (EB2410A 92.4 -> 71.8%, P0010G
            // 98.7 -> 66.9% within 1pt). The likely reason is that a declared box is often
            // padding rather than a tight fit, so box-fitting stretches text that legacy
            // leaves alone, and we cannot yet tell the two cases apart. Reproducing this
            // properly needs per-glyph positioning driven by the legacy TJ offsets, not a
            // better whole-run scale factor.
            //
            // So: distribute the FXR/actual width difference as letter-spacing, which gets
            // each run's total width right and leaves a residual drift in its interior.
            string spacing = "";
            if (FxrAdvanceCorrection && f != null && t.Text.Length > 0
                && faceAdvances.TryGetValue(faceKey, out var advTable))
            {
                float natural = 0f;
                foreach (char c in t.Text) natural += (c < 256 ? advTable[c] : advTable['n']) * size;
                float target = f.MeasureFap(t.Text) * S;
                if (target > 0f && natural > 0f)
                {
                    // Chromium applies letter-spacing after every character, trailing included.
                    float ls = (target - natural) / t.Text.Length;
                    if (Math.Abs(ls) >= 0.005f) spacing = $";letter-spacing:{N(ls)}pt";
                }
            }

            float baseline = Py(t.Position.Row2) - BaselineBelowRow2;
            float top = baseline - (lh + ascEm * size - descEm * size) / 2f
                      + ChromiumBaselineBias;

            // One span per FAP text record, positioned at its col1. Splitting a run into
            // per-word spans anchored at their FXR-measured offsets was TRIED and measured
            // WORSE at the gate (2026-08-22): it does remove the intra-run accumulation
            // (EB2410A's dx went from -0.01pt at the first glyph / -0.54pt at the fortieth
            // to -0.01/+0.18) but it replaces it with a larger scatter, and every accepted
            // form regressed -- EB2410A 92.4 -> 88.0%, EB22489Q 94.3 -> 87.5%, P0010G
            // 98.7 -> 95.1%, A0238C 74.1 -> 70.5%. Chromium's own inter-word advances plus
            // the run-level FXR correction track the legacy render better than FXR word
            // offsets do. Do not re-try this without a different mechanism.
            sb.Append($"<span class=\"abs\" data-fid=\"{t.FontId}\" data-kind=\"{t.Kind}\" style=\"left:{N(Px(t.Position.Col1))}pt;top:{N(top)}pt;")
              .Append($"font-family:'{fam}';font-size:{N(size)}pt;line-height:{N(lh)}pt{spacing}")
              .Append(bold ? ";font-weight:bold" : "")
              .Append((f?.Italic ?? false) ? ";font-style:italic" : "")
              .Append($"\">{Esc(t.Text)}</span>\n");
        }

        foreach (var l in parsed.Lines.Where(l => l.PageIndex == p
                         && !(l.Style != 0 && ShadeFor(l.Style) != null))
                     .OrderBy(l => l.Position.Row1).ThenBy(l => l.Position.Col1))
        {
            float x1 = Px(l.Position.Col1), y1 = Py(l.Position.Row1);
            float x2 = Px(l.Position.Col2), y2 = Py(l.Position.Row2);
            float thick = Math.Max(0.5f, l.Width * S);

            // A row of SIDE-BY-SIDE line records is an underline row, not a row of
            // boxes. Products spotted this by eye on M7902AA: legacy underlines each
            // column header ("Number", "Address", "Property", "of Loss Payee") and we drew
            // a rectangle around each one.
            //
            // Measured over the 1,000-form sweep, an X, record in group (24,24) or (25,25)
            // that shares its page, group and top row with at least one sibling renders as
            // rules, not a rectangle, in 482 of the 506 cases legacy draws at all (95%):
            //   (24,24) h<420  107 rules / 0 rect      (25,25) h<420   69 / 10
            //   (24,24) h>=420 183 rules / 11 rect     (25,25) h>=420 123 / 3
            // A LONE record in the same groups is a rectangle 58-80% of the time, so the
            // sibling test -- not the group alone -- is what carries the signal. An earlier
            // group-only rule was reverted (section 32) because it dropped real edges; this
            // one is confined to the case the data actually supports.
            // THREE or more, not merely two: at >=3 the rule is 97% accurate (324 rules
            // against 9 rectangles), where exactly 2 siblings is only 91% (158/15). A
            // header row has three or four columns; a pair of boxes side by side is
            // often just two boxes. Requiring three keeps the case Products validated
            // (M7902AA has four) and gives back the recall the looser rule cost.
            bool siblingRow = (l.Group == "24,24" || l.Group == "25,25")
                && parsed.Lines.Count(o => o.PageIndex == l.PageIndex && o.Group == l.Group
                                           && o.Position.Row1 == l.Position.Row1) >= 3;

            // M,I is a bullet -- a solid disc, not a box. Legacy draws it as a filled
            // path of four curves, black, at exactly the declared coordinates.
            if (l.Source == "MI")
            {
                sb.Append($"<div class=\"bullet\" style=\"left:{N(x1)}pt;top:{N(y1)}pt;")
                  .Append($"width:{N(x2 - x1)}pt;height:{N(y2 - y1)}pt\"></div>\n");
                continue;
            }

            // M,PX (a line record nested in a text area) is NOT a rectangle. Measured over
            // 67 records in 10 forms against the legacy renders:
            //   * vertical edges are NEVER drawn -- zero in every form checked;
            //   * the BOTTOM edge is drawn 64/67 (96%);
            //   * the TOP edge splits cleanly on the box height: 2/35 below 420 FAP units,
            //     31/32 at or above it.
            // The mechanism is unknown, so this is an empirical rule, but it is a wide split
            // rather than a fitted curve and it covers the library: of 2,448 M,PX records,
            // 1,238 are under the threshold and 1,207 over, with just 3 in between.
            // An X,(24,24) record no taller than one text line renders as horizontal
            // rules 73/79 (92%) of the time, and adopting that measured WORSE: the ink
            // recall gate went 159/163 -> 156/163 because the other 8% lose the vertical
            // edges legacy does draw. 92% is not enough when the failure mode is
            // dropping real ink. Reverted -- see FORM-STUDIO-PLAN section 32.
            // FapLine.Group carries the second group for anyone continuing this; it is
            // deliberately not interpreted here.
            if ((l.Source == "MPX" || siblingRow) && Math.Abs(y2 - y1) >= 0.01f)
            {
                var barH = Math.Max(0.5f, l.Width * S);
                if (l.Position.Row2 - l.Position.Row1 >= 420)
                    sb.Append($"<div class=\"rule\" style=\"left:{N(x1)}pt;top:{N(y1)}pt;width:{N(x2 - x1)}pt;height:{N(barH)}pt\"></div>\n");
                sb.Append($"<div class=\"rule\" style=\"left:{N(x1)}pt;top:{N(y2)}pt;width:{N(x2 - x1)}pt;height:{N(barH)}pt\"></div>\n");
                continue;
            }

            // A "rectangle" thinner than a point is a RULE, not a box. The threshold used
            // to be 0.01pt, so a 0.45pt-tall X, record became a bordered box -- two
            // hairlines with a gap, where Documaker draws one solid bar (measured on
            // F9950B: legacy emits a filled 254x0.4pt bar; we emitted a 0.45pt box whose
            // 0.5pt borders collapse). 1pt is comfortably below any real box in the
            // library and comfortably above every bar.
            const float RuleMaxThickness = 1.0f;
            if (Math.Abs(y2 - y1) < RuleMaxThickness)   // horizontal rule
                sb.Append($"<div class=\"rule\" style=\"left:{N(x1)}pt;top:{N(y1)}pt;width:{N(x2 - x1)}pt;height:{N(thick)}pt\"></div>\n");
            else if (Math.Abs(x2 - x1) < RuleMaxThickness) // vertical rule
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
    // M7215A carries 20 M,PX records (lines nested in a text area) and BOPSECT3 carries a
    // style-8 shaded box. Both are here because the suite was BLIND to those constructs:
    // it reported "OK, no regressions" through two real parser/emitter changes simply
    // because none of the original eight forms used them. A suite cannot guard a construct
    // it does not contain.
    string[] regressForms = { "QTE_EA9910E", "QTE_COVER_A", "QTE_BILLINFO", "QFRM_FGL", "QFRM_FPL", "QTE_COVAUTOSYM", "QTE_FORM", "MCS90A", "M7215A", "BOPSECT3" };
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
