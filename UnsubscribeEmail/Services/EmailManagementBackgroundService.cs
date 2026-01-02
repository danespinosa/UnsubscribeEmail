using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Identity.Web;
using UnsubscribeEmail.Hubs;
using UnsubscribeEmail.Models;

namespace UnsubscribeEmail.Services
{
    public class EmailManagementBackgroundService : IEmailManagementBackgroundService
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IHubContext<EmailManagementHub> _hubContext;
        private readonly ILogger<EmailManagementBackgroundService> _logger;
        private readonly ITokenAcquisition _tokenAcquisition;

        public EmailManagementBackgroundService(
            IHttpClientFactory httpClientFactory,
            IHubContext<EmailManagementHub> hubContext,
            ILogger<EmailManagementBackgroundService> logger,
            ITokenAcquisition tokenAcquisition)
        {
            _httpClientFactory = httpClientFactory;
            _hubContext = hubContext;
            _logger = logger;
            _tokenAcquisition = tokenAcquisition;
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

        private async Task<string?> GetDeletedItemsFolderIdAsync(HttpClient httpClient)
        {
            var foldersResponse = await httpClient.GetAsync("https://graph.microsoft.com/v1.0/me/mailFolders?$filter=displayName eq 'Deleted Items'&$select=id");
            foldersResponse.EnsureSuccessStatusCode();
            var foldersContent = await foldersResponse.Content.ReadAsStringAsync();
            var foldersData = JsonSerializer.Deserialize<JsonElement>(foldersContent);
            
            if (foldersData.TryGetProperty("value", out var folderValue) && folderValue.GetArrayLength() > 0)
            {
                return folderValue[0].GetProperty("id").GetString();
            }
            return null;
        }

        private async Task<string?> GetJunkEmailFolderIdAsync(HttpClient httpClient)
        {
            var foldersResponse = await httpClient.GetAsync("https://graph.microsoft.com/v1.0/me/mailFolders?$filter=displayName eq 'Junk Email'&$select=id");
            foldersResponse.EnsureSuccessStatusCode();
            var foldersContent = await foldersResponse.Content.ReadAsStringAsync();
            var foldersData = JsonSerializer.Deserialize<JsonElement>(foldersContent);
            
            if (foldersData.TryGetProperty("value", out var folderValue) && folderValue.GetArrayLength() > 0)
            {
                return folderValue[0].GetProperty("id").GetString();
            }
            return null;
        }

        private async Task<string> GetUserEmailAsync(HttpClient httpClient)
        {
            var userResponse = await httpClient.GetAsync("https://graph.microsoft.com/v1.0/me?$select=mail,userPrincipalName");
            userResponse.EnsureSuccessStatusCode();
            var userContent = await userResponse.Content.ReadAsStringAsync();
            var userData = JsonSerializer.Deserialize<JsonElement>(userContent);
            
            // Try 'mail' first, fallback to 'userPrincipalName'
            if (userData.TryGetProperty("mail", out var mail) && !string.IsNullOrEmpty(mail.GetString()))
            {
                return mail.GetString() ?? "";
            }
            
            if (userData.TryGetProperty("userPrincipalName", out var upn))
            {
                return upn.GetString() ?? "";
            }
            
            return "";
        }

        private bool IsEmailInExcludedFolder(JsonElement message, string? deletedItemsFolderId, string? junkEmailFolderId)
        {
            if (!message.TryGetProperty("parentFolderId", out var parentFolderId))
            {
                return false;
            }

            var parentFolder = parentFolderId.GetString();
            
            return (!string.IsNullOrEmpty(deletedItemsFolderId) && parentFolder == deletedItemsFolderId) ||
                   (!string.IsNullOrEmpty(junkEmailFolderId) && parentFolder == junkEmailFolderId);
        }

        private HttpClient CreateAuthenticatedClient(string accessToken)
        {
            var httpClient = _httpClientFactory.CreateClient("GraphAPI");
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            return httpClient;
        }

        public async Task<List<EmailMessage>> FetchEmailsAsync(int daysBack, string connectionId, string accessToken)
        {
            var emails = new List<EmailMessage>();
            var httpClient = CreateAuthenticatedClient(accessToken);

            // First, get the Deleted Items and Junk Email folder IDs
            await _hubContext.Clients.Client(connectionId).SendAsync("ReceiveProgress", "Getting folder information...");
            var deletedItemsFolderId = await GetDeletedItemsFolderIdAsync(httpClient);
            var junkEmailFolderId = await GetJunkEmailFolderIdAsync(httpClient);

            var startDate = DateTime.UtcNow.AddDays(-daysBack).ToString("yyyy-MM-ddTHH:mm:ssZ");
            var filter = $"receivedDateTime ge {startDate} and isDraft eq false";
            // Query all messages, not just inbox
            var url = $"https://graph.microsoft.com/v1.0/me/messages?$filter={Uri.EscapeDataString(filter)}&$select=id,subject,from,toRecipients,receivedDateTime,isRead,parentFolderId&$top=999&$orderby=receivedDateTime desc";

            var pageCount = 0;
            while (!string.IsNullOrEmpty(url))
            {
                pageCount++;
                await _hubContext.Clients.Client(connectionId).SendAsync("ReceiveProgress", $"Fetching page {pageCount} from all folders...");

                var response = await httpClient.GetAsync(url);
                response.EnsureSuccessStatusCode();

                var content = await response.Content.ReadAsStringAsync();
                var data = JsonSerializer.Deserialize<JsonElement>(content);

                if (data.TryGetProperty("value", out var messagesArray))
                {
                    foreach (var message in messagesArray.EnumerateArray())
                    {
                        // Skip emails in Deleted Items or Junk Email folders
                        if (IsEmailInExcludedFolder(message, deletedItemsFolderId, junkEmailFolderId))
                        {
                            continue;
                        }

                        var email = new EmailMessage
                        {
                            Id = message.GetProperty("id").GetString() ?? "",
                            Subject = message.TryGetProperty("subject", out var subject) ? subject.GetString() ?? "" : "",
                            ReceivedDateTime = message.TryGetProperty("receivedDateTime", out var receivedDateTime) 
                                ? DateTime.Parse(receivedDateTime.GetString() ?? "") 
                                : DateTime.MinValue,
                            IsRead = message.TryGetProperty("isRead", out var isRead) && isRead.GetBoolean()
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

            await _hubContext.Clients.Client(connectionId).SendAsync("ReceiveProgress", $"Fetched {emails.Count} emails total (excluding deleted items and junk)");

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
                    UnreadCount = g.Count(e => !e.IsRead),
                    EmailIds = g.Select(e => e.Id).ToList(),
                    LastEmailDate = g.Max(e => e.ReceivedDateTime)
                })
                .ToList();

            return grouped;
        }

        public async Task<EmailActionResult> MarkEmailsAsReadAsync(string senderEmail, int daysBack, string accessToken)
        {
            try
            {
                var httpClient = CreateAuthenticatedClient(accessToken);
                var deletedItemsFolderId = await GetDeletedItemsFolderIdAsync(httpClient);
                var junkEmailFolderId = await GetJunkEmailFolderIdAsync(httpClient);

                // Get the current user's email address
                var userEmail = await GetUserEmailAsync(httpClient);

                var startDate = DateTime.UtcNow.AddDays(-daysBack).ToString("yyyy-MM-ddTHH:mm:ssZ");
                string filter;
                string url;

                // If querying for own emails, we need to get all emails and filter in C# because
                // Graph API returns 0 results when filtering inbox for emails from yourself
                if (senderEmail.Equals(userEmail, StringComparison.OrdinalIgnoreCase))
                {
                    filter = $"receivedDateTime ge {startDate}";
                    url = $"https://graph.microsoft.com/v1.0/me/messages?$filter={Uri.EscapeDataString(filter)}&$select=id,isRead,from,parentFolderId&$top=999";
                }
                else
                {
                    filter = $"from/emailAddress/address eq '{senderEmail}' and receivedDateTime ge {startDate}";
                    url = $"https://graph.microsoft.com/v1.0/me/messages?$filter={Uri.EscapeDataString(filter)}&$select=id,isRead,parentFolderId&$top=999";
                }

                var emailIds = new List<string>();
                var unreadCount = 0;

                while (!string.IsNullOrEmpty(url))
                {
                    var response = await httpClient.GetAsync(url);
                    response.EnsureSuccessStatusCode();

                    var content = await response.Content.ReadAsStringAsync();
                    var data = JsonSerializer.Deserialize<JsonElement>(content);

                    if (data.TryGetProperty("value", out var messagesArray))
                    {
                        foreach (var message in messagesArray.EnumerateArray())
                        {
                            // Skip emails in Deleted Items or Junk Email folders
                            if (IsEmailInExcludedFolder(message, deletedItemsFolderId, junkEmailFolderId))
                            {
                                continue;
                            }

                            // If querying own emails, filter by sender in C#
                            if (senderEmail.Equals(userEmail, StringComparison.OrdinalIgnoreCase))
                            {
                                if (message.TryGetProperty("from", out var from) &&
                                    from.TryGetProperty("emailAddress", out var emailAddress) &&
                                    emailAddress.TryGetProperty("address", out var address))
                                {
                                    var fromEmail = address.GetString() ?? "";
                                    if (!fromEmail.Equals(senderEmail, StringComparison.OrdinalIgnoreCase))
                                    {
                                        continue;
                                    }
                                }
                                else
                                {
                                    continue;
                                }
                            }

                            var id = message.GetProperty("id").GetString();
                            var isRead = message.TryGetProperty("isRead", out var isReadProp) && isReadProp.GetBoolean();
                            
                            if (!isRead && !string.IsNullOrEmpty(id))
                            {
                                emailIds.Add(id);
                                unreadCount++;
                            }
                        }
                    }

                    // Check for next page
                    url = data.TryGetProperty("@odata.nextLink", out var nextLink)
                        ? nextLink.GetString() ?? ""
                        : "";
                }

                // Mark each email as read using PATCH
                var markedCount = 0;
                foreach (var emailId in emailIds)
                {
                    try
                    {
                        var patchUrl = $"https://graph.microsoft.com/v1.0/me/messages/{emailId}";
                        var patchContent = new StringContent(
                            JsonSerializer.Serialize(new { isRead = true }),
                            System.Text.Encoding.UTF8,
                            "application/json");

                        var patchResponse = await httpClient.PatchAsync(patchUrl, patchContent);
                        if (patchResponse.IsSuccessStatusCode)
                        {
                            markedCount++;
                        }
                        else
                        {
                            _logger.LogWarning($"Failed to mark email {emailId} as read: {patchResponse.StatusCode}");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"Error marking email {emailId} as read");
                    }
                }

                return new EmailActionResult
                {
                    Success = true,
                    Message = $"Marked {markedCount} of {unreadCount} unread emails as read from {senderEmail}",
                    Count = markedCount
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error marking emails as read from {senderEmail}");
                return new EmailActionResult
                {
                    Success = false,
                    Message = $"Error: {ex.Message}",
                    Count = 0
                };
            }
        }

        public async Task<EmailActionResult> DeleteEmailsAsync(string senderEmail, int daysBack, string accessToken)
        {
            try
            {
                var httpClient = CreateAuthenticatedClient(accessToken);
                var deletedItemsFolderId = await GetDeletedItemsFolderIdAsync(httpClient);
                var junkEmailFolderId = await GetJunkEmailFolderIdAsync(httpClient);

                // Get the current user's email address
                var userEmail = await GetUserEmailAsync(httpClient);

                var startDate = DateTime.UtcNow.AddDays(-daysBack).ToString("yyyy-MM-ddTHH:mm:ssZ");
                string filter;
                string url;

                // If querying for own emails, we need to get all emails and filter in C# because
                // Graph API returns 0 results when filtering inbox for emails from yourself
                if (senderEmail.Equals(userEmail, StringComparison.OrdinalIgnoreCase))
                {
                    filter = $"receivedDateTime ge {startDate}";
                    url = $"https://graph.microsoft.com/v1.0/me/messages?$filter={Uri.EscapeDataString(filter)}&$select=id,subject,from,parentFolderId&$top=999";
                }
                else
                {
                    filter = $"from/emailAddress/address eq '{senderEmail}' and receivedDateTime ge {startDate}";
                    url = $"https://graph.microsoft.com/v1.0/me/messages?$filter={Uri.EscapeDataString(filter)}&$select=id,subject,parentFolderId&$top=999";
                }

                var emailIds = new List<string>();

                while (!string.IsNullOrEmpty(url))
                {
                    var response = await httpClient.GetAsync(url);
                    response.EnsureSuccessStatusCode();

                    var content = await response.Content.ReadAsStringAsync();
                    var data = JsonSerializer.Deserialize<JsonElement>(content);

                    if (data.TryGetProperty("value", out var messagesArray))
                    {
                        foreach (var message in messagesArray.EnumerateArray())
                        {
                            // Skip emails already in Deleted Items or Junk Email folders
                            if (IsEmailInExcludedFolder(message, deletedItemsFolderId, junkEmailFolderId))
                            {
                                continue;
                            }

                            // If querying own emails, filter by sender in C#
                            if (senderEmail.Equals(userEmail, StringComparison.OrdinalIgnoreCase))
                            {
                                if (message.TryGetProperty("from", out var from) &&
                                    from.TryGetProperty("emailAddress", out var emailAddress) &&
                                    emailAddress.TryGetProperty("address", out var address))
                                {
                                    var fromEmail = address.GetString() ?? "";
                                    if (!fromEmail.Equals(senderEmail, StringComparison.OrdinalIgnoreCase))
                                    {
                                        continue;
                                    }
                                }
                                else
                                {
                                    continue;
                                }
                            }

                            var id = message.GetProperty("id").GetString();
                            if (!string.IsNullOrEmpty(id))
                            {
                                emailIds.Add(id);
                            }
                        }
                    }

                    // Check for next page
                    url = data.TryGetProperty("@odata.nextLink", out var nextLink)
                        ? nextLink.GetString() ?? ""
                        : "";
                }

                // Delete each email using DELETE
                var deletedCount = 0;
                foreach (var emailId in emailIds)
                {
                    try
                    {
                        var deleteUrl = $"https://graph.microsoft.com/v1.0/me/messages/{emailId}";
                        var deleteResponse = await httpClient.DeleteAsync(deleteUrl);
                        if (deleteResponse.IsSuccessStatusCode)
                        {
                            deletedCount++;
                        }
                        else
                        {
                            _logger.LogWarning($"Failed to delete email {emailId}: {deleteResponse.StatusCode}");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"Error deleting email {emailId}");
                    }
                }

                return new EmailActionResult
                {
                    Success = true,
                    Message = $"Deleted {deletedCount} of {emailIds.Count} emails from {senderEmail}",
                    Count = deletedCount
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error deleting emails from {senderEmail}");
                return new EmailActionResult
                {
                    Success = false,
                    Message = $"Error: {ex.Message}",
                    Count = 0
                };
            }
        }

        public async Task<List<EmailMessage>> GetEmailsFromDateRangeAsync(int daysBack = 365, string? accessToken = null, Action<int, int>? progressCallback = null)
        {
            var emails = new List<EmailMessage>();

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

                // Get folder IDs to exclude deleted and junk emails
                var deletedItemsFolderId = await GetDeletedItemsFolderIdAsync(httpClient);
                var junkEmailFolderId = await GetJunkEmailFolderIdAsync(httpClient);

                // Get emails from specified days back
                var startDate = DateTime.Now.AddDays(-daysBack);
                
                var filter = $"receivedDateTime ge {startDate:yyyy-MM-ddTHH:mm:ssZ}";
                var select = "from,toRecipients,subject,body,receivedDateTime,parentFolderId";
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
                            // Skip emails in Deleted Items or Junk Email folders
                            if (IsEmailInExcludedFolder(message, deletedItemsFolderId, junkEmailFolderId))
                            {
                                continue;
                            }

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

                            emails.Add(new EmailMessage
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

                _logger.LogInformation($"Total emails fetched from last {daysBack} days: {emails.Count} across {pageNumber} pages (excluding deleted items and junk)");
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
}
