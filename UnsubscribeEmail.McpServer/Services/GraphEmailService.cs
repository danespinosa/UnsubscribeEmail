using System.Net;
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
    private const string AttachmentSelect =
        "id,name,contentType,size,isInline,lastModifiedDateTime,microsoft.graph.fileAttachment/contentId";
    private const int DefaultMaxAttachments = 100;
    private const int MaxAllowedAttachments = 500;
    private const int DefaultMaxAttachmentBytes = 4_000_000;
    private const int MaxAllowedAttachmentBytes = 10_000_000;
    private const int AttachmentRetryCount = 3;
    private const int MessagePageSize = 100;
    private const int MaxSearchCandidateScan = 100;

    private readonly AuthService _authService;
    private readonly ILogger<GraphEmailService> _logger;
    private readonly IGraphRetryDelay _retryDelay;
    private readonly IGraphRetryJitter _retryJitter;
    private readonly TimeProvider _timeProvider;

    private static readonly Regex EmailRegex = new(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.Compiled);

    public GraphEmailService(
        AuthService authService,
        ILogger<GraphEmailService> logger,
        IGraphRetryDelay? retryDelay = null,
        TimeProvider? timeProvider = null,
        IGraphRetryJitter? retryJitter = null)
    {
        _authService = authService;
        _logger = logger;
        _retryDelay = retryDelay ?? new GraphRetryDelay();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _retryJitter = retryJitter ?? new GraphRetryJitter();
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

        maxEmails = Math.Clamp(maxEmails, 1, 10);
        var httpClient = _authService.CreateAuthenticatedHttpClient();

        var deletedItemsFolderId = await GetFolderIdAsync(httpClient, "Deleted Items");
        var junkEmailFolderId = await GetFolderIdAsync(httpClient, "Junk Email");

        // Outlook.com rejects this sender filter when it is combined with $orderby.
        // Use $search for the server-side candidate set, then apply sender/date
        // filtering and newest-first ordering locally.
        var search = $"\"from:{EscapeGraphSearchValue(senderEmail)}\"";
        var url = BuildMessagesSearchUrl(
            search,
            "id,subject,body,from,toRecipients,receivedDateTime,isRead,parentFolderId",
            MessagePageSize);
        var emails = new List<EmailMessage>();
        var pageCount = 0;
        var startDate = daysBack.HasValue
            ? DateTime.UtcNow.AddDays(-daysBack.Value)
            : (DateTime?)null;

        while (!string.IsNullOrEmpty(url))
        {
            pageCount++;
            _logger.LogInformation("Fetching page {Page} for sender content", pageCount);

            using var response = await httpClient.GetAsync(url);
            await EnsureGraphSuccessAsync(response);

            var content = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(content);
            var data = document.RootElement;

            if (data.TryGetProperty("value", out var messagesArray) &&
                messagesArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var message in messagesArray.EnumerateArray())
                {
                    if (IsEmailInExcludedFolder(message, deletedItemsFolderId, junkEmailFolderId))
                        continue;

                    var email = ParseEmailMessage(message, includeBody: true);
                    if (!email.SenderEmail.Equals(senderEmail, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (startDate.HasValue && email.ReceivedDateTime < startDate.Value)
                        continue;

                    emails.Add(email);
                }
            }

            url = GetNextLink(data);
        }

        return emails
            .OrderByDescending(email => email.ReceivedDateTime)
            .Take(maxEmails)
            .ToList();
    }

    public async Task<EmailSearchPage> SearchEmailsPageAsync(
        string? senderEmail,
        string? senderDomain,
        string? subjectContains,
        int maxEmails = 50,
        int daysBack = 30,
        CancellationToken cancellationToken = default)
    {
        maxEmails = Math.Clamp(maxEmails, 1, 50);
        daysBack = Math.Clamp(daysBack, 1, 730);

        var httpClient = _authService.CreateAuthenticatedHttpClient();
        var deletedItemsFolderId = await GetFolderIdAsync(httpClient, "Deleted Items");
        var junkEmailFolderId = await GetFolderIdAsync(httpClient, "Junk Email");
        var startDate = DateTime.UtcNow.AddDays(-daysBack).ToString("yyyy-MM-ddTHH:mm:ssZ");
        var filter = $"receivedDateTime ge {startDate} and isDraft eq false";
        var url = BuildMessagesUrl(
            filter,
            "id,subject,from,receivedDateTime,isRead,hasAttachments,parentFolderId",
            MessagePageSize,
            orderByReceivedDateDescending: true);
        var results = new List<EmailSearchResult>();
        var hasMore = false;
        var candidateScanCount = 0;

        while (!string.IsNullOrEmpty(url) &&
               results.Count < maxEmails &&
               candidateScanCount < MaxSearchCandidateScan)
        {
            using var response = await httpClient.GetAsync(url, cancellationToken);
            await EnsureGraphSuccessAsync(response);

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(content);
            var data = document.RootElement;

            if (data.TryGetProperty("value", out var messagesArray) &&
                messagesArray.ValueKind == JsonValueKind.Array)
            {
                var pageHasUnprocessedCandidates = false;
                for (var messageIndex = 0; messageIndex < messagesArray.GetArrayLength(); messageIndex++)
                {
                    if (results.Count >= maxEmails)
                    {
                        hasMore = true;
                        break;
                    }

                    if (candidateScanCount >= MaxSearchCandidateScan)
                    {
                        pageHasUnprocessedCandidates = true;
                        break;
                    }

                    candidateScanCount++;
                    var message = messagesArray[messageIndex];

                    if (IsEmailInExcludedFolder(message, deletedItemsFolderId, junkEmailFolderId))
                        continue;

                    var sender = GetSenderEmail(message);
                    if (!string.IsNullOrWhiteSpace(senderEmail) &&
                        !sender.Equals(senderEmail, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (!string.IsNullOrWhiteSpace(senderDomain) &&
                        !sender.EndsWith($"@{senderDomain.TrimStart('@')}", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var subject = GetStringProperty(message, "subject") ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(subjectContains) &&
                        !subject.Contains(subjectContains, StringComparison.OrdinalIgnoreCase))
                        continue;

                    results.Add(new EmailSearchResult
                    {
                        MessageId = GetRequiredStringProperty(message, "id", "message ID"),
                        Subject = subject,
                        ReceivedDateTime = GetStringProperty(message, "receivedDateTime") ?? string.Empty,
                        Sender = sender,
                        IsRead = GetBoolProperty(message, "isRead") ?? false,
                        HasAttachments = GetBoolProperty(message, "hasAttachments") ?? false
                    });

                    if (results.Count >= maxEmails)
                    {
                        hasMore = messageIndex + 1 < messagesArray.GetArrayLength();
                        break;
                    }
                }

                hasMore |= pageHasUnprocessedCandidates;
            }

            var nextUrl = GetNextLink(data);
            hasMore |= !string.IsNullOrEmpty(nextUrl);
            // Keep discovery bounded to one Graph page (at most 100 candidates).
            // A next link is reported rather than followed so selective local
            // filters cannot turn this read-only tool into an unbounded scan.
            break;
        }

        return new EmailSearchPage
        {
            Messages = results,
            HasMore = hasMore
        };
    }

    public async Task<List<EmailSearchResult>> SearchEmailsAsync(
        string? senderEmail,
        string? senderDomain,
        string? subjectContains,
        int maxEmails = 50,
        int daysBack = 30,
        CancellationToken cancellationToken = default)
        => (await SearchEmailsPageAsync(
                senderEmail,
                senderDomain,
                subjectContains,
                maxEmails,
                daysBack,
                cancellationToken))
            .Messages
            .ToList();

    public async Task<List<MarkAsReadResult>> MarkEmailsAsReadAsync(
        string? senderEmail,
        string? senderDomain,
        bool unreadOnly,
        bool dryRun,
        int? daysBack,
        CancellationToken cancellationToken = default)
    {
        var httpClient = _authService.CreateAuthenticatedHttpClient();

        var deletedItemsFolderId = await GetFolderIdAsync(httpClient, "Deleted Items");
        var junkEmailFolderId = await GetFolderIdAsync(httpClient, "Junk Email");

        // Build OData filter — keep it minimal to avoid 400s on personal accounts.
        // Filter on sender + date server-side; isDraft / isRead are checked client-side.
        var filter = !string.IsNullOrEmpty(senderEmail)
            ? $"from/emailAddress/address eq '{EscapeODataString(senderEmail)}'"
            : "isDraft eq false";

        if (daysBack.HasValue)
        {
            var startDate = DateTime.UtcNow.AddDays(-daysBack.Value).ToString("yyyy-MM-ddTHH:mm:ssZ");
            filter += $" and receivedDateTime ge {startDate}";
        }
        var select = "id,subject,from,receivedDateTime,isRead,parentFolderId";
        var url = BuildMessagesUrl(filter, select, MessagePageSize);

        var results = new List<MarkAsReadResult>();
        var pageCount = 0;

        while (!string.IsNullOrEmpty(url))
        {
            pageCount++;
            _logger.LogInformation("Fetching page {Page} for mark-as-read", pageCount);

            using var response = await httpClient.GetAsync(url, cancellationToken);
            await EnsureGraphSuccessAsync(response);

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(content);
            var data = document.RootElement;

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
                            var patchUrl = BuildMessageUrl(email.Id);
                            var patchContent = new StringContent(
                                "{\"isRead\":true}",
                                System.Text.Encoding.UTF8,
                                "application/json");
                            using var patchResponse = await httpClient.PatchAsync(
                                patchUrl,
                                patchContent,
                                cancellationToken);
                            await EnsureGraphSuccessAsync(patchResponse);
                            result.MarkedAsRead = true;
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            throw;
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

            url = GetNextLink(data);
        }

        _logger.LogInformation(
            "Mark-as-read completed: {Total} matched, {Marked} marked, dryRun={DryRun}",
            results.Count, results.Count(r => r.MarkedAsRead), dryRun);

        return results;
    }

    public async Task<EmailAttachmentListResult> GetEmailAttachmentsAsync(
        string messageId,
        int maxAttachments = DefaultMaxAttachments,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentifier(messageId, nameof(messageId));
        maxAttachments = Math.Clamp(maxAttachments, 1, MaxAllowedAttachments);

        await _authService.AttachmentOperationGate.WaitAsync(cancellationToken);
        try
        {
            var httpClient = _authService.CreateAuthenticatedHttpClient();
            var attachments = new List<EmailAttachment>();
            var url = BuildMessageAttachmentsUrl(messageId) + $"?$select={AttachmentSelect}";
            var hasMore = false;

            while (!string.IsNullOrEmpty(url))
            {
                using var response = await SendAttachmentGetWithRetryAsync(
                    httpClient,
                    url,
                    HttpCompletionOption.ResponseContentRead,
                    cancellationToken);
                var content = await response.Content.ReadAsStringAsync(cancellationToken);
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

                    var nextUrl = GetNextLink(data);

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

                url = GetNextLink(data);
            }

            return new EmailAttachmentListResult
            {
                MessageId = messageId,
                TotalAttachments = attachments.Count,
                HasMore = hasMore,
                Attachments = attachments
            };
        }
        finally
        {
            _authService.AttachmentOperationGate.Release();
        }
    }

    public async Task<EmailAttachmentDownload> DownloadEmailAttachmentAsync(
        string messageId,
        string attachmentId,
        int maxBytes = DefaultMaxAttachmentBytes,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentifier(messageId, nameof(messageId));
        ValidateIdentifier(attachmentId, nameof(attachmentId));
        maxBytes = Math.Clamp(maxBytes, 1, MaxAllowedAttachmentBytes);

        await _authService.AttachmentOperationGate.WaitAsync(cancellationToken);
        try
        {
            var httpClient = _authService.CreateAuthenticatedHttpClient();
            var attachment = await GetEmailAttachmentAsync(
                httpClient,
                messageId,
                attachmentId,
                cancellationToken);

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

            // Graph's metadata size can differ from the decoded bytes returned by /$value.
            // Enforce the bound against the actual response stream instead.
            var downloadUrl = BuildAttachmentUrl(messageId, attachmentId) + "/$value";
            using var response = await SendAttachmentGetWithRetryAsync(
                httpClient,
                downloadUrl,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            if (response.Content.Headers.ContentLength > maxBytes)
            {
                throw new InvalidOperationException(
                    $"Attachment '{attachment.AttachmentId}' content exceeds the maxBytes limit of {maxBytes}.");
            }

            var bytes = await ReadBoundedContentAsync(
                response.Content,
                maxBytes,
                cancellationToken);
            return new EmailAttachmentDownload
            {
                Attachment = attachment,
                ContentType = response.Content.Headers.ContentType?.MediaType ?? attachment.ContentType,
                DownloadedSize = bytes.LongLength,
                Base64Content = Convert.ToBase64String(bytes)
            };
        }
        finally
        {
            _authService.AttachmentOperationGate.Release();
        }
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

            using var response = await httpClient.GetAsync(url);
            await EnsureGraphSuccessAsync(response);

            var content = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(content);
            var data = document.RootElement;

            if (data.TryGetProperty("value", out var messagesArray))
            {
                foreach (var message in messagesArray.EnumerateArray())
                {
                    if (IsEmailInExcludedFolder(message, deletedItemsFolderId, junkEmailFolderId))
                        continue;

                    emails.Add(ParseEmailMessage(message, includeBody));
                }
            }

            url = GetNextLink(data);
        }

        _logger.LogInformation("Fetched {Count} emails from last {Days} days across {Pages} pages", emails.Count, daysBack, pageCount);
        return emails;
    }

    private async Task<EmailAttachment> GetEmailAttachmentAsync(
        HttpClient httpClient,
        string messageId,
        string attachmentId,
        CancellationToken cancellationToken)
    {
        var url = BuildAttachmentUrl(messageId, attachmentId) + $"?$select={AttachmentSelect}";
        using var response = await SendAttachmentGetWithRetryAsync(
            httpClient,
            url,
            HttpCompletionOption.ResponseContentRead,
            cancellationToken);

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
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
            Id = GetRequiredStringProperty(message, "id", "message ID"),
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

    private static string GetSenderEmail(JsonElement message)
    {
        return message.TryGetProperty("from", out var from) &&
               from.TryGetProperty("emailAddress", out var emailAddress)
            ? GetStringProperty(emailAddress, "address") ?? string.Empty
            : string.Empty;
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
            ContentId = GetStringProperty(attachment, "contentId"),
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

    private static string GetRequiredStringProperty(
        JsonElement element,
        string propertyName,
        string description)
    {
        var value = GetStringProperty(element, propertyName);
        return string.IsNullOrEmpty(value)
            ? throw new InvalidOperationException($"Graph response did not contain a {description}.")
            : value;
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

    private static string BuildMessagesUrl(
        string filter,
        string select,
        int top,
        bool orderByReceivedDateDescending = false)
    {
        var url =
            $"{GraphApiBaseUrl}/me/messages?$filter={Uri.EscapeDataString(filter)}" +
            $"&$select={select}&$top={top}";
        return orderByReceivedDateDescending
            ? $"{url}&$orderby=receivedDateTime desc"
            : url;
    }

    private static string BuildMessagesSearchUrl(string search, string select, int top)
        => $"{GraphApiBaseUrl}/me/messages?$search={Uri.EscapeDataString(search)}" +
           $"&$select={select}&$top={top}";

    private static string BuildMessageUrl(string messageId)
        => $"{GraphApiBaseUrl}/me/messages/{Uri.EscapeDataString(messageId)}";

    private static string BuildMessageAttachmentsUrl(string messageId)
        => $"{GraphApiBaseUrl}/me/messages/{Uri.EscapeDataString(messageId)}/attachments";

    private static string BuildAttachmentUrl(string messageId, string attachmentId)
        => $"{BuildMessageAttachmentsUrl(messageId)}/{Uri.EscapeDataString(attachmentId)}";

    private static void ValidateIdentifier(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{parameterName} is required.", parameterName);
    }

    private static string EscapeODataString(string value)
        => value.Replace("'", "''", StringComparison.Ordinal);

    private static string EscapeGraphSearchValue(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);

    private static string GetNextLink(JsonElement data)
        => data.TryGetProperty("@odata.nextLink", out var nextLink) &&
           nextLink.ValueKind == JsonValueKind.String
            ? nextLink.GetString() ?? string.Empty
            : string.Empty;

    private static async Task<byte[]> ReadBoundedContentAsync(
        HttpContent content,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        var expectedLength = content.Headers.ContentLength;
        await using var responseStream = await content.ReadAsStreamAsync(cancellationToken);
        await using var memoryStream = new MemoryStream();
        var buffer = new byte[81920];
        var totalBytes = 0;

        int bytesRead;
        while ((bytesRead = await responseStream.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
        {
            if (bytesRead > maxBytes - totalBytes)
            {
                throw new InvalidOperationException(
                    $"Attachment content exceeds the maxBytes limit of {maxBytes}.");
            }

            await memoryStream.WriteAsync(buffer.AsMemory(0, bytesRead));
            totalBytes += bytesRead;
        }

        if (expectedLength.HasValue && totalBytes != expectedLength.Value)
        {
            throw new InvalidOperationException(
                $"Attachment content was incomplete: received {totalBytes} bytes, " +
                $"but the response Content-Length declared {expectedLength.Value} bytes.");
        }

        return memoryStream.ToArray();
    }

    private async Task<HttpResponseMessage> SendAttachmentGetWithRetryAsync(
        HttpClient httpClient,
        string url,
        HttpCompletionOption completionOption,
        CancellationToken cancellationToken)
    {
        var retryCount = 0;
        var totalRetryDelay = TimeSpan.Zero;

        while (true)
        {
            var response = await httpClient.GetAsync(url, completionOption, cancellationToken);
            if (response.IsSuccessStatusCode)
                return response;

            var errorBody = await ReadGraphErrorBodyAsync(response, cancellationToken);
            var details = GraphApiException.ReadError(errorBody);
            var statusCode = response.StatusCode;
            var isRetryable = IsTransientStatusCode(statusCode);

            if (!isRetryable || retryCount >= AttachmentRetryCount)
            {
                var retryAfter = GetRetryAfter(response);
                response.Dispose();
                throw new GraphApiException(
                    statusCode,
                    details.Code,
                    details.Message ?? errorBody,
                    errorBody,
                    isRetryable,
                    retryCount,
                    totalRetryDelay,
                    details.RequestId,
                    details.ClientRequestId,
                    details.InnerCode,
                    retryAfter,
                    totalRetryDelay);
            }

            var delay = GetRetryDelay(response, retryCount);
            response.Dispose();
            retryCount++;
            totalRetryDelay += delay;
            _logger.LogWarning(
                "Transient Graph attachment request failure ({StatusCode}/{GraphCode}); retry {RetryCount} of {MaxRetries} after {Delay}",
                (int)statusCode,
                details.Code,
                retryCount,
                AttachmentRetryCount,
                delay);
            await _retryDelay.DelayAsync(delay, cancellationToken);
        }
    }

    private TimeSpan GetRetryDelay(HttpResponseMessage response, int retryIndex)
    {
        if (GetRetryAfter(response) is { } retryAfter)
            return retryAfter;

        var fallbackSeconds = Math.Min(Math.Pow(2, retryIndex), 8);
        var jitteredDelay = _retryJitter.Apply(TimeSpan.FromSeconds(fallbackSeconds));
        return jitteredDelay > TimeSpan.FromSeconds(8)
            ? TimeSpan.FromSeconds(8)
            : jitteredDelay < TimeSpan.Zero ? TimeSpan.Zero : jitteredDelay;
    }

    private TimeSpan? GetRetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta)
            return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;

        if (response.Headers.RetryAfter?.Date is { } retryAt)
        {
            var delay = retryAt - _timeProvider.GetUtcNow();
            return delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
        }

        return null;
    }

    private static bool IsTransientStatusCode(HttpStatusCode statusCode)
        => statusCode == HttpStatusCode.RequestTimeout ||
           statusCode == (HttpStatusCode)429 ||
           (int)statusCode >= 500;

    private static async Task EnsureGraphSuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken = default)
    {
        if (response.IsSuccessStatusCode)
            return;

        var errorBody = await ReadGraphErrorBodyAsync(response, cancellationToken);
        var details = GraphApiException.ReadError(errorBody);
        throw new GraphApiException(
            response.StatusCode,
            details.Code,
            details.Message ?? errorBody,
            errorBody,
            requestId: details.RequestId,
            clientRequestId: details.ClientRequestId,
            innerErrorCode: details.InnerCode);
    }

    private static async Task<string> ReadGraphErrorBodyAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (!string.IsNullOrWhiteSpace(mediaType) &&
            !mediaType.Contains("json", StringComparison.OrdinalIgnoreCase) &&
            !mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) &&
            !mediaType.Contains("xml", StringComparison.OrdinalIgnoreCase))
        {
            return $"[Non-text Graph error body omitted; content-type={mediaType}; " +
                   $"length={response.Content.Headers.ContentLength?.ToString() ?? "unknown"}]";
        }

        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private async Task<string?> GetFolderIdAsync(HttpClient httpClient, string folderName)
    {
        using var response = await httpClient.GetAsync(
            $"{GraphApiBaseUrl}/me/mailFolders?$filter=displayName eq '{EscapeODataString(folderName)}'&$select=id");
        await EnsureGraphSuccessAsync(response);
        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);
        var data = document.RootElement;

        if (data.TryGetProperty("value", out var folderValue) && folderValue.GetArrayLength() > 0)
            return folderValue[0].GetProperty("id").GetString();

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
