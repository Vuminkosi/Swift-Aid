using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Authorization;
using Bit.Bonboarding;
using MudBlazor.Services;
using SwiftAid.Browser.Components;
using SwiftAid.Browser.Services;
using SwiftAid.Contracts.Ellai;
using SwiftAid.UI.Ellai;
using SwiftAid.Client.Intake;
using SwiftAid.Client.Presentation.PublicWorkflows;
using SwiftAid.Contracts.Cases;
using SwiftAid.Client.Sessions;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<BrowserGuestRoot>("#guest-root");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped<SwiftAid.UI.Ellai.Guidance.EllaiDisplayedReceiptContext>();
builder.Services.AddScoped<SwiftAid.UI.Ellai.Guidance.EllaiValidationGuidance>();
builder.Services.AddScoped<SwiftAid.UI.Ellai.Guidance.EllaiCaseFilterContext>();
builder.Services.AddScoped<SwiftAid.UI.Ellai.Guidance.EllaiPublicReportDraftContext>();
builder.Services.AddMudServices();
builder.Services.AddAuthorizationCore();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<AuthenticationStateProvider, BrowserGuestAuthenticationStateProvider>();
builder.Services.AddScoped(_ => new HttpClient
{
    BaseAddress = new Uri(builder.HostEnvironment.BaseAddress)
});
BitBonboarding.AddBitBonboardingServices(builder.Services, _ => { });
BitBonboarding.AddBitBonboardingInMemoryStore(builder.Services);
builder.Services.AddScoped<EllaiLauncher>();
builder.Services.AddScoped<EllaiEventPoller>();
builder.Services.AddScoped<IEllaiReviewClient>(serviceProvider =>
    new EllaiReviewHttpClient(serviceProvider.GetRequiredService<HttpClient>(), useGuestRoutes: true,
        receiptProofResolver: async (request, ct) =>
        {
            var record = await serviceProvider.GetRequiredService<IGuestSubmissionPersistence>()
                .GetAsync(request.ClientSubmissionId, ct);
            return record?.State == GuestSubmissionLocalState.Received && record.Receipt?.ReceiptId == request.ReceiptId &&
                record.Receipt.ClientSubmissionId == request.ClientSubmissionId ? record.ReceiptProof : null;
        }));
builder.Services.AddScoped<SwiftAid.UI.Ellai.Guidance.IEllaiGuidanceControl, BrowserGuestEllaiGuidanceControl>();
builder.Services.AddScoped<IPublicIntakeMetadataClient, BrowserPublicIntakeMetadataClient>();
builder.Services.AddScoped<IPublicIntakeProcessingPolicyClient, BrowserPublicIntakeProcessingPolicyClient>();
builder.Services.AddScoped<IPublicIntakeSubmissionClient, BrowserPublicIntakeSubmissionClient>();
builder.Services.AddScoped<IPublicCommunitySessionClient>(serviceProvider =>
    new HttpPublicCommunitySessionClient(new CommunitySessionHttpClient(
        serviceProvider.GetRequiredService<HttpClient>())));
builder.Services.AddScoped<SwiftAid.Client.Presentation.IGeoLookupClient>(serviceProvider => new SwiftAid.Client.Presentation.HttpGeoLookupClient(
    serviceProvider.GetRequiredService<HttpClient>()));
builder.Services.AddScoped<IGuestIntakeTransport>(serviceProvider =>
{
    var httpClient = serviceProvider.GetRequiredService<HttpClient>();
    return httpClient.BaseAddress?.Scheme == Uri.UriSchemeHttps
        ? new GuestIntakeHttpClient(httpClient)
        : new BrowserUnavailableGuestIntakeTransport();
});
builder.Services.AddScoped<IGuestSubmissionCoordinator, GuestSubmissionCoordinator>();
builder.Services.AddScoped<ICaseQueueClient>(serviceProvider => new SwiftAid.Client.Cases.CaseQueueHttpClient(
    serviceProvider.GetRequiredService<HttpClient>()));
builder.Services.AddScoped<BrowserGuestSubmissionPersistence>();
builder.Services.AddScoped<IGuestSubmissionPersistence>(serviceProvider =>
    serviceProvider.GetRequiredService<BrowserGuestSubmissionPersistence>());
builder.Services.AddScoped<IGuestSubmissionStore>(serviceProvider => new GuestDraftStore(
    serviceProvider.GetRequiredService<IGuestSubmissionPersistence>(),
    transport: serviceProvider.GetRequiredService<IGuestIntakeTransport>(),
    cleanupJournal: serviceProvider.GetRequiredService<IGuestAudioCleanupJournal>(),
    audioStore: serviceProvider.GetRequiredService<IGuestAudioStore>()));
builder.Services.AddScoped<IGuestAudioStore, BrowserGuestAudioStore>();
builder.Services.AddScoped<IGuestAudioCleanupJournal, BrowserGuestAudioCleanupJournal>();

await builder.Build().RunAsync();
