using System.Collections.Immutable;
using System.IO;

namespace SwiftAid.Infrastructure.Persistence.Protection;

internal sealed class KeyRingSecurityValidator(IKeyRingSecurityInspector inspector)
    : IKeyRingSecurityValidator
{
    public void Validate(string keyRingPath)
    {
        if (string.IsNullOrWhiteSpace(keyRingPath) || !Path.IsPathFullyQualified(keyRingPath))
        {
            throw new KeyRingSecurityException();
        }

        try
        {
            var snapshot = inspector.Inspect(keyRingPath);
            KeyRingSecurityPolicy.Validate(snapshot);
        }
        catch (KeyRingSecurityException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or System.Security.SecurityException or
                ArgumentException or NotSupportedException or InvalidOperationException or
                System.ComponentModel.Win32Exception or System.Security.Principal.IdentityNotMappedException)
        {
            throw new KeyRingSecurityException();
        }
    }
}

internal static class KeyRingSecurityPolicy
{
    private const UnixFileMode LinuxWritableByGroupOrOthers =
        UnixFileMode.GroupWrite | UnixFileMode.OtherWrite;

    private const UnixFileMode LinuxGroupOrOtherOrSpecialBits =
        UnixFileMode.SetUser | UnixFileMode.SetGroup | UnixFileMode.StickyBit |
        UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    private const KeyRingAccessRights WindowsProtectedEntrySensitiveRights =
        KeyRingAccessRights.Read | KeyRingAccessRights.Write | KeyRingAccessRights.Delete |
        KeyRingAccessRights.ChangePermissions | KeyRingAccessRights.TakeOwnership |
        KeyRingAccessRights.CreateDirectories;

    private const KeyRingAccessRights WindowsAncestorReplacementRights =
        KeyRingAccessRights.Write | KeyRingAccessRights.Delete |
        KeyRingAccessRights.ChangePermissions | KeyRingAccessRights.TakeOwnership;

    public static void Validate(KeyRingSecuritySnapshot snapshot)
    {
        if (snapshot.Entries.IsDefaultOrEmpty ||
            snapshot.Entries.Count(entry => entry.Kind == KeyRingEntryKind.KeyRingDirectory) != 1 ||
            snapshot.Entries[0].Kind != KeyRingEntryKind.AncestorDirectory ||
            snapshot.Entries.Any(entry => entry.IsReparsePoint))
        {
            throw new KeyRingSecurityException();
        }

        var keyRingIndex = Array.FindIndex(
            snapshot.Entries.ToArray(),
            entry => entry.Kind == KeyRingEntryKind.KeyRingDirectory);
        if (keyRingIndex < 0 || !snapshot.Entries[keyRingIndex].IsDirectory ||
            snapshot.Entries.Take(keyRingIndex).Any(entry =>
                entry.Kind != KeyRingEntryKind.AncestorDirectory || !entry.IsDirectory) ||
            snapshot.Entries.Skip(keyRingIndex + 1).Any(entry =>
                entry.Kind != KeyRingEntryKind.KeyFile || entry.IsDirectory))
        {
            throw new KeyRingSecurityException();
        }

        switch (snapshot.Platform)
        {
            case KeyRingPlatform.Linux:
                ValidateLinux(snapshot, keyRingIndex);
                break;
            case KeyRingPlatform.Windows:
                ValidateWindows(snapshot, keyRingIndex);
                break;
            default:
                throw new KeyRingSecurityException();
        }
    }

    private static void ValidateLinux(KeyRingSecuritySnapshot snapshot, int keyRingIndex)
    {
        // UnixFileMode does not expose owner identity; deployment must run under a dedicated
        // unprivileged account. The mode check does not prove ownership or exclude root/DAC override.
        if (!snapshot.TrustedWindowsSids.IsEmpty || !snapshot.TrustedWindowsAncestorSids.IsEmpty ||
            snapshot.Entries.Any(entry => entry.OwnerSid is not null || !entry.AccessRules.IsDefaultOrEmpty))
        {
            throw new KeyRingSecurityException();
        }

        for (var index = 0; index < keyRingIndex; index++)
        {
            var entry = snapshot.Entries[index];
            if (!entry.IsDirectory || entry.UnixMode is not { } mode ||
                (mode & LinuxWritableByGroupOrOthers) != 0)
            {
                throw new KeyRingSecurityException();
            }
        }

        var keyRing = snapshot.Entries[keyRingIndex];
        var expectedDirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        if (keyRing.UnixMode != expectedDirectoryMode)
        {
            throw new KeyRingSecurityException();
        }

        foreach (var keyFile in snapshot.Entries.Skip(keyRingIndex + 1))
        {
            if (keyFile.UnixMode is not { } mode ||
                (mode & LinuxGroupOrOtherOrSpecialBits) != 0 ||
                (mode & (UnixFileMode.UserRead | UnixFileMode.UserExecute)) != UnixFileMode.UserRead)
            {
                throw new KeyRingSecurityException();
            }
        }
    }

    private static void ValidateWindows(KeyRingSecuritySnapshot snapshot, int keyRingIndex)
    {
        if (snapshot.Entries.Any(entry => entry.UnixMode is not null))
        {
            throw new KeyRingSecurityException();
        }

        var trustedSids = snapshot.TrustedWindowsSids;
        var trustedAncestorSids = snapshot.TrustedWindowsAncestorSids;
        if (trustedSids.IsEmpty || trustedAncestorSids.IsEmpty)
        {
            throw new KeyRingSecurityException();
        }

        for (var index = 0; index < snapshot.Entries.Length; index++)
        {
            var entry = snapshot.Entries[index];
            var isAncestor = index < keyRingIndex;
            var trustedEntryOwners = isAncestor ? trustedAncestorSids : trustedSids;
            var trustedEntryRules = isAncestor ? trustedAncestorSids : trustedSids;
            var sensitiveRights = isAncestor
                ? WindowsAncestorReplacementRights
                : WindowsProtectedEntrySensitiveRights;

            if (string.IsNullOrWhiteSpace(entry.OwnerSid) || !trustedEntryOwners.Contains(entry.OwnerSid) ||
                entry.AccessRules.IsDefaultOrEmpty || entry.AccessRules.Any(rule =>
                    rule.IsEffectiveOnEntry && rule.AllowsAccess &&
                    !trustedEntryRules.Contains(rule.TrusteeSid) &&
                    (rule.Rights & sensitiveRights) != 0))
            {
                throw new KeyRingSecurityException();
            }
        }
    }
}
