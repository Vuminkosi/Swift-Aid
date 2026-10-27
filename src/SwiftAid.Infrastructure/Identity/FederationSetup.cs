using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Facebook;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using SwiftAid.Infrastructure.Configuration;

namespace SwiftAid.Infrastructure.Identity;

/// <summary>Registers only explicitly enabled, server-configured federation handlers.</summary>
public static class FederationSetup
{
    public const string GoogleProviderId = "google";
    public const string FacebookProviderId = "facebook";
    public const string GoogleIssuer = "https://accounts.google.com";
    public const string FacebookIssuer = "https://www.facebook.com";
    public const string GoogleScheme = "SwiftAid.Google";
    public const string FacebookScheme = "SwiftAid.Facebook";
    public const string OrganizationSchemePrefix = "SwiftAid.Organization.";

    public static AuthenticationBuilder AddConfiguredProviders(
        AuthenticationBuilder authentication,
        FederationOptions options)
    {
        ArgumentNullException.ThrowIfNull(authentication);
        ArgumentNullException.ThrowIfNull(options);

        if (!options.Enabled)
        {
            return authentication;
        }

        if (options.Google.Enabled && options.Google.CallbackVerified)
        {
            authentication.AddGoogle(GoogleScheme, google =>
            {
                google.ClientId = options.Google.ClientId;
                google.ClientSecret = options.Google.ClientSecret;
                google.SignInScheme = IdentityConstants.ExternalScheme;
                google.CallbackPath = options.Google.CallbackPath;
                google.SaveTokens = false;
                google.Scope.Clear();
                google.Scope.Add("profile");
            });
        }

        if (options.Facebook.Enabled && options.Facebook.CallbackVerified)
        {
            authentication.AddFacebook(FacebookScheme, facebook =>
            {
                facebook.ClientId = options.Facebook.ClientId;
                facebook.ClientSecret = options.Facebook.ClientSecret;
                facebook.SignInScheme = IdentityConstants.ExternalScheme;
                facebook.CallbackPath = "/signin-facebook";
                facebook.SaveTokens = false;
                facebook.Scope.Clear();
                facebook.Scope.Add("public_profile");
                facebook.Fields.Clear();
                facebook.Fields.Add("id");
            });
        }

        foreach (var provider in options.OrganizationProviders.Where(provider => provider.CallbackVerified))
        {
            var expectedIssuer = provider.Issuer;
            var tenantClaimType = provider.TenantClaimType;
            var tenantClaimValue = provider.TenantClaimValue;
            authentication.AddOpenIdConnect(GetOrganizationScheme(provider.ProviderId), oidc =>
            {
                oidc.SignInScheme = IdentityConstants.ExternalScheme;
                oidc.Authority = provider.Authority;
                oidc.ClientId = provider.ClientId;
                oidc.ClientSecret = provider.ClientSecret;
                oidc.CallbackPath = $"/signin-organization-{provider.ProviderId}";
                oidc.ResponseType = OpenIdConnectResponseType.Code;
                oidc.SaveTokens = false;
                oidc.GetClaimsFromUserInfoEndpoint = false;
                oidc.RequireHttpsMetadata = true;
                oidc.MapInboundClaims = false;
                oidc.Scope.Clear();
                oidc.Scope.Add("openid");
                oidc.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = expectedIssuer,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    NameClaimType = "name",
                    RoleClaimType = "role"
                };
                oidc.Events.OnTokenValidated = context =>
                {
                    var tenantClaim = context.Principal?.FindFirst(tenantClaimType)?.Value;
                    if (!string.Equals(tenantClaim, tenantClaimValue, StringComparison.Ordinal))
                    {
                        context.Fail("The organization tenant binding is invalid.");
                    }

                    return Task.CompletedTask;
                };
            });
        }

        return authentication;
    }

    public static string GetOrganizationScheme(string providerId) =>
        OrganizationSchemePrefix + providerId;

    public static string GetScheme(string providerId) => providerId switch
    {
        GoogleProviderId => GoogleScheme,
        FacebookProviderId => FacebookScheme,
        _ => GetOrganizationScheme(providerId)
    };

    public static bool TryGetProviderId(string scheme, out string providerId)
    {
        if (string.Equals(scheme, GoogleScheme, StringComparison.Ordinal))
        {
            providerId = GoogleProviderId;
            return true;
        }

        if (string.Equals(scheme, FacebookScheme, StringComparison.Ordinal))
        {
            providerId = FacebookProviderId;
            return true;
        }

        if (scheme.StartsWith(OrganizationSchemePrefix, StringComparison.Ordinal))
        {
            providerId = scheme[OrganizationSchemePrefix.Length..];
            return providerId.Length > 0;
        }

        providerId = string.Empty;
        return false;
    }
}
