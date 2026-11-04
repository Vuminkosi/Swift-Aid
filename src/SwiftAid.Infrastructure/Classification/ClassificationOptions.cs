using SwiftAid.Application.Classification;

namespace SwiftAid.Infrastructure.Classification;

public sealed class ClassificationOptions
{
    public bool Enabled { get; set; }
    public string WorkerId { get; set; } = "classification-worker";
    public string WorkerTokenSha256 { get; set; } = "";
    public string[] Organizations { get; set; } = [];
    public int LeaseSeconds { get; set; } = 180;
    public int MaximumAttempts { get; set; } = 5;
    public ClassificationProfile Profile { get; set; } = new(
        "facebook/bart-large-mnli", "d7645e127eaf1aefc7862fd59a17a5aa8558b8ce",
        "reported-presentations-v1", "nfc-whitespace-v1", "report-about-v1", "This report describes {}.", "und", 512, 0.7,
        [new("human", "subject", "a human health concern"), new("animal", "subject", "an animal health concern"),
         new("environmental", "subject", "an environmental concern"), new("unrelated", "subject", "a matter unrelated to health or environment"),
         new("gastrointestinal", "presentation", "diarrhoea, vomiting or abdominal symptoms"),
         new("respiratory", "presentation", "cough or breathing symptoms"), new("febrile", "presentation", "fever"),
         new("neurological", "presentation", "confusion, seizures or neurological symptoms"),
         new("rash-fever", "presentation", "rash with fever"),
         new("water-exposure", "exposure", "reported contact with potentially contaminated water"),
         new("food-exposure", "exposure", "reported exposure through food"),
         new("animal-exposure", "exposure", "reported contact with animals")]);

    public void Validate()
    {
        Profile.Validate();
        if (LeaseSeconds is < 30 or > 600 || MaximumAttempts is < 1 or > 8 ||
            string.IsNullOrWhiteSpace(WorkerId) || WorkerId.Length > 100 ||
            Organizations.Length is < 1 or > 100 || Organizations.Any(string.IsNullOrWhiteSpace) ||
            WorkerTokenSha256.Length != 64 || !WorkerTokenSha256.All(char.IsAsciiHexDigit))
            throw new InvalidOperationException("Classification requires a scoped worker credential and bounded configuration.");
    }
}
