using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using UnsubscribeEmail.McpServer.Services;

namespace UnsubscribeEmail.McpServer.Tools;

[McpServerToolType]
public class ReadEmailsTool
{
    [McpServerTool(Name = "read_emails"), Description(
        "Read all emails from the specified number of days back, aggregated by sender. " +
        "Returns sender email, sender name, email count, unread count, last email date, " +
        "and the HTML body content of the most recent email from each sender. " +
        "Common values for daysBack: 1, 7, 30, 60, 90, 365. " +
        "You must be logged in first (call 'login' tool). " +
        "The LLM should inspect the HTML content to find unsubscribe links.")]
    public static async Task<string> ReadEmails(
        AuthService authService,
        GraphEmailService graphService,
        [Description("Number of days back to scan emails. Common values: 1, 7, 30, 60, 90, 365.")] int daysBack = 30)
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

            if (daysBack < 1)
                daysBack = 1;
            if (daysBack > 730)
                daysBack = 730;

            var senders = await graphService.GetEmailsAggregatedBySenderAsync(daysBack);

            return JsonSerializer.Serialize(new
            {
                status = "success",
                daysBack,
                totalSenders = senders.Count,
                totalEmails = senders.Sum(s => s.EmailCount),
                senders = senders.Select(s => new
                {
                    s.SenderEmail,
                    s.SenderName,
                    s.RecipientEmail,
                    s.EmailCount,
                    s.UnreadCount,
                    lastEmailDate = s.LastEmailDate?.ToString("yyyy-MM-dd HH:mm:ss"),
                    sampleEmailHtmlBody = s.SampleEmailHtmlBody
                })
            }, new JsonSerializerOptions { WriteIndented = false });
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
