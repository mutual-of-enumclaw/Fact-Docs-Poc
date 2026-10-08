using System.Security.Cryptography;
using System.Text;

namespace FapPdfTools.Server.Infrastructure;

/// <summary>
/// A project-owned "Quote" concept model for the quote-form data that the CA-centric MoE Model
/// Library doesn't cover (premium / limit / deductible / coverage / schedule fields). This is the
/// SINGLE source of truth for both (a) the converter's bindings and (b) the emitted concept-library
/// (.gdm) additions, so their GUIDs always agree. GUIDs are derived deterministically from the
/// concept path — no randomness, reproducible across runs.
///
/// It's a flat scaffold (all "PREMIUM" fields bind to Quote.Premium, etc.) — enough to make quote
/// fields data-bindable in GhostDraft; refine into repeating coverage sections later.
/// </summary>
public static class ProjectConcepts
{
    public const string Root = "Quote";                 // the bindable domainModel root name
    public const string ConceptName = "Model Library_Quote";

    /// <summary>Bindable attributes of the Quote model: (attribute name, base type).</summary>
    public static readonly (string Attr, string Type)[] Attributes =
    {
        ("Premium",             "Currency"),
        ("Total Premium",       "Currency"),
        ("Limit",               "Currency"),
        ("Deductible",          "Currency"),
        ("Exposure",            "Text"),
        ("Coinsurance",         "Text"),
        ("Coverage Description","Text"),
        ("Coverage Type",       "Text"),
        ("Coverage Sequence",   "Text"),
        ("Insurance Line",      "Text"),
        ("Form Number",         "Text"),
        ("Form Name",           "Text"),
        ("Edition Date",        "Text"),
        ("Proposal Period",     "Text"),
        ("Agent Phone",         "Text"),
        ("Valuation",           "Text"),
        // Location / building schedule
        ("Location",            "Text"),
        ("Building",            "Text"),
        ("Cause of Loss",       "Text"),
        ("Protection Class",    "Text"),
        ("Class Code",          "Text"),
        ("Address",             "Text"),
        // Vehicle schedule
        ("Vehicle Year",        "Text"),
        ("Vehicle VIN",         "Text"),
        ("Vehicle Make Model",  "Text"),
        ("Vehicle State",       "Text"),
        ("Vehicle Number",      "Text"),
        ("Vehicle Premium",     "Currency"),
        ("Vehicle Description",  "Text"),
    };

    // FAP field name (normalized, no " #NNN" suffix) -> Quote attribute name.
    private static readonly Dictionary<string, string> FieldToAttr = new(StringComparer.OrdinalIgnoreCase)
    {
        ["PREMIUM"] = "Premium", ["TERRPREM"] = "Premium", ["PREMDET"] = "Premium",
        ["TOTPREMIUM"] = "Total Premium",
        ["LIMIT"] = "Limit", ["AGGLIM"] = "Limit", ["OCCURLIM"] = "Limit", ["PERADVLIM"] = "Limit",
        ["PRODUCTAGGLIM"] = "Limit", ["FIREDAMAGE"] = "Limit",
        ["DEDUCTIBLE"] = "Deductible", ["DED AMT"] = "Deductible",
        ["EXPOSURE AMT"] = "Exposure",
        ["COINS"] = "Coinsurance",
        ["COVERAGE"] = "Coverage Description", ["DESCRIPTION"] = "Coverage Description", ["CLASS DESC"] = "Coverage Description",
        ["FORMNUM"] = "Form Number", ["FORMNAME"] = "Form Name", ["EDATE"] = "Edition Date",
        ["PROPOSAL PERIOD"] = "Proposal Period",
        ["AGENT PHONE"] = "Agent Phone",
        ["VALUATION"] = "Valuation",
        ["TOTPREM"] = "Total Premium", ["MEDPAYLIM"] = "Limit",
        // Coverage markers (the "> " prefix is stripped in AttributeFor)
        ["CoverageType"] = "Coverage Type", ["CovSeq"] = "Coverage Sequence", ["COVLINE"] = "Insurance Line",
        // Location / building schedule
        ["LOC A"] = "Location", ["BLDG"] = "Building", ["COL"] = "Cause of Loss",
        ["PROTCLASS"] = "Protection Class", ["CLASS CODE"] = "Class Code", ["CLASS"] = "Class Code",
        ["ADDR"] = "Address",
        ["A1L2"] = "Limit", ["A1L3"] = "Limit", ["A1L4"] = "Limit", ["A1L5"] = "Limit",
        ["A1L6"] = "Limit", ["A1L7"] = "Limit", ["A1L8"] = "Limit",
        // Vehicle schedule
        ["VEH YEAR"] = "Vehicle Year", ["VEH VIN"] = "Vehicle VIN", ["VEH MM"] = "Vehicle Make Model",
        ["VEH STATE"] = "Vehicle State", ["VEH NUM"] = "Vehicle Number", ["VEH TOT PREM"] = "Vehicle Premium",
        ["VEHICLE A"] = "Vehicle Description",
    };

    public static string RootGuid => Guid(Root);                       // domainModel guid (= .gd rootguid)
    public static string ConceptGuid => Guid("concept:" + Root);       // <concept> guid (distinct)
    public static string AttrGuid(string attr) => Guid(Root + "." + attr);

    /// <summary>The Quote attribute a FAP field binds to, or null.</summary>
    public static string? AttributeFor(string fieldName)
    {
        var n = fieldName.Contains(" #") ? fieldName[..fieldName.IndexOf(" #")].Trim() : fieldName.Trim();
        n = n.TrimStart('>', ' ');  // repeating-group marker fields are named "> CoverageType" etc.
        return FieldToAttr.TryGetValue(n, out var a) ? a : null;
    }

    // Deterministic GUID from a stable key (MD5 of a namespaced string, laid into a Guid).
    private static string Guid(string key)
    {
        using var md5 = MD5.Create();
        var h = md5.ComputeHash(Encoding.UTF8.GetBytes("fact-pdf-tools/QuoteLib/" + key));
        return new Guid(h).ToString();
    }
}
