using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Application.Ellai;

/// <summary>Structural + semantic validation for EllaiDecision v2 payloads.
/// Fail-closed: any unknown field, authority smuggling, or invented
/// evidence fails validation. (ELL-T05 groundwork; full prompt binding in T05.)</summary>
public static class EllaiDecisionValidator
{
    public const int MaxDisplayChars = 2000;

    public const int MaxSpokenWords = 120;

    public const int MaxUiIntents = 3;

    public const int MaxPlanActions = 3;

    public const int MaxQuestions = 3;

    public static IReadOnlyList<string> Validate(EllaiDecision decision)
    {
        var errors = new List<string>();
        if (decision.SchemaVersion != EllaiDecision.CurrentSchemaVersion)
        {
            errors.Add($"Unsupported schemaVersion {decision.SchemaVersion}; expected {EllaiDecision.CurrentSchemaVersion}.");
            return errors;
        }

        if (string.IsNullOrWhiteSpace(decision.DisplayText) || decision.DisplayText.Length > MaxDisplayChars)
        {
            errors.Add("DisplayText must be 1..2000 characters.");
        }

        if (CountWords(decision.SpokenText) > MaxSpokenWords)
        {
            errors.Add("SpokenText exceeds 120 words.");
        }

        if (decision.UiIntents is null)
        {
            errors.Add("UiIntents must be an array.");
        }
        else if (decision.UiIntents.Count > MaxUiIntents)
        {
            errors.Add("At most 3 UI intents per decision.");
        }

        if (decision.UiIntents is not null)
        {
            try { _ = EllaiUiProposalPayload.Serialize(decision.UiIntents); }
            catch (ArgumentException) { errors.Add("UI proposals must have a valid bounded shape."); }
        }

        if (decision.Questions is null)
        {
            errors.Add("Questions must be an array.");
        }
        else
        {
            if (decision.Questions.Count > MaxQuestions)
            {
                errors.Add("At most 3 clarifying questions per decision.");
            }

            if (decision.Questions.Any(question => question is null))
            {
                errors.Add("Questions entries must be objects.");
            }
        }

        if (decision.PlanProposal is not null && decision.PlanProposal.Actions is null)
        {
            errors.Add("Plan actions must be an array.");
        }
        else if (decision.PlanProposal is not null && decision.PlanProposal.Actions.Count > MaxPlanActions)
        {
            errors.Add("At most 3 administrative actions per plan.");
        }

        if (decision.TopicRefs is null)
        {
            errors.Add("TopicRefs must be an array.");
        }

        if (decision.Kind == DecisionKind.NeedInput && decision.Questions is { Count: 0 })
        {
            errors.Add("NeedInput requires 1..3 questions.");
        }

        if (decision.Kind == DecisionKind.ProposePlan && decision.PlanProposal is null)
        {
            errors.Add("ProposePlan requires a plan proposal.");
        }

        if (decision.Claims is null)
        {
            errors.Add("Claims must be an array.");
        }
        else
        {
            foreach (var claim in decision.Claims)
            {
                if (claim is null)
                {
                    errors.Add("Claims entries must be objects.");
                    continue;
                }

                if (claim.Kind is not ClaimKind.Unknown
                    && claim.Kind is not ClaimKind.Proposed
                    && (claim.Evidence is null || claim.Evidence.Count == 0))
                {
                    errors.Add($"Claim {claim.ClaimId} of kind {claim.Kind} requires evidence references.");
                }
            }
        }

        return errors;
    }

    public static IReadOnlyList<string> ValidateDraft(EllaiGuidancePlanDraft draft)
    {
        var errors = new List<string>();
        if (draft is null || draft.Steps is null || draft.Questions is null)
        {
            return ["A guidance draft requires steps and questions arrays."];
        }
        if (!Enum.IsDefined(draft.Status) || !Enum.IsDefined(draft.Mode))
        {
            return ["The guidance status or mode is unsupported."];
        }
        if (string.IsNullOrWhiteSpace(draft.Objective) || draft.Objective.Length > 2000 ||
            draft.Questions.Count > MaxQuestions || draft.Questions.Any(q => q is null || string.IsNullOrWhiteSpace(q.QuestionId) ||
                string.IsNullOrWhiteSpace(q.Text) || q.Text.Length > 1000 || q.ChoiceIds is null || q.ChoiceIds.Count > 10))
            return ["Guidance requires a bounded objective and valid questions."];
        if (draft.Steps.Any(step => step is null || string.IsNullOrWhiteSpace(step.StepKey)
            || string.IsNullOrWhiteSpace(step.ControlId) || string.IsNullOrWhiteSpace(step.Explanation)
            || string.IsNullOrWhiteSpace(step.ObjectiveId) || step.StepKey.Length > 120 || step.ControlId.Length > 120
            || step.FactIds is null || step.EvidenceIds is null || step.FactIds.Count > 20 || step.EvidenceIds.Count > 20
            || !Enum.IsDefined(step.Behavior)))
        {
            return ["Guidance steps require valid identifiers, explanations and behaviors."];
        }
        if (draft.SchemaVersion != EllaiGuidancePlanDraft.CurrentSchemaVersion)
        {
            errors.Add($"Unsupported plan schemaVersion {draft.SchemaVersion}; expected {EllaiGuidancePlanDraft.CurrentSchemaVersion}.");
            return errors;
        }

        if (draft.Status == GuidancePlanStatus.Ready)
        {
            if (draft.Steps.Count is < 1 or > EllaiGuidancePlanDraft.MaxStepsPerChunk)
            {
                errors.Add("Ready drafts require 1..8 steps.");
            }

            if (draft.Questions.Count != 0)
            {
                errors.Add("Ready drafts must not carry clarification questions.");
            }
        }
        else if (draft.Status == GuidancePlanStatus.NeedsInput)
        {
            if (draft.Steps.Count != 0)
            {
                errors.Add("NeedsInput drafts must not carry executable steps.");
            }

            if (draft.Questions.Count is < 1 or > MaxQuestions)
            {
                errors.Add("NeedsInput drafts require 1..3 questions.");
            }
        }
        else if (draft.Steps.Count != 0)
        {
            errors.Add($"{draft.Status} drafts must not carry executable steps.");
        }

        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var step in draft.Steps)
        {
            if (!keys.Add(step.StepKey))
            {
                errors.Add($"Duplicate stepKey {step.StepKey}.");
            }

            if (step.Explanation.Length > EllaiGuidancePlanDraft.MaxExplanationChars)
            {
                errors.Add($"Step {step.StepKey} explanation exceeds 1000 characters.");
            }

            if (step.Behavior == PlanStepBehavior.RequestUiIntent && string.IsNullOrWhiteSpace(step.UiIntentBindingId))
            {
                errors.Add($"Step {step.StepKey} RequestUiIntent requires uiIntentBindingId.");
            }

            if (step.Behavior != PlanStepBehavior.RequestUiIntent && step.UiIntentBindingId is not null)
            {
                errors.Add($"Step {step.StepKey} only RequestUiIntent may carry uiIntentBindingId.");
            }

            if (step.NextStepKey is not null && step.NextStepKey == step.StepKey)
            {
                errors.Add($"Step {step.StepKey} nextStepKey must not be self-referential.");
            }
        }

        foreach (var step in draft.Steps)
        {
            if (step.NextStepKey is not null && !keys.Contains(step.NextStepKey))
            {
                errors.Add($"Step {step.StepKey} references unknown nextStepKey {step.NextStepKey}.");
            }
        }

        if (errors.Count == 0 && draft.Steps.Count > 0)
        {
            var byKey = draft.Steps.ToDictionary(step => step.StepKey, StringComparer.Ordinal);
            var visited = new HashSet<string>(StringComparer.Ordinal);
            string? next = draft.Steps[0].StepKey;
            while (next is not null && visited.Add(next)) next = byKey[next].NextStepKey;
            if (next is not null || visited.Count != draft.Steps.Count)
                errors.Add("Guidance steps must form one connected, acyclic sequence.");
        }

        return errors;
    }

    private static int CountWords(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        return text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
    }
}
