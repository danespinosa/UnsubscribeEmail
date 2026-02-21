namespace UnsubscribeEmail.McpServer.Models;

public class SenderEmailInfo
{
    public string SenderName { get; set; } = string.Empty;
    public string SenderEmail { get; set; } = string.Empty;
    public string RecipientEmail { get; set; } = string.Empty;
    public int EmailCount { get; set; }
    public int UnreadCount { get; set; }
    public DateTime? LastEmailDate { get; set; }
    public string? SampleEmailHtmlBody { get; set; }
}
