using System.Text;
using System.Text.RegularExpressions;

namespace SwiftAid.Application.Ellai;

/// <summary>Locally reduces a public-help question to approved product-fact
/// topics. The original wording is never forwarded to a model.</summary>
public sealed class EllaiPublicHelpClassifier
{
    public const int MaximumInputLength = 320;

    private static readonly HashSet<string> QuestionWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "about", "account", "accounts", "am", "an", "and", "app", "are", "as", "at",
        "be", "can", "could", "create", "did", "do", "does", "explain", "file", "for", "from",
        "get", "give", "guest", "guests", "happens", "has", "have", "help", "here", "how", "i", "in",
        "is", "it", "mean", "me", "my", "of", "on", "or", "our", "please", "report", "send",
        "show", "submit", "tell", "that", "the", "these", "this", "those", "to", "use", "was",
        "we", "were", "what", "when", "where", "which", "who", "why", "with", "without", "would",
        "you", "your", "swiftaid", "system", "public", "product"
    };

    private readonly IReadOnlySet<string> _publicTopics;

    public EllaiPublicHelpClassifier(EllaiProductFactCatalog? catalog = null)
    {
        var source = catalog ?? new EllaiProductFactCatalog();
        _publicTopics = source.Find(null)
            .SelectMany(fact => fact.Topics)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public bool TryClassify(string? input, out EllaiPublicHelpClassification? classification)
    {
        classification = null;
        if (string.IsNullOrWhiteSpace(input) || input.Length > MaximumInputLength)
        {
            return false;
        }

        var normalized = input.Normalize(NormalizationForm.FormKC);
        if (normalized.Any(character =>
                !char.IsLetter(character) && !char.IsWhiteSpace(character) && character is not '?' and not '!' and not '.' and not ',' and not '\'' and not '-'))
        {
            return false;
        }

        var words = Regex.Matches(normalized, @"\p{L}+")
            .Select(match => match.Value)
            .ToArray();
        if (words.Length == 0 || words.Any(word => !QuestionWords.Contains(word) && !_publicTopics.Contains(word)))
        {
            return false;
        }

        var topics = words
            .Where(_publicTopics.Contains)
            .Select(topic => topic.ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(topic => topic, StringComparer.Ordinal)
            .ToArray();
        if (topics.Length == 0)
        {
            return false;
        }

        var topicList = string.Join(", ", topics);
        classification = new EllaiPublicHelpClassification(
            topics,
            $"Answer a public SwiftAid product-help question about these approved topics: {topicList}. " +
            "Use only approved public product facts. Do not infer personal, case-specific, medical, or staff information.");
        return true;
    }
}

public sealed record EllaiPublicHelpClassification(
    IReadOnlyList<string> Topics,
    string SanitizedPrompt);
