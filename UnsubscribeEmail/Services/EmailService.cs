using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MimeKit;
using UnsubscribeEmail.Models;

namespace UnsubscribeEmail.Services;

public interface IEmailService
{
    Task<List<EmailInfo>> GetEmailsFromCurrentYearAsync();
}

public class EmailService : IEmailService
{
    private readonly EmailConfiguration _config;
    private readonly ILogger<EmailService> _logger;

    public EmailService(EmailConfiguration config, ILogger<EmailService> logger)
    {
        _config = config;
        _logger = logger;
    }

    public async Task<List<EmailInfo>> GetEmailsFromCurrentYearAsync()
    {
        var emails = new List<EmailInfo>();

        try
        {
            using var client = new ImapClient();
            await client.ConnectAsync(_config.ImapServer, _config.ImapPort, _config.UseSsl);
            await client.AuthenticateAsync(_config.Email, _config.Password);

            var inbox = client.Inbox;
            await inbox.OpenAsync(FolderAccess.ReadOnly);

            // Get emails from current year
            var currentYear = DateTime.Now.Year;
            var startOfYear = new DateTime(currentYear, 1, 1);
            
            var query = SearchQuery.DeliveredAfter(startOfYear);
            var uids = await inbox.SearchAsync(query);

            _logger.LogInformation($"Found {uids.Count} emails from {currentYear}");

            foreach (var uid in uids)
            {
                var message = await inbox.GetMessageAsync(uid);
                
                emails.Add(new EmailInfo
                {
                    From = message.From.ToString(),
                    Subject = message.Subject ?? string.Empty,
                    Body = message.TextBody ?? message.HtmlBody ?? string.Empty,
                    Date = message.Date.DateTime
                });
            }

            await client.DisconnectAsync(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching emails");
            throw;
        }

        return emails;
    }
}
