namespace UnsubscribeEmail.Models;

public class SenderUnsubscribeInfo
{
    public string SenderEmail { get; set; } = string.Empty;
    public string? UnsubscribeLink { get; set; }
    public DateTime LastChecked { get; set; }
}
