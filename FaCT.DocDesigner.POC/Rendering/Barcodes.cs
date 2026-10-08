using System.Globalization;
using System.Text;
using ZXing;
using ZXing.Common;

namespace FaCT.DocDesigner.POC.Rendering;

/// <summary>
/// Barcodes and QR codes as inline SVG (one path, no images, no network), drawn from ZXing's module matrix.
/// 1D codes (Code 128, Code 39) stretch to the height their box gives them; 2D codes keep their square modules.
/// A value the symbology can't encode gives no barcode (null) rather than failing the whole document.
/// </summary>
public static class Barcodes
{
	public const int MaxLength = 1000;

	public static readonly IReadOnlyDictionary<string, BarcodeFormat> Kinds = new Dictionary<string, BarcodeFormat>
	{
		["qr"] = BarcodeFormat.QR_CODE,
		["code128"] = BarcodeFormat.CODE_128,
		["code39"] = BarcodeFormat.CODE_39,
		["datamatrix"] = BarcodeFormat.DATA_MATRIX,
		["pdf417"] = BarcodeFormat.PDF_417
	};

	public static bool IsLinear(string kind) => kind is "code128" or "code39";

	public static string? Svg(string? text, string kind)
	{
		if (string.IsNullOrEmpty(text) || text.Length > MaxLength || !Kinds.TryGetValue(kind, out var format)) return null;
		BitMatrix matrix;
		try
		{
			var hints = new Dictionary<EncodeHintType, object>
			{
				// quiet zones come from the box's padding (1D) or the SVG's view box (2D, not every writer honors MARGIN)
				[EncodeHintType.MARGIN] = 0,
				[EncodeHintType.CHARACTER_SET] = "UTF-8"
			};
			matrix = new MultiFormatWriter().encode(text, format, 0, IsLinear(kind) ? 1 : 0, hints);
		}
		catch (Exception ex) when (ex is ArgumentException or WriterException or InvalidOperationException or IndexOutOfRangeException)
		{
			return null;
		}

		var rows = IsLinear(kind) ? 1 : matrix.Height;
		var path = new StringBuilder();
		for (var y = 0; y < rows; y++)
		{
			var x = 0;
			while (x < matrix.Width)
			{
				if (!matrix[x, y])
				{
					x++;
					continue;
				}
				var start = x;
				while (x < matrix.Width && matrix[x, y]) x++;
				path.Append(CultureInfo.InvariantCulture, $"M{start} {y}h{x - start}v1h-{x - start}z");
			}
		}

		var aspect = IsLinear(kind) ? " preserveAspectRatio=\"none\"" : string.Empty;
		var box = IsLinear(kind) ? $"0 0 {matrix.Width} {rows}" : $"-{QuietZone} -{QuietZone} {matrix.Width + 2 * QuietZone} {rows + 2 * QuietZone}";
		return $"<svg class=\"doc-code doc-code-{kind}\" viewBox=\"{box}\"{aspect} aria-hidden=\"true\"><path d=\"{path}\"></path></svg>";
	}

	/// <summary>Blank modules around a 2D code, so scanners find its edges.</summary>
	private const int QuietZone = 4;
}
