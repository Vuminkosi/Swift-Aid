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
        words = NormalizeRecognizedTopicTerms(words);
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

    private static string[] NormalizeRecognizedTopicTerms(IReadOnlyList<string> words)
    {
        var normalized = new List<string>(words.Count);
        for (var index = 0; index < words.Count; index++)
        {
            if (index + 1 < words.Count
                && string.Equals(words[index], "swift", StringComparison.OrdinalIgnoreCase)
                && string.Equals(words[index + 1], "aid", StringComparison.OrdinalIgnoreCase))
            {
                normalized.Add("swiftaid");
                index++;
                continue;
            }

            // The configured ASR commonly renders the spoken topic "guest"
            // as "guess". It is safe to accept this alias because only the
            // matched catalog topic, never the recognized wording, is sent on.
            normalized.Add(string.Equals(words[index], "guess", StringComparison.OrdinalIgnoreCase)
                ? "guest"
                : words[index]);
        }

        return normalized.ToArray();
    }
}

public sealed record EllaiPublicHelpClassification(
    IReadOnlyList<string> Topics,
    string SanitizedPrompt);
