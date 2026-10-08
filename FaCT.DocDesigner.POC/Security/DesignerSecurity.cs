using System.Security.Claims;

namespace FaCT.DocDesigner.POC.Security;

/// <summary>
/// Roles in the designer. Anyone signed in can view documents, previews and history; changing documents needs Author,
/// publishing needs Publisher, and commenting needs any of the three roles.
/// </summary>
public static class DesignerRoles
{
	public const string Author = "Author";
	public const string Reviewer = "Reviewer";
	public const string Publisher = "Publisher";

	public static readonly IReadOnlyList<string> All = [Author, Reviewer, Publisher];

	/// <summary>Authorization policies (named after what they allow).</summary>
	public const string CanEdit = "CanEdit";
	public const string CanPublish = "CanPublish";
	public const string CanComment = "CanComment";
}

/// <summary>A designer user (POC sign-in list; production would take users and roles from Entra ID).</summary>
public sealed class DesignerUser
{
	public string Id { get; set; } = string.Empty;
	public string Name { get; set; } = string.Empty;
	public string[] Roles { get; set; } = [];
}

/// <summary>
/// Security settings: <c>Security:Enabled</c> turns sign-in and role checks on (off = everyone is the local designer
/// with every role, as before); <c>Security:Users</c> lists who can sign in and their roles;
/// <c>Security:RequireSecondPersonToPublish</c> stops people publishing a version they saved themselves.
/// </summary>
public sealed class SecurityOptions
{
	public bool Enabled { get; set; }
	public bool RequireSecondPersonToPublish { get; set; }
	public List<DesignerUser> Users { get; set; } = [];

	public const string LocalUserName = "Local designer";

	public DesignerUser? Find(string? id) =>
		Users.FirstOrDefault(u => string.Equals(u.Id, id, StringComparison.OrdinalIgnoreCase));

	public static ClaimsPrincipal Principal(DesignerUser user, string scheme)
	{
		var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, user.Id), new(ClaimTypes.Name, user.Name) };
		claims.AddRange(user.Roles.Where(r => DesignerRoles.All.Contains(r)).Select(r => new Claim(ClaimTypes.Role, r)));
		return new ClaimsPrincipal(new ClaimsIdentity(claims, scheme));
	}

	/// <summary>Who did something, for the audit log and comments.</summary>
	public static string NameOf(ClaimsPrincipal user) => user.Identity?.Name is { Length: > 0 } name ? name : LocalUserName;
}
