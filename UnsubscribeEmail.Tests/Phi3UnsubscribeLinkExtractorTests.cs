using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using UnsubscribeEmail.Services;

namespace UnsubscribeEmail.Tests;

public class Phi3UnsubscribeLinkExtractorTests
{
    private readonly Mock<ILogger<Phi3UnsubscribeLinkExtractor>> _mockLogger;
    private readonly Mock<IConfiguration> _mockConfiguration;

    public Phi3UnsubscribeLinkExtractorTests()
    {
        _mockLogger = new Mock<ILogger<Phi3UnsubscribeLinkExtractor>>();
        _mockConfiguration = new Mock<IConfiguration>();
        _mockConfiguration.Setup(x => x["Phi3ModelPath"]).Returns("./non-existent-model");
    }

    [Fact]
    public async Task ExtractUnsubscribeLinkAsync_WithSimpleUnsubscribeLink_ReturnsLink()
    {
        var extractor = new Phi3UnsubscribeLinkExtractor(_mockLogger.Object, _mockConfiguration.Object);
        var emailBody = "To unsubscribe, visit: https://example.com/unsubscribe";

        var result = await extractor.ExtractUnsubscribeLinkAsync(emailBody);

        Assert.NotNull(result);
        Assert.Contains("unsubscribe", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExtractUnsubscribeLinkAsync_WithHtmlLink_ReturnsLink()
    {
        var extractor = new Phi3UnsubscribeLinkExtractor(_mockLogger.Object, _mockConfiguration.Object);
        var emailBody = @"<html><body>Click <a href=""https://example.com/unsubscribe?id=123"">here</a> to unsubscribe</body></html>";

        var result = await extractor.ExtractUnsubscribeLinkAsync(emailBody);

        Assert.NotNull(result);
        Assert.Contains("unsubscribe", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExtractUnsubscribeLinkAsync_WithPreferencesLink_ReturnsLink()
    {
        var extractor = new Phi3UnsubscribeLinkExtractor(_mockLogger.Object, _mockConfiguration.Object);
        var emailBody = "Manage your preferences at https://example.com/preferences";

        var result = await extractor.ExtractUnsubscribeLinkAsync(emailBody);

        Assert.NotNull(result);
        Assert.Contains("preferences", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExtractUnsubscribeLinkAsync_WithOptOutLink_ReturnsLink()
    {
        var extractor = new Phi3UnsubscribeLinkExtractor(_mockLogger.Object, _mockConfiguration.Object);
        var emailBody = "To opt-out, go to https://example.com/opt-out";

        var result = await extractor.ExtractUnsubscribeLinkAsync(emailBody);

        Assert.NotNull(result);
        Assert.Contains("opt-out", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExtractUnsubscribeLinkAsync_WithOptOutNoHyphenLink_ReturnsLink()
    {
        var extractor = new Phi3UnsubscribeLinkExtractor(_mockLogger.Object, _mockConfiguration.Object);
        var emailBody = "Visit https://example.com/optout to stop receiving emails";

        var result = await extractor.ExtractUnsubscribeLinkAsync(emailBody);

        Assert.NotNull(result);
        Assert.Contains("optout", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExtractUnsubscribeLinkAsync_WithNoUnsubscribeLink_ReturnsNull()
    {
        var extractor = new Phi3UnsubscribeLinkExtractor(_mockLogger.Object, _mockConfiguration.Object);
        var emailBody = "This is a regular email with no unsubscribe link. Visit https://example.com for more info.";

        var result = await extractor.ExtractUnsubscribeLinkAsync(emailBody);

        Assert.Null(result);
    }

    [Fact]
    public async Task ExtractUnsubscribeLinkAsync_WithMultipleLinks_ReturnsFirstUnsubscribeLink()
    {
        var extractor = new Phi3UnsubscribeLinkExtractor(_mockLogger.Object, _mockConfiguration.Object);
        var emailBody = @"
            Visit our website: https://example.com
            Unsubscribe here: https://example.com/unsubscribe
            Another link: https://example.com/other
        ";

        var result = await extractor.ExtractUnsubscribeLinkAsync(emailBody);

        Assert.NotNull(result);
        Assert.Contains("unsubscribe", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExtractUnsubscribeLinkAsync_WithCaseInsensitive_ReturnsLink()
    {
        var extractor = new Phi3UnsubscribeLinkExtractor(_mockLogger.Object, _mockConfiguration.Object);
        var emailBody = "To UNSUBSCRIBE, visit: https://example.com/UNSUBSCRIBE";

        var result = await extractor.ExtractUnsubscribeLinkAsync(emailBody);

        Assert.NotNull(result);
        Assert.Contains("UNSUBSCRIBE", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExtractUnsubscribeLinkAsync_WithComplexHtmlEmail_ReturnsLink()
    {
        var extractor = new Phi3UnsubscribeLinkExtractor(_mockLogger.Object, _mockConfiguration.Object);
        var emailBody = @"
            <html>
            <head><title>Newsletter</title></head>
            <body>
                <div>
                    <p>Thank you for subscribing!</p>
                    <div style='font-size: 10px;'>
                        <a href='https://example.com/home'>Home</a> |
                        <a href='https://example.com/preferences'>Manage Preferences</a> |
                        <a href='https://example.com/contact'>Contact</a>
                    </div>
                </div>
            </body>
            </html>
        ";

        var result = await extractor.ExtractUnsubscribeLinkAsync(emailBody);

        Assert.NotNull(result);
        Assert.Contains("preferences", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExtractUnsubscribeLinkAsync_WithQueryParameters_PreservesParameters()
    {
        var extractor = new Phi3UnsubscribeLinkExtractor(_mockLogger.Object, _mockConfiguration.Object);
        var emailBody = "Unsubscribe: https://example.com/unsubscribe?email=user@test.com&token=abc123";

        var result = await extractor.ExtractUnsubscribeLinkAsync(emailBody);

        Assert.NotNull(result);
        Assert.Contains("email=user@test.com", result);
        Assert.Contains("token=abc123", result);
    }

    [Fact]
    public async Task ExtractUnsubscribeLinkAsync_WithEncodedHtml_ReturnsLink()
    {
        var extractor = new Phi3UnsubscribeLinkExtractor(_mockLogger.Object, _mockConfiguration.Object);
        var emailBody = @"<a href=""https://example.com/unsubscribe?email=test%40example.com"">Unsubscribe</a>";

        var result = await extractor.ExtractUnsubscribeLinkAsync(emailBody);

        Assert.NotNull(result);
        Assert.Contains("unsubscribe", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExtractUnsubscribeLinkAsync_WithEmptyBody_ReturnsNull()
    {
        var extractor = new Phi3UnsubscribeLinkExtractor(_mockLogger.Object, _mockConfiguration.Object);
        var emailBody = "";

        var result = await extractor.ExtractUnsubscribeLinkAsync(emailBody);

        Assert.Null(result);
    }

    [Fact]
    public async Task ExtractUnsubscribeLinkAsync_WithHttpsAndHttp_PrefersHttps()
    {
        var extractor = new Phi3UnsubscribeLinkExtractor(_mockLogger.Object, _mockConfiguration.Object);
        var emailBody = "Unsubscribe: https://secure.example.com/unsubscribe or http://example.com/unsubscribe";

        var result = await extractor.ExtractUnsubscribeLinkAsync(emailBody);

        Assert.NotNull(result);
        Assert.StartsWith("https://", result);
    }

    [Fact]
    public async Task ExtractUnsubscribeLinkAsync_WithAnchorTagContainingUnsubscribeText_ReturnsLink()
    {
        var extractor = new Phi3UnsubscribeLinkExtractor(_mockLogger.Object, _mockConfiguration.Object);
        var emailBody = @"<p>Thank you for your subscription.</p>
                         <a href=""https://example.com/remove"">Unsubscribe</a>";

        var result = await extractor.ExtractUnsubscribeLinkAsync(emailBody);

        Assert.NotNull(result);
        Assert.Contains("example.com", result);
    }

    [Fact]
    public async Task ExtractUnsubscribeLinkAsync_WithAnchorTagContainingUnsubscribeTextCaseInsensitive_ReturnsLink()
    {
        var extractor = new Phi3UnsubscribeLinkExtractor(_mockLogger.Object, _mockConfiguration.Object);
        var emailBody = @"<a href=""https://example.com/stop"">UNSUBSCRIBE HERE</a>";

        var result = await extractor.ExtractUnsubscribeLinkAsync(emailBody);

        Assert.NotNull(result);
        Assert.Contains("example.com", result);
    }

    [Fact]
    public async Task ExtractUnsubscribeLinkAsync_WithAnchorTagContainingClickToUnsubscribe_ReturnsLink()
    {
        var extractor = new Phi3UnsubscribeLinkExtractor(_mockLogger.Object, _mockConfiguration.Object);
        var emailBody = @"<div>
                         <a href=""https://newsletter.example.com/unsubscribe/123"">Click here to unsubscribe</a>
                         </div>";

        var result = await extractor.ExtractUnsubscribeLinkAsync(emailBody);

        Assert.NotNull(result);
        Assert.Contains("unsubscribe", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExtractUnsubscribeLinkAsync_WithMultipleLinksButOnlyOneUnsubscribe_ReturnsCorrectLink()
    {
        var extractor = new Phi3UnsubscribeLinkExtractor(_mockLogger.Object, _mockConfiguration.Object);
        var emailBody = @"<html>
                         <body>
                         <a href=""https://example.com/home"">Visit our website</a>
                         <a href=""https://example.com/shop"">Shop now</a>
                         <a href=""https://example.com/unsubscribe?id=456"">Unsubscribe from this list</a>
                         </body>
                         </html>";

        var result = await extractor.ExtractUnsubscribeLinkAsync(emailBody);

        Assert.NotNull(result);
        Assert.Contains("unsubscribe", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("id=456", result);
    }

    [Fact]
    public async Task ExtractUnsubscribeLinkAsync_WithStyledAnchorTagContainingUnsubscribe_ReturnsLink()
    {
        var extractor = new Phi3UnsubscribeLinkExtractor(_mockLogger.Object, _mockConfiguration.Object);
        var emailBody = @"<a href=""https://example.com/unsubscribe"" style=""color: gray; font-size: 10px;"">
                         Unsubscribe
                         </a>";

        var result = await extractor.ExtractUnsubscribeLinkAsync(emailBody);

        Assert.NotNull(result);
        Assert.Contains("unsubscribe", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExtractUnsubscribeLinkAsync_WithAnchorContainingUnsubscribeInSpan_ReturnsLink()
    {
        var extractor = new Phi3UnsubscribeLinkExtractor(_mockLogger.Object, _mockConfiguration.Object);
        var emailBody = @"<a href=""https://mail.example.com/u/abc123"">
                         <span>Unsubscribe</span>
                         </a>";

        var result = await extractor.ExtractUnsubscribeLinkAsync(emailBody);

        Assert.NotNull(result);
        Assert.Contains("example.com", result);
    }
}
