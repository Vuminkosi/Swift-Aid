using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Microsoft.JSInterop.Infrastructure;
using MudBlazor.Services;
using SwiftAid.UI.Components.Surfaces.Account;

namespace SwiftAid.Ui.Tests;

public sealed class ProfileSurfaceTests
{
    [Fact]
    public async Task ProfileSurface_SignedOutOffersSignIn()
    {
        var markup = await RenderAsync(ProfileSnapshot.SignedOut);

        Assert.Contains("Sign in to view your profile.", markup, StringComparison.Ordinal);
        Assert.Contains("Sign in", markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProfileSurface_ReadyShowsFieldsAndVerification()
    {
        var markup = await RenderAsync(new ProfileSnapshot(
            ProfileViewState.Ready,
            "Amina Yusuf",
            "+265991234567",
            "MW",
            "Lilongwe",
            "Area 25",
            "amina@example.invalid",
            EmailVerified: false,
            IsSaving: false));

        Assert.Contains("Personal information", markup, StringComparison.Ordinal);
        Assert.Contains("Verification required", markup, StringComparison.Ordinal);
        Assert.Contains("Save changes", markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProfileSurface_ExposesPasswordManagerAndKeyboardHints()
    {
        var markup = await RenderAsync(new ProfileSnapshot(
            ProfileViewState.Ready,
            "Amina Yusuf",
            "+265991234567",
            "MW",
            "Lilongwe",
            "Area 25",
            "amina@example.invalid",
            EmailVerified: true,
            IsSaving: false));

        Assert.Contains("autocomplete=\"name\"", markup, StringComparison.Ordinal);
        Assert.Contains("autocomplete=\"tel\"", markup, StringComparison.Ordinal);
        Assert.Contains("inputmode=\"tel\"", markup, StringComparison.Ordinal);
        Assert.Contains("autocomplete=\"country\"", markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProfileSurface_ReadyShowsInitialsWhenNoPhoto()
    {
        var markup = await RenderAsync(new ProfileSnapshot(
            ProfileViewState.Ready,
            "Amina Yusuf",
            null, null, null, null,
            "amina@example.invalid",
            EmailVerified: true,
            IsSaving: false));

        Assert.Contains("Profile photo", markup, StringComparison.Ordinal);
        Assert.Contains("Choose photo", markup, StringComparison.Ordinal);
        Assert.Contains("JPEG, PNG, or WebP up to 2 MB", markup, StringComparison.Ordinal);
        Assert.Contains(">AY<", markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProfileSurface_ReadyShowsPhotoAndRemoveWhenAvatarSet()
    {
        var markup = await RenderAsync(new ProfileSnapshot(
            ProfileViewState.Ready,
            "Amina Yusuf",
            null, null, null, null,
            "amina@example.invalid",
            EmailVerified: true,
            IsSaving: false,
            StatusMessage: null,
            StatusIsError: false,
            AvatarUrl: "/auth/profile/avatar"));

        Assert.Contains("/auth/profile/avatar", markup, StringComparison.Ordinal);
        Assert.Contains("Your profile photo", markup, StringComparison.Ordinal);
        Assert.Contains("Remove", markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProfileSurface_SavingDisablesSubmit()
    {
        var markup = await RenderAsync(new ProfileSnapshot(
            ProfileViewState.Ready,
            "Amina Yusuf",
            null, null, null, null,
            "amina@example.invalid",
            EmailVerified: true,
            IsSaving: true));

        Assert.DoesNotContain("Save changes", markup, StringComparison.Ordinal);
        Assert.Contains("disabled", markup, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Verified", markup, StringComparison.Ordinal);
    }

    private static async Task<string> RenderAsync(ProfileSnapshot snapshot)
    {
        await using var services = CreateServices();
        await using var renderer = CreateRenderer(services);
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<ProfileSurface>(
                ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    [nameof(ProfileSurface.Snapshot)] = snapshot,
                    [nameof(ProfileSurface.OnRefresh)] = EventCallback.Factory.Create(
                        new object(), () => { }),
                    [nameof(ProfileSurface.OnSave)] = EventCallback.Factory.Create<ProfileEditRequest>(
                        new object(), (ProfileEditRequest _) => { })
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
