using SwiftAid.Application.Ellai;
using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Application.Tests.Ellai;

[Trait("Feature", "Ellai")]
[Trait("Task", "ELL-T07")]
public sealed class EllaiBrokerBoundsTests
{
    private static readonly EllaiToolContext Context = new(
        "session-1", "epoch-1", "cases", "case-1", 4, true, "consent/1", EllaiProcessingClassification.SyntheticDemo);

    private static readonly IReadOnlyDictionary<string, string> NoArgs =
        new Dictionary<string, string>();

    private static EllaiToolBroker GrantedBroker()
    {
        var consent = new EllaiConsentService();
        consent.Grant("session-1", EllaiConsentDimension.ReasoningText,
            EllaiToolBroker.ToolReadPurpose, EllaiProcessingClassification.SyntheticDemo, TimeSpan.FromHours(1));
        return new EllaiToolBroker(new EllaiPolicyService(), consent);
    }

    private static EllaiToolResult EmptyOk() => new(
        EllaiToolNames.State, true, null, Array.Empty<EvidenceRef>(),
        string.Empty, 0, false, EllaiToolReasonCodes.Ok);

    [Fact]
    public async Task MissingConsentInfrastructureFailsClosed()
    {
        Assert.Throws<ArgumentNullException>(
            () => new EllaiToolBroker(new EllaiPolicyService(), null!));
        Assert.Throws<ArgumentNullException>(
            () => new EllaiToolBroker(null!, new EllaiConsentService()));

        var consent = new EllaiConsentService();
        var broker = new EllaiToolBroker(new EllaiPolicyService(), consent);
        var denied = await broker.InvokeAsync(
            Context, new EllaiToolRequest(EllaiToolNames.State, NoArgs),
            CancellationToken.None);
        Assert.False(denied.Allowed);
        Assert.Equal(EllaiToolReasonCodes.ConsentMissing, denied.ReasonCode);
    }

    [Fact]
    public async Task OrdinaryTypedMessageCanUseItsAllowedPublicFactToolWithoutConsentGrant()
    {
        var broker = new EllaiToolBroker(new EllaiPolicyService(), new EllaiConsentService());
        var context = Context with
        {
            ConsentPurpose = EllaiConsentService.PublicProductHelpPurpose,
            ConsentVersion = "consent/0",
            DataClassification = EllaiProcessingClassification.UserProvidedNonSensitive,
            AllowedTools = new HashSet<string>(StringComparer.Ordinal) { EllaiToolNames.ProductFacts },
        };

        var result = await broker.InvokeAsync(context,
            new EllaiToolRequest(EllaiToolNames.ProductFacts,
                new Dictionary<string, string> { ["topic"] = "guest" }),
            CancellationToken.None);

        Assert.True(result.Allowed);
        Assert.Contains(result.Evidence, evidence => evidence.SourceKind == "ProductFact");
    }

    [Fact]
    public async Task DenialsCarryMachineReadableReasonCodes()
    {
        var broker = GrantedBroker();

        var unregistered = await broker.InvokeAsync(
            Context, new EllaiToolRequest("ellai_sql", NoArgs), CancellationToken.None);
        Assert.Equal(EllaiToolReasonCodes.UnregisteredTool, unregistered.ReasonCode);
        Assert.False(string.IsNullOrWhiteSpace(unregistered.DenialReason));

        var budget = await broker.InvokeAsync(
            Context with { RemainingCalls = 0 },
            new EllaiToolRequest(EllaiToolNames.State, NoArgs), CancellationToken.None);
        Assert.Equal(EllaiToolReasonCodes.BudgetExceeded, budget.ReasonCode);

        var scope = await broker.InvokeAsync(
            Context with { ScopeEpoch = string.Empty },
            new EllaiToolRequest(EllaiToolNames.State, NoArgs), CancellationToken.None);
        Assert.Equal(EllaiToolReasonCodes.MissingScope, scope.ReasonCode);

        var args = await broker.InvokeAsync(
            Context,
            new EllaiToolRequest(EllaiToolNames.State,
                Enumerable.Range(0, 9).ToDictionary(i => $"k{i}", i => "v")),
            CancellationToken.None);
        Assert.Equal(EllaiToolReasonCodes.InvalidArguments, args.ReasonCode);
    }

    [Fact]
    public async Task BrokerRejectsToolArgumentsOutsideDeclaredHandlerConstraints()
    {
        var broker = GrantedBroker();
        var invalid = await broker.InvokeAsync(
            Context,
            new EllaiToolRequest(EllaiToolNames.State,
                new Dictionary<string, string> { ["projection"] = "private" }),
            CancellationToken.None);

        Assert.False(invalid.Allowed);
        Assert.Equal(EllaiToolReasonCodes.InvalidArguments, invalid.ReasonCode);
    }

    [Fact]
    public async Task SessionToolAllowlistHidesAndDeniesUnlistedTools()
    {
        var broker = GrantedBroker();
        var profile = Context with
        {
            AllowedTools = new HashSet<string>(StringComparer.Ordinal)
            {
                EllaiToolNames.ProductFacts,
            },
        };

        var catalogue = EllaiToolBroker.DescribeCatalogue(profile.AllowedTools);
        Assert.Contains(EllaiToolNames.ProductFacts, catalogue, StringComparison.Ordinal);
        Assert.DoesNotContain(EllaiToolNames.State, catalogue, StringComparison.Ordinal);
        Assert.DoesNotContain(EllaiToolNames.Evidence, catalogue, StringComparison.Ordinal);

        var denied = await broker.InvokeAsync(
            profile, new EllaiToolRequest(EllaiToolNames.State, NoArgs), CancellationToken.None);
        Assert.False(denied.Allowed);
        Assert.Equal(EllaiToolReasonCodes.DeniedByPolicy, denied.ReasonCode);

        var deniedAlias = await broker.InvokeAsync(
            profile, new EllaiToolRequest("ellai_guidance_session", NoArgs), CancellationToken.None);
        Assert.False(deniedAlias.Allowed);
        Assert.Equal(EllaiToolReasonCodes.DeniedByPolicy, deniedAlias.ReasonCode);

        var allowed = await broker.InvokeAsync(
            profile,
            new EllaiToolRequest(EllaiToolNames.ProductFacts,
                new Dictionary<string, string> { ["topic"] = "guest" }),
            CancellationToken.None);
        Assert.True(allowed.Allowed);
        Assert.Contains(allowed.Evidence, evidence => evidence.SourceKind == "ProductFact");
    }

    [Fact]
    public void ByteBoundaryIsEnforcedOnSerializedEnvelope()
    {
        var head = EllaiToolBroker.MeasureEnvelopeBytes(EmptyOk());
        Assert.True(head < EllaiToolBroker.MaxResultBytes);

        var fits = EmptyOk() with
        {
            ResultSummary = new string('a', EllaiToolBroker.MaxResultBytes - head)
        };
        Assert.Equal(EllaiToolBroker.MaxResultBytes, EllaiToolBroker.MeasureEnvelopeBytes(fits));
        Assert.True(EllaiToolBroker.ApplyBounds(fits).Allowed);

        var over = EmptyOk() with
        {
            ResultSummary = new string('a', EllaiToolBroker.MaxResultBytes - head + 1)
        };
        var denied = EllaiToolBroker.ApplyBounds(over);
        Assert.False(denied.Allowed);
        Assert.Equal(EllaiToolReasonCodes.ResultTooLarge, denied.ReasonCode);
        Assert.False(string.IsNullOrWhiteSpace(denied.DenialReason));
    }

    [Fact]
    public void MultibyteSummaryCountsSerializedBytesNotChars()
    {
        var head = EllaiToolBroker.MeasureEnvelopeBytes(EmptyOk());
        var pad = EllaiToolBroker.MaxResultBytes - head;

        var ascii = EmptyOk() with { ResultSummary = new string('a', pad) };
        Assert.True(EllaiToolBroker.ApplyBounds(ascii).Allowed);

        // Same char count, more serialized bytes: must be denied.
        var wide = EmptyOk() with { ResultSummary = new string('\u00E9', pad) };
        Assert.True(EllaiToolBroker.MeasureEnvelopeBytes(wide) > wide.ResultSummary.Length);
        var denied = EllaiToolBroker.ApplyBounds(wide);
        Assert.False(denied.Allowed);
        Assert.Equal(EllaiToolReasonCodes.ResultTooLarge, denied.ReasonCode);
    }

    [Fact]
    public async Task CompatibilityAliasesShareCanonicalAuthorizationAndBounds()
    {
        var broker = GrantedBroker();
        var cases = new[]
        {
            ("ellai_voice_capability", EllaiToolNames.Capabilities),
            ("ellai_guidance_session", EllaiToolNames.State),
        };

        foreach (var (alias, canonical) in cases)
        {
            var viaAlias = await broker.InvokeAsync(
                Context, new EllaiToolRequest(alias, NoArgs), CancellationToken.None);
            var viaCanonical = await broker.InvokeAsync(
                Context, new EllaiToolRequest(canonical, NoArgs), CancellationToken.None);

            Assert.True(viaAlias.Allowed);
            Assert.Equal(EllaiToolReasonCodes.Ok, viaAlias.ReasonCode);
            Assert.Equal(alias, viaAlias.ToolName);
            Assert.Equal(viaCanonical.Allowed, viaAlias.Allowed);
            Assert.Equal(viaCanonical.ItemCount, viaAlias.ItemCount);
            Assert.True(viaAlias.ItemCount <= EllaiToolBroker.MaxItems);
            Assert.True(EllaiToolBroker.MeasureEnvelopeBytes(viaAlias) <= EllaiToolBroker.MaxResultBytes);
        }
    }

    [Fact]
    public void CompatibilityAliasesAreAcceptedButNotAdvertised()
    {
        Assert.False(EllaiToolNames.All.Contains("ellai_voice_capability"));
        Assert.False(EllaiToolNames.All.Contains("ellai_guidance_session"));
        Assert.Equal(19, EllaiToolNames.All.Count);
    }

    [Fact]
    public async Task NowToolReportsCurrentUtcTime()
    {
        var broker = GrantedBroker();
        var before = DateTimeOffset.UtcNow;

        var result = await broker.InvokeAsync(
            Context, new EllaiToolRequest(EllaiToolNames.Now, NoArgs),
            CancellationToken.None);

        var after = DateTimeOffset.UtcNow;
        Assert.True(result.Allowed);
        Assert.Equal(EllaiToolReasonCodes.Ok, result.ReasonCode);
        Assert.Equal(EllaiToolNames.Now, result.ToolName);

        var reported = DateTimeOffset.Parse(
            result.ResultSummary.Split("UTC): ", StringSplitOptions.None)[1].Split(' ')[0],
            null, System.Globalization.DateTimeStyles.RoundtripKind);
        Assert.True(reported >= before.AddSeconds(-5) && reported <= after.AddSeconds(5));
        Assert.True(EllaiToolBroker.MeasureEnvelopeBytes(result) <= EllaiToolBroker.MaxResultBytes);
    }
}
