using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Application.Ellai;

/// <summary>Training verification request: names an approved objective and the
/// current attempt's proof — never a client-asserted success.</summary>
public sealed record TrainingVerificationRequest(
    string AttemptId,
    string ObjectiveId,
    string TaskVersion,
    GuidanceProofKind PresentedKind,
    IReadOnlyList<EvidenceRef> ProofRefs,
    GuidanceProofKind RequiredKind,
    bool CapabilityAvailable);

/// <summary>Deterministic training verifier (ELL-T31 groundwork). Only
/// registered verifiers prove task completion: narration, clicks or a model's
/// self-assessment never verify. No clinical competence is ever graded.</summary>
public sealed class EllaiTrainingVerifier
{
    public const string VerifierVersion = "ellai-training-verifier/1.0";

    public TrainingTaskResult Verify(TrainingVerificationRequest request, string actorScope, string taskVersion)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!request.CapabilityAvailable)
        {
            return new TrainingTaskResult(request.AttemptId, request.ObjectiveId, taskVersion,
                "blocked", request.PresentedKind, Array.Empty<EvidenceRef>(),
                VerifierVersion, Array.Empty<string>());
        }

        if (request.PresentedKind != request.RequiredKind || request.ProofRefs.Count == 0)
        {
            return new TrainingTaskResult(request.AttemptId, request.ObjectiveId, taskVersion,
                "not-satisfied", request.PresentedKind, request.ProofRefs,
                VerifierVersion, request.ProofRefs.Select(r => r.Version).ToList());
        }

        return new TrainingTaskResult(request.AttemptId, request.ObjectiveId, taskVersion,
            "verified", request.PresentedKind, request.ProofRefs,
            VerifierVersion, request.ProofRefs.Select(r => r.Version).ToList());
    }
}
