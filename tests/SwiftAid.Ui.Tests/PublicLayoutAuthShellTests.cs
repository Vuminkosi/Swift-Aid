using System.Security.Claims;
using System.Reflection;
using Bit.Bonboarding;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor.Services;
using SwiftAid.Contracts.Cases;
using SwiftAid.Contracts.Ellai;
using SwiftAid.Gateway.Components.Layout;
using SwiftAid.UI.Components.Layout;
using SwiftAid.UI.Components.Surfaces.Public;
using SwiftAid.UI.Ellai;
using SwiftAid.UI.Ellai.Guidance;

namespace SwiftAid.Ui.Tests;

public sealed class PublicLayoutAuthShellTests
{
    [Theory]
    [InlineData("/sign-in")]
    [InlineData("/register?returnUrl=%2Fdashboard")]
    [InlineData("/forgot-password")]
    [InlineData("/reset-password?userId=user&code=token")]
    [InlineData("/verify-email")]
    [InlineData("/welcome-complete")]
    public async Task IdentityJourneyRoutesHideTheGeneralPublicNavigation(string path)
    {
        var markup = await RenderPublicLayoutAsync(path);

        Assert.Contains("Public page", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Public navigation", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("/sessions", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("/sign-in?returnUrl=%2Fdashboard", markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublicLandingRouteKeepsCommunityAndSignInNavigation()
    {
        var markup = await RenderPublicLayoutAsync("/");

        Assert.Contains("Public navigation", markup, StringComparison.Ordinal);
        Assert.Contains("/sessions", markup, StringComparison.Ordinal);
        Assert.Contains("/sign-in?returnUrl=%2Fdashboard", markup, StringComparison.Ordinal);
        Assert.Contains("Ask", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Guide Me", markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnonymousGatewayHomeRendersTheSwiftAidWebsiteWithoutWorkspaceNavigation()
    {
        var markup = await RenderGatewayHomeAsync(isSignedIn: false);

        Assert.Contains("Community health, connected", markup, StringComparison.Ordinal);
        Assert.Contains("What SwiftAid is about", markup, StringComparison.Ordinal);
        Assert.Contains("From concern to follow-through", markup, StringComparison.Ordinal);
        Assert.Contains("href=\"/sessions\"", markup, StringComparison.Ordinal);
        Assert.Contains("public-website-shell", markup, StringComparison.Ordinal);
        Assert.Contains("href=\"/sign-in\"", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Support that follows through.", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("swiftaid-authorized-navigation", markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SignedInGatewayHomeKeepsTheWebsiteAndShowsWorkspaceNavigation()
    {
        var markup = await RenderGatewayHomeAsync(isSignedIn: true);

        Assert.Contains("Community health, connected", markup, StringComparison.Ordinal);
        Assert.Contains("What SwiftAid is about", markup, StringComparison.Ordinal);
        Assert.Contains("href=\"/dashboard\"", markup, StringComparison.Ordinal);
        Assert.Contains(">Workspace</a>", markup, StringComparison.Ordinal);
        Assert.Contains("Open your workspace", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("href=\"/sign-in\"", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("swiftaid-authorized-navigation", markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AccountEntrySurfaceProvidesResponsiveBrandAndGuestReportPath()
    {
        var markup = await RenderAccountEntrySurfaceAsync();

        Assert.Contains("aria-label=\"Back to SwiftAid home\"", markup, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"SwiftAid home\"", markup, StringComparison.Ordinal);
        Assert.Contains("_content/SwiftAid.UI/assets/swiftaid-icon.png", markup, StringComparison.Ordinal);
        Assert.Contains("Healthier people.", markup, StringComparison.Ordinal);
        Assert.Contains("Brighter tomorrows.", markup, StringComparison.Ordinal);
        Assert.Contains("href=\"/report\"", markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PasswordResetAcceptedSurfaceKeepsTheResultNeutralAndProvidesResend()
    {
        var services = CreateServices("/forgot-password");
        await using var serviceProvider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(
            serviceProvider,
            serviceProvider.GetRequiredService<ILoggerFactory>());

        var markup = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<PasswordResetRequestedSurface>(
                ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    [nameof(PasswordResetRequestedSurface.ErrorMessage)] = "Resend failed."
                }));
            return component.ToHtmlString();
        });

        Assert.Contains("Check your inbox", markup, StringComparison.Ordinal);
        Assert.Contains("If an account matches the address, reset instructions will be sent.", markup, StringComparison.Ordinal);
        Assert.Contains("Resend instructions", markup, StringComparison.Ordinal);
        Assert.Contains("Return to sign in", markup, StringComparison.Ordinal);
        Assert.Contains("role=\"alert\"", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("email was sent", markup, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string> RenderPublicLayoutAsync(string path)
    {
        var services = CreateServices(path);
        await using var serviceProvider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(
            serviceProvider,
            serviceProvider.GetRequiredService<ILoggerFactory>());

        RenderFragment layout = builder =>
        {
            builder.OpenComponent<PublicLayout>(0);
            builder.AddAttribute(1, nameof(PublicLayout.ChildContent), (RenderFragment)(content =>
                content.AddContent(0, "Public page")));
            builder.CloseComponent();
        };

        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<CascadingAuthenticationState>(
                ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    [nameof(CascadingAuthenticationState.ChildContent)] = layout,
                }));
            return component.ToHtmlString();
        });
    }

    private static async Task<string> RenderAccountEntrySurfaceAsync()
    {
        var services = CreateServices("/");
        await using var serviceProvider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(
            serviceProvider,
            serviceProvider.GetRequiredService<ILoggerFactory>());

        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<AccountEntrySurface>(ParameterView.Empty);
            return component.ToHtmlString();
        });
    }

    private static async Task<string> RenderGatewayHomeAsync(bool isSignedIn)
    {
        var services = CreateServices("/");
        if (isSignedIn)
        {
            services.AddSingleton<AuthenticationStateProvider, SignedInAuthenticationStateProvider>();
        }

        services.AddSingleton<ICaseQueueClient, NoOpCaseQueueClient>();
        await using var serviceProvider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(
            serviceProvider,
            serviceProvider.GetRequiredService<ILoggerFactory>());

        RenderFragment home = builder =>
        {
            builder.OpenComponent<SwiftAid.Gateway.Components.Pages.Home>(0);
            builder.CloseComponent();
        };
        RenderFragment routeLayout = builder =>
        {
            builder.OpenComponent<SwiftAid.Gateway.Components.Layout.PublicWebsiteLayout>(0);
            builder.AddAttribute(1, nameof(SwiftAid.Gateway.Components.Layout.PublicWebsiteLayout.Body), home);
            builder.CloseComponent();
        };

        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<CascadingAuthenticationState>(
                ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    [nameof(CascadingAuthenticationState.ChildContent)] = routeLayout,
                }));
            return component.ToHtmlString();
        });
    }

    private static ServiceCollection CreateServices(string path)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMudServices();
        services.AddAuthorizationCore();
        services.AddSingleton<IJSRuntime, NoOpJsRuntime>();
        services.AddSingleton<NavigationManager>(new TestNavigationManager(path));
        services.AddSingleton<AuthenticationStateProvider, AnonymousAuthenticationStateProvider>();
        BitBonboarding.AddBitBonboardingServices(services, _ => { });
        BitBonboarding.AddBitBonboardingInMemoryStore(services);
        services.AddSingleton<IBonboardingService>(DispatchProxy.Create<IBonboardingService, NoOpBonboardingProxy>());
        services.AddSingleton<IEllaiGuidanceControl>(DispatchProxy.Create<IEllaiGuidanceControl, NoOpGuidanceProxy>());
        services.AddScoped<EllaiLauncher>();
        var reviewClient = DispatchProxy.Create<IEllaiReviewClient, NoOpReviewClientProxy>();
        services.AddSingleton(reviewClient);
        services.AddSingleton(new EllaiEventPoller(reviewClient));
        return services;
    }

    public class NoOpBonboardingProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.ReturnType == typeof(void) ? null :
            targetMethod?.ReturnType == typeof(Task) ? Task.CompletedTask :
            targetMethod?.ReturnType.IsValueType == true ? Activator.CreateInstance(targetMethod.ReturnType) : null;
    }

    public class NoOpGuidanceProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.ReturnType == typeof(Task) ? Task.CompletedTask :
            targetMethod?.ReturnType.IsValueType == true ? Activator.CreateInstance(targetMethod.ReturnType) : null;
    }

    public class NoOpReviewClientProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var returnType = targetMethod?.ReturnType;
            if (returnType == typeof(Task)) return Task.CompletedTask;
            if (returnType?.IsGenericType == true && returnType.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var resultType = returnType.GenericTypeArguments[0];
                var result = resultType.IsValueType ? Activator.CreateInstance(resultType) : null;
                return typeof(Task).GetMethod(nameof(Task.FromResult))!
                    .MakeGenericMethod(resultType).Invoke(null, [result]);
            }
            return returnType?.IsValueType == true ? Activator.CreateInstance(returnType) : null;
        }
    }

    private sealed class TestNavigationManager : NavigationManager
    {
        public TestNavigationManager() : this("/")
        {
        }

        public TestNavigationManager(string path) => Initialize("http://localhost/", $"http://localhost{path}");

        protected override void NavigateToCore(string uri, bool forceLoad) => NotifyLocationChanged(false);

        protected override void NavigateToCore(string uri, NavigationOptions options) => NotifyLocationChanged(false);
    }

    private sealed class AnonymousAuthenticationStateProvider : AuthenticationStateProvider
    {
        private static readonly AuthenticationState State = new(new ClaimsPrincipal(new ClaimsIdentity()));

        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(State);
    }

    private sealed class SignedInAuthenticationStateProvider : AuthenticationStateProvider
    {
        private static readonly AuthenticationState State = new(new ClaimsPrincipal(
            new ClaimsIdentity([new Claim(ClaimTypes.Name, "SwiftAid test user")], "Test")));

        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(State);
    }

    private sealed class NoOpCaseQueueClient : ICaseQueueClient
    {
        public Task<CaseQueueResult> GetMineAsync(int? limit, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CaseQueueResult(CaseQueueAccess.Allowed, []));
    }

    private sealed class NoOpJsRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            ValueTask.FromResult(default(TValue)!);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args) => ValueTask.FromResult(default(TValue)!);
    }
}
