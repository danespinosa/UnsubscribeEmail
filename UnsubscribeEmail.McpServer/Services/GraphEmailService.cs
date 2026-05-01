using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using UnsubscribeEmail.McpServer.Models;

namespace UnsubscribeEmail.McpServer.Services;

/// <summary>
/// Fetches and aggregates emails from Microsoft Graph API.
/// </summary>
public class GraphEmailService
{
    private readonly AuthService _authService;
    private readonly ILogger<GraphEmailService> _logger;

    private static readonly Regex EmailRegex = new(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.Compiled);

    public GraphEmailService(AuthService authService, ILogger<GraphEmailService> logger)
    {
        _authService = authService;
        _logger = logger;
    }

    public async Task<List<SenderEmailInfo>> GetEmailsAggregatedBySenderAsync(int daysBack)
    {
        var emails = await FetchEmailsAsync(daysBack, includeBody: true);

        var grouped = emails
            .GroupBy(e => e.SenderEmail.ToLowerInvariant())
            .Select(g =>
            {
                var mostRecent = g.OrderByDescending(e => e.ReceivedDateTime).First();
                return new SenderEmailInfo
                {
                    SenderName = mostRecent.SenderName,
                    SenderEmail = g.Key,
                    RecipientEmail = mostRecent.RecipientEmail,
                    EmailCount = g.Count(),
                    UnreadCount = g.Count(e => !e.IsRead),
                    LastEmailDate = g.Max(e => e.ReceivedDateTime),
                    SampleEmailHtmlBody = mostRecent.Body
                };
            })
            .OrderByDescending(s => s.EmailCount)
            .ToList();

        return grouped;
    }

    public async Task<List<EmailMessage>> GetEmailsFromSenderAsync(string senderEmail, int maxEmails = 1, int? daysBack = null)
    {
        if (!EmailRegex.IsMatch(senderEmail))
            throw new ArgumentException($"Invalid email format: {senderEmail}", nameof(senderEmail));

        var httpClient = _authService.CreateAuthenticatedHttpClient();

        var deletedItemsFolderId = await GetFolderIdAsync(httpClient, "Deleted Items");
        var junkEmailFolderId = await GetFolderIdAsync(httpClient, "Junk Email");

        var filter = $"from/emailAddress/address eq '{senderEmail}'";
        if (daysBack.HasValue)
        {
            var startDate = DateTime.UtcNow.AddDays(-daysBack.Value).ToString("yyyy-MM-ddTHH:mm:ssZ");
            filter += $" and receivedDateTime ge {startDate}";
        }

        // Note: $orderby cannot be combined with $filter on from/emailAddress/address (Graph returns 400).
        // Fetch all matching emails across pages, then sort and take client-side.
        string? url = $"https://graph.microsoft.com/v1.0/me/messages?$filter={Uri.EscapeDataString(filter)}&$select=id,subject,body,from,toRecipients,receivedDateTime,isRead,parentFolderId&$top=50";

        var emails = new List<EmailMessage>();

        while (!string.IsNullOrEmpty(url))
        {
            var response = await httpClient.GetAsync(url);
            response.EnsureSuccessStatusCode();

            var content = await response.Content.ReadAsStringAsync();
            var data = JsonSerializer.Deserialize<JsonElement>(content);

            if (data.TryGetProperty("value", out var messagesArray))
            {
                foreach (var message in messagesArray.EnumerateArray())
                {
                    if (IsEmailInExcludedFolder(message, deletedItemsFolderId, junkEmailFolderId))
                        continue;

                    emails.Add(ParseEmailMessage(message, includeBody: true));
                }
            }

            url = data.TryGetProperty("@odata.nextLink", out var nextLink)
                ? nextLink.GetString()
                : null;
        }

        return emails
            .OrderByDescending(e => e.ReceivedDateTime)
            .Take(maxEmails)
            .ToList();
    }

    private async Task<List<EmailMessage>> FetchEmailsAsync(int daysBack, bool includeBody)
    {
        var emails = new List<EmailMessage>();
        var httpClient = _authService.CreateAuthenticatedHttpClient();

        var deletedItemsFolderId = await GetFolderIdAsync(httpClient, "Deleted Items");
        var junkEmailFolderId = await GetFolderIdAsync(httpClient, "Junk Email");

        var startDate = DateTime.UtcNow.AddDays(-daysBack).ToString("yyyy-MM-ddTHH:mm:ssZ");
        var filter = $"receivedDateTime ge {startDate} and isDraft eq false";
        var select = includeBody
            ? "id,subject,body,from,toRecipients,receivedDateTime,isRead,parentFolderId"
            : "id,subject,from,toRecipients,receivedDateTime,isRead,parentFolderId";

        var url = $"https://graph.microsoft.com/v1.0/me/messages?$filter={Uri.EscapeDataString(filter)}&$select={select}&$top=100&$orderby=receivedDateTime desc";

        var pageCount = 0;

        while (!string.IsNullOrEmpty(url))
        {
            pageCount++;
            _logger.LogInformation("Fetching page {Page} from Graph API", pageCount);

            var response = await httpClient.GetAsync(url);
            response.EnsureSuccessStatusCode();

            var content = await response.Content.ReadAsStringAsync();
            var data = JsonSerializer.Deserialize<JsonElement>(content);

            if (data.TryGetProperty("value", out var messagesArray))
            {
                foreach (var message in messagesArray.EnumerateArray())
                {
                    if (IsEmailInExcludedFolder(message, deletedItemsFolderId, junkEmailFolderId))
                        continue;

                    emails.Add(ParseEmailMessage(message, includeBody));
                }
            }

            url = data.TryGetProperty("@odata.nextLink", out var nextLink)
                ? nextLink.GetString() ?? ""
                : "";

            if (string.IsNullOrEmpty(url)) break;
        }

        _logger.LogInformation("Fetched {Count} emails from last {Days} days across {Pages} pages", emails.Count, daysBack, pageCount);
        return emails;
    }

    private static EmailMessage ParseEmailMessage(JsonElement message, bool includeBody)
    {
        var email = new EmailMessage
        {
            Id = message.GetProperty("id").GetString() ?? "",
            Subject = message.TryGetProperty("subject", out var subject) ? subject.GetString() ?? "" : "",
            ReceivedDateTime = message.TryGetProperty("receivedDateTime", out var dt) ? dt.GetDateTime() : DateTime.MinValue,
            IsRead = message.TryGetProperty("isRead", out var isRead) && isRead.GetBoolean()
        };

        if (message.TryGetProperty("from", out var from) &&
            from.TryGetProperty("emailAddress", out var emailAddr))
        {
            email.SenderName = emailAddr.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "";
            email.SenderEmail = emailAddr.TryGetProperty("address", out var addr) ? addr.GetString() ?? "" : "";
        }

        if (message.TryGetProperty("toRecipients", out var toRecipients) && toRecipients.GetArrayLength() > 0)
        {
            var first = toRecipients[0];
            if (first.TryGetProperty("emailAddress", out var recipientAddr))
            {
                email.RecipientEmail = recipientAddr.TryGetProperty("address", out var addr) ? addr.GetString() ?? "" : "";
            }
        }

        if (includeBody && message.TryGetProperty("body", out var bodyObj) &&
            bodyObj.TryGetProperty("content", out var bodyContent))
        {
            email.Body = bodyContent.GetString() ?? "";
        }

        return email;
    }

    private async Task<string?> GetFolderIdAsync(HttpClient httpClient, string folderName)
    {
        try
        {
            var response = await httpClient.GetAsync($"https://graph.microsoft.com/v1.0/me/mailFolders?$filter=displayName eq '{folderName}'&$select=id");
            response.EnsureSuccessStatusCode();
            var content = await response.Content.ReadAsStringAsync();
            var data = JsonSerializer.Deserialize<JsonElement>(content);

            if (data.TryGetProperty("value", out var folderValue) && folderValue.GetArrayLength() > 0)
                return folderValue[0].GetProperty("id").GetString();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get folder ID for '{FolderName}'", folderName);
        }

        return null;
    }

    public async Task<int> MarkEmailsAsReadAsync(string senderEmail, int? daysBack = null)
    {
        if (!EmailRegex.IsMatch(senderEmail))
            throw new ArgumentException($"Invalid email format: {senderEmail}", nameof(senderEmail));

        var ids = await GetEmailIdsBySenderAsync(senderEmail, daysBack, unreadOnly: true);
        var httpClient = _authService.CreateAuthenticatedHttpClient();
        var marked = 0;

        foreach (var id in ids)
        {
            var request = new HttpRequestMessage(HttpMethod.Patch,
                $"https://graph.microsoft.com/v1.0/me/messages/{id}");
            request.Content = new StringContent(
                "{\"isRead\":true}",
                System.Text.Encoding.UTF8,
                "application/json");

            var response = await httpClient.SendAsync(request);
            if (response.IsSuccessStatusCode) marked++;
        }

        _logger.LogInformation("Marked {Count}/{Total} emails from {Sender} as read", marked, ids.Count, senderEmail);
        return marked;
    }

    public async Task<int> DeleteEmailsAsync(string senderEmail, int? daysBack = null)
    {
        if (!EmailRegex.IsMatch(senderEmail))
            throw new ArgumentException($"Invalid email format: {senderEmail}", nameof(senderEmail));

        var ids = await GetEmailIdsBySenderAsync(senderEmail, daysBack);
        var httpClient = _authService.CreateAuthenticatedHttpClient();
        var deleted = 0;

        foreach (var id in ids)
        {
            var response = await httpClient.DeleteAsync(
                $"https://graph.microsoft.com/v1.0/me/messages/{id}");
            if (response.IsSuccessStatusCode) deleted++;
        }

        _logger.LogInformation("Deleted {Count}/{Total} emails from {Sender}", deleted, ids.Count, senderEmail);
        return deleted;
    }

    private async Task<List<string>> GetEmailIdsBySenderAsync(string senderEmail, int? daysBack, bool unreadOnly = false)
    {
        var httpClient = _authService.CreateAuthenticatedHttpClient();

        var filter = $"from/emailAddress/address eq '{senderEmail}'";
        if (daysBack.HasValue)
        {
            var startDate = DateTime.UtcNow.AddDays(-daysBack.Value).ToString("yyyy-MM-ddTHH:mm:ssZ");
            filter += $" and receivedDateTime ge {startDate}";
        }
        if (unreadOnly)
        {
            filter += " and isRead eq false";
        }

        string? url = $"https://graph.microsoft.com/v1.0/me/messages?$filter={Uri.EscapeDataString(filter)}&$select=id&$top=50";
        var ids = new List<string>();

        while (!string.IsNullOrEmpty(url))
        {
            var response = await httpClient.GetAsync(url);
            response.EnsureSuccessStatusCode();

            var content = await response.Content.ReadAsStringAsync();
            var data = JsonSerializer.Deserialize<JsonElement>(content);

            if (data.TryGetProperty("value", out var messagesArray))
            {
                foreach (var message in messagesArray.EnumerateArray())
                {
                    var id = message.GetProperty("id").GetString();
                    if (id != null) ids.Add(id);
                }
            }

            url = data.TryGetProperty("@odata.nextLink", out var nextLink)
                ? nextLink.GetString()
                : null;
        }

        return ids;
    }

    private static bool IsEmailInExcludedFolder(JsonElement message, string? deletedItemsFolderId, string? junkEmailFolderId)
    {
        if (!message.TryGetProperty("parentFolderId", out var parentFolderId))
            return false;

        var parentFolder = parentFolderId.GetString();
        return (!string.IsNullOrEmpty(deletedItemsFolderId) && parentFolder == deletedItemsFolderId) ||
               (!string.IsNullOrEmpty(junkEmailFolderId) && parentFolder == junkEmailFolderId);
    }
}
