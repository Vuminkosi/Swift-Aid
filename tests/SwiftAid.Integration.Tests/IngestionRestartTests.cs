using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NATS.Client.Core;
using NATS.Client.JetStream.Models;
using NATS.Net;
using Npgsql;
using SwiftAid.Application.Abstractions;
using SwiftAid.Application.Intake.Imports;
using SwiftAid.Contracts.Events;
using SwiftAid.Infrastructure.Messaging;

namespace SwiftAid.Integration.Tests;

/// <summary>Explicit opt-in hard process failure on named, task-labelled disposable containers only.</summary>
[Collection(IntakePostgresCollection.Name)]
public sealed class IngestionRestartTests
{
    [IngestionRestartFact]
    public async Task Committed_http_import_survives_postgres_process_loss_and_resumes_without_duplicates()
    {
        await using var fixture = await CaseImportPostgresTests.Fixture.CreateAsync();
        var connection = new NpgsqlConnectionStringBuilder(fixture.Db.Database.GetConnectionString());
        Assert.Equal("127.0.0.1", connection.Host);
        Assert.Equal(55439, connection.Port);
        Assert.Equal("swiftaid_intake_test", connection.Database);
        const string container = "swiftaid-ingestion-test-pg";
        await RequireDisposableAsync(container);
        var importId = Guid.NewGuid();
        var firstCommand = Guid.NewGuid();
        var nextCommand = Guid.NewGuid();
        await using (var host = await CaseImportEndpointTests.Host.StartAsync(fixture))
        {
            var first = await SendAsync(host, importId, 1, false, firstCommand);
            Assert.Equal("Accepted", Assert.Single(first.Rows).Status);
            Assert.Equal(2, first.Summary!.NextRow);
        }

        // Simulate an unclean database process exit, not a graceful transaction shutdown.
        await DockerAsync("kill", "--signal", "KILL", container);
        await DockerAsync("start", container);
        NpgsqlConnection.ClearAllPools();
        connection.Timeout = 1;
        var ready = false;
        for (var attempt = 0; attempt < 30 && !ready; attempt++)
        {
            try
            {
                await using var probe = new NpgsqlConnection(connection.ConnectionString);
                await probe.OpenAsync();
                ready = true;
            }
            catch (NpgsqlException) { await Task.Delay(200); }
        }
        Assert.True(ready, "Disposable PostgreSQL did not recover after process loss.");
        await using (var host = await CaseImportEndpointTests.Host.StartAsync(fixture))
        {
            var replay = await SendAsync(host, importId, 1, false, firstCommand);
            Assert.Equal("Replayed", Assert.Single(replay.Rows).Status);
            var resumed = await SendAsync(host, importId, 2, true, nextCommand);
            Assert.Equal("Accepted", Assert.Single(resumed.Rows).Status);
            Assert.Equal("Completed", resumed.Summary!.State);
            Assert.Equal(2, resumed.Summary.Accepted);
        }
        var commands = new[] { firstCommand, nextCommand };
        Assert.Equal(2, await fixture.Db.CommandReceipts.CountAsync(row => commands.Contains(row.CommandId)));
        var events = await fixture.Db.CaseEvents.Where(row => commands.Contains(row.CommandId)).Select(row => row.EventId).ToArrayAsync();
        Assert.Equal(4, events.Length);
        Assert.Equal(4, await fixture.Db.DomainOutbox.CountAsync(row => events.Contains(row.EventId)));
    }

    [IngestionRestartFact]
    public async Task Broker_process_loss_preserves_stored_notice_and_duplicate_acknowledgement()
    {
        const string container = "swiftaid-ingestion-test-nats";
        await RequireDisposableAsync(container);
        Assert.Equal("nats://127.0.0.1:44229", Environment.GetEnvironmentVariable("SWIFTAID_TEST_INGESTION_NATS_URL"));
        var options = new NatsOpts { Url = "nats://127.0.0.1:44229", RequestTimeout = TimeSpan.FromSeconds(3) };
        var notice = new IngestionNotice(1, Guid.NewGuid(), "intake", Guid.NewGuid(), 1, 1, "IntakeReceived", DateTimeOffset.UtcNow);
        EventPublishResult stored;
        await using (var client = new NatsClient(options))
        {
            await client.CreateJetStreamContext().CreateStreamAsync(new StreamConfig
            {
                Name = IngestionNotice.StreamName, Subjects = [$"{IngestionNotice.SubjectPrefix}.>"],
                MaxMsgs = 10000, MaxBytes = 16 * 1024 * 1024, MaxMsgSize = 4096, DuplicateWindow = TimeSpan.FromMinutes(2)
            });
            stored = await new JetStreamIngestionNoticePublisher(client).PublishAsync(notice, default);
            Assert.Equal(EventPublishStatus.AcknowledgedByJetStream, stored.Status);
        }
        await DockerAsync("kill", "--signal", "KILL", container);
        await DockerAsync("start", container);
        await using var restarted = new NatsClient(options);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var duplicate = await new JetStreamIngestionNoticePublisher(restarted).PublishAsync(notice, timeout.Token);
        Assert.Equal(EventPublishStatus.AcknowledgedByJetStream, duplicate.Status);
        Assert.Equal(stored.StreamSequence, duplicate.StreamSequence);
    }

    private static async Task<CaseImportBatchResult> SendAsync(CaseImportEndpointTests.Host host, Guid importId, long row, bool final, Guid command)
    {
        using var request = CaseImportEndpointTests.Host.Request(importId, row, final, CaseImportPostgresTests.Context, CaseImportPostgresTests.Csv(command));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "synthetic-import-token");
        using var response = await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonSerializer.Deserialize<CaseImportBatchResult>(await response.Content.ReadAsStringAsync(), CaseImportEndpointTests.Host.Json)!;
    }

    private static async Task RequireDisposableAsync(string container)
    {
        var label = await DockerAsync("inspect", "--format", "{{ index .Config.Labels \"swiftaid.task\" }}", container);
        Assert.StartsWith("ingestion-", label.Trim(), StringComparison.Ordinal);
    }

    private static async Task<string> DockerAsync(params string[] arguments)
    {
        var start = new ProcessStartInfo("docker") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        Assert.True(process.ExitCode == 0, await stderr);
        return await stdout;
    }
}

public sealed class IngestionRestartFactAttribute : FactAttribute
{
    public IngestionRestartFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("SWIFTAID_RUN_INGESTION_RESTART") != "1" ||
            Environment.GetEnvironmentVariable("SWIFTAID_RUN_INTAKE_POSTGRES_FIXTURES") != "1")
            Skip = "Explicitly opt in to process failure on labelled disposable ingestion containers.";
    }
}
