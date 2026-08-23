using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using FapPdfTools.Server.Configuration;

namespace FapPdfTools.Server.Infrastructure;

// ---------------------------------------------------------------------------
// Domain records (adapted from MoE.Commercial.McpServer)
// ---------------------------------------------------------------------------

public record FormDatSection(string FileName, string SectionType, string SectionMetadata);

public record FormDatEntry(string FormKey, string FileName, string Metadata, string RawLine)
{
    public IReadOnlyList<FormDatSection> Sections { get; init; } = [];
}

public record FapField(
    string Name, int Length,
    (int Row1, int Col1, int Row2, int Col2) Position,
    (int FontId, int S1, int S2, int S3) FontAttributes,
    int LineNumber, int PageIndex = 0);

public record FapStaticText(
    string Text, int Length,
    (int Row1, int Col1, int Row2, int Col2) Position,
    (int FontId, int S1, int S2, int S3) FontAttributes,
    int LineNumber, int PageIndex = 0);

/// <param name="Source">
/// Which record produced this line. "X" is a top-level X, record and draws as a real
/// rectangle. "MPX" is an M,PX / M,X record nested in a text area and does NOT: Documaker
/// draws only its horizontal edges (see the renderer for the measured rule).
/// </param>
public record FapLine(
    (int Row1, int Col1, int Row2, int Col2) Position,
    int Width, int Style, int LineNumber, int PageIndex = 0, string Source = "X");

public record FapTextArea(
    (int Row1, int Col1, int Row2, int Col2) Position,
    IReadOnlyList<FapTextToken> Tokens,
    int LineNumber, int PageIndex = 0);

// A single pre-positioned word from an M,TT record. The FAP fully lays out every
// word, so each token carries its own absolute position and font.
// IsFieldPlaceholder: token is an "X" placeholder for an A,T1 field reference;
// it marks where the field value appears inline and should NOT be rendered as text.
public record FapTextToken(
    string Text, bool IsBold, int FontId,
    (int Row1, int Col1, int Row2, int Col2) Position,
    bool IsFieldPlaceholder = false);

public record FapParseResult(
    string FileName, int PageCount,
    IReadOnlyList<FapField> Fields,
    IReadOnlyList<FapStaticText> StaticTexts,
    IReadOnlyList<FapLine> Lines,
    IReadOnlyList<FapTextArea> TextAreas)
{
    /// <summary>Per-page dimensions parsed from each H-line. Index = page number (0-based).</summary>
    public IReadOnlyList<FapPageInfo> PageInfos { get; init; } = [];

    /// <summary>
    /// Inline field positions discovered from A,T1 anchors in M,TT text blocks.
    /// Key = field name (case-insensitive). Value = (Row1, Col1, Row2, Col2) of the
    /// "X" placeholder M,TT token that immediately precedes the A,T1 entry.
    /// These positions should be preferred over the F, field's explicit position when
    /// they are available, because the A,T1 anchor is the true visual position.
    /// </summary>
    public IReadOnlyDictionary<string, (int Row1, int Col1, int Row2, int Col2)> InlineFieldPositions { get; init; }
        = new Dictionary<string, (int, int, int, int)>(StringComparer.OrdinalIgnoreCase);
}

public record DdtFieldRule(
    string FieldName, string Method, string Source,
    IReadOnlyList<string> DalFunctions,
    IReadOnlyList<string> TablesReferenced,
    string Format, int Occurrence, string ConditionGroup, int LineNumber);

public record DdtParseResult(
    string FileName,
    IReadOnlyList<DdtFieldRule> FieldRules,
    IReadOnlyList<string> DalScriptsRequired,
    IReadOnlyList<string> TablesReferenced);

// ---------------------------------------------------------------------------
// Service
// ---------------------------------------------------------------------------

public class FormFileClient
{
    private readonly FormFileOptions _options;
    private readonly ILogger<FormFileClient> _logger;

    public FormFileClient(IOptions<FormFileOptions> options, ILogger<FormFileClient> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    // -----------------------------------------------------------------------
    // FORM.DAT catalog (new for UI)
    // -----------------------------------------------------------------------

    /// <summary>List all FORM.DAT entries, optionally filtered by form number prefix.</summary>
    public async Task<IReadOnlyList<FormDatEntry>> ListAllFormsAsync(
        string? searchPrefix = null, CancellationToken ct = default)
    {
        var lines = await ReadLinesAsync(_options.FormDatPath, ct);
        if (lines == null) return [];

        var normSearch = searchPrefix?.Replace(" ", "").ToUpperInvariant();
        var results = new List<FormDatEntry>();

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("/*")) continue;
            var parts = line.Split(';');
            if (parts.Length < 8) continue;

            var rawKey = parts[3];
            var keyNoSpaces = rawKey.Replace(" ", "").ToUpperInvariant();
            var fileAndMeta = parts[7].Trim().TrimEnd(';');
            var sections = ParseFormDatSections(fileAndMeta);

            foreach (var section in sections)
            {
                var sectionNameNoSpaces = section.FileName.Replace(" ", "").ToUpperInvariant();

                if (normSearch != null &&
                    !keyNoSpaces.Contains(normSearch, StringComparison.Ordinal) &&
                    !sectionNameNoSpaces.Contains(normSearch, StringComparison.Ordinal))
                    continue;

                results.Add(new FormDatEntry(rawKey.Trim(), section.FileName, section.SectionType, line)
                {
                    Sections = sections
                });
            }
        }

        return results;
    }

    // -----------------------------------------------------------------------
    // FORM.DAT lookup (exact form + edition)
    // -----------------------------------------------------------------------

    public async Task<IReadOnlyList<FormDatEntry>> ResolveFormFileNameAsync(
        string formNumber, string editionDate, CancellationToken ct = default)
    {
        var normForm = formNumber.Replace(" ", "").ToUpperInvariant();
        var normEdition = editionDate.Replace(" ", "").ToUpperInvariant();
        var lines = await ReadLinesAsync(_options.FormDatPath, ct);
        if (lines == null) return [];

        var matches = new List<FormDatEntry>();
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("/*")) continue;
            var parts = line.Split(';');
            if (parts.Length < 8) continue;

            var keyNoSpaces = parts[3].Replace(" ", "").ToUpperInvariant();
            if (!keyNoSpaces.StartsWith(normForm, StringComparison.Ordinal)) continue;
            if (!keyNoSpaces.EndsWith(normEdition, StringComparison.Ordinal)) continue;

            var fileAndMeta = parts[7].Trim().TrimEnd(';');
            var sections = ParseFormDatSections(fileAndMeta);
            var primary = sections[0];

            matches.Add(new FormDatEntry(parts[3].Trim(), primary.FileName, primary.SectionType, line)
            {
                Sections = sections
            });
        }
        return matches;
    }

    // -----------------------------------------------------------------------
    // FAP parser
    // -----------------------------------------------------------------------

    public async Task<FapParseResult?> ParseFapFileAsync(string fileName, CancellationToken ct = default)
    {
        var path = FindFile(_options.FormsDirectory, fileName, ".FAP");
        var lines = await ReadLinesAsync(path, ct);
        if (lines == null) return null;

        var fields = new List<FapField>();
        var staticTexts = new List<FapStaticText>();
        var xLines = new List<FapLine>();
        var textAreas = new List<FapTextArea>();
        var pageInfos = new List<FapPageInfo>();
        int pageCount = 0, lineNum = 0;
        int currentPage = -1; // 0-based page index, incremented on each H-line

        (int Row1, int Col1, int Row2, int Col2)? currentTextAreaPos = null;
        int currentTextAreaLine = 0;
        var currentTokens = new List<FapTextToken>();
        var inlineFieldPositions = new Dictionary<string, (int Row1, int Col1, int Row2, int Col2)>(StringComparer.OrdinalIgnoreCase);
        // After a named A,T1 anchor, subsequent whitespace-only M,TT tokens are the blank
        // space Documaker allocated for the field value. We extend the field's Col2 through
        // them so the PDF form field widget fills the correct visual space.
        string? inlineContinuationField = null;

        void FlushTextArea()
        {
            if (currentTextAreaPos == null) return;
            if (currentTokens.Count > 0)
                textAreas.Add(new FapTextArea(currentTextAreaPos.Value, currentTokens.ToList(), currentTextAreaLine, Math.Max(0, currentPage)));
            currentTextAreaPos = null;
            currentTokens.Clear();
            inlineContinuationField = null;
        }

        foreach (var line in lines)
        {
            lineNum++;
            var trimmed = line.Trim();

            if (trimmed.StartsWith("H,", StringComparison.OrdinalIgnoreCase))
            {
                pageCount++;
                currentPage++;
                var pi = ParseHLineText(trimmed);
                if (pi != null) pageInfos.Add(pi);
                continue;
            }
            int pg = Math.Max(0, currentPage);
            if (trimmed.StartsWith("F,", StringComparison.OrdinalIgnoreCase)) { var f = ParseFapFLine(trimmed, lineNum); if (f != null) fields.Add(f with { PageIndex = pg }); continue; }
            if (trimmed.StartsWith("T,", StringComparison.OrdinalIgnoreCase)) { var t = ParseFapTLine(trimmed, lineNum); if (t != null) staticTexts.Add(t with { PageIndex = pg }); continue; }
            if (trimmed.StartsWith("X,", StringComparison.OrdinalIgnoreCase)) { var x = ParseFapXLine(trimmed, lineNum); if (x != null) xLines.Add(x with { PageIndex = pg }); continue; }
            // M,PX / M,X are line & rectangle records nested inside a text area. They were
            // being dropped entirely, which is why forms whose rules come from a text area
            // rather than a top-level X, record rendered with NO horizontal rules at all --
            // invisible to the text-only Tier 1 and Tier 2 gates. 315 of 4210 forms use them.
            if (trimmed.StartsWith("M,PX,", StringComparison.OrdinalIgnoreCase))
            { var mx = ParseFapXLine(trimmed, lineNum, 5); if (mx != null) xLines.Add(mx with { PageIndex = pg, Source = "MPX" }); continue; }
            if (trimmed.StartsWith("M,X,", StringComparison.OrdinalIgnoreCase))
            { var mx = ParseFapXLine(trimmed, lineNum, 4); if (mx != null) xLines.Add(mx with { PageIndex = pg, Source = "MPX" }); continue; }
            if (trimmed.StartsWith("M,H,", StringComparison.OrdinalIgnoreCase)) { FlushTextArea(); var mh = ParseMHLine(trimmed); if (mh != null) { currentTextAreaPos = mh.Value; currentTextAreaLine = lineNum; } continue; }
            if (trimmed.StartsWith("M,TT,", StringComparison.OrdinalIgnoreCase))
            {
                var tk = ParseMTTLine(trimmed);
                if (tk != null)
                {
                    // If we're in field-continuation mode and this token is blank/whitespace,
                    // it represents the space Documaker allocated for the field value.
                    // Extend the field's recorded Col2 and hide the token from rendering.
                    if (inlineContinuationField != null && string.IsNullOrWhiteSpace(tk.Text))
                    {
                        var cur = inlineFieldPositions[inlineContinuationField];
                        inlineFieldPositions[inlineContinuationField] = (cur.Row1, cur.Col1, cur.Row2, tk.Position.Col2);
                        tk = tk with { IsFieldPlaceholder = true };
                    }
                    else
                    {
                        inlineContinuationField = null; // real text encountered — stop extending
                    }
                    currentTokens.Add(tk);
                }
                continue;
            }
            if (trimmed.StartsWith("M,P,", StringComparison.OrdinalIgnoreCase)) { continue; }
            if (trimmed.StartsWith("M,E", StringComparison.OrdinalIgnoreCase)) { FlushTextArea(); continue; }

            // A,T1,"FIELDNAME",... lines mark the inline position of a form field value.
            // The M,TT token immediately preceding it is the visual placeholder (typically
            // a single "X" character) — mark it as a field placeholder so it is NOT
            // rendered as static text, and record its position as the field's inline position.
            if (trimmed.StartsWith("A,T1,", StringComparison.OrdinalIgnoreCase))
            {
                var fieldName = ParseAT1FieldName(trimmed);
                if (!string.IsNullOrEmpty(fieldName) && currentTokens.Count > 0)
                {
                    var lastIdx = currentTokens.Count - 1;
                    var lastToken = currentTokens[lastIdx];
                    // Record the inline position (first occurrence wins per field name)
                    if (!inlineFieldPositions.ContainsKey(fieldName))
                        inlineFieldPositions[fieldName] = lastToken.Position;
                    // Mark the token as a field placeholder so it won't render as "X" text
                    currentTokens[lastIdx] = lastToken with { IsFieldPlaceholder = true };
                    // Enter continuation mode to extend Col2 through subsequent blank tokens
                    inlineContinuationField = fieldName;
                }
                // Space-only A,T1 entries don't reset continuation — they're line-break hints
                // that may appear between the named A,T1 and its blank continuation tokens.
                continue;
            }
        }
        FlushTextArea();
        return new FapParseResult(fileName, pageCount, fields, staticTexts, xLines, textAreas)
        {
            PageInfos = pageInfos,
            InlineFieldPositions = inlineFieldPositions,
        };
    }

    // -----------------------------------------------------------------------
    // DDT parser
    // -----------------------------------------------------------------------

    public async Task<DdtParseResult?> ParseDdtFileAsync(string fileName, CancellationToken ct = default)
    {
        var path = FindFile(_options.DdtDirectory, fileName, ".DDT");
        var lines = await ReadLinesAsync(path, ct);
        if (lines == null) return null;

        var rules = new List<DdtFieldRule>();
        bool inOverride = false;
        int lineNum = 0;

        foreach (var rawLine in lines)
        {
            lineNum++;
            var line = rawLine.Trim();
            if (line.StartsWith("<Image Field Rules Override>", StringComparison.OrdinalIgnoreCase)) { inOverride = true; continue; }
            if (!inOverride || string.IsNullOrWhiteSpace(line) || line.StartsWith("/*") || !line.StartsWith(';')) continue;

            var rule = ParseDdtRuleLine(line, lineNum);
            if (rule != null) rules.Add(rule);
        }

        var dalRequired = rules.Where(r => r.Method.Equals("DAL", StringComparison.OrdinalIgnoreCase))
            .SelectMany(r => r.DalFunctions).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(f => f).ToList();
        var tables = rules.SelectMany(r => r.TablesReferenced).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(t => t).ToList();

        return new DdtParseResult(fileName, rules, dalRequired, tables);
    }

    // -----------------------------------------------------------------------
    // File path helpers
    // -----------------------------------------------------------------------

    public string? FindFormFilePath(string fileName, string extension) =>
        FindFile(extension.Equals(".FAP", StringComparison.OrdinalIgnoreCase)
            ? _options.FormsDirectory : _options.DdtDirectory, fileName, extension);

    /// <summary>
    /// Classifies a form as Static, Variable, or WIP using the two-signal decision tree:
    ///   1. FAP F-line count == 0  →  Static
    ///   2. DDT contains ;powtype; →  WIP
    ///   3. Otherwise              →  Variable
    /// Edge case: a single F-line whose field name is POLNUM is still treated as Static.
    /// </summary>
    public async Task<string> ClassifyFormAsync(string fileName, CancellationToken ct = default)
    {
        var fapPath = FindFile(_options.FormsDirectory, fileName, ".FAP");
        var fapLines = await ReadLinesAsync(fapPath, ct);
        if (fapLines == null) return FapPdfTools.Server.Models.FormClassification.Unknown;

        var fLines = fapLines
            .Where(l => l.TrimStart().StartsWith("F,", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (fLines.Count == 0)
            return FapPdfTools.Server.Models.FormClassification.Static;

        // Edge case: sole F-line is the policy number stamp — still Static
        if (fLines.Count == 1 && fLines[0].TrimEnd().EndsWith(",POLNUM", StringComparison.OrdinalIgnoreCase))
            return FapPdfTools.Server.Models.FormClassification.Static;

        var ddtPath = FindFile(_options.DdtDirectory, fileName, ".DDT");
        var ddtLines = await ReadLinesAsync(ddtPath, ct);
        if (ddtLines != null &&
            ddtLines.Any(l => l.Contains(";powtype;", StringComparison.OrdinalIgnoreCase)))
            return FapPdfTools.Server.Models.FormClassification.Wip;

        return FapPdfTools.Server.Models.FormClassification.Variable;
    }

    // -----------------------------------------------------------------------
    // Private parsing methods
    // -----------------------------------------------------------------------

    /// <summary>Parse an H-line string into page dimensions.</summary>
    private static FapPageInfo? ParseHLineText(string line)
    {
        try
        {
            var rest = line[2..]; // skip "H,"
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

    private static IReadOnlyList<FormDatSection> ParseFormDatSections(string fileAndMeta)
    {
        var sections = new List<FormDatSection>();
        foreach (var part in fileAndMeta.Split('/'))
        {
            var trimmed = part.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;
            var pipeIdx = trimmed.IndexOf('|');
            var fileName = pipeIdx >= 0 ? trimmed[..pipeIdx].Trim() : trimmed;
            var typeAndMeta = pipeIdx >= 0 ? trimmed[(pipeIdx + 1)..] : string.Empty;
            var angleIdx = typeAndMeta.IndexOf('<');
            string sectionType, sectionMetadata;
            if (angleIdx >= 0)
            {
                sectionType = typeAndMeta[..angleIdx].Trim();
                var closeAngle = typeAndMeta.IndexOf('>', angleIdx);
                sectionMetadata = closeAngle >= 0 ? typeAndMeta[(angleIdx + 1)..closeAngle] : typeAndMeta[(angleIdx + 1)..];
            }
            else { sectionType = typeAndMeta.Trim(); sectionMetadata = string.Empty; }
            sections.Add(new FormDatSection(fileName, sectionType, sectionMetadata));
        }
        return sections;
    }

    private static FapField? ParseFapFLine(string line, int lineNum)
    {
        try
        {
            var rest = line[2..];
            var pos = ExtractParenGroup(rest, 0);
            var pc = pos.Content.Split(',');
            var position = (int.Parse(pc[0]), int.Parse(pc[1]), int.Parse(pc[2]), int.Parse(pc[3]));
            var font = ExtractParenGroup(rest, pos.End);
            var fp = font.Content.Split(',');
            var fontAttr = (int.Parse(fp[0]), int.Parse(fp[1]), int.Parse(fp[2]), int.Parse(fp[3]));
            var after = rest[(font.End)..].TrimStart(',');
            var ci = after.IndexOf(',');
            return new FapField(after[(ci + 1)..].Trim(), int.Parse(after[..ci]), position, fontAttr, lineNum);
        }
        catch { return null; }
    }

    private static FapStaticText? ParseFapTLine(string line, int lineNum)
    {
        try
        {
            var rest = line[2..];
            var pos = ExtractParenGroup(rest, 0);
            var pc = pos.Content.Split(',');
            var position = (int.Parse(pc[0]), int.Parse(pc[1]), int.Parse(pc[2]), int.Parse(pc[3]));
            var font = ExtractParenGroup(rest, pos.End);
            var fp = font.Content.Split(',');
            var fontAttr = (int.Parse(fp[0]), int.Parse(fp[1]), int.Parse(fp[2]), int.Parse(fp[3]));
            var after = rest[(font.End)..].TrimStart(',');
            var ci = after.IndexOf(',');
            return new FapStaticText(after[(ci + 1)..].Trim(), int.Parse(after[..ci]), position, fontAttr, lineNum);
        }
        catch { return null; }
    }

    /// <summary>
    /// Parses a line/rectangle record. Used for both the top-level <c>X,</c> record and the
    /// <c>M,PX,</c> / <c>M,X,</c> records nested inside a text area, which carry an identical
    /// payload -- <c>(row1,col1,row2,col2),(w,h),width,style</c> -- and only differ in prefix.
    /// </summary>
    private static FapLine? ParseFapXLine(string line, int lineNum, int prefixLen = 2)
    {
        try
        {
            var rest = line[prefixLen..];
            var pos = ExtractParenGroup(rest, 0);
            var pc = pos.Content.Split(',');
            var position = (int.Parse(pc[0]), int.Parse(pc[1]), int.Parse(pc[2]), int.Parse(pc[3]));
            var second = ExtractParenGroup(rest, pos.End);
            var after = rest[(second.End)..].TrimStart(',');
            var parts = after.Split(',');
            return new FapLine(position, int.Parse(parts[0]), int.Parse(parts[1]), lineNum);
        }
        catch { return null; }
    }

    /// <summary>
    /// Extracts the field name from an A,T1 line, e.g. A,T1,"POLICYNUM ",... → "POLICYNUM".
    /// Returns null if the name is blank (space-only A,T1 lines are line-break hints, not field refs).
    /// </summary>
    private static string? ParseAT1FieldName(string line)
    {
        try
        {
            // Format: A,T1,"FIELDNAME",<rest>
            var rest = line[5..]; // skip "A,T1,"
            if (rest.Length > 0 && rest[0] == '"')
            {
                var endQuote = rest.IndexOf('"', 1);
                if (endQuote > 1)
                {
                    var name = rest[1..endQuote].Trim();
                    return string.IsNullOrWhiteSpace(name) ? null : name;
                }
            }
            return null;
        }
        catch { return null; }
    }

    private static (int, int, int, int)? ParseMHLine(string line)    {
        try
        {
            var pos = ExtractParenGroup(line[4..], 0);
            var pc = pos.Content.Split(',');
            return (int.Parse(pc[0]), int.Parse(pc[1]), int.Parse(pc[2]), int.Parse(pc[3]));
        }
        catch { return null; }
    }

    private static FapTextToken? ParseMTTLine(string line)
    {
        try
        {
            var rest = line[5..];
            var pos = ExtractParenGroup(rest, 0);
            var pc = pos.Content.Split(',');
            var position = (int.Parse(pc[0]), int.Parse(pc[1]), int.Parse(pc[2]), int.Parse(pc[3]));
            var font = ExtractParenGroup(rest, pos.End);
            var fontId = int.Parse(font.Content.Split(',')[0]);
            var isBold = fontId == 14110 || fontId == 14112 || fontId == 14116;
            var after = rest[(font.End)..].TrimStart(',');
            var ci = after.IndexOf(',');
            var text = after[(ci + 1)..].Trim();
            // Do NOT strip enclosing double quotes. The M,TT text field is stored bare
            // (e.g. `...,10,"Personal `), so a token that both starts and ends with a
            // quote is a DEFINED TERM whose quotes are content -- "fungi", "we", "you",
            // "wrongful acts". Stripping them silently deleted 122 quote characters
            // across the sample and was the sole cause of 9 of 10 content-gate failures.
            return new FapTextToken(text, isBold, fontId, position);
        }
        catch { return null; }
    }

    private static DdtFieldRule? ParseDdtRuleLine(string line, int lineNum)
    {
        try
        {
            var parts = line.Split(';');
            if (parts.Length < 12) return null;
            var method = parts[10].Trim();
            var fieldName = parts[6].Trim();
            var source = parts[11].Trim();
            if (method.Equals("EjectPage", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(fieldName)) return null;
            _ = int.TryParse(parts[2].Trim(), out int occurrence);

            var dalFunctions = new List<string>();
            if (method.Equals("DAL", StringComparison.OrdinalIgnoreCase))
                foreach (Match m in Regex.Matches(source, @"CALL\(""([^""]+)""\)", RegexOptions.IgnoreCase))
                    dalFunctions.Add(m.Groups[1].Value.Trim());

            return new DdtFieldRule(fieldName, method, source, dalFunctions, [],
                parts[9].Trim(), occurrence, parts[3].Trim(), lineNum);
        }
        catch { return null; }
    }

    // Documaker resources (FAP/DDT/FORM.DAT) are Windows-1252, not UTF-8: they carry
    // curly quotes (0x93/0x94) and en dashes (0x96) that UTF-8 decoding turns into
    // U+FFFD. Latin-1 is built in and correct for 0xA0-0xFF; only 0x80-0x9F differs,
    // so remap that range explicitly rather than taking a CodePages dependency.
    private static readonly char[] Cp1252HighRange =
    [
        '€', '', '‚', 'ƒ', '„', '…', '†', '‡',
        'ˆ', '‰', 'Š', '‹', 'Œ', '', 'Ž', '',
        '', '‘', '’', '“', '”', '•', '–', '—',
        '˜', '™', 'š', '›', 'œ', '', 'ž', 'Ÿ',
    ];

    private static async Task<string[]?> ReadLinesAsync(string? path, CancellationToken ct)
    {
        if (path == null || !File.Exists(path)) return null;

        var bytes = await File.ReadAllBytesAsync(path, ct);
        var chars = new char[bytes.Length];
        for (int i = 0; i < bytes.Length; i++)
        {
            byte b = bytes[i];
            chars[i] = b is >= 0x80 and <= 0x9F ? Cp1252HighRange[b - 0x80] : (char)b;
        }
        return new string(chars).Split(["\r\n", "\n", "\r"], StringSplitOptions.None);
    }

    private static string? FindFile(string directory, string baseName, string extension)
    {
        if (!Directory.Exists(directory)) return null;
        var ext = extension.StartsWith('.') ? extension : "." + extension;
        var exact = Path.Combine(directory, baseName + ext);
        if (File.Exists(exact)) return exact;
        return Directory.EnumerateFiles(directory, "*" + ext)
            .FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).Equals(baseName, StringComparison.OrdinalIgnoreCase));
    }

    private static (string Content, int End) ExtractParenGroup(string s, int startIdx)
    {
        var open = s.IndexOf('(', startIdx);
        var close = s.IndexOf(')', open);
        return (s[(open + 1)..close], close + 1);
    }
}
