namespace SwiftAid.Application.Cases.Commands;

/// <summary>The persistence lease access mode required for a case command transaction.</summary>
public enum CaseLeaseMode
{
    Read,
    Write
}
