using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using MudBlazor.Services;
using SwiftAid.Browser.Components;
using SwiftAid.Browser.Services;
using SwiftAid.Client.Intake;
using SwiftAid.Client.Presentation.PublicWorkflows;
using SwiftAid.Contracts.Cases;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<BrowserGuestRoot>("#guest-root");

builder.Services.AddMudServices();
builder.Services.AddScoped(_ => new HttpClient
{
    BaseAddress = new Uri(builder.HostEnvironment.BaseAddress)
});
builder.Services.AddScoped<IPublicIntakeMetadataClient, BrowserPublicIntakeMetadataClient>();
builder.Services.AddScoped<IPublicIntakeProcessingPolicyClient, BrowserPublicIntakeProcessingPolicyClient>();
builder.Services.AddScoped<IPublicIntakeSubmissionClient, BrowserPublicIntakeSubmissionClient>();
builder.Services.AddScoped<IGuestIntakeTransport>(serviceProvider =>
{
    var httpClient = serviceProvider.GetRequiredService<HttpClient>();
    return httpClient.BaseAddress?.Scheme == Uri.UriSchemeHttps
        ? new GuestIntakeHttpClient(httpClient)
        : new BrowserUnavailableGuestIntakeTransport();
});
builder.Services.AddScoped<IGuestSubmissionCoordinator, GuestSubmissionCoordinator>();
builder.Services.AddScoped<ICaseQueueClient, BrowserUnavailableCaseQueueClient>();
builder.Services.AddScoped<BrowserGuestSubmissionPersistence>();
builder.Services.AddScoped<IGuestSubmissionPersistence>(serviceProvider =>
    serviceProvider.GetRequiredService<BrowserGuestSubmissionPersistence>());
builder.Services.AddScoped<IGuestSubmissionStore>(serviceProvider => new GuestDraftStore(
    serviceProvider.GetRequiredService<IGuestSubmissionPersistence>(),
    cleanupJournal: serviceProvider.GetRequiredService<IGuestAudioCleanupJournal>(),
    audioStore: serviceProvider.GetRequiredService<IGuestAudioStore>()));
builder.Services.AddScoped<IGuestAudioStore, BrowserGuestAudioStore>();
builder.Services.AddScoped<IGuestAudioCleanupJournal, BrowserGuestAudioCleanupJournal>();

await builder.Build().RunAsync();
