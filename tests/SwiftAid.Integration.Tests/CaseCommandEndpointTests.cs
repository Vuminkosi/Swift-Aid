using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Validation.AspNetCore;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases.Commands;
using SwiftAid.Contracts.Commands;
using SwiftAid.Gateway.Endpoints;

namespace SwiftAid.Integration.Tests;

public sealed class CaseCommandEndpointTests
{
    [Theory]
    [InlineData("recipient")]
    [InlineData("actions")]
    [InlineData("expiry")]
    [InlineData("reason")]
    public async Task Collaborator_grant_rejects_missing_scope_fields_before_dispatch(string missingField)
    {
        await using var host = await TestCommandHost.StartAsync();
        var body = new CaseCommandApiRequest(1, "GrantCaseCollaborator", Guid.NewGuid(), 3, null, "Synthetic reason", null, null, null, null,
            GrantId: Guid.NewGuid(), CollaboratorActorId: "recipient",
            CollaborationActions: CaseCollaborationActions.ReadCase,
            ExpiresAtUtc: DateTimeOffset.UtcNow.AddHours(1));
        body = missingField switch
        {
            "recipient" => body with { CollaboratorActorId = null },
            "actions" => body with { CollaborationActions = null },
            "expiry" => body with { ExpiresAtUtc = null },
            _ => body with { Reason = null }
        };
        using var request = CreateRequest(FixedCaseId, body);
        using var response = await host.SendBearerAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, host.Processor.Calls);
    }

    [Theory]
    [InlineData("GrantCaseCollaborator")]
    [InlineData("RevokeCaseCollaborator")]
    public async Task Collaborator_commands_map_explicit_scope_and_identity(string commandType)
    {
        await using var host = await TestCommandHost.StartAsync();
        var body = new CaseCommandApiRequest(1, commandType, Guid.NewGuid(), 3, null, "Synthetic reason", null, null, null, null,
            GrantId: Guid.NewGuid(), CollaboratorActorId: "recipient",
            CollaborationActions: CaseCollaborationActions.ReadCase | CaseCollaborationActions.ReadAttachment,
            ExpiresAtUtc: DateTimeOffset.UtcNow.AddHours(1));
        using var request = CreateRequest(FixedCaseId, body);
        using var response = await host.SendBearerAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(body.CommandId, host.Processor.LastCommand!.CommandId);
        Assert.Equal(body.ExpectedVersion, host.Processor.LastCommand.ExpectedVersion);
        if (commandType == "GrantCaseCollaborator")
        {
            var grant = Assert.IsType<GrantCaseCollaboratorCommand>(host.Processor.LastCommand);
            Assert.Equal(body.GrantId, grant.GrantId);
            Assert.Equal(body.CollaboratorActorId, grant.CollaboratorActorId);
            Assert.Equal(body.CollaborationActions, grant.Actions);
            Assert.Equal(body.ExpiresAtUtc, grant.ExpiresAtUtc);
        }
        else
        {
            Assert.Equal(body.GrantId, Assert.IsType<RevokeCaseCollaboratorCommand>(host.Processor.LastCommand).GrantId);
        }

        using var missing = CreateRequest(FixedCaseId, body with { GrantId = null });
        using var invalid = await host.SendBearerAsync(missing);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(1, host.Processor.Calls);
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Guid FixedCaseId = Guid.Parse("c0de0001-1111-4c22-9dd9-123456789abc");

    [Fact]
    public void MappedCommandRoute_RequiresStaffAuthentication()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAntiforgery();
        builder.Services.AddSingleton<ICaseCommandProcessor>(new TestCaseCommandProcessor());
        builder.Services.AddAuthorization();
        using var app = builder.Build();
        app.MapCaseCommandEndpoints();

        var route = Assert.Single(
            ((IEndpointRouteBuilder)app).DataSources
                .SelectMany(source => source.Endpoints)
                .OfType<RouteEndpoint>(),
            candidate => candidate.RoutePattern.RawText == "/api/v1/cases/{caseId:guid}/commands");

        var authorization = Assert.Single(route.Metadata.GetOrderedMetadata<IAuthorizeData>());
        Assert.Equal(
            string.Join(",", IdentityConstants.ApplicationScheme, OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme),
            authorization.AuthenticationSchemes);
    }

    [Fact]
    public async Task AnonymousCommandRequest_IsUnauthorized()
    {
        await using var host = await TestCommandHost.StartAsync();
        using var request = CreateRequest(FixedCaseId, ResolveBody());

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, host.Processor.Calls);
    }

    [Fact]
    public async Task CookieWithoutAntiforgery_IsRejected()
    {
        await using var host = await TestCommandHost.StartAsync();
        using var request = CreateRequest(FixedCaseId, ResolveBody());
        TestAuthenticationHandler.AddCookieAuthentication(request);

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, host.Processor.Calls);
    }

    [Fact]
    public async Task CorrectReport_IsAccepted()
    {
        await using var host = await TestCommandHost.StartAsync();
        var commandId = Guid.NewGuid();
        using var request = CreateRequest(FixedCaseId, new CaseCommandApiRequest(
            1, "CorrectReport", commandId, 3, null, "reason for correction", null, null, null, null,
            Narrative: "corrected report text"));

        using var response = await host.SendBearerAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var receipt = JsonSerializer.Deserialize<CaseCommandApiResponse>(body, JsonOptions);
        Assert.NotNull(receipt);
        Assert.Equal(FixedCaseId, receipt!.CaseId);
        Assert.Equal(commandId, receipt.CommandId);
        Assert.Single(receipt.EventIds);
        var command = Assert.IsType<CorrectReportCommand>(host.Processor.LastCommand);
        Assert.Equal(commandId, command.CommandId);
        Assert.Equal(3, command.ExpectedVersion);
        Assert.Equal("corrected report text", command.CorrectedNarrative);
        Assert.Equal("reason for correction", command.Reason);
    }

    [Theory]
    [InlineData(null, "reason for correction")]
    [InlineData("corrected report text", null)]
    public async Task CorrectReport_RequiresNarrativeAndReason(string? narrative, string? reason)
    {
        await using var host = await TestCommandHost.StartAsync();
        using var request = CreateRequest(FixedCaseId, new CaseCommandApiRequest(
            1, "CorrectReport", Guid.NewGuid(), 3, null, reason, null, null, null, null,
            Narrative: narrative));

        using var response = await host.SendBearerAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, host.Processor.Calls);
    }

    [Fact]
    public async Task RequestProtectedAction_IsAccepted()
    {
        await using var host = await TestCommandHost.StartAsync();
        var commandId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        using var request = CreateRequest(FixedCaseId, new CaseCommandApiRequest(
            1, "RequestProtectedAction", commandId, 3, null, "needs reassignment", "owner-2", requestId, null, "Reassign"));

        using var response = await host.SendBearerAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var receipt = JsonSerializer.Deserialize<CaseCommandApiResponse>(body, JsonOptions);
        Assert.NotNull(receipt);
        Assert.Equal(FixedCaseId, receipt!.CaseId);
        Assert.Equal(commandId, receipt.CommandId);
        var command = Assert.IsType<RequestCaseActionCommand>(host.Processor.LastCommand);
        Assert.Equal(requestId, command.RequestId);
        Assert.Equal(CaseAction.Reassign, command.Action);
        Assert.Equal("owner-2", command.ProposedOwnerId);
    }

    [Fact]
    public async Task RequestResolveAction_WithoutOwner_IsAccepted()
    {
        await using var host = await TestCommandHost.StartAsync();
        var commandId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        using var request = CreateRequest(FixedCaseId, new CaseCommandApiRequest(
            1, "RequestProtectedAction", commandId, 3, null, "needs resolution review", null, requestId, null, "Resolve"));

        using var response = await host.SendBearerAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var command = Assert.IsType<RequestCaseActionCommand>(host.Processor.LastCommand);
        Assert.Equal(CaseAction.Resolve, command.Action);
        Assert.Null(command.ProposedOwnerId);
    }

    [Fact]
    public async Task RequestReassignAction_WithoutOwner_IsRejected()
    {
        await using var host = await TestCommandHost.StartAsync();
        using var request = CreateRequest(FixedCaseId, new CaseCommandApiRequest(
            1, "RequestProtectedAction", Guid.NewGuid(), 3, null, "needs reassignment", null, Guid.NewGuid(), null, "Reassign"));

        using var response = await host.SendBearerAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, host.Processor.Calls);
    }

    [Fact]
    public async Task RequestAssistAction_WithOwner_IsAccepted()
    {
        await using var host = await TestCommandHost.StartAsync();
        var commandId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        using var request = CreateRequest(FixedCaseId, new CaseCommandApiRequest(
            1, "RequestProtectedAction", commandId, 3, null, "needs assistance", "helper-2", requestId, null, "Assist"));

        using var response = await host.SendBearerAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var receipt = JsonSerializer.Deserialize<CaseCommandApiResponse>(body, JsonOptions);
        Assert.NotNull(receipt);
        Assert.Equal(FixedCaseId, receipt!.CaseId);
        Assert.Equal(commandId, receipt.CommandId);
        var command = Assert.IsType<RequestCaseActionCommand>(host.Processor.LastCommand);
        Assert.Equal(requestId, command.RequestId);
        Assert.Equal(CaseAction.Assist, command.Action);
        Assert.Equal("helper-2", command.ProposedOwnerId);
    }

    [Fact]
    public async Task RequestAssistAction_WithoutOwner_IsRejected()
    {
        await using var host = await TestCommandHost.StartAsync();
        using var request = CreateRequest(FixedCaseId, new CaseCommandApiRequest(
            1, "RequestProtectedAction", Guid.NewGuid(), 3, null, "needs assistance", null, Guid.NewGuid(), null, "Assist"));

        using var response = await host.SendBearerAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, host.Processor.Calls);
    }

    [Fact]
    public async Task RequestUnknownAction_IsRejected()
    {
        await using var host = await TestCommandHost.StartAsync();
        using var request = CreateRequest(FixedCaseId, new CaseCommandApiRequest(
            1, "RequestProtectedAction", Guid.NewGuid(), 3, null, "needs something", null, Guid.NewGuid(), null, "DeleteCase"));

        using var response = await host.SendBearerAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, host.Processor.Calls);
    }

    [Fact]
    public async Task ReviewProtectedAction_IsAccepted()
    {
        await using var host = await TestCommandHost.StartAsync();
        var commandId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        using var request = CreateRequest(FixedCaseId, new CaseCommandApiRequest(
            1, "ReviewProtectedAction", commandId, 3, null, "transfer approved", "owner-2", requestId, "Approved", null));

        using var response = await host.SendBearerAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var receipt = JsonSerializer.Deserialize<CaseCommandApiResponse>(body, JsonOptions);
        Assert.NotNull(receipt);
        Assert.Equal(commandId, receipt!.CommandId);
        var command = Assert.IsType<ReviewCaseActionRequestCommand>(host.Processor.LastCommand);
        Assert.Equal(requestId, command.RequestId);
        Assert.Equal(CaseActionReviewOutcome.Approved, command.Outcome);
        Assert.Equal("owner-2", command.AcceptedOwnerId);
    }

    [Fact]
    public async Task Resolve_IsAccepted()
    {
        await using var host = await TestCommandHost.StartAsync();
        var commandId = Guid.NewGuid();
        using var request = CreateRequest(FixedCaseId, ResolveBody(commandId));

        using var response = await host.SendBearerAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var receipt = JsonSerializer.Deserialize<CaseCommandApiResponse>(body, JsonOptions);
        Assert.NotNull(receipt);
        Assert.Equal(FixedCaseId, receipt!.CaseId);
        Assert.Equal(commandId, receipt.CommandId);
        var command = Assert.IsType<ResolveCaseCommand>(host.Processor.LastCommand);
        Assert.Equal(3, command.ExpectedVersion);
        Assert.Equal(3, host.Processor.LastAggregateVersion);
    }

    [Fact]
    public async Task Reopen_IsAccepted()
    {
        await using var host = await TestCommandHost.StartAsync();
        var commandId = Guid.NewGuid();
        using var request = CreateRequest(FixedCaseId, new CaseCommandApiRequest(
            1, "Reopen", commandId, 3, null, "new evidence", "owner-3", null, null, null));

        using var response = await host.SendBearerAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var receipt = JsonSerializer.Deserialize<CaseCommandApiResponse>(body, JsonOptions);
        Assert.NotNull(receipt);
        Assert.Equal(commandId, receipt!.CommandId);
        var command = Assert.IsType<ReopenCaseCommand>(host.Processor.LastCommand);
        Assert.Equal("owner-3", command.AccountableOwnerId);
        Assert.Equal(1, host.Processor.LastSchemaVersion);
        Assert.Equal(FixedCaseId, host.Processor.LastCaseId);
        Assert.Equal("actor-1", host.Processor.LastActor?.ActorId);
    }

    [Fact]
    public async Task DeniedCommand_GetsNotFound()
    {
        await using var host = await TestCommandHost.StartAsync(CaseCommandProcessingStatus.Denied);
        using var request = CreateRequest(FixedCaseId, ResolveBody());

        using var response = await host.SendBearerAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(1, host.Processor.Calls);
        Assert.DoesNotContain("resolved", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownCommandType_IsRejected()
    {
        await using var host = await TestCommandHost.StartAsync();
        using var request = CreateRequest(FixedCaseId, ResolveBody() with { CommandType = "DeleteCase" });

        using var response = await host.SendBearerAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, host.Processor.Calls);
    }

    [Fact]
    public async Task UnsupportedSchemaVersion_IsRejected()
    {
        await using var host = await TestCommandHost.StartAsync();
        using var request = CreateRequest(FixedCaseId, ResolveBody() with { SchemaVersion = 2 });

        using var response = await host.SendBearerAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, host.Processor.Calls);
    }

    [Fact]
    public async Task EmptyCommandId_IsRejected()
    {
        await using var host = await TestCommandHost.StartAsync();
        using var request = CreateRequest(FixedCaseId, ResolveBody() with { CommandId = Guid.Empty });

        using var response = await host.SendBearerAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, host.Processor.Calls);
    }

    [Fact]
    public async Task EmptyCaseId_IsRejected()
    {
        await using var host = await TestCommandHost.StartAsync();
        using var request = CreateRequest(Guid.Empty, ResolveBody());

        using var response = await host.SendBearerAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, host.Processor.Calls);
    }

    [Fact]
    public async Task ResolveWithoutReason_IsRejected()
    {
        await using var host = await TestCommandHost.StartAsync();
        using var request = CreateRequest(FixedCaseId, ResolveBody() with { Reason = " " });

        using var response = await host.SendBearerAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, host.Processor.Calls);
    }

    [Fact]
    public async Task ReopenWithoutOwner_IsRejected()
    {
        await using var host = await TestCommandHost.StartAsync();
        using var request = CreateRequest(FixedCaseId, new CaseCommandApiRequest(
            1, "Reopen", Guid.NewGuid(), 3, null, "new evidence", null, null, null, null));

        using var response = await host.SendBearerAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, host.Processor.Calls);
    }

    [Fact]
    public async Task ReviewWithUnknownOutcome_IsRejected()
    {
        await using var host = await TestCommandHost.StartAsync();
        using var request = CreateRequest(FixedCaseId, new CaseCommandApiRequest(
            1, "ReviewProtectedAction", Guid.NewGuid(), 3, null, "review", null, Guid.NewGuid(), "Maybe", null));

        using var response = await host.SendBearerAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, host.Processor.Calls);
    }

    [Fact]
    public async Task Conflict_GetsConflict()
    {
        await using var host = await TestCommandHost.StartAsync(CaseCommandProcessingStatus.Conflict);
        using var request = CreateRequest(FixedCaseId, ResolveBody());

        using var response = await host.SendBearerAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(1, host.Processor.Calls);
    }

    [Fact]
    public async Task UnavailableCommand_GetsServiceUnavailable()
    {
        await using var host = await TestCommandHost.StartAsync(CaseCommandProcessingStatus.Unavailable);
        using var request = CreateRequest(FixedCaseId, ResolveBody());

        using var response = await host.SendBearerAsync(request);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(1, host.Processor.Calls);
    }

    [Fact]
    public async Task DisabledPipeline_GetsNotFound()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAntiforgery();
        builder.Services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = IdentityConstants.ApplicationScheme;
                options.DefaultChallengeScheme = IdentityConstants.ApplicationScheme;
            })
            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(
                IdentityConstants.ApplicationScheme, _ => { })
            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(
                OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, _ => { });
        builder.Services.AddAuthorization();
        // No ICaseCommandProcessor: the case command pipeline is disabled here.
        using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapCaseCommandEndpoints();
        await app.StartAsync();
        using var client = app.GetTestClient();
        using var request = CreateRequest(Guid.NewGuid(), ResolveBody());
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestAuthenticationHandler.BearerToken);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await app.StopAsync();
    }

    private static CaseCommandApiRequest ResolveBody(Guid? commandId = null) => new(
        1, "Resolve", commandId ?? Guid.NewGuid(), 3, null, "resolved", null, null, null, null);

    private static HttpRequestMessage CreateRequest(Guid caseId, CaseCommandApiRequest body) =>
        new(HttpMethod.Post, $"/api/v1/cases/{caseId:D}/commands")
        {
            Content = new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json")
        };
}

internal sealed class TestCommandHost : IAsyncDisposable
{
    private readonly WebApplication _application;

    public HttpClient Client { get; }

    public TestCaseCommandProcessor Processor { get; }

    private TestCommandHost(WebApplication application, HttpClient client, TestCaseCommandProcessor processor)
    {
        _application = application;
        Client = client;
        Processor = processor;
    }

    public static async Task<TestCommandHost> StartAsync(
        CaseCommandProcessingStatus status = CaseCommandProcessingStatus.Accepted)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAntiforgery();
        builder.Services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = IdentityConstants.ApplicationScheme;
                options.DefaultChallengeScheme = IdentityConstants.ApplicationScheme;
            })
            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(
                IdentityConstants.ApplicationScheme, _ => { })
            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(
                OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, _ => { });
        builder.Services.AddAuthorization();

        var processor = new TestCaseCommandProcessor { Status = status };
        builder.Services.AddSingleton<ICaseCommandProcessor>(processor);

        var application = builder.Build();
        application.UseAuthentication();
        application.UseAuthorization();
        application.MapCaseCommandEndpoints();
        await application.StartAsync().ConfigureAwait(false);

        return new TestCommandHost(application, application.GetTestClient(), processor);
    }

    public Task<HttpResponseMessage> SendBearerAsync(HttpRequestMessage request)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestAuthenticationHandler.BearerToken);
        return Client.SendAsync(request);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _application.DisposeAsync().ConfigureAwait(false);
    }
}

internal sealed class TestCaseCommandProcessor : ICaseCommandProcessor
{
    public int Calls { get; private set; }

    public CaseCommandProcessingStatus Status { get; set; } = CaseCommandProcessingStatus.Accepted;

    public ActorContext? LastActor { get; private set; }

    public Guid LastCaseId { get; private set; }

    public int LastSchemaVersion { get; private set; }

    public long LastAggregateVersion { get; private set; }

    public CaseCommand? LastCommand { get; private set; }

    public ValueTask<CaseCommandProcessingResult> ProcessAsync(
        ActorContext actor,
        Guid caseId,
        int wireCommandSchemaVersion,
        CaseCommand command,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls++;
        LastActor = actor;
        LastCaseId = caseId;
        LastSchemaVersion = wireCommandSchemaVersion;
        LastAggregateVersion = command.ExpectedVersion;
        LastCommand = command;

        return ValueTask.FromResult(Status switch
        {
            CaseCommandProcessingStatus.Accepted or CaseCommandProcessingStatus.Replayed =>
                new CaseCommandProcessingResult(
                    Status,
                    caseId,
                    command.CommandId,
                    ImmutableArray.Create(Guid.NewGuid()),
                    command.ExpectedVersion,
                    1),
            _ => new CaseCommandProcessingResult(
                Status,
                caseId,
                command.CommandId,
                ImmutableArray<Guid>.Empty,
                null,
                null,
                DenialAuditDurable: Status == CaseCommandProcessingStatus.Denied),
        });
    }
}
