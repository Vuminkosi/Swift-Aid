using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.EntityFrameworkCore;
using SwiftAid.Infrastructure.Configuration;
using SwiftAid.Infrastructure.Identity;
using SwiftAid.Infrastructure.Persistence;

namespace SwiftAid.Integration.Tests;

public sealed class ExternalIdentityMappingPolicyTests
{
    [Fact]
    public async Task ResolverRejectsUnapprovedIssuerBeforeCallingMappingStore()
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
            new ValidatedExternalIdentity(
                FederationSetup.GoogleProviderId,
                "https://unapproved.example.test",
                "subject-1"),
            CancellationToken.None);

        Assert.Equal(ExternalIdentityResolutionStatus.Rejected, result.Status);
        Assert.Empty(store.ResolutionCalls);
    }

    [IdentityPostgresFact]
    public async Task OrdinaryActorCreationUsesExactIdentityKeyAndCreatesNoEmailOrAuthorityGrants()
    {
        await using var fixture = await ExternalIdentityPostgresFixture.CreateAsync();
        var services = new ServiceCollection();
        services.AddLogging();
        var lookupBarrier = new MappingLookupBarrierInterceptor();
        services.AddSingleton(lookupBarrier);
        services.AddDbContext<SwiftAidDbContext>(database =>
            database.UseNpgsql(fixture.ConnectionString).UseOpenIddict().AddInterceptors(lookupBarrier));
        services.AddIdentityCore<SwiftAidUser>(identity =>
                identity.Stores.SchemaVersion = IdentitySchemaVersions.Version3)
            .AddEntityFrameworkStores<SwiftAidDbContext>();
        var federationOptions = new FederationOptions
        {
            Enabled = true,
            Google = new SocialProviderOptions { Enabled = true, CallbackVerified = true }
        };
        var organizationSsoRegistry = new OrganizationSsoRegistry(federationOptions);

        await using var serviceProvider = services.BuildServiceProvider();
        await using var scope = serviceProvider.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<SwiftAidDbContext>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<SwiftAidUser>>();
        var issuer = FederationSetup.GoogleIssuer;
        var runId = Guid.NewGuid().ToString("N");
        var subjectA = $"{runId}-subject-a";
        var subjectB = $"{runId}-subject-b";
        var concurrentSubject = $"{runId}-concurrent-subject";
        var createdActorIds = new ConcurrentBag<string>();

        async Task<ExternalIdentityResolution> ResolveAsync(
            string provider,
            string identityIssuer,
            string subject)
        {
            await using var operationScope = serviceProvider.CreateAsyncScope();
            var operationDatabase = operationScope.ServiceProvider.GetRequiredService<SwiftAidDbContext>();
            var operationUserManager = operationScope.ServiceProvider.GetRequiredService<UserManager<SwiftAidUser>>();
            var operationStore = new EfExternalIdentityMappingStore(
                operationDatabase,
                operationUserManager,
                TimeProvider.System,
                organizationSsoRegistry);
            var resolution = await operationStore.ResolveOrCreateOrdinaryActorAsync(
                provider,
                identityIssuer,
                subject,
                CancellationToken.None);
            if (resolution.Created && resolution.ActorId is not null)
            {
                createdActorIds.Add(resolution.ActorId);
            }

            return resolution;
        }

        try
        {
            // The two identities model provider accounts with the same profile email.
            // Email is deliberately absent from the resolver/store key, so only their
            // distinct validated subjects can select their persisted actors.
            var first = await ResolveAsync(FederationSetup.GoogleProviderId, issuer, subjectA);
            var second = await ResolveAsync(FederationSetup.GoogleProviderId, issuer, subjectB);

            lookupBarrier.ArmForTwoConcurrentTransactions();
            var concurrentResults = await Task.WhenAll(
                ResolveAsync(FederationSetup.GoogleProviderId, issuer, concurrentSubject),
                ResolveAsync(FederationSetup.GoogleProviderId, issuer, concurrentSubject));

            Assert.Equal(ExternalIdentityResolutionStatus.Resolved, first.Status);
            Assert.True(first.Created);
            Assert.Equal(ExternalIdentityResolutionStatus.Resolved, second.Status);
            Assert.True(second.Created);
            Assert.All(concurrentResults, result =>
                Assert.Equal(ExternalIdentityResolutionStatus.Resolved, result.Status));
            Assert.Single(concurrentResults, result => result.Created);
            Assert.Single(concurrentResults, result => !result.Created);
            Assert.NotEqual(first.ActorId, second.ActorId);
            Assert.Equal(concurrentResults[0].ActorId, concurrentResults[1].ActorId);

            var actorIds = new[]
            {
                first.ActorId!,
                second.ActorId!,
                concurrentResults[0].ActorId!
            };
            var users = await database.Users
                .AsNoTracking()
                .Where(user => actorIds.Contains(user.Id))
                .ToListAsync();
            Assert.Equal(3, users.Count);
            Assert.All(users, user =>
            {
                Assert.Null(user.Email);
                Assert.Null(user.NormalizedEmail);
                Assert.False(user.EmailConfirmed);
            });

            Assert.Equal(3, await database.ExternalIdentityMappings
                .CountAsync(mapping => actorIds.Contains(mapping.ActorId)));
            Assert.Equal(1, await database.ExternalIdentityMappings.CountAsync(mapping =>
                mapping.Provider == FederationSetup.GoogleProviderId &&
                mapping.Issuer == issuer &&
                mapping.Subject == concurrentSubject));
            Assert.Equal(3, await database.Actors.CountAsync(actor => actorIds.Contains(actor.ActorId)));
            Assert.Equal(3, await database.ActorAccessStates
                .CountAsync(state => actorIds.Contains(state.ActorId) && state.AccessEpoch == 0));
            Assert.Empty(await database.OrganizationMemberships
                .Where(membership => actorIds.Contains(membership.ActorId))
                .ToListAsync());
            Assert.Empty(await database.AccessGrantGroups
                .Where(grant => actorIds.Contains(grant.ActorId))
                .ToListAsync());
        }
        finally
        {
            if (createdActorIds.Count > 0)
            {
                var actorIds = createdActorIds.Distinct(StringComparer.Ordinal).ToArray();
                database.ExternalIdentityMappings.RemoveRange(await database.ExternalIdentityMappings
                    .Where(mapping => actorIds.Contains(mapping.ActorId))
                    .ToListAsync());
                database.OrganizationMemberships.RemoveRange(await database.OrganizationMemberships
                    .Where(membership => actorIds.Contains(membership.ActorId))
                    .ToListAsync());
                database.AccessGrantGroups.RemoveRange(await database.AccessGrantGroups
                    .Where(grant => actorIds.Contains(grant.ActorId))
                    .ToListAsync());
                database.ActorAccessStates.RemoveRange(await database.ActorAccessStates
                    .Where(state => actorIds.Contains(state.ActorId))
                    .ToListAsync());

                foreach (var actorId in actorIds)
                {
                    var user = await userManager.FindByIdAsync(actorId);
                    if (user is not null)
                    {
                        var deleted = await userManager.DeleteAsync(user);
                        Assert.True(deleted.Succeeded, "The disposable identity fixture must remove its test user.");
                    }
                }

                database.Actors.RemoveRange(await database.Actors
                    .Where(actor => actorIds.Contains(actor.ActorId))
                    .ToListAsync());
                await database.SaveChangesAsync();
            }
        }
    }

    [IdentityPostgresFact]
    public async Task SameSubjectWithDifferentProviderOrConfiguredIssuerCreatesDistinctActors()
    {
        await using var fixture = await ExternalIdentityPostgresFixture.CreateAsync();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<SwiftAidDbContext>(database =>
            database.UseNpgsql(fixture.ConnectionString).UseOpenIddict());
        services.AddIdentityCore<SwiftAidUser>(identity =>
                identity.Stores.SchemaVersion = IdentitySchemaVersions.Version3)
            .AddEntityFrameworkStores<SwiftAidDbContext>();

        var subject = Guid.NewGuid().ToString("N");
        var sharedIssuerConfiguration = CreateOrganizationFederationOptions(
            FederationSetup.GoogleIssuer,
            includeVerifiedGoogleProvider: true);
        var previousIssuer = "https://clinic.example.test/old-issuer";
        var currentIssuer = "https://clinic.example.test/current-issuer";
        var previousConfiguration = CreateOrganizationFederationOptions(
            previousIssuer,
            includeVerifiedGoogleProvider: true);
        var currentConfiguration = CreateOrganizationFederationOptions(
            currentIssuer,
            includeVerifiedGoogleProvider: true);

        await using var serviceProvider = services.BuildServiceProvider();
        await using var scope = serviceProvider.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<SwiftAidDbContext>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<SwiftAidUser>>();
        var store = new EfExternalIdentityMappingStore(
            database,
            userManager,
            TimeProvider.System,
            new OrganizationSsoRegistry(currentConfiguration));
        var createdActorIds = new ConcurrentBag<string>();

        async Task<ExternalIdentityResolution> ResolveAsync(
            FederationOptions options,
            string provider,
            string issuer)
        {
            var resolver = new ExternalIdentityResolver(options, new OrganizationSsoRegistry(options), store);
            var result = await resolver.ResolveAsync(
                new ValidatedExternalIdentity(provider, issuer, subject),
                CancellationToken.None);
            if (result.Created && result.ActorId is not null)
            {
                createdActorIds.Add(result.ActorId);
            }

            return result;
        }

        try
        {
            var google = await ResolveAsync(
                sharedIssuerConfiguration,
                FederationSetup.GoogleProviderId,
                FederationSetup.GoogleIssuer);
            var organizationWithSameIssuer = await ResolveAsync(
                sharedIssuerConfiguration,
                "clinic-sso",
                FederationSetup.GoogleIssuer);
            var previousOrganizationIssuer = await ResolveAsync(
                previousConfiguration,
                "clinic-sso",
                previousIssuer);
            var currentOrganizationIssuer = await ResolveAsync(
                currentConfiguration,
                "clinic-sso",
                currentIssuer);

            var results = new[]
            {
                google,
                organizationWithSameIssuer,
                previousOrganizationIssuer,
                currentOrganizationIssuer
            };
            Assert.All(results, result => Assert.Equal(ExternalIdentityResolutionStatus.Resolved, result.Status));
            Assert.All(results, result => Assert.True(result.Created));

            var actorIds = new[]
            {
                google.ActorId!,
                organizationWithSameIssuer.ActorId!,
                previousOrganizationIssuer.ActorId!,
                currentOrganizationIssuer.ActorId!
            };
            Assert.Equal(actorIds.Length, actorIds.Distinct(StringComparer.Ordinal).Count());
            Assert.Equal(actorIds.Length, await database.ExternalIdentityMappings
                .CountAsync(mapping => actorIds.Contains(mapping.ActorId)));
            Assert.Equal(actorIds.Length, await database.Users
                .CountAsync(user => actorIds.Contains(user.Id) && user.Email == null));
        }
        finally
        {
            var actorIds = createdActorIds.Distinct(StringComparer.Ordinal).ToArray();
            if (actorIds.Length > 0)
            {
                database.ExternalIdentityMappings.RemoveRange(await database.ExternalIdentityMappings
                    .Where(mapping => actorIds.Contains(mapping.ActorId))
                    .ToListAsync());
                database.OrganizationMemberships.RemoveRange(await database.OrganizationMemberships
                    .Where(membership => actorIds.Contains(membership.ActorId))
                    .ToListAsync());
                database.AccessGrantGroups.RemoveRange(await database.AccessGrantGroups
                    .Where(grant => actorIds.Contains(grant.ActorId))
                    .ToListAsync());
                database.ActorAccessStates.RemoveRange(await database.ActorAccessStates
                    .Where(state => actorIds.Contains(state.ActorId))
                    .ToListAsync());

                foreach (var actorId in actorIds)
                {
                    var user = await userManager.FindByIdAsync(actorId);
                    if (user is not null)
                    {
                        var deleted = await userManager.DeleteAsync(user);
                        Assert.True(deleted.Succeeded, "The disposable identity fixture must remove its test user.");
                    }
                }

                database.Actors.RemoveRange(await database.Actors
                    .Where(actor => actorIds.Contains(actor.ActorId))
                    .ToListAsync());
                await database.SaveChangesAsync();
            }
        }
    }

    [IdentityPostgresTheory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public async Task UnlinkRequiresAnEnabledVerifiedAlternateLoginRoute(
        bool alternateProviderEnabled,
        bool alternateProviderCallbackVerified,
        bool expectedUnlinked)
    {
        await using var fixture = await ExternalIdentityPostgresFixture.CreateAsync();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<SwiftAidDbContext>(database =>
            database.UseNpgsql(fixture.ConnectionString).UseOpenIddict());
        services.AddIdentityCore<SwiftAidUser>(identity =>
                identity.Stores.SchemaVersion = IdentitySchemaVersions.Version3)
            .AddEntityFrameworkStores<SwiftAidDbContext>();
        var federationOptions = new FederationOptions
        {
            Enabled = true,
            Google = new SocialProviderOptions { Enabled = true, CallbackVerified = true },
            Facebook = new SocialProviderOptions
            {
                Enabled = alternateProviderEnabled,
                CallbackVerified = alternateProviderCallbackVerified
            }
        };
        var registry = new OrganizationSsoRegistry(federationOptions);

        await using var serviceProvider = services.BuildServiceProvider();
        await using var scope = serviceProvider.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<SwiftAidDbContext>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<SwiftAidUser>>();
        var store = new EfExternalIdentityMappingStore(database, userManager, TimeProvider.System, registry);
        var createdActorIds = new ConcurrentBag<string>();
        var runId = Guid.NewGuid().ToString("N");

        try
        {
            var creation = await store.ResolveOrCreateOrdinaryActorAsync(
                FederationSetup.GoogleProviderId,
                FederationSetup.GoogleIssuer,
                $"{runId}-google-subject",
                CancellationToken.None);
            if (creation.Created && creation.ActorId is not null)
            {
                createdActorIds.Add(creation.ActorId);
            }

            Assert.Equal(ExternalIdentityResolutionStatus.Resolved, creation.Status);
            Assert.True(creation.Created);

            database.ExternalIdentityMappings.Add(new ExternalIdentityMappingEntity
            {
                MappingId = Guid.NewGuid(),
                ActorId = creation.ActorId!,
                Provider = FederationSetup.FacebookProviderId,
                Issuer = FederationSetup.FacebookIssuer,
                Subject = $"{runId}-facebook-subject",
                CreatedAtUtc = TimeProvider.System.GetUtcNow()
            });
            await database.SaveChangesAsync();

            var unlink = await store.UnlinkProviderAsync(
                creation.ActorId!,
                FederationSetup.GoogleProviderId,
                CancellationToken.None);

            Assert.Equal(
                expectedUnlinked ? ExternalIdentityUnlinkStatus.Unlinked : ExternalIdentityUnlinkStatus.LastLoginRoute,
                unlink);
            Assert.Equal(
                expectedUnlinked ? 0 : 1,
                await database.ExternalIdentityMappings.CountAsync(mapping =>
                    mapping.ActorId == creation.ActorId && mapping.Provider == FederationSetup.GoogleProviderId));
        }
        finally
        {
            var actorIds = createdActorIds.Distinct(StringComparer.Ordinal).ToArray();
            if (actorIds.Length > 0)
            {
                database.ExternalIdentityMappings.RemoveRange(await database.ExternalIdentityMappings
                    .Where(mapping => actorIds.Contains(mapping.ActorId))
                    .ToListAsync());
                database.OrganizationMemberships.RemoveRange(await database.OrganizationMemberships
                    .Where(membership => actorIds.Contains(membership.ActorId))
                    .ToListAsync());
                database.AccessGrantGroups.RemoveRange(await database.AccessGrantGroups
                    .Where(grant => actorIds.Contains(grant.ActorId))
                    .ToListAsync());
                database.ActorAccessStates.RemoveRange(await database.ActorAccessStates
                    .Where(state => actorIds.Contains(state.ActorId))
                    .ToListAsync());

                foreach (var actorId in actorIds)
                {
                    var user = await userManager.FindByIdAsync(actorId);
                    if (user is not null)
                    {
                        var deleted = await userManager.DeleteAsync(user);
                        Assert.True(deleted.Succeeded, "The disposable identity fixture must remove its test user.");
                    }
                }

                database.Actors.RemoveRange(await database.Actors
                    .Where(actor => actorIds.Contains(actor.ActorId))
                    .ToListAsync());
                await database.SaveChangesAsync();
            }
        }
    }

    private sealed class RecordingMappingStore : IExternalIdentityMappingStore
    {
        public List<(string Provider, string Issuer, string Subject)> ResolutionCalls { get; } = [];

        public ValueTask<ExternalIdentityResolution> ResolveOrCreateOrdinaryActorAsync(
            string provider,
            string issuer,
            string subject,
            CancellationToken cancellationToken)
        {
            ResolutionCalls.Add((provider, issuer, subject));
            return ValueTask.FromResult(new ExternalIdentityResolution(
                ExternalIdentityResolutionStatus.Resolved,
                ActorId: $"actor-{subject}"));
        }

        public ValueTask<ExternalIdentityResolution> ResolveExistingAsync(
            string provider,
            string issuer,
            string subject,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new ExternalIdentityResolution(ExternalIdentityResolutionStatus.Rejected));

        public ValueTask<ExternalIdentityLinkStatus> LinkAsync(
            string actorId,
            string provider,
            string issuer,
            string subject,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(ExternalIdentityLinkStatus.Rejected);

        public ValueTask<ExternalIdentityUnlinkStatus> UnlinkProviderAsync(
            string actorId,
            string provider,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(ExternalIdentityUnlinkStatus.Rejected);
    }

    private static FederationOptions CreateOrganizationFederationOptions(
        string issuer,
        bool includeVerifiedGoogleProvider) => new()
    {
        Enabled = true,
        Google = new SocialProviderOptions
        {
            Enabled = includeVerifiedGoogleProvider,
            CallbackVerified = includeVerifiedGoogleProvider,
            ClientId = "google-test-client",
            ClientSecret = "google-test-secret"
        },
        OrganizationProviders =
        [
            new OrganizationSsoProviderOptions
            {
                ProviderId = "clinic-sso",
                DisplayName = "Clinic SSO",
                CallbackVerified = true,
                Authority = issuer,
                Issuer = issuer,
                ClientId = "clinic-test-client",
                ClientSecret = "clinic-test-secret",
                TenantClaimType = "tenant_id",
                TenantClaimValue = "clinic-test"
            }
        ]
    };

    private sealed class MappingLookupBarrierInterceptor : DbCommandInterceptor
    {
        private readonly object _sync = new();
        private TaskCompletionSource _rendezvous = CreateRendezvous();
        private int _arrivals;
        private int _completedRounds;
        private bool _armed;

        public void ArmForTwoConcurrentTransactions()
        {
            lock (_sync)
            {
                _armed = true;
                _arrivals = 0;
                _completedRounds = 0;
                _rendezvous = CreateRendezvous();
            }
        }

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (!command.CommandText.Contains("external_identities", StringComparison.OrdinalIgnoreCase))
            {
                return result;
            }

            TaskCompletionSource rendezvous;
            lock (_sync)
            {
                if (!_armed || _completedRounds >= 2)
                {
                    return result;
                }

                rendezvous = _rendezvous;
                _arrivals++;
                if (_arrivals == 2)
                {
                    _completedRounds++;
                    _arrivals = 0;
                    _rendezvous = CreateRendezvous();
                    rendezvous.TrySetResult();
                }
            }

            await rendezvous.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            return result;
        }

        private static TaskCompletionSource CreateRendezvous() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
