namespace FapPdfTools.Server.Configuration;

/// <summary>
/// Base URLs for the Commercial API across deployment environments.
/// </summary>
public class CommercialApiOptions
{
	public const string SectionName = "CommercialApi";

	/// <summary>Environment identifier → base URL. Keys are lower-case (dev, dev3, tst, tst2, acc).</summary>
	public Dictionary<string, string> BaseUrls { get; set; } = [];

	/// <summary>Environment to use when no <c>env</c> query parameter is supplied. Defaults to <c>tst</c>.</summary>
	public string DefaultEnvironment { get; set; } = "tst";

	/// <summary>
	/// Return the base URL for <paramref name="env"/>, falling back to <see cref="DefaultEnvironment"/>
	/// if not found, then returning <see langword="null"/> when no URLs are configured.
	/// </summary>
	public string? ResolveBaseUrl(string? env)
	{
		string key = string.IsNullOrWhiteSpace(env) ? DefaultEnvironment : env.Trim().ToLowerInvariant();
		if (BaseUrls.TryGetValue(key, out string? url)) return url;
		if (BaseUrls.TryGetValue(DefaultEnvironment, out string? def)) return def;
		return null;
	}
}
