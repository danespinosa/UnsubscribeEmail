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

        var (result, anchors) = await extractor.ExtractUnsubscribeLinkAsync(emailBody);

        Assert.NotNull(result);
        Assert.Contains("unsubscribe", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExtractUnsubscribeLinkAsync_WithHtmlLink_ReturnsLink()
    {
        var extractor = new Phi3UnsubscribeLinkExtractor(_mockLogger.Object, _mockConfiguration.Object);
        var emailBody = @"<html><body>Click <a href=""https://example.com/unsubscribe?id=123"">here</a> to unsubscribe</body></html>";

        var (result, anchors) = await extractor.ExtractUnsubscribeLinkAsync(emailBody);

        Assert.NotNull(result);
        Assert.Contains("unsubscribe", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExtractUnsubscribeLinkAsync_WithPreferencesLink_ReturnsLink()
    {
        var extractor = new Phi3UnsubscribeLinkExtractor(_mockLogger.Object, _mockConfiguration.Object);
        var emailBody = "Manage your preferences at https://example.com/preferences";

        var (result, anchors) = await extractor.ExtractUnsubscribeLinkAsync(emailBody);

        Assert.NotNull(result);
        Assert.Contains("preferences", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExtractUnsubscribeLinkAsync_WithOptOutLink_ReturnsLink()
    {
        var extractor = new Phi3UnsubscribeLinkExtractor(_mockLogger.Object, _mockConfiguration.Object);
        var emailBody = "To opt-out, go to https://example.com/opt-out";

        var (result, anchors) = await extractor.ExtractUnsubscribeLinkAsync(emailBody);

        Assert.NotNull(result);
        Assert.Contains("opt-out", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExtractUnsubscribeLinkAsync_WithOptOutNoHyphenLink_ReturnsLink()
    {
        var extractor = new Phi3UnsubscribeLinkExtractor(_mockLogger.Object, _mockConfiguration.Object);
        var emailBody = "Visit https://example.com/optout to stop receiving emails";

        var (result, anchors) = await extractor.ExtractUnsubscribeLinkAsync(emailBody);

        Assert.NotNull(result);
        Assert.Contains("optout", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExtractUnsubscribeLinkAsync_WithNoUnsubscribeLink_ReturnsNull()
    {
        var extractor = new Phi3UnsubscribeLinkExtractor(_mockLogger.Object, _mockConfiguration.Object);
        var emailBody = "This is a regular email with no unsubscribe link. Visit https://example.com for more info.";

        var (result, anchors) = await extractor.ExtractUnsubscribeLinkAsync(emailBody);

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

        var (result, anchors) = await extractor.ExtractUnsubscribeLinkAsync(emailBody);

        Assert.NotNull(result);
        Assert.Contains("unsubscribe", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExtractUnsubscribeLinkAsync_WithCaseInsensitive_ReturnsLink()
    {
        var extractor = new Phi3UnsubscribeLinkExtractor(_mockLogger.Object, _mockConfiguration.Object);
        var emailBody = "To UNSUBSCRIBE, visit: https://example.com/UNSUBSCRIBE";

        var (result, anchors) = await extractor.ExtractUnsubscribeLinkAsync(emailBody);

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

        var (result, anchors) = await extractor.ExtractUnsubscribeLinkAsync(emailBody);

        Assert.NotNull(result);
        Assert.Contains("preferences", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExtractUnsubscribeLinkAsync_WithQueryParameters_PreservesParameters()
    {
        var extractor = new Phi3UnsubscribeLinkExtractor(_mockLogger.Object, _mockConfiguration.Object);
        var emailBody = "Unsubscribe: https://example.com/unsubscribe?email=user@test.com&token=abc123";

        var result = await extractor.ExtractUnsubscribeLinkAsync(emailBody);

        Assert.Contains("email=user@test.com", result.UnsubscribeLink);
        Assert.Contains("token=abc123", result.UnsubscribeLink);
    }

    [Fact]
    public async Task ExtractUnsubscribeLinkAsync_WithEncodedHtml_ReturnsLink()
    {
        var extractor = new Phi3UnsubscribeLinkExtractor(_mockLogger.Object, _mockConfiguration.Object);
        var emailBody = @"<a href=""https://example.com/unsubscribe?email=test%40example.com"">Unsubscribe</a>";

        var (result, anchors) = await extractor.ExtractUnsubscribeLinkAsync(emailBody);

        Assert.NotNull(result);
        Assert.Contains("unsubscribe", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExtractUnsubscribeLinkAsync_WithEmptyBody_ReturnsNull()
    {
        var extractor = new Phi3UnsubscribeLinkExtractor(_mockLogger.Object, _mockConfiguration.Object);
        var emailBody = "";

        var (result, anchors) = await extractor.ExtractUnsubscribeLinkAsync(emailBody);

        Assert.Null(result);
    }

    [Fact]
    public async Task ExtractUnsubscribeLinkAsync_WithHttpsAndHttp_PrefersHttps()
    {
        var extractor = new Phi3UnsubscribeLinkExtractor(_mockLogger.Object, _mockConfiguration.Object);
        var emailBody = "Unsubscribe: https://secure.example.com/unsubscribe or http://example.com/unsubscribe";

        var result = await extractor.ExtractUnsubscribeLinkAsync(emailBody);

        Assert.StartsWith("https://", result.UnsubscribeLink);
    }

    [Fact]
    public async Task ExtractUnsubscribeLinkAsync_WithAnchorTagContainingUnsubscribeText_ReturnsLink()
    {
        var extractor = new Phi3UnsubscribeLinkExtractor(_mockLogger.Object, _mockConfiguration.Object);
        var emailBody = @"<p>Thank you for your subscription.</p>
                         <a href=""https://example.com/remove"">Unsubscribe</a>";

        var result = await extractor.ExtractUnsubscribeLinkAsync(emailBody);

        Assert.Contains("example.com", result.UnsubscribeLink);
    }

    [Fact]
    public async Task ExtractUnsubscribeLinkAsync_WithAnchorTagContainingUnsubscribeTextCaseInsensitive_ReturnsLink()
    {
        var extractor = new Phi3UnsubscribeLinkExtractor(_mockLogger.Object, _mockConfiguration.Object);
        var emailBody = @"<a href=""https://example.com/stop"">UNSUBSCRIBE HERE</a>";

        var result = await extractor.ExtractUnsubscribeLinkAsync(emailBody);

        Assert.Contains("example.com", result.UnsubscribeLink);
    }

    [Fact]
    public async Task ExtractUnsubscribeLinkAsync_WithAnchorTagContainingClickToUnsubscribe_ReturnsLink()
    {
        var extractor = new Phi3UnsubscribeLinkExtractor(_mockLogger.Object, _mockConfiguration.Object);
        var emailBody = @"<div>
                         <a href=""https://newsletter.example.com/unsubscribe/123"">Click here to unsubscribe</a>
                         </div>";

        var (result, anchors) = await extractor.ExtractUnsubscribeLinkAsync(emailBody);

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

        var (result, anchors) = await extractor.ExtractUnsubscribeLinkAsync(emailBody);

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

        var (result, anchors) = await extractor.ExtractUnsubscribeLinkAsync(emailBody);

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

        Assert.Contains("example.com", result.UnsubscribeLink);
    }

    // ===== Tests for Anchor-Based Heuristic Detection =====

    [Fact]
    public async Task ExtractUnsubscribeLinkAsync_AnchorHeuristic_WithUnsubscribeInAnchorText_ReturnsLink()
    {
        // Test case where regex fails but anchor text contains "unsubscribe"
        var extractor = new Phi3UnsubscribeLinkExtractor(_mockLogger.Object, _mockConfiguration.Object);
        var emailBody = @"<html><body>
                         <p>Thank you for subscribing to our newsletter.</p>
                         <a href=""https://newsletter.example.com/remove?id=abc123"">Unsubscribe from this list</a>
                         </body></html>";

        var (result, anchors) = await extractor.ExtractUnsubscribeLinkAsync(emailBody);

        Assert.NotNull(result);
        Assert.Equal("https://newsletter.example.com/remove?id=abc123", result);
    }

    [Fact]
    public async Task ExtractUnsubscribeLinkAsync_AnchorHeuristic_WithUnsubscribeInHref_ReturnsLink()
    {
        // Test case where only href contains unsubscribe keyword
        var extractor = new Phi3UnsubscribeLinkExtractor(_mockLogger.Object, _mockConfiguration.Object);
        var emailBody = @"<html><body>
                         <p>Manage your email settings.</p>
                         <a href=""https://example.com/unsubscribe?user=123"">Click here</a>
                         </body></html>";

        var (result, anchors) = await extractor.ExtractUnsubscribeLinkAsync(emailBody);

        Assert.NotNull(result);
        Assert.Contains("unsubscribe", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("user=123", result);
    }

    [Fact]
    public async Task ExtractUnsubscribeLinkAsync_AnchorHeuristic_WithOptOutInHref_ReturnsLink()
    {
        // Test case where href contains "opt-out"
        var extractor = new Phi3UnsubscribeLinkExtractor(_mockLogger.Object, _mockConfiguration.Object);
        var emailBody = @"<html><body>
                         <p>Email notification settings.</p>
                         <a href=""https://example.com/opt-out?token=xyz"">Update settings</a>
                         </body></html>";

        var (result, anchors) = await extractor.ExtractUnsubscribeLinkAsync(emailBody);

        Assert.NotNull(result);
        Assert.Contains("opt-out", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExtractUnsubscribeLinkAsync_AnchorHeuristic_WithManagePreferencesInHref_ReturnsLink()
    {
        // Test case where href contains "preferences"
        var extractor = new Phi3UnsubscribeLinkExtractor(_mockLogger.Object, _mockConfiguration.Object);
        var emailBody = @"<html><body>
                         <a href=""https://mail.example.com/managepreferences?id=456"">Settings</a>
                         </body></html>";

        var (result, anchors) = await extractor.ExtractUnsubscribeLinkAsync(emailBody);

        Assert.NotNull(result);
        Assert.Contains("preferences", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExtractUnsubscribeLinkAsync_AnchorHeuristic_WithContextBeforeAnchor_ReturnsLink()
    {
        // Test case where context before anchor contains unsubscribe keyword
        var extractor = new Phi3UnsubscribeLinkExtractor(_mockLogger.Object, _mockConfiguration.Object);
        var emailBody = @"<html><body>
                         <p>If you no longer wish to receive these emails, please unsubscribe by visiting 
                         <a href=""https://example.com/stop-emails?id=789"">this link</a>.</p>
                         </body></html>";

        var (result, anchors) = await extractor.ExtractUnsubscribeLinkAsync(emailBody);

        Assert.NotNull(result);
        Assert.Contains("stop-emails", result);
        Assert.Equal("https://example.com/stop-emails?id=789", result);
    }

    [Fact]
    public async Task ExtractUnsubscribeLinkAsync_AnchorHeuristic_WithContextAfterAnchor_ReturnsLink()
    {
        // Test case where context after anchor contains opt-out keyword
        var extractor = new Phi3UnsubscribeLinkExtractor(_mockLogger.Object, _mockConfiguration.Object);
        var emailBody = @"<html><body>
                         <p>Click <a href=""https://example.com/leave?user=test"">here</a> to opt out of future communications.</p>
                         </body></html>";

        var (result, anchors) = await extractor.ExtractUnsubscribeLinkAsync(emailBody);

        Assert.NotNull(result);
        Assert.Equal("https://example.com/leave?user=test", result);
    }

    [Fact]
    public async Task ExtractUnsubscribeLinkAsync_AnchorHeuristic_WithEmailPreferencesInContext_ReturnsLink()
    {
        // Test case where context contains "email preferences"
        var extractor = new Phi3UnsubscribeLinkExtractor(_mockLogger.Object, _mockConfiguration.Object);
        var emailBody = @"<html><body>
                         <p>To update your email preferences, <a href=""https://example.com/settings?ref=email"">click here</a>.</p>
                         </body></html>";

        var (result, anchors) = await extractor.ExtractUnsubscribeLinkAsync(emailBody);

        Assert.NotNull(result);
        Assert.Equal("https://example.com/settings?ref=email", result);
    }

    [Fact]
    public async Task ExtractUnsubscribeLinkAsync_AnchorHeuristic_MultipleAnchors_PrefersUnsubscribeInText()
    {
        // Test case with multiple anchors where regex won't match but anchor heuristic will
        // Using anchor text that doesn't contain regex keywords
        var extractor = new Phi3UnsubscribeLinkExtractor(_mockLogger.Object, _mockConfiguration.Object);
        var emailBody = @"<html><body>
                         <a href=""https://example.com/settings?id=1"">Click to manage</a> | 
                         <a href=""https://example.com/remove?id=2"">Unsubscribe</a> | 
                         <a href=""https://example.com/contact"">Contact Us</a>
                         </body></html>";

        var result = await extractor.ExtractUnsubscribeLinkAsync(emailBody);

        // Should select the "Unsubscribe" link (priority 1 in anchor heuristics)
        Assert.Equal("https://example.com/remove?id=2", result.UnsubscribeLink);
    }

    [Fact]
    public async Task ExtractUnsubscribeLinkAsync_AnchorHeuristic_MultipleAnchors_PrefersUnsubscribeInHref()
    {
        // Test case with multiple anchors - should prefer "unsubscribe" in href when not in text
        var extractor = new Phi3UnsubscribeLinkExtractor(_mockLogger.Object, _mockConfiguration.Object);
        var emailBody = @"<html><body>
                         <a href=""https://example.com/settings"">Settings</a> | 
                         <a href=""https://example.com/unsubscribe?id=xyz"">Click here</a> | 
                         <a href=""https://example.com/help"">Help</a>
                         </body></html>";

        var result = await extractor.ExtractUnsubscribeLinkAsync(emailBody);

        // Should select the link with "unsubscribe" in href (priority 2)
        Assert.Equal("https://example.com/unsubscribe?id=xyz", result.UnsubscribeLink);
    }

    [Fact]
    public async Task ExtractUnsubscribeLinkAsync_AnchorHeuristic_MultipleAnchors_SelectsFirstDocumentOrder()
    {
        // Test case with multiple preference anchors - should select first in document order
        var extractor = new Phi3UnsubscribeLinkExtractor(_mockLogger.Object, _mockConfiguration.Object);
        var emailBody = @"<html><body>
                         <a href=""https://example.com/preferences1"">Manage Preferences</a>
                         <a href=""https://example.com/preferences2"">Email Preferences</a>
                         </body></html>";

        var result = await extractor.ExtractUnsubscribeLinkAsync(emailBody);

        // Should select first matching anchor
        Assert.Equal("https://example.com/preferences1", result.UnsubscribeLink);
    }

    [Fact]
    public async Task ExtractUnsubscribeLinkAsync_AnchorHeuristic_NoMatchingAnchors_ReturnsNull()
    {
        // Test case where anchors exist but none match unsubscribe criteria
        var extractor = new Phi3UnsubscribeLinkExtractor(_mockLogger.Object, _mockConfiguration.Object);
        var emailBody = @"<html><body>
                         <a href=""https://example.com/about"">About Us</a>
                         <a href=""https://example.com/products"">Products</a>
                         <a href=""https://example.com/contact"">Contact</a>
                         </body></html>";

        var result = await extractor.ExtractUnsubscribeLinkAsync(emailBody);

        // Should return null since no unsubscribe-related links found
        Assert.Null(result.UnsubscribeLink);
    }

    [Fact]
    public async Task ExtractUnsubscribeLinkAsync_AnchorHeuristic_IgnoresNonHttpAnchors()
    {
        // Test case where some anchors have non-HTTP hrefs (mailto, javascript, etc.)
        var extractor = new Phi3UnsubscribeLinkExtractor(_mockLogger.Object, _mockConfiguration.Object);
        var emailBody = @"<html><body>
                         <a href=""mailto:support@example.com"">Email Us</a>
                         <a href=""javascript:void(0)"">Action</a>
                         <a href=""https://example.com/unsubscribe"">Unsubscribe</a>
                         </body></html>";

        var result = await extractor.ExtractUnsubscribeLinkAsync(emailBody);

        // Should only consider HTTP/HTTPS links
        Assert.Equal("https://example.com/unsubscribe", result.UnsubscribeLink);
    }

    [Fact]
    public async Task ExtractUnsubscribeLinkAsync_AnchorHeuristic_HandlesLongContextWindows()
    {
        // Test case with long text to verify 100-char context window
        var extractor = new Phi3UnsubscribeLinkExtractor(_mockLogger.Object, _mockConfiguration.Object);
        var emailBody = @"<html><body>
                         <p>" + new string('x', 150) + @"</p>
                         <p>To unsubscribe from our mailing list, please " + new string('y', 80) + @" 
                         <a href=""https://example.com/leave"">click here</a> to complete the process.</p>
                         </body></html>";

        var (result, anchors) = await extractor.ExtractUnsubscribeLinkAsync(emailBody);

        Assert.NotNull(result);
        Assert.Equal("https://example.com/leave", result);
    }

    [Fact]
    public async Task ExtractUnsubscribeLinkAsync_AnchorHeuristic_CollectsAllAnchors()
    {
        // Test that multiple anchors are collected even when one is selected
        var extractor = new Phi3UnsubscribeLinkExtractor(_mockLogger.Object, _mockConfiguration.Object);
        var emailBody = @"<html><body>
                         <a href=""https://example.com/home"">Home</a>
                         <a href=""https://example.com/shop"">Shop</a>
                         <a href=""https://example.com/unsubscribe"">Unsubscribe</a>
                         <a href=""https://example.com/contact"">Contact</a>
                         </body></html>";

        var result = await extractor.ExtractUnsubscribeLinkAsync(emailBody);

        // Should successfully extract the unsubscribe link and anchors
        Assert.Contains("unsubscribe", result.UnsubscribeLink, StringComparison.OrdinalIgnoreCase);
        Assert.Collection(result.AllAnchors,
            anchor1 => Assert.Equal("https://example.com/home", anchor1),
            anchor2 => Assert.Equal("https://example.com/shop", anchor2),
            anchor3 => Assert.Equal("https://example.com/unsubscribe", anchor3),
            anchor4 => Assert.Equal("https://example.com/contact", anchor4)
        );
    }

    [Fact]
    public async Task ExtractUnsubscribeLinkAsync_WithMultipleAnchorsNoKeywords_ReturnsNull()
    {
        // Test that when no anchor matches unsubscribe criteria, null is returned
        // This also ensures anchors are collected for potential Phi3 model processing
        var extractor = new Phi3UnsubscribeLinkExtractor(_mockLogger.Object, _mockConfiguration.Object);
        var emailBody = @"<html><body>
                         <a href=""https://example.com/home"">Home</a>
                         <a href=""https://example.com/products"">Products</a>
                         <a href=""https://example.com/blog"">Blog</a>
                         </body></html>";

        var result = await extractor.ExtractUnsubscribeLinkAsync(emailBody);

        // Should return null when no unsubscribe-related anchors found
        // In a real scenario with Phi3 model, the model could process these anchors
        Assert.Null(result.UnsubscribeLink);
        Assert.Collection(result.AllAnchors,
            anchor1 => Assert.Equal("https://example.com/home", anchor1),
            anchor2 => Assert.Equal("https://example.com/products", anchor2),
            anchor3 => Assert.Equal("https://example.com/blog", anchor3)
        );
    }
}

