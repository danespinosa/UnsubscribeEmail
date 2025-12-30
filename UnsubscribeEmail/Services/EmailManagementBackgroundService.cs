using Microsoft.AspNetCore.SignalR;
using System.Net.Http.Headers;
using System.Text.Json;
using UnsubscribeEmail.Hubs;
using UnsubscribeEmail.Models;

namespace UnsubscribeEmail.Services
{
    public class EmailManagementBackgroundService : IEmailManagementBackgroundService
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IHubContext<EmailManagementHub> _hubContext;
        private readonly ILogger<EmailManagementBackgroundService> _logger;

        public EmailManagementBackgroundService(
            IHttpClientFactory httpClientFactory,
            IHubContext<EmailManagementHub> hubContext,
            ILogger<EmailManagementBackgroundService> logger)
        {
            _httpClientFactory = httpClientFactory;
            _hubContext = hubContext;
            _logger = logger;
        }

        public async Task ProcessEmailsAsync(int daysBack, string connectionId, string accessToken)
        {
            try
            {
                await _hubContext.Clients.Client(connectionId).SendAsync("ReceiveProgress", "Starting email processing...");
                await _hubContext.Clients.Client(connectionId).SendAsync("ReceiveStatus", "Fetching emails from Microsoft Graph...");

                var emails = await FetchEmailsAsync(daysBack, connectionId, accessToken);

                await _hubContext.Clients.Client(connectionId).SendAsync("ReceiveStatus", $"Processing {emails.Count} emails...");

                var senderGroups = GroupEmailsBySender(emails);

                await _hubContext.Clients.Client(connectionId).SendAsync("ReceiveStatus", $"Found {senderGroups.Count} unique senders");

                foreach (var senderGroup in senderGroups.OrderByDescending(s => s.EmailCount))
                {
                    await _hubContext.Clients.Client(connectionId).SendAsync("ReceiveSenderEmail", senderGroup);
                    await Task.Delay(50); // Small delay to avoid overwhelming the client
                }

                await _hubContext.Clients.Client(connectionId).SendAsync("ReceiveComplete", new
                {
                    TotalEmails = emails.Count,
                    UniqueSenders = senderGroups.Count
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing emails");
                await _hubContext.Clients.Client(connectionId).SendAsync("ReceiveError", $"Error: {ex.Message}");
            }
        }

        private async Task<List<EmailMessage>> FetchEmailsAsync(int daysBack, string connectionId, string accessToken)
        {
            var emails = new List<EmailMessage>();
            var httpClient = _httpClientFactory.CreateClient("GraphAPI");
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            var startDate = DateTime.UtcNow.AddDays(-daysBack).ToString("yyyy-MM-ddTHH:mm:ssZ");
            var filter = $"receivedDateTime ge {startDate}";
            var url = $"https://graph.microsoft.com/v1.0/me/messages?$filter={Uri.EscapeDataString(filter)}&$select=id,subject,from,toRecipients,receivedDateTime&$top=999&$orderby=receivedDateTime desc";

            var pageCount = 0;
            while (!string.IsNullOrEmpty(url))
            {
                pageCount++;
                await _hubContext.Clients.Client(connectionId).SendAsync("ReceiveProgress", $"Fetching page {pageCount}...");

                var response = await httpClient.GetAsync(url);
                response.EnsureSuccessStatusCode();

                var content = await response.Content.ReadAsStringAsync();
                var data = JsonSerializer.Deserialize<JsonElement>(content);

                if (data.TryGetProperty("value", out var messagesArray))
                {
                    foreach (var message in messagesArray.EnumerateArray())
                    {
                        var email = new EmailMessage
                        {
                            Id = message.GetProperty("id").GetString() ?? "",
                            Subject = message.TryGetProperty("subject", out var subject) ? subject.GetString() ?? "" : "",
                            ReceivedDateTime = message.TryGetProperty("receivedDateTime", out var receivedDateTime) 
                                ? DateTime.Parse(receivedDateTime.GetString() ?? "") 
                                : DateTime.MinValue
                        };

                        if (message.TryGetProperty("from", out var from) &&
                            from.TryGetProperty("emailAddress", out var emailAddress))
                        {
                            email.SenderName = emailAddress.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "";
                            email.SenderEmail = emailAddress.TryGetProperty("address", out var address) ? address.GetString() ?? "" : "";
                        }

                        // Get recipient email (first toRecipient)
                        if (message.TryGetProperty("toRecipients", out var toRecipients) && toRecipients.GetArrayLength() > 0)
                        {
                            var firstRecipient = toRecipients[0];
                            if (firstRecipient.TryGetProperty("emailAddress", out var recipientEmailAddress))
                            {
                                email.RecipientEmail = recipientEmailAddress.TryGetProperty("address", out var recipientAddress) 
                                    ? recipientAddress.GetString() ?? "" 
                                    : "";
                            }
                        }

                        emails.Add(email);
                    }
                }

                // Check for next page
                url = data.TryGetProperty("@odata.nextLink", out var nextLink) 
                    ? nextLink.GetString() ?? "" 
                    : "";
            }

            await _hubContext.Clients.Client(connectionId).SendAsync("ReceiveProgress", $"Fetched {emails.Count} emails total");

            return emails;
        }

        private List<SenderEmailInfo> GroupEmailsBySender(List<EmailMessage> emails)
        {
            var grouped = emails
                .GroupBy(e => e.SenderEmail.ToLowerInvariant())
                .Select(g => new SenderEmailInfo
                {
                    SenderEmail = g.First().SenderEmail,
                    SenderName = g.First().SenderName,
                    RecipientEmail = g.First().RecipientEmail,
                    EmailCount = g.Count(),
                    EmailIds = g.Select(e => e.Id).ToList(),
                    LastEmailDate = g.Max(e => e.ReceivedDateTime)
                })
                .ToList();

            return grouped;
        }

        private class EmailMessage
        {
            public string Id { get; set; } = string.Empty;
            public string Subject { get; set; } = string.Empty;
            public string SenderName { get; set; } = string.Empty;
            public string SenderEmail { get; set; } = string.Empty;
            public string RecipientEmail { get; set; } = string.Empty;
            public DateTime ReceivedDateTime { get; set; }
        }
    }
}
