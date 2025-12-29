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

    public async Task<string> StartProcessingAsync(string userId, string accessToken)
    {
        var jobId = Guid.NewGuid().ToString();
        var status = new ProcessingStatus
        {
            JobId = jobId,
            UserId = userId,
            AccessToken = accessToken
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
            var emailService = scope.ServiceProvider.GetRequiredService<IEmailService>();
            var linkExtractor = scope.ServiceProvider.GetRequiredService<IUnsubscribeLinkExtractor>();

            // Step 1: Fetch emails using the pre-acquired access token
            status.CurrentStep = "Fetching emails...";
            await SendProgressUpdate(userId, status);

            var emails = await emailService.GetEmailsFromCurrentYearAsync(status.AccessToken);
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

            // Step 3: Process each sender
            foreach (var senderGroup in emailsBySender)
            {
                var senderEmail = senderGroup.Key;
                status.ProcessedSenders++;
                status.CurrentStep = $"Processing sender {status.ProcessedSenders}/{status.TotalSenders}: {senderEmail}";
                await SendProgressUpdate(userId, status);

                string? unsubscribeLink = null;

                // Process emails from this sender until we find an unsubscribe link
                foreach (var email in senderGroup.OrderByDescending(e => e.Date))
                {
                    status.ProcessedEmails++;

                    unsubscribeLink = await linkExtractor.ExtractUnsubscribeLinkAsync(email.Body);

                    if (!string.IsNullOrEmpty(unsubscribeLink))
                    {
                        _logger.LogInformation($"Found unsubscribe link for {senderEmail}: {unsubscribeLink}");
                        break;
                    }
                }

                var senderInfo = new SenderUnsubscribeInfo
                {
                    SenderEmail = senderEmail,
                    UnsubscribeLink = unsubscribeLink,
                    LastChecked = DateTime.Now
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
            unsubscribeLink = senderInfo.UnsubscribeLink,
            lastChecked = senderInfo.LastChecked.ToString("yyyy-MM-dd HH:mm:ss")
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
}
