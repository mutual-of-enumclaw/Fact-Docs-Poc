using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using FapPdfTools.Server.Configuration;

namespace FapPdfTools.Server.Infrastructure;

/// <summary>
/// A single font definition resolved from the Documaker FXR (font cross-reference).
/// PointSize/Typeface/Bold/Italic drive PDF font selection; Widths gives the exact
/// per-character advance (in FAP units, 1/2400") that Documaker uses for layout.
/// </summary>
public record FxrFont(
    int FontId,
    string Typeface,
    float PointSize,
    bool Bold,
    bool Italic,
    int LineHeight,
    int Ascent)
{
    /// <summary>Character advance widths in FAP units, indexed by char code 0-255.</summary>
    public int[] Widths { get; init; } = new int[256];

    /// <summary>Measure a string's width in FAP units using the FXR width table.</summary>
    public int MeasureFap(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        int total = 0;
        foreach (char c in text)
        {
            int code = c;
            total += code < 256 ? Widths[code] : Widths['n']; // fallback advance
        }
        return total;
    }
}

/// <summary>
/// Loads and caches the Documaker FXR so FontIds in FAP records resolve to exact
/// typeface, point size, style, and character metrics. Mirrors how FAPW32/FNTW32
/// resolve fonts before the LPDF driver emits text.
/// </summary>
public class FxrFontLibrary
{
    private readonly FormFileOptions _options;
    private readonly ILogger<FxrFontLibrary> _logger;
    private readonly object _lock = new();
    private Dictionary<int, FxrFont>? _fonts;

    public FxrFontLibrary(IOptions<FormFileOptions> options, ILogger<FxrFontLibrary> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>Resolve a FontId to its FXR definition, or null if unknown.</summary>
    public FxrFont? Resolve(int fontId)
    {
        EnsureLoaded();
        return _fonts != null && _fonts.TryGetValue(fontId, out var f) ? f : null;
    }

    private void EnsureLoaded()
    {
        if (_fonts != null) return;
        lock (_lock)
        {
            if (_fonts != null) return;
            _fonts = Load();
        }
    }

    private Dictionary<int, FxrFont> Load()
    {
        var result = new Dictionary<int, FxrFont>();
        var path = ResolveFxrPath();
        if (path == null || !File.Exists(path))
        {
            _logger.LogWarning("FXR file not found (FxrPath={Path}); falling back to base-14 metrics", _options.FxrPath);
            return result;
        }

        // Working state for the font record currently being parsed.
        int curId = 0;
        int lineHeight = 0, ascent = 0;
        float pointSize = 0;
        string typeface = "Arial";
        bool bold = false, italic = false;
        var widths = new int[256];

        void Commit()
        {
            if (curId == 0) return;
            result[curId] = new FxrFont(curId, typeface, pointSize, bold, italic, lineHeight, ascent)
            {
                Widths = (int[])widths.Clone()
            };
        }

        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            // R,<id>,<bodyHeight>,<descent/ascent>,<lineHeight>,<file>,
            if (line.StartsWith("R,", StringComparison.Ordinal))
            {
                Commit();
                var p = line.Split(',');
                curId = ParseInt(p, 1);
                ascent = ParseInt(p, 3);
                lineHeight = ParseInt(p, 4);
                pointSize = 0; typeface = "Arial"; bold = false; italic = false;
                Array.Clear(widths);
                continue;
            }

            if (curId == 0) continue;

            // A,R1,<id>,<pointSize*100>,...,"Typeface ","Typeface ",<Display incl. Bold/Italic + PT>
            if (line.StartsWith("A,R1,", StringComparison.Ordinal))
            {
                var p = line.Split(',');
                if (ParseInt(p, 2) != curId) continue;
                pointSize = ParseInt(p, 3) / 100f;

                // First quoted token is the typeface name.
                var firstQuote = line.IndexOf('"');
                if (firstQuote >= 0)
                {
                    var endQuote = line.IndexOf('"', firstQuote + 1);
                    if (endQuote > firstQuote)
                        typeface = line[(firstQuote + 1)..endQuote].Trim();
                }

                // The trailing display string carries the human style ("Arial Bold 10 Pt").
                var display = line[(line.LastIndexOf('"') + 1)..];
                bold = display.Contains("Bold", StringComparison.OrdinalIgnoreCase);
                italic = display.Contains("Italic", StringComparison.OrdinalIgnoreCase)
                      || display.Contains("Oblique", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            // A,R2,<id>,<row 2..8>,<32 char-advance widths in FAP units>
            // Row 2 -> chars 32..63, row 3 -> 64..95, ... row 8 -> 224..255.
            if (line.StartsWith("A,R2,", StringComparison.Ordinal))
            {
                var p = line.Split(',');
                if (ParseInt(p, 2) != curId) continue;
                int row = ParseInt(p, 3);
                int baseChar = 32 + (row - 2) * 32;
                for (int i = 0; i < 32; i++)
                {
                    int code = baseChar + i;
                    if (code < 256) widths[code] = ParseInt(p, 4 + i);
                }
                continue;
            }
        }
        Commit();

        _logger.LogInformation("Loaded {Count} fonts from FXR {Path}", result.Count, path);
        return result;
    }

    private string? ResolveFxrPath()
    {
        if (!string.IsNullOrWhiteSpace(_options.FxrPath) && File.Exists(_options.FxrPath))
            return _options.FxrPath;

        // Fall back to the standard DEFLIB location relative to the forms tree.
        if (!string.IsNullOrWhiteSpace(_options.FormsDirectory))
        {
            var parent = Directory.GetParent(_options.FormsDirectory)?.FullName;
            if (parent != null)
            {
                var deflib = Path.Combine(parent, "DEFLIB");
                if (Directory.Exists(deflib))
                {
                    var fxr = Directory.EnumerateFiles(deflib, "*.FXR")
                        .OrderByDescending(f => new FileInfo(f).Length) // prefer the full table
                        .FirstOrDefault();
                    if (fxr != null) return fxr;
                }
            }
        }
        return _options.FxrPath;
    }

    private static int ParseInt(string[] parts, int index)
    {
        if (index < 0 || index >= parts.Length) return 0;
        return int.TryParse(parts[index].Trim().Trim('"').Trim(), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out int v) ? v : 0;
    }
}
