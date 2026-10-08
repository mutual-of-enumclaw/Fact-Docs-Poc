namespace FaCT.DocDesigner.POC.Embedding;

/// <summary>
/// Which other web apps (e.g. Commercial Web) may show the designer in a frame (<c>/?embed=1</c>) and drive it with
/// window messages. Configuration: <c>Embed:AllowedOrigins</c> = list of origins ("https://commercial.moe.com",
/// "http://localhost:8080"). Pages may only be framed by this site and these origins (CSP frame-ancestors), and the
/// designer only talks to a parent page from one of them.
/// </summary>
public sealed class EmbedOptions
{
	public IReadOnlyList<string> AllowedOrigins { get; }

	public EmbedOptions(IConfiguration configuration)
	{
		var configured = configuration.GetSection("Embed:AllowedOrigins").Get<string[]>() ?? [];
		AllowedOrigins = configured.Select(Normalize).Where(o => o is not null).Select(o => o!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
	}

	/// <summary>The CSP frame-ancestors value: this site plus the allowed origins.</summary>
	public string FrameAncestors => string.Join(' ', new[] { "'self'" }.Concat(AllowedOrigins));

	/// <summary>
	/// "scheme://host[:port]" for an http(s) origin, or null (paths, wildcards, other schemes and plain http to anything
	/// but localhost are refused).
	/// </summary>
	public static string? Normalize(string? value)
	{
		if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri)) return null;
		if (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)) return null;
		if (uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.UserInfo.Length > 0) return null;
		if (uri.Host.Contains('*')) return null;
		return uri.GetLeftPart(UriPartial.Authority);
	}
}
