using System.Collections.Immutable;
using System.IO;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("SwiftAid.Integration.Tests")]

namespace SwiftAid.Infrastructure.Persistence.Protection;

internal interface IKeyRingSecurityValidator
{
    void Validate(string keyRingPath);
}

internal interface IKeyRingSecurityInspector
{
    KeyRingSecuritySnapshot Inspect(string keyRingPath);
}

internal enum KeyRingPlatform
{
    Unsupported,
    Windows,
    Linux
}

internal enum KeyRingEntryKind
{
    AncestorDirectory,
    KeyRingDirectory,
    KeyFile
}

[Flags]
internal enum KeyRingAccessRights
{
    None = 0,
    Read = 1 << 0,
    Write = 1 << 1,
    Delete = 1 << 2,
    ChangePermissions = 1 << 3,
    TakeOwnership = 1 << 4,
    CreateDirectories = 1 << 5
}

internal sealed record KeyRingSecuritySnapshot(
    KeyRingPlatform Platform,
    ImmutableArray<KeyRingEntrySecurity> Entries,
    ImmutableHashSet<string> TrustedWindowsSids,
    ImmutableHashSet<string> TrustedWindowsAncestorSids);

internal sealed record KeyRingEntrySecurity(
    KeyRingEntryKind Kind,
    bool IsDirectory,
    bool IsReparsePoint,
    string? OwnerSid,
    UnixFileMode? UnixMode,
    ImmutableArray<KeyRingAccessRuleSecurity> AccessRules);

internal sealed record KeyRingAccessRuleSecurity(
    string TrusteeSid,
    bool AllowsAccess,
    KeyRingAccessRights Rights,
    bool IsEffectiveOnEntry);

internal sealed class KeyRingSecurityException : Exception
{
    public KeyRingSecurityException()
        : base("The Data Protection key ring failed filesystem security validation.")
    {
    }
}
