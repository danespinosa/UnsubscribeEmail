using Microsoft.Graph;
using Microsoft.Identity.Abstractions;
using Microsoft.Identity.Web;
using UnsubscribeEmail.Models;
using System.Net.Http.Headers;
using System.Text.Json;

namespace UnsubscribeEmail.Services;

public interface IEmailService
{
    Task<List<EmailInfo>> GetEmailsFromDateRangeAsync(int daysBack = 365, string? accessToken = null, Action<int, int>? progressCallback = null);
}

public class EmailService : IEmailService
{
    private readonly ILogger<EmailService> _logger;
    private readonly ITokenAcquisition _tokenAcquisition;
    private readonly IHttpClientFactory _httpClientFactory;

    public EmailService(ILogger<EmailService> logger, ITokenAcquisition tokenAcquisition, IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _tokenAcquisition = tokenAcquisition;
        _httpClientFactory = httpClientFactory;
    }

    public async Task<List<EmailInfo>> GetEmailsFromDateRangeAsync(int daysBack = 365, string? accessToken = null, Action<int, int>? progressCallback = null)
    {
        var emails = new List<EmailInfo>();

        try
        {
            // Only try to acquire token if not provided
            if (string.IsNullOrEmpty(accessToken))
            {
                var scopes = new[] { "Mail.Read" };
                accessToken = await _tokenAcquisition.GetAccessTokenForUserAsync(scopes);
            }

            var httpClient = _httpClientFactory.CreateClient();
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            // Get emails from specified days back
            var startDate = DateTime.Now.AddDays(-daysBack);
            
            var filter = $"receivedDateTime ge {startDate:yyyy-MM-ddTHH:mm:ssZ}";
            var select = "from,toRecipients,subject,body,receivedDateTime";
            var top = 100;
            
            var nextUrl = $"https://graph.microsoft.com/v1.0/me/messages?$filter={Uri.EscapeDataString(filter)}&$select={select}&$top={top}";
            var pageNumber = 0;

            while (!string.IsNullOrEmpty(nextUrl))
            {
                pageNumber++;
                _logger.LogInformation($"Fetching page {pageNumber} from Graph API");
                
                var response = await httpClient.GetAsync(nextUrl);
                response.EnsureSuccessStatusCode();

                var content = await response.Content.ReadAsStringAsync();
                var jsonDoc = JsonDocument.Parse(content);
                var root = jsonDoc.RootElement;

                if (root.TryGetProperty("value", out var messagesArray))
                {
                    var messageCount = messagesArray.GetArrayLength();
                    var previousCount = emails.Count;
                    
                    _logger.LogInformation($"Processing {messageCount} emails from page {pageNumber}");

                    foreach (var message in messagesArray.EnumerateArray())
                    {
                        var from = "";
                        if (message.TryGetProperty("from", out var fromObj) &&
                            fromObj.TryGetProperty("emailAddress", out var emailAddr) &&
                            emailAddr.TryGetProperty("address", out var addr))
                        {
                            from = addr.GetString() ?? "";
                        }

                        var to = "";
                        if (message.TryGetProperty("toRecipients", out var toRecipientsArray) &&
                            toRecipientsArray.GetArrayLength() > 0)
                        {
                            var firstRecipient = toRecipientsArray[0];
                            if (firstRecipient.TryGetProperty("emailAddress", out var toEmailAddr) &&
                                toEmailAddr.TryGetProperty("address", out var toAddr))
                            {
                                to = toAddr.GetString() ?? "";
                            }
                        }

                        var subject = message.TryGetProperty("subject", out var subj) ? subj.GetString() ?? "" : "";
                        
                        var body = "";
                        if (message.TryGetProperty("body", out var bodyObj) &&
                            bodyObj.TryGetProperty("content", out var bodyContent))
                        {
                            body = bodyContent.GetString() ?? "";
                        }

                        var date = DateTime.MinValue;
                        if (message.TryGetProperty("receivedDateTime", out var receivedDt))
                        {
                            date = receivedDt.GetDateTime();
                        }

                        emails.Add(new EmailInfo
                        {
                            From = from,
                            To = to,
                            Subject = subject,
                            Body = body,
                            Date = date
                        });
                    }
                    
                    // Notify progress after each page
                    progressCallback?.Invoke(emails.Count, pageNumber);
                }

                // Check for next page
                nextUrl = null;
                if (root.TryGetProperty("@odata.nextLink", out var nextLink))
                {
                    nextUrl = nextLink.GetString();
                }
            }

            _logger.LogInformation($"Total emails fetched from last {daysBack} days: {emails.Count} across {pageNumber} pages");
        }
        catch (MicrosoftIdentityWebChallengeUserException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching emails from Microsoft Graph");
            throw;
        }

        return emails;
    }
}
