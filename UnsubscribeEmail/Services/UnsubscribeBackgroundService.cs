using Microsoft.AspNetCore.SignalR;
using System.Collections.Concurrent;
using UnsubscribeEmail.Hubs;
using UnsubscribeEmail.Models;

namespace UnsubscribeEmail.Services;

public class UnsubscribeBackgroundService : IUnsubscribeBackgroundService
{
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly IHubContext<UnsubscribeProgressHub> _hubContext;
    private readonly ILogger<UnsubscribeBackgroundService> _logger;
    private readonly ConcurrentDictionary<string, ProcessingStatus> _jobs = new();

    public UnsubscribeBackgroundService(
        IServiceScopeFactory serviceScopeFactory,
        IHubContext<UnsubscribeProgressHub> hubContext,
        ILogger<UnsubscribeBackgroundService> logger)
    {
        _serviceScopeFactory = serviceScopeFactory;
        _hubContext = hubContext;
        _logger = logger;
    }

    public string StartProcessing(string userId, string accessToken, int daysBack = 365)
    {
        var jobId = Guid.NewGuid().ToString();
        var status = new ProcessingStatus
        {
            JobId = jobId,
            UserId = userId,
            AccessToken = accessToken,
            DaysBack = daysBack
        };

        _jobs[jobId] = status;

        // Start background task
        _ = Task.Run(async () => await ProcessEmailsAsync(jobId, userId));

        return jobId;
    }

    public Task<ProcessingStatus?> GetStatusAsync(string jobId)
    {
        _jobs.TryGetValue(jobId, out var status);
        return Task.FromResult(status);
    }

    private async Task ProcessEmailsAsync(string jobId, string userId)
    {
        var status = _jobs[jobId];

        try
        {
            using var scope = _serviceScopeFactory.CreateScope();
            var emailService = scope.ServiceProvider.GetRequiredService<IEmailManagementBackgroundService>();
            var linkExtractor = scope.ServiceProvider.GetRequiredService<IUnsubscribeLinkExtractor>();

            // Step 1: Fetch emails using the pre-acquired access token with progress updates
            status.CurrentStep = "Connecting to Microsoft Graph API...";
            await SendProgressUpdate(userId, status);

            var emails = await emailService.GetEmailsFromDateRangeAsync(
                status.DaysBack,
                status.AccessToken, 
                async (emailCount, pageNumber) =>
                {
                    status.TotalEmails = emailCount;
                    status.CurrentStep = $"Fetched {emailCount} emails from Graph API (page {pageNumber})...";
                    await SendProgressUpdate(userId, status);
                });
            
            status.TotalEmails = emails.Count;

            status.CurrentStep = $"Retrieved {emails.Count} emails. Grouping by sender...";
            await SendProgressUpdate(userId, status);

            // Step 2: Group by sender
            var emailsBySender = emails
                .GroupBy(e => ExtractEmailAddress(e.From))
                .OrderBy(g => g.Key)
                .ToList();

            status.TotalSenders = emailsBySender.Count;
            status.CurrentStep = $"Found {emailsBySender.Count} unique senders. Processing unsubscribe links...";
            await SendProgressUpdate(userId, status);

            bool foundAnySenderLinks;

            // Step 3: Process each sender
            foreach (var senderGroup in emailsBySender)
            {
                foundAnySenderLinks = false;
                var senderEmail = senderGroup.Key;
                status.ProcessedSenders++;
                status.CurrentStep = $"Processing sender {status.ProcessedSenders}/{status.TotalSenders}: {senderEmail}";
                await SendProgressUpdate(userId, status);

                string? unsubscribeLink = null;
                string recipientEmail = "";
                List<string>? allAnchors = null;

                // Process emails from this sender until we find an unsubscribe link
                foreach (var email in senderGroup.OrderByDescending(e => e.Date))
                {
                    status.ProcessedEmails++;

                    // Capture the recipient email (To field)
                    if (string.IsNullOrEmpty(recipientEmail) && !string.IsNullOrEmpty(email.To))
                    {
                        recipientEmail = email.To;
                    }

                    var (link, anchors) = await linkExtractor.ExtractUnsubscribeLinkAsync(email.Body);
                    
                    if (!string.IsNullOrEmpty(link))
                    {
                        _logger.LogInformation($"Found unsubscribe link for {senderEmail}: {link}");
                        unsubscribeLink = link;
                        foundAnySenderLinks = true;
                        break;
                    }
                    
                    // Store anchors from the most recent email if no unsubscribe link found yet
                    if (allAnchors == null && anchors != null && anchors.Count > 0)
                    {
                        allAnchors = anchors;
                    }
                }

                if (!foundAnySenderLinks)
                {
                    _logger.LogInformation($"No unsubscribe link found for {senderEmail}");
                    _logger.LogInformation($"Extracted {allAnchors?.Count ?? 0} anchor links for {senderEmail}");
                    await SaveFailedEmailHtmlAsync(senderGroup.OrderByDescending(e => e.Date).First().Body, senderEmail);
                }

                var senderInfo = new SenderUnsubscribeInfo
                {
                    SenderEmail = senderEmail,
                    RecipientEmail = recipientEmail,
                    UnsubscribeLink = unsubscribeLink,
                    LastChecked = DateTime.Now,
                    AllAnchors = allAnchors
                };

                status.Results.Add(senderInfo);

                // Send update with new sender added
                await SendSenderUpdate(userId, senderInfo);
            }

            // Step 4: Complete
            status.IsComplete = true;
            status.CompletedAt = DateTime.Now;
            status.CurrentStep = $"Processing complete! Found {status.Results.Count(r => r.UnsubscribeLink != null)} unsubscribe links out of {status.TotalSenders} senders.";
            await SendProgressUpdate(userId, status);
            await SendComplete(userId, status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing emails in background");
            status.Error = ex.Message;
            status.IsComplete = true;
            status.CompletedAt = DateTime.Now;
            status.CurrentStep = $"Error: {ex.Message}";
            await SendProgressUpdate(userId, status);
        }
    }

    private string ExtractEmailAddress(string fromField)
    {
        var match = System.Text.RegularExpressions.Regex.Match(fromField, @"<([^>]+)>");
        if (match.Success)
        {
            return match.Groups[1].Value.ToLower();
        }

        var emailMatch = System.Text.RegularExpressions.Regex.Match(fromField, @"[\w\.-]+@[\w\.-]+\.\w+");
        if (emailMatch.Success)
        {
            return emailMatch.Value.ToLower();
        }

        return fromField.ToLower();
    }

    private async Task SendProgressUpdate(string userId, ProcessingStatus status)
    {
        await _hubContext.Clients.User(userId).SendAsync("ProgressUpdate", new
        {
            step = status.CurrentStep,
            totalEmails = status.TotalEmails,
            processedEmails = status.ProcessedEmails,
            totalSenders = status.TotalSenders,
            processedSenders = status.ProcessedSenders,
            percentage = status.TotalSenders > 0 ? (int)((double)status.ProcessedSenders / status.TotalSenders * 100) : 0
        });
    }

    private async Task SendSenderUpdate(string userId, SenderUnsubscribeInfo senderInfo)
    {
        await _hubContext.Clients.User(userId).SendAsync("SenderProcessed", new
        {
            senderEmail = senderInfo.SenderEmail,
            recipientEmail = senderInfo.RecipientEmail,
            unsubscribeLink = senderInfo.UnsubscribeLink,
            lastChecked = senderInfo.LastChecked.ToString("yyyy-MM-dd HH:mm:ss"),
            allAnchors = senderInfo.AllAnchors
        });
    }

    private async Task SendComplete(string userId, ProcessingStatus status)
    {
        await _hubContext.Clients.User(userId).SendAsync("ProcessingComplete", new
        {
            totalSenders = status.TotalSenders,
            foundLinks = status.Results.Count(r => r.UnsubscribeLink != null),
            duration = (status.CompletedAt - status.StartedAt)?.TotalSeconds ?? 0
        });
    }

    private async Task SaveFailedEmailHtmlAsync(string htmlBody, string senderEmail)
    {
        try
        {
            // Create a directory for failed emails if it doesn't exist
            var failedEmailsDir = Path.Combine(Directory.GetCurrentDirectory(), "FailedEmails");
            Directory.CreateDirectory(failedEmailsDir);

            // Sanitize sender email for filename
            var sanitizedSender = string.Join("_", senderEmail.Split(Path.GetInvalidFileNameChars()));

            // Use a combination of sender and GUID for unique filename
            var fileName = $"{sanitizedSender}_{Guid.NewGuid()}.html";
            var filePath = Path.Combine(failedEmailsDir, fileName);

            await File.WriteAllTextAsync(filePath, htmlBody);
            _logger.LogInformation($"Saved failed email HTML to {filePath}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to save email HTML for {senderEmail}");
        }
    }
}
