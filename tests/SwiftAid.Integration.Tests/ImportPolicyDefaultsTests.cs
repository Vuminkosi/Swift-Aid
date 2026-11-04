using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using SwiftAid.Client.Presentation;
using SwiftAid.Contracts.Intake;
using SwiftAid.Gateway.Presentation;
using SwiftAid.Infrastructure.Persistence.Repositories.Intake;

namespace SwiftAid.Integration.Tests;

public sealed class ImportPolicyDefaultsTests
{
    [Fact]
    public async Task Gateway_defaults_expose_a_policy_that_accepts_matching_import_confirmations()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root.Parent is not null && !File.Exists(Path.Combine(root.FullName, "spec.md"))) root = root.Parent;
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(root.FullName, "src", "SwiftAid.Gateway", "appsettings.json"))
            .Build();
        var options = Options.Create(configuration.GetSection(IntakeConsentOptions.SectionName).Get<IntakeConsentOptions>()!);
        Assert.True(options.Value.IsValid);
        var exposed = await new GatewayPublicIntakeProcessingPolicyClient(options).GetPolicyAsync();
        Assert.True(exposed.Succeeded);
        Assert.Equal(UiCapabilityProvenance.LiveService, exposed.Metadata.Provenance);
        var policy = Assert.IsType<SwiftAid.Client.Presentation.PublicWorkflows.PublicReportProcessingPolicy>(exposed.Value);
        var confirmations = new IntakeConfirmations(true, "ZW", "fixture", true, true,
            policy.Reference, policy.Version, null, null, null);
        var validator = new ConfiguredIntakeConsentPolicy(options);
        Assert.True(await validator.IsCurrentAsync(confirmations, false, default));
        Assert.False(await validator.IsCurrentAsync(confirmations with { ProcessingPolicyVersion = "stale" }, false, default));
        Assert.False(await validator.IsCurrentAsync(confirmations with { ProcessingConfirmed = false }, false, default));
        Assert.False(await validator.IsCurrentAsync(confirmations, true, default));
        Assert.True(await validator.IsCurrentAsync(confirmations with
        {
            RecordingConfirmed = true,
            RecordingPolicyReference = options.Value.RecordingPolicyReference,
            RecordingPolicyVersion = options.Value.RecordingPolicyVersion
        }, true, default));
    }
}
