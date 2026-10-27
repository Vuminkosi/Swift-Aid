using Duende.IdentityModel.OidcClient.Browser;
using Microsoft.Maui.Authentication;
using SwiftAid.Client.Authentication;

namespace SwiftAid.Mobile;

/// <summary>Runs OIDC authorization through the operating system browser using the registered SwiftAid callback.</summary>
public sealed class MauiSystemBrowser : Duende.IdentityModel.OidcClient.Browser.IBrowser
{
    private readonly NativeLoginOptions _loginOptions;
    private readonly Uri _authority;

    public MauiSystemBrowser(NativeLoginOptions loginOptions)
    {
        _loginOptions = loginOptions ?? throw new ArgumentNullException(nameof(loginOptions));
        _loginOptions.Validate();
        _authority = new Uri(_loginOptions.Authority, UriKind.Absolute);
    }

    public async Task<BrowserResult> InvokeAsync(
        BrowserOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!Uri.TryCreate(options.StartUrl, UriKind.Absolute, out var startUri) ||
            !string.Equals(startUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(startUri.Host, _authority.Host, StringComparison.OrdinalIgnoreCase) ||
            startUri.Port != _authority.Port ||
            !IsRegisteredCallback(options.EndUrl))
        {
            return CreateFailure(BrowserResultType.UnknownError, "The sign-in address could not be opened.");
        }

        using var browserTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (options.Timeout > TimeSpan.Zero && options.Timeout != Timeout.InfiniteTimeSpan)
        {
            browserTimeout.CancelAfter(options.Timeout);
        }

        try
        {
            var result = await WebAuthenticator.Default.AuthenticateAsync(
                startUri,
                new Uri(options.EndUrl, UriKind.Absolute),
                browserTimeout.Token).ConfigureAwait(false);

            var response = BuildCallbackResponse(options.EndUrl, result.Properties);
            return new BrowserResult
            {
                ResultType = BrowserResultType.Success,
                Response = response
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (browserTimeout.IsCancellationRequested)
        {
            return CreateFailure(BrowserResultType.Timeout, "The sign-in request timed out.");
        }
        catch (TaskCanceledException)
        {
            return CreateFailure(BrowserResultType.UserCancel, "Sign-in was cancelled.");
        }
        catch
        {
            return CreateFailure(BrowserResultType.UnknownError, "The sign-in request could not be completed.");
        }
    }

    private bool IsRegisteredCallback(string callback)
    {
        return string.Equals(callback, _loginOptions.RedirectUri, StringComparison.Ordinal) ||
            string.Equals(callback, _loginOptions.PostLogoutRedirectUri, StringComparison.Ordinal);
    }

    private static string BuildCallbackResponse(
        string callback,
        IReadOnlyDictionary<string, string> parameters)
    {
        if (parameters.Count == 0)
        {
            return callback;
        }

        var query = string.Join(
            "&",
            parameters.Select(parameter =>
                $"{Uri.EscapeDataString(parameter.Key)}={Uri.EscapeDataString(parameter.Value)}"));

        return $"{callback}?{query}";
    }

    private static BrowserResult CreateFailure(BrowserResultType resultType, string description) => new()
    {
        ResultType = resultType,
        Error = resultType.ToString(),
        ErrorDescription = description
    };
}
