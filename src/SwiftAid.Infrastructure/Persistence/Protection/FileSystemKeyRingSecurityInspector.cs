using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace SwiftAid.Infrastructure.Persistence.Protection;

internal sealed class FileSystemKeyRingSecurityInspector : IKeyRingSecurityInspector
{
    // TrustedInstaller may own operating-system ancestors (for example, the volume root).
    // This exact SID is trusted only for ancestor ownership/replacement checks, never for
    // the protected key-ring directory or its key files.
    private const string TrustedInstallerSid = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";

    private static readonly ImmutableHashSet<string> EmptyTrustedSids =
        ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal);

    public KeyRingSecuritySnapshot Inspect(string keyRingPath)
    {
        try
        {
            var platform = OperatingSystem.IsWindows()
                ? KeyRingPlatform.Windows
                : OperatingSystem.IsLinux()
                    ? KeyRingPlatform.Linux
                    : KeyRingPlatform.Unsupported;
            if (platform == KeyRingPlatform.Unsupported)
            {
                return new KeyRingSecuritySnapshot(
                    platform,
                    ImmutableArray<KeyRingEntrySecurity>.Empty,
                    EmptyTrustedSids,
                    EmptyTrustedSids);
            }

            var (trustedSids, trustedAncestorSids) = platform == KeyRingPlatform.Windows
                ? ReadTrustedWindowsSids()
                : (EmptyTrustedSids, EmptyTrustedSids);
            var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(keyRingPath));
            var rootPath = Path.GetPathRoot(fullPath);
            if (string.IsNullOrWhiteSpace(rootPath))
            {
                throw new KeyRingSecurityException();
            }

            var entries = ImmutableArray.CreateBuilder<KeyRingEntrySecurity>();
            var components = fullPath[rootPath.Length..]
                .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
            if (components.Length == 0)
            {
                entries.Add(ReadEntry(rootPath, KeyRingEntryKind.KeyRingDirectory, platform));
            }
            else
            {
                var currentPath = rootPath;
                entries.Add(ReadEntry(currentPath, KeyRingEntryKind.AncestorDirectory, platform));
                for (var index = 0; index < components.Length; index++)
                {
                    currentPath = Path.Combine(currentPath, components[index]);
                    var kind = index == components.Length - 1
                        ? KeyRingEntryKind.KeyRingDirectory
                        : KeyRingEntryKind.AncestorDirectory;
                    entries.Add(ReadEntry(currentPath, kind, platform));
                }

                foreach (var childPath in Directory.EnumerateFileSystemEntries(fullPath)
                             .OrderBy(path => path, StringComparer.Ordinal))
                {
                    entries.Add(ReadEntry(childPath, KeyRingEntryKind.KeyFile, platform));
                }
            }

            return new KeyRingSecuritySnapshot(platform, entries.ToImmutable(), trustedSids, trustedAncestorSids);
        }
        catch (KeyRingSecurityException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or System.Security.SecurityException or
                ArgumentException or NotSupportedException or InvalidOperationException or
                System.ComponentModel.Win32Exception or IdentityNotMappedException)
        {
            throw new KeyRingSecurityException();
        }
    }

    private static KeyRingEntrySecurity ReadEntry(
        string path,
        KeyRingEntryKind kind,
        KeyRingPlatform platform)
    {
        var attributes = File.GetAttributes(path);
        var isDirectory = (attributes & FileAttributes.Directory) != 0;
        var isReparsePoint = (attributes & FileAttributes.ReparsePoint) != 0;
        if (isReparsePoint)
        {
            throw new KeyRingSecurityException();
        }

        var unixMode = platform == KeyRingPlatform.Linux
            ? ReadLinuxMode(path)
            : (UnixFileMode?)null;
        var ownerSid = (string?)null;
        var accessRules = ImmutableArray<KeyRingAccessRuleSecurity>.Empty;
        if (platform == KeyRingPlatform.Windows)
        {
            (ownerSid, accessRules) = ReadWindowsSecurity(path, isDirectory);
        }

        return new KeyRingEntrySecurity(kind, isDirectory, isReparsePoint, ownerSid, unixMode, accessRules);
    }

    private static UnixFileMode ReadLinuxMode(string path)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new KeyRingSecurityException();
        }

        return File.GetUnixFileMode(path);
    }

    private static (string OwnerSid, ImmutableArray<KeyRingAccessRuleSecurity> AccessRules) ReadWindowsSecurity(
        string path,
        bool isDirectory)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new KeyRingSecurityException();
        }

        FileSystemSecurity securityDescriptor = isDirectory
            ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access)
            : new FileInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        if (securityDescriptor.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner)
        {
            throw new KeyRingSecurityException();
        }

        var rules = securityDescriptor.GetAccessRules(
            includeExplicit: true,
            includeInherited: true,
            targetType: typeof(SecurityIdentifier));
        var snapshots = ImmutableArray.CreateBuilder<KeyRingAccessRuleSecurity>(rules.Count);
        foreach (AuthorizationRule rule in rules)
        {
            if (rule is not FileSystemAccessRule fileSystemRule ||
                fileSystemRule.IdentityReference is not SecurityIdentifier trustee)
            {
                throw new KeyRingSecurityException();
            }

            var isEffectiveOnEntry = (fileSystemRule.PropagationFlags & PropagationFlags.InheritOnly) == 0;
            snapshots.Add(new KeyRingAccessRuleSecurity(
                trustee.Value,
                fileSystemRule.AccessControlType == AccessControlType.Allow,
                MapWindowsRights(fileSystemRule.FileSystemRights, isDirectory),
                isEffectiveOnEntry));
        }

        return (owner.Value, snapshots.ToImmutable());
    }

    private static KeyRingAccessRights MapWindowsRights(FileSystemRights rights, bool isDirectory)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new KeyRingSecurityException();
        }

        var mapped = KeyRingAccessRights.None;
        if ((rights & FileSystemRights.Read) != 0)
        {
            mapped |= KeyRingAccessRights.Read;
        }

        var ordinaryWriteRights = FileSystemRights.Write;
        if (isDirectory)
        {
            // On directories, AppendData's numeric alias is CreateDirectories. Keep that
            // capability separate so an ancestor's create-child-only grant is not mistaken
            // for permission to modify or replace an existing path component.
            ordinaryWriteRights &= ~FileSystemRights.CreateDirectories;
        }

        if ((rights & ordinaryWriteRights) != 0)
        {
            mapped |= KeyRingAccessRights.Write;
        }

        if (isDirectory && (rights & FileSystemRights.CreateDirectories) != 0)
        {
            mapped |= KeyRingAccessRights.CreateDirectories;
        }

        if ((rights & (FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles)) != 0)
        {
            mapped |= KeyRingAccessRights.Delete;
        }

        if ((rights & FileSystemRights.ChangePermissions) != 0)
        {
            mapped |= KeyRingAccessRights.ChangePermissions;
        }

        if ((rights & FileSystemRights.TakeOwnership) != 0)
        {
            mapped |= KeyRingAccessRights.TakeOwnership;
        }

        return mapped;
    }

    private static (ImmutableHashSet<string> TrustedSids, ImmutableHashSet<string> TrustedAncestorSids)
        ReadTrustedWindowsSids()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new KeyRingSecurityException();
        }

        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        if (identity.User is null)
        {
            throw new KeyRingSecurityException();
        }

        // The process user is the configured service identity. Add only service SIDs that are
        // actually present in the current token and resolve to an NT SERVICE account name.
        var trustedSids = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        trustedSids.Add(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, domainSid: null).Value);
        trustedSids.Add(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, domainSid: null).Value);
        trustedSids.Add(identity.User.Value);
        if (identity.Groups is not null)
        {
            foreach (var group in identity.Groups.OfType<SecurityIdentifier>())
            {
                if (group.Translate(typeof(NTAccount)) is NTAccount account &&
                    IsTrustedServiceSid(group.Value, account.Value))
                {
                    trustedSids.Add(group.Value);
                }
            }
        }

        var trusted = trustedSids.ToImmutable();
        return (trusted, trusted.Add(TrustedInstallerSid));
    }

    internal static bool IsTrustedServiceSid(string sid, string accountName)
    {
        const string serviceSidPrefix = "S-1-5-80";
        const string serviceAccountPrefix = "NT SERVICE\\";

        if (string.Equals(sid, TrustedInstallerSid, StringComparison.Ordinal) ||
            !accountName.StartsWith(serviceAccountPrefix, StringComparison.OrdinalIgnoreCase) ||
            accountName.Length == serviceAccountPrefix.Length)
        {
            return false;
        }

        var components = sid.Split('-');
        if (components.Length != 9 ||
            !string.Equals(string.Join("-", components.Take(4)), serviceSidPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        return components.Skip(4).All(component =>
            uint.TryParse(component, NumberStyles.None, CultureInfo.InvariantCulture, out _));
    }
}
