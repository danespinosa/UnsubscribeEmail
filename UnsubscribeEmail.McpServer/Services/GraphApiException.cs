using System.Net;
using System.Text.Json;

namespace UnsubscribeEmail.McpServer.Services;

/// <summary>
/// Describes an error returned by Microsoft Graph without discarding its structured error details.
/// </summary>
public sealed class GraphApiException : HttpRequestException
{
    public GraphApiException(
        HttpStatusCode statusCode,
        string? graphCode,
        string graphMessage,
        string responseBody,
        bool isRetryable = false,
        int retryCount = 0,
        TimeSpan? retryDelay = null,
        string? requestId = null,
        string? clientRequestId = null,
        string? innerErrorCode = null)
        : base(
            BuildMessage(
                statusCode,
                graphCode,
                graphMessage,
                responseBody,
                isRetryable,
                retryCount,
                retryDelay,
                requestId,
                clientRequestId,
                innerErrorCode),
            inner: null,
            statusCode)
    {
        GraphStatusCode = statusCode;
        GraphCode = graphCode;
        GraphMessage = graphMessage;
        ResponseBody = responseBody;
        IsRetryable = isRetryable;
        RetryCount = retryCount;
        RetryDelay = retryDelay ?? TimeSpan.Zero;
        RequestId = requestId;
        ClientRequestId = clientRequestId;
        InnerErrorCode = innerErrorCode;
    }

    public HttpStatusCode GraphStatusCode { get; }
    public string? GraphCode { get; }
    public string GraphMessage { get; }
    public string ResponseBody { get; }
    public bool IsRetryable { get; }
    public int RetryCount { get; }
    public TimeSpan RetryDelay { get; }
    public string? RequestId { get; }
    public string? ClientRequestId { get; }
    public string? InnerErrorCode { get; }

    public static GraphErrorDetails ReadError(string responseBody)
    {
        if (!string.IsNullOrWhiteSpace(responseBody))
        {
            try
            {
                using var document = JsonDocument.Parse(responseBody);
                if (document.RootElement.ValueKind == JsonValueKind.Object &&
                    document.RootElement.TryGetProperty("error", out var error) &&
                    error.ValueKind == JsonValueKind.Object)
                {
                    var code = error.TryGetProperty("code", out var codeValue)
                        ? GetStringValue(codeValue)
                        : null;
                    var message = error.TryGetProperty("message", out var messageValue)
                        ? GetStringValue(messageValue)
                        : null;

                    if (!string.IsNullOrWhiteSpace(code) || !string.IsNullOrWhiteSpace(message))
                    {
                        var innerError = error.TryGetProperty("innerError", out var inner)
                            ? ReadInnerError(inner)
                            : new GraphErrorDetails(null, null, null, null, null);
                        return new GraphErrorDetails(
                            code,
                            string.IsNullOrWhiteSpace(message) ? responseBody : message,
                            innerError.InnerCode,
                            innerError.RequestId,
                            innerError.ClientRequestId);
                    }
                }
            }
            catch (JsonException)
            {
                // Preserve the raw response below when Graph does not return JSON.
            }
        }

        return new GraphErrorDetails(
            null,
            string.IsNullOrWhiteSpace(responseBody)
                ? "The Graph API returned an empty error response."
                : responseBody,
            null,
            null,
            null);
    }

    private static GraphErrorDetails ReadInnerError(JsonElement innerError)
    {
        if (innerError.ValueKind != JsonValueKind.Object)
            return new GraphErrorDetails(null, null, null, null, null);

        var code = innerError.TryGetProperty("code", out var codeValue)
            ? GetStringValue(codeValue)
            : null;
        var requestId = innerError.TryGetProperty("request-id", out var requestIdValue)
            ? GetStringValue(requestIdValue)
            : null;
        var clientRequestId = innerError.TryGetProperty("client-request-id", out var clientRequestIdValue)
            ? GetStringValue(clientRequestIdValue)
            : null;
        return new GraphErrorDetails(null, null, code, requestId, clientRequestId);
    }

    private static string? GetStringValue(JsonElement value)
        => value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string BuildMessage(
        HttpStatusCode statusCode,
        string? graphCode,
        string graphMessage,
        string responseBody,
        bool isRetryable,
        int retryCount,
        TimeSpan? retryDelay,
        string? requestId,
        string? clientRequestId,
        string? innerErrorCode)
    {
        var code = string.IsNullOrWhiteSpace(graphCode) ? "unknown" : graphCode;
        var retryDetails = isRetryable
            ? $" after {retryCount} retries (retry timing: {retryDelay ?? TimeSpan.Zero})"
            : string.Empty;

        var requestDetails = string.IsNullOrWhiteSpace(requestId) &&
                             string.IsNullOrWhiteSpace(clientRequestId)
            ? string.Empty
            : $" Request IDs: request-id={requestId ?? "unknown"}, client-request-id={clientRequestId ?? "unknown"}.";
        var innerDetails = string.IsNullOrWhiteSpace(innerErrorCode)
            ? string.Empty
            : $" Inner error code: {innerErrorCode}.";
        return $"Graph API returned {(int)statusCode} ({code}){retryDetails}: {graphMessage}.{innerDetails}{requestDetails} " +
               $"Response body: {responseBody}";
    }
}

public sealed record GraphErrorDetails(
    string? Code,
    string? Message,
    string? InnerCode,
    string? RequestId,
    string? ClientRequestId)
{
    public GraphErrorDetails(string? code, string? message)
        : this(code, message, null, null, null)
    {
    }
}
