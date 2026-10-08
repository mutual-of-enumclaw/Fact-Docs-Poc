using System.Xml.Linq;

namespace FapPdfTools.Server.Infrastructure;

/// <summary>A single data attribute of a Model Library concept (a bindable leaf).</summary>
public record MlAttribute(string ConceptName, string ConceptGuid, string AttributeName, string AttributeGuid, string Type);

/// <summary>
/// The GhostDraft "Model Library" concept catalog (a <c>.gdm</c> file), flattened to the set of
/// bindable attributes. This is what <c>.gd</c> fill points bind to; loading it lets us report,
/// for any form, which fields already have a concept vs. which need one added to the model.
/// </summary>
public class ModelLibrary
{
    public IReadOnlyList<MlAttribute> Attributes { get; }

    private ModelLibrary(IReadOnlyList<MlAttribute> attributes) => Attributes = attributes;

    /// <summary>Parse a Model Library <c>.gdm</c> (XML) into its bindable attributes.</summary>
    public static ModelLibrary Load(string gdmPath)
    {
        var doc = XDocument.Load(gdmPath);
        static string L(XElement e) => e.Name.LocalName;

        var attrs = new List<MlAttribute>();
        foreach (var concept in doc.Descendants().Where(e => L(e) == "concept"))
        {
            string cName = (string?)concept.Attribute("name") ?? "";
            string cGuid = (string?)concept.Attribute("guid") ?? "";
            var attributesEl = concept.Elements().FirstOrDefault(e => L(e) == "attributes");
            if (attributesEl == null) continue;
            foreach (var a in attributesEl.Elements().Where(e => L(e) == "attribute"))
            {
                string aName = (string?)a.Attribute("name") ?? "";
                string aGuid = (string?)a.Attribute("guid") ?? "";
                var typeEl = a.Descendants().FirstOrDefault(e => L(e) == "conceptName");
                attrs.Add(new MlAttribute(cName, cGuid, aName, aGuid, typeEl?.Value?.Trim() ?? "?"));
            }
        }
        return new ModelLibrary(attrs);
    }

    /// <summary>
    /// Best-effort match of a FAP field name to a Model Library attribute. Returns null if nothing
    /// resembles it. Compares on a normalized (alphanumeric, upper) key so "INSURED NAME1",
    /// "InsuredName" and "Full Name" can line up.
    /// </summary>
    public MlAttribute? Match(string fieldName)
    {
        string key = Norm(fieldName);
        // Names shorter than 4 normalized chars (OF, DAY, YR, CHK…) are too ambiguous to
        // suggest a concept for — leave them to curated mapping.
        if (key.Length < 4) return null;

        // 1. Exact normalized match on the attribute name.
        var exact = Attributes.FirstOrDefault(a => Norm(a.AttributeName) == key);
        if (exact != null) return exact;

        // 2. Containment either way (field name contains the attribute name or vice versa),
        //    preferring the longest attribute-name overlap to avoid trivial hits.
        return Attributes
            .Where(a => Norm(a.AttributeName).Length >= 4 &&
                        (key.Contains(Norm(a.AttributeName)) || Norm(a.AttributeName).Contains(key)))
            .OrderByDescending(a => Norm(a.AttributeName).Length)
            .FirstOrDefault();
    }

    private static string Norm(string s) =>
        new string((s ?? "").Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
}
