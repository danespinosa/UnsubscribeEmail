using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Moq;
using UnsubscribeEmail.McpServer.Services;
using UnsubscribeEmail.McpServer.Tools;

namespace UnsubscribeEmail.Tests;

public class AttachmentToolsTests
{
    [Fact]
    public async Task ListAttachments_ReturnsMetadataTypesAndEncodedMessageIdWithoutContentBytes()
    {
        var handler = new AttachmentGraphHandler();
        handler.EnqueueListResponse("""
        {
          "value": [
            {
              "@odata.type": "#microsoft.graph.fileAttachment",
              "id": "file/1=",
              "name": "inline.png",
              "contentType": "image/png",
              "size": 3,
              "isInline": true,
              "contentId": "cid-inline",
              "lastModifiedDateTime": "2026-09-24T12:00:00Z",
              "contentBytes": "AQID"
            },
            {
              "@odata.type": "#microsoft.graph.itemAttachment",
              "id": "item-1",
              "name": "forwarded.msg",
              "contentType": "message/rfc822",
              "size": 10,
              "isInline": false
            },
            {
              "@odata.type": "#microsoft.graph.referenceAttachment",
              "id": "reference-1",
              "name": "Shared file",
              "contentType": "application/octet-stream",
              "size": 0,
              "isInline": false,
              "sourceUrl": "https://contoso.example/shared/file"
            }
          ]
        }
        """);
        var (authService, graphService) = CreateServices(handler);

        var result = await ListEmailAttachmentsTool.ListEmailAttachments(
            authService.Object,
            graphService,
            "message/with=characters",
            maxAttachments: 10);

        var json = JsonSerializer.Deserialize<JsonElement>(result);
        Assert.Equal("success", json.GetProperty("status").GetString());
        Assert.Equal("message/with=characters", json.GetProperty("messageId").GetString());
        Assert.Equal(3, json.GetProperty("totalAttachments").GetInt32());
        Assert.False(json.GetProperty("hasMore").GetBoolean());

        var attachments = json.GetProperty("attachments");
        Assert.Equal(3, attachments.GetArrayLength());
        Assert.Equal("file", attachments[0].GetProperty("attachmentType").GetString());
        Assert.True(attachments[0].GetProperty("isInline").GetBoolean());
        Assert.Equal("cid-inline", attachments[0].GetProperty("contentId").GetString());
        Assert.Equal("2026-09-24T12:00:00Z", attachments[0].GetProperty("lastModifiedDateTime").GetString());
        Assert.True(attachments[0].GetProperty("downloadSupported").GetBoolean());
        Assert.Equal("item", attachments[1].GetProperty("attachmentType").GetString());
        Assert.True(attachments[1].GetProperty("downloadSupported").GetBoolean());
        Assert.Equal("reference", attachments[2].GetProperty("attachmentType").GetString());
        Assert.False(attachments[2].GetProperty("downloadSupported").GetBoolean());
        Assert.DoesNotContain("contentBytes", result, StringComparison.OrdinalIgnoreCase);

        Assert.Contains(
            "/me/messages/message%2Fwith%3Dcharacters/attachments",
            handler.Requests[0],
            StringComparison.Ordinal);
        Assert.DoesNotContain("contentBytes", handler.Requests[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ListAttachments_FollowsPaginationUntilCapAndReportsHasMore()
    {
        var handler = new AttachmentGraphHandler();
        handler.EnqueueListResponse("""
        {
          "value": [
            { "@odata.type": "#microsoft.graph.fileAttachment", "id": "a1", "name": "a1.txt", "size": 1 },
            { "@odata.type": "#microsoft.graph.fileAttachment", "id": "a2", "name": "a2.txt", "size": 1 }
          ],
          "@odata.nextLink": "https://graph.microsoft.com/v1.0/me/messages/msg/attachments?page=2"
        }
        """);
        handler.EnqueueListResponse("""
        {
          "value": [
            { "@odata.type": "#microsoft.graph.fileAttachment", "id": "a3", "name": "a3.txt", "size": 1 }
          ]
        }
        """);
        var (authService, graphService) = CreateServices(handler);

        var capped = await graphService.GetEmailAttachmentsAsync("msg", maxAttachments: 2);
        Assert.Equal(2, capped.TotalAttachments);
        Assert.True(capped.HasMore);
        Assert.Single(handler.Requests);

        var completeHandler = new AttachmentGraphHandler();
        completeHandler.EnqueueListResponse("""
        {
          "value": [
            { "@odata.type": "#microsoft.graph.fileAttachment", "id": "a1", "name": "a1.txt", "size": 1 },
            { "@odata.type": "#microsoft.graph.fileAttachment", "id": "a2", "name": "a2.txt", "size": 1 }
          ],
          "@odata.nextLink": "https://graph.microsoft.com/v1.0/me/messages/msg/attachments?page=2"
        }
        """);
        completeHandler.EnqueueListResponse("""
        {
          "value": [
            { "@odata.type": "#microsoft.graph.fileAttachment", "id": "a3", "name": "a3.txt", "size": 1 }
          ]
        }
        """);
        var (_, completeService) = CreateServices(completeHandler);
        var complete = await completeService.GetEmailAttachmentsAsync("msg", maxAttachments: 10);
        Assert.Equal(3, complete.TotalAttachments);
        Assert.False(complete.HasMore);
        Assert.Equal(2, completeHandler.Requests.Count);
    }

    [Fact]
    public async Task ListTool_ClampsAttachmentLimitToAtLeastOne()
    {
        var handler = new AttachmentGraphHandler();
        handler.EnqueueListResponse("""
        {
          "value": [
            { "@odata.type": "#microsoft.graph.fileAttachment", "id": "a1", "name": "a1.txt", "size": 1 },
            { "@odata.type": "#microsoft.graph.fileAttachment", "id": "a2", "name": "a2.txt", "size": 1 }
          ]
        }
        """);
        var (authService, graphService) = CreateServices(handler);

        var result = await ListEmailAttachmentsTool.ListEmailAttachments(
            authService.Object,
            graphService,
            "msg",
            maxAttachments: 0);

        var json = JsonSerializer.Deserialize<JsonElement>(result);
        Assert.Equal(1, json.GetProperty("totalAttachments").GetInt32());
        Assert.True(json.GetProperty("hasMore").GetBoolean());
    }

    [Fact]
    public async Task DownloadFileAttachment_UsesMetadataFirstAndReturnsBase64WithinLimit()
    {
        var handler = new AttachmentGraphHandler();
        handler.EnqueueMetadataResponse("""
        {
          "@odata.type": "#microsoft.graph.fileAttachment",
          "id": "attachment/1=",
          "name": "report.pdf",
          "contentType": "application/pdf",
          "size": 3,
          "isInline": false,
          "lastModifiedDateTime": "2026-09-24T12:00:00Z"
        }
        """);
        handler.EnqueueRawResponse(new byte[] { 1, 2, 3 }, "application/pdf");
        var (authService, graphService) = CreateServices(handler);

        var result = await graphService.DownloadEmailAttachmentAsync(
            "message/1=",
            "attachment/1=",
            maxBytes: 10);

        Assert.Equal("attachment/1=", result.Attachment.AttachmentId);
        Assert.Equal("file", result.Attachment.AttachmentType);
        Assert.Equal("AQID", result.Base64Content);
        Assert.Equal(3, result.DownloadedSize);
        Assert.Equal("application/pdf", result.ContentType);
        Assert.Contains("message%2F1%3D", handler.Requests[0], StringComparison.Ordinal);
        Assert.Contains("attachment%2F1%3D", handler.Requests[0], StringComparison.Ordinal);
        Assert.EndsWith("/$value", handler.Requests[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task DownloadTool_ReturnsBoundedBase64AndMetadataEnvelope()
    {
        var handler = new AttachmentGraphHandler();
        handler.EnqueueMetadataResponse("""
        {
          "@odata.type": "#microsoft.graph.fileAttachment",
          "id": "attachment-1",
          "name": "report.txt",
          "contentType": "text/plain",
          "size": 4,
          "isInline": false
        }
        """);
        handler.EnqueueRawResponse(Encoding.UTF8.GetBytes("data"), "text/plain");
        var (authService, graphService) = CreateServices(handler);

        var result = await DownloadEmailAttachmentTool.DownloadEmailAttachment(
            authService.Object,
            graphService,
            "message-1",
            "attachment-1",
            maxBytes: 10);

        var json = JsonSerializer.Deserialize<JsonElement>(result);
        Assert.Equal("success", json.GetProperty("status").GetString());
        Assert.Equal("message-1", json.GetProperty("messageId").GetString());
        Assert.Equal("attachment-1", json.GetProperty("attachmentId").GetString());
        Assert.Equal("text/plain", json.GetProperty("contentType").GetString());
        Assert.Equal("ZGF0YQ==", json.GetProperty("base64Content").GetString());
        Assert.False(json.TryGetProperty("filePath", out _));
    }

    [Fact]
    public async Task DownloadItemAttachment_ReturnsGraphMimeContent()
    {
        var handler = new AttachmentGraphHandler();
        handler.EnqueueMetadataResponse("""
        {
          "@odata.type": "#microsoft.graph.itemAttachment",
          "id": "item-1",
          "name": "message.eml",
          "contentType": "message/rfc822",
          "size": 4,
          "isInline": false
        }
        """);
        handler.EnqueueRawResponse(Encoding.UTF8.GetBytes("MIME"), "message/rfc822");
        var (authService, graphService) = CreateServices(handler);

        var result = await graphService.DownloadEmailAttachmentAsync("message-1", "item-1", 10);

        Assert.Equal("item", result.Attachment.AttachmentType);
        Assert.Equal("message/rfc822", result.ContentType);
        Assert.Equal("TUlNRQ==", result.Base64Content);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task DownloadReferenceAttachment_RejectsBeforeValueRequest()
    {
        var handler = new AttachmentGraphHandler();
        handler.EnqueueMetadataResponse("""
        {
          "@odata.type": "#microsoft.graph.referenceAttachment",
          "id": "reference-1",
          "name": "Shared file",
          "contentType": "application/octet-stream",
          "size": 0,
          "sourceUrl": "https://contoso.example/shared/file"
        }
        """);
        var (authService, graphService) = CreateServices(handler);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            graphService.DownloadEmailAttachmentAsync("message-1", "reference-1", 10));

        Assert.Contains("reference attachment", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(handler.Requests);
        Assert.DoesNotContain("$value", handler.Requests[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task DownloadAttachment_RejectsKnownOversizeBeforeValueRequest()
    {
        var handler = new AttachmentGraphHandler();
        handler.EnqueueMetadataResponse("""
        {
          "@odata.type": "#microsoft.graph.fileAttachment",
          "id": "large-1",
          "name": "large.bin",
          "contentType": "application/octet-stream",
          "size": 11
        }
        """);
        var (authService, graphService) = CreateServices(handler);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            graphService.DownloadEmailAttachmentAsync("message-1", "large-1", 10));

        Assert.Contains("maxBytes", exception.Message, StringComparison.Ordinal);
        Assert.Single(handler.Requests);
        Assert.DoesNotContain("$value", handler.Requests[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task DownloadAttachment_EnforcesRuntimeBoundWhenGraphSizeIsMissing()
    {
        var handler = new AttachmentGraphHandler();
        handler.EnqueueMetadataResponse("""
        {
          "@odata.type": "#microsoft.graph.fileAttachment",
          "id": "unknown-size",
          "name": "unknown.bin",
          "contentType": "application/octet-stream"
        }
        """);
        handler.EnqueueRawResponse(new byte[11], "application/octet-stream", contentLength: null);
        var (authService, graphService) = CreateServices(handler);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            graphService.DownloadEmailAttachmentAsync("message-1", "unknown-size", 10));

        Assert.Contains("maxBytes", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tools_ReturnStandardErrorsWhenUnauthenticatedOrGraphFails()
    {
        var unauthenticated = new Mock<AuthService>(Mock.Of<ILogger<AuthService>>());
        unauthenticated.SetupGet(a => a.IsAuthenticated).Returns(false);
        var unauthenticatedService = new GraphEmailService(
            unauthenticated.Object,
            Mock.Of<ILogger<GraphEmailService>>());

        var unauthenticatedResult = await DownloadEmailAttachmentTool.DownloadEmailAttachment(
            unauthenticated.Object,
            unauthenticatedService,
            "message-1",
            "attachment-1");
        var unauthenticatedJson = JsonSerializer.Deserialize<JsonElement>(unauthenticatedResult);
        Assert.Equal("error", unauthenticatedJson.GetProperty("status").GetString());
        Assert.Contains("Not authenticated", unauthenticatedJson.GetProperty("message").GetString());

        var failingHandler = new AttachmentGraphHandler { ListStatusCode = HttpStatusCode.BadGateway };
        var (authService, graphService) = CreateServices(failingHandler);
        var graphError = await ListEmailAttachmentsTool.ListEmailAttachments(
            authService.Object,
            graphService,
            "message-1");
        var graphErrorJson = JsonSerializer.Deserialize<JsonElement>(graphError);
        Assert.Equal("error", graphErrorJson.GetProperty("status").GetString());
        Assert.Contains("502", graphErrorJson.GetProperty("message").GetString());
    }

    [Fact]
    public async Task ExistingEmailTools_ExposeLegacyAndAdditiveMessageIds()
    {
        var handler = new MockGraphHandler();
        handler.AddMessage("msg-1", "sender@example.com", "Subject", isRead: false);
        var authService = new Mock<AuthService>(Mock.Of<ILogger<AuthService>>());
        authService.SetupGet(a => a.IsAuthenticated).Returns(true);
        authService.Setup(a => a.CreateAuthenticatedHttpClient()).Returns(handler.CreateClient());
        var graphService = new GraphEmailService(authService.Object, Mock.Of<ILogger<GraphEmailService>>());

        var contentResult = await GetEmailContentTool.GetEmailContent(
            authService.Object,
            graphService,
            "sender@example.com");
        var contentJson = JsonSerializer.Deserialize<JsonElement>(contentResult);
        var email = contentJson.GetProperty("emails")[0];
        Assert.Equal("msg-1", email.GetProperty("Id").GetString());
        Assert.Equal("msg-1", email.GetProperty("messageId").GetString());

        var readResult = await ReadEmailsTool.ReadEmails(authService.Object, graphService, daysBack: 1);
        var readJson = JsonSerializer.Deserialize<JsonElement>(readResult);
        Assert.Equal("msg-1", readJson.GetProperty("senders")[0].GetProperty("sampleMessageId").GetString());
    }

    [Fact]
    public void AttachmentToolContractsExposeBoundedIdBasedParameters()
    {
        var listParameters = typeof(ListEmailAttachmentsTool)
            .GetMethod(nameof(ListEmailAttachmentsTool.ListEmailAttachments))!
            .GetParameters();
        Assert.Equal(new[] { "authService", "graphService", "messageId", "maxAttachments" },
            listParameters.Select(parameter => parameter.Name).ToArray());
        Assert.Equal(100, listParameters[^1].DefaultValue);

        var downloadParameters = typeof(DownloadEmailAttachmentTool)
            .GetMethod(nameof(DownloadEmailAttachmentTool.DownloadEmailAttachment))!
            .GetParameters();
        Assert.Equal(new[] { "authService", "graphService", "messageId", "attachmentId", "maxBytes" },
            downloadParameters.Select(parameter => parameter.Name).ToArray());
        Assert.Equal(4_000_000, downloadParameters[^1].DefaultValue);
    }

    private static (Mock<AuthService> AuthService, GraphEmailService GraphService) CreateServices(
        AttachmentGraphHandler handler)
    {
        var authService = new Mock<AuthService>(Mock.Of<ILogger<AuthService>>());
        authService.SetupGet(a => a.IsAuthenticated).Returns(true);
        authService.Setup(a => a.CreateAuthenticatedHttpClient()).Returns(handler.CreateClient());
        return (
            authService,
            new GraphEmailService(authService.Object, Mock.Of<ILogger<GraphEmailService>>()));
    }
}

internal sealed class AttachmentGraphHandler : HttpMessageHandler
{
    private readonly Queue<string> _listResponses = new();
    private readonly Queue<string> _metadataResponses = new();
    private readonly Queue<RawResponse> _rawResponses = new();

    public List<string> Requests { get; } = [];
    public HttpStatusCode ListStatusCode { get; init; } = HttpStatusCode.OK;

    public void EnqueueListResponse(string response) => _listResponses.Enqueue(response);
    public void EnqueueMetadataResponse(string response) => _metadataResponses.Enqueue(response);

    public void EnqueueRawResponse(byte[] bytes, string contentType, long? contentLength = null)
        => _rawResponses.Enqueue(new RawResponse(bytes, contentType, contentLength));

    public HttpClient CreateClient() => new(this);

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var url = request.RequestUri?.ToString() ?? string.Empty;
        Requests.Add(url);

        if (request.Method != HttpMethod.Get)
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.MethodNotAllowed));

        if (url.EndsWith("/$value", StringComparison.Ordinal))
        {
            if (_rawResponses.Count == 0)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

            var raw = _rawResponses.Dequeue();
            var content = new StreamContent(new MemoryStream(raw.Bytes));
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(raw.ContentType);
            if (raw.ContentLength.HasValue)
                content.Headers.ContentLength = raw.ContentLength.Value;
            else
                content.Headers.ContentLength = null;

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }

        if (url.Contains("/attachments?", StringComparison.Ordinal))
        {
            if (ListStatusCode != HttpStatusCode.OK)
            {
                return Task.FromResult(new HttpResponseMessage(ListStatusCode)
                {
                    Content = new StringContent("list failure", Encoding.UTF8, "text/plain")
                });
            }

            var response = _listResponses.Count > 0
                ? _listResponses.Dequeue()
                : """{ "value": [] }""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json")
            });
        }

        if (url.Contains("/attachments/", StringComparison.Ordinal) &&
            url.Contains("select=", StringComparison.Ordinal))
        {
            if (_metadataResponses.Count == 0)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    _metadataResponses.Dequeue(),
                    Encoding.UTF8,
                    "application/json")
            });
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private sealed record RawResponse(byte[] Bytes, string ContentType, long? ContentLength);
}
