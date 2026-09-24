using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using UnsubscribeEmail.McpServer.Services;

namespace UnsubscribeEmail.McpServer.Tools;

[McpServerToolType]
public class DownloadEmailAttachmentTool
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    [McpServerTool(Name = "download_email_attachment"), Description(
        "Download one file or item attachment from an email message using Microsoft Graph. " +
        "Pass the exact message ID returned by an email-reading tool and the exact attachment ID returned by list_email_attachments. " +
        "The raw bytes are bounded and returned as base64Content with the response MIME type and attachment metadata, including inline flags and content IDs. " +
        "maxBytes defaults to 4,000,000 and is clamped to a maximum of 10,000,000; known oversized content is rejected before reading, and the stream is bounded when no length is provided. " +
        "The metadata size and downloadedSize describe different representations and can differ; a completed bounded stream is not treated as truncated solely because those values differ. " +
        "File and item attachments use Graph's /$value form. Reference attachments return an explicit error and never call /$value. " +
        "You must be logged in first (call 'login' tool).")]
    public static async Task<string> DownloadEmailAttachment(
        AuthService authService,
        GraphEmailService graphService,
        [Description("The exact message ID returned by an email-reading tool. Do not shorten, normalize, or reconstruct it.")] string messageId,
        [Description("The exact attachment ID returned by list_email_attachments. Do not shorten, normalize, or reconstruct it.")] string attachmentId,
        [Description("Maximum attachment bytes to return. Defaults to 4,000,000. Values are clamped to 1 through 10,000,000.")] int maxBytes = 4_000_000)
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

            if (string.IsNullOrWhiteSpace(attachmentId))
            {
                return JsonSerializer.Serialize(new
                {
                    status = "error",
                    message = "attachmentId is required."
                }, JsonOptions);
            }

            maxBytes = Math.Clamp(maxBytes, 1, 10_000_000);
            var download = await graphService.DownloadEmailAttachmentAsync(messageId, attachmentId, maxBytes);

            return JsonSerializer.Serialize(new
            {
                status = "success",
                messageId = download.Attachment.MessageId,
                attachmentId = download.Attachment.AttachmentId,
                name = download.Attachment.Name,
                contentType = download.ContentType ?? download.Attachment.ContentType,
                size = download.Attachment.Size,
                downloadedSize = download.DownloadedSize,
                isInline = download.Attachment.IsInline,
                contentId = download.Attachment.ContentId,
                lastModifiedDateTime = download.Attachment.LastModifiedDateTime,
                attachmentType = download.Attachment.AttachmentType,
                base64Content = download.Base64Content
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
