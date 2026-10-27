using System.Collections.Immutable;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SwiftAid.Application.Cases.Commands;
using SwiftAid.Infrastructure.Configuration;
using SwiftAid.Infrastructure.Persistence.Protection;

namespace SwiftAid.Integration.Tests;

public sealed class ProtectedPayloadKeyRingSecurityTests
{
    private const KeyRingAccessRights AllSensitiveRights =
        KeyRingAccessRights.Read | KeyRingAccessRights.Write | KeyRingAccessRights.Delete |
        KeyRingAccessRights.ChangePermissions | KeyRingAccessRights.TakeOwnership;

    private static readonly UnixFileMode SafeLinuxAncestorMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
        UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    private static readonly UnixFileMode SafeLinuxKeyRingMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private static readonly UnixFileMode SafeLinuxKeyFileMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite;

    [Fact]
    public void Disabled_protection_returns_before_filesystem_inspection()
    {
        var inspector = new RecordingInspector(SafeLinuxSnapshot());
        var validator = new ProtectedPayloadOptionsValidator(
            new TestHostEnvironment(Environments.Production),
            new KeyRingSecurityValidator(inspector));

        var result = validator.Validate(Options.DefaultName, new ProtectedPayloadOptions
        {
            Enabled = false,
            KeysPath = "not a path"
        });

        Assert.True(result.Succeeded);
        Assert.Equal(0, inspector.InspectionCount);
    }

    [Fact]
    public void Inspection_errors_are_sanitized()
    {
        var validator = new KeyRingSecurityValidator(new ThrowingInspector());

        var privatePath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "private-key-path"));
        var exception = Assert.Throws<KeyRingSecurityException>(() => validator.Validate(privatePath));

        Assert.Equal("The Data Protection key ring failed filesystem security validation.", exception.Message);
        Assert.DoesNotContain("private-key-path", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Linux_policy_requires_private_key_ring_and_non_writable_ancestors()
    {
        var safeSnapshot = SafeLinuxSnapshot();
        KeyRingSecurityPolicy.Validate(safeSnapshot);

        var unsafeAncestor = safeSnapshot with
        {
            Entries = safeSnapshot.Entries.SetItem(
                1,
                safeSnapshot.Entries[1] with
                {
                    UnixMode = SafeLinuxAncestorMode | UnixFileMode.OtherWrite
                })
        };
        Assert.Throws<KeyRingSecurityException>(() => KeyRingSecurityPolicy.Validate(unsafeAncestor));

        var unsafeDirectory = safeSnapshot with
        {
            Entries = safeSnapshot.Entries.SetItem(
                2,
                safeSnapshot.Entries[2] with
                {
                    UnixMode = SafeLinuxKeyRingMode | UnixFileMode.GroupRead
                })
        };
        Assert.Throws<KeyRingSecurityException>(() => KeyRingSecurityPolicy.Validate(unsafeDirectory));

        var unsafeFile = safeSnapshot with
        {
            Entries = safeSnapshot.Entries.SetItem(
                3,
                safeSnapshot.Entries[3] with
                {
                    UnixMode = SafeLinuxKeyFileMode | UnixFileMode.OtherRead
                })
        };
        Assert.Throws<KeyRingSecurityException>(() => KeyRingSecurityPolicy.Validate(unsafeFile));

        var linkedDirectory = safeSnapshot with
        {
            Entries = safeSnapshot.Entries.SetItem(
                2,
                safeSnapshot.Entries[2] with { IsReparsePoint = true })
        };
        Assert.Throws<KeyRingSecurityException>(() => KeyRingSecurityPolicy.Validate(linkedDirectory));
    }

    [Fact]
    public void Windows_policy_rejects_untrusted_access_to_key_material_and_path_replacement()
    {
        const string serviceSid = "S-1-5-80-1-2-3-4-5";
        const string administratorsSid = "S-1-5-32-544";
        const string systemSid = "S-1-5-18";
        const string untrustedSid = "S-1-5-11";
        var trusted = ImmutableHashSet.Create(StringComparer.Ordinal, serviceSid, administratorsSid, systemSid);
        var safeRules = ImmutableArray.Create(
            new KeyRingAccessRuleSecurity(serviceSid, true, AllSensitiveRights, true),
            new KeyRingAccessRuleSecurity(administratorsSid, true, AllSensitiveRights, true),
            new KeyRingAccessRuleSecurity(systemSid, true, AllSensitiveRights, true));
        var snapshot = WindowsSnapshot(trusted, safeRules, safeRules);

        KeyRingSecurityPolicy.Validate(snapshot);

        var ancestorCreateOnly = snapshot with
        {
            Entries = snapshot.Entries.SetItem(
                1,
                snapshot.Entries[1] with
                {
                    AccessRules = [new KeyRingAccessRuleSecurity(
                        untrustedSid,
                        true,
                        KeyRingAccessRights.CreateDirectories,
                        true)]
                })
        };
        KeyRingSecurityPolicy.Validate(ancestorCreateOnly);

        var keyRingCreateOnly = ancestorCreateOnly with
        {
            Entries = ancestorCreateOnly.Entries.SetItem(
                2,
                ancestorCreateOnly.Entries[2] with
                {
                    AccessRules = [new KeyRingAccessRuleSecurity(
                        untrustedSid,
                        true,
                        KeyRingAccessRights.CreateDirectories,
                        true)]
                })
        };
        Assert.Throws<KeyRingSecurityException>(() => KeyRingSecurityPolicy.Validate(keyRingCreateOnly));

        var untrustedRead = snapshot with
        {
            Entries = snapshot.Entries.SetItem(
                2,
                snapshot.Entries[2] with
                {
                    AccessRules = safeRules.Add(
                        new KeyRingAccessRuleSecurity(untrustedSid, true, KeyRingAccessRights.Read, true))
                })
        };
        Assert.Throws<KeyRingSecurityException>(() => KeyRingSecurityPolicy.Validate(untrustedRead));

        var untrustedAncestorWrite = snapshot with
        {
            Entries = snapshot.Entries.SetItem(
                1,
                snapshot.Entries[1] with
                {
                    AccessRules = [new KeyRingAccessRuleSecurity(
                        untrustedSid,
                        true,
                        KeyRingAccessRights.Write | KeyRingAccessRights.Delete,
                        true)]
                })
        };
        Assert.Throws<KeyRingSecurityException>(() => KeyRingSecurityPolicy.Validate(untrustedAncestorWrite));

        foreach (var entryIndex in Enumerable.Range(0, snapshot.Entries.Length))
        {
            var untrustedOwner = snapshot with
            {
                Entries = snapshot.Entries.SetItem(
                    entryIndex,
                    snapshot.Entries[entryIndex] with { OwnerSid = untrustedSid })
            };
            Assert.Throws<KeyRingSecurityException>(() => KeyRingSecurityPolicy.Validate(untrustedOwner));
        }
    }

    [Fact]
    public void Windows_service_sid_selection_rejects_aggregate_and_malformed_sid_shapes()
    {
        const string specificServiceSid = "S-1-5-80-1-2-3-4-5";

        Assert.True(FileSystemKeyRingSecurityInspector.IsTrustedServiceSid(
            specificServiceSid,
            "NT SERVICE\\SwiftAid"));
        Assert.False(FileSystemKeyRingSecurityInspector.IsTrustedServiceSid(
            "S-1-5-80-0",
            "NT SERVICE\\ALL SERVICES"));
        Assert.False(FileSystemKeyRingSecurityInspector.IsTrustedServiceSid(
            "S-1-5-80-1-2-3-4-5-extra",
            "NT SERVICE\\NotAServiceSid"));
        Assert.False(FileSystemKeyRingSecurityInspector.IsTrustedServiceSid(
            "S-1-5-80-1-2-3-4-5",
            "BUILTIN\\Administrators"));
        Assert.False(FileSystemKeyRingSecurityInspector.IsTrustedServiceSid(
            "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464",
            "NT SERVICE\\TrustedInstaller"));
    }

    [Fact]
    public void Unsupported_platform_fails_closed()
    {
        var snapshot = SafeLinuxSnapshot() with { Platform = KeyRingPlatform.Unsupported };

        Assert.Throws<KeyRingSecurityException>(() => KeyRingSecurityPolicy.Validate(snapshot));
    }

    [Fact]
    public void Linux_inspection_does_not_repair_unsafe_permissions()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var keyRoot = PrivateKeyRingTestDirectory.CreateRoot("swiftaid-key-ring-security");
        try
        {
            var keyRingPath = PrivateKeyRingTestDirectory.CreateKeyDirectory(keyRoot);
            var originalMode = SafeLinuxKeyRingMode | UnixFileMode.GroupRead;
            File.SetUnixFileMode(keyRingPath, originalMode);
            var inspector = new FileSystemKeyRingSecurityInspector();
            var validator = new KeyRingSecurityValidator(inspector);

            Assert.Throws<KeyRingSecurityException>(() => validator.Validate(keyRingPath));
            Assert.Equal(originalMode, File.GetUnixFileMode(keyRingPath));
        }
        finally
        {
            PrivateKeyRingTestDirectory.DeleteRoot(keyRoot);
        }
    }

    [Fact]
    public void Windows_inherited_untrusted_directory_access_is_rejected_without_acl_repair()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var rootPath = PrivateKeyRingTestDirectory.CreateRoot("swiftaid-key-ring-inherited-acl");
        try
        {
            AddUntrustedDirectoryRule(
                rootPath,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit);
            var keyRingPath = Path.Combine(rootPath, "keys");
            Directory.CreateDirectory(keyRingPath);
            var beforeRoot = CaptureWindowsAcl(rootPath, isDirectory: true);
            var beforeKeyRing = CaptureWindowsAcl(keyRingPath, isDirectory: true);
            var validator = new KeyRingSecurityValidator(new FileSystemKeyRingSecurityInspector());

            Assert.Throws<KeyRingSecurityException>(() => validator.Validate(keyRingPath));
            Assert.Equal(beforeRoot, CaptureWindowsAcl(rootPath, isDirectory: true));
            Assert.Equal(beforeKeyRing, CaptureWindowsAcl(keyRingPath, isDirectory: true));
        }
        finally
        {
            PrivateKeyRingTestDirectory.DeleteRoot(rootPath);
        }
    }

    [Fact]
    public void Windows_real_inspector_allows_create_directories_only_on_task_owned_ancestor()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var rootPath = PrivateKeyRingTestDirectory.CreateRoot("swiftaid-key-ring-ancestor-create");
        try
        {
            AddUntrustedDirectoryRule(rootPath, FileSystemRights.CreateDirectories, InheritanceFlags.None);
            var keyRingPath = PrivateKeyRingTestDirectory.CreateKeyDirectory(rootPath);

            new KeyRingSecurityValidator(new FileSystemKeyRingSecurityInspector()).Validate(keyRingPath);
        }
        finally
        {
            PrivateKeyRingTestDirectory.DeleteRoot(rootPath);
        }
    }

    [Fact]
    public void Windows_untrusted_key_file_access_is_rejected_without_acl_repair()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var rootPath = PrivateKeyRingTestDirectory.CreateRoot("swiftaid-key-ring-file-acl");
        try
        {
            var keyRingPath = Path.Combine(rootPath, "keys");
            Directory.CreateDirectory(keyRingPath);
            var harmlessKeyFile = Path.Combine(keyRingPath, "harmless-key.xml");
            File.WriteAllText(harmlessKeyFile, "synthetic key fixture");
            AddUntrustedFileRule(harmlessKeyFile, FileSystemRights.ReadData | FileSystemRights.ReadAttributes);
            var before = CaptureWindowsAcl(harmlessKeyFile, isDirectory: false);
            var validator = new KeyRingSecurityValidator(new FileSystemKeyRingSecurityInspector());

            Assert.Throws<KeyRingSecurityException>(() => validator.Validate(keyRingPath));
            Assert.Equal(before, CaptureWindowsAcl(harmlessKeyFile, isDirectory: false));
        }
        finally
        {
            PrivateKeyRingTestDirectory.DeleteRoot(rootPath);
        }
    }

    [Fact]
    public void Real_key_ring_protection_round_trip_passes_filesystem_validation()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
        {
            return;
        }

        var rootPath = PrivateKeyRingTestDirectory.CreateRoot("swiftaid-key-ring-real-roundtrip");
        byte[]? plaintext = null;
        byte[]? protectedBytes = null;
        try
        {
            var keyRingPath = PrivateKeyRingTestDirectory.CreateKeyDirectory(rootPath);
            using var provider = ProtectedPayloadDataProtectionProviderFactory.Create(
                new ProtectedPayloadOptions
                {
                    Enabled = true,
                    ApplicationName = "SwiftAid.Integration.Tests",
                    KeysPath = keyRingPath,
                    AllowUnencryptedDevelopmentKeys = true,
                    MaximumPayloadBytes = 1024
                },
                new TestHostEnvironment(Environments.Development));
            var protector = provider.CreateProtector("key-ring-security-test");
            plaintext = System.Text.Encoding.UTF8.GetBytes("harmless startup validation sentinel");
            protectedBytes = protector.Protect(plaintext);
            var unprotected = protector.Unprotect(protectedBytes);
            try
            {
                Assert.Equal(plaintext, unprotected);
            }
            finally
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(unprotected);
            }

            new KeyRingSecurityValidator(new FileSystemKeyRingSecurityInspector()).Validate(keyRingPath);
        }
        finally
        {
            if (plaintext is not null)
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(plaintext);
            }

            if (protectedBytes is not null)
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(protectedBytes);
            }

            PrivateKeyRingTestDirectory.DeleteRoot(rootPath);
        }
    }

    [Fact]
    public async Task Startup_rechecks_key_ring_permissions_after_the_protection_round_trip()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
        {
            return;
        }

        var keyRoot = PrivateKeyRingTestDirectory.CreateRoot("swiftaid-key-ring-recheck");
        try
        {
            var keyRingPath = PrivateKeyRingTestDirectory.CreateKeyDirectory(keyRoot);
            var inspector = new SequenceInspector(
                SafeSnapshotForCurrentPlatform(),
                UnsafeSnapshotForCurrentPlatform());
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IKeyRingSecurityInspector>(inspector);
            services.AddSwiftAidInfrastructure(
                new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["CaseCommandProcessing:Enabled"] = "true",
                        ["ConnectionStrings:SwiftAid"] = "Host=127.0.0.1;Port=1;Database=swiftaid_di_test;Username=test",
                        ["DataProtection:Enabled"] = "true",
                        ["DataProtection:ApplicationName"] = "SwiftAid.Integration.Tests",
                        ["DataProtection:KeysPath"] = keyRingPath,
                        ["DataProtection:MaximumPayloadBytes"] = "1024",
                        ["DataProtection:AllowUnencryptedDevelopmentKeys"] = "true"
                    })
                    .Build(),
                new TestHostEnvironment(Environments.Development));

            await using var serviceProvider = services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateScopes = true,
                ValidateOnBuild = true
            });
            var hostedService = Assert.Single(serviceProvider.GetServices<IHostedService>());

            await Assert.ThrowsAsync<KeyRingSecurityException>(
                () => hostedService.StartAsync(CancellationToken.None));
            Assert.Equal(2, inspector.InspectionCount);
        }
        finally
        {
            PrivateKeyRingTestDirectory.DeleteRoot(keyRoot);
        }
    }

    private static KeyRingSecuritySnapshot SafeLinuxSnapshot() => new(
        KeyRingPlatform.Linux,
        [
            new KeyRingEntrySecurity(
                KeyRingEntryKind.AncestorDirectory,
                true,
                false,
                null,
                SafeLinuxAncestorMode,
                ImmutableArray<KeyRingAccessRuleSecurity>.Empty),
            new KeyRingEntrySecurity(
                KeyRingEntryKind.AncestorDirectory,
                true,
                false,
                null,
                SafeLinuxAncestorMode,
                ImmutableArray<KeyRingAccessRuleSecurity>.Empty),
            new KeyRingEntrySecurity(
                KeyRingEntryKind.KeyRingDirectory,
                true,
                false,
                null,
                SafeLinuxKeyRingMode,
                ImmutableArray<KeyRingAccessRuleSecurity>.Empty),
            new KeyRingEntrySecurity(
                KeyRingEntryKind.KeyFile,
                false,
                false,
                null,
                SafeLinuxKeyFileMode,
                ImmutableArray<KeyRingAccessRuleSecurity>.Empty)
        ],
        ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal),
        ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal));

    private static KeyRingSecuritySnapshot WindowsSnapshot(
        ImmutableHashSet<string> trustedSids,
        ImmutableArray<KeyRingAccessRuleSecurity> protectedEntryRules,
        ImmutableArray<KeyRingAccessRuleSecurity> ancestorRules) => new(
        KeyRingPlatform.Windows,
        [
            new KeyRingEntrySecurity(KeyRingEntryKind.AncestorDirectory, true, false, "S-1-5-80-1-2-3-4-5", null, ancestorRules),
            new KeyRingEntrySecurity(KeyRingEntryKind.AncestorDirectory, true, false, "S-1-5-80-1-2-3-4-5", null, ancestorRules),
            new KeyRingEntrySecurity(KeyRingEntryKind.KeyRingDirectory, true, false, "S-1-5-80-1-2-3-4-5", null, protectedEntryRules),
            new KeyRingEntrySecurity(KeyRingEntryKind.KeyFile, false, false, "S-1-5-80-1-2-3-4-5", null, protectedEntryRules)
        ],
        trustedSids,
        trustedSids.Add("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"));

    private static KeyRingSecuritySnapshot SafeSnapshotForCurrentPlatform() =>
        OperatingSystem.IsWindows()
            ? WindowsSnapshot(
                ImmutableHashSet.Create(StringComparer.Ordinal, "S-1-5-80-1-2-3-4-5", "S-1-5-32-544", "S-1-5-18"),
                [
                    new KeyRingAccessRuleSecurity("S-1-5-80-1-2-3-4-5", true, AllSensitiveRights, true),
                    new KeyRingAccessRuleSecurity("S-1-5-32-544", true, AllSensitiveRights, true),
                    new KeyRingAccessRuleSecurity("S-1-5-18", true, AllSensitiveRights, true)
                ],
                [
                    new KeyRingAccessRuleSecurity("S-1-5-80-1-2-3-4-5", true, AllSensitiveRights, true),
                    new KeyRingAccessRuleSecurity("S-1-5-32-544", true, AllSensitiveRights, true),
                    new KeyRingAccessRuleSecurity("S-1-5-18", true, AllSensitiveRights, true)
                ])
            : SafeLinuxSnapshot();

    private static KeyRingSecuritySnapshot UnsafeSnapshotForCurrentPlatform()
    {
        var safe = SafeSnapshotForCurrentPlatform();
        if (safe.Platform == KeyRingPlatform.Windows)
        {
            var untrustedRule = new KeyRingAccessRuleSecurity("S-1-5-11", true, KeyRingAccessRights.Read, true);
            return safe with
            {
                Entries = safe.Entries.SetItem(
                    2,
                    safe.Entries[2] with { AccessRules = safe.Entries[2].AccessRules.Add(untrustedRule) })
            };
        }

        return safe with
        {
            Entries = safe.Entries.SetItem(
                2,
                safe.Entries[2] with { UnixMode = SafeLinuxKeyRingMode | UnixFileMode.GroupRead })
        };
    }

    private sealed class RecordingInspector(KeyRingSecuritySnapshot snapshot) : IKeyRingSecurityInspector
    {
        public int InspectionCount { get; private set; }

        public KeyRingSecuritySnapshot Inspect(string keyRingPath)
        {
            InspectionCount++;
            return snapshot;
        }
    }

    private sealed class ThrowingInspector : IKeyRingSecurityInspector
    {
        public KeyRingSecuritySnapshot Inspect(string keyRingPath) =>
            throw new UnauthorizedAccessException($"Inspection failed for {keyRingPath}.");
    }

    private sealed class SequenceInspector : IKeyRingSecurityInspector
    {
        private readonly KeyRingSecuritySnapshot[] _snapshots;

        public SequenceInspector(params KeyRingSecuritySnapshot[] snapshots)
        {
            _snapshots = snapshots;
        }

        public int InspectionCount { get; private set; }

        public KeyRingSecuritySnapshot Inspect(string keyRingPath)
        {
            var index = Math.Min(InspectionCount, _snapshots.Length - 1);
            InspectionCount++;
            return _snapshots[index];
        }
    }

    private sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = "SwiftAid.Integration.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static void AddUntrustedDirectoryRule(
        string path,
        FileSystemRights rights,
        InheritanceFlags inheritanceFlags)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }

        var security = new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access);
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier("S-1-5-11"),
            rights,
            inheritanceFlags,
            PropagationFlags.None,
            AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(security);
    }

    private static void AddUntrustedFileRule(string path, FileSystemRights rights)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }

        var security = new FileInfo(path).GetAccessControl(AccessControlSections.Access);
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier("S-1-5-11"),
            rights,
            AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
    }

    private static string[] CaptureWindowsAcl(string path, bool isDirectory)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }

        FileSystemSecurity security = isDirectory
            ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access)
            : new FileInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        var owner = security.GetOwner(typeof(SecurityIdentifier))?.Value ?? string.Empty;
        var rules = new List<string>();
        foreach (AuthorizationRule authorizationRule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (authorizationRule is not FileSystemAccessRule rule)
            {
                throw new InvalidOperationException("The Windows test ACL contained an unexpected rule type.");
            }

            rules.Add(string.Join(
                ":",
                rule.IdentityReference.Value,
                rule.AccessControlType,
                (int)rule.FileSystemRights,
                rule.InheritanceFlags,
                rule.PropagationFlags,
                rule.IsInherited));
        }

        rules.Sort(StringComparer.Ordinal);
        return [owner, .. rules];
    }
}

internal static class PrivateKeyRingTestDirectory
{
    private const UnixFileMode LinuxPrivateDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    public static string CreateRoot(string prefix)
    {
        var userDataPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(userDataPath) || !Path.IsPathFullyQualified(userDataPath))
        {
            throw new InvalidOperationException("A user-private fixture parent is required for the key-ring test.");
        }

        var rootPath = Path.Combine(userDataPath, $"{prefix}-{Guid.NewGuid():N}");
        CreatePrivateDirectory(rootPath);
        return rootPath;
    }

    public static string CreateKeyDirectory(string rootPath)
    {
        var keysPath = Path.Combine(rootPath, "keys");
        CreatePrivateDirectory(keysPath);
        new KeyRingSecurityValidator(new FileSystemKeyRingSecurityInspector()).Validate(keysPath);
        return keysPath;
    }

    public static void DeleteRoot(string rootPath)
    {
        if (Directory.Exists(rootPath))
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    private static void CreatePrivateDirectory(string path)
    {
        if (OperatingSystem.IsLinux())
        {
            Directory.CreateDirectory(path, LinuxPrivateDirectoryMode);
            return;
        }

        Directory.CreateDirectory(path);
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
            if (identity.User is null)
            {
                throw new InvalidOperationException("The Windows key-ring fixture requires a process user SID.");
            }

            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.SetOwner(identity.User);
            foreach (var sid in new[]
                     {
                         identity.User,
                         new SecurityIdentifier(WellKnownSidType.LocalSystemSid, domainSid: null),
                         new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, domainSid: null)
                     })
            {
                security.AddAccessRule(new FileSystemAccessRule(
                    sid,
                    FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None,
                    AccessControlType.Allow));
            }

            new DirectoryInfo(path).SetAccessControl(security);
        }
    }
}
