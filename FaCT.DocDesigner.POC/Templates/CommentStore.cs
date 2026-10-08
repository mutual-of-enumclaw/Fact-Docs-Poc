using System.Text.Json;

namespace FaCT.DocDesigner.POC.Templates;

public sealed record CommentReply(string Author, string Text, DateTimeOffset CreatedUtc);

/// <summary>
/// A reviewer comment on an element of a template. <see cref="Anchor"/> is the id the designer keeps on the element
/// (saved with the project, never exported to the document); <see cref="Path"/> (child indexes from the page, e.g.
/// "0.3.1") finds it until the anchor is saved; <see cref="Element"/> describes it for when it is gone.
/// </summary>
public sealed record ReviewComment(
	string Id,
	string Anchor,
	string Path,
	string Element,
	string Author,
	string Text,
	DateTimeOffset CreatedUtc,
	bool Resolved,
	DateTimeOffset? ResolvedUtc,
	string? ResolvedBy,
	IReadOnlyList<CommentReply> Replies);

/// <summary>
/// Reviewer comments per template / clause name, kept across versions (like test scenarios). File-backed for the POC:
/// {root}/{kind}/{name}.json.
/// </summary>
public sealed class CommentStore
{
	public const int MaxComments = 500;
	public const int MaxReplies = 100;
	public const int MaxTextLength = 2000;
	public const int MaxAuthorLength = 60;

	private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
	private readonly SemaphoreSlim _writeLock = new(1, 1);
	private readonly string _root;

	public CommentStore(string root)
	{
		_root = root;
		foreach (var kind in ScenarioStore.Kinds) Directory.CreateDirectory(Path.Combine(_root, kind));
	}

	public async Task<IReadOnlyList<ReviewComment>> ListAsync(string kind, string name)
	{
		var path = PathFor(kind, name);
		if (!File.Exists(path)) return [];
		await using var stream = File.OpenRead(path);
		return await JsonSerializer.DeserializeAsync<List<ReviewComment>>(stream, JsonOptions) ?? [];
	}

	/// <exception cref="InvalidOperationException">The template already has <see cref="MaxComments"/> comments.</exception>
	public Task<ReviewComment> AddAsync(string kind, string name, string anchor, string path, string element, string author, string text) =>
		UpdateAsync(kind, name, comments =>
		{
			if (comments.Count >= MaxComments) throw new InvalidOperationException($"A template can have at most {MaxComments} comments.");
			var comment = new ReviewComment(Guid.NewGuid().ToString("N")[..12], anchor, path, element, author, text, DateTimeOffset.UtcNow, false, null, null, []);
			comments.Add(comment);
			return comment;
		});

	public Task<ReviewComment?> ReplyAsync(string kind, string name, string id, string author, string text) =>
		UpdateAsync<ReviewComment?>(kind, name, comments =>
		{
			var index = comments.FindIndex(c => c.Id == id);
			if (index < 0) return null;
			if (comments[index].Replies.Count >= MaxReplies) throw new InvalidOperationException($"A comment can have at most {MaxReplies} replies.");
			comments[index] = comments[index] with { Replies = [.. comments[index].Replies, new CommentReply(author, text, DateTimeOffset.UtcNow)] };
			return comments[index];
		});

	public Task<ReviewComment?> ResolveAsync(string kind, string name, string id, bool resolved, string? by) =>
		UpdateAsync<ReviewComment?>(kind, name, comments =>
		{
			var index = comments.FindIndex(c => c.Id == id);
			if (index < 0) return null;
			comments[index] = comments[index] with
			{
				Resolved = resolved,
				ResolvedUtc = resolved ? DateTimeOffset.UtcNow : null,
				ResolvedBy = resolved ? by : null
			};
			return comments[index];
		});

	public Task<bool> DeleteAsync(string kind, string name, string id) =>
		UpdateAsync(kind, name, comments => comments.RemoveAll(c => c.Id == id) > 0);

	private async Task<T> UpdateAsync<T>(string kind, string name, Func<List<ReviewComment>, T> change)
	{
		await _writeLock.WaitAsync();
		try
		{
			var comments = (await ListAsync(kind, name)).ToList();
			var result = change(comments);
			await AtomicFile.WriteAllTextAsync(PathFor(kind, name), JsonSerializer.Serialize(comments, JsonOptions));
			return result;
		}
		finally
		{
			_writeLock.Release();
		}
	}

	private string PathFor(string kind, string name) =>
		ScenarioStore.IsValidKind(kind) && TemplateStore.IsValidName(name)
			? Path.Combine(_root, kind, name + ".json")
			: throw new ArgumentException("Invalid template name.", nameof(name));
}
