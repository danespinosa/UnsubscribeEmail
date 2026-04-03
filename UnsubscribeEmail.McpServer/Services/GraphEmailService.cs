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

        var url = $"https://graph.microsoft.com/v1.0/me/messages?$filter={Uri.EscapeDataString(filter)}&$select=id,subject,body,from,toRecipients,receivedDateTime,isRead,parentFolderId&$top={Math.Min(maxEmails * 2, 50)}&$orderby=receivedDateTime desc";

        var emails = new List<EmailMessage>();

        var response = await httpClient.GetAsync(url);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync();
        var data = JsonSerializer.Deserialize<JsonElement>(content);

        if (data.TryGetProperty("value", out var messagesArray))
        {
            foreach (var message in messagesArray.EnumerateArray())
            {
                if (emails.Count >= maxEmails) break;

                if (IsEmailInExcludedFolder(message, deletedItemsFolderId, junkEmailFolderId))
                    continue;

                emails.Add(ParseEmailMessage(message, includeBody: true));
            }
        }

        return emails;
    }

    public async Task<List<MarkAsReadResult>> MarkEmailsAsReadAsync(
        string? senderEmail,
        string? senderDomain,
        bool unreadOnly,
        bool dryRun,
        int? daysBack)
    {
        var httpClient = _authService.CreateAuthenticatedHttpClient();

        var deletedItemsFolderId = await GetFolderIdAsync(httpClient, "Deleted Items");
        var junkEmailFolderId = await GetFolderIdAsync(httpClient, "Junk Email");

        // Build OData filter — keep it minimal to avoid 400s on personal accounts.
        // Filter on sender + date server-side; isDraft / isRead are checked client-side.
        var filter = !string.IsNullOrEmpty(senderEmail)
            ? $"from/emailAddress/address eq '{senderEmail}'"
            : "isDraft eq false";

        if (daysBack.HasValue)
        {
            var startDate = DateTime.UtcNow.AddDays(-daysBack.Value).ToString("yyyy-MM-ddTHH:mm:ssZ");
            filter += $" and receivedDateTime ge {startDate}";
        }
        var select = "id,subject,from,receivedDateTime,isRead,parentFolderId";
        var url = $"https://graph.microsoft.com/v1.0/me/messages?$filter={Uri.EscapeDataString(filter)}&$select={select}&$top=100";

        var results = new List<MarkAsReadResult>();
        var pageCount = 0;

        while (!string.IsNullOrEmpty(url))
        {
            pageCount++;
            _logger.LogInformation("Fetching page {Page} for mark-as-read", pageCount);

            var response = await httpClient.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                throw new HttpRequestException(
                    $"Graph API returned {(int)response.StatusCode}: {errorBody}");
            }

            var content = await response.Content.ReadAsStringAsync();
            var data = JsonSerializer.Deserialize<JsonElement>(content);

            if (data.TryGetProperty("value", out var messagesArray))
            {
                foreach (var message in messagesArray.EnumerateArray())
                {
                    if (IsEmailInExcludedFolder(message, deletedItemsFolderId, junkEmailFolderId))
                        continue;

                    var email = ParseEmailMessage(message, includeBody: false);

                    // Client-side domain filter
                    if (!string.IsNullOrEmpty(senderDomain))
                    {
                        var emailDomain = email.SenderEmail.Split('@').LastOrDefault() ?? "";
                        if (!emailDomain.Equals(senderDomain, StringComparison.OrdinalIgnoreCase))
                            continue;
                    }

                    // Client-side unreadOnly filter
                    if (unreadOnly && email.IsRead)
                        continue;

                    var result = new MarkAsReadResult
                    {
                        MessageId = email.Id,
                        Subject = email.Subject,
                        SenderEmail = email.SenderEmail,
                        ReceivedDateTime = email.ReceivedDateTime,
                        WasAlreadyRead = email.IsRead
                    };

                    if (email.IsRead)
                    {
                        // Already read — nothing to do
                        result.MarkedAsRead = false;
                    }
                    else if (dryRun)
                    {
                        result.MarkedAsRead = false;
                    }
                    else
                    {
                        try
                        {
                            var patchUrl = $"https://graph.microsoft.com/v1.0/me/messages/{email.Id}";
                            var patchContent = new StringContent(
                                "{\"isRead\":true}",
                                System.Text.Encoding.UTF8,
                                "application/json");
                            var patchResponse = await httpClient.PatchAsync(patchUrl, patchContent);
                            patchResponse.EnsureSuccessStatusCode();
                            result.MarkedAsRead = true;
                        }
                        catch (Exception ex)
                        {
                            result.MarkedAsRead = false;
                            result.Error = ex.Message;
                            _logger.LogWarning(ex, "Failed to mark message {MessageId} as read", email.Id);
                        }
                    }

                    results.Add(result);
                }
            }

            url = data.TryGetProperty("@odata.nextLink", out var nextLink)
                ? nextLink.GetString() ?? ""
                : "";

            if (string.IsNullOrEmpty(url)) break;
        }

        _logger.LogInformation(
            "Mark-as-read completed: {Total} matched, {Marked} marked, dryRun={DryRun}",
            results.Count, results.Count(r => r.MarkedAsRead), dryRun);

        return results;
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

    private static bool IsEmailInExcludedFolder(JsonElement message, string? deletedItemsFolderId, string? junkEmailFolderId)
    {
        if (!message.TryGetProperty("parentFolderId", out var parentFolderId))
            return false;

        var parentFolder = parentFolderId.GetString();
        return (!string.IsNullOrEmpty(deletedItemsFolderId) && parentFolder == deletedItemsFolderId) ||
               (!string.IsNullOrEmpty(junkEmailFolderId) && parentFolder == junkEmailFolderId);
    }
}
