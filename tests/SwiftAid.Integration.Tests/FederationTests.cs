using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Facebook;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenIddict.EntityFrameworkCore;
using SwiftAid.Infrastructure.Configuration;
using SwiftAid.Infrastructure.Identity;
using SwiftAid.Infrastructure.Persistence;

namespace SwiftAid.Integration.Tests;

public sealed class FederationTests
{
    [Fact]
    public void IdentityPasskeyModelIsValidForDirectAndConfiguredContextCreation()
    {
        var directOptions = new DbContextOptionsBuilder<SwiftAidDbContext>()
            .UseNpgsql("Host=127.0.0.1;Database=swift_aid_model_only;Username=model_only")
            .UseOpenIddict()
            .Options;

        using (var directContext = new SwiftAidDbContext(directOptions))
        {
            AssertPasskeyModel(directContext);
        }

        var services = new ServiceCollection();
        services.AddDbContext<SwiftAidDbContext>(options =>
            options.UseNpgsql("Host=127.0.0.1;Database=swift_aid_model_only;Username=model_only")
                .UseOpenIddict());
        services.AddIdentityCore<SwiftAidUser>(identity =>
            identity.Stores.SchemaVersion = IdentitySchemaVersions.Version3)
            .AddEntityFrameworkStores<SwiftAidDbContext>();

        using var serviceProvider = services.BuildServiceProvider();
        using var configuredContext = serviceProvider.GetRequiredService<SwiftAidDbContext>();
        AssertPasskeyModel(configuredContext);
    }

    [Fact]
    public async Task UnverifiedProvidersAreNotRegisteredAsChallenges()
    {
        var options = new FederationOptions
        {
            Enabled = true,
            Google = new SocialProviderOptions
            {
                Enabled = true,
                CallbackVerified = false,
                ClientId = "google-client",
                ClientSecret = "test-secret"
            }
        };
        var services = new ServiceCollection();
        var authentication = services.AddAuthentication();
        FederationSetup.AddConfiguredProviders(authentication, options);
        using var provider = services.BuildServiceProvider();

        var schemes = await provider.GetRequiredService<IAuthenticationSchemeProvider>()
            .GetAllSchemesAsync();

        Assert.DoesNotContain(schemes, scheme => scheme.Name == FederationSetup.GoogleScheme);
    }

    [Fact]
    public async Task GoogleHandlerIsAbsentWhenFederationOrProviderIsDisabled()
    {
        var defaultOptions = new FederationOptions();
        var federationDisabled = new FederationOptions
        {
            Enabled = false,
            Google = new SocialProviderOptions
            {
                Enabled = true,
                CallbackVerified = true,
                ClientId = "google-client",
                ClientSecret = "test-google-secret"
            }
        };
        var googleDisabled = new FederationOptions
        {
            Enabled = true,
            Google = new SocialProviderOptions
            {
                Enabled = false,
                CallbackVerified = true,
                ClientId = "google-client",
                ClientSecret = "test-google-secret"
            }
        };

        Assert.DoesNotContain(FederationSetup.GoogleScheme, await GetRegisteredSchemeNames(defaultOptions));
        Assert.DoesNotContain(FederationSetup.GoogleScheme, await GetRegisteredSchemeNames(federationDisabled));
        Assert.DoesNotContain(FederationSetup.GoogleScheme, await GetRegisteredSchemeNames(googleDisabled));
    }

    [Fact]
    public async Task VerifiedProviderSetupRegistersOnlyTheFixedConfiguredSchemes()
    {
        var options = new FederationOptions
        {
            Enabled = true,
            Google = new SocialProviderOptions
            {
                Enabled = true,
                CallbackVerified = true,
                ClientId = "google-client",
                ClientSecret = "test-google-secret"
            },
            OrganizationProviders =
            [
                new OrganizationSsoProviderOptions
                {
                    ProviderId = "clinic-sso",
                    DisplayName = "Clinic SSO",
                    CallbackVerified = true,
                    Authority = "https://login.example.test/tenant-a",
                    Issuer = "https://login.example.test/tenant-a",
                    ClientId = "organization-client",
                    ClientSecret = "test-organization-secret",
                    TenantClaimType = "tenant_id",
                    TenantClaimValue = "clinic-a"
                }
            ]
        };
        var services = new ServiceCollection();
        FederationSetup.AddConfiguredProviders(services.AddAuthentication(), options);
        using var serviceProvider = services.BuildServiceProvider();

        var schemes = await serviceProvider.GetRequiredService<IAuthenticationSchemeProvider>()
            .GetAllSchemesAsync();
        var names = schemes.Select(scheme => scheme.Name).ToHashSet(StringComparer.Ordinal);

        Assert.Contains(FederationSetup.GoogleScheme, names);
        Assert.Contains(FederationSetup.GetOrganizationScheme("clinic-sso"), names);
        Assert.DoesNotContain(FederationSetup.FacebookScheme, names);
        Assert.DoesNotContain(FederationSetup.GetOrganizationScheme("unconfigured"), names);
    }

    [Fact]
    public async Task VerifiedGoogleAndFacebookHandlersUseTheirApprovedCallbackPaths()
    {
        var options = new FederationOptions
        {
            Enabled = true,
            Google = new SocialProviderOptions
            {
                Enabled = true,
                CallbackVerified = true,
                ClientId = "google-client",
                ClientSecret = "test-google-secret",
                CallbackPath = "/google/callback"
            },
            Facebook = new SocialProviderOptions
            {
                Enabled = true,
                CallbackVerified = true,
                ClientId = "facebook-client",
                ClientSecret = "test-facebook-secret",
                CallbackPath = "/attacker-controlled-facebook-path"
            }
        };
        var services = new ServiceCollection();
        FederationSetup.AddConfiguredProviders(services.AddAuthentication(), options);
        using var serviceProvider = services.BuildServiceProvider();

        var schemes = await serviceProvider.GetRequiredService<IAuthenticationSchemeProvider>()
            .GetAllSchemesAsync();
        var names = schemes.Select(scheme => scheme.Name).ToHashSet(StringComparer.Ordinal);
        var google = serviceProvider.GetRequiredService<IOptionsMonitor<GoogleOptions>>()
            .Get(FederationSetup.GoogleScheme);
        var facebook = serviceProvider.GetRequiredService<IOptionsMonitor<FacebookOptions>>()
            .Get(FederationSetup.FacebookScheme);

        Assert.Contains(FederationSetup.GoogleScheme, names);
        Assert.Contains(FederationSetup.FacebookScheme, names);
        Assert.Equal("/google/callback", google.CallbackPath.Value);
        Assert.Equal("/signin-facebook", facebook.CallbackPath.Value);
    }

    [Fact]
    public void GoogleCallbackPathValidationAcceptsConfiguredLocalPathAndIsDisabledWithProvider()
    {
        var validator = new ProviderOptionsValidator();
        var valid = validator.Validate(null, new FederationOptions
        {
            Enabled = true,
            Google = new SocialProviderOptions
            {
                Enabled = true,
                ClientId = "google-client",
                ClientSecret = "test-google-secret",
                CallbackPath = "/google/callback"
            }
        });
        var disabled = validator.Validate(null, new FederationOptions
        {
            Enabled = false,
            Google = new SocialProviderOptions
            {
                Enabled = false,
                CallbackPath = "https://evil.example.test/callback"
            }
        });

        Assert.True(valid.Succeeded);
        Assert.True(disabled.Succeeded);
    }

    [Theory]
    [InlineData("")]
    [InlineData("google/callback")]
    [InlineData("//evil.example/callback")]
    [InlineData("/google//callback")]
    [InlineData("/google/")]
    [InlineData("/.")]
    [InlineData("/..")]
    [InlineData("/google/../callback")]
    [InlineData("/google/%2e%2e/callback")]
    [InlineData("/google\\callback")]
    [InlineData("/google/callback?next=/")]
    [InlineData("/google/callback#fragment")]
    [InlineData("/google:callback")]
    [InlineData("/google/*")]
    [InlineData("/google/café")]
    [InlineData("/google/callback ")]
    public void GoogleCallbackPathValidationRejectsUnsafeConfiguredPaths(string callbackPath)
    {
        var result = new ProviderOptionsValidator().Validate(null, new FederationOptions
        {
            Enabled = true,
            Google = new SocialProviderOptions
            {
                Enabled = true,
                ClientId = "google-client",
                ClientSecret = "test-google-secret",
                CallbackPath = callbackPath
            }
        });

        Assert.True(result.Failed);
    }

    [Fact]
    public async Task ExternalIdentityResolverRejectsUnapprovedIssuerWithoutTouchingStore()
    {
        var options = new FederationOptions
        {
            Enabled = true,
            Google = new SocialProviderOptions
            {
                Enabled = true,
                CallbackVerified = true,
                ClientId = "google-client",
                ClientSecret = "test-secret"
            }
        };
        var store = new RecordingMappingStore();
        var resolver = new ExternalIdentityResolver(
            options,
            new OrganizationSsoRegistry(options),
            store);

        var result = await resolver.ResolveAsync(
            new ValidatedExternalIdentity("google", "https://attacker.example.test", "subject-1"),
            CancellationToken.None);

        Assert.Equal(ExternalIdentityResolutionStatus.Rejected, result.Status);
        Assert.False(store.WasCalled);
    }

    [Fact]
    public async Task DifferentSubjectsArePassedAsDistinctIdentityKeysRegardlessOfEmailClaims()
    {
        var options = new FederationOptions
        {
            Enabled = true,
            Google = new SocialProviderOptions
            {
                Enabled = true,
                CallbackVerified = true,
                ClientId = "google-client",
                ClientSecret = "test-secret"
            }
        };
        var store = new RecordingMappingStore();
        var resolver = new ExternalIdentityResolver(
            options,
            new OrganizationSsoRegistry(options),
            store);

        var first = await resolver.ResolveAsync(
            new ValidatedExternalIdentity("google", FederationSetup.GoogleIssuer, "subject-1"),
            CancellationToken.None);
        var second = await resolver.ResolveAsync(
            new ValidatedExternalIdentity("google", FederationSetup.GoogleIssuer, "subject-2"),
            CancellationToken.None);

        Assert.Equal(ExternalIdentityResolutionStatus.Resolved, first.Status);
        Assert.Equal(ExternalIdentityResolutionStatus.Resolved, second.Status);
        Assert.Equal(new[] { "subject-1", "subject-2" }, store.Subjects);
    }

    [Fact]
    public void OrganizationIssuerMustBeHttpsAndCannotCollideWithSocialProviderIds()
    {
        var validator = new ProviderOptionsValidator();
        var insecure = validator.Validate(null, new FederationOptions
        {
            Enabled = true,
            OrganizationProviders =
            [
                new OrganizationSsoProviderOptions
                {
                    ProviderId = "clinic-sso",
                    Authority = "http://login.example.test",
                    Issuer = "https://login.example.test",
                    ClientId = "client",
                    ClientSecret = "secret",
                    TenantClaimType = "tenant",
                    TenantClaimValue = "clinic"
                }
            ]
        });
        var collision = validator.Validate(null, new FederationOptions
        {
            Enabled = true,
            OrganizationProviders =
            [
                new OrganizationSsoProviderOptions
                {
                    ProviderId = "google",
                    Authority = "https://login.example.test",
                    Issuer = "https://login.example.test",
                    ClientId = "client",
                    ClientSecret = "secret",
                    TenantClaimType = "tenant",
                    TenantClaimValue = "clinic"
                }
            ]
        });

        Assert.True(insecure.Failed);
        Assert.True(collision.Failed);
    }

    private sealed class RecordingMappingStore : IExternalIdentityMappingStore
    {
        public bool WasCalled { get; private set; }

        public bool ExistingLookupWasCalled { get; private set; }

        public List<string> Subjects { get; } = [];

        public ValueTask<ExternalIdentityResolution> ResolveOrCreateOrdinaryActorAsync(
            string provider,
            string issuer,
            string subject,
            CancellationToken cancellationToken)
        {
            WasCalled = true;
            Subjects.Add(subject);
            return ValueTask.FromResult(new ExternalIdentityResolution(
                ExternalIdentityResolutionStatus.Resolved,
                ActorId: $"actor-{subject}"));
        }

        public ValueTask<ExternalIdentityResolution> ResolveExistingAsync(
            string provider,
            string issuer,
            string subject,
            CancellationToken cancellationToken)
        {
            ExistingLookupWasCalled = true;
            return ValueTask.FromResult(new ExternalIdentityResolution(
                ExternalIdentityResolutionStatus.Resolved,
                ActorId: $"actor-{subject}"));
        }

        public ValueTask<ExternalIdentityLinkStatus> LinkAsync(
            string actorId,
            string provider,
            string issuer,
            string subject,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(ExternalIdentityLinkStatus.Linked);

        public ValueTask<ExternalIdentityUnlinkStatus> UnlinkProviderAsync(
            string actorId,
            string provider,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(ExternalIdentityUnlinkStatus.Unlinked);
    }

    private static async Task<HashSet<string>> GetRegisteredSchemeNames(FederationOptions options)
    {
        var services = new ServiceCollection();
        FederationSetup.AddConfiguredProviders(services.AddAuthentication(), options);
        using var serviceProvider = services.BuildServiceProvider();

        var schemes = await serviceProvider.GetRequiredService<IAuthenticationSchemeProvider>()
            .GetAllSchemesAsync();
        return schemes.Select(scheme => scheme.Name).ToHashSet(StringComparer.Ordinal);
    }

    private static void AssertPasskeyModel(SwiftAidDbContext context)
    {
        var passkey = context.Model.FindEntityType(typeof(IdentityUserPasskey<string>));
        Assert.NotNull(passkey);
        var key = passkey.FindPrimaryKey();
        Assert.NotNull(key);
        Assert.Collection(key.Properties, property => Assert.Equal(nameof(IdentityUserPasskey<string>.CredentialId), property.Name));
        var data = passkey.FindNavigation(nameof(IdentityUserPasskey<string>.Data));
        Assert.NotNull(data);
        Assert.True(data.TargetEntityType.IsOwned());
        Assert.Equal("user_passkeys", passkey.GetTableName());
        Assert.Equal("swiftaid_identity", passkey.GetSchema());
    }
}
