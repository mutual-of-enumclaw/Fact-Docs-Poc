using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Util;

namespace FaCT.DocDesigner.POC.Tests;

/// <summary>
/// PDF -> text in the same shape as fact-docgen's GoldenSnapshot.txt: one block per page separated by
/// "--- Page N ---", words grouped into lines by baseline and roughly spaced by their horizontal gaps.
/// </summary>
public static class PdfText
{
	public static string Extract(byte[] pdf)
	{
		using var document = PdfDocument.Open(pdf);
		var sb = new StringBuilder();
		foreach (var page in document.GetPages())
		{
			if (page.Number > 1) sb.AppendLine($"--- Page {page.Number} ---");
			sb.AppendLine(PageText(page).Trim());
		}
		return sb.ToString().Trim();
	}

	private static string PageText(Page page)
	{
		var lines = new List<List<(Word Word, int Layer)>>();
		foreach (var item in Words(page).OrderByDescending(w => w.Word.BoundingBox.Bottom).ThenBy(w => w.Word.BoundingBox.Left))
		{
			var line = lines.LastOrDefault();
			if (line is not null && Math.Abs(line[0].Word.BoundingBox.Bottom - item.Word.BoundingBox.Bottom) <= 2.0)
			{
				line.Add(item);
			}
			else
			{
				lines.Add([item]);
			}
		}

		const double charWidth = 4.5; // ~10pt text; only used to lay words out readably
		var spaces = page.Letters.Where(l => string.IsNullOrWhiteSpace(l.Value)).ToList();
		var sb = new StringBuilder();
		foreach (var line in lines)
		{
			var text = new StringBuilder();
			Word? prev = null;
			var layer = 0;
			// an overprinted copy follows the text under it, as Spire gives it ("CA 20 01 11 20CA 20 01 11 20")
			foreach (var (word, wordLayer) in line.OrderBy(w => w.Layer).ThenBy(w => w.Word.BoundingBox.Left))
			{
				if (wordLayer != layer)
				{
					(layer, prev) = (wordLayer, null);
					text.Append(' ');
				}
				// Absolutely positioned pieces of one word ("N" + "amed") touch: no gap narrower than a space splits a word.
				// A space character between them does (Word's condensed spacing, \expndtw, narrows a real space) -- unless
				// they touch: a letter of another layer (a form sheet's own wording) can sit in the gap of "N" + "amed".
				var gap = prev is null ? 0 : word.BoundingBox.Left - prev.BoundingBox.Right;
				if (prev is not null && gap < 0.15 * FontSize(prev) && !(gap > 0.25 && SpaceBetween(spaces, prev, word)))
				{
					text.Append(word.Text);
					prev = word;
					continue;
				}
				var column = (int)Math.Round(word.BoundingBox.Left / charWidth);
				text.Append(' ', Math.Max(text.Length == 0 ? column : 1, column - text.Length));
				text.Append(word.Text);
				prev = word;
			}
			sb.AppendLine(text.ToString().TrimEnd());
		}
		return sb.ToString();
	}

	// Text printed over other text (a form sheet's footer over the footer drawn in the sheet's own picture) is
	// grouped into words layer by layer, the way Spire extracts it, instead of the two copies interleaving letter by
	// letter ("CCAA 2200"): a letter landing on an earlier, non-adjacent letter starts the next layer.
	private static List<(Word Word, int Layer)> Words(Page page)
	{
		var layers = new List<List<Letter>>();
		var placed = new Dictionary<(int, int), List<(int Index, int Layer, double X, double Y)>>();
		var layer = 0;
		var index = 0;
		foreach (var letter in page.Letters)
		{
			index++;
			if (!string.IsNullOrWhiteSpace(letter.Value))
			{
				var (x, y) = (letter.StartBaseLine.X, letter.StartBaseLine.Y);
				var (cx, cy) = ((int)Math.Floor(x / 3), (int)Math.Floor(y / 3));
				// the copies needn't coincide exactly (a different font's advance widths shift them a point or two)
				var tolerance = Math.Min(3.0, 0.35 * letter.PointSize);
				layer = 0;
				for (var i = -1; i <= 1; i++)
				for (var j = -1; j <= 1; j++)
				{
					if (!placed.TryGetValue((cx + i, cy + j), out var near)) continue;
					foreach (var p in near)
					{
						if (index - p.Index > 1 && Math.Abs(p.X - x) < tolerance && Math.Abs(p.Y - y) < 1.0) layer = Math.Max(layer, p.Layer + 1);
					}
				}
				if (!placed.TryGetValue((cx, cy), out var cell)) placed[(cx, cy)] = cell = [];
				cell.Add((index, layer, x, y));
			}
			while (layers.Count <= layer) layers.Add([]);
			layers[layer].Add(letter);
		}
		return layers.Count <= 1
			? page.GetWords().Select(w => (w, 0)).ToList()
			: layers.SelectMany((l, n) => DefaultWordExtractor.Instance.GetWords(l).Select(w => (w, n))).ToList();
	}

	private static double FontSize(Word word) => word.Letters.Count > 0 ? word.Letters[^1].PointSize : 10;

	private static bool SpaceBetween(List<Letter> spaces, Word left, Word right) =>
		spaces.Any(s => Math.Abs(s.StartBaseLine.Y - left.Letters[^1].StartBaseLine.Y) < 1.0 &&
			s.StartBaseLine.X >= left.Letters[^1].StartBaseLine.X && s.StartBaseLine.X <= right.Letters[0].StartBaseLine.X);
}
