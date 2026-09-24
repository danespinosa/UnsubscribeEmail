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
    private const string GraphApiBaseUrl = "https://graph.microsoft.com/v1.0";
    private const string AttachmentSelect = "id,name,contentType,size,isInline,lastModifiedDateTime";
    private const int DefaultMaxAttachments = 100;
    private const int MaxAllowedAttachments = 500;
    private const int DefaultMaxAttachmentBytes = 4_000_000;
    private const int MaxAllowedAttachmentBytes = 10_000_000;

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
                    SampleEmailHtmlBody = mostRecent.Body,
                    SampleMessageId = mostRecent.Id
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

    public async Task<EmailAttachmentListResult> GetEmailAttachmentsAsync(
        string messageId,
        int maxAttachments = DefaultMaxAttachments)
    {
        ValidateIdentifier(messageId, nameof(messageId));
        maxAttachments = Math.Clamp(maxAttachments, 1, MaxAllowedAttachments);

        var httpClient = _authService.CreateAuthenticatedHttpClient();
        var attachments = new List<EmailAttachment>();
        var url = BuildMessageAttachmentsUrl(messageId) + $"?$select={AttachmentSelect}";
        var hasMore = false;

        while (!string.IsNullOrEmpty(url))
        {
            using var response = await httpClient.GetAsync(url);
            await EnsureGraphSuccessAsync(response);

            var content = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(content);
            var data = document.RootElement;

            if (data.TryGetProperty("value", out var attachmentsArray) &&
                attachmentsArray.ValueKind == JsonValueKind.Array)
            {
                var pageHasUnprocessedItems = false;
                foreach (var attachment in attachmentsArray.EnumerateArray())
                {
                    if (attachments.Count >= maxAttachments)
                    {
                        pageHasUnprocessedItems = true;
                        break;
                    }

                    attachments.Add(ParseEmailAttachment(attachment, messageId));
                }

                var nextUrl = data.TryGetProperty("@odata.nextLink", out var pageNextLink)
                    ? pageNextLink.GetString() ?? string.Empty
                    : string.Empty;

                if (pageHasUnprocessedItems)
                {
                    hasMore = true;
                    break;
                }

                if (attachments.Count >= maxAttachments && !string.IsNullOrEmpty(nextUrl))
                {
                    hasMore = true;
                    break;
                }

                url = nextUrl;
                continue;
            }

            url = data.TryGetProperty("@odata.nextLink", out var nextLinkProperty)
                ? nextLinkProperty.GetString() ?? string.Empty
                : string.Empty;
        }

        return new EmailAttachmentListResult
        {
            MessageId = messageId,
            TotalAttachments = attachments.Count,
            HasMore = hasMore,
            Attachments = attachments
        };
    }

    public async Task<EmailAttachmentDownload> DownloadEmailAttachmentAsync(
        string messageId,
        string attachmentId,
        int maxBytes = DefaultMaxAttachmentBytes)
    {
        ValidateIdentifier(messageId, nameof(messageId));
        ValidateIdentifier(attachmentId, nameof(attachmentId));
        maxBytes = Math.Clamp(maxBytes, 1, MaxAllowedAttachmentBytes);

        var httpClient = _authService.CreateAuthenticatedHttpClient();
        var attachment = await GetEmailAttachmentAsync(httpClient, messageId, attachmentId);

        if (attachment.AttachmentType == "reference")
        {
            throw new InvalidOperationException(
                $"Attachment '{attachment.AttachmentId}' on message '{attachment.MessageId}' " +
                "is a reference attachment and cannot be downloaded because it has no message content.");
        }

        if (attachment.AttachmentType is not ("file" or "item"))
        {
            throw new InvalidOperationException(
                $"Attachment '{attachment.AttachmentId}' on message '{attachment.MessageId}' " +
                $"has unsupported type '{attachment.AttachmentType}' and cannot be downloaded.");
        }

        if (attachment.Size.HasValue && attachment.Size.Value > maxBytes)
        {
            throw new InvalidOperationException(
                $"Attachment '{attachment.AttachmentId}' is {attachment.Size.Value} bytes, " +
                $"which exceeds the maxBytes limit of {maxBytes}.");
        }

        var downloadUrl = BuildAttachmentUrl(messageId, attachmentId) + "/$value";
        using var response = await httpClient.GetAsync(
            downloadUrl,
            HttpCompletionOption.ResponseHeadersRead);
        await EnsureGraphSuccessAsync(response);

        if (response.Content.Headers.ContentLength > maxBytes)
        {
            throw new InvalidOperationException(
                $"Attachment '{attachment.AttachmentId}' content exceeds the maxBytes limit of {maxBytes}.");
        }

        var bytes = await ReadBoundedContentAsync(response.Content, maxBytes);
        return new EmailAttachmentDownload
        {
            Attachment = attachment,
            ContentType = response.Content.Headers.ContentType?.MediaType ?? attachment.ContentType,
            DownloadedSize = bytes.LongLength,
            Base64Content = Convert.ToBase64String(bytes)
        };
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

    private async Task<EmailAttachment> GetEmailAttachmentAsync(
        HttpClient httpClient,
        string messageId,
        string attachmentId)
    {
        var url = BuildAttachmentUrl(messageId, attachmentId) + $"?$select={AttachmentSelect}";
        using var response = await httpClient.GetAsync(url);
        await EnsureGraphSuccessAsync(response);

        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);
        var attachment = ParseEmailAttachment(document.RootElement, messageId);

        if (!string.Equals(attachment.AttachmentId, attachmentId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Graph returned attachment ID '{attachment.AttachmentId}' instead of the requested attachment ID '{attachmentId}'.");
        }

        return attachment;
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

    private static EmailAttachment ParseEmailAttachment(JsonElement attachment, string messageId)
    {
        var attachmentId = GetStringProperty(attachment, "id");
        if (string.IsNullOrEmpty(attachmentId))
            throw new InvalidOperationException("Graph attachment response did not contain an attachment ID.");

        var item = attachment.TryGetProperty("item", out var itemValue) &&
                   itemValue.ValueKind == JsonValueKind.Object
            ? itemValue
            : (JsonElement?)null;
        var sourceUrl = GetStringProperty(attachment, "sourceUrl");
        var odataType = GetStringProperty(attachment, "@odata.type");
        var attachmentType = GetAttachmentType(odataType, attachment, item, sourceUrl);

        return new EmailAttachment
        {
            MessageId = messageId,
            AttachmentId = attachmentId,
            Name = GetStringProperty(attachment, "name"),
            ContentType = GetStringProperty(attachment, "contentType"),
            Size = GetLongProperty(attachment, "size"),
            IsInline = GetBoolProperty(attachment, "isInline"),
            LastModifiedDateTime = GetStringProperty(attachment, "lastModifiedDateTime"),
            AttachmentType = attachmentType,
            DownloadSupported = attachmentType is "file" or "item",
        };
    }

    private static string GetAttachmentType(
        string? odataType,
        JsonElement attachment,
        JsonElement? item,
        string? sourceUrl)
    {
        if (odataType?.EndsWith("fileAttachment", StringComparison.OrdinalIgnoreCase) == true)
            return "file";
        if (odataType?.EndsWith("itemAttachment", StringComparison.OrdinalIgnoreCase) == true)
            return "item";
        if (odataType?.EndsWith("referenceAttachment", StringComparison.OrdinalIgnoreCase) == true)
            return "reference";
        if (item.HasValue)
            return "item";
        if (!string.IsNullOrEmpty(sourceUrl))
            return "reference";
        if ((attachment.TryGetProperty("contentBytes", out var contentBytes) &&
             contentBytes.ValueKind != JsonValueKind.Null &&
             contentBytes.ValueKind != JsonValueKind.Undefined) ||
            !string.IsNullOrEmpty(GetStringProperty(attachment, "contentId")))
            return "file";

        return "unknown";
    }

    private static string? GetStringProperty(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) &&
               value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static long? GetLongProperty(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) ||
            value.ValueKind == JsonValueKind.Null ||
            value.ValueKind == JsonValueKind.Undefined)
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
            return number;

        return value.ValueKind == JsonValueKind.String &&
               long.TryParse(value.GetString(), out var parsed)
            ? parsed
            : null;
    }

    private static bool? GetBoolProperty(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) ||
            value.ValueKind == JsonValueKind.Null ||
            value.ValueKind == JsonValueKind.Undefined)
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.True
            ? true
            : value.ValueKind == JsonValueKind.False ? false : null;
    }

    private static string BuildMessageAttachmentsUrl(string messageId)
        => $"{GraphApiBaseUrl}/me/messages/{Uri.EscapeDataString(messageId)}/attachments";

    private static string BuildAttachmentUrl(string messageId, string attachmentId)
        => $"{BuildMessageAttachmentsUrl(messageId)}/{Uri.EscapeDataString(attachmentId)}";

    private static void ValidateIdentifier(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{parameterName} is required.", parameterName);
    }

    private static async Task<byte[]> ReadBoundedContentAsync(HttpContent content, int maxBytes)
    {
        await using var responseStream = await content.ReadAsStreamAsync();
        await using var memoryStream = new MemoryStream();
        var buffer = new byte[81920];
        var totalBytes = 0;

        int bytesRead;
        while ((bytesRead = await responseStream.ReadAsync(buffer)) > 0)
        {
            if (bytesRead > maxBytes - totalBytes)
            {
                throw new InvalidOperationException(
                    $"Attachment content exceeds the maxBytes limit of {maxBytes}.");
            }

            await memoryStream.WriteAsync(buffer.AsMemory(0, bytesRead));
            totalBytes += bytesRead;
        }

        return memoryStream.ToArray();
    }

    private static async Task EnsureGraphSuccessAsync(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
            return;

        var errorBody = await response.Content.ReadAsStringAsync();
        throw new HttpRequestException(
            $"Graph API returned {(int)response.StatusCode}: {errorBody}");
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
