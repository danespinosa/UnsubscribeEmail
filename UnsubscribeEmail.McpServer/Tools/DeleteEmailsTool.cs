using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using UnsubscribeEmail.McpServer.Services;

namespace UnsubscribeEmail.McpServer.Tools;

[McpServerToolType]
public class DeleteEmailsTool
{
    [McpServerTool(Name = "delete_emails"), Description(
        "Delete all emails from a specific sender. " +
        "Optionally limit to emails from the last N days. " +
        "Deleted emails are moved to the Deleted Items folder. " +
        "You must be logged in first (call 'login' tool).")]
    public static async Task<string> DeleteEmails(
        AuthService authService,
        GraphEmailService graphService,
        [Description("The sender email address whose emails should be deleted.")] string senderEmail,
        [Description("Optional: number of days back to limit the scope. If not specified, deletes all emails from this sender.")] int? daysBack = null)
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

            var count = await graphService.DeleteEmailsAsync(senderEmail, daysBack);

            return JsonSerializer.Serialize(new
            {
                status = "success",
                senderEmail,
                deletedCount = count,
                message = $"Deleted {count} email(s) from {senderEmail}."
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
