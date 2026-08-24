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

    // =======================================================================
    // GLOBAL CONCEPT MODEL
    // A single canonical catalog of GhostDraft Model Library concepts, keyed by
    // a stable concept key (e.g. "Policy.EffectiveDate"). Every form's fields
    // map INTO this model via the per-form field maps below. Add a concept here
    // once, then reference it by key from any number of forms.
    // =======================================================================
    private static readonly Dictionary<string, ConceptBinding> ConceptModel =
        new(StringComparer.OrdinalIgnoreCase)
    {
        // --- Policy ---
        ["Policy.Number"] = new("Policy Number of Policy",
            "Policy", "ee97488b-9ec8-4f0a-88db-dd6a000f5f22",
            [new("Policy Number", "16ed3972-4199-4419-93de-0ff41c640622")]),
        ["Policy.EffectiveDate"] = new("Policy Effective Date of Policy",
            "Policy", "ee97488b-9ec8-4f0a-88db-dd6a000f5f22",
            [new("Policy Effective Date", "db668a25-ce3b-4d08-a1f8-db911b0a2e46")]),
        ["Policy.ExpirationDate"] = new("Policy Expiration Date of Policy",
            "Policy", "ee97488b-9ec8-4f0a-88db-dd6a000f5f22",
            [new("Policy Expiration Date", "bb431649-c0cc-48fb-a417-63849b55ad7b")]),
        // --- Insured ---
        ["Insured.FullName"] = new("Full Name of Insured",
            "Insured", "aade6b84-2f42-4d5a-8eb9-870e8a063187",
            [new("Full Name", "32280915-3ab9-49e4-9c45-60923542e048")]),
        ["Insured.AddressLine1"] = new("Address Line 1 of Insured",
            "Insured", "aade6b84-2f42-4d5a-8eb9-870e8a063187",
            [new("Address Line 1", "7a25c42e-ec66-49ee-b098-37e31fb3a67f")]),
        ["Insured.AddressLine2"] = new("Address Line 2 of Insured",
            "Insured", "aade6b84-2f42-4d5a-8eb9-870e8a063187",
            [new("Address Line 2", "7644bfa5-c8ee-4df0-9a7a-5ddb7601cc77")]),
        ["Insured.City"] = new("City of Insured",
            "Insured", "aade6b84-2f42-4d5a-8eb9-870e8a063187",
            [new("City", "3204e969-b7ef-4ee7-a496-f345fb51ee3a")]),
        ["Insured.State"] = new("State of Insured",
            "Insured", "aade6b84-2f42-4d5a-8eb9-870e8a063187",
            [new("State", "2db53ea0-31c2-4d87-b1c2-52b373d10776")]),
        ["Insured.Zip"] = new("Zip of Insured",
            "Insured", "aade6b84-2f42-4d5a-8eb9-870e8a063187",
            [new("Zip", "3d327169-5851-424f-835f-f65dbe9d0f1b")]),
        // --- Agent ---
        ["Agent.FullName"] = new("Full Name of Agent",
            "Agent", "fd9eebc5-e398-4c3a-8e10-d78c46900de0",
            [new("Full Name", "9b7a6e43-7c6a-44bc-8a2b-aef9c5d03b55")]),
        // --- MCS-90 (root: MOECAPolicyLevelCoverages) ---
        ["Mcs90.MotorCarrierName"] = new("MCS-90 Motor Carrier Name",
            "MOECAPolicyLevelCoverages", "b58de484-20b2-4d56-8752-0455438c8c74",
            [new("MCS-90", "80678beb-986e-4a7c-89aa-cf85d062e2d3"),
             new("MotorCarrierName", "007526b3-8557-4db5-a62f-9138338180f7")]),
        ["Mcs90.MotorCarrierState"] = new("MCS-90 Motor Carrier State",
            "MOECAPolicyLevelCoverages", "b58de484-20b2-4d56-8752-0455438c8c74",
            [new("MCS-90", "80678beb-986e-4a7c-89aa-cf85d062e2d3"),
             new("MotorCarrierStateOrProvince", "f2dfd6db-b34c-40b6-85fe-44cdcdcd5992")]),
        ["Mcs90.UsDot"] = new("MCS-90 US DOT Number",
            "MOECAPolicyLevelCoverages", "b58de484-20b2-4d56-8752-0455438c8c74",
            [new("MCS-90", "80678beb-986e-4a7c-89aa-cf85d062e2d3"),
             new("USDOTNumber", "74b96b32-edd9-47c7-b61a-ce6b0cdd8588")]),
        ["Mcs90.IssuedDay"] = new("MCS-90 Issued Day",
            "MOECAPolicyLevelCoverages", "b58de484-20b2-4d56-8752-0455438c8c74",
            [new("MCS-90", "80678beb-986e-4a7c-89aa-cf85d062e2d3"),
             new("Issued Day", "60affef8-63e0-4e40-bc45-2baa5226a819")]),
        ["Mcs90.IssuedMonth"] = new("MCS-90 Issued Month",
            "MOECAPolicyLevelCoverages", "b58de484-20b2-4d56-8752-0455438c8c74",
            [new("MCS-90", "80678beb-986e-4a7c-89aa-cf85d062e2d3"),
             new("Issued Month", "ece33aca-b375-4fce-aba1-b55328915609")]),
        ["Mcs90.IssuedYear"] = new("MCS-90 Issued Year",
            "MOECAPolicyLevelCoverages", "b58de484-20b2-4d56-8752-0455438c8c74",
            [new("MCS-90", "80678beb-986e-4a7c-89aa-cf85d062e2d3"),
             new("Issued Year", "effde952-23ce-4eac-89fd-3227c1e01f29")]),
        ["Mcs90.AmendingPolicyNumber"] = new("MCS-90 Amending Policy Number",
            "MOECAPolicyLevelCoverages", "b58de484-20b2-4d56-8752-0455438c8c74",
            [new("MCS-90", "80678beb-986e-4a7c-89aa-cf85d062e2d3"),
             new("AmendingPolicyNumber", "b2a5a1ee-b27b-4134-a9e7-fc0ce8266436")]),
        ["Mcs90.EffectiveDate"] = new("MCS-90 Effective Date",
            "MOECAPolicyLevelCoverages", "b58de484-20b2-4d56-8752-0455438c8c74",
            [new("MCS-90", "80678beb-986e-4a7c-89aa-cf85d062e2d3"),
             new("EffectiveDate", "8962ca0e-748c-442c-83d7-dffe13685d1c")]),
        ["Mcs90.InExcessOf"] = new("MCS-90 In Excess Of",
            "MOECAPolicyLevelCoverages", "b58de484-20b2-4d56-8752-0455438c8c74",
            [new("MCS-90", "80678beb-986e-4a7c-89aa-cf85d062e2d3"),
             new("InExcessOf", "077a2460-99f2-48a8-910a-1ce91c40bb50")]),
    };

    // =======================================================================
    // PER-FORM FIELD MAPS  (FAP field name -> concept key in ConceptModel)
    // Each form maps its own field names into the global model, so the same
    // field name can mean different concepts on different forms — e.g. EFFDATE
    // is the policy effective date on a quote page but the endorsement
    // effective date on MCS-90. Form maps are matched by form-number prefix so
    // a family of editions shares one map; DefaultFieldMap is the fallback for
    // generic field names no form-specific map claims.
    // =======================================================================

    private static readonly Dictionary<string, string> DefaultFieldMap =
        new(StringComparer.OrdinalIgnoreCase)
    {
        ["POLNUM"]     = "Policy.Number",
        ["POLICYNO"]   = "Policy.Number",
        ["POLEFF"]     = "Policy.EffectiveDate",
        ["POLEXPDATE"] = "Policy.ExpirationDate",
        ["POLEXPIRE"]  = "Policy.ExpirationDate",
        ["INSNAME"]    = "Insured.FullName",
        ["NAMEDINS"]   = "Insured.FullName",
        ["INSADDR1"]   = "Insured.AddressLine1",
        ["INSADDR2"]   = "Insured.AddressLine2",
        ["INSCITY"]    = "Insured.City",
        ["INSSTATE"]   = "Insured.State",
        ["INSZIP"]     = "Insured.Zip",
        ["AGENTNAME"]  = "Agent.FullName",
        // Common header fields shared across the quote-form families (QTE_/QFRM_/QCPP_/QBOP_).
        // Form-specific maps still win first (e.g. MCS-90's EFFDATE = endorsement date), so these
        // only apply where a form hasn't claimed the name.
        ["INSURED NAME1"] = "Insured.FullName",
        ["INSURED NAME2"] = "Insured.FullName",
        ["AGENT NAME"]    = "Agent.FullName",
        ["EFFDATE"]       = "Policy.EffectiveDate",
        ["EXPDATE"]       = "Policy.ExpirationDate",
        ["POLICYNBR"]     = "Policy.Number",
        ["POLICYNUM"]     = "Policy.Number",
        ["POLICYNUMBER"]  = "Policy.Number",
    };

    // Form-number prefix -> (FAP field name -> concept key). Most specific
    // prefixes should come first.
    private static readonly (string FormPrefix, Dictionary<string, string> Fields)[] FormFieldMaps =
    {
        // Quote cover page. Unbound for now (no known concept): TITLE,
        // INSURED NAME2, AGENT PHONE, PROPOSAL PERIOD, MT_DISCL.
        ("QTE_COVER", new(StringComparer.OrdinalIgnoreCase)
        {
            ["INSURED NAME1"] = "Insured.FullName",
            ["AGENT NAME"]    = "Agent.FullName",
            ["EFFDATE"]       = "Policy.EffectiveDate",
            ["EXPDATE"]       = "Policy.ExpirationDate",
        }),
        // MCS-90 financial-responsibility endorsement
        ("MCS90", new(StringComparer.OrdinalIgnoreCase)
        {
            ["ISSUEDTO"]  = "Mcs90.MotorCarrierName",
            ["OF"]        = "Mcs90.MotorCarrierState",
            ["USDOT"]     = "Mcs90.UsDot",
            ["DAY"]       = "Mcs90.IssuedDay",
            ["MONTH"]     = "Mcs90.IssuedMonth",
            ["YR"]        = "Mcs90.IssuedYear",
            ["POLICYNUM"] = "Mcs90.AmendingPolicyNumber",
            ["EFFDATE"]   = "Mcs90.EffectiveDate",
            ["LIMIT"]     = "Mcs90.InExcessOf",
        }),
    };

    // Strip duplicate-suffix like " #002" that FAP appends for repeated field names
    private static string NormalizeFieldName(string name)
        => name.Contains(" #") ? name[..name.IndexOf(" #")].Trim() : name.Trim();

    /// <summary>
    /// Authoritative (curated) concept binding for a field, or null if unbound. Exposes the
    /// converter's own mapping so gap-analysis tooling can distinguish a verified binding from a
    /// mere Model Library name-match candidate. Returns the human concept description.
    /// </summary>
    public static string? DescribeBinding(string formNumber, string fieldName)
        => LookupBinding(formNumber, fieldName)?.Description;

    // Resolve a field to a concept: the form-specific map first (matched by
    // form-number prefix), then the generic DefaultFieldMap, else unbound (null).
    private static ConceptBinding? LookupBinding(string formNumber, string rawFieldName)
    {
        var name = NormalizeFieldName(rawFieldName);

        foreach (var (prefix, fields) in FormFieldMaps)
        {
            if (!formNumber.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (TryResolve(fields, name, out var formBinding)) return formBinding;
        }
        if (TryResolve(DefaultFieldMap, name, out var generic)) return generic;

        // Fall back to the project-owned Quote concept model (premium/limit/deductible/coverage/
        // schedule fields the CA-centric Model Library doesn't have). Requires the extended concept
        // library — generate it with `demo make-concept-library`.
        var attr = ProjectConcepts.AttributeFor(name);
        if (attr != null)
            return new ConceptBinding($"{attr} of {ProjectConcepts.Root}", ProjectConcepts.Root,
                ProjectConcepts.RootGuid, new[] { new NodeRef(attr, ProjectConcepts.AttrGuid(attr)) });
        return null;
    }

    // Look a field name up in one field map (exact match, then prefix match for
    // FAP's numbered duplicates like INSNAME001) and resolve it to a concept.
    private static bool TryResolve(Dictionary<string, string> fieldMap, string name, out ConceptBinding? binding)
    {
        binding = null;
        if (fieldMap.TryGetValue(name, out var key) && ConceptModel.TryGetValue(key, out var exact))
        {
            binding = exact;
            return true;
        }
        foreach (var kv in fieldMap)
            if (name.StartsWith(kv.Key, StringComparison.OrdinalIgnoreCase)
                && ConceptModel.TryGetValue(kv.Value, out var pre))
            {
                binding = pre;
                return true;
            }
        return false;
    }

    // -----------------------------------------------------------------------
    // Public entry point
    // -----------------------------------------------------------------------

    private record TextItem(int Row, int Col, int ColRight, int HalfPts, bool Bold, bool Italic, int FontIdx, int WidthTw, string Text, bool IsField,
        bool Underline = false);
    private record FieldSlot(int Id, string RawName, ConceptBinding? Binding);

    // Legacy guess used only when the FXR font library is unavailable.
    private static (int HalfPts, bool Bold) GetFont(int fontId)
        => FontMap.TryGetValue(fontId, out var f) ? f : (20, false);

    // Deterministic font resolution from the Documaker FXR: exact point size,
    // bold, italic and typeface (\f0 Times / \f1 Arial). Falls back to the guess
    // map only when the FXR is missing or the id is unknown.
    private static (int HalfPts, bool Bold, bool Italic, int FontIdx) ResolveFont(int fontId, FxrFontLibrary? fonts)
    {
        var f = fonts?.Resolve(fontId);
        if (f != null && f.PointSize > 0)
        {
            int hp = Math.Max(8, (int)Math.Round(f.PointSize * 2));
            int idx = f.Typeface.IndexOf("Times", StringComparison.OrdinalIgnoreCase) >= 0 ? 0 : 1;
            return (hp, f.Bold, f.Italic, idx);
        }
        var (ghp, gbold) = GetFont(fontId);
        return (ghp, gbold, false, 1);
    }

    public static string Generate(FapParseResult fap, string formTitle, string formNumber, bool isWip = false, byte[]? backgroundEmf = null, FxrFontLibrary? fonts = null)
    {
        // Build ordered list of fields with their %[N] IDs and concept bindings
        var slots = new List<FieldSlot>();
        int idx = 1;
        foreach (var f in fap.Fields.OrderBy(x => x.PageIndex).ThenBy(x => x.Position.Row1))
            slots.Add(new FieldSlot(idx++, f.Name, LookupBinding(formNumber, f.Name)));

        // WIP forms use absolute-positioned shapes (over a rendered page-image
        // background). Everything else uses the deterministic table builder: exact
        // fonts from the FXR, and columnar rows emitted as RTF tables (\trowd) whose
        // cell boundaries come straight from the FAP column positions — matching how
        // GhostDraft itself lays out columnar documents. Prose stays as paragraphs.
        var rtf = isWip ? BuildWipRtf(fap, slots, backgroundEmf) : BuildRtf(fap, slots, formTitle, fonts);
        return WrapInGdXml(rtf, formTitle, slots);
    }

    // -----------------------------------------------------------------------
    // RTF builder
    // -----------------------------------------------------------------------

    private static string BuildRtf(FapParseResult fap, List<FieldSlot> slots, string formTitle, FxrFontLibrary? fonts)
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
        const int ml = 1080, mt = 720, mb = 720;

        // Right margin is derived from the FAP's own content, not a fixed guess.
        // Documaker right-justifies value fields to the right edge (col2) of their
        // boxes; that shared edge is the true right margin. Align the usable area
        // (and therefore the right-aligned value column) to the rightmost content
        // edge on the page so values land exactly where the legacy form puts them.
        int rightZoneColG = (int)(pi.PageWidth * 0.5);
        int contentRightCol = 0;
        foreach (var t in fap.StaticTexts)
            if (t.Position.Col1 > rightZoneColG) contentRightCol = Math.Max(contentRightCol, t.Position.Col2);
        foreach (var a in fap.TextAreas)
            foreach (var tok in a.Tokens)
                if (!string.IsNullOrWhiteSpace(tok.Text) && tok.Position.Col1 > rightZoneColG)
                    contentRightCol = Math.Max(contentRightCol, tok.Position.Col2);
        foreach (var f in fap.Fields)
            if (f.Position.Col1 > rightZoneColG) contentRightCol = Math.Max(contentRightCol, f.Position.Col2);

        int contentRightTw = contentRightCol > 0 ? TwFromMargin(contentRightCol) : (pgwTw - ml - 1080);
        int mr = Math.Max(0, pgwTw - ml - contentRightTw);

        sb.Append($@"\sectd\sbknone\pgwsxn{pgwTw}\pghsxn{pghTw}\marglsxn{ml}\margtsxn{mt}\margrsxn{mr}\margbsxn{mb}\headery1080\footery360");

        // Measure a raw string's rendered width (twips) using the FXR character-
        // advance table — used to size the value column so the widest value fits.
        int MeasureTw(int fontId, string text)
        {
            var f = fonts?.Resolve(fontId);
            int fap = f != null ? f.MeasureFap(text) : text.Length * 260;
            return (int)(fap * FapToTwips);
        }

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
                var (hp, bold, italic, fi) = ResolveFont(t.FontAttributes.FontId, fonts);
                items.Add(new TextItem(t.Position.Row1, t.Position.Col1, t.Position.Col2, hp, bold, italic, fi,
                    MeasureTw(t.FontAttributes.FontId, t.Text), EscapeRtf(t.Text), false, t.Underline));
            }

            foreach (var area in fap.TextAreas.Where(x => x.PageIndex == page))
                foreach (var tok in area.Tokens)
                {
                    if (string.IsNullOrWhiteSpace(tok.Text)) continue;
                    var (hp, bold, italic, fi) = ResolveFont(tok.FontId, fonts);
                    items.Add(new TextItem(tok.Position.Row1, tok.Position.Col1, tok.Position.Col2, hp, bold || tok.IsBold, italic, fi,
                        MeasureTw(tok.FontId, tok.Text), EscapeRtf(tok.Text), false, tok.Underline));
                }

            foreach (var f in fap.Fields.Where(x => x.PageIndex == page))
            {
                var (hp, bold, italic, fi) = ResolveFont(f.FontAttributes.FontId, fonts);
                // A fill point's rendered width is unknown; estimate ~1 char-width
                // (in twips) per max-length character, capped.
                int w = Math.Min(f.Length * (hp * 5), 4000);
                items.Add(new TextItem(f.Position.Row1, f.Position.Col1, f.Position.Col2, hp, bold, italic, fi,
                    w, $"%[{fieldIndex++}]", true));
            }

            items.Sort((a, b) => a.Row != b.Row ? a.Row.CompareTo(b.Row) : a.Col.CompareTo(b.Col));

            int usableTw = pgwTw - ml - mr;

            // A grid drawn with FAP X, rectangles (e.g. the payment-plans table) is
            // rendered as a real bordered RTF table from the rectangle geometry.
            // Text inside the grid is handled there and excluded from column
            // inference / normal flow so it doesn't get treated as prose.
            var grid = DetectGrid(fap.Lines, page);
            bool InGrid(TextItem it) => grid != null &&
                it.Row >= grid.RowB[0] && it.Row < grid.RowB[^1] &&
                it.Col >= grid.ColB[0] - 100 && it.Col < grid.ColB[^1];

            var nonGridItems = grid == null ? items : items.Where(it => !InGrid(it)).ToList();
            var columns = InferColumns(nonGridItems, contentRightCol, usableTw);

            int prevRow = -1;
            bool gridEmitted = false;
            foreach (var line in GroupIntoLines(items))
            {
                // When we reach the grid's vertical band, emit the whole grid once
                // (in document order) and skip its rows in the normal flow.
                if (grid != null && line[0].Row >= grid.RowB[0] && line[0].Row < grid.RowB[^1])
                {
                    if (!gridEmitted)
                    {
                        EmitGrid(sb, grid, items.Where(InGrid).ToList());
                        gridEmitted = true;
                    }
                    continue;
                }

                if (prevRow >= 0 && line[0].Row - prevRow > 450)
                    EmitBlankParagraph(sb);

                // A row is a table row when its items occupy 2+ inferred columns;
                // otherwise it's ordinary single-column prose.
                int occupied = columns.Count(c => line.Any(it => ColumnOf(it, columns) == c));
                if (columns.Count >= 2 && occupied >= 2)
                    EmitTableRow(sb, line, columns);
                else
                    EmitParagraph(sb, line.OrderBy(x => x.Col).ToList());

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

    // Gap (FAP units) between adjacent items that marks a real column break
    // (label → value) rather than ordinary word spacing within a phrase.
    private const int ColumnGap = 6000;

    private static int TwFromMargin(int col) => (int)Math.Max(0, (col - FapLeftMargin) * FapToTwips);

    // Split a row's items left→right into column clusters (a wide gap = a column
    // break; small gaps = words in the same phrase).
    private static int FapColFromTw(int tw) => (int)(tw / FapToTwips) + FapLeftMargin;

    // A page-global column: FAP start column, alignment, and cell right boundary
    // (twips from left margin). Every table row uses the same boundaries so columns
    // line up down the page.
    private record ColumnDef(int StartCol, char Align, int CellRightTw);

    // Infer the page column grid. A RIGHT-aligned value column is detected by items
    // sharing the page's right content edge (col2 == contentRightCol) while their
    // left edges (col1) VARY -- the signature of right-justified values ("$40" and
    // "Included..." both ending at col 19600). Everything else groups into LEFT
    // columns by clustering left edges. This separates a right-justified value
    // column from a left-aligned column that merely sits on the right (side-by-side
    // definitions, stacked form fields).
    private static List<ColumnDef> InferColumns(List<TextItem> items, int contentRightCol, int usableTw)
    {
        if (items.Count == 0) return new List<ColumnDef>();

        var rightItems = contentRightCol > 0
            ? items.Where(it => Math.Abs(it.ColRight - contentRightCol) <= 200).ToList()
            : new List<TextItem>();
        bool hasRight = rightItems.Count >= 3 &&
                        rightItems.Max(x => x.Col) - rightItems.Min(x => x.Col) > 2000;

        var leftItems = hasRight ? items.Where(it => !rightItems.Contains(it)).ToList() : items;

        // A real column break is a wide gap; smaller offsets are indentation levels
        // within one column (title / label / sub-item), handled by \li in the cell.
        const int Tol = 3500;
        var starts = new List<(int Start, char Align)>();
        foreach (var s in leftItems.Select(it => it.Col).Distinct().OrderBy(x => x))
            if (starts.Count == 0 || s - starts[^1].Start > Tol) starts.Add((s, 'l'));

        if (hasRight)
        {
            // Widen the value cell past the FXR width estimate so the widest value
            // fits GhostDraft's (wider) Arial and lands on the shared right edge.
            int widestTw = rightItems.Max(x => x.WidthTw);
            int leftTw = Math.Max(0, usableTw - (int)(widestTw * 1.3) - 120);
            starts.Add((FapColFromTw(leftTw), 'r'));
        }
        starts.Sort((a, b) => a.Start.CompareTo(b.Start));

        var cols = new List<ColumnDef>();
        for (int i = 0; i < starts.Count; i++)
        {
            int rightTw = i < starts.Count - 1 ? TwFromMargin(starts[i + 1].Start) : usableTw;
            if (cols.Count > 0 && rightTw <= cols[^1].CellRightTw) rightTw = cols[^1].CellRightTw + 120;
            cols.Add(new ColumnDef(starts[i].Start, starts[i].Align, rightTw));
        }
        return cols;
    }

    // Which column an item belongs to: the rightmost column whose start <= item.Col.
    private static ColumnDef ColumnOf(TextItem it, List<ColumnDef> cols)
    {
        ColumnDef best = cols[0];
        foreach (var c in cols)
            if (c.StartCol <= it.Col + 300) best = c;
        return best;
    }

    // Emit one text run (a static phrase, or a %[N] fill point) with its font.
    private static void EmitRun(StringBuilder sb, TextItem item)
    {
        if (item.IsField)
        {
            sb.Append($@"{{\cf0\f{item.FontIdx}\fs{item.HalfPts}\ulnone\ulc0 %}}");
            string indexPart = item.Text.Length > 1 ? item.Text[1..] : item.Text;
            sb.Append($@"{{\cf0\f{item.FontIdx}\fs{item.HalfPts}\ulnone\ulc0 {indexPart}}}");
        }
        else
        {
            string style = (item.Bold ? @"\b" : @"\b0") + (item.Italic ? @"\i" : @"\i0");
            string off = (item.Bold ? @"\b0" : "") + (item.Italic ? @"\i0" : "");
            // Underline from bit 0 of the run's A,T1 flag. Every run here used to be
            // written \ulnone unconditionally, so all 911 underlined runs across 186 forms
            // were lost on the .gd path as well as the HTML one -- and the .gd content gate
            // could not see it, because it compares characters and an underline is not a
            // character. See FORM-STUDIO-PLAN sections 38-39.
            string ul = item.Underline ? @"\ul" : @"\ulnone";
            sb.Append($@"{{{style}\cf0\f{item.FontIdx}\fs{item.HalfPts}{ul}\ulc0 {item.Text}{off}}}");
        }
    }

    // Emit all items of one cluster as space-separated runs.
    private static void EmitClusterRuns(StringBuilder sb, List<TextItem> cluster)
    {
        for (int k = 0; k < cluster.Count; k++)
        {
            if (k > 0) sb.Append($@"{{\cf0\f{cluster[k].FontIdx}\fs{cluster[k].HalfPts}\ulnone\ulc0  }}");
            EmitRun(sb, cluster[k]);
        }
    }

    // A single-column line → an ordinary left-aligned paragraph, indented to the
    // content's FAP column. (Prose, section headings, legends.)
    private static void EmitParagraph(StringBuilder sb, List<TextItem> cluster)
    {
        int li = TwFromMargin(cluster[0].Col);
        int hp = cluster.Max(x => x.HalfPts);
        sb.Append($@"\pard\plain\ql\sb0\sa0\li{li}\ri0\fi0\sl240\slmult1\nowidctlpar\f1\fs{hp}\b0\i0\nosupersub\caps0\v0\shad0\outl0\strike0\lang1033\expnd0\expndtw0\charscalex100");
        sb.Append('{');
        EmitClusterRuns(sb, cluster);
        sb.Append($"{{\n\\par }}}}");
    }

    // Constant GhostDraft table-row property string (borderless cells, matching a
    // real authored dec page). Cell right boundaries are appended per column.
    // Zero cell padding so cells align text exactly to their FAP boundaries — in
    // particular a right-aligned (\qr) value lands on the exact cell edge (= the
    // FAP col2 right margin) whether it's short ("$40") or nearly fills the cell
    // ("Included in applicable Limit of Insurance").
    private const string RowProps =
        @"\trbrdrt\brdrw10\brdrs\trbrdrl\brdrw10\brdrs\trbrdrb\brdrw10\brdrs\trbrdrr\brdrw10\brdrs\trbrdrh\brdrw10\brdrs\trbrdrv\brdrw10\brdrs\trkeep\gdtrftsWidth2\trftsWidth2\trwWidth5000\trpaddfl3\trpaddft3\trpaddfr3\trpaddfb3\trpaddt0\trpaddl0\trpaddr0\trpaddb0";
    private const string CellProps =
        @"\clvertalt\cltxlrtb\clbrdrt\brdrw0\brdrnone\clbrdrl\brdrw0\brdrnone\clbrdrb\brdrw0\brdrnone\clbrdrr\brdrw0\brdrnone\gdclftsWidth0\clftsWidth0";
    // Same, but with a visible single-line border on all four sides — for grids
    // drawn with FAP X, rectangles (the payment-plans table etc.).
    private const string BorderedCellProps =
        @"\clvertalt\cltxlrtb\clbrdrt\brdrw10\brdrs\clbrdrl\brdrw10\brdrs\clbrdrb\brdrw10\brdrs\clbrdrr\brdrw10\brdrs\gdclftsWidth0\clftsWidth0";

    // Build the \trowd row definition: left offset + a cell (props + \cellx right
    // boundary) for each column. Bordered cells draw a visible grid.
    private static string RowDef(int trleft, List<int> cellx, bool bordered = false)
    {
        var cellProps = bordered ? BorderedCellProps : CellProps;
        var sb = new StringBuilder();
        sb.Append($@"\trowd \trleft{trleft}").Append(RowProps);
        foreach (var edge in cellx)
            sb.Append(cellProps).Append($@"\cellx{edge}");
        return sb.ToString();
    }

    // ---- Grid (FAP X, rectangles) -----------------------------------------

    // A rectangle grid detected from FAP X, lines: sorted distinct row and column
    // boundaries (FAP units).
    private record GridModel(List<int> RowB, List<int> ColB);

    // Merge near-equal boundary values (rectangles rarely share exact coords).
    private static List<int> QuantizeBoundaries(IEnumerable<int> values, int tol)
    {
        var sorted = values.Distinct().OrderBy(x => x).ToList();
        var result = new List<int>();
        foreach (var v in sorted)
            if (result.Count == 0 || v - result[^1] > tol) result.Add(v);
        return result;
    }

    // Detect a bordered grid: >= 4 rectangular X, lines on the page whose shared
    // edges form at least a 2x2 boundary lattice. Returns null when there's no grid.
    private static GridModel? DetectGrid(IReadOnlyList<FapLine> lines, int page)
    {
        var rects = lines.Where(l => l.PageIndex == page
            && Math.Abs(l.Position.Row2 - l.Position.Row1) > 40
            && Math.Abs(l.Position.Col2 - l.Position.Col1) > 40).ToList();
        if (rects.Count < 4) return null;

        var rowB = QuantizeBoundaries(rects.SelectMany(r => new[] { r.Position.Row1, r.Position.Row2 }), 40);
        var colB = QuantizeBoundaries(rects.SelectMany(r => new[] { r.Position.Col1, r.Position.Col2 }), 40);
        if (rowB.Count < 2 || colB.Count < 2) return null;
        return new GridModel(rowB, colB);
    }

    // Emit the grid as a bordered RTF table, placing each text item into the cell
    // (row band x column band) whose rectangle contains its FAP position.
    private static void EmitGrid(StringBuilder sb, GridModel g, List<TextItem> items)
    {
        int trleft = TwFromMargin(g.ColB[0]);
        var cellx = new List<int>();
        for (int i = 1; i < g.ColB.Count; i++) cellx.Add(TwFromMargin(g.ColB[i]));
        string def = RowDef(trleft, cellx, bordered: true);

        for (int ri = 0; ri < g.RowB.Count - 1; ri++)
        {
            int rTop = g.RowB[ri], rBot = g.RowB[ri + 1];
            sb.Append(def);
            for (int ci = 0; ci < g.ColB.Count - 1; ci++)
            {
                int cLeft = g.ColB[ci], cRight = g.ColB[ci + 1];
                var cellItems = items
                    .Where(it => it.Row >= rTop && it.Row < rBot && it.Col >= cLeft - 60 && it.Col < cRight)
                    .OrderBy(it => it.Col).ToList();
                int hp = cellItems.Count > 0 ? cellItems.Max(x => x.HalfPts) : 20;
                sb.Append($@"\pard\plain\ql\li60\ri0\fi0\sb0\sa0\sl240\slmult1\intbl");
                sb.Append('{');
                EmitClusterRuns(sb, cellItems);
                sb.Append('}');
                sb.Append($@"{{\cf0\f0\fs{hp}\ulnone\ulc0 \cell}}");
            }
            sb.Append($@"\pard\plain\intbl{{{def}\row }}");
        }
    }

    // Emit one row as an RTF table (\trowd) over the page-global column grid: one
    // cell per column, with FIXED boundaries and \trleft = 0 on every row so columns
    // line up down the page. Left columns are \ql (label text indented within its
    // cell via \li to its FAP position); a value column is \qr to the shared right
    // edge. Empty columns still emit a cell so the grid stays aligned.
    private static void EmitTableRow(StringBuilder sb, List<TextItem> line, List<ColumnDef> columns)
    {
        // Bucket the row's items into their columns.
        var byCol = new Dictionary<ColumnDef, List<TextItem>>();
        foreach (var it in line.OrderBy(x => x.Col))
        {
            var c = ColumnOf(it, columns);
            if (!byCol.TryGetValue(c, out var lst)) byCol[c] = lst = new List<TextItem>();
            lst.Add(it);
        }

        var cellx = columns.Select(c => c.CellRightTw).ToList();
        string def = RowDef(0, cellx);
        sb.Append(def);

        int cellLeftTw = 0;
        foreach (var col in columns)
        {
            byCol.TryGetValue(col, out var cellItems);
            bool any = cellItems is { Count: > 0 };
            int hp = any ? cellItems!.Max(x => x.HalfPts) : 20;
            string a = col.Align == 'r' ? @"\qr" : @"\ql";
            // Left columns: indent text to its FAP column within the cell.
            int li = col.Align == 'l' && any
                ? Math.Max(0, TwFromMargin(cellItems![0].Col) - cellLeftTw) : 0;

            sb.Append($@"\pard\plain{a}\li{li}\ri0\fi0\sb0\sa0\sl240\slmult1\intbl");
            sb.Append('{');
            if (any) EmitClusterRuns(sb, cellItems!);
            sb.Append('}');
            sb.Append($@"{{\cf0\f0\fs{hp}\ulnone\ulc0 \cell}}");

            cellLeftTw = col.CellRightTw;
        }

        // GhostDraft repeats the row definition immediately before \row.
        sb.Append($@"\pard\plain\intbl{{{def}\row }}");
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