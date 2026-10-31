using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using SwiftAid.Application.Ellai;
using SwiftAid.Gateway.Endpoints;
using SwiftAid.Gateway.Ellai;

namespace SwiftAid.Integration.Tests;

public sealed class EllaiEndpointMetadataTests
{
    [Fact]
    public async Task ApprovalAndReceiptEndpointsCarryScopedAuthorizationMetadata()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddScoped<EllaiSessionService>();
        builder.Services.AddScoped<EllaiSessionScopeResolver>();
        builder.Services.AddScoped<EllaiUtteranceSubmissionService>();
        builder.Services.AddScoped<EllaiApprovalService>();
        builder.Services.AddScoped<EllaiApprovedActionService>();
        builder.Services.AddSingleton<EllaiApprovalDialogCoordinator>();
        builder.Services.AddSingleton<EllaiSessionEventJournal>();
        builder.Services.AddSingleton<EllaiConsentService>();
        builder.Services.AddScoped<EllaiVoiceTranscriptionService>();
        builder.Services.AddSingleton<SwiftAid.Intelligence.Meta.EllaiOptions>();
        builder.Services.AddSingleton<EllaiSessionResponseStore>();
        builder.Services.AddHttpClient<EllaiSpeechSynthesisClient>();
        builder.Services.AddTransient<IEllaiSpeechSynthesisClient>(services =>
            services.GetRequiredService<EllaiSpeechSynthesisClient>());
        builder.Services.AddSingleton<IEllaiSessionStore, InMemoryEllaiSessionStore>();
        builder.Services.AddSingleton<IEllaiApprovalStore, EllaiReceiptStore>();
        builder.Services.AddSingleton<IEllaiUiObservationStore, InMemoryEllaiUiObservationStore>();
        builder.Services.AddSingleton<IEllaiConsentStore, InMemoryEllaiConsentStore>();
        await using var app = builder.Build();
        app.MapEllaiEndpoints();

        var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>().ToList();
        var approvalRoutes = endpoints.Where(endpoint =>
            endpoint.RoutePattern.RawText?.Contains("approval", StringComparison.OrdinalIgnoreCase) == true)
            .ToList();
        var receiptRoutes = endpoints.Where(endpoint =>
            endpoint.RoutePattern.RawText?.EndsWith("receipts", StringComparison.OrdinalIgnoreCase) == true)
            .ToList();

        Assert.NotEmpty(approvalRoutes);
        Assert.All(approvalRoutes, endpoint =>
            Assert.NotNull(endpoint.Metadata.GetMetadata<EllaiScopedAuthorizationRequiredMetadata>()));

        Assert.NotEmpty(receiptRoutes);
        Assert.All(receiptRoutes, endpoint =>
            Assert.NotNull(endpoint.Metadata.GetMetadata<EllaiScopedAuthorizationRequiredMetadata>()));
    }

}
