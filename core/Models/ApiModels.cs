namespace FapPdfTools.Server.Models;

/// <summary>Form classification derived from FAP F-line count + DDT powtype signal.</summary>
public static class FormClassification
{
    public const string Static   = "Static";
    public const string Variable = "Variable";
    public const string Wip      = "WIP";
    public const string Unknown  = "Unknown";
}

public record FormCatalogEntry(
    string FormKey,
    string FileName,
    string SectionType,
    int SectionCount,
    string? DisplayName = null,
    string? SourceFormNumber = null,
    string? SourceEditionDate = null,
    string? Description = null,
    string? Classification = null);

public record FormInfoResponse(
    string FormKey,
    string FileName,
    int FieldCount,
    int StaticTextCount,
    int LineCount,
    int TextAreaCount,
    int PageCount,
    IReadOnlyList<string> FieldNames,
    IReadOnlyList<FormSectionInfo> Sections,
    string Classification = FormClassification.Unknown);

public record FormSectionInfo(string FileName, string SectionType, string Metadata);

public record ConvertRequest(string FormNumber, string EditionDate);

public record ConvertResponse(string FileName, int FieldCount, IReadOnlyList<string> FieldNames);

public record FillFieldsRequest(
    string FormNumber,
    string EditionDate,
    Dictionary<string, string> FieldValues,
    bool Flatten = false);

/// <summary>A single fillable field with PDF-point bounds, FXR font, and DDT rule.</summary>
public record FieldDescriptor(
    string Name,
    int Page,
    float X,
    float Y,
    float Width,
    float Height,
    int FontId,
    float PointSize,
    bool Bold,
    int MaxLength,
    string DdtMethod,
    string DdtSource);

public record FormFieldsResponse(
    string FormKey,
    string FileName,
    int PageCount,
    IReadOnlyList<FieldDescriptor> Fields);

/// <summary>A fillable AcroForm field read directly from a pre-built PDF template (no FAP).</summary>
public record TemplateField(string Name, string Type, int MaxLength);

/// <summary>Request to build (offline) a fillable PDF template from a legacy FAP form.</summary>
public record BuildTemplateRequest(string FormNumber, string EditionDate);

/// <summary>The fillable fields of a pre-built template, for driving a data-entry UI.</summary>
public record TemplateFieldsResponse(
    string FormNumber,
    string EditionDate,
    int FieldCount,
    IReadOnlyList<TemplateField> Fields);

/// <summary>
/// Per-field rendering override applied at template build. Each property is optional; when null the
/// FAP/FXR-derived default is used. <see cref="PointSize"/> sets the field's font size (fixes
/// "too small" fields like an 8pt insured name); <see cref="MaxLength"/> caps user input on the
/// field; <see cref="Bold"/> forces bold on/off.
/// </summary>
public class FieldOverride
{
    public float? PointSize { get; set; }
    public int? MaxLength { get; set; }
    public bool? Bold { get; set; }

    /// <summary>Value format applied at render: "money", "date", "percent", or null/"text".
    /// Lets a raw value (from the policy map or user input) render consistently, e.g. 1000000 -> 1,000,000.</summary>
    public string Format { get; set; }

    /// <summary>Decimal places for money/percent. Defaults: money=2, percent=0.</summary>
    public int? Decimals { get; set; }

    /// <summary>Optional literal prefix for money (e.g. "$"). Leave null when the form already prints a "$".</summary>
    public string Prefix { get; set; }
}

/// <summary>Effective per-field metadata: FAP/FXR defaults merged with any saved override.</summary>
public record TemplateFieldMeta(
    string Name,
    int FontId,
    float PointSize,
    int MaxLength,
    bool Bold,
    bool Overridden,
    string Format = null,
    int? Decimals = null,
    string Prefix = null);

/// <summary>Response for the overrides endpoint — a ready-to-edit per-field metadata list.</summary>
public record TemplateOverridesResponse(
    string FormNumber,
    string EditionDate,
    IReadOnlyList<TemplateFieldMeta> Fields);

/// <summary>
/// A fillable field with its policy-mapped pre-fill value (for an editable interactive form).
/// <see cref="Locked"/> is true when the field is owned by the policy field map (pre-filled from
/// the Common Model and shown read-only on a hybrid WIP form); false fields are user-editable.
/// </summary>
public record TemplateFieldValue(string Name, string Type, int MaxLength, string Value, bool Locked, string Format = null);

/// <summary>
/// Pre-fill response: each fillable field of a template paired with the value the field map
/// derived from the policy. The UI renders these as editable inputs (pre-filled), lets the user
/// adjust, then renders via /api/templates/fill.
/// </summary>
public record TemplatePrefillResponse(
    string FormNumber,
    string EditionDate,
    int FieldCount,
    IReadOnlyList<TemplateFieldValue> Fields);
