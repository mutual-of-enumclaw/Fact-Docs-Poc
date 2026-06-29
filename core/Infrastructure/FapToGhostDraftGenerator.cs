using System.Text;

namespace FapPdfTools.Server.Infrastructure;

/// <summary>
/// Converts a parsed FAP file into a GhostDraft 5.x .gd document file (XML + RTF).
///
/// Each FAP F, field becomes a %[N] placeholder in the RTF body AND a corresponding
/// <instruction xsi:type="fillPointType"> in the <markup><instructions> section,
/// bound to the appropriate Model Library concept path where one is known.
/// Unknown fields are emitted as unbound placeholders (path xsi:nil="true").
/// </summary>
public static class FapToGhostDraftGenerator
{
    private const double FapToTwips  = 0.6;    // 1440/2400
    private const int    FapLeftMargin = 1800; // standard left margin column

    // FAP font ID => (RTF \fs half-points, bold)
    private static readonly Dictionary<int, (int HalfPts, bool Bold)> FontMap = new()
    {
        // 16xxx series — standard Documaker document fonts
        { 16010, (18, false) }, { 16012, (20, false) }, { 16110, (18, false) },
        { 16112, (20, true)  }, { 16114, (26, true)  },
        // 14xxx series — slightly smaller
        { 14008, (18, false) }, { 14010, (18, false) }, { 14012, (20, false) },
        { 14110, (20, false) }, { 14112, (20, true)  }, { 14118, (28, true)  },
        // 10xxx series — small body / WIP form fields
        { 10008, (16, false) }, { 10009, (16, false) }, { 10010, (18, false) },
        { 10108, (16, false) }, { 10109, (16, false) }, { 10110, (18, false) },
        { 10209, (18, false) }, { 10210, (18, false) },
        // 18xxx / 19xxx series — large display / header fonts
        { 18008, (22, false) }, { 18010, (22, false) }, { 18012, (24, false) },
        { 18112, (24, true)  }, { 18114, (28, true)  }, { 18124, (44, true)  },
        { 19008, (20, true)  }, { 19010, (22, true)  }, { 19012, (24, true)  },
        { 19114, (28, true)  },
    };

    // -----------------------------------------------------------------------
    // Model Library concept bindings
    // (rootNode, rootGuid, pathNodes[])
    // -----------------------------------------------------------------------

    private record NodeRef(string Name, string Guid);
    private record ConceptBinding(string Description, string RootNode, string RootGuid, NodeRef[] PathNodes);

    // Keyed by FAP field name (case-insensitive, exact match first, then prefix)
    private static readonly Dictionary<string, ConceptBinding> FieldConceptMap =
        new(StringComparer.OrdinalIgnoreCase)
    {
        // Policy
        ["POLNUM"]     = new("Policy Number of Policy",
                             "Policy", "ee97488b-9ec8-4f0a-88db-dd6a000f5f22",
                             [new("Policy Number", "16ed3972-4199-4419-93de-0ff41c640622")]),
        ["POLICYNO"]   = new("Policy Number of Policy",
                             "Policy", "ee97488b-9ec8-4f0a-88db-dd6a000f5f22",
                             [new("Policy Number", "16ed3972-4199-4419-93de-0ff41c640622")]),
        ["POLEFF"]     = new("Policy Effective Date of Policy",
                             "Policy", "ee97488b-9ec8-4f0a-88db-dd6a000f5f22",
                             [new("Policy Effective Date", "db668a25-ce3b-4d08-a1f8-db911b0a2e46")]),
        ["POLEXPDATE"] = new("Policy Expiration Date of Policy",
                             "Policy", "ee97488b-9ec8-4f0a-88db-dd6a000f5f22",
                             [new("Policy Expiration Date", "bb431649-c0cc-48fb-a417-63849b55ad7b")]),
        ["POLEXPIRE"]  = new("Policy Expiration Date of Policy",
                             "Policy", "ee97488b-9ec8-4f0a-88db-dd6a000f5f22",
                             [new("Policy Expiration Date", "bb431649-c0cc-48fb-a417-63849b55ad7b")]),
        // Insured
        ["INSNAME"]    = new("Full Name of Insured",
                             "Insured", "aade6b84-2f42-4d5a-8eb9-870e8a063187",
                             [new("Full Name", "32280915-3ab9-49e4-9c45-60923542e048")]),
        ["NAMEDINS"]   = new("Full Name of Insured",
                             "Insured", "aade6b84-2f42-4d5a-8eb9-870e8a063187",
                             [new("Full Name", "32280915-3ab9-49e4-9c45-60923542e048")]),
        ["INSADDR1"]   = new("Address Line 1 of Insured",
                             "Insured", "aade6b84-2f42-4d5a-8eb9-870e8a063187",
                             [new("Address Line 1", "7a25c42e-ec66-49ee-b098-37e31fb3a67f")]),
        ["INSADDR2"]   = new("Address Line 2 of Insured",
                             "Insured", "aade6b84-2f42-4d5a-8eb9-870e8a063187",
                             [new("Address Line 2", "7644bfa5-c8ee-4df0-9a7a-5ddb7601cc77")]),
        ["INSCITY"]    = new("City of Insured",
                             "Insured", "aade6b84-2f42-4d5a-8eb9-870e8a063187",
                             [new("City", "3204e969-b7ef-4ee7-a496-f345fb51ee3a")]),
        ["INSSTATE"]   = new("State of Insured",
                             "Insured", "aade6b84-2f42-4d5a-8eb9-870e8a063187",
                             [new("State", "2db53ea0-31c2-4d87-b1c2-52b373d10776")]),
        ["INSZIP"]     = new("Zip of Insured",
                             "Insured", "aade6b84-2f42-4d5a-8eb9-870e8a063187",
                             [new("Zip", "3d327169-5851-424f-835f-f65dbe9d0f1b")]),
        // Agent
        ["AGENTNAME"]  = new("Full Name of Agent",
                             "Agent", "fd9eebc5-e398-4c3a-8e10-d78c46900de0",
                             [new("Full Name", "9b7a6e43-7c6a-44bc-8a2b-aef9c5d03b55")]),
        // MCS-90 form fields (root: MOECAPolicyLevelCoverages)
        ["ISSUEDTO"]   = new("MCS-90 Motor Carrier Name",
                             "MOECAPolicyLevelCoverages", "b58de484-20b2-4d56-8752-0455438c8c74",
                             [new("MCS-90", "80678beb-986e-4a7c-89aa-cf85d062e2d3"),
                              new("MotorCarrierName", "007526b3-8557-4db5-a62f-9138338180f7")]),
        ["OF"]         = new("MCS-90 Motor Carrier State",
                             "MOECAPolicyLevelCoverages", "b58de484-20b2-4d56-8752-0455438c8c74",
                             [new("MCS-90", "80678beb-986e-4a7c-89aa-cf85d062e2d3"),
                              new("MotorCarrierStateOrProvince", "f2dfd6db-b34c-40b6-85fe-44cdcdcd5992")]),
        ["USDOT"]      = new("MCS-90 US DOT Number",
                             "MOECAPolicyLevelCoverages", "b58de484-20b2-4d56-8752-0455438c8c74",
                             [new("MCS-90", "80678beb-986e-4a7c-89aa-cf85d062e2d3"),
                              new("USDOTNumber", "74b96b32-edd9-47c7-b61a-ce6b0cdd8588")]),
        ["DAY"]        = new("MCS-90 Issued Day",
                             "MOECAPolicyLevelCoverages", "b58de484-20b2-4d56-8752-0455438c8c74",
                             [new("MCS-90", "80678beb-986e-4a7c-89aa-cf85d062e2d3"),
                              new("Issued Day", "60affef8-63e0-4e40-bc45-2baa5226a819")]),
        ["MONTH"]      = new("MCS-90 Issued Month",
                             "MOECAPolicyLevelCoverages", "b58de484-20b2-4d56-8752-0455438c8c74",
                             [new("MCS-90", "80678beb-986e-4a7c-89aa-cf85d062e2d3"),
                              new("Issued Month", "ece33aca-b375-4fce-aba1-b55328915609")]),
        ["YR"]         = new("MCS-90 Issued Year",
                             "MOECAPolicyLevelCoverages", "b58de484-20b2-4d56-8752-0455438c8c74",
                             [new("MCS-90", "80678beb-986e-4a7c-89aa-cf85d062e2d3"),
                              new("Issued Year", "effde952-23ce-4eac-89fd-3227c1e01f29")]),
        ["POLICYNUM"]  = new("MCS-90 Amending Policy Number",
                             "MOECAPolicyLevelCoverages", "b58de484-20b2-4d56-8752-0455438c8c74",
                             [new("MCS-90", "80678beb-986e-4a7c-89aa-cf85d062e2d3"),
                              new("AmendingPolicyNumber", "b2a5a1ee-b27b-4134-a9e7-fc0ce8266436")]),
        ["EFFDATE"]    = new("MCS-90 Effective Date",
                             "MOECAPolicyLevelCoverages", "b58de484-20b2-4d56-8752-0455438c8c74",
                             [new("MCS-90", "80678beb-986e-4a7c-89aa-cf85d062e2d3"),
                              new("EffectiveDate", "8962ca0e-748c-442c-83d7-dffe13685d1c")]),
        ["LIMIT"]      = new("MCS-90 In Excess Of",
                             "MOECAPolicyLevelCoverages", "b58de484-20b2-4d56-8752-0455438c8c74",
                             [new("MCS-90", "80678beb-986e-4a7c-89aa-cf85d062e2d3"),
                              new("InExcessOf", "077a2460-99f2-48a8-910a-1ce91c40bb50")]),
    };

    // Strip duplicate-suffix like " #002" that FAP appends for repeated field names
    private static string NormalizeFieldName(string name)
        => name.Contains(" #") ? name[..name.IndexOf(" #")].Trim() : name.Trim();

    private static ConceptBinding? LookupBinding(string rawFieldName)
    {
        var name = NormalizeFieldName(rawFieldName);
        if (FieldConceptMap.TryGetValue(name, out var exact)) return exact;
        // Prefix match: INSNAME001, POLNUM_1, etc.
        foreach (var kv in FieldConceptMap)
            if (name.StartsWith(kv.Key, StringComparison.OrdinalIgnoreCase)) return kv.Value;
        return null;
    }

    // -----------------------------------------------------------------------
    // Public entry point
    // -----------------------------------------------------------------------

    private record TextItem(int Row, int Col, int HalfPts, bool Bold, string Text, bool IsField);
    private record FieldSlot(int Id, string RawName, ConceptBinding? Binding);

    private static (int HalfPts, bool Bold) GetFont(int fontId)
        => FontMap.TryGetValue(fontId, out var f) ? f : (20, false);

    public static string Generate(FapParseResult fap, string formTitle, bool isWip = false, byte[]? backgroundEmf = null)
    {
        // Build ordered list of fields with their %[N] IDs and concept bindings
        var slots = new List<FieldSlot>();
        int idx = 1;
        foreach (var f in fap.Fields.OrderBy(x => x.PageIndex).ThenBy(x => x.Position.Row1))
            slots.Add(new FieldSlot(idx++, f.Name, LookupBinding(f.Name)));

        var rtf = isWip ? BuildWipRtf(fap, slots, backgroundEmf) : BuildRtf(fap, slots, formTitle);
        return WrapInGdXml(rtf, formTitle, slots);
    }

    // -----------------------------------------------------------------------
    // RTF builder
    // -----------------------------------------------------------------------

    private static string BuildRtf(FapParseResult fap, List<FieldSlot> slots, string formTitle)
    {
        // Build a map from (page, fieldName) -> slot ID for inline placeholder insertion
        var fieldIdMap = new Dictionary<(int page, string name), int>();
        foreach (var s in slots)
        {
            // match by position order - we iterate fields in same order as slots
        }
        // Simpler: track by sequential index matching the per-page field order
        var sb = new StringBuilder();

        sb.Append(@"{\rtf1 \adeflang1025\uc1\deflang1033 ");
        sb.Append(@"{\fonttbl{\f0 Times New Roman;}{\f1 Arial;}}");
        sb.Append(@"{\colortbl;}");
        sb.Append($@"{{\*\generator GhostDraft 5.2.50927.0;}}{{\info{{\title {EscapeRtf(formTitle)}}}}}");
        sb.Append(@"\noxlattoyen\expshrtn\noultrlspc\dntblnsbdb\nospaceforul\nolnhtadjtbl\splytwnine\ftnlytwnine\htmautsp\useltbaln\alntblind\lytcalctblwd\lyttblrtgr\lnbrkrule\nobrkwrptbl\snaptogridincell\allowfieldendsel\wrppunct\asianbrkrule\newtblstyruls\nogrowautofit\utinl\noindnmbrts\nocxsptable\ftnbj\aenddoc ");

        var pi = fap.PageInfos.Count > 0 ? fap.PageInfos[0] : new FapPageInfo(2400, 0, 0, 20400, 26400);
        int pgwTw = Math.Max(12240, (int)(pi.PageWidth  * FapToTwips));
        int pghTw = Math.Max(15840, (int)(pi.PageHeight * FapToTwips));
        const int ml = 1080, mt = 720, mr = 1080, mb = 720;

        sb.Append($@"\sectd\sbknone\pgwsxn{pgwTw}\pghsxn{pghTw}\marglsxn{ml}\margtsxn{mt}\margrsxn{mr}\margbsxn{mb}\headery1080\footery360");

        int fieldIndex = 1;
        for (int page = 0; page < Math.Max(1, fap.PageCount); page++)
        {
            if (page > 0)
            {
                sb.Append($@"\pard\plain\ql\li0\ri0\fi0\sb0\sa0\sl240\slmult1{{\cf0\f1\fs20\ulnone\ulc0 \par }}");
                sb.Append($@"\sectd\sbkpage\pgwsxn{pgwTw}\pghsxn{pghTw}\marglsxn{ml}\margtsxn{mt}\margrsxn{mr}\margbsxn{mb}\headery1080\footery360");
            }

            var items = new List<TextItem>();

            foreach (var t in fap.StaticTexts.Where(x => x.PageIndex == page))
            {
                var (hp, bold) = GetFont(t.FontAttributes.FontId);
                items.Add(new TextItem(t.Position.Row1, t.Position.Col1, hp, bold, EscapeRtf(t.Text), false));
            }

            foreach (var area in fap.TextAreas.Where(x => x.PageIndex == page))
                foreach (var tok in area.Tokens)
                {
                    if (string.IsNullOrWhiteSpace(tok.Text)) continue;
                    var (hp, bold) = GetFont(tok.FontId);
                    items.Add(new TextItem(tok.Position.Row1, tok.Position.Col1, hp, bold || tok.IsBold, EscapeRtf(tok.Text), false));
                }

            foreach (var f in fap.Fields.Where(x => x.PageIndex == page))
            {
                var (hp, _) = GetFont(f.FontAttributes.FontId);
                items.Add(new TextItem(f.Position.Row1, f.Position.Col1, hp, false, $"%[{fieldIndex++}]", true));
            }

            items.Sort((a, b) => a.Row != b.Row ? a.Row.CompareTo(b.Row) : a.Col.CompareTo(b.Col));

            int prevRow = -1;
            foreach (var line in GroupIntoLines(items))
            {
                if (prevRow >= 0 && line[0].Row - prevRow > 450)
                    EmitBlankParagraph(sb);
                EmitParagraph(sb, line);
                prevRow = line[0].Row;
            }
        }

        sb.Append('}');
        return sb.ToString();
    }

    private static List<List<TextItem>> GroupIntoLines(List<TextItem> items)
    {
        var result = new List<List<TextItem>>();
        if (items.Count == 0) return result;
        var current = new List<TextItem> { items[0] };
        int baseRow = items[0].Row;
        for (int i = 1; i < items.Count; i++)
        {
            if (Math.Abs(items[i].Row - baseRow) <= 150) { current.Add(items[i]); }
            else { result.Add(current); current = [items[i]]; baseRow = items[i].Row; }
        }
        result.Add(current);
        return result;
    }

    private static void EmitBlankParagraph(StringBuilder sb)
    {
        sb.Append(@"\pard\plain\ql\sb0\sa0\li0\ri0\fi0\sl240\slmult1\nowidctlpar\f1\fs20\b0\i0\nosupersub\caps0\v0\shad0\outl0\strike0\lang1033\expnd0\expndtw0\charscalex100");
        sb.Append("{\n\\par }");
    }

    private static void EmitParagraph(StringBuilder sb, List<TextItem> line)
    {
        int indentTw    = Math.Max(0, (int)((line[0].Col - FapLeftMargin) * FapToTwips));
        int paraHalfPts = line.Max(x => x.HalfPts);
        sb.Append($@"\pard\plain\ql\sb0\sa0\li{indentTw}\ri0\fi0\sl240\slmult1\nowidctlpar\f1\fs{paraHalfPts}\b0\i0\nosupersub\caps0\v0\shad0\outl0\strike0\lang1033\expnd0\expndtw0\charscalex100");
        sb.Append('{');
        for (int i = 0; i < line.Count; i++)
        {
            var item = line[i];
            if (i > 0) sb.Append($@"{{\cf0\f1\fs{item.HalfPts}\ulnone\ulc0  }}");
            if (item.IsField)
            {
                sb.Append($@"{{\cf0\f1\fs{item.HalfPts}\ulnone\ulc0 %}}");
                string indexPart = item.Text.Length > 1 ? item.Text[1..] : item.Text;
                sb.Append($@"{{\cf0\f1\fs{item.HalfPts}\ulnone\ulc0 {indexPart}}}");
            }
            else
            {
                string boldOn  = item.Bold ? @"\b"  : @"\b0";
                string boldOff = item.Bold ? @"\b0" : "";
                sb.Append($@"{{{boldOn}\cf0\f1\fs{item.HalfPts}\ulnone\ulc0 {item.Text}{boldOff}}}");
            }
        }
        sb.Append($"{{\n\\par }}}}");
    }

    // -----------------------------------------------------------------------
    // WIP / shape-based RTF builder
    // For WIP forms, all content (text labels + fillable fields + borders) is
    // rendered as absolutely-positioned RTF \shp shapes anchored to the page.
    // This matches the GhostDraft WIP form format used for MCS-90, ME forms, etc.
    // -----------------------------------------------------------------------

    /// Emits an RTF shape property: {\sp{\sn NAME}{{\sv VALUE}}}
    /// (GhostDraft uses double-braces around \sv VALUE — verified from real .gd files)
    private static string SpProp(string name, string value)
        => $@"{{\sp{{\sn {name}}}{{{{\sv {value}}}}}}}";

    private static string BuildWipRtf(FapParseResult fap, List<FieldSlot> slots, byte[]? backgroundEmf = null)
    {
        var sb = new StringBuilder();

        // RTF header — identical to flowing format
        sb.Append(@"{\rtf1 \adeflang1025\uc1\deflang1033 ");
        sb.Append(@"{\fonttbl{\f0 Times New Roman;}{\f1 Arial;}}");
        sb.Append(@"{\colortbl;\red0\green0\blue0;}"); // \cf1 = black (matches reference WIP format)
        sb.Append(@"{\*\generator GhostDraft 5.2.50927.0;}");
        sb.Append(@"\noxlattoyen\expshrtn\noultrlspc\dntblnsbdb\nospaceforul\nolnhtadjtbl\splytwnine\ftnlytwnine\htmautsp\useltbaln\alntblind\lytcalctblwd\lyttblrtgr\lnbrkrule\nobrkwrptbl\snaptogridincell\allowfieldendsel\wrppunct\asianbrkrule\newtblstyruls\nogrowautofit\utinl\noindnmbrts\nocxsptable\ftnbj\aenddoc ");

        var pi = fap.PageInfos.Count > 0 ? fap.PageInfos[0] : new FapPageInfo(2400, 0, 0, 20400, 26400);
        int pgwTw = Math.Max(12240, (int)(pi.PageWidth  * FapToTwips));
        int pghTw = Math.Max(15840, (int)(pi.PageHeight * FapToTwips));

        // Zero margins — all shapes use absolute page-origin coordinates
        sb.Append($@"\sectd\sbknone\pgwsxn{pgwTw}\pghsxn{pghTw}\marglsxn0\margtsxn0\margrsxn0\margbsxn0\headery1080\footery360");

        // Base paragraph required before shapes in GhostDraft WIP RTF
        sb.Append(@"\pard\plain\ql\sb0\sa0\li0\ri0\fi0\sl240\slmult1\widctlpar\f1\fs20 ");

        int z = 1;

        if (backgroundEmf != null)
        {
            // Background image (shape 0, behind everything) — contains all static content.
            // Only emit interactive field boxes on top; no static text/line shapes needed.
            AppendBackgroundImageShape(sb, pgwTw, pghTw, backgroundEmf, z++);
        }
        else
        {
            // No background image: render static text and lines as shapes
            foreach (var line in fap.Lines)
                AppendLineShape(sb, line.Position, line.Width, z++);
            foreach (var t in fap.StaticTexts)
            {
                var (hp, bold) = GetFont(t.FontAttributes.FontId);
                AppendStaticTextShape(sb, t.Position, hp, bold, EscapeRtf(t.Text), z++);
            }
            foreach (var area in fap.TextAreas)
                AppendTextAreaShape(sb, area, z++);
        }

        // Interactive fill-point field boxes (on top)
        var orderedFields = fap.Fields.OrderBy(x => x.PageIndex).ThenBy(x => x.Position.Row1).ToList();
        for (int i = 0; i < slots.Count && i < orderedFields.Count; i++)
        {
            var f = orderedFields[i];
            var fieldName = NormalizeFieldName(f.Name);
            AppendFieldShape(sb, f.Position, f.Length, fieldName, slots[i].Id, z++);
        }

        // Close the base paragraph and RTF group
        sb.Append(@"{\cf0\f1\fs20\ulnone\ulc0 \par }}");
        return sb.ToString();
    }

    /// Emit an absolutely-positioned static text label shape (non-interactive).
    private static void AppendStaticTextShape(
        StringBuilder sb,
        (int Row1, int Col1, int Row2, int Col2) pos,
        int halfPts, bool bold, string text, int z)
    {
        int l = (int)(pos.Col1 * FapToTwips);
        int t = (int)(pos.Row1 * FapToTwips);
        int r = Math.Max(l + 200, (int)(pos.Col2 * FapToTwips));
        int b = Math.Max(t + halfPts + 80, (int)(pos.Row2 * FapToTwips));
        string bld = bold ? @"\b" : @"\b0";

        sb.Append($@"{{\shp{{\*\shpinst\shpleft{l}\shptop{t}\shpright{r}\shpbottom{b}\shpbxpage\shpbypage\shpwr3\shpwrk0\shpfblwtxt0\shpz{z}");
        sb.Append(SpProp("shapeType", "202"));
        sb.Append(SpProp("fBehindDocument", "0"));
        sb.Append(SpProp("fFilled", "0"));
        sb.Append(SpProp("fLine", "0"));
        sb.Append($@"{{\shptxt\pard\plain\s6\ql\sb0\sa0\li0\ri0\fi0\sl240\slmult1\widctlpar\f1\fs{halfPts}{bld}\i0\nosupersub\caps0\v0\shad0\outl0\strike0\expnd0\expndtw0\charscalex100");
        sb.Append($@"{{\cf0\f1\fs{halfPts}{bld}\ulnone\ulc0 {text}\par}}");
        sb.Append("}}}}"); // closes \shptxt, \*\shpinst, and the two outer {{ of the shape
    }

    /// Emit an absolutely-positioned interactive fill-point field box with %[N] placeholder.
    /// Format matches GhostDraft WIP reference (MCS90 0117.gd): zero padding, cf1, wzName.
    private static void AppendFieldShape(
        StringBuilder sb,
        (int Row1, int Col1, int Row2, int Col2) pos,
        int maxLen, string fieldName, int slotId, int z)
    {
        int l = (int)(pos.Col1 * FapToTwips);
        int t = (int)(pos.Row1 * FapToTwips);
        int r = Math.Max(l + 200, (int)(pos.Col2 * FapToTwips));
        int b = Math.Max(t + 240, (int)(pos.Row2 * FapToTwips));

        sb.Append($@"{{\shp{{\*\shpinst\shpleft{l}\shptop{t}\shpright{r}\shpbottom{b}\shpbxpage\shpbypage\shpwr3\shpwrk0\shpfblwtxt0\shpz{z}");
        sb.Append(SpProp("shapeType", "202"));
        sb.Append(SpProp("dxTextLeft", "0")); sb.Append(SpProp("dxTextRight", "0"));
        sb.Append(SpProp("dyTextTop", "0")); sb.Append(SpProp("dyTextBottom", "0"));
        sb.Append(SpProp("fBehindDocument", "0")); sb.Append(SpProp("fFlipH", "0")); sb.Append(SpProp("fFlipV", "0"));
        sb.Append(SpProp("fUseShapeAnchor", "0")); sb.Append(SpProp("fFilled", "0")); sb.Append(SpProp("fLine", "0"));
        sb.Append(SpProp("dxWrapDistLeft", "0")); sb.Append(SpProp("dxWrapDistRight", "0"));
        sb.Append(SpProp("gdContentScaling", "0")); sb.Append(SpProp("gdContentAngle", "0"));
        sb.Append(SpProp("gdMaxLen", maxLen.ToString()));
        sb.Append(SpProp("wzName", EscapeRtf(fieldName)));
        // shptxt — \cf1 = black (per reference WIP format), no \s6 or \widctlpar
        // Runs 1+2 self-close. Run 3 is left open; }}}} closes it + shptxt + \shp{ + outer {
        sb.Append($@"{{\shptxt\pard\plain\ql\li0\ri0\fi0\sb0\sa0\sl240\slmult1");
        sb.Append($@"{{\cf1\f1\fs18\ulnone\ulc0 %}}{{\cf1\f0\fs20\ulnone\ulc0 [{slotId}]}}{{\cf1\f0\fs20\ulnone\ulc0 \par ");
        sb.Append("}}}}"); // closes: run3, shptxt, \shp{, outer {
    }

    /// Embed a full-page background image (PNG) as shape 0 with fBehindDocument=1.
    /// Structure verified against GhostDraft WIP reference (MCS90 0117.gd).
    private static void AppendBackgroundImageShape(
        StringBuilder sb, int pgwTw, int pghTw, byte[] pngBytes, int z)
    {
        sb.Append($@"{{\shp{{\*\shpinst\shpleft0\shptop0\shpright{pgwTw}\shpbottom{pghTw}\shpbxpage\shpbypage\shpwr3\shpwrk0\shpfblwtxt0\shpz{z}");
        sb.Append(SpProp("shapeType", "202"));
        sb.Append(SpProp("dxTextLeft", "0")); sb.Append(SpProp("dxTextRight", "0"));
        sb.Append(SpProp("dyTextTop", "0")); sb.Append(SpProp("dyTextBottom", "0"));
        sb.Append(SpProp("fBehindDocument", "1"));
        sb.Append(SpProp("fFlipH", "0")); sb.Append(SpProp("fFlipV", "0"));
        sb.Append(SpProp("fUseShapeAnchor", "0")); sb.Append(SpProp("fFilled", "0")); sb.Append(SpProp("fLine", "0"));
        sb.Append(SpProp("dxWrapDistLeft", "0")); sb.Append(SpProp("dxWrapDistRight", "0"));
        sb.Append(SpProp("gdContentScaling", "0")); sb.Append(SpProp("gdContentAngle", "0")); sb.Append(SpProp("gdMaxLen", "0"));
        // shptxt contains the image via \*\shppict — structure from MCS90 0117.gd reference
        // Outer run left open; \par }}}} closes: run, shptxt, \shp{, outer {
        sb.Append(@"{\shptxt\pard\plain\ql\li0\ri0\fi0\sb0\sa0\sl240\slmult1");
        sb.Append(@"{\cf0\f0\fs20\ulnone\ulc0 "); // open run (closed below by \par }}}} first })
        // \*\shppict group containing the PNG: {\*\shppict{{ \pict{\*\picprop{sp}{sp}} hexdata }}}
        sb.Append(@"{\*\shppict{{\pict{\*\picprop");
        sb.Append(@"{\sp{\sn fPreferRelativeResize}{{\sv 1}}}");
        sb.Append(@"{\sp{\sn fLockAspectRatio}{{\sv 1}}}");
        sb.Append("}"); // close \*\picprop
        sb.Append($@"\picwgoal{pgwTw}\pichgoal{pghTw}\picw0\pich0\picscalex100\picscaley100\emfblip");
        sb.AppendLine();
        sb.Append(Convert.ToHexString(pngBytes));
        sb.Append("}}}}"); // close: \pict group, inner { of {{, \*\shppict outer {, and the cf0 run
        // Now close: shptxt, \shp{, outer {
        sb.Append(@"\par }}}");
    }

    /// Aggregate all M,TT word tokens in a text area into a single text-box shape.
    /// Without this, each word becomes a separate overlapping shape — visual chaos.
    private static void AppendTextAreaShape(StringBuilder sb, FapTextArea area, int z)
    {
        var tokens = area.Tokens.Where(t => !string.IsNullOrWhiteSpace(t.Text)).ToList();
        if (tokens.Count == 0) return;

        var pos = area.Position;
        var (hp, boldFromFont) = GetFont(tokens[0].FontId);
        bool bold = boldFromFont || tokens[0].IsBold;
        string bld = bold ? @"\b" : @"\b0";

        // Sort by row then col and group into visual lines
        var sorted = tokens.OrderBy(t => t.Position.Row1).ThenBy(t => t.Position.Col1).ToList();
        var lineGroups = new List<List<FapTextToken>>();
        var cur = new List<FapTextToken> { sorted[0] };
        int baseRow = sorted[0].Position.Row1;
        for (int i = 1; i < sorted.Count; i++)
        {
            if (Math.Abs(sorted[i].Position.Row1 - baseRow) <= 150)
                cur.Add(sorted[i]);
            else { lineGroups.Add(cur); cur = [sorted[i]]; baseRow = sorted[i].Position.Row1; }
        }
        lineGroups.Add(cur);

        int l = (int)(pos.Col1 * FapToTwips);
        int t = (int)(pos.Row1 * FapToTwips);
        int r = Math.Max(l + 200, (int)(pos.Col2 * FapToTwips));
        int b = Math.Max(t + hp * lineGroups.Count + 80, (int)(pos.Row2 * FapToTwips));

        sb.Append($@"{{\shp{{\*\shpinst\shpleft{l}\shptop{t}\shpright{r}\shpbottom{b}\shpbxpage\shpbypage\shpwr3\shpwrk0\shpfblwtxt0\shpz{z}");
        sb.Append(SpProp("shapeType", "202"));
        sb.Append(SpProp("fBehindDocument", "0"));
        sb.Append(SpProp("fFilled", "0"));
        sb.Append(SpProp("fLine", "0"));
        sb.Append($@"{{\shptxt\pard\plain\s6\ql\sb0\sa0\li0\ri0\fi0\sl240\slmult1\widctlpar\f1\fs{hp}{bld}\i0\nosupersub\caps0\v0\shad0\outl0\strike0\expnd0\expndtw0\charscalex100");
        for (int li = 0; li < lineGroups.Count; li++)
        {
            var words = string.Join(" ", lineGroups[li].Select(tok => EscapeRtf(tok.Text)));
            sb.Append($@"{{\cf0\f1\fs{hp}{bld}\ulnone\ulc0 {words}}}");
            if (li < lineGroups.Count - 1) sb.Append(@"\line ");
        }
        sb.Append(@"\par");
        sb.Append("}}}}"); // closes \shptxt, \*\shpinst, and outer shape group
    }

    /// Emit a border line or hollow rectangle shape (for X, FAP elements).
    private static void AppendLineShape(
        StringBuilder sb,
        (int Row1, int Col1, int Row2, int Col2) pos,
        int fapLineWidth, int z)
    {
        int l = (int)(pos.Col1 * FapToTwips);
        int t = (int)(pos.Row1 * FapToTwips);
        int r = (int)(pos.Col2 * FapToTwips);
        int b = (int)(pos.Row2 * FapToTwips);

        bool isHorizLine = Math.Abs(pos.Row1 - pos.Row2) <= 10;
        bool isVertLine  = Math.Abs(pos.Col1 - pos.Col2) <= 10;
        int lineWidthTw  = Math.Max(1, (int)(fapLineWidth * FapToTwips));

        if (isHorizLine)
        {
            // Render as a filled thin rectangle spanning the line width
            b = t + lineWidthTw;
            if (r <= l) r = l + 100;
        }
        else if (isVertLine)
        {
            r = l + lineWidthTw;
            if (b <= t) b = t + 100;
        }
        // else: use coordinates as-is for a hollow rectangle

        string shapeType = (isHorizLine || isVertLine) ? "1" : "1"; // 1=rect for both
        string fFilled   = (isHorizLine || isVertLine) ? "1" : "0"; // fill lines, outline rects
        string lineColor = "0"; // black

        sb.Append($@"{{\shp{{\*\shpinst\shpleft{l}\shptop{t}\shpright{r}\shpbottom{b}\shpbxpage\shpbypage\shpwr3\shpwrk0\shpfblwtxt0\shpz{z}");
        sb.Append(SpProp("shapeType", shapeType));
        sb.Append(SpProp("fBehindDocument", "0"));
        sb.Append(SpProp("fFilled", fFilled));
        sb.Append(SpProp("fLine", isHorizLine || isVertLine ? "0" : "1"));
        if (fFilled == "1") sb.Append(SpProp("fillColor", lineColor));
        sb.Append("}}"); // closes \*\shpinst and outer shape group (no \shptxt needed for pure shapes)
    }

    // -----------------------------------------------------------------------
    // XML wrapper — matches real GhostDraft .gd document structure exactly:
    //   <Content>
    //     <document>
    //       <properties> ... </properties>
    //       <content> <rtf/> <bookmarks/> </content>
    //       <library xsi:nil="true" />
    //       <markup>
    //         <markup ID="0"> <instructions> ... </instructions> </markup>
    //       </markup>
    //     </document>
    //   </Content>
    // -----------------------------------------------------------------------

    private static string WrapInGdXml(string rtf, string formTitle, List<FieldSlot> slots)
    {
        var xml = new StringBuilder();
        xml.AppendLine(@"<?xml version=""1.0"" encoding=""utf-8""?>");
        xml.AppendLine(@"<Content Name=""GhostDraftDocument"" Version=""1.0"" ApplicationVersion=""GhostDraft 5.2.50927.0"" CompatibleVersion=""GhostDraft 3.3"">");
        xml.AppendLine(@"  <document xmlns:xsd=""http://www.w3.org/2001/XMLSchema"" xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"" xmlns=""http://schemas.korbitec.com/GhostDraft/Document/1.0"">");

        // Properties
        xml.AppendLine(@"    <properties>");
        xml.AppendLine(@"      <system xmlns=""http://schemas.korbitec.com/GhostDraft/DocumentProperties/1.0"">");
        xml.AppendLine($@"        <property name=""Title"" type=""string""><value>{EscapeXml(formTitle)}</value></property>");
        xml.AppendLine($@"        <property name=""Author"" type=""string""><value>fact-pdf-tools</value></property>");
        xml.AppendLine(@"        <property name=""Category"" type=""string""><value /></property>");
        xml.AppendLine(@"        <property name=""Company"" type=""string""><value /></property>");
        xml.AppendLine(@"        <property name=""Comments"" type=""string""><value /></property>");
        xml.AppendLine(@"        <property name=""Keywords"" type=""string""><value /></property>");
        xml.AppendLine(@"        <property name=""Manager"" type=""string""><value /></property>");
        xml.AppendLine(@"        <property name=""Subject"" type=""string""><value /></property>");
        xml.AppendLine(@"        <property name=""HyperlinkBase"" type=""string""><value /></property>");
        xml.AppendLine(@"        <property name=""WordTemplate"" type=""string""><value /></property>");
        xml.AppendLine(@"      </system>");
        xml.AppendLine(@"      <custom xmlns=""http://schemas.korbitec.com/GhostDraft/DocumentProperties/1.0"">");
        xml.AppendLine($@"        <property name=""Created"" type=""string""><value>{DateTime.UtcNow:M/d/yyyy h:mm:ss tt}</value></property>");
        xml.AppendLine(@"        <property name=""Creator"" type=""string""><value>fact-pdf-tools</value></property>");
        xml.AppendLine(@"      </custom>");
        xml.AppendLine(@"    </properties>");

        // Content (RTF + bookmarks)
        xml.AppendLine(@"    <content>");
        xml.Append(@"      <rtf>");
        xml.Append(EscapeXml(rtf));
        xml.AppendLine(@"</rtf>");
        xml.AppendLine(@"      <bookmarks>");
        xml.AppendLine(@"        <bookmark name=""Save"" mimetype=""Text/Plain"" />");
        xml.AppendLine(@"      </bookmarks>");
        xml.AppendLine(@"    </content>");

        // Library (nil - no linked library)
        xml.AppendLine(@"    <library xsi:nil=""true"" />");

        // Markup section with fill point bindings
        xml.AppendLine(@"    <markup>");
        xml.AppendLine(@"      <markup ID=""0"" descriptionSource=""ParsedUserText"" xmlns=""http://schemas.korbitec.com/GhostDraft/MarkupModel/1.0"">");
        xml.AppendLine(@"        <instructions>");

        foreach (var slot in slots)
        {
            var b = slot.Binding;
            xml.AppendLine($@"          <instruction xsi:type=""fillPointType"" ID=""{slot.Id}"" descriptionSource=""ParsedUserText"">");
            string desc = b?.Description ?? $"{NormalizeFieldName(slot.RawName)} (unbound)";
            xml.AppendLine($@"            <description>{EscapeXml(desc)}</description>");

            if (b != null)
            {
                xml.AppendLine(@"            <path conceptLibrary=""Model Library"">");
                xml.AppendLine($@"              <rootNode>{EscapeXml(b.RootNode)}</rootNode>");
                xml.AppendLine($@"              <rootguid>{b.RootGuid}</rootguid>");
                xml.AppendLine(@"              <pathNodes>");
                foreach (var node in b.PathNodes)
                    xml.AppendLine($@"                <node name=""{EscapeXml(node.Name)}"" guid=""{node.Guid}"" />");
                xml.AppendLine(@"              </pathNodes>");
                xml.AppendLine(@"            </path>");
            }
            else
            {
                // Unbound — no concept mapping yet; user can assign in GhostDraft Designer
                xml.AppendLine(@"            <path xsi:nil=""true"" />");
            }

            xml.AppendLine(@"            <adornmentPath conceptLibrary="""" />");
            xml.AppendLine(@"          </instruction>");
        }

        xml.AppendLine(@"        </instructions>");
        xml.AppendLine(@"      </markup>");
        xml.AppendLine(@"    </markup>");
        xml.AppendLine(@"  </document>");
        xml.AppendLine(@"</Content>");
        return xml.ToString();
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    // RTF title used for formTitle only — don't escape backslash in RTF body (caller handles that)
    private static string EscapeRtf(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return text.Replace(@"\", @"\\").Replace("{", @"\{").Replace("}", @"\}");
    }

    private static string EscapeXml(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
    }
}