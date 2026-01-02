using System.Collections.Concurrent;
using UnsubscribeEmail.Models;

namespace UnsubscribeEmail.Services;

public interface IUnsubscribeService
{
    Task<List<SenderUnsubscribeInfo>> GetSenderUnsubscribeLinksAsync();
}

public class UnsubscribeService : IUnsubscribeService
{
    private readonly IEmailManagementBackgroundService _emailService;
    private readonly IUnsubscribeLinkExtractor _linkExtractor;
    private readonly ILogger<UnsubscribeService> _logger;
    private readonly ConcurrentDictionary<string, SenderUnsubscribeInfo> _cache;

    public UnsubscribeService(
        IEmailManagementBackgroundService emailService,
        IUnsubscribeLinkExtractor linkExtractor,
        ILogger<UnsubscribeService> logger)
    {
        _emailService = emailService;
        _linkExtractor = linkExtractor;
        _logger = logger;
        _cache = new ConcurrentDictionary<string, SenderUnsubscribeInfo>();
    }

    public async Task<List<SenderUnsubscribeInfo>> GetSenderUnsubscribeLinksAsync()
    {
        _logger.LogInformation("Starting to fetch emails and extract unsubscribe links...");

        var emails = await _emailService.GetEmailsFromDateRangeAsync();
        _logger.LogInformation($"Retrieved {emails.Count} emails from date range");

        // Group emails by sender
        var emailsBySender = emails
            .GroupBy(e => ExtractEmailAddress(e.From))
            .OrderBy(g => g.Key);

        foreach (var senderGroup in emailsBySender)
        {
            var senderEmail = senderGroup.Key;

            // Check if we already have unsubscribe link for this sender
            if (_cache.TryGetValue(senderEmail, out var existingInfo) && existingInfo.UnsubscribeLink != null)
            {
                _logger.LogInformation($"Already have unsubscribe link for {senderEmail}, skipping...");
                continue;
            }

            _logger.LogInformation($"Processing emails from {senderEmail}...");

            // Process emails from this sender until we find an unsubscribe link
            string? unsubscribeLink = null;
            foreach (var email in senderGroup.OrderByDescending(e => e.Date))
            {
                unsubscribeLink = await _linkExtractor.ExtractUnsubscribeLinkAsync(email.Body);
                
                if (!string.IsNullOrEmpty(unsubscribeLink))
                {
                    _logger.LogInformation($"Found unsubscribe link for {senderEmail}: {unsubscribeLink}");
                    break;
                }
            }

            // Add or update cache
            _cache.AddOrUpdate(
                senderEmail,
                new SenderUnsubscribeInfo
                {
                    SenderEmail = senderEmail,
                    UnsubscribeLink = unsubscribeLink,
                    LastChecked = DateTime.Now
                },
                (key, oldValue) => new SenderUnsubscribeInfo
                {
                    SenderEmail = senderEmail,
                    UnsubscribeLink = unsubscribeLink ?? oldValue.UnsubscribeLink,
                    LastChecked = DateTime.Now
                });
        }

        _logger.LogInformation($"Completed processing. Found unsubscribe links for {_cache.Count(c => c.Value.UnsubscribeLink != null)} senders");

        return _cache.Values.OrderBy(s => s.SenderEmail).ToList();
    }

    private string ExtractEmailAddress(string fromField)
    {
        // Extract email address from "Name <email@example.com>" format
        var match = System.Text.RegularExpressions.Regex.Match(fromField, @"<([^>]+)>");
        if (match.Success)
        {
            return match.Groups[1].Value.ToLower();
        }

        // If no angle brackets, assume the whole string is the email
        var emailMatch = System.Text.RegularExpressions.Regex.Match(fromField, @"[\w\.-]+@[\w\.-]+\.\w+");
        if (emailMatch.Success)
        {
            return emailMatch.Value.ToLower();
        }

        return fromField.ToLower();
    }
}
