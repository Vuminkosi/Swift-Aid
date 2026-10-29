using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MudBlazor.Services;
using SwiftAid.UI.Components.Onboarding;

namespace SwiftAid.Ui.Tests;

public sealed class OnboardingFoundationTests
{
    [Fact]
    public void ParticipationSelection_GrantsNoPermissions()
    {
        Assert.False(ParticipationPreferences.SelectionGrantsPermissions);
        Assert.Equal(5, ParticipationPreferences.All.Count);
    }

    [Fact]
    public void FlowState_GuestEntrySurvivesUntilAuthorizedLanding()
    {
        Assert.True(OnboardingFlowRoutes.KeepsGuestEntryAvailable(OnboardingFlowState.NotStarted));
        Assert.True(OnboardingFlowRoutes.KeepsGuestEntryAvailable(OnboardingFlowState.IntroInProgress));
        Assert.True(OnboardingFlowRoutes.KeepsGuestEntryAvailable(OnboardingFlowState.PublicEntryReady));
        Assert.False(OnboardingFlowRoutes.KeepsGuestEntryAvailable(OnboardingFlowState.Completed));
    }

    [Fact]
    public void FlowState_SuggestsSafeFallbackForUnknownStates()
    {
        Assert.Equal("/", OnboardingFlowRoutes.SuggestNextHref((OnboardingFlowState)999));
        Assert.Equal("/welcome", OnboardingFlowRoutes.SuggestNextHref(OnboardingFlowState.NotStarted));
    }

    [Fact]
    public async Task Stepper_MarksCurrentStepForAssistiveTechnology()
    {
        var markup = await RenderStepperAsync(currentStep: 2, totalSteps: 4);

        Assert.Contains("aria-label=\"Onboarding progress\"", markup, StringComparison.Ordinal);
        Assert.Contains("aria-current=\"step\"", markup, StringComparison.Ordinal);
        Assert.Contains("Step 2 of 4, current step", markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stepper_ClampsOutOfRangeProgress()
    {
        var markup = await RenderStepperAsync(currentStep: 9, totalSteps: 4);

        Assert.Contains("Step 4 of 4, current step", markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Shell_RendersBrandHeadingAndLegalLinks()
    {
        await using var services = CreateServices();
        await using var renderer = CreateRenderer(services);

        var markup = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<OnboardingShell>(
                ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    [nameof(OnboardingShell.Title)] = "Welcome back",
                    [nameof(OnboardingShell.Eyebrow)] = "Your SwiftAid account",
                    [nameof(OnboardingShell.HeadingId)] = "onboarding-test-heading"
                }));
            return component.ToHtmlString();
        });

        Assert.Contains("SwiftAid", markup, StringComparison.Ordinal);
        Assert.Contains("id=\"onboarding-test-heading\"", markup, StringComparison.Ordinal);
        Assert.Contains("/privacy", markup, StringComparison.Ordinal);
        Assert.Contains("/terms", markup, StringComparison.Ordinal);
    }

    private static Task<string> RenderStepperAsync(int currentStep, int totalSteps)
    {
        return RenderAsync<OnboardingStepper>(new Dictionary<string, object?>
        {
            [nameof(OnboardingStepper.CurrentStep)] = currentStep,
            [nameof(OnboardingStepper.TotalSteps)] = totalSteps
        });
    }

    private static async Task<string> RenderAsync<TComponent>(Dictionary<string, object?> parameters)
        where TComponent : IComponent
    {
        await using var services = CreateServices();
        await using var renderer = CreateRenderer(services);
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<TComponent>(
                ParameterView.FromDictionary(parameters));
            return component.ToHtmlString();
        });
    }

    private static ServiceProvider CreateServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMudServices();
        return services.BuildServiceProvider();
    }

    private static HtmlRenderer CreateRenderer(IServiceProvider services) =>
        new(services, services.GetRequiredService<ILoggerFactory>());
}
