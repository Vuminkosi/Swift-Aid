using SwiftAid.Application.Ellai;

namespace SwiftAid.Application.Tests.Ellai;

public sealed class EllaiPublicHelpClassifierTests
{
    private readonly EllaiPublicHelpClassifier _classifier = new();

    [Theory]
    [InlineData("How can a guest submit a report without an account?", "account, guest, report")]
    [InlineData("What does an intake receipt mean?", "intake, receipt")]
    [InlineData("Can Ellai give medical advice?", "advice, ellai, medical")]
    [InlineData("What is SwiftAid and what can I do here?", "swiftaid")]
    public void ApprovedPublicQuestion_IsReducedToCatalogTopics(string input, string expectedTopics)
    {
        Assert.True(_classifier.TryClassify(input, out var result));
        Assert.NotNull(result);
        Assert.Equal(expectedTopics.Split(", "), result!.Topics);
        Assert.DoesNotContain(input, result.SanitizedPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("approved public product facts", result.SanitizedPrompt, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("What is the status of case 4812?")]
    [InlineData("Can I report for Jane Smith?")]
    [InlineData("Patient has a fever, what should I do?")]
    [InlineData("What does this link show https://example.test/report?")]
    [InlineData("Tell me a joke")]
    public void UnknownOrSensitiveText_IsRejectedWithoutSanitizingAndForwarding(string input)
    {
        Assert.False(_classifier.TryClassify(input, out var result));
        Assert.Null(result);
    }

    [Fact]
    public void OverlengthQuestion_IsRejected()
    {
        Assert.False(_classifier.TryClassify(new string('a', EllaiPublicHelpClassifier.MaximumInputLength + 1), out var result));
        Assert.Null(result);
    }
}
