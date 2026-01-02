using Microsoft.Extensions.Logging;
using Moq;
using UnsubscribeEmail.Models;
using UnsubscribeEmail.Services;

namespace UnsubscribeEmail.Tests;

public class UnsubscribeServiceTests
{
    private readonly Mock<IEmailManagementBackgroundService> _mockEmailService;
    private readonly Mock<IUnsubscribeLinkExtractor> _mockLinkExtractor;
    private readonly Mock<ILogger<UnsubscribeService>> _mockLogger;
    private readonly UnsubscribeService _service;

    public UnsubscribeServiceTests()
    {
        _mockEmailService = new Mock<IEmailManagementBackgroundService>();
        _mockLinkExtractor = new Mock<IUnsubscribeLinkExtractor>();
        _mockLogger = new Mock<ILogger<UnsubscribeService>>();
        _service = new UnsubscribeService(_mockEmailService.Object, _mockLinkExtractor.Object, _mockLogger.Object);
    }

    [Fact]
    public async Task GetSenderUnsubscribeLinksAsync_WithNoEmails_ReturnsEmptyList()
    {
        _mockEmailService.Setup(x => x.GetEmailsFromDateRangeAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<Action<int, int>>()))
            .ReturnsAsync(new List<EmailMessage>());

        var result = await _service.GetSenderUnsubscribeLinksAsync();

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetSenderUnsubscribeLinksAsync_WithEmailsButNoUnsubscribeLinks_ReturnsListWithNullLinks()
    {
        var emails = new List<EmailMessage>
        {
            new EmailMessage { From = "sender@example.com", Subject = "Test", Body = "No link here", Date = DateTime.Now }
        };

        _mockEmailService.Setup(x => x.GetEmailsFromDateRangeAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<Action<int, int>>()))
            .ReturnsAsync(emails);

        _mockLinkExtractor.Setup(x => x.ExtractUnsubscribeLinkAsync(It.IsAny<string>()))
            .ReturnsAsync((string?)null);

        var result = await _service.GetSenderUnsubscribeLinksAsync();

        Assert.Single(result);
        Assert.Equal("sender@example.com", result[0].SenderEmail);
        Assert.Null(result[0].UnsubscribeLink);
    }

    [Fact]
    public async Task GetSenderUnsubscribeLinksAsync_WithUnsubscribeLink_ReturnsCorrectLink()
    {
        var emails = new List<EmailMessage>
        {
            new EmailMessage 
            { 
                From = "newsletter@example.com", 
                Subject = "Newsletter", 
                Body = "Click here to unsubscribe: https://example.com/unsubscribe", 
                Date = DateTime.Now 
            }
        };

        _mockEmailService.Setup(x => x.GetEmailsFromDateRangeAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<Action<int, int>>()))
            .ReturnsAsync(emails);

        _mockLinkExtractor.Setup(x => x.ExtractUnsubscribeLinkAsync(It.IsAny<string>()))
            .ReturnsAsync("https://example.com/unsubscribe");

        var result = await _service.GetSenderUnsubscribeLinksAsync();

        Assert.Single(result);
        Assert.Equal("newsletter@example.com", result[0].SenderEmail);
        Assert.Equal("https://example.com/unsubscribe", result[0].UnsubscribeLink);
    }

    [Fact]
    public async Task GetSenderUnsubscribeLinksAsync_WithMultipleEmailsFromSameSender_ProcessesUntilLinkFound()
    {
        var emails = new List<EmailMessage>
        {
            new EmailMessage 
            { 
                From = "sender@example.com", 
                Subject = "Email 1", 
                Body = "No link", 
                Date = DateTime.Now.AddDays(-2) 
            },
            new EmailMessage 
            { 
                From = "sender@example.com", 
                Subject = "Email 2", 
                Body = "Unsubscribe: https://example.com/unsub", 
                Date = DateTime.Now.AddDays(-1) 
            },
            new EmailMessage 
            { 
                From = "sender@example.com", 
                Subject = "Email 3", 
                Body = "Another link", 
                Date = DateTime.Now 
            }
        };

        _mockEmailService.Setup(x => x.GetEmailsFromDateRangeAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<Action<int, int>>()))
            .ReturnsAsync(emails);

        var callCount = 0;
        _mockLinkExtractor.Setup(x => x.ExtractUnsubscribeLinkAsync(It.IsAny<string>()))
            .ReturnsAsync((string body) =>
            {
                callCount++;
                if (body.Contains("https://example.com/unsub"))
                    return "https://example.com/unsub";
                return null;
            });

        var result = await _service.GetSenderUnsubscribeLinksAsync();

        Assert.Single(result);
        Assert.Equal("sender@example.com", result[0].SenderEmail);
        Assert.Equal("https://example.com/unsub", result[0].UnsubscribeLink);
        Assert.True(callCount <= 3, "Should stop processing after finding link");
    }

    [Fact]
    public async Task GetSenderUnsubscribeLinksAsync_WithMultipleSenders_ReturnsAllSenders()
    {
        var emails = new List<EmailMessage>
        {
            new EmailMessage 
            { 
                From = "sender1@example.com", 
                Subject = "Email 1", 
                Body = "Unsubscribe: https://example1.com/unsub", 
                Date = DateTime.Now 
            },
            new EmailMessage 
            { 
                From = "sender2@example.com", 
                Subject = "Email 2", 
                Body = "Unsubscribe: https://example2.com/unsub", 
                Date = DateTime.Now 
            },
            new EmailMessage 
            { 
                From = "sender3@example.com", 
                Subject = "Email 3", 
                Body = "No link here", 
                Date = DateTime.Now 
            }
        };

        _mockEmailService.Setup(x => x.GetEmailsFromDateRangeAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<Action<int, int>>()))
            .ReturnsAsync(emails);

        _mockLinkExtractor.Setup(x => x.ExtractUnsubscribeLinkAsync(It.Is<string>(s => s.Contains("example1.com"))))
            .ReturnsAsync("https://example1.com/unsub");
        
        _mockLinkExtractor.Setup(x => x.ExtractUnsubscribeLinkAsync(It.Is<string>(s => s.Contains("example2.com"))))
            .ReturnsAsync("https://example2.com/unsub");
        
        _mockLinkExtractor.Setup(x => x.ExtractUnsubscribeLinkAsync(It.Is<string>(s => !s.Contains("example1.com") && !s.Contains("example2.com"))))
            .ReturnsAsync((string?)null);

        var result = await _service.GetSenderUnsubscribeLinksAsync();

        Assert.Equal(3, result.Count);
        Assert.Contains(result, r => r.SenderEmail == "sender1@example.com" && r.UnsubscribeLink == "https://example1.com/unsub");
        Assert.Contains(result, r => r.SenderEmail == "sender2@example.com" && r.UnsubscribeLink == "https://example2.com/unsub");
        Assert.Contains(result, r => r.SenderEmail == "sender3@example.com" && r.UnsubscribeLink == null);
    }

    [Fact]
    public async Task GetSenderUnsubscribeLinksAsync_WithEmailInAngleBrackets_ExtractsEmailCorrectly()
    {
        var emails = new List<EmailMessage>
        {
            new EmailMessage 
            { 
                From = "John Doe <john@example.com>", 
                Subject = "Test", 
                Body = "Unsubscribe: https://example.com/unsub", 
                Date = DateTime.Now 
            }
        };

        _mockEmailService.Setup(x => x.GetEmailsFromDateRangeAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<Action<int, int>>()))
            .ReturnsAsync(emails);

        _mockLinkExtractor.Setup(x => x.ExtractUnsubscribeLinkAsync(It.IsAny<string>()))
            .ReturnsAsync("https://example.com/unsub");

        var result = await _service.GetSenderUnsubscribeLinksAsync();

        Assert.Single(result);
        Assert.Equal("john@example.com", result[0].SenderEmail);
    }

    [Fact]
    public async Task GetSenderUnsubscribeLinksAsync_WithMixedCaseEmail_NormalizesToLowercase()
    {
        var emails = new List<EmailMessage>
        {
            new EmailMessage 
            { 
                From = "John@Example.COM", 
                Subject = "Test", 
                Body = "Unsubscribe link", 
                Date = DateTime.Now 
            }
        };

        _mockEmailService.Setup(x => x.GetEmailsFromDateRangeAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<Action<int, int>>()))
            .ReturnsAsync(emails);

        _mockLinkExtractor.Setup(x => x.ExtractUnsubscribeLinkAsync(It.IsAny<string>()))
            .ReturnsAsync("https://example.com/unsub");

        var result = await _service.GetSenderUnsubscribeLinksAsync();

        Assert.Single(result);
        Assert.Equal("john@example.com", result[0].SenderEmail);
    }

    [Fact]
    public async Task GetSenderUnsubscribeLinksAsync_ProcessesMostRecentEmailsFirst()
    {
        var emails = new List<EmailMessage>
        {
            new EmailMessage 
            { 
                From = "sender@example.com", 
                Subject = "Old", 
                Body = "Old link: https://old.com/unsub", 
                Date = DateTime.Now.AddDays(-10) 
            },
            new EmailMessage 
            { 
                From = "sender@example.com", 
                Subject = "Recent", 
                Body = "Recent link: https://recent.com/unsub", 
                Date = DateTime.Now.AddDays(-1) 
            }
        };

        _mockEmailService.Setup(x => x.GetEmailsFromDateRangeAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<Action<int, int>>()))
            .ReturnsAsync(emails);

        string? firstBodyProcessed = null;
        _mockLinkExtractor.Setup(x => x.ExtractUnsubscribeLinkAsync(It.IsAny<string>()))
            .ReturnsAsync((string body) =>
            {
                if (firstBodyProcessed == null)
                    firstBodyProcessed = body;
                
                if (body.Contains("https://recent.com/unsub"))
                    return "https://recent.com/unsub";
                if (body.Contains("https://old.com/unsub"))
                    return "https://old.com/unsub";
                return null;
            });

        var result = await _service.GetSenderUnsubscribeLinksAsync();

        Assert.Single(result);
        Assert.Equal("https://recent.com/unsub", result[0].UnsubscribeLink);
        Assert.Contains("Recent link", firstBodyProcessed);
    }
}
