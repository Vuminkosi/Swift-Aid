using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Microsoft.JSInterop.Infrastructure;
using MudBlazor.Services;
using SwiftAid.UI.Components.Surfaces.Account;

namespace SwiftAid.Ui.Tests;

public sealed class ChangeEmailSurfaceTests
{
    [Fact]
    public async Task DetailsShowsCurrentEmailAndPasswordGuard()
    {
        var markup = await RenderAsync(ChangeEmailSnapshot.Details("old@example.invalid"));

        Assert.Contains("Change email address", markup, StringComparison.Ordinal);
        Assert.Contains("Current address: old@example.invalid", markup, StringComparison.Ordinal);
        Assert.Contains("New email address", markup, StringComparison.Ordinal);
        Assert.Contains("Current password", markup, StringComparison.Ordinal);
        Assert.Contains("Send verification code", markup, StringComparison.Ordinal);
        Assert.Contains("Your sign-in stays on the old address until you confirm.", markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CodeSentShowsCodeEntryAndPendingAddress()
    {
        var markup = await RenderAsync(new ChangeEmailSnapshot(
            ChangeEmailViewState.CodeSent,
            "old@example.invalid",
            "new@example.invalid",
            IsSaving: false));

        Assert.Contains("sent to new@example.invalid", markup, StringComparison.Ordinal);
        Assert.Contains("Verification code", markup, StringComparison.Ordinal);
        Assert.Contains("Confirm new email", markup, StringComparison.Ordinal);
        Assert.Contains("Request a new code", markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DoneShowsReturnToProfile()
    {
        var markup = await RenderAsync(new ChangeEmailSnapshot(
            ChangeEmailViewState.Done,
            "new@example.invalid",
            PendingEmail: null,
            IsSaving: false,
            StatusMessage: "Your email address has been updated.",
            StatusIsError: false));

        Assert.Contains("Your email address has been updated.", markup, StringComparison.Ordinal);
        Assert.Contains("Return to profile", markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SignedOutPromptsSignIn()
    {
        var markup = await RenderAsync(ChangeEmailSnapshot.SignedOut);

        Assert.Contains("Sign in to change your email address.", markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CodeSentShowsResendCountdownAndDisablesResend()
    {
        var markup = await RenderAsync(new ChangeEmailSnapshot(
            ChangeEmailViewState.CodeSent,
            "old@example.invalid",
            "new@example.invalid",
            IsSaving: false));

        Assert.Contains("Request a new code (00:30)", markup, StringComparison.Ordinal);
        Assert.Contains("disabled", markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SavingDisablesConfirm()
    {
        var markup = await RenderAsync(new ChangeEmailSnapshot(
            ChangeEmailViewState.CodeSent,
            "old@example.invalid",
            "new@example.invalid",
            IsSaving: true));

        Assert.Contains("Confirming", markup, StringComparison.Ordinal);
        Assert.Contains("disabled", markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrimaryActionsMeetTouchTargetClass()
    {
        var markup = await RenderAsync(ChangeEmailSnapshot.Details("old@example.invalid"));

        Assert.Contains("auth-cta", markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RenderFaultShowsRecovery()
    {
        var markup = await RenderAsync(null!);

        Assert.Contains("failed to load", markup, StringComparison.Ordinal);
        Assert.Contains("Retry", markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CleanFormNavigatesWithoutPrompt()
    {
        await using var services = CreateServices();
        await using var renderer = CreateRenderer(services);
        var navigation = (TestNavigationManager)services.GetRequiredService<NavigationManager>();
        await renderer.Dispatcher.InvokeAsync(async () =>
            await renderer.RenderComponentAsync<ChangeEmailSurface>(ParameterView.FromDictionary(
                new Dictionary<string, object?>
                {
                    [nameof(ChangeEmailSurface.Snapshot)] =
                        ChangeEmailSnapshot.Details("old@example.invalid"),
                    [nameof(ChangeEmailSurface.OnRefresh)] = EventCallback.Factory.Create(
                        new object(), () => { }),
                    [nameof(ChangeEmailSurface.OnRequest)] = EventCallback.Factory.Create<ChangeEmailRequest>(
                        new object(), (ChangeEmailRequest _) => { }),
                    [nameof(ChangeEmailSurface.OnConfirm)] = EventCallback.Factory.Create<string>(
                        new object(), (string _) => { }),
                    [nameof(ChangeEmailSurface.OnStartOver)] = EventCallback.Factory.Create(
                        new object(), () => { })
                })));

        await renderer.Dispatcher.InvokeAsync(() => navigation.NavigateTo("/profile"));

        Assert.Equal("http://localhost/profile", navigation.Uri);
    }

    private static async Task<string> RenderAsync(ChangeEmailSnapshot snapshot)
    {
        await using var services = CreateServices();
        await using var renderer = CreateRenderer(services);
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<ChangeEmailSurface>(
                ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    [nameof(ChangeEmailSurface.Snapshot)] = snapshot,
                    [nameof(ChangeEmailSurface.OnRefresh)] = EventCallback.Factory.Create(
                        new object(), () => { }),
                    [nameof(ChangeEmailSurface.OnRequest)] = EventCallback.Factory.Create<ChangeEmailRequest>(
                        new object(), (ChangeEmailRequest _) => { }),
                    [nameof(ChangeEmailSurface.OnConfirm)] = EventCallback.Factory.Create<string>(
                        new object(), (string _) => { }),
                    [nameof(ChangeEmailSurface.OnStartOver)] = EventCallback.Factory.Create(
                        new object(), () => { }),
                    [nameof(ChangeEmailSurface.ProfileHref)] = "/profile"
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
        services.AddSingleton<IErrorBoundaryLogger>(NoOpErrorBoundaryLogger.Instance);
        return services.BuildServiceProvider();
    }

    private sealed class TestNavigationManager : NavigationManager
    {
        public TestNavigationManager() => Initialize("http://localhost/", "http://localhost/");

        protected override void NavigateToCore(string uri, NavigationOptions options) =>
            Uri = ToAbsoluteUri(uri).ToString();
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

    private sealed class NoOpErrorBoundaryLogger : IErrorBoundaryLogger
    {
        public static readonly NoOpErrorBoundaryLogger Instance = new();

        public ValueTask LogErrorAsync(Exception exception) => ValueTask.CompletedTask;
    }

    private static HtmlRenderer CreateRenderer(IServiceProvider services) =>
        new(services, services.GetRequiredService<ILoggerFactory>());
}
