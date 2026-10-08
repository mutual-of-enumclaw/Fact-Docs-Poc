using System.Text.RegularExpressions;
using WeCantSpell.Hunspell;

namespace FaCT.DocDesigner.POC.Spelling;

/// <summary>A word the dictionary doesn't know, with suggestions.</summary>
public sealed record Misspelling(string Word, IReadOnlyList<string> Suggestions);

/// <summary>
/// Spell checking for document text: the en_US Hunspell dictionary (SCOWL, Spelling/Dictionaries), the insurance and
/// MOE terms shipped in Spelling/insurance-words.txt, and words authors add (custom-words.txt in the spelling folder,
/// shared by everyone). Acronyms (short all-caps words) and words with digits are not checked.
/// </summary>
public sealed partial class SpellChecker
{
	public const int MaxWords = 5000;
	public const int MaxWordLength = 60;
	private const int MaxSuggestions = 5;

	private readonly Lazy<WordList> _dictionary;
	private readonly HashSet<string> _domainWords;
	private readonly HashSet<string> _customWords;
	private readonly string _customPath;
	private readonly SemaphoreSlim _writeLock = new(1, 1);
	private readonly object _customGate = new();

	public SpellChecker(string contentRoot, string customRoot)
	{
		var dictionaries = Path.Combine(contentRoot, "Spelling", "Dictionaries");
		_dictionary = new Lazy<WordList>(() =>
			WordList.CreateFromFiles(Path.Combine(dictionaries, "en_US.dic"), Path.Combine(dictionaries, "en_US.aff")));
		_domainWords = ReadWords(Path.Combine(contentRoot, "Spelling", "insurance-words.txt"));
		Directory.CreateDirectory(customRoot);
		_customPath = Path.Combine(customRoot, "custom-words.txt");
		_customWords = ReadWords(_customPath);
	}

	/// <summary>Letters with inner apostrophes or hyphens (insured's, re-issue), at most 60 characters.</summary>
	public static bool IsWord(string? word) => word is { Length: > 0 and <= MaxWordLength } && WordPattern().IsMatch(word);

	public IReadOnlyList<string> CustomWords()
	{
		lock (_customGate) return _customWords.OrderBy(w => w, StringComparer.OrdinalIgnoreCase).ToList();
	}

	/// <summary>The misspelled words among <paramref name="words"/> (each once, in first-seen order).</summary>
	public IReadOnlyList<Misspelling> Check(IEnumerable<string> words)
	{
		var result = new List<Misspelling>();
		var seen = new HashSet<string>(StringComparer.Ordinal);
		foreach (var raw in words)
		{
			var word = Normalize(raw);
			if (!seen.Add(word) || IsCorrect(word)) continue;
			result.Add(new Misspelling(word, _dictionary.Value.Suggest(word).Take(MaxSuggestions).ToList()));
		}
		return result;
	}

	public bool IsCorrect(string word)
	{
		word = Normalize(word);
		if (Skip(word)) return true;
		if (Known(word) || Known(word.ToLowerInvariant())) return true;
		// Possessives of known words (insured's, MOE's) and hyphenated compounds (re-issue, follow-up).
		if (word.EndsWith("'s", StringComparison.OrdinalIgnoreCase) && IsCorrect(word[..^2])) return true;
		if (word.Contains('-')) return word.Split('-', StringSplitOptions.RemoveEmptyEntries).All(IsCorrect);
		return _dictionary.Value.Check(word);
	}

	/// <summary>Adds a word to the shared custom dictionary. Returns false when it was already known there.</summary>
	public async Task<bool> AddWordAsync(string word)
	{
		word = Normalize(word);
		await _writeLock.WaitAsync();
		try
		{
			lock (_customGate)
			{
				if (!_customWords.Add(word)) return false;
			}
			await File.AppendAllTextAsync(_customPath, word + Environment.NewLine);
			return true;
		}
		finally
		{
			_writeLock.Release();
		}
	}

	private bool Known(string word)
	{
		if (_domainWords.Contains(word)) return true;
		lock (_customGate) return _customWords.Contains(word);
	}

	// Curly apostrophes as typed by Word.
	private static string Normalize(string word) => word.Trim().Replace('\u2019', '\'');

	// Not checked: acronyms (CPP, TIV, NOTICE IS short caps), single letters, and anything with digits.
	private static bool Skip(string word) =>
		word.Length < 2 || word.Any(char.IsDigit) || (word.Length <= 5 && word.All(c => !char.IsLetter(c) || char.IsUpper(c)));

	private static HashSet<string> ReadWords(string path) =>
		File.Exists(path)
			? File.ReadAllLines(path).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToHashSet(StringComparer.Ordinal)
			: new HashSet<string>(StringComparer.Ordinal);

	[GeneratedRegex(@"^\p{L}+(?:['\u2019-]\p{L}+)*$")]
	private static partial Regex WordPattern();
}
