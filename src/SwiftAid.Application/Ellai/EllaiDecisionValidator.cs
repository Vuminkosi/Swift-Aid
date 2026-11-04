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

        if (!Enum.IsDefined(decision.Kind))
        {
            errors.Add("Decision kind is unsupported.");
        }

        if (string.IsNullOrWhiteSpace(decision.DisplayText) || decision.DisplayText.Length > MaxDisplayChars)
        {
            errors.Add("DisplayText must be 1..2000 characters.");
        }

        if (decision.SpokenText is null || CountWords(decision.SpokenText) > MaxSpokenWords)
        {
            errors.Add("SpokenText must be a string of at most 120 words.");
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

            if (decision.Questions.Any(question => question is null || string.IsNullOrWhiteSpace(question.QuestionId) ||
                string.IsNullOrWhiteSpace(question.Text) || question.Text.Length > 1000 || question.ChoiceIds is null ||
                question.ChoiceIds.Count > 10 || question.ChoiceIds.Any(string.IsNullOrWhiteSpace)))
            {
                errors.Add("Questions must have an identifier, bounded text and a valid choice array.");
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
        if (decision.PlanProposal is { } plan &&
            (string.IsNullOrWhiteSpace(plan.Objective) || plan.Actions is { Count: 0 } ||
             plan.Actions is not null && plan.Actions.Any(action => action is null ||
                string.IsNullOrWhiteSpace(action.ProposalLabel) || !Enum.IsDefined(action.Kind) ||
                action.Obligation is null || string.IsNullOrWhiteSpace(action.FactualRationale) ||
                action.Evidence is null || action.Evidence.Count == 0 || action.Evidence.Any(reference => reference is null))))
        {
            errors.Add("Plan proposals require an objective and valid evidence-backed actions.");
        }

        if (decision.TopicRefs is null)
        {
            errors.Add("TopicRefs must be an array.");
        }
        else if (decision.TopicRefs.Any(string.IsNullOrWhiteSpace))
        {
            errors.Add("TopicRefs entries must be nonempty strings.");
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

                if (string.IsNullOrWhiteSpace(claim.ClaimId) || string.IsNullOrWhiteSpace(claim.Text))
                {
                    errors.Add("Claims require an identifier and text.");
                }
                if (!Enum.IsDefined(claim.Kind))
                {
                    errors.Add("Claims require a supported kind.");
                }
                if (claim.Evidence is null || claim.Evidence.Any(reference => reference is null))
                {
                    errors.Add("Claim evidence must be an array of references.");
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
            try { EllaiGuidanceSequence.Order(draft.Steps); }
            catch (ArgumentException) { errors.Add("Guidance steps must form one connected, acyclic sequence."); }
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
