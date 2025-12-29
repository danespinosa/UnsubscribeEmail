namespace UnsubscribeEmail.Models;

public class EmailInfo
{
    public string From { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public DateTime Date { get; set; }
}
