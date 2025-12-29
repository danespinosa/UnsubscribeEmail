using Microsoft.Graph;
using Microsoft.Identity.Web;
using UnsubscribeEmail.Models;

namespace UnsubscribeEmail.Services;

public interface IEmailService
{
    Task<List<EmailInfo>> GetEmailsFromCurrentYearAsync();
}

public class EmailService : IEmailService
{
    private readonly ILogger<EmailService> _logger;
    private readonly ITokenAcquisition _tokenAcquisition;
    private readonly GraphServiceClient _graphClient;

    public EmailService(ILogger<EmailService> logger, ITokenAcquisition tokenAcquisition, GraphServiceClient graphClient)
    {
        _logger = logger;
        _tokenAcquisition = tokenAcquisition;
        _graphClient = graphClient;
    }

    public async Task<List<EmailInfo>> GetEmailsFromCurrentYearAsync()
    {
        var emails = new List<EmailInfo>();

        try
        {
            // Get emails from current year
            var currentYear = DateTime.Now.Year;
            var startOfYear = new DateTime(currentYear, 1, 1);
            
            var filter = $"receivedDateTime ge {startOfYear:yyyy-MM-ddTHH:mm:ssZ}";
            
            var messagesRequest = _graphClient.Me.Messages.Request()
                .Filter(filter)
                .Select("from,subject,body,receivedDateTime")
                .Top(999);
            
            var messages = await messagesRequest.GetAsync();

            if (messages?.Count > 0)
            {
                _logger.LogInformation($"Found {messages.Count} emails from {currentYear}");

                foreach (var message in messages)
                {
                    emails.Add(new EmailInfo
                    {
                        From = message.From?.EmailAddress?.Address ?? string.Empty,
                        Subject = message.Subject ?? string.Empty,
                        Body = message.Body?.Content ?? string.Empty,
                        Date = message.ReceivedDateTime?.DateTime ?? DateTime.MinValue
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching emails from Microsoft Graph");
            throw;
        }

        return emails;
    }
}
