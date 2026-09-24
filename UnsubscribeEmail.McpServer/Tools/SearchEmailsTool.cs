using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using UnsubscribeEmail.McpServer.Services;

namespace UnsubscribeEmail.McpServer.Tools;

[McpServerToolType]
public class SearchEmailsTool
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    [McpServerTool(Name = "search_emails"), Description(
        "Search messages without changing mailbox state. At least one of senderEmail, senderDomain, or " +
        "subjectContains is required. Results are newest first, capped at 50, and include the exact Graph " +
        "messageId, subject, receivedDateTime, sender, isRead, and hasAttachments. Attachment metadata is " +
        "not fetched here; pass an exact messageId to list_email_attachments to avoid unbounded N+1 calls. " +
        "You must be logged in first (call 'login' tool).")]
    public static async Task<string> SearchEmails(
        AuthService authService,
        GraphEmailService graphService,
        [Description("Optional exact sender email address.")] string? senderEmail = null,
        [Description("Optional sender domain, such as 'example.com'.")] string? senderDomain = null,
        [Description("Optional case-insensitive text to find in the subject.")] string? subjectContains = null,
        [Description("Maximum messages to return. Values are clamped to 1 through 50.")] int maxEmails = 20,
        [Description("Search the last N days. Values are clamped to 1 through 730.")] int daysBack = 30)
    {
        try
        {
            if (!authService.IsAuthenticated)
            {
                return JsonSerializer.Serialize(new
                {
                    status = "error",
                    message = "Not authenticated. Call the 'login' tool first."
                }, JsonOptions);
            }

            if (string.IsNullOrWhiteSpace(senderEmail) &&
                string.IsNullOrWhiteSpace(senderDomain) &&
                string.IsNullOrWhiteSpace(subjectContains))
            {
                return JsonSerializer.Serialize(new
                {
                    status = "error",
                    message = "At least one filter is required: provide senderEmail, senderDomain, or subjectContains."
                }, JsonOptions);
            }

            var page = await graphService.SearchEmailsPageAsync(
                senderEmail?.Trim(),
                senderDomain?.Trim(),
                subjectContains,
                maxEmails,
                daysBack);

            return JsonSerializer.Serialize(new
            {
                status = "success",
                maxEmails = Math.Clamp(maxEmails, 1, 50),
                daysBack = Math.Clamp(daysBack, 1, 730),
                messageCount = page.Messages.Count,
                hasMore = page.HasMore,
                messages = page.Messages.Select(result => new
                {
                    messageId = result.MessageId,
                    subject = result.Subject,
                    receivedDateTime = result.ReceivedDateTime,
                    sender = result.Sender,
                    isRead = result.IsRead,
                    hasAttachments = result.HasAttachments
                })
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                status = "error",
                message = ex.Message
            }, JsonOptions);
        }
    }
}
