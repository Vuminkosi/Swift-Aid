using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using SwiftAid.Application.Intake;
using SwiftAid.Contracts.Intake;

namespace SwiftAid.Gateway.Endpoints;

/// <summary>Maps the three anonymous, proof-scoped intake endpoints. No public handler reads a managed case.</summary>
public static class PublicIntakeEndpoints
{
    private const int MaximumJsonBodyBytes = 64 * 1024;
    private const string CapabilityHeader = "X-SwiftAid-Intake-Capability";
    private const string SessionHeader = "X-SwiftAid-Intake-Session";
    private const string ProofHeader = "X-SwiftAid-Receipt-Proof";

    private static readonly JsonSerializerOptions StrictJsonOptions = CreateJsonOptions();

    public static void MapPublicIntakeEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var routes = endpoints.MapGroup("/public/v1/intake");
        routes.MapPost("/sessions", CreateSessionAsync)
            .AllowAnonymous()
            .WithName("CreatePublicIntakeSession")
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(MaximumJsonBodyBytes));
        routes.MapPost("/reports", SubmitAsync)
            .AllowAnonymous()
            .WithName("SubmitPublicIntakeReport")
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(MaximumJsonBodyBytes));
        routes.MapPost("/receipt", GetReceiptAsync)
            .AllowAnonymous()
            .WithName("GetPublicIntakeReceipt")
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(4096));
        routes.MapGet("/metadata", GetMetadata)
            .AllowAnonymous()
            .WithName("GetPublicIntakeMetadata");
        routes.MapGet("/metadata/countries", GetCountriesAsync)
            .AllowAnonymous()
            .WithName("GetPublicIntakeCountries");
    }

    private static IResult GetMetadata(HttpContext context)
    {
        context.Response.Headers.CacheControl = "public, max-age=300";
        var metadata = context.RequestServices.GetService<IIntakePublicMetadataProvider>()?.GetMetadata();
        return metadata is null ? Unavailable() : Results.Ok(metadata);
    }

    private static async Task<IResult> GetCountriesAsync(HttpContext context, CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "public, max-age=300";
        var provider = context.RequestServices.GetService<IIntakePublicMetadataProvider>();
        if (provider is null)
        {
            return Unavailable();
        }

        try
        {
            return Results.Ok(await provider.GetCountriesAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return Unavailable();
        }
    }

    private static async Task<IResult> CreateSessionAsync(HttpContext context, CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (TryGetService<IIntakeSessionEndpointService>(context.RequestServices) is not { } sessions)
        {
            return Unavailable();
        }

        CreateIntakeSessionRequest? request;
        try
        {
            request = await ReadBodyAsync<CreateIntakeSessionRequest>(context, MaximumJsonBodyBytes, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return InvalidRequest();
        }
        catch (IntakePayloadTooLargeException)
        {
            return Problem(StatusCodes.Status413PayloadTooLarge, "INTAKE_PAYLOAD_TOO_LARGE");
        }
        if (request is null || request.SessionId == Guid.Empty ||
            !IntakeReceiptProof.TryDecode(request.ReceiptProof, out var proof))
        {
            return InvalidRequest();
        }

        try
        {
            var session = await sessions.CreateAsync(request.SessionId, proof, cancellationToken)
                .ConfigureAwait(false);
            return session is null ? InvalidRequest() : NoStore(Results.Ok(session));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return Unavailable();
        }
        finally
        {
            IntakeReceiptProof.Clear(proof);
        }
    }

    private static async Task<IResult> SubmitAsync(HttpContext context, CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (TryGetService<IIntakeService>(context.RequestServices) is not { } intake ||
            TryGetService<IIntakeSessionEndpointService>(context.RequestServices) is not { } sessions)
        {
            return Unavailable();
        }

        IntakeSubmission? submission;
        try
        {
            submission = await ReadBodyAsync<IntakeSubmission>(context, MaximumJsonBodyBytes, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return InvalidRequest();
        }
        catch (IntakePayloadTooLargeException)
        {
            return Problem(StatusCodes.Status413PayloadTooLarge, "INTAKE_PAYLOAD_TOO_LARGE");
        }
        if (submission is null ||
            !context.Request.Headers.TryGetValue(CapabilityHeader, out var capabilityValues) ||
            capabilityValues.Count != 1 || string.IsNullOrWhiteSpace(capabilityValues[0]) ||
            capabilityValues[0]!.Length > 4096 ||
            !context.Request.Headers.TryGetValue(SessionHeader, out var sessionValues) || sessionValues.Count != 1 ||
            !Guid.TryParseExact(sessionValues[0], "D", out var sessionId) || sessionId == Guid.Empty ||
            !context.Request.Headers.TryGetValue(ProofHeader, out var proofValues) ||
            proofValues.Count != 1 || !IntakeReceiptProof.TryDecode(proofValues[0], out var proof))
        {
            return InvalidRequest();
        }

        try
        {
            var capability = await sessions.ResolveCapabilityAsync(
                capabilityValues[0]!, sessionId, proof, cancellationToken).ConfigureAwait(false);
            if (capability is null)
            {
                return Problem(StatusCodes.Status401Unauthorized, "INTAKE_PROOF_INVALID");
            }

            var receipt = await intake.SubmitAsync(submission, capability, cancellationToken).ConfigureAwait(false);
            return NoStore(Results.Json(receipt, options: StrictJsonOptions, statusCode: StatusCodes.Status202Accepted));
        }
        catch (IntakeSubmissionException exception)
        {
            return exception.Code switch
            {
                IntakeErrorCode.CapabilityInvalid => Problem(StatusCodes.Status401Unauthorized, "INTAKE_PROOF_INVALID"),
                IntakeErrorCode.RateLimited => Problem(StatusCodes.Status429TooManyRequests, "INTAKE_RATE_LIMITED"),
                IntakeErrorCode.PayloadTooLarge => Problem(StatusCodes.Status413PayloadTooLarge, "INTAKE_PAYLOAD_TOO_LARGE"),
                IntakeErrorCode.AlreadyPromoted => Problem(StatusCodes.Status409Conflict, "INTAKE_ALREADY_PROMOTED"),
                IntakeErrorCode.SubmissionConflict => Problem(StatusCodes.Status409Conflict, "INTAKE_SUBMISSION_CONFLICT"),
                IntakeErrorCode.InvalidReport => InvalidRequest(),
                _ => Unavailable()
            };
        }
        catch (ArgumentException)
        {
            return InvalidRequest();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return Unavailable();
        }
        finally
        {
            IntakeReceiptProof.Clear(proof);
        }
    }

    private static async Task<IResult> GetReceiptAsync(HttpContext context, CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (TryGetService<IIntakeSessionEndpointService>(context.RequestServices) is not { } sessions)
        {
            return Unavailable();
        }

        IntakeReceiptQuery? query;
        try
        {
            query = await ReadBodyAsync<IntakeReceiptQuery>(context, 4096, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return InvalidRequest();
        }
        catch (IntakePayloadTooLargeException)
        {
            return Problem(StatusCodes.Status413PayloadTooLarge, "INTAKE_PAYLOAD_TOO_LARGE");
        }
        if (query is null || query.ReceiptId == Guid.Empty ||
            !context.Request.Headers.TryGetValue(ProofHeader, out var proofValues) || proofValues.Count != 1 ||
            !IntakeReceiptProof.TryDecode(proofValues[0], out var proof))
        {
            return InvalidRequest();
        }

        try
        {
            var receipt = await sessions.GetReceiptAsync(query.ReceiptId, proof, cancellationToken).ConfigureAwait(false);
            return receipt is null
                ? Problem(StatusCodes.Status404NotFound, "INTAKE_PROOF_INVALID")
                : NoStore(Results.Json(receipt, options: StrictJsonOptions));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return Unavailable();
        }
        finally
        {
            IntakeReceiptProof.Clear(proof);
        }
    }

    private static async Task<T?> ReadBodyAsync<T>(HttpContext context, int maximumBytes, CancellationToken ct)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (context.Request.ContentType is null ||
            !context.Request.ContentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
        {
            throw new JsonException("Unsupported intake content type.");
        }

        if (context.Request.ContentLength is long length && length > maximumBytes)
        {
            throw new IntakePayloadTooLargeException();
        }

        await using var buffer = new MemoryStream(Math.Min(maximumBytes, 16 * 1024));
        var chunk = new byte[8192];
        try
        {
            while (true)
            {
                var read = await context.Request.Body.ReadAsync(chunk, ct).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                if (buffer.Length + read > maximumBytes)
                {
                    throw new IntakePayloadTooLargeException();
                }

                await buffer.WriteAsync(chunk.AsMemory(0, read), ct).ConfigureAwait(false);
            }

            buffer.Position = 0;
            return await JsonSerializer.DeserializeAsync<T>(buffer, StrictJsonOptions, ct).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(chunk);
            if (buffer.TryGetBuffer(out var body))
            {
                CryptographicOperations.ZeroMemory(body.AsSpan());
            }
        }
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            MaxDepth = 16,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    private static IResult NoStore(IResult result) => new NoStoreResult(result);

    private static IResult InvalidRequest() => Problem(StatusCodes.Status400BadRequest, "INTAKE_REQUEST_INVALID");

    private static IResult Unavailable() => Problem(StatusCodes.Status503ServiceUnavailable, "INTAKE_UNAVAILABLE");

    private static IResult Problem(int statusCode, string code) =>
        Results.Problem(statusCode: statusCode, title: "The intake request could not be completed.", extensions: new Dictionary<string, object?>
        {
            ["code"] = code
        });

    private static T? TryGetService<T>(IServiceProvider services) where T : class
    {
        try
        {
            return services.GetService<T>();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private sealed class NoStoreResult(IResult inner) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.Headers.CacheControl = "no-store";
            await inner.ExecuteAsync(httpContext).ConfigureAwait(false);
        }
    }

    private sealed class IntakePayloadTooLargeException : Exception
    {
    }
}

internal static class IntakeReceiptProof
{
    public static bool TryDecode(string? encoded, out byte[] proof)
    {
        proof = [];
        if (encoded is not { Length: 43 } || encoded.Contains('=') ||
            encoded.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
        {
            return false;
        }

        try
        {
            var base64 = encoded.Replace('-', '+').Replace('_', '/') + "=";
            var decoded = Convert.FromBase64String(base64);
            var canonical = Convert.ToBase64String(decoded).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            if (decoded.Length != 32 || !string.Equals(canonical, encoded, StringComparison.Ordinal))
            {
                CryptographicOperations.ZeroMemory(decoded);
                return false;
            }

            proof = decoded;
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static void Clear(byte[] proof) => CryptographicOperations.ZeroMemory(proof);
}
