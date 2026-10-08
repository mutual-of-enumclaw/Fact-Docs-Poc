using System.Text;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace FaCT.DocDesigner.POC.Tests.Import;

/// <summary>Builds PDFs for the importer tests: PdfPig's writer for content, hand-written objects for AcroForms.</summary>
internal static class Pdf
{
	public sealed class Fonts(PdfDocumentBuilder builder)
	{
		public PdfDocumentBuilder.AddedFont Helvetica { get; } = builder.AddStandard14Font(Standard14Font.Helvetica);
		public PdfDocumentBuilder.AddedFont HelveticaBold { get; } = builder.AddStandard14Font(Standard14Font.HelveticaBold);
		public PdfDocumentBuilder.AddedFont Times { get; } = builder.AddStandard14Font(Standard14Font.TimesRoman);
		public PdfDocumentBuilder.AddedFont TimesItalic { get; } = builder.AddStandard14Font(Standard14Font.TimesItalic);
		public PdfDocumentBuilder.AddedFont Courier { get; } = builder.AddStandard14Font(Standard14Font.Courier);
	}

	/// <summary>One Letter page (612 x 792 pt, origin bottom-left).</summary>
	public static byte[] Letter(Action<PdfPageBuilder, Fonts> draw) => Pages(1, (page, fonts, _) => draw(page, fonts));

	public static byte[] Pages(int count, Action<PdfPageBuilder, Fonts, int> draw, double width = 612, double height = 792)
	{
		var builder = new PdfDocumentBuilder();
		var fonts = new Fonts(builder);
		for (var i = 0; i < count; i++)
		{
			draw(builder.AddPage(width, height), fonts, i + 1);
		}
		return builder.Build();
	}

	public static byte[] A4(Action<PdfPageBuilder, Fonts> draw)
	{
		var builder = new PdfDocumentBuilder();
		var fonts = new Fonts(builder);
		draw(builder.AddPage(PageSize.A4), fonts);
		return builder.Build();
	}

	public static PdfPoint At(double x, double y) => new(x, y);

	/// <summary>
	/// A one-page PDF with an AcroForm: a text field "PolicyNumber" (MaxLen 12) at [100 600 300 620], a checkbox "Agree"
	/// at [100 550 112 562], a signature "Signature" at [100 480 300 520], and the label "Policy Number:" at (72, 700).
	/// </summary>
	public static byte[] WithForm() => Raw(
		"<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R 5 0 R 6 0 R] >> >>",
		"<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
		"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [4 0 R 5 0 R 6 0 R] /Contents 7 0 R /Resources << /Font << /F1 8 0 R >> >> >>",
		"<< /Type /Annot /Subtype /Widget /FT /Tx /T (PolicyNumber) /MaxLen 12 /Rect [100 600 300 620] /P 3 0 R /F 4 >>",
		"<< /Type /Annot /Subtype /Widget /FT /Btn /T (Agree) /Rect [100 550 112 562] /P 3 0 R /V /Off /AS /Off /F 4 >>",
		"<< /Type /Annot /Subtype /Widget /FT /Sig /T (Signature) /Rect [100 480 300 520] /P 3 0 R /F 4 >>",
		Stream("BT /F1 12 Tf 72 700 Td (Policy Number:) Tj ET"),
		"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");

	public static string Stream(string content) =>
		$"<< /Length {Encoding.Latin1.GetByteCount(content)} >>\nstream\n{content}\nendstream";

	/// <summary>A one-page Letter PDF whose content stream is written by hand (for curves, diagonals, etc.).</summary>
	public static byte[] RawPage(string content) => Raw(
		"<< /Type /Catalog /Pages 2 0 R >>",
		"<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
		"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
		Stream(content),
		"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");

	/// <summary>Writes objects 1..n with a correct xref table; object 1 is the catalog.</summary>
	public static byte[] Raw(params string[] objects)
	{
		var pdf = new StringBuilder("%PDF-1.7\n");
		var offsets = new List<int>();
		for (var i = 0; i < objects.Length; i++)
		{
			offsets.Add(Encoding.Latin1.GetByteCount(pdf.ToString()));
			pdf.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
		}
		var xref = Encoding.Latin1.GetByteCount(pdf.ToString());
		pdf.Append("xref\n0 ").Append(objects.Length + 1).Append('\n').Append("0000000000 65535 f \n");
		foreach (var offset in offsets)
		{
			pdf.Append(offset.ToString("D10")).Append(" 00000 n \n");
		}
		pdf.Append("trailer\n<< /Size ").Append(objects.Length + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
		return Encoding.Latin1.GetBytes(pdf.ToString());
	}
}
