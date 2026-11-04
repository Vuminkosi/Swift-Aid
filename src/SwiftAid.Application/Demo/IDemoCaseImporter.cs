namespace SwiftAid.Application.Demo;

/// <summary>Development-only demo case import. Never enabled in production.</summary>
public interface IDemoCaseImporter
{
    Task<DemoCaseImportReport> ImportAsync(CancellationToken cancellationToken);
}

/// <summary>Outcome of one demo import attempt.</summary>
public sealed record DemoCaseImportReport(
    int TotalRecords,
    int Inserted,
    int Invalid,
    bool AlreadySeeded);
