namespace FapPdfTools.Server.Models;

public record FormCatalogEntry(
    string FormKey,
    string FileName,
    string SectionType,
    int SectionCount,
    string? DisplayName = null,
    string? SourceFormNumber = null,
    string? SourceEditionDate = null,
    string? Description = null);

public record FormInfoResponse(
    string FormKey,
    string FileName,
    int FieldCount,
    int StaticTextCount,
    int LineCount,
    int TextAreaCount,
    int PageCount,
    IReadOnlyList<string> FieldNames,
    IReadOnlyList<FormSectionInfo> Sections);

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
