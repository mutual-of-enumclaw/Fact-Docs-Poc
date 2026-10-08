using System.IO.Compression;
using System.Net;
using System.Text.RegularExpressions;

namespace FaCT.DocDesigner.POC.Import;

/// <summary>
/// Result of converting an uploaded PDF or Word document into designer HTML (plus template CSS, e.g. the page geometry of a
/// Word document with running headers/footers).
/// <see cref="Model"/> is a sample data model for the merge fields found (Word only), or null when there are none.
/// </summary>
public sealed record DocumentImportResult(
	string Format,
	string Html,
	string Css,
	int Pages,
	IReadOnlyDictionary<string, int> Counts,
	IReadOnlyList<string> Fields,
	Dictionary<string, object?>? Model,
	IReadOnlyList<string> Notes);

/// <summary>The upload can't be converted; the message is safe to show to the user.</summary>
public class DocumentImportException(string message, Exception? inner = null) : Exception(message, inner);

public enum DocumentKind { Unknown, Pdf, Docx }

public static partial class DocumentImport
{
	/// <summary>
	/// Placed between a "{" and a following "{" or "%" in imported text. Entities alone don't keep wording such as
	/// "{{ x }}" from becoming Liquid: the designer decodes them and saves the raw characters. An element between the
	/// two characters survives the designer, prints nothing, and means the template never contains "{{" or "{%".
	/// </summary>
	public const string LiquidBreak = "<span class=\"no-liquid\"></span>";

	/// <summary>HTML-encodes document text so that it is shown as written and can never be read as Liquid.</summary>
	public static string EncodeText(string text) => LiquidOpener().Replace(WebUtility.HtmlEncode(text), "{" + LiquidBreak);

	[GeneratedRegex(@"\{(?=[{%])")]
	private static partial Regex LiquidOpener();

	public const string PdfContentType = "application/pdf";
	public const string DocxContentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

	public const long MaxUploadBytes = 25 * 1024 * 1024;

	// A .docx is a zip: cap what it may expand to so a small upload can't exhaust memory (zip bomb).
	public const long MaxDocxExpandedBytes = 200L * 1024 * 1024;
	public const int MaxDocxEntries = 2000;

	/// <summary>Identifies the file by its content, never by its name or declared type.</summary>
	public static DocumentKind Detect(ReadOnlySpan<byte> bytes)
	{
		if (bytes.StartsWith("%PDF-"u8)) return DocumentKind.Pdf;
		if (bytes.StartsWith("PK\u0003\u0004"u8)) return DocumentKind.Docx;
		return DocumentKind.Unknown;
	}

	/// <summary>Old binary Office files (.doc) are OLE compound files.</summary>
	public static bool IsLegacyOfficeFile(ReadOnlySpan<byte> bytes) =>
		bytes.StartsWith(new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 });

	public static DocumentKind ForContentType(string? contentType) =>
		contentType?.Split(';')[0].Trim().ToLowerInvariant() switch
		{
			PdfContentType => DocumentKind.Pdf,
			DocxContentType => DocumentKind.Docx,
			_ => DocumentKind.Unknown
		};

	/// <summary>Rejects zips that are not Word documents or that would expand past the limits.</summary>
	public static void CheckDocxPackage(byte[] bytes)
	{
		ZipArchive zip;
		try
		{
			zip = new ZipArchive(new MemoryStream(bytes, writable: false), ZipArchiveMode.Read);
		}
		catch (InvalidDataException ex)
		{
			throw new DocumentImportException("The file is not a valid Word document.", ex);
		}

		using (zip)
		{
			if (zip.Entries.Count > MaxDocxEntries)
			{
				throw new DocumentImportException("The Word document has too many parts.");
			}
			long total = 0;
			foreach (var entry in zip.Entries)
			{
				total += entry.Length;
				if (total > MaxDocxExpandedBytes)
				{
					throw new DocumentImportException("The Word document is too large once uncompressed.");
				}
			}
			if (zip.GetEntry("word/document.xml") is null && !zip.Entries.Any(e => e.FullName.EndsWith("/document.xml", StringComparison.OrdinalIgnoreCase)))
			{
				throw new DocumentImportException("The file is a zip archive but not a Word (.docx) document.");
			}
		}
	}
}
