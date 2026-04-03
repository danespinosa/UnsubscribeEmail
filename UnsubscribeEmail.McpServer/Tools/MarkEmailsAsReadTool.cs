using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using UnsubscribeEmail.McpServer.Services;

namespace UnsubscribeEmail.McpServer.Tools;

[McpServerToolType]
public class MarkEmailsAsReadTool
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    [McpServerTool(Name = "mark_emails_as_read"), Description(
        "Mark emails as read using Microsoft Graph. " +
        "Requires at least one filter: senderEmail (exact address) or senderDomain (e.g. 'example.com'). " +
        "Supports unreadOnly (default true) to skip already-read messages, and dryRun (default false) " +
        "to preview which messages would be marked without making changes. " +
        "Returns the exact message IDs and per-message results. " +
        "You must be logged in first (call 'login' tool).")]
    public static async Task<string> MarkEmailsAsRead(
        AuthService authService,
        GraphEmailService graphService,
        [Description("Filter by exact sender email address (e.g. 'news@example.com').")] string? senderEmail = null,
        [Description("Filter by sender domain (e.g. 'example.com'). All senders from this domain are matched.")] string? senderDomain = null,
        [Description("When true (default), only processes unread emails. Set to false to mark all matching emails.")] bool unreadOnly = true,
        [Description("When true, returns the list of emails that would be marked without actually marking them. Defaults to false.")] bool dryRun = false,
        [Description("Optional: limit to emails from the last N days. If not specified, searches all emails.")] int? daysBack = null)
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

            if (string.IsNullOrWhiteSpace(senderEmail) && string.IsNullOrWhiteSpace(senderDomain))
            {
                return JsonSerializer.Serialize(new
                {
                    status = "error",
                    message = "At least one filter is required: provide senderEmail, senderDomain, or both."
                }, JsonOptions);
            }

            if (daysBack is < 1)
                daysBack = 1;
            if (daysBack is > 730)
                daysBack = 730;

            var results = await graphService.MarkEmailsAsReadAsync(
                senderEmail?.Trim(),
                senderDomain?.Trim(),
                unreadOnly,
                dryRun,
                daysBack);

            var marked = results.Count(r => r.MarkedAsRead);
            var skipped = results.Count(r => r.WasAlreadyRead && !r.MarkedAsRead);
            var errors = results.Count(r => r.Error != null);

            return JsonSerializer.Serialize(new
            {
                status = "success",
                dryRun,
                totalMatched = results.Count,
                markedAsRead = marked,
                skippedAlreadyRead = skipped,
                errors,
                messages = results.Select(r => new
                {
                    messageId = r.MessageId,
                    subject = r.Subject,
                    senderEmail = r.SenderEmail,
                    receivedDateTime = r.ReceivedDateTime.ToString("yyyy-MM-dd HH:mm:ss"),
                    wasAlreadyRead = r.WasAlreadyRead,
                    markedAsRead = r.MarkedAsRead,
                    error = r.Error
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
