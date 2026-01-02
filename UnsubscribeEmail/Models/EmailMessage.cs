namespace UnsubscribeEmail.Models;

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
    
    // Aliases for compatibility with EmailInfo usage
    public string From
    {
        get => SenderEmail;
        set => SenderEmail = value;
    }
    
    public string To
    {
        get => RecipientEmail;
        set => RecipientEmail = value;
    }
    
    public DateTime Date
    {
        get => ReceivedDateTime;
        set => ReceivedDateTime = value;
    }
}
