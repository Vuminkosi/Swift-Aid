namespace SwiftAid.Contracts.Intake;

/// <summary>Explicit permission for this session to send report speech text for extraction.</summary>
public sealed record ReportVoiceConsentRequest(bool Confirmed);

public sealed record ReportVoiceExtractionRequest(string? Transcript);

/// <summary>Unconfirmed model suggestion with words from the reporter's transcript.</summary>
public sealed record ReportVoiceSuggestion(string Field, string Value, string Evidence);

public sealed record ReportVoiceExtractionResponse(string Status, IReadOnlyList<ReportVoiceSuggestion> Suggestions);
