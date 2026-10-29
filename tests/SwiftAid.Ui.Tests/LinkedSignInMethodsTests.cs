using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Microsoft.JSInterop.Infrastructure;
using MudBlazor.Services;
using SwiftAid.UI.Components.Identity;

namespace SwiftAid.Ui.Tests;

public sealed class LinkedSignInMethodsTests
{
    [Fact]
    public async Task Loading_RendersProgressWithoutForms()
    {
        var markup = await RenderAsync(new LinkedSignInMethodsSnapshot(
            LinkedIdentityViewState.Loading,
            [],
            [],
            RequestToken: string.Empty,
            ReauthenticationRequired: false,
            ReauthenticationProviderId: null,
            ReauthenticationDisplayName: null,
            StatusMessage: null,
            StatusIsError: false));

        Assert.Contains("Checking your linked sign-in methods", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("<form", markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SignedOut_PointsToSignInWithoutOfferingChanges()
    {
        var markup = await RenderAsync(new LinkedSignInMethodsSnapshot(
            LinkedIdentityViewState.SignedOut,
            [],
            [],
            RequestToken: string.Empty,
            ReauthenticationRequired: false,
            ReauthenticationProviderId: null,
            ReauthenticationDisplayName: null,
            StatusMessage: null,
            StatusIsError: false));

        Assert.Contains("href=\"/sign-in\"", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Remove", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("<form", markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Ready_RendersLinkedRowsLinkFormsAndReauthentication()
    {
        var markup = await RenderAsync(new LinkedSignInMethodsSnapshot(
            LinkedIdentityViewState.Ready,
            [
                new LinkedIdentityRow("google", "Google", CurrentlyAvailable: true),
                new LinkedIdentityRow("org-ho", "Harbor Org", CurrentlyAvailable: false)
            ],
            [new LinkableProviderRow("facebook", "Facebook")],
            RequestToken: "test-token",
            ReauthenticationRequired: true,
            ReauthenticationProviderId: "google",
            ReauthenticationDisplayName: "Google",
            StatusMessage: null,
            StatusIsError: false));

        Assert.Contains("Google", markup, StringComparison.Ordinal);
        Assert.Contains("Linked", markup, StringComparison.Ordinal);
        Assert.Contains("Currently unavailable for sign-in.", markup, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Remove Google sign-in\"", markup, StringComparison.Ordinal);
        Assert.Contains(
            "action=\"/auth/identities/facebook/link?returnUrl=%2Fsign-in-methods%3Flinked%3Dfacebook\"",
            markup,
            StringComparison.Ordinal);
        Assert.Contains(
            "action=\"/auth/reauthenticate/google?returnUrl=%2Fsign-in-methods%3Freauthenticated%3Dgoogle\"",
            markup,
            StringComparison.Ordinal);
        Assert.Contains("name=\"__RequestVerificationToken\" value=\"test-token\"", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("/auth/identities/google/unlink", markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ready_EmptyStatesExplainNextStep()
    {
        var markup = await RenderAsync(new LinkedSignInMethodsSnapshot(
            LinkedIdentityViewState.Ready,
            [],
            [],
            RequestToken: "test-token",
            ReauthenticationRequired: false,
            ReauthenticationProviderId: null,
            ReauthenticationDisplayName: null,
            StatusMessage: null,
            StatusIsError: false));

        Assert.Contains("No sign-in methods are linked yet.", markup, StringComparison.Ordinal);
        Assert.Contains("No other verified providers are available right now.", markup, StringComparison.Ordinal);
    }

    private sealed record LinkedSignInMethodsSnapshot(
        LinkedIdentityViewState ViewState,
        IReadOnlyList<LinkedIdentityRow> Linked,
        IReadOnlyList<LinkableProviderRow> AvailableToLink,
        string RequestToken,
        bool ReauthenticationRequired,
        string? ReauthenticationProviderId,
        string? ReauthenticationDisplayName,
        string? StatusMessage,
        bool StatusIsError);

    private static async Task<string> RenderAsync(LinkedSignInMethodsSnapshot snapshot)
    {
        await using var services = CreateServices();
        await using var renderer = CreateRenderer(services);
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<LinkedSignInMethods>(
                ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    [nameof(LinkedSignInMethods.ViewState)] = snapshot.ViewState,
                    [nameof(LinkedSignInMethods.Linked)] = snapshot.Linked,
                    [nameof(LinkedSignInMethods.AvailableToLink)] = snapshot.AvailableToLink,
                    [nameof(LinkedSignInMethods.RequestToken)] = snapshot.RequestToken,
                    [nameof(LinkedSignInMethods.ReauthenticationRequired)] = snapshot.ReauthenticationRequired,
                    [nameof(LinkedSignInMethods.ReauthenticationProviderId)] = snapshot.ReauthenticationProviderId,
                    [nameof(LinkedSignInMethods.ReauthenticationDisplayName)] = snapshot.ReauthenticationDisplayName,
                    [nameof(LinkedSignInMethods.StatusMessage)] = snapshot.StatusMessage,
                    [nameof(LinkedSignInMethods.StatusIsError)] = snapshot.StatusIsError,
                    [nameof(LinkedSignInMethods.OnRefresh)] = EventCallback.Factory.Create(new object(), static () => { }),
                    [nameof(LinkedSignInMethods.OnUnlink)] = EventCallback.Factory.Create<string>(new object(), static _ => { })
                }));
            return component.ToHtmlString();
        });
    }

    private static ServiceProvider CreateServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMudServices();
        services.AddSingleton<IJSRuntime>(NoOpJsRuntime.Instance);
        services.AddSingleton<NavigationManager>(new TestNavigationManager());
        return services.BuildServiceProvider();
    }

    private sealed class TestNavigationManager : NavigationManager
    {
        public TestNavigationManager() => Initialize("http://localhost/", "http://localhost/");
    }

    private sealed class NoOpJsRuntime : JSRuntime
    {
        public static readonly NoOpJsRuntime Instance = new();

        protected override void BeginInvokeJS(
            long taskId,
            string identifier,
            string? argsJson,
            JSCallResultType resultType,
            long targetInstanceId)
        {
        }

        protected override void EndInvokeDotNet(
            DotNetInvocationInfo callInfo,
            in DotNetInvocationResult result)
        {
        }
    }

    private static HtmlRenderer CreateRenderer(IServiceProvider services) =>
        new(services, services.GetRequiredService<ILoggerFactory>());
}
