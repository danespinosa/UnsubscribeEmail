namespace UnsubscribeEmail.McpServer.Models;

public class MarkAsReadResult
{
    public string MessageId { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string SenderEmail { get; set; } = string.Empty;
    public DateTime ReceivedDateTime { get; set; }
    public bool WasAlreadyRead { get; set; }
    public bool MarkedAsRead { get; set; }
    public string? Error { get; set; }
}
