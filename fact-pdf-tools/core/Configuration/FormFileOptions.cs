namespace FapPdfTools.Server.Configuration;

/// <summary>
/// Configuration for FAP/DDT file paths.
/// </summary>
public class FormFileOptions
{
    public const string SectionName = "FormFiles";

    public string FormDatPath { get; set; } = string.Empty;
    public string FormsDirectory { get; set; } = string.Empty;
    public string DdtDirectory { get; set; } = string.Empty;
    public string PdfOutputDirectory { get; set; } = string.Empty;

    /// <summary>Path to the Documaker FXR font cross-reference. If empty, the
    /// DEFLIB folder next to FormsDirectory is searched for the largest *.FXR.</summary>
    public string FxrPath { get; set; } = string.Empty;

    /// <summary>Directory for authored form definitions (JSON). Defaults to ./authored-forms.</summary>
    public string AuthoredFormsDirectory { get; set; } = string.Empty;

    /// <summary>Directory for saved test scenarios (JSON). Defaults to ./scenarios.</summary>
    public string ScenariosDirectory { get; set; } = string.Empty;

    /// <summary>Directory for pre-built fillable PDF templates (one .pdf per form+edition).
    /// Templates are produced offline (FAP -> fillable PDF) and loaded at request time
    /// without any FAP parsing. Defaults to ./pdf-templates.</summary>
    public string PdfTemplateDirectory { get; set; } = string.Empty;
}

