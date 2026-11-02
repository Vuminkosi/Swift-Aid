using System.Security.Claims;
using System.Text.Json;
using SwiftAid.Application.Authorization;

namespace SwiftAid.Application.Ellai;

/// <summary>Versioned durable serialization for accepted Ellai work. The
/// actor is restored from its server-captured identity and epoch; no request
/// body fields are used to create trusted authority.</summary>
public sealed record EllaiUtteranceWorkEnvelope(
    int SchemaVersion,
    string SessionId,
    string ClientUtteranceId,
    string ScopeEpoch,
    string Utterance,
    string FeatureId,
    string? ResourceId,
    SwiftAid.Contracts.Ellai.EllaiMode Mode,
    long ControllerEpoch,
    string CycleId,
    string ActorId,
    long AccessEpoch,
    bool ContributorEligible,
    string ConsentVersion,
    string ProcessingLabel,
    string DataClassification,
    string ConsentPurpose,
    string[] AllowedTools,
    SwiftAid.Contracts.Ellai.EllaiLanguagePreferences? Languages = null,
    string? ActivePage = null,
    string InteractionMode = "typed")
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string Serialize(EllaiUtteranceWork work, string clientUtteranceId)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientUtteranceId);
        if (string.IsNullOrWhiteSpace(work.Utterance) || work.Utterance.Length > 8000)
        {
            throw new ArgumentException("Persisted Ellai work must contain a bounded sanitized prompt.", nameof(work));
        }

        var envelope = new EllaiUtteranceWorkEnvelope(
            1,
            work.SessionId,
            clientUtteranceId,
            work.ScopeEpoch,
            work.Utterance,
            work.FeatureId,
            work.ResourceId,
            work.Mode,
            work.ControllerEpoch,
            work.CycleId,
            work.Actor.ActorId,
            work.Actor.AccessEpoch,
            work.ContributorEligible,
            work.ConsentVersion,
            work.ProcessingLabel,
            work.DataClassification,
            work.ConsentPurpose,
            work.AllowedTools?.Order(StringComparer.Ordinal).ToArray() ?? [],
            work.Languages,
            work.ActivePage, work.InteractionMode);
        return JsonSerializer.Serialize(envelope, Json);
    }

    public EllaiUtteranceWork ToWork()
    {
        if (!EllaiInteractionContext.IsValid(InteractionMode)
            || SchemaVersion != 1
            || string.IsNullOrWhiteSpace(SessionId)
            || string.IsNullOrWhiteSpace(ClientUtteranceId)
            || string.IsNullOrWhiteSpace(ScopeEpoch)
            || string.IsNullOrWhiteSpace(Utterance)
            || Utterance.Length > 8000
            || string.IsNullOrWhiteSpace(FeatureId)
            || FeatureId.Length > 120
            || ResourceId is { Length: > 120 }
            || ControllerEpoch < 0
            || string.IsNullOrWhiteSpace(CycleId)
            || string.IsNullOrWhiteSpace(ActorId)
            || AccessEpoch < 0
            || string.IsNullOrWhiteSpace(ConsentVersion)
            || string.IsNullOrWhiteSpace(ProcessingLabel)
            || string.IsNullOrWhiteSpace(DataClassification)
            || string.IsNullOrWhiteSpace(ConsentPurpose)
            || AllowedTools is null
            || AllowedTools.Length > 32
            || AllowedTools.Any(string.IsNullOrWhiteSpace)
            || Languages is not null && !Languages.IsValid())
        {
            throw new InvalidOperationException("The persisted Ellai work envelope is invalid.");
        }

        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, ActorId),
            new Claim(ActorContext.AccessEpochClaimType,
                AccessEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        ], authenticationType: "ellai-outbox"));
        var actor = ActorContext.FromValidatedPrincipal(principal);
        return new EllaiUtteranceWork(
            SessionId,
            ScopeEpoch,
            Utterance,
            FeatureId,
            ResourceId,
            Mode,
            ControllerEpoch,
            CycleId,
            actor,
            ContributorEligible,
            ConsentVersion,
            ProcessingLabel,
            DataClassification,
            new HashSet<string>(AllowedTools, StringComparer.Ordinal),
            ConsentPurpose,
            ClientUtteranceId,
            Languages,
            ActivePage, InteractionMode);
    }

    public static EllaiUtteranceWorkEnvelope Deserialize(string payloadJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadJson);
        if (System.Text.Encoding.UTF8.GetByteCount(payloadJson) > 16 * 1024)
        {
            throw new InvalidOperationException("The persisted Ellai work payload exceeds its storage bound.");
        }

        return JsonSerializer.Deserialize<EllaiUtteranceWorkEnvelope>(payloadJson, Json)
            ?? throw new InvalidOperationException("The persisted Ellai work payload is empty.");
    }
}
