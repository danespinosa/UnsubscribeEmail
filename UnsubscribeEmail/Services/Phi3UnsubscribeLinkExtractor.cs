using Microsoft.ML.OnnxRuntimeGenAI;
using System.Text.RegularExpressions;

namespace UnsubscribeEmail.Services;

public interface IUnsubscribeLinkExtractor
{
    Task<string?> ExtractUnsubscribeLinkAsync(string emailBody);
}

/// <summary>
/// Represents an anchor tag candidate with surrounding context for heuristic evaluation.
/// </summary>
/// <remarks>
/// This class stores information about an anchor tag found in the email body, including:
/// - The href URL
/// - The visible anchor text
/// - Context window (100 characters before and after the anchor)
/// - Position in the original document
/// 
/// This data is used by the anchor-based heuristic detection to identify unsubscribe links
/// when regex-based extraction fails.
/// </remarks>
internal class AnchorCandidate
{
    public required string Href { get; init; }
    public required string AnchorText { get; init; }
    public required string ContextBefore { get; init; }
    public required string ContextAfter { get; init; }
    public required int Position { get; init; }
}

/// <summary>
/// Extracts unsubscribe links from email content using a three-stage detection approach:
/// anchor-based heuristics, regex patterns, and AI-powered analysis.
/// </summary>
/// <remarks>
/// This extractor implements a progressive enhancement strategy for finding unsubscribe links:
/// 
/// 1. **Anchor-based heuristics** (Stage 1): Extracts all anchor tags with 100-character
///    context windows and evaluates them using keyword-based heuristics. Provides comprehensive coverage
///    by analyzing anchors with surrounding context.
/// 
/// 2. **Regex-based extraction** (Stage 2): If heuristics fail, uses fast pattern matching for common 
///    unsubscribe link patterns. Catches patterns that may be missed by the heuristic approach.
/// 
/// 3. **Phi-3 AI model** (Stage 3): As a final fallback, uses Microsoft's Phi-3 language model to
///    intelligently extract links from complex or obfuscated HTML structures.
/// 
/// The anchor-based heuristic stage (Stage 1) provides enhanced detection accuracy by:
/// - Collecting all anchors with surrounding context for better understanding
/// - Using deterministic keyword-based evaluation
/// - Implementing priority-based selection when multiple candidates exist
/// - Providing comprehensive detection before falling back to simpler patterns
/// </remarks>
public class Phi3UnsubscribeLinkExtractor : IUnsubscribeLinkExtractor
{
    private readonly ILogger<Phi3UnsubscribeLinkExtractor> _logger;
    private readonly string _modelPath;
    private Model? _model;
    private Tokenizer? _tokenizer;
    private bool _modelInitialized = false;

    // Regex patterns for anchor tag and URL matching
    private static readonly Regex AnchorTagRegex = new(
        @"<a[^>]+href=[""']([^""']+)[""'][^>]*>(.*?)</a>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex UnsubscribeKeywordsRegex = new(
        @"unsubscribe|opt-out|optout|preferences",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex UrlRegex = new(
        @"https?://[^\s<>""']+",
        RegexOptions.Compiled);

    // Keywords for unsubscribe link detection (used across multiple methods)
    private static readonly string[] UnsubscribeKeywords = 
    {
        "unsubscribe",
        "opt out",
        "opt-out",
        "optout",  // Added for consistency with regex patterns
        "manage preferences",
        "email preferences",
        "update preferences",
        "no longer wish to receive"
    };

    // Pre-computed keyword variations for URL matching (includes no-space and hyphenated versions)
    // This avoids repeated string operations in loops
    private static readonly string[] UnsubscribeKeywordVariations = UnsubscribeKeywords
        .Where(k => !k.Contains(" "))  // Keep single-word keywords as-is
        .Concat(UnsubscribeKeywords
            .Where(k => k.Contains(" "))
            .SelectMany(k => new[] 
            { 
                k.Replace(" ", ""),      // "opt out" -> "optout"
                k.Replace(" ", "-"),     // "opt out" -> "opt-out"
                k                        // Keep original for matching
            }))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    // Common action phrases in anchor text (used across multiple methods)
    private static readonly string[] ActionPhrases = 
    {
        "click here",
        "here",
        "click",
        "tap here",
        "this link",
        "follow this link",
        "tap"
    };

    private static readonly Regex[] UnsubscribeLinkPatterns = 
    {
        new Regex(@"https?://[^\s<>""']+unsubscribe[^\s<>""']*", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new Regex(@"https?://[^\s<>""']+/preferences[^\s<>""']*", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new Regex(@"https?://[^\s<>""']+/opt-out[^\s<>""']*", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new Regex(@"https?://[^\s<>""']+/optout[^\s<>""']*", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new Regex(@"<a[^>]+href=[""']([^""']+unsubscribe[^""']*)[""'][^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new Regex(@"<a[^>]+href=[""']([^""']+/preferences[^""']*)[""'][^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        // Match pattern: "To unsubscribe ... please <a href="...">click here</a>"
        new Regex(@"(?:to\s+)?unsubscribe[^<]{0,150}?<a[^>]+href=[""']([^""']+)[""'][^>]*>(?:click\s+here|here|unsubscribe)", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline),
        // Match pattern: "unsubscribe from [something], please <a>click here</a>"
        new Regex(@"unsubscribe\s+from[^<]{0,150}?please\s*<a[^>]+href=[""']([^""']+)[""'][^>]*>(?:click[^<]*here|here)", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline),
    };

    public Phi3UnsubscribeLinkExtractor(ILogger<Phi3UnsubscribeLinkExtractor> logger, IConfiguration configuration)
    {
        _logger = logger;
        _modelPath = configuration["Phi3ModelPath"] ?? "./phi3-model";
    }

    private async Task InitializeModelAsync()
    {
        if (_modelInitialized)
        {
            return;
        }

        _modelInitialized = true;

        try
        {
            // Check if model exists
            if (!Directory.Exists(_modelPath))
            {
                _logger.LogWarning($"Phi3 model not found at {_modelPath}. Using fallback extraction method.");
                return;
            }

            _logger.LogInformation($"Loading Phi3 model from {_modelPath}");
            _model = new Model(_modelPath);
            _tokenizer = new Tokenizer(_model);
            _logger.LogInformation("Phi3 model loaded successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading Phi3 model. Using fallback extraction method.");
        }
        
        await Task.CompletedTask;
    }

    /// <summary>
    /// Extracts unsubscribe links from email body using a three-stage detection approach.
    /// </summary>
    /// <param name="emailBody">The raw HTML or text content of the email.</param>
    /// <returns>
    /// The unsubscribe URL if found, or null if no unsubscribe link could be detected.
    /// </returns>
    /// <remarks>
    /// This method uses a three-stage detection strategy:
    /// 
    /// 1. **Anchor-based heuristics** (comprehensive): Extracts all anchor 
    ///    tags with surrounding context (100 chars before/after) and evaluates them using 
    ///    keyword-based heuristics. Selection priority:
    ///    - Anchor text contains "unsubscribe" (highest priority)
    ///    - Href contains unsubscribe-related keywords
    ///    - Anchor text contains other keywords (opt-out, preferences, etc.)
    ///    - Surrounding context contains keywords with actionable anchor text
    /// 
    /// 2. **Regex-based extraction** (fallback): If heuristics fail, searches for common unsubscribe patterns 
    ///    including anchor tags with keywords like "unsubscribe", "opt-out", "preferences" 
    ///    in the text or href attributes.
    /// 
    /// 3. **Phi-3 AI model** (final fallback): If both heuristics and regex fail, uses the 
    ///    Phi-3 language model for intelligent extraction from complex HTML structures.
    /// 
    /// All URLs are validated before returning to ensure they are well-formed.
    /// </remarks>
    public async Task<string?> ExtractUnsubscribeLinkAsync(string emailBody)
    {
        // Step 1: Try anchor-based heuristics first (comprehensive detection)
        _logger.LogInformation("Attempting anchor-based heuristics...");
        var (anchorLink, allAnchors) = ExtractUnsubscribeLinkWithAnchorHeuristics(emailBody);
        
        if (!string.IsNullOrEmpty(anchorLink) && IsValidUrl(anchorLink))
        {
            _logger.LogInformation($"Anchor-based heuristic extraction successful: {anchorLink}");
            return anchorLink;
        }
        else
        {
            _logger.LogInformation("Anchor-based heuristic extraction failed, attempting regex-based extraction...");
        }

        // Step 2: Try regex-based extraction (fallback for patterns not caught by heuristics)
        var regexLink = ExtractUnsubscribeLinkFallback(emailBody);
        
        if (!string.IsNullOrEmpty(regexLink))
        {
            // Validate that the URL is well-formed
            if (IsValidUrl(regexLink))
            {
                _logger.LogInformation($"Regex extraction successful: {regexLink}");
                return regexLink;
            }
            else
            {
                _logger.LogWarning($"Regex found malformed URL: {regexLink}, falling back to Phi3 model...");
            }
        }
        else
        {
            _logger.LogInformation("Regex extraction failed, falling back to Phi3 model...");
        }

        // Step 3: Fallback to Phi3 model if regex and anchor heuristics didn't find anything
        await InitializeModelAsync();

        // If model is not available, return the regex result even if malformed (better than nothing)
        if (_model == null || _tokenizer == null)
        {
            _logger.LogWarning("Phi3 model not available, returning regex result or null");
            return regexLink; // May be null or malformed, but it's the best we have
        }

        try
        {
            // If we have collected anchors from Step 1, use them for model processing
            // Otherwise, fall back to extracting context snippets from the full email body
            if (allAnchors.Count > 0)
            {
                _logger.LogInformation($"Processing {allAnchors.Count} anchor(s) with Phi3 model...");
                var link = await ExtractFromAnchorsWithModelAsync(allAnchors);
                if (!string.IsNullOrEmpty(link) && IsValidUrl(link))
                {
                    return link;
                }
            }
            
            // Fallback: Try multiple context windows to increase chances of finding the link
            var contextSnippets = ExtractMultipleUnsubscribeContexts(emailBody);
            
            // If no unsubscribe context found, return the regex result (even if malformed)
            if (contextSnippets.Count == 0)
            {
                _logger.LogInformation("No unsubscribe context found for model processing");
                return regexLink;
            }

            _logger.LogInformation($"Processing {contextSnippets.Count} context(s) with Phi3 model...");

            // Try each context until we find a valid link
            foreach (var contextSnippet in contextSnippets)
            {
                // Create prompt for the model with better instructions
                var prompt = $@"
You are an email parser that extracts unsubscribe links from HTML emails.
Find the unsubscribe link in this email HTML to parse. The link may be:
- In an <a href=""""> tag after text like 'To unsubscribe', 'no longer wish to receive', or 'opt-out'
- In an <a> tag where the link text says 'click here', 'here', 'unsubscribe', or similar
- A complete URL in the href attribute starting with http:// or https://
- The href may contain query parameters and be very long

Example patterns:
- To unsubscribe please <a href=""https://example.com/unsub?id=123"">click here</a>
- Click <a href=""https://example.com/optout"">here</a> to unsubscribe
- <a href=""https://unsubscribe.example.com"">Unsubscribe</a>

Extract ONLY the complete URL from the href attribute. Return the full URL including all parameters without the <a> tag. If no unsubscribe link found, return NONE.
Example Outputs:
 https://example.com/unsub?id=123
 https://example.com/optout
 https://unsubscribe.example.com
 NONE
Dont write code or return code just return the URL that you found. Also, returning SafeLink URL is expected.

Email HTML to parse:
{contextSnippet}";

                var sequences = _tokenizer.Encode(prompt);
                
                using var generatorParams = new GeneratorParams(_model);
                // max_length is total tokens (input + output), increase to handle larger context
                generatorParams.SetSearchOption("max_length", 2048);
                generatorParams.SetSearchOption("min_length", 10); // Ensure some output
                generatorParams.SetSearchOption("do_sample", false); // Deterministic output
                generatorParams.SetSearchOption("top_k", 1); // Greedy decoding for consistency
                
                using var generator = new Generator(_model, generatorParams);
                generator.AppendTokenSequences(sequences);
                
                // Generate response
                while (!generator.IsDone())
                {
                    generator.GenerateNextToken();
                }

                var outputSequences = generator.GetSequence(0);
                var output = _tokenizer.Decode(outputSequences);
                
                // Extract URL from output
                var link = ExtractUrlFromText(output, prompt);
                if (!string.IsNullOrEmpty(link))
                {
                    // Validate the URL from model
                    if (IsValidUrl(link))
                    {
                        _logger.LogInformation($"Phi3 model extraction successful: {link}");
                        return link;
                    }
                    else
                    {
                        _logger.LogInformation($"Phi3 model extracted invalid URL: {link}");
                    }
                }
                else
                {
                    _logger.LogInformation($"Phi3 model did not return a valid URL for context snippet");
                }
            }
            
            // Fall back to regex result if model didn't find a valid URL in any context
            _logger.LogInformation("No valid URL found in any context, falling back to regex result");
            return regexLink;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error using Phi3 model for extraction, falling back to regex");
            return regexLink;
        }
    }

    /// <summary>
    /// Processes a list of anchor candidates with the Phi3 model to select the most likely unsubscribe link.
    /// </summary>
    private async Task<string?> ExtractFromAnchorsWithModelAsync(List<AnchorCandidate> anchors)
    {
        if (_model == null || _tokenizer == null)
        {
            return null;
        }

        // Build a formatted list of anchors for the model
        var anchorList = new System.Text.StringBuilder();
        for (int i = 0; i < anchors.Count; i++)
        {
            anchorList.AppendLine($"Anchor {i + 1}:");
            anchorList.AppendLine($"  URL: {anchors[i].Href}");
            anchorList.AppendLine($"  Link Text: {anchors[i].AnchorText}");
            anchorList.AppendLine($"  Context Before: {anchors[i].ContextBefore}");
            anchorList.AppendLine($"  Context After: {anchors[i].ContextAfter}");
            anchorList.AppendLine();
        }

        // Create prompt for the model
        var prompt = $@"
You are an email parser that identifies unsubscribe links from a list of anchor tags extracted from an HTML email.

Below is a list of {anchors.Count} anchor tag(s) found in the email. Each anchor includes:
- URL: The href attribute value
- Link Text: The visible text of the anchor
- Context Before: Text appearing before the anchor (up to 100 characters)
- Context After: Text appearing after the anchor (up to 100 characters)

Your task is to select ONLY ONE anchor that is most likely the unsubscribe link. Look for:
- URLs or link text containing words like 'unsubscribe', 'opt-out', 'optout', 'preferences', 'manage preferences'
- Context mentioning unsubscribe, opt-out, stop receiving emails, manage settings
- Link text like 'click here', 'here', 'this link' when context mentions unsubscribing

Return ONLY the complete URL (the href value) of the selected anchor. Do not include the anchor number or any explanation.
If none of the anchors appear to be an unsubscribe link, return NONE.

Anchors:
{anchorList}

Selected unsubscribe URL:";

        try
        {
            var sequences = _tokenizer.Encode(prompt);
            
            using var generatorParams = new GeneratorParams(_model);
            generatorParams.SetSearchOption("max_length", 2048);
            generatorParams.SetSearchOption("min_length", 10);
            generatorParams.SetSearchOption("do_sample", false);
            generatorParams.SetSearchOption("top_k", 1);
            
            using var generator = new Generator(_model, generatorParams);
            generator.AppendTokenSequences(sequences);
            
            // Generate response
            while (!generator.IsDone())
            {
                generator.GenerateNextToken();
            }

            var outputSequences = generator.GetSequence(0);
            var output = _tokenizer.Decode(outputSequences);
            
            // Extract URL from output
            var link = ExtractUrlFromText(output, prompt);
            if (!string.IsNullOrEmpty(link))
            {
                _logger.LogInformation($"Phi3 model selected anchor with URL: {link}");
                return link;
            }
            else
            {
                _logger.LogInformation("Phi3 model did not select a valid unsubscribe anchor");
                return null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing anchors with Phi3 model");
            return null;
        }
    }

    private List<string> ExtractMultipleUnsubscribeContexts(string emailBody)
    {
        var contexts = new List<string>();
        
        // Find all occurrences of keywords and extract contexts around them
        var keywordPositions = new List<(int position, string keyword)>();
        
        foreach (var keyword in UnsubscribeKeywords)
        {
            var index = 0;
            while ((index = emailBody.IndexOf(keyword, index, StringComparison.OrdinalIgnoreCase)) != -1)
            {
                keywordPositions.Add((index, keyword));
                index += keyword.Length;
            }
        }
        
        if (keywordPositions.Count == 0)
        {
            return contexts;
        }
        
        // Sort by position
        keywordPositions.Sort((a, b) => a.position.CompareTo(b.position));
        
        // Take the first 3 occurrences to avoid processing too much
        var positionsToProcess = keywordPositions.Take(3).ToList();
        
        foreach (var (position, keyword) in positionsToProcess)
        {
            // Try to find the nearest <a> tag (either before or after the keyword)
            var anchorContext = FindNearestAnchorTag(emailBody, position);
            
            if (!string.IsNullOrEmpty(anchorContext))
            {
                _logger.LogInformation($"Extracted anchor tag context around '{keyword}' at position {position}");
                contexts.Add(anchorContext);
            }
            else
            {
                // Fallback to extracting 1000 chars before and after the keyword
                var startPos = Math.Max(0, position - 1000);
                var endPos = Math.Min(emailBody.Length, position + keyword.Length + 1000);
                
                var length = endPos - startPos;
                var context = emailBody.Substring(startPos, length);
                
                _logger.LogInformation($"Extracted context snippet #{contexts.Count + 1} (length: {context.Length}) centered around '{keyword}' at position {position}");
                
                contexts.Add(context);
            }
        }
        
        return contexts;
    }

    private string FindNearestAnchorTag(string html, int keywordPosition)
    {
        const int searchRadius = 2000; // Search within 500 chars before and after keyword
        
        var searchStart = Math.Max(0, keywordPosition - searchRadius);
        var searchEnd = Math.Min(html.Length, keywordPosition + searchRadius);
        
        // Find <a> tags before and after the keyword position
        var beforeSection = html.Substring(searchStart, keywordPosition - searchStart);
        var afterSection = html.Substring(keywordPosition, searchEnd - keywordPosition);
        
        // Look for <a> tag that contains or is near the keyword
        // First, try to find <a> tag after the keyword (common pattern: "To unsubscribe... <a href...>click here</a>")
        var aTagAfterMatch = Regex.Match(afterSection, @"<a\s+[^>]*href\s*=\s*[""']([^""']+)[""'][^>]*>.*?</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (aTagAfterMatch.Success)
        {
            // Extract more context including the keyword
            var fullStart = Math.Max(0, keywordPosition - 200);
            var fullEnd = Math.Min(html.Length, keywordPosition + aTagAfterMatch.Index + aTagAfterMatch.Length + 100);
            return html.Substring(fullStart, fullEnd - fullStart);
        }
        
        // Second, try to find <a> tag before the keyword (pattern: "<a>unsubscribe</a>")
        var aTagBeforeMatches = Regex.Matches(beforeSection, @"<a\s+[^>]*href\s*=\s*[""']([^""']+)[""'][^>]*>.*?</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (aTagBeforeMatches.Count > 0)
        {
            // Get the last <a> tag before the keyword
            var lastMatch = aTagBeforeMatches[aTagBeforeMatches.Count - 1];
            var tagStart = searchStart + lastMatch.Index;
            var tagEnd = tagStart + lastMatch.Length;
            
            // Extract context including some padding
            var fullStart = Math.Max(0, tagStart - 100);
            var fullEnd = Math.Min(html.Length, tagEnd + 200);
            return html.Substring(fullStart, fullEnd - fullStart);
        }
        
        return null;
    }

    private string? ExtractUnsubscribeContext(string emailBody)
    {
        // Search for multiple keywords related to unsubscribing
        int bestIndex = -1;
        string? foundKeyword = null;
        
        // Find the first occurrence of any keyword
        foreach (var keyword in UnsubscribeKeywords)
        {
            var index = emailBody.IndexOf(keyword, StringComparison.OrdinalIgnoreCase);
            if (index != -1 && (bestIndex == -1 || index < bestIndex))
            {
                bestIndex = index;
                foundKeyword = keyword;
            }
        }
        
        if (bestIndex == -1 || foundKeyword == null)
        {
            // No keywords found, return null to trigger fallback
            return null;
        }

        // Calculate positions - we need more context AFTER the keyword
        // because often the link comes after phrases like "To unsubscribe, please click here"
        // Take 200 chars before and 1000 chars after the start of the keyword
        var startPos = Math.Max(0, bestIndex - 200);
        
        // End position: include more content after to capture links that come after the keyword
        var endPos = Math.Min(emailBody.Length, bestIndex + foundKeyword.Length + 1000);
        
        // Extract the context snippet
        var length = endPos - startPos;
        var context = emailBody.Substring(startPos, length);
        
        _logger.LogInformation($"Extracted context snippet (length: {context.Length}) starting at position {startPos}");
        
        return context;
    }

    private string? ExtractUnsubscribeLinkFallback(string emailBody)
    {
        // Step 1: Look for anchor tags with unsubscribe keywords in the text or href
        var anchorMatches = AnchorTagRegex.Matches(emailBody);
        
        foreach (Match anchorMatch in anchorMatches)
        {
            var anchorText = anchorMatch.Groups[2].Value;
            var href = anchorMatch.Groups[1].Value;
            
            // Check if anchor text contains unsubscribe-related keywords
            if (UnsubscribeKeywordsRegex.IsMatch(anchorText))
            {
                if (href.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || 
                    href.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation($"Found unsubscribe link in anchor tag text: {href}");
                    return href;
                }
            }
            
            // Check if href contains unsubscribe-related keywords
            if (UnsubscribeKeywordsRegex.IsMatch(href))
            {
                if (href.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || 
                    href.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation($"Found unsubscribe link in href: {href}");
                    return href;
                }
            }
        }
        
        // Step 2: Look for links near unsubscribe keywords (within ~500 characters)
        // This handles cases like: "If you would like to unsubscribe... <a>click here</a>"
        var keywordMatches = UnsubscribeKeywordsRegex.Matches(emailBody);
        
        foreach (Match keywordMatch in keywordMatches)
        {
            var keywordPosition = keywordMatch.Index;
            
            // Search for anchor tags within 500 characters after the keyword
            var searchStart = keywordPosition;
            var searchEnd = Math.Min(emailBody.Length, keywordPosition + 500);
            var searchText = emailBody.Substring(searchStart, searchEnd - searchStart);
            
            var nearbyAnchors = AnchorTagRegex.Matches(searchText);
            
            foreach (Match nearbyAnchor in nearbyAnchors)
            {
                var href = nearbyAnchor.Groups[1].Value;
                var anchorText = nearbyAnchor.Groups[2].Value.Trim().ToLower();
                
                // Look for common action phrases
                if (ActionPhrases.Any(phrase => anchorText.Contains(phrase)))
                {
                    if (href.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || 
                        href.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogInformation($"Found unsubscribe link near keyword (anchor text: '{anchorText}'): {href}");
                        return href;
                    }
                }
            }
        }

        // Step 3: Fallback method using regex to find unsubscribe links directly in URLs
        foreach (var regex in UnsubscribeLinkPatterns)
        {
            var match = regex.Match(emailBody);
            
            if (match.Success)
            {
                var link = match.Groups[match.Groups.Count > 1 ? 1 : 0].Value;
                _logger.LogInformation($"Found unsubscribe link using fallback method: {link}");
                return link;
            }
        }

        return null;
    }

    /// <summary>
    /// Extracts unsubscribe links using anchor-based heuristics.
    /// Collects all anchor tags with surrounding context and evaluates them based on unsubscribe-related keywords.
    /// Returns both the selected link and all collected anchors for potential fallback processing.
    /// </summary>
    private (string? selectedLink, List<AnchorCandidate> allCandidates) ExtractUnsubscribeLinkWithAnchorHeuristics(string emailBody)
    {
        // Step 1: Collect all anchor candidates with context
        var candidates = CollectAnchorCandidates(emailBody);
        
        if (candidates.Count == 0)
        {
            _logger.LogInformation("No anchor tags found in email body");
            return (null, candidates);
        }
        
        _logger.LogInformation($"Found {candidates.Count} anchor candidate(s) for heuristic evaluation");
        
        // Step 2: Evaluate and select the best candidate
        var selectedLink = SelectBestUnsubscribeCandidate(candidates);
        return (selectedLink, candidates);
    }

    /// <summary>
    /// Collects all anchor tags from the email body with surrounding context (100 chars before/after).
    /// </summary>
    private List<AnchorCandidate> CollectAnchorCandidates(string emailBody)
    {
        var candidates = new List<AnchorCandidate>();
        const int contextWindow = 100;
        
        // Match all anchor tags with href and text content
        var anchorMatches = AnchorTagRegex.Matches(emailBody);
        
        foreach (Match match in anchorMatches)
        {
            var href = match.Groups[1].Value.Trim();
            var anchorText = match.Groups[2].Value.Trim();
            var position = match.Index;
            
            // Only process anchors with valid HTTP/HTTPS hrefs
            if (!href.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && 
                !href.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            
            // Extract context before the anchor (100 chars or less)
            var contextStartBefore = Math.Max(0, position - contextWindow);
            var contextLengthBefore = position - contextStartBefore;
            var contextBefore = emailBody.Substring(contextStartBefore, contextLengthBefore);
            
            // Extract context after the anchor (100 chars or less)
            var anchorEnd = position + match.Length;
            var contextStartAfter = anchorEnd;
            var contextEndAfter = Math.Min(emailBody.Length, anchorEnd + contextWindow);
            var contextLengthAfter = contextEndAfter - contextStartAfter;
            var contextAfter = emailBody.Substring(contextStartAfter, contextLengthAfter);
            
            candidates.Add(new AnchorCandidate
            {
                Href = href,
                AnchorText = anchorText,
                ContextBefore = contextBefore,
                ContextAfter = contextAfter,
                Position = position
            });
        }
        
        return candidates;
    }

    /// <summary>
    /// Evaluates anchor candidates and selects the most likely unsubscribe link.
    /// Uses a deterministic priority-based selection strategy.
    /// </summary>
    private string? SelectBestUnsubscribeCandidate(List<AnchorCandidate> candidates)
    {
        // Priority 1: Anchor text directly contains "unsubscribe"
        foreach (var candidate in candidates)
        {
            if (candidate.AnchorText.Contains("unsubscribe", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation($"Selected anchor with 'unsubscribe' in text: {candidate.Href}");
                return candidate.Href;
            }
        }
        
        // Priority 2: Href contains unsubscribe-related keywords (using pre-computed variations)
        foreach (var candidate in candidates)
        {
            foreach (var keywordVariation in UnsubscribeKeywordVariations)
            {
                if (candidate.Href.Contains(keywordVariation, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation($"Selected anchor with '{keywordVariation}' in href: {candidate.Href}");
                    return candidate.Href;
                }
            }
        }
        
        // Priority 3: Anchor text contains other unsubscribe-related keywords
        foreach (var candidate in candidates)
        {
            foreach (var keyword in UnsubscribeKeywords)
            {
                if (candidate.AnchorText.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation($"Selected anchor with '{keyword}' in text: {candidate.Href}");
                    return candidate.Href;
                }
            }
        }
        
        // Priority 4: Context (before or after) contains unsubscribe-related keywords
        // Check each context separately to avoid unnecessary string allocations
        foreach (var candidate in candidates)
        {
            foreach (var keyword in UnsubscribeKeywords)
            {
                if (candidate.ContextBefore.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                    candidate.ContextAfter.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                {
                    // Additional check: anchor text should be actionable or empty
                    // Empty text is allowed for image-based links (e.g., <a><img src="unsubscribe.png"></a>)
                    var isActionPhrase = ActionPhrases.Any(phrase => 
                        candidate.AnchorText.Contains(phrase, StringComparison.OrdinalIgnoreCase));
                    
                    if (isActionPhrase || string.IsNullOrWhiteSpace(candidate.AnchorText))
                    {
                        _logger.LogInformation($"Selected anchor with '{keyword}' in context: {candidate.Href}");
                        return candidate.Href;
                    }
                }
            }
        }
        
        _logger.LogInformation("No anchor candidates matched unsubscribe-related criteria");
        return null;
    }

    private bool IsValidUrl(string url)
    {
        // Check if URL is null, empty, or whitespace
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        // Check if URL starts with http:// or https://
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Try to parse as a valid URI
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uriResult))
        {
            return false;
        }

        // Check that the URI has a valid host (not empty)
        if (string.IsNullOrWhiteSpace(uriResult.Host))
        {
            return false;
        }

        // Check that host contains at least one dot (e.g., example.com)
        if (!uriResult.Host.Contains('.'))
        {
            return false;
        }

        // Additional validation: Check for common malformed patterns
        // - URL shouldn't end with incomplete parameters like "?=" or "&="
        if (url.EndsWith("?=") || url.EndsWith("&=") || url.EndsWith("?") || url.EndsWith("&"))
        {
            return false;
        }

        // - URL shouldn't have spaces (should be encoded as %20)
        if (url.Contains(' '))
        {
            return false;
        }

        return true;
    }

    private string? ExtractUrlFromText(string text, string prompt)
    {
        // compare text to prompt to remove any echoed prompt
        if (text.StartsWith(prompt, StringComparison.OrdinalIgnoreCase))
        {
            text = text.Substring(prompt.Length);
        }

        // if text contains [response]: take the text after that until the first carriage return
        var responseIndex = text.IndexOf("[response]:", StringComparison.OrdinalIgnoreCase);
        // If found, extract text after it until the first line break or carriage return
        if (responseIndex != -1)
        {
            text = text.Substring(responseIndex + "[response]:".Length);
            var lineEndIndex = text.IndexOfAny(new[] { '\r', '\n' });
            if (lineEndIndex != -1)
            {
                text = text.Substring(0, lineEndIndex);
            }
        }

        // Remove the original prompt from the output if present
        var assistantIndex = text.IndexOf("<|assistant|>", StringComparison.OrdinalIgnoreCase);
        if (assistantIndex != -1)
        {
            text = text.Substring(assistantIndex + "<|assistant|>".Length);
        }
        
        // Clean up the response
        text = text.Trim();
        
        // Check if model explicitly said no link found
        if (text.Contains("NONE", StringComparison.OrdinalIgnoreCase) || 
            text.Contains("no link", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        
        // Extract URL from the model output - try to find the cleanest URL
        var matches = UrlRegex.Matches(text);
        
        if (matches.Count > 0)
        {
            // Return the first valid-looking URL
            foreach (Match match in matches)
            {
                var url = match.Value;
                // Remove trailing punctuation that might not be part of the URL
                url = url.TrimEnd('.', ',', ';', ')', ']', '}');
                
                if (IsValidUrl(url))
                {
                    return url;
                }
            }
        }

        return null;
    }
}
