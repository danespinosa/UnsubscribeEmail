using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Moq;
using UnsubscribeEmail.McpServer.Services;
using UnsubscribeEmail.McpServer.Tools;

namespace UnsubscribeEmail.Tests;

public sealed class ConsumerReliabilityTests
{
    [Fact]
    public void AuthenticatedGraphClientsPreferImmutableMessageIds()
    {
        var authService = new Mock<AuthService>(Mock.Of<ILogger<AuthService>>())
        {
            CallBase = true
        };
        authService.Setup(service => service.GetAccessToken()).Returns("access-token");

        using var client = authService.Object.CreateAuthenticatedHttpClient();

        Assert.Equal(
            AuthService.ImmutableIdPreferenceHeaderValue,
            client.DefaultRequestHeaders.GetValues("Prefer").Single());
    }

    [Fact]
    public async Task GetEmailContent_OmitsOrderByAndSortsNewestFirstAcrossPagesAndExclusions()
    {
        var now = DateTime.UtcNow;
        var handler = new ReliabilityGraphHandler();
        handler.SetFolderIds("deleted-folder", "junk-folder");
        handler.EnqueueMessagePage(
            MessagePage(
                CreateMessage("old-target", "sender@example.com", "old", GraphDateTime(now.AddDays(-4))),
                CreateMessage("excluded", "sender@example.com", "deleted", GraphDateTime(now.AddHours(-1)), parentFolderId: "deleted-folder"),
                nextLink: "https://graph.microsoft.com/v1.0/me/messages?page=2"));
        handler.EnqueueMessagePage(
            MessagePage(
                CreateMessage("new-target", "sender@example.com", "new", GraphDateTime(now.AddHours(-2))),
                CreateMessage("middle-target", "sender@example.com", "middle", GraphDateTime(now.AddDays(-1)))));
        var (authService, graphService) = CreateServices(handler);

        var result = await GetEmailContentTool.GetEmailContent(
            authService.Object,
            graphService,
            "sender@example.com",
            maxEmails: 2);

        var json = JsonSerializer.Deserialize<JsonElement>(result);
        Assert.Equal("success", json.GetProperty("status").GetString());
        var emails = json.GetProperty("emails");
        Assert.Equal(2, emails.GetArrayLength());
        Assert.Equal("new-target", emails[0].GetProperty("messageId").GetString());
        Assert.Equal("middle-target", emails[1].GetProperty("messageId").GetString());

        var messageRequests = handler.Requests
            .Where(request => request.Method == HttpMethod.Get &&
                              request.RequestUri!.AbsolutePath.EndsWith("/me/messages", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(2, messageRequests.Length);
        var initialQuery = messageRequests[0].RequestUri!.Query;
        var nextLinkQuery = messageRequests[1].RequestUri!.Query;
        Assert.Contains("$search=", initialQuery, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("$filter", initialQuery, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("$orderby", initialQuery, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("$top=100", initialQuery, StringComparison.Ordinal);
        Assert.DoesNotContain("$search=", nextLinkQuery, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("$filter", nextLinkQuery, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("$orderby", nextLinkQuery, StringComparison.OrdinalIgnoreCase);
        AssertImmutableIdPreference(handler.Requests);
    }

    [Fact]
    public async Task GetEmailContent_AppliesDaysBackClientSideWithoutChangingConsumerSearchShape()
    {
        var now = DateTime.UtcNow;
        var handler = new ReliabilityGraphHandler();
        handler.EnqueueMessagePage(
            MessagePage(
                CreateMessage("too-old", "sender@example.com", "old", GraphDateTime(now.AddDays(-10))),
                CreateMessage("in-range", "sender@example.com", "new", GraphDateTime(now.AddHours(-1))),
                nextLink: "https://graph.microsoft.com/v1.0/me/messages?page=2"));
        handler.EnqueueMessagePage(
            MessagePage(CreateMessage("also-in-range", "sender@example.com", "newer", GraphDateTime(now.AddDays(-1)))));
        var (authService, graphService) = CreateServices(handler);

        var result = await GetEmailContentTool.GetEmailContent(
            authService.Object,
            graphService,
            "sender@example.com",
            maxEmails: 10,
            daysBack: 2);

        var json = JsonSerializer.Deserialize<JsonElement>(result);
        var emails = json.GetProperty("emails");
        Assert.Equal(2, emails.GetArrayLength());
        Assert.Equal("in-range", emails[0].GetProperty("messageId").GetString());
        Assert.Equal("also-in-range", emails[1].GetProperty("messageId").GetString());
        Assert.All(
            handler.Requests.Where(request => request.RequestUri!.AbsolutePath.EndsWith("/me/messages", StringComparison.Ordinal)),
            request => Assert.DoesNotContain("$filter", request.RequestUri!.Query, StringComparison.OrdinalIgnoreCase));
        AssertImmutableIdPreference(handler.Requests);
    }

    [Fact]
    public async Task GraphErrorCodeMessageAndBodySurviveGetEmailContentToolBoundary()
    {
        var handler = new ReliabilityGraphHandler();
        handler.EnqueueMessageResponse(
            JsonResponse(
                HttpStatusCode.BadRequest,
                """{"error":{"code":"ErrorInvalidFilter","message":"Consumer filter rejected","innerError":{"code":"InnerConsumerError","request-id":"request-123","client-request-id":"client-456"}}}"""));
        var (authService, graphService) = CreateServices(handler);

        var result = await GetEmailContentTool.GetEmailContent(
            authService.Object,
            graphService,
            "sender@example.com");

        var json = JsonSerializer.Deserialize<JsonElement>(result);
        var message = json.GetProperty("message").GetString()!;
        Assert.Equal("error", json.GetProperty("status").GetString());
        Assert.Contains("ErrorInvalidFilter", message, StringComparison.Ordinal);
        Assert.Contains("Consumer filter rejected", message, StringComparison.Ordinal);
        Assert.Contains("InnerConsumerError", message, StringComparison.Ordinal);
        Assert.Contains("request-123", message, StringComparison.Ordinal);
        Assert.Contains("Response body", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BinaryGraphErrorBodiesAreNotDumpedIntoToolDiagnostics()
    {
        var handler = new ReliabilityGraphHandler();
        handler.EnqueueMessageResponse(
            new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new ByteArrayContent([0, 1, 2, 3])
                {
                    Headers = { ContentType = new MediaTypeHeaderValue("application/octet-stream") }
                }
            });
        var (authService, graphService) = CreateServices(handler);

        var result = await GetEmailContentTool.GetEmailContent(
            authService.Object,
            graphService,
            "sender@example.com");

        var json = JsonSerializer.Deserialize<JsonElement>(result);
        var message = json.GetProperty("message").GetString()!;
        Assert.Contains("Non-text Graph error body omitted", message, StringComparison.Ordinal);
        Assert.DoesNotContain("\u0000", message, StringComparison.Ordinal);
    }

    [Fact]
    public void GraphErrorsCaptureDeepestNestedInnerCodeAndRequestIds()
    {
        var details = GraphApiException.ReadError(
            """{"error":{"code":"Outer","message":"failure","request-id":"outer-request","innerError":{"code":"Middle","client-request-id":"middle-client","innerError":{"code":"Deepest","request-id":"deep-request"}}}}""");

        Assert.Equal("Deepest", details.InnerCode);
        Assert.Equal("deep-request", details.RequestId);
        Assert.Equal("middle-client", details.ClientRequestId);
    }

    [Fact]
    public async Task EveryMessageProducerRoundTripsOpaqueMessageIdIntoAttachmentListing()
    {
        const string messageId = "A-_/_+==%2F";
        var handler = new ReliabilityGraphHandler();
        var (authService, graphService) = CreateServices(handler);

        handler.EnqueueMessagePage(MessagePage(CreateMessage(
            messageId,
            "sender@example.com",
            "content",
            GraphDateTime(DateTime.UtcNow.AddMinutes(-1)),
            hasAttachments: true)));
        var content = JsonSerializer.Deserialize<JsonElement>(
            await GetEmailContentTool.GetEmailContent(
                authService.Object,
                graphService,
                "sender@example.com"))!;
        var contentMessageId = content.GetProperty("emails")[0].GetProperty("messageId").GetString();

        handler.EnqueueMessagePage(MessagePage(CreateMessage(
            messageId,
            "sender@example.com",
            "mark",
            GraphDateTime(DateTime.UtcNow.AddMinutes(-1)),
            hasAttachments: true)));
        var marked = JsonSerializer.Deserialize<JsonElement>(
            await MarkEmailsAsReadTool.MarkEmailsAsRead(
                authService.Object,
                graphService,
                senderEmail: "sender@example.com",
                dryRun: true))!;
        var markedMessageId = marked.GetProperty("messages")[0].GetProperty("messageId").GetString();

        handler.EnqueueMessagePage(MessagePage(CreateMessage(
            messageId,
            "sender@example.com",
            "read",
            GraphDateTime(DateTime.UtcNow.AddMinutes(-1)),
            hasAttachments: true)));
        var read = JsonSerializer.Deserialize<JsonElement>(
            await ReadEmailsTool.ReadEmails(authService.Object, graphService, daysBack: 1))!;
        var readMessageId = read.GetProperty("senders")[0].GetProperty("sampleMessageId").GetString();

        handler.EnqueueMessagePage(MessagePage(CreateMessage(
            messageId,
            "sender@example.com",
            "search",
            GraphDateTime(DateTime.UtcNow.AddMinutes(-1)),
            hasAttachments: true)));
        var searched = JsonSerializer.Deserialize<JsonElement>(
            await SearchEmailsTool.SearchEmails(
                authService.Object,
                graphService,
                senderEmail: "sender@example.com",
                maxEmails: 1,
                daysBack: 1))!;
        var searchedMessageId = searched.GetProperty("messages")[0].GetProperty("messageId").GetString();

        Assert.Equal(messageId, contentMessageId);
        Assert.Equal(messageId, markedMessageId);
        Assert.Equal(messageId, readMessageId);
        Assert.Equal(messageId, searchedMessageId);

        foreach (var producerMessageId in new[] { contentMessageId, markedMessageId, readMessageId, searchedMessageId })
        {
            var listed = JsonSerializer.Deserialize<JsonElement>(
                await ListEmailAttachmentsTool.ListEmailAttachments(
                    authService.Object,
                    graphService,
                    producerMessageId!))!;
            Assert.Equal("success", listed.GetProperty("status").GetString());
            Assert.Equal(messageId, listed.GetProperty("messageId").GetString());
        }

        var listRequests = handler.Requests
            .Where(request => request.RequestUri!.AbsolutePath.EndsWith("/attachments", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(4, listRequests.Length);
        Assert.All(listRequests, request =>
        {
            var encodedSegment = request.RequestUri!.AbsolutePath.Split('/')[^2];
            Assert.Equal(messageId, Uri.UnescapeDataString(encodedSegment));
            Assert.Equal(Uri.EscapeDataString(messageId), encodedSegment);
        });
        AssertImmutableIdPreference(handler.Requests);
    }

    [Fact]
    public async Task MarkAsReadEscapesOpaqueMessageIdForPatchPath()
    {
        const string messageId = "mark-_/%2F+=";
        var handler = new ReliabilityGraphHandler();
        handler.EnqueueMessagePage(MessagePage(CreateMessage(
            messageId,
            "sender@example.com",
            "mark",
            GraphDateTime(DateTime.UtcNow.AddMinutes(-1)))));
        var (authService, graphService) = CreateServices(handler);

        var result = JsonSerializer.Deserialize<JsonElement>(
            await MarkEmailsAsReadTool.MarkEmailsAsRead(
                authService.Object,
                graphService,
                senderEmail: "sender@example.com"))!;

        Assert.Equal("success", result.GetProperty("status").GetString());
        var patch = handler.Requests.Single(request => request.Method == HttpMethod.Patch);
        var encodedSegment = patch.RequestUri!.AbsolutePath.Split('/')[^1];
        Assert.Equal(messageId, Uri.UnescapeDataString(encodedSegment));
        Assert.Equal(Uri.EscapeDataString(messageId), encodedSegment);
        AssertImmutableIdPreference(handler.Requests);
    }

    [Fact]
    public async Task ListedAttachmentIdRoundTripsToMetadataAndValueWithOnePathEncoding()
    {
        const string messageId = "message-_/%2F+=";
        const string attachmentId = "attachment-_/%2F+=";
        var pdfBytes = Encoding.ASCII.GetBytes("%PDF-1.7\r\nbody\r\n%%EOF");
        var handler = new ReliabilityGraphHandler();
        handler.EnqueueAttachmentListResponse(
            JsonResponse(
                HttpStatusCode.OK,
                $$"""{"value":[{"@odata.type":"#microsoft.graph.fileAttachment","id":"{{attachmentId}}","name":"file.pdf","contentType":"application/pdf","size":999}]}"""));
        handler.EnqueueAttachmentMetadataResponse(
            JsonResponse(
                HttpStatusCode.OK,
                $$"""{"@odata.type":"#microsoft.graph.fileAttachment","id":"{{attachmentId}}","name":"file.pdf","contentType":"application/pdf","size":999}"""));
        handler.EnqueueAttachmentValueResponse(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(pdfBytes)
            });
        var (authService, graphService) = CreateServices(handler);

        var listed = JsonSerializer.Deserialize<JsonElement>(
            await ListEmailAttachmentsTool.ListEmailAttachments(
                authService.Object,
                graphService,
                messageId))!;
        var exactAttachmentId = listed.GetProperty("attachments")[0].GetProperty("attachmentId").GetString();
        Assert.Equal(attachmentId, exactAttachmentId);

        var downloaded = JsonSerializer.Deserialize<JsonElement>(
            await DownloadEmailAttachmentTool.DownloadEmailAttachment(
                authService.Object,
                graphService,
                listed.GetProperty("messageId").GetString()!,
                exactAttachmentId!))!;
        Assert.Equal("success", downloaded.GetProperty("status").GetString());
        Assert.Equal(attachmentId, downloaded.GetProperty("attachmentId").GetString());
        Assert.Equal(999, downloaded.GetProperty("size").GetInt64());
        Assert.Equal(pdfBytes.LongLength, downloaded.GetProperty("downloadedSize").GetInt64());
        Assert.Equal(Convert.ToBase64String(pdfBytes), downloaded.GetProperty("base64Content").GetString());

        var requests = handler.Requests
            .Where(request => request.RequestUri!.AbsolutePath.Contains("/attachments", StringComparison.Ordinal))
            .Select(request => request.RequestUri!.ToString())
            .ToArray();
        Assert.Contains($"/me/messages/{Uri.EscapeDataString(messageId)}/attachments", requests[0], StringComparison.Ordinal);
        Assert.Contains($"/attachments/{Uri.EscapeDataString(attachmentId)}?", requests[1], StringComparison.Ordinal);
        Assert.EndsWith(
            $"/me/messages/{Uri.EscapeDataString(messageId)}/attachments/{Uri.EscapeDataString(attachmentId)}/$value",
            requests[2],
            StringComparison.Ordinal);
        Assert.DoesNotContain(Uri.UnescapeDataString(Uri.EscapeDataString(attachmentId)), requests[1], StringComparison.Ordinal);
        AssertImmutableIdPreference(handler.Requests);
    }

    [Fact]
    public async Task AttachmentOperationsAreSerializedAcrossListAndDownload()
    {
        var handler = new ReliabilityGraphHandler { BlockFirstAttachmentList = true };
        handler.EnqueueAttachmentListResponse(JsonResponse(HttpStatusCode.OK, """{"value":[]}"""));
        handler.EnqueueAttachmentMetadataResponse(
            JsonResponse(
                HttpStatusCode.OK,
                """{"@odata.type":"#microsoft.graph.fileAttachment","id":"att","contentType":"text/plain","size":1}"""));
        handler.EnqueueAttachmentValueResponse(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1])
            });
        var (authService, graphService) = CreateServices(handler);

        var listTask = graphService.GetEmailAttachmentsAsync("message");
        await handler.FirstAttachmentListEntered.Task;
        var downloadTask = graphService.DownloadEmailAttachmentAsync("message", "att");
        handler.ReleaseFirstAttachmentList.TrySetResult();

        await Task.WhenAll(listTask, downloadTask);
        Assert.Equal(1, handler.MaxConcurrentAttachmentRequests);
    }

    [Fact]
    public async Task AttachmentRetriesHonorDeltaRetryAfterWithoutSleeping()
    {
        var handler = new ReliabilityGraphHandler();
        handler.EnqueueAttachmentListResponse(
            JsonResponse(HttpStatusCode.TooManyRequests, GraphErrorJson("ApplicationThrottled", "Mailbox busy"), retryAfter: TimeSpan.FromSeconds(7)));
        handler.EnqueueAttachmentListResponse(JsonResponse(HttpStatusCode.OK, """{"value":[]}"""));
        var retryDelay = new RecordingRetryDelay();
        var (authService, graphService) = CreateServices(handler, retryDelay);

        await graphService.GetEmailAttachmentsAsync("message");

        Assert.Equal([TimeSpan.FromSeconds(7)], retryDelay.Delays);
        Assert.Equal(2, handler.AttachmentListRequestCount);
    }

    [Fact]
    public async Task AttachmentRetriesHonorHttpDateRetryAfterWithoutSleeping()
    {
        var now = new DateTimeOffset(2026, 9, 24, 10, 0, 0, TimeSpan.Zero);
        var handler = new ReliabilityGraphHandler();
        handler.EnqueueAttachmentListResponse(
            JsonResponse(
                HttpStatusCode.TooManyRequests,
                GraphErrorJson("ApplicationThrottled", "Mailbox busy"),
                retryAfterDate: now.AddSeconds(5)));
        handler.EnqueueAttachmentListResponse(JsonResponse(HttpStatusCode.OK, """{"value":[]}"""));
        var retryDelay = new RecordingRetryDelay();
        var (authService, graphService) = CreateServices(
            handler,
            retryDelay,
            new FixedTimeProvider(now));

        await graphService.GetEmailAttachmentsAsync("message");

        Assert.Equal([TimeSpan.FromSeconds(5)], retryDelay.Delays);
    }

    [Fact]
    public async Task AttachmentRetriesUseBoundedFallbackAndPreserveRetryableGraphErrorAtToolBoundary()
    {
        var handler = new ReliabilityGraphHandler();
        for (var i = 0; i < 4; i++)
        {
            handler.EnqueueAttachmentListResponse(
                JsonResponse(
                    HttpStatusCode.TooManyRequests,
                    GraphErrorJson("ApplicationThrottled", "Mailbox concurrency limit"),
                    retryAfter: null));
        }

        var retryDelay = new RecordingRetryDelay();
        var (authService, graphService) = CreateServices(handler, retryDelay);
        var result = await ListEmailAttachmentsTool.ListEmailAttachments(
            authService.Object,
            graphService,
            "message");

        var json = JsonSerializer.Deserialize<JsonElement>(result);
        var message = json.GetProperty("message").GetString()!;
        Assert.Equal("error", json.GetProperty("status").GetString());
        Assert.Contains("ApplicationThrottled", message, StringComparison.Ordinal);
        Assert.Contains("Mailbox concurrency limit", message, StringComparison.Ordinal);
        Assert.Contains("after 3 retries", message, StringComparison.Ordinal);
        Assert.Contains("retryable=true", message, StringComparison.Ordinal);
        Assert.Contains("retry-after=unknown", message, StringComparison.Ordinal);
        Assert.Contains("total-retry-delay=00:00:07", message, StringComparison.Ordinal);
        Assert.Equal(429, json.GetProperty("statusCode").GetInt32());
        Assert.Equal("ApplicationThrottled", json.GetProperty("code").GetString());
        Assert.True(json.GetProperty("retryable").GetBoolean());
        Assert.True(json.GetProperty("retryAfterSeconds").ValueKind == JsonValueKind.Null);
        Assert.Equal(7, json.GetProperty("totalRetryDelaySeconds").GetDouble());
        Assert.Equal(3, json.GetProperty("retryCount").GetInt32());
        Assert.Equal(
            [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)],
            retryDelay.Delays);
    }

    [Fact]
    public async Task RetryAfterDetailsSurviveAttachmentToolBoundaryAfterExhaustion()
    {
        var handler = new ReliabilityGraphHandler();
        for (var i = 0; i < 4; i++)
        {
            handler.EnqueueAttachmentListResponse(
                JsonResponse(
                    HttpStatusCode.TooManyRequests,
                    GraphErrorJson("ApplicationThrottled", "Mailbox concurrency limit"),
                    retryAfter: TimeSpan.FromSeconds(7)));
        }

        var retryDelay = new RecordingRetryDelay();
        var (authService, graphService) = CreateServices(handler, retryDelay);
        var result = await ListEmailAttachmentsTool.ListEmailAttachments(
            authService.Object,
            graphService,
            "message");

        var json = JsonSerializer.Deserialize<JsonElement>(result);
        var message = json.GetProperty("message").GetString()!;
        Assert.Equal("error", json.GetProperty("status").GetString());
        Assert.Contains("ApplicationThrottled", message, StringComparison.Ordinal);
        Assert.Contains("after 3 retries", message, StringComparison.Ordinal);
        Assert.Contains("retry-after=00:00:07", message, StringComparison.Ordinal);
        Assert.Contains("total-retry-delay=00:00:21", message, StringComparison.Ordinal);
        Assert.Equal(7, json.GetProperty("retryAfterSeconds").GetDouble());
        Assert.Equal(21, json.GetProperty("totalRetryDelaySeconds").GetDouble());
        Assert.Equal(
            [TimeSpan.FromSeconds(7), TimeSpan.FromSeconds(7), TimeSpan.FromSeconds(7)],
            retryDelay.Delays);
    }

    [Fact]
    public async Task AttachmentNonTransientErrorsAreNotRetried()
    {
        var handler = new ReliabilityGraphHandler();
        handler.EnqueueAttachmentListResponse(
            JsonResponse(
                HttpStatusCode.BadRequest,
                GraphErrorJson("ErrorInvalidIdMalformed", "The message ID is malformed")));
        var retryDelay = new RecordingRetryDelay();
        var (authService, graphService) = CreateServices(handler, retryDelay);

        var exception = await Assert.ThrowsAsync<GraphApiException>(() =>
            graphService.GetEmailAttachmentsAsync("message"));

        Assert.Equal("ErrorInvalidIdMalformed", exception.GraphCode);
        Assert.False(exception.IsRetryable);
        Assert.Empty(retryDelay.Delays);
        Assert.Equal(1, handler.AttachmentListRequestCount);
    }

    [Fact]
    public async Task AttachmentRetryDelayReceivesCancellationToken()
    {
        var handler = new ReliabilityGraphHandler();
        handler.EnqueueAttachmentListResponse(
            JsonResponse(
                HttpStatusCode.TooManyRequests,
                GraphErrorJson("ApplicationThrottled", "Mailbox busy")));
        using var cancellation = new CancellationTokenSource();
        var retryDelay = new CancellingRetryDelay(cancellation);
        var (authService, graphService) = CreateServices(handler, retryDelay);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            graphService.GetEmailAttachmentsAsync("message", cancellationToken: cancellation.Token));

        Assert.True(retryDelay.ReceivedCancellationToken);
        Assert.Equal(1, handler.AttachmentListRequestCount);
    }

    [Fact]
    public async Task SearchEmailsIsCappedPagedNewestFirstAndDoesNotFetchAttachments()
    {
        var now = DateTime.UtcNow;
        var handler = new ReliabilityGraphHandler();
        handler.EnqueueMessagePage(
            MessagePage(
                CreateMessage("newest", "sender@example.com", "match", GraphDateTime(now.AddHours(-1)), hasAttachments: true),
                CreateMessage("second", "sender@example.com", "match two", GraphDateTime(now.AddHours(-2)), hasAttachments: true),
                nextLink: "https://graph.microsoft.com/v1.0/me/messages?page=2"));
        var (authService, graphService) = CreateServices(handler);

        var result = await SearchEmailsTool.SearchEmails(
            authService.Object,
            graphService,
            senderEmail: "sender@example.com",
            maxEmails: 2,
            daysBack: 30);

        var json = JsonSerializer.Deserialize<JsonElement>(result);
        Assert.Equal("success", json.GetProperty("status").GetString());
        Assert.Equal(2, json.GetProperty("messageCount").GetInt32());
        var messages = json.GetProperty("messages");
        Assert.Equal("newest", messages[0].GetProperty("messageId").GetString());
        Assert.Equal("second", messages[1].GetProperty("messageId").GetString());
        Assert.True(messages[0].GetProperty("hasAttachments").GetBoolean());
        Assert.True(json.GetProperty("hasMore").GetBoolean());
        Assert.DoesNotContain(
            handler.Requests,
            request => request.RequestUri!.AbsolutePath.Contains("/attachments", StringComparison.Ordinal));
        Assert.Single(
            handler.Requests,
            request => request.RequestUri!.AbsolutePath.EndsWith("/me/messages", StringComparison.Ordinal));
        AssertImmutableIdPreference(handler.Requests);
    }

    [Fact]
    public async Task SearchEmailsStopsAfterOneCandidatePageForSelectiveFilters()
    {
        var now = DateTime.UtcNow;
        var handler = new ReliabilityGraphHandler();
        handler.EnqueueMessagePage(
            MessagePage(
                CreateMessage("non-match", "other@example.com", "other", GraphDateTime(now.AddHours(-1))),
                nextLink: "https://graph.microsoft.com/v1.0/me/messages?page=2"));
        var (authService, graphService) = CreateServices(handler);

        var result = await SearchEmailsTool.SearchEmails(
            authService.Object,
            graphService,
            senderEmail: "missing@example.com",
            maxEmails: 5,
            daysBack: 30);

        var json = JsonSerializer.Deserialize<JsonElement>(result);
        Assert.Equal("success", json.GetProperty("status").GetString());
        Assert.Equal(0, json.GetProperty("messageCount").GetInt32());
        Assert.True(json.GetProperty("hasMore").GetBoolean());
        Assert.Single(
            handler.Requests,
            request => request.RequestUri!.AbsolutePath.EndsWith("/me/messages", StringComparison.Ordinal));
        AssertImmutableIdPreference(handler.Requests);
    }

    private static (Mock<AuthService> AuthService, GraphEmailService GraphService) CreateServices(
        ReliabilityGraphHandler handler,
        IGraphRetryDelay? retryDelay = null,
        TimeProvider? timeProvider = null)
    {
        var authService = new Mock<AuthService>(Mock.Of<ILogger<AuthService>>());
        authService.SetupGet(service => service.IsAuthenticated).Returns(true);
        authService
            .Setup(service => service.CreateAuthenticatedHttpClient())
            .Returns(() =>
            {
                var client = handler.CreateClient();
                client.DefaultRequestHeaders.TryAddWithoutValidation(
                    "Prefer",
                    AuthService.ImmutableIdPreferenceHeaderValue);
                return client;
            });
        return (
            authService,
            new GraphEmailService(
                authService.Object,
                Mock.Of<ILogger<GraphEmailService>>(),
                retryDelay,
                timeProvider,
                new NoOpGraphRetryJitter()));
    }

    private static void AssertImmutableIdPreference(IEnumerable<HttpRequestMessage> requests)
    {
        Assert.NotEmpty(requests);
        Assert.All(
            requests,
            request => Assert.Equal(
                AuthService.ImmutableIdPreferenceHeaderValue,
                request.Headers.GetValues("Prefer").Single()));
    }

    private static string CreateMessage(
        string id,
        string sender,
        string subject,
        string receivedDateTime,
        string parentFolderId = "inbox-folder",
        bool hasAttachments = false)
    {
        return JsonSerializer.Serialize(new
        {
            id,
            subject,
            body = new { content = $"<p>{subject}</p>" },
            from = new { emailAddress = new { name = "Sender", address = sender } },
            toRecipients = new[] { new { emailAddress = new { address = "me@example.com" } } },
            receivedDateTime,
            isRead = false,
            hasAttachments,
            parentFolderId
        });
    }

    private static string GraphDateTime(DateTime value)
        => value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ");

    private static string MessagePage(string first, string? second = null, string? nextLink = null)
    {
        var values = second is null ? first : $"{first},{second}";
        return nextLink is null
            ? $$"""{"value":[{{values}}]}"""
            : $$"""{"value":[{{values}}],"@odata.nextLink":"{{nextLink}}"}""";
    }

    private static HttpResponseMessage JsonResponse(
        HttpStatusCode statusCode,
        string body,
        TimeSpan? retryAfter = null,
        DateTimeOffset? retryAfterDate = null)
    {
        var response = new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        if (retryAfter.HasValue)
            response.Headers.RetryAfter = new RetryConditionHeaderValue(retryAfter.Value);
        if (retryAfterDate.HasValue)
            response.Headers.RetryAfter = new RetryConditionHeaderValue(retryAfterDate.Value);
        return response;
    }

    private static string GraphErrorJson(string code, string message)
        => JsonSerializer.Serialize(new { error = new { code, message } });
}

internal sealed class ReliabilityGraphHandler : HttpMessageHandler
{
    private readonly ConcurrentQueue<HttpResponseMessage> _messageResponses = new();
    private readonly ConcurrentQueue<HttpResponseMessage> _attachmentListResponses = new();
    private readonly ConcurrentQueue<HttpResponseMessage> _attachmentMetadataResponses = new();
    private readonly ConcurrentQueue<HttpResponseMessage> _attachmentValueResponses = new();
    private int _attachmentListRequestCount;
    private int _activeAttachmentRequests;
    private int _maxConcurrentAttachmentRequests;
    private int _firstAttachmentListSeen;

    public List<HttpRequestMessage> Requests { get; } = [];
    public string DeletedFolderId { get; private set; } = string.Empty;
    public string JunkFolderId { get; private set; } = string.Empty;
    public bool BlockFirstAttachmentList { get; init; }
    public TaskCompletionSource FirstAttachmentListEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReleaseFirstAttachmentList { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int AttachmentListRequestCount => Volatile.Read(ref _attachmentListRequestCount);
    public int MaxConcurrentAttachmentRequests => Volatile.Read(ref _maxConcurrentAttachmentRequests);

    public void SetFolderIds(string deletedFolderId, string junkFolderId)
    {
        DeletedFolderId = deletedFolderId;
        JunkFolderId = junkFolderId;
    }

    public void EnqueueMessagePage(string body)
        => _messageResponses.Enqueue(JsonResponse(HttpStatusCode.OK, body));

    public void EnqueueMessageResponse(HttpResponseMessage response)
        => _messageResponses.Enqueue(response);

    public void EnqueueAttachmentListResponse(HttpResponseMessage response)
        => _attachmentListResponses.Enqueue(response);

    public void EnqueueAttachmentMetadataResponse(HttpResponseMessage response)
        => _attachmentMetadataResponses.Enqueue(response);

    public void EnqueueAttachmentValueResponse(HttpResponseMessage response)
        => _attachmentValueResponses.Enqueue(response);

    public HttpClient CreateClient() => new(this);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        lock (Requests)
        {
            Requests.Add(request);
        }

        var uri = request.RequestUri!;
        var url = uri.ToString();
        var path = uri.AbsolutePath;

        if (path.EndsWith("/mailFolders", StringComparison.Ordinal))
        {
            var decodedUrl = Uri.UnescapeDataString(url);
            var folderId = decodedUrl.Contains("Deleted Items", StringComparison.Ordinal)
                ? DeletedFolderId
                : decodedUrl.Contains("Junk Email", StringComparison.Ordinal)
                    ? JunkFolderId
                    : string.Empty;
            var body = string.IsNullOrEmpty(folderId)
                ? """{"value":[]}"""
                : $$"""{"value":[{"id":"{{folderId}}"}]}""";
            return JsonResponse(HttpStatusCode.OK, body);
        }

        if (request.Method == HttpMethod.Patch && path.Contains("/me/messages/", StringComparison.Ordinal))
        {
            return JsonResponse(HttpStatusCode.OK, """{"isRead":true}""");
        }

        if (path.EndsWith("/me/messages", StringComparison.Ordinal))
        {
            return _messageResponses.TryDequeue(out var messageResponse)
                ? messageResponse
                : JsonResponse(HttpStatusCode.OK, """{"value":[]}""");
        }

        if (path.EndsWith("/$value", StringComparison.Ordinal))
        {
            return await HandleAttachmentRequestAsync(
                _attachmentValueResponses,
                cancellationToken);
        }

        if (path.Contains("/attachments/", StringComparison.Ordinal) &&
            uri.Query.Contains("$select", StringComparison.Ordinal))
        {
            return await HandleAttachmentRequestAsync(
                _attachmentMetadataResponses,
                cancellationToken);
        }

        if (path.EndsWith("/attachments", StringComparison.Ordinal))
        {
            Interlocked.Increment(ref _attachmentListRequestCount);
            return await HandleAttachmentListRequestAsync(cancellationToken);
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private async Task<HttpResponseMessage> HandleAttachmentListRequestAsync(
        CancellationToken cancellationToken)
    {
        var active = Interlocked.Increment(ref _activeAttachmentRequests);
        UpdateMaxConcurrent(active);
        try
        {
            if (BlockFirstAttachmentList &&
                Interlocked.CompareExchange(ref _firstAttachmentListSeen, 1, 0) == 0)
            {
                FirstAttachmentListEntered.TrySetResult();
                await ReleaseFirstAttachmentList.Task.WaitAsync(cancellationToken);
            }

            return _attachmentListResponses.TryDequeue(out var response)
                ? response
                : JsonResponse(HttpStatusCode.OK, """{"value":[]}""");
        }
        finally
        {
            Interlocked.Decrement(ref _activeAttachmentRequests);
        }
    }

    private async Task<HttpResponseMessage> HandleAttachmentRequestAsync(
        ConcurrentQueue<HttpResponseMessage> responses,
        CancellationToken cancellationToken)
    {
        var active = Interlocked.Increment(ref _activeAttachmentRequests);
        UpdateMaxConcurrent(active);
        try
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            return responses.TryDequeue(out var response)
                ? response
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        }
        finally
        {
            Interlocked.Decrement(ref _activeAttachmentRequests);
        }
    }

    private static HttpResponseMessage JsonResponse(
        HttpStatusCode statusCode,
        string body)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
    }

    private void UpdateMaxConcurrent(int active)
    {
        while (true)
        {
            var current = Volatile.Read(ref _maxConcurrentAttachmentRequests);
            if (active <= current ||
                Interlocked.CompareExchange(ref _maxConcurrentAttachmentRequests, active, current) == current)
                return;
        }
    }
}

internal sealed class RecordingRetryDelay : IGraphRetryDelay
{
    public List<TimeSpan> Delays { get; } = [];

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Delays.Add(delay);
        return Task.CompletedTask;
    }
}

internal sealed class CancellingRetryDelay : IGraphRetryDelay
{
    private readonly CancellationTokenSource _cancellation;

    public CancellingRetryDelay(CancellationTokenSource cancellation)
    {
        _cancellation = cancellation;
    }

    public bool ReceivedCancellationToken { get; private set; }

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        ReceivedCancellationToken = cancellationToken.CanBeCanceled;
        _cancellation.Cancel();
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}

internal sealed class FixedTimeProvider : TimeProvider
{
    private readonly DateTimeOffset _now;

    public FixedTimeProvider(DateTimeOffset now)
    {
        _now = now;
    }

    public override DateTimeOffset GetUtcNow() => _now;
}

internal sealed class NoOpGraphRetryJitter : IGraphRetryJitter
{
    public TimeSpan Apply(TimeSpan baseDelay) => baseDelay;
}
