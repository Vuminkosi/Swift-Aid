namespace SwiftAid.Application.Ellai;

/// <summary>Protects a location search for later authorization rechecks without storing plaintext in the tool trace.</summary>
public interface IEllaiLocationReplayProtector
{
    string Protect(EllaiToolContext context, IReadOnlyDictionary<string, string> arguments);
    IReadOnlyDictionary<string, string>? Unprotect(EllaiToolContext context, string token);
}
