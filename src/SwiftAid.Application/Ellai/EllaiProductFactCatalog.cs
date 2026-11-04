namespace SwiftAid.Application.Ellai;

/// <summary>A reviewed, nonclinical product statement with explicit source
/// and revision provenance for Ellai's read-only product-help tool.</summary>
public sealed record EllaiProductFact(
    string FactId,
    string Text,
    string SourceId,
    string Revision,
    IReadOnlySet<string> Topics);

/// <summary>Application-owned projection of approved public product facts.
/// This is grounding material for the model, not a response/FAQ catalogue.
/// Update the source revision whenever the governing spec changes.</summary>
public sealed class EllaiProductFactCatalog
{
    public const string Revision = "spec/1.4+ellai-spec/1.3";

    private static readonly EllaiProductFact[] Facts =
    [
        new(
            "swiftaid-public-overview",
            "SwiftAid is an AI-powered Community Health Support and One Health Case Coordination System. People can submit concerns, explore published community sessions, and authorized teams can coordinate cases. It is not a general-purpose chatbot and does not provide medical or veterinary advice.",
            "spec.md#1.1-product-definition;spec.md#3.1-personas;spec.md#6.5-account-free-reporting-private-intake-and-coverage;docs/ellai/ellai-spec.md#13-security-privacy-and-nonclinical-safety",
            Revision,
            new HashSet<string>(["swiftaid"], StringComparer.OrdinalIgnoreCase)),
        new(
            "public-intake-no-account",
            "A public visitor can read approved information and submit a private concern without creating an account.",
            "spec.md#3.1-personas-and-access-boundaries;spec.md#14.5-guest-mode",
            Revision,
            new HashSet<string>(["guest", "public", "report", "intake", "account"], StringComparer.OrdinalIgnoreCase)),
        new(
            "intake-receipt-is-not-casework",
            "An intake receipt confirms that a concern was received. It does not mean the report was clinically verified, accepted as a managed case, assigned to a worker, or grant access to staff records.",
            "spec.md#sa-pub-03;spec.md#sa-pub-07",
            Revision,
            new HashSet<string>(["receipt", "tracking", "report", "intake", "assignment", "case"], StringComparer.OrdinalIgnoreCase)),
        new(
            "role-does-not-grant-access",
            "A selected professional role describes the person's intended role. It does not grant staff access; permissions require verified organizational membership and authorized grants.",
            "spec.md#3.2-permission-model;spec.md#sa-acl-06",
            Revision,
            new HashSet<string>(["role", "staff", "account", "permission", "access", "membership"], StringComparer.OrdinalIgnoreCase)),
        new(
            "ellai-nonclinical-boundary",
            "Ellai does not provide medical or veterinary advice, diagnosis, treatment, symptom-based clinical triage, or generated health education.",
            "spec.md#sa-cmp-02;docs/ellai/ellai-spec.md#13-security-privacy-and-nonclinical-safety",
            Revision,
            new HashSet<string>(["ellai", "medical", "health", "advice", "diagnosis", "triage", "education"], StringComparer.OrdinalIgnoreCase))
    ];

    public IReadOnlyList<EllaiProductFact> Find(string? topic)
    {
        if (topic is null)
        {
            return Facts;
        }

        if (string.IsNullOrWhiteSpace(topic))
        {
            return Array.Empty<EllaiProductFact>();
        }

        var terms = topic.Split(
                [' ', '-', '_', '/', ',', '.', '?', '!'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(term => term.Length >= 3)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (terms.Length == 0)
        {
            return Array.Empty<EllaiProductFact>();
        }

        return Facts
            .Select(fact => new
            {
                Fact = fact,
                Score = terms.Count(term => fact.Topics.Contains(term))
            })
            .Where(match => match.Score > 0)
            .OrderByDescending(match => match.Score)
            .ThenBy(match => match.Fact.FactId, StringComparer.Ordinal)
            .Select(match => match.Fact)
            .ToArray();
    }
}
