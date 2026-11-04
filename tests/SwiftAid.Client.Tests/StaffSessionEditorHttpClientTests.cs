using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SwiftAid.Client.Presentation;
using SwiftAid.Contracts.Sessions;

namespace SwiftAid.Client.Tests;

public sealed class StaffSessionEditorHttpClientTests
{
    private static readonly Guid Id = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public async Task ReviewSendsExpectedVersionAndRejectsMismatchedReceipt()
    {
        var handler = new Handler(new SessionCommandResult(Id, 4, "Reviewed"));
        var client = Create(handler);
        var result = await client.ExecuteAsync(new ReviewSessionCommand(Id, 3, true, "review", DateTimeOffset.UtcNow));
        Assert.True(result.Succeeded);
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.EndsWith($"/api/v1/sessions/{Id:D}/review", handler.Uri!.AbsolutePath);
        using var json = JsonDocument.Parse(handler.Body!);
        Assert.Equal(3, json.RootElement.GetProperty("expectedVersion").GetInt64());
        Assert.True(json.RootElement.GetProperty("acknowledgeSelfReview").GetBoolean());
        handler.Value = new SessionCommandResult(Guid.NewGuid(), 4, "Reviewed");
        Assert.False((await client.ExecuteAsync(new ReviewSessionCommand(Id, 3, true, "review", DateTimeOffset.UtcNow))).Succeeded);
    }

    [Fact]
    public async Task CreateCannotLoseItsClientIdentityAndConflictsRemainConflicts()
    {
        var handler = new Handler(new SessionCommandResult(Id, 4, "Reviewed")) { Status = HttpStatusCode.Conflict };
        var client = Create(handler);
        var conflict = await client.ExecuteAsync(new ScheduleSessionCommand(Id, 3, "schedule", DateTimeOffset.UtcNow));
        Assert.Equal(UiCapabilityResultCode.Conflict, conflict.Code);
        var create = new CreateSessionDraftCommand(Id, "org", "Title", "Summary", "Audience", [], "SZ", "region", "human",
            "Presenter", "English", DateTimeOffset.UtcNow.AddDays(1), "UTC", 60, null, [], SessionVisibility.Public,
            SessionChannelType.None, null, [], "reason", DateTimeOffset.UtcNow);
        handler.Uri = null;
        Assert.Equal(UiCapabilityResultCode.Unavailable, (await client.ExecuteAsync(create)).Code);
        Assert.Null(handler.Uri);
    }

    [Fact]
    public async Task RejectsCapabilitiesForAnotherSessionOrFalseCurrentVersion()
    {
        var handler = new Handler(new StaffSessionManagementCapabilities(false, true, false, false, false, false,
            Guid.NewGuid(), 3, "Draft", true));
        var client = Create(handler);
        Assert.False((await client.GetManagementCapabilitiesAsync(Id, 3)).Succeeded);
        handler.Value = new StaffSessionManagementCapabilities(false, true, false, false, false, false, Id, 4, "Draft", true);
        Assert.False((await client.GetManagementCapabilitiesAsync(Id, 3)).Succeeded);
    }

    private static StaffSessionEditorHttpClient Create(Handler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://example.test/") });

    private sealed class Handler(object value) : HttpMessageHandler
    {
        public object Value { get; set; } = value;
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public Uri? Uri { get; set; }
        public HttpMethod? Method { get; private set; }
        public string? Body { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri = request.RequestUri;
            Method = request.Method;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(Status) { Content = JsonContent.Create(Value, Value.GetType()) };
        }
    }
}
