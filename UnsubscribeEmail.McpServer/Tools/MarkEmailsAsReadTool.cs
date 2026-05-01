using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using UnsubscribeEmail.McpServer.Services;

namespace UnsubscribeEmail.McpServer.Tools;

[McpServerToolType]
public class MarkEmailsAsReadTool
{
    [McpServerTool(Name = "mark_emails_as_read"), Description(
        "Mark all emails from a specific sender as read. " +
        "Optionally limit to emails from the last N days. " +
        "You must be logged in first (call 'login' tool).")]
    public static async Task<string> MarkEmailsAsRead(
        AuthService authService,
        GraphEmailService graphService,
        [Description("The sender email address whose emails should be marked as read.")] string senderEmail,
        [Description("Optional: number of days back to limit the scope. If not specified, marks all emails from this sender.")] int? daysBack = null)
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

            var count = await graphService.MarkEmailsAsReadAsync(senderEmail, daysBack);

            return JsonSerializer.Serialize(new
            {
                status = "success",
                senderEmail,
                markedAsReadCount = count,
                message = $"Marked {count} email(s) from {senderEmail} as read."
            });
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
