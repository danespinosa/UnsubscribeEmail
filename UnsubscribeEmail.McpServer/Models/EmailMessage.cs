namespace UnsubscribeEmail.McpServer.Models;

public class EmailMessage
{
    public string Id { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string SenderName { get; set; } = string.Empty;
    public string SenderEmail { get; set; } = string.Empty;
    public string RecipientEmail { get; set; } = string.Empty;
    public DateTime ReceivedDateTime { get; set; }
    public bool IsRead { get; set; }
}
