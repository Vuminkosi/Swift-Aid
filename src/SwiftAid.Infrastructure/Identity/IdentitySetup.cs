using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenIddict.Abstractions;
using SwiftAid.Infrastructure.Configuration;
using SwiftAid.Infrastructure.Persistence;

namespace SwiftAid.Infrastructure.Identity;

/// <summary>Registers the server-side SwiftAid authorization-code issuer.</summary>
public static class IdentitySetup
{
    public static void AddOpenIddict(IServiceCollection services, IdentityServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        var certificates = IdentityServerCertificates.Load(options);
        services.AddSingleton(certificates);
        services.AddHostedService<IdentityPublicClientRegistrationService>();
        services.AddOpenIddict()
            .AddCore(core => core
                .UseEntityFrameworkCore()
                .UseDbContext<SwiftAidDbContext>())
            .AddServer(server =>
            {
                server.SetIssuer(new Uri(options.Issuer, UriKind.Absolute));
                server.SetAuthorizationEndpointUris("/connect/authorize");
                server.SetTokenEndpointUris("/connect/token");
                server.SetEndSessionEndpointUris("/connect/logout");
                server.AllowAuthorizationCodeFlow();
                server.AllowRefreshTokenFlow();
                server.RequireProofKeyForCodeExchange();
                server.SetAuthorizationCodeLifetime(TimeSpan.FromSeconds(options.AuthorizationCodeLifetimeSeconds));
                server.SetAccessTokenLifetime(TimeSpan.FromSeconds(options.AccessTokenLifetimeSeconds));
                server.SetRefreshTokenLifetime(TimeSpan.FromSeconds(options.RefreshTokenLifetimeSeconds));
                server.AddSigningCertificate(certificates.SigningCertificate);
                server.AddEncryptionCertificate(certificates.EncryptionCertificate);
                server.UseAspNetCore()
                    .EnableAuthorizationEndpointPassthrough()
                    .EnableTokenEndpointPassthrough()
                    .EnableEndSessionEndpointPassthrough();
            })
            .AddValidation(validation =>
            {
                validation.UseLocalServer();
                validation.EnableTokenEntryValidation();
                validation.UseAspNetCore();
            });
    }
}

/// <summary>Loads only explicitly configured signing and encryption certificates.</summary>
public sealed class IdentityServerCertificates : IDisposable
{
    private IdentityServerCertificates(X509Certificate2 signingCertificate, X509Certificate2 encryptionCertificate)
    {
        SigningCertificate = signingCertificate;
        EncryptionCertificate = encryptionCertificate;
    }

    public X509Certificate2 SigningCertificate { get; }

    public X509Certificate2 EncryptionCertificate { get; }

    public static IdentityServerCertificates Load(IdentityServerOptions options)
    {
        try
        {
            var signing = X509CertificateLoader.LoadPkcs12FromFile(
                options.SigningCertificatePath,
                options.SigningCertificatePassword,
                X509KeyStorageFlags.EphemeralKeySet);
            var encryption = X509CertificateLoader.LoadPkcs12FromFile(
                options.EncryptionCertificatePath,
                options.EncryptionCertificatePassword,
                X509KeyStorageFlags.EphemeralKeySet);

            var now = DateTime.UtcNow;
            if (!signing.HasPrivateKey || !encryption.HasPrivateKey ||
                signing.NotBefore.ToUniversalTime() > now || encryption.NotBefore.ToUniversalTime() > now ||
                signing.NotAfter.ToUniversalTime() <= now || encryption.NotAfter.ToUniversalTime() <= now)
            {
                signing.Dispose();
                encryption.Dispose();
                throw new InvalidOperationException("Identity signing/encryption certificates must have valid private keys and be unexpired.");
            }

            return new IdentityServerCertificates(signing, encryption);
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is CryptographicException or IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException("Identity certificate configuration is invalid.");
        }
    }

    public void Dispose()
    {
        SigningCertificate.Dispose();
        EncryptionCertificate.Dispose();
    }
}

/// <summary>Creates missing, explicitly configured public clients and rejects DB/config drift.</summary>
internal sealed class IdentityPublicClientRegistrationService(
    IServiceScopeFactory scopeFactory,
    IdentityServerOptions options) : IHostedService
{
    private static readonly string[] RequiredPermissions =
    [
        OpenIddictConstants.Permissions.Endpoints.Authorization,
        OpenIddictConstants.Permissions.Endpoints.EndSession,
        OpenIddictConstants.Permissions.Endpoints.Token,
        OpenIddictConstants.Permissions.GrantTypes.AuthorizationCode,
        OpenIddictConstants.Permissions.GrantTypes.RefreshToken,
        OpenIddictConstants.Permissions.ResponseTypes.Code,
        OpenIddictConstants.Permissions.Prefixes.Scope + OpenIddictConstants.Scopes.OfflineAccess
    ];

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var applicationManager = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        foreach (var client in options.PublicClients)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var existing = await applicationManager.FindByClientIdAsync(client.ClientId, cancellationToken)
                .ConfigureAwait(false);
            if (existing is null)
            {
                var descriptor = new OpenIddictApplicationDescriptor
                {
                    ClientId = client.ClientId,
                    ClientType = OpenIddictConstants.ClientTypes.Public,
                    ApplicationType = OpenIddictConstants.ApplicationTypes.Native,
                    ConsentType = OpenIddictConstants.ConsentTypes.Implicit,
                    DisplayName = client.DisplayName
                };
                foreach (var redirect in client.RedirectUris)
                {
                    descriptor.RedirectUris.Add(new Uri(redirect, UriKind.Absolute));
                }

                foreach (var redirect in client.PostLogoutRedirectUris)
                {
                    descriptor.PostLogoutRedirectUris.Add(new Uri(redirect, UriKind.Absolute));
                }

                foreach (var permission in RequiredPermissions)
                {
                    descriptor.Permissions.Add(permission);
                }

                descriptor.Requirements.Add(OpenIddictConstants.Requirements.Features.ProofKeyForCodeExchange);
                await applicationManager.CreateAsync(descriptor, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (!await IsExactConfiguredClientAsync(
                    applicationManager,
                    existing,
                    client,
                    cancellationToken)
                .ConfigureAwait(false))
            {
                throw new InvalidOperationException("An Identity public client registration conflicts with configured redirect or protocol permissions.");
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async ValueTask<bool> IsExactConfiguredClientAsync(
        IOpenIddictApplicationManager applicationManager,
        object application,
        IdentityPublicClientOptions client,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(
                await applicationManager.GetDisplayNameAsync(application, cancellationToken).ConfigureAwait(false),
                client.DisplayName,
                StringComparison.Ordinal) ||
            await applicationManager.GetClientTypeAsync(application, cancellationToken).ConfigureAwait(false) !=
                OpenIddictConstants.ClientTypes.Public ||
            await applicationManager.GetApplicationTypeAsync(application, cancellationToken).ConfigureAwait(false) !=
                OpenIddictConstants.ApplicationTypes.Native ||
            await applicationManager.GetConsentTypeAsync(application, cancellationToken).ConfigureAwait(false) !=
                OpenIddictConstants.ConsentTypes.Implicit)
        {
            return false;
        }

        var redirectUris = await applicationManager.GetRedirectUrisAsync(application, cancellationToken).ConfigureAwait(false);
        var logoutUris = await applicationManager.GetPostLogoutRedirectUrisAsync(application, cancellationToken).ConfigureAwait(false);
        var permissions = await applicationManager.GetPermissionsAsync(application, cancellationToken).ConfigureAwait(false);
        var requirements = await applicationManager.GetRequirementsAsync(application, cancellationToken).ConfigureAwait(false);
        return redirectUris.ToHashSet(StringComparer.Ordinal).SetEquals(client.RedirectUris) &&
            logoutUris.ToHashSet(StringComparer.Ordinal).SetEquals(client.PostLogoutRedirectUris) &&
            permissions.ToHashSet(StringComparer.Ordinal).SetEquals(RequiredPermissions) &&
            requirements.ToHashSet(StringComparer.Ordinal).SetEquals(
                [OpenIddictConstants.Requirements.Features.ProofKeyForCodeExchange]);
    }
}
