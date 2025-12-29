namespace UnsubscribeEmail.Models;

public class EmailConfiguration
{
    public string ImapServer { get; set; } = "outlook.office365.com";
    public int ImapPort { get; set; } = 993;
    public bool UseSsl { get; set; } = true;
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}
