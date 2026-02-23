using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using UnsubscribeEmail.McpServer.Services;

namespace UnsubscribeEmail.McpServer.Tools;

[McpServerToolType]
public class GetEmailContentTool
{
    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = false };

    [McpServerTool(Name = "get_email_content"), Description(
        "Get the full HTML body content of emails from a specific sender. " +
        "Use this to inspect email content for unsubscribe links when the sample " +
        "from read_emails wasn't sufficient. You must be logged in first (call 'login' tool).")]
    public static async Task<string> GetEmailContent(
        AuthService authService,
        GraphEmailService graphService,
        [Description("The sender email address to fetch emails from.")] string senderEmail,
        [Description("Maximum number of emails to return. Defaults to 1. Value must be between 1 and 10.")] int maxEmails = 1,
        [Description("Optional: number of days back to limit the search. If not specified, searches all emails.")] int? daysBack = null)
    {
        try
        {
            if (!authService.IsAuthenticated)
            {
                return JsonSerializer.Serialize(new
                {
                    status = "error",
                    message = "Not authenticated. Call the 'login' tool first."
                });
            }

            if (string.IsNullOrWhiteSpace(senderEmail))
            {
                return JsonSerializer.Serialize(new
                {
                    status = "error",
                    message = "senderEmail is required."
                });
            }

            if (maxEmails < 1) maxEmails = 1;
            if (maxEmails > 10) maxEmails = 10;

            var emails = await graphService.GetEmailsFromSenderAsync(senderEmail, maxEmails, daysBack);

            return JsonSerializer.Serialize(new
            {
                status = "success",
                senderEmail,
                emailCount = emails.Count,
                emails = emails.Select(e => new
                {
                    e.Id,
                    e.Subject,
                    receivedDateTime = e.ReceivedDateTime.ToString("yyyy-MM-dd HH:mm:ss"),
                    e.IsRead,
                    htmlBody = e.Body
                })
            }, s_jsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                status = "error",
                message = ex.Message
            });
        }
    }
}
