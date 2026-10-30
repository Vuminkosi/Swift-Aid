using SwiftAid.Application.Ellai;
using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Application.Tests.Ellai;

[Trait("Feature", "Ellai")]
[Trait("Task", "ELL-T02")]
public sealed class EllaiContractTests
{
    [Fact]
    public void RejectsUnknownIntentArgumentsAndAuthorityFields()
    {
        var decision = new EllaiDecision(
            99, DecisionKind.ProposeUi, "x", "x",
            Array.Empty<GroundedClaim>(), Array.Empty<EllaiQuestion>(),
            Array.Empty<UiIntentProposal>(), null,
            Array.Empty<string>(), null, null);

        var errors = EllaiDecisionValidator.Validate(decision);

        Assert.Contains(errors, e => e.Contains("schemaVersion", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RejectsOversizedAndMismatchedDecisions()
    {
        var tooManyIntents = Enumerable.Range(0, 4).Select(i => new UiIntentProposal(
            UiIntentKind.HighlightControl, "cases", null, "c",
            new Dictionary<string, string>())).ToList();

        var decision = new EllaiDecision(
            2, DecisionKind.ProposeUi, new string('x', 2001), "ok",
            Array.Empty<GroundedClaim>(), Array.Empty<EllaiQuestion>(),
            tooManyIntents, null, Array.Empty<string>(), null, null);

        var errors = EllaiDecisionValidator.Validate(decision);

        Assert.Contains(errors, e => e.Contains("DisplayText", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(errors, e => e.Contains("3 UI intents", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void NeedInputRequiresQuestionsAndProposePlanRequiresPlan()
    {
        var needInput = new EllaiDecision(
            2, DecisionKind.NeedInput, "q", "q",
            Array.Empty<GroundedClaim>(), Array.Empty<EllaiQuestion>(),
            Array.Empty<UiIntentProposal>(), null, Array.Empty<string>(), null, null);

        Assert.Contains(
            EllaiDecisionValidator.Validate(needInput),
            e => e.Contains("question", StringComparison.OrdinalIgnoreCase));

        var planless = needInput with { Kind = DecisionKind.ProposePlan };
        Assert.Contains(
            EllaiDecisionValidator.Validate(planless),
            e => e.Contains("plan proposal", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ObservedClaimWithoutEvidenceIsRejected()
    {
        var claim = new GroundedClaim("c1", "created task", ClaimKind.Observed, Array.Empty<EvidenceRef>());
        var decision = new EllaiDecision(
            2, DecisionKind.Answer, "a", "a",
            new[] { claim }, Array.Empty<EllaiQuestion>(),
            Array.Empty<UiIntentProposal>(), null, Array.Empty<string>(), null, null);

        Assert.Contains(
            EllaiDecisionValidator.Validate(decision),
            e => e.Contains("c1", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PlanDraftRejectsBadStepGraph()
    {
        var draft = new EllaiGuidancePlanDraft(
            1, GuidancePlanStatus.Ready, "goal", GuidanceMode.Navigation,
            new[]
            {
                new GuidancePlanStep("s1", "c1", "o1", PlanStepBehavior.Explain,
                    null, null, Array.Empty<string>(), Array.Empty<string>(), "ok", "missing"),
                new GuidancePlanStep("s1", "c2", "o2", PlanStepBehavior.GuideHuman,
                    "binding-x", null, Array.Empty<string>(), Array.Empty<string>(), "ok", null),
            },
            Array.Empty<EllaiQuestion>(), null);

        var errors = EllaiDecisionValidator.ValidateDraft(draft);

        Assert.Contains(errors, e => e.Contains("Duplicate stepKey", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(errors, e => e.Contains("unknown nextStepKey", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(errors, e => e.Contains("only RequestUiIntent", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ValidDecisionAndDraftPass()
    {
        var decision = new EllaiDecision(
            2, DecisionKind.Answer, "Here is help.", "Here is help.",
            new[]
            {
                new GroundedClaim("c1", "Queue has 3 items.", ClaimKind.Observed,
                    new[] { new EvidenceRef("queue", "q1", "v3", null, "e1") })
            },
            Array.Empty<EllaiQuestion>(), Array.Empty<UiIntentProposal>(),
            null, Array.Empty<string>(), null, null);

        Assert.Empty(EllaiDecisionValidator.Validate(decision));

        var draft = new EllaiGuidancePlanDraft(
            1, GuidancePlanStatus.Ready, "goal", GuidanceMode.Navigation,
            new[]
            {
                new GuidancePlanStep("s1", "cases.queue", "view-queue", PlanStepBehavior.Explain,
                    null, null, Array.Empty<string>(), Array.Empty<string>(), "Open the queue.", "s2"),
                new GuidancePlanStep("s2", "cases.queue", "open-first", PlanStepBehavior.GuideHuman,
                    null, null, Array.Empty<string>(), Array.Empty<string>(), "Open the first item.", null),
            },
            Array.Empty<EllaiQuestion>(), null);

        Assert.Empty(EllaiDecisionValidator.ValidateDraft(draft));
    }
}
