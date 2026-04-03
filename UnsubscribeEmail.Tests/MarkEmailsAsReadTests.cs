using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Moq;
using UnsubscribeEmail.McpServer.Services;
using UnsubscribeEmail.McpServer.Tools;

namespace UnsubscribeEmail.Tests;

public class MarkEmailsAsReadTests
{
    #region Tool Tests

    [Fact]
    public async Task Tool_NotAuthenticated_ReturnsError()
    {
        var authService = CreateMockAuthService(isAuthenticated: false);
        var graphService = CreateGraphService(authService.Object);

        var result = await MarkEmailsAsReadTool.MarkEmailsAsRead(
            authService.Object, graphService, senderEmail: "test@example.com");

        var json = JsonSerializer.Deserialize<JsonElement>(result);
        Assert.Equal("error", json.GetProperty("status").GetString());
        Assert.Contains("Not authenticated", json.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Tool_NoFiltersProvided_ReturnsError()
    {
        var authService = CreateMockAuthService(isAuthenticated: true);
        var graphService = CreateGraphService(authService.Object);

        var result = await MarkEmailsAsReadTool.MarkEmailsAsRead(
            authService.Object, graphService, senderEmail: null, senderDomain: null);

        var json = JsonSerializer.Deserialize<JsonElement>(result);
        Assert.Equal("error", json.GetProperty("status").GetString());
        Assert.Contains("filter", json.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Tool_ValidRequest_ReturnsStructuredResult()
    {
        var handler = new MockGraphHandler();
        handler.AddMessage("msg-1", "sender@example.com", "Test Subject", isRead: false);
        var (authService, graphService) = CreateServicesWithHandler(handler);

        var result = await MarkEmailsAsReadTool.MarkEmailsAsRead(
            authService.Object, graphService, senderEmail: "sender@example.com");

        var json = JsonSerializer.Deserialize<JsonElement>(result);
        Assert.Equal("success", json.GetProperty("status").GetString());
        Assert.Equal(1, json.GetProperty("totalMatched").GetInt32());
        Assert.Equal(1, json.GetProperty("markedAsRead").GetInt32());
        Assert.Equal(0, json.GetProperty("skippedAlreadyRead").GetInt32());
        Assert.Equal(0, json.GetProperty("errors").GetInt32());
        Assert.False(json.GetProperty("dryRun").GetBoolean());

        var messages = json.GetProperty("messages");
        Assert.Equal(1, messages.GetArrayLength());
        Assert.Equal("msg-1", messages[0].GetProperty("messageId").GetString());
        Assert.Equal("Test Subject", messages[0].GetProperty("subject").GetString());
        Assert.True(messages[0].GetProperty("markedAsRead").GetBoolean());
    }

    [Fact]
    public async Task Tool_DryRunTrue_ReflectedInResponse()
    {
        var handler = new MockGraphHandler();
        handler.AddMessage("msg-1", "sender@example.com", "Email", isRead: false);
        var (authService, graphService) = CreateServicesWithHandler(handler);

        var result = await MarkEmailsAsReadTool.MarkEmailsAsRead(
            authService.Object, graphService, senderEmail: "sender@example.com", dryRun: true);

        var json = JsonSerializer.Deserialize<JsonElement>(result);
        Assert.Equal("success", json.GetProperty("status").GetString());
        Assert.True(json.GetProperty("dryRun").GetBoolean());
        Assert.Equal(0, json.GetProperty("markedAsRead").GetInt32());
    }

    #endregion

    #region Service Tests

    [Fact]
    public async Task Service_MarksUnreadEmailAsRead()
    {
        var handler = new MockGraphHandler();
        handler.AddMessage("msg-1", "sender@example.com", "Hello", isRead: false);
        var service = CreateServiceWithHandler(handler).GraphService;

        var results = await service.MarkEmailsAsReadAsync("sender@example.com", null, true, false, null);

        Assert.Single(results);
        Assert.Equal("msg-1", results[0].MessageId);
        Assert.True(results[0].MarkedAsRead);
        Assert.False(results[0].WasAlreadyRead);
        Assert.Null(results[0].Error);
        Assert.Contains(handler.Requests,
            r => r.Method == HttpMethod.Patch && r.RequestUri!.ToString().Contains("msg-1"));
    }

    [Fact]
    public async Task Service_DryRun_DoesNotPatchMessages()
    {
        var handler = new MockGraphHandler();
        handler.AddMessage("msg-1", "sender@example.com", "Hello", isRead: false);
        var service = CreateServiceWithHandler(handler).GraphService;

        var results = await service.MarkEmailsAsReadAsync("sender@example.com", null, true, dryRun: true, null);

        Assert.Single(results);
        Assert.False(results[0].MarkedAsRead);
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Patch);
    }

    [Fact]
    public async Task Service_FiltersBySenderDomain()
    {
        var handler = new MockGraphHandler();
        handler.AddMessage("msg-1", "news@example.com", "Newsletter", isRead: false);
        handler.AddMessage("msg-2", "alerts@other.com", "Alert", isRead: false);
        var service = CreateServiceWithHandler(handler).GraphService;

        var results = await service.MarkEmailsAsReadAsync(null, "example.com", true, false, null);

        Assert.Single(results);
        Assert.Equal("msg-1", results[0].MessageId);
    }

    [Fact]
    public async Task Service_DomainFilter_IsCaseInsensitive()
    {
        var handler = new MockGraphHandler();
        handler.AddMessage("msg-1", "news@Example.COM", "Newsletter", isRead: false);
        var service = CreateServiceWithHandler(handler).GraphService;

        var results = await service.MarkEmailsAsReadAsync(null, "example.com", true, false, null);

        Assert.Single(results);
        Assert.Equal("msg-1", results[0].MessageId);
    }

    [Fact]
    public async Task Service_UnreadOnlyFalse_IncludesReadEmails()
    {
        var handler = new MockGraphHandler();
        handler.AddMessage("msg-1", "sender@example.com", "Read Email", isRead: true);
        var service = CreateServiceWithHandler(handler).GraphService;

        var results = await service.MarkEmailsAsReadAsync("sender@example.com", null, unreadOnly: false, false, null);

        Assert.Single(results);
        Assert.True(results[0].WasAlreadyRead);
        Assert.False(results[0].MarkedAsRead);
    }

    [Fact]
    public async Task Service_ExcludesDeletedFolder()
    {
        var handler = new MockGraphHandler();
        handler.SetDeletedFolderId("deleted-folder-123");
        handler.AddMessage("msg-1", "sender@example.com", "Deleted", isRead: false, parentFolderId: "deleted-folder-123");
        handler.AddMessage("msg-2", "sender@example.com", "Inbox", isRead: false, parentFolderId: "inbox-id");
        var service = CreateServiceWithHandler(handler).GraphService;

        var results = await service.MarkEmailsAsReadAsync("sender@example.com", null, true, false, null);

        Assert.Single(results);
        Assert.Equal("msg-2", results[0].MessageId);
    }

    [Fact]
    public async Task Service_ExcludesJunkFolder()
    {
        var handler = new MockGraphHandler();
        handler.SetJunkFolderId("junk-folder-456");
        handler.AddMessage("msg-1", "sender@example.com", "Junk", isRead: false, parentFolderId: "junk-folder-456");
        handler.AddMessage("msg-2", "sender@example.com", "Inbox", isRead: false, parentFolderId: "inbox-id");
        var service = CreateServiceWithHandler(handler).GraphService;

        var results = await service.MarkEmailsAsReadAsync("sender@example.com", null, true, false, null);

        Assert.Single(results);
        Assert.Equal("msg-2", results[0].MessageId);
    }

    [Fact]
    public async Task Service_HandlesPatchFailure_RecordsError()
    {
        var handler = new MockGraphHandler();
        handler.AddMessage("msg-1", "sender@example.com", "Email", isRead: false);
        handler.FailPatchForMessage("msg-1");
        var service = CreateServiceWithHandler(handler).GraphService;

        var results = await service.MarkEmailsAsReadAsync("sender@example.com", null, true, false, null);

        Assert.Single(results);
        Assert.False(results[0].MarkedAsRead);
        Assert.NotNull(results[0].Error);
    }

    [Fact]
    public async Task Service_MultipleEmails_ProcessesAll()
    {
        var handler = new MockGraphHandler();
        handler.AddMessage("msg-1", "sender@example.com", "Email 1", isRead: false);
        handler.AddMessage("msg-2", "sender@example.com", "Email 2", isRead: false);
        handler.AddMessage("msg-3", "sender@example.com", "Email 3", isRead: false);
        var service = CreateServiceWithHandler(handler).GraphService;

        var results = await service.MarkEmailsAsReadAsync("sender@example.com", null, true, false, null);

        Assert.Equal(3, results.Count);
        Assert.All(results, r => Assert.True(r.MarkedAsRead));
    }

    [Fact]
    public async Task Service_NoMatchingEmails_ReturnsEmptyList()
    {
        var handler = new MockGraphHandler();
        var service = CreateServiceWithHandler(handler).GraphService;

        var results = await service.MarkEmailsAsReadAsync("nobody@example.com", null, true, false, null);

        Assert.Empty(results);
    }

    #endregion

    #region Helpers

    private static Mock<AuthService> CreateMockAuthService(bool isAuthenticated)
    {
        var mock = new Mock<AuthService>(Mock.Of<ILogger<AuthService>>());
        mock.SetupGet(a => a.IsAuthenticated).Returns(isAuthenticated);
        return mock;
    }

    private static GraphEmailService CreateGraphService(AuthService authService)
    {
        return new GraphEmailService(authService, Mock.Of<ILogger<GraphEmailService>>());
    }

    private static (Mock<AuthService> AuthService, GraphEmailService GraphService) CreateServicesWithHandler(MockGraphHandler handler)
    {
        var mockAuth = new Mock<AuthService>(Mock.Of<ILogger<AuthService>>());
        mockAuth.SetupGet(a => a.IsAuthenticated).Returns(true);
        mockAuth.Setup(a => a.CreateAuthenticatedHttpClient()).Returns(handler.CreateClient());
        var graphService = new GraphEmailService(mockAuth.Object, Mock.Of<ILogger<GraphEmailService>>());
        return (mockAuth, graphService);
    }

    private static (Mock<AuthService> AuthService, GraphEmailService GraphService) CreateServiceWithHandler(MockGraphHandler handler)
        => CreateServicesWithHandler(handler);

    #endregion
}

internal class MockGraphHandler : HttpMessageHandler
{
    private readonly List<JsonElement> _messages = new();
    private string? _deletedFolderId;
    private string? _junkFolderId;
    private readonly HashSet<string> _failPatchIds = new();

    public List<HttpRequestMessage> Requests { get; } = new();

    public void AddMessage(string id, string senderEmail, string subject, bool isRead, string parentFolderId = "inbox-id")
    {
        var json = $$"""
        {
            "id": "{{id}}",
            "subject": "{{subject}}",
            "from": { "emailAddress": { "name": "Sender", "address": "{{senderEmail}}" } },
            "toRecipients": [{ "emailAddress": { "address": "me@test.com" } }],
            "receivedDateTime": "{{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}}",
            "isRead": {{isRead.ToString().ToLowerInvariant()}},
            "parentFolderId": "{{parentFolderId}}"
        }
        """;
        _messages.Add(JsonSerializer.Deserialize<JsonElement>(json));
    }

    public void SetDeletedFolderId(string id) => _deletedFolderId = id;
    public void SetJunkFolderId(string id) => _junkFolderId = id;
    public void FailPatchForMessage(string id) => _failPatchIds.Add(id);

    public HttpClient CreateClient() => new(this);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        var url = request.RequestUri?.ToString() ?? "";

        // Mail folders request
        if (url.Contains("mailFolders"))
        {
            string? folderId = null;
            if (url.Contains("Deleted") && _deletedFolderId != null)
                folderId = _deletedFolderId;
            else if (url.Contains("Junk") && _junkFolderId != null)
                folderId = _junkFolderId;

            var body = folderId != null
                ? $$"""{ "value": [{ "id": "{{folderId}}" }] }"""
                : """{ "value": [] }""";

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }

        // PATCH mark as read
        if (request.Method == HttpMethod.Patch && url.Contains("/me/messages/"))
        {
            var msgId = url.Split("/me/messages/").Last();
            if (_failPatchIds.Contains(msgId))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$"""{ "id": "{{msgId}}", "isRead": true }""", Encoding.UTF8, "application/json")
            });
        }

        // GET messages
        if (url.Contains("/me/messages"))
        {
            var messagesJson = JsonSerializer.Serialize(_messages);
            var body = $$"""{ "value": {{messagesJson}} }""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
