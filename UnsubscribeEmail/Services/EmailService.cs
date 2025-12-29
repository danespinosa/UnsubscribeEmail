using Microsoft.Graph;
using Microsoft.Kiota.Abstractions.Authentication;
using UnsubscribeEmail.Models;

namespace UnsubscribeEmail.Services;

public interface IEmailService
{
    Task<List<EmailInfo>> GetEmailsFromCurrentYearAsync(string accessToken);
}

public class EmailService : IEmailService
{
    private readonly ILogger<EmailService> _logger;

    public EmailService(ILogger<EmailService> logger)
    {
        _logger = logger;
    }

    public async Task<List<EmailInfo>> GetEmailsFromCurrentYearAsync(string accessToken)
    {
        var emails = new List<EmailInfo>();

        try
        {
            var authProvider = new BaseBearerTokenAuthenticationProvider(new TokenProvider(accessToken));
            var graphClient = new GraphServiceClient(authProvider);

            // Get emails from current year
            var currentYear = DateTime.Now.Year;
            var startOfYear = new DateTime(currentYear, 1, 1);
            
            var filter = $"receivedDateTime ge {startOfYear:yyyy-MM-ddTHH:mm:ssZ}";
            
            var messages = await graphClient.Me.Messages
                .GetAsync(requestConfiguration =>
                {
                    requestConfiguration.QueryParameters.Filter = filter;
                    requestConfiguration.QueryParameters.Select = new[] { "from", "subject", "body", "receivedDateTime" };
                    requestConfiguration.QueryParameters.Top = 999;
                });

            if (messages?.Value != null)
            {
                _logger.LogInformation($"Found {messages.Value.Count} emails from {currentYear}");

                foreach (var message in messages.Value)
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

internal class TokenProvider : IAccessTokenProvider
{
    private readonly string _accessToken;

    public TokenProvider(string accessToken)
    {
        _accessToken = accessToken;
    }

    public Task<string> GetAuthorizationTokenAsync(Uri uri, Dictionary<string, object>? additionalAuthenticationContext = null, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_accessToken);
    }

    public AllowedHostsValidator AllowedHostsValidator => new AllowedHostsValidator();
}
