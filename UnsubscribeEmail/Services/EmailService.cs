using Microsoft.Graph;
using Microsoft.Identity.Abstractions;
using Microsoft.Identity.Web;
using UnsubscribeEmail.Models;
using System.Net.Http.Headers;
using System.Text.Json;

namespace UnsubscribeEmail.Services;

public interface IEmailService
{
    Task<List<EmailInfo>> GetEmailsFromCurrentYearAsync(string? accessToken = null, Action<int, int>? progressCallback = null);
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

    public async Task<List<EmailInfo>> GetEmailsFromCurrentYearAsync(string? accessToken = null, Action<int, int>? progressCallback = null)
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

            // Get emails from current year
            var currentYear = DateTime.Now.Year;
            var startOfYear = new DateTime(currentYear, 1, 1);
            
            var filter = $"receivedDateTime ge {startOfYear:yyyy-MM-ddTHH:mm:ssZ}";
            var select = "from,subject,body,receivedDateTime";
            var top = 999;
            
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

            _logger.LogInformation($"Total emails fetched from {currentYear}: {emails.Count} across {pageNumber} pages");
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
