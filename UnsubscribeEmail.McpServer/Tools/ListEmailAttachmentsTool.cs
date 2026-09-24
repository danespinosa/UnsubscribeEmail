using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using UnsubscribeEmail.McpServer.Services;

namespace UnsubscribeEmail.McpServer.Tools;

[McpServerToolType]
public class ListEmailAttachmentsTool
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    [McpServerTool(Name = "list_email_attachments"), Description(
        "List up to 500 attachments on one email message using Microsoft Graph. " +
        "Pass the exact message ID returned by get_email_content (emails[].messageId or legacy emails[].Id), " +
        "mark_emails_as_read (messages[].messageId), or read_emails (senders[].sampleMessageId). " +
        "Returns the exact message ID, bounded totalAttachments, hasMore, and attachment metadata including " +
        "attachment IDs, names, MIME types, sizes, inline flags/content IDs, last-modified timestamps, and " +
        "attachment type (file, item, reference, or unknown). " +
        "The response selects metadata only and never includes contentBytes. " +
        "Reference attachments are listed with their Graph metadata but are not downloadable by the download tool. " +
        "You must be logged in first (call 'login' tool).")]
    public static async Task<string> ListEmailAttachments(
        AuthService authService,
        GraphEmailService graphService,
        [Description("The exact message ID returned by an email-reading tool. Do not shorten, normalize, or reconstruct it.")] string messageId,
        [Description("Maximum number of attachments to return. Defaults to 100. Values are clamped to 1 through 500.")] int maxAttachments = 100)
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

            if (string.IsNullOrWhiteSpace(messageId))
            {
                return JsonSerializer.Serialize(new
                {
                    status = "error",
                    message = "messageId is required."
                }, JsonOptions);
            }

            maxAttachments = Math.Clamp(maxAttachments, 1, 500);
            var result = await graphService.GetEmailAttachmentsAsync(messageId, maxAttachments);

            return JsonSerializer.Serialize(new
            {
                status = "success",
                messageId,
                totalAttachments = result.TotalAttachments,
                hasMore = result.HasMore,
                attachments = result.Attachments
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
