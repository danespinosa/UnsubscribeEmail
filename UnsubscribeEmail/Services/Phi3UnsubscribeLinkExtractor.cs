using Microsoft.ML.OnnxRuntimeGenAI;
using System.Text.RegularExpressions;

namespace UnsubscribeEmail.Services;

public interface IUnsubscribeLinkExtractor
{
    Task<string?> ExtractUnsubscribeLinkAsync(string emailBody);
}

public class Phi3UnsubscribeLinkExtractor : IUnsubscribeLinkExtractor
{
    private readonly ILogger<Phi3UnsubscribeLinkExtractor> _logger;
    private readonly string _modelPath;
    private Model? _model;
    private Tokenizer? _tokenizer;
    private bool _modelInitialized = false;

    private static readonly Regex AnchorTagRegex = new(
        @"<a[^>]+href=[""']([^""']+)[""'][^>]*>(.*?)</a>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex UnsubscribeKeywordsRegex = new(
        @"unsubscribe|opt-out|optout|preferences",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex UrlRegex = new(
        @"https?://[^\s<>""']+",
        RegexOptions.Compiled);

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

    public async Task<string?> ExtractUnsubscribeLinkAsync(string emailBody)
    {
        // Step 1: Try regex-based extraction first (fast and efficient)
        _logger.LogInformation("Attempting regex-based extraction...");
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

        // Step 2: Fallback to Phi3 model if regex didn't find anything or URL is malformed
        await InitializeModelAsync();

        // If model is not available, return the regex result even if malformed (better than nothing)
        if (_model == null || _tokenizer == null)
        {
            _logger.LogWarning("Phi3 model not available, returning regex result or null");
            return regexLink; // May be null or malformed, but it's the best we have
        }

        try
        {
            // Try multiple context windows to increase chances of finding the link
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
                var prompt = $@"<|system|>
You are an email parser that extracts unsubscribe links from HTML emails. Extract complete URLs from href attributes.
<|end|>
<|user|>
Find the unsubscribe link in this email HTML. The link may be:
- In an <a href=""""> tag after text like 'To unsubscribe', 'no longer wish to receive', or 'opt-out'
- In an <a> tag where the link text says 'click here', 'here', 'unsubscribe', or similar
- A complete URL in the href attribute starting with http:// or https://
- The href may contain query parameters and be very long

Example patterns:
- To unsubscribe please <a href=""https://example.com/unsub?id=123"">click here</a>
- Click <a href=""https://example.com/optout"">here</a> to unsubscribe
- <a href=""https://unsubscribe.example.com"">Unsubscribe</a>

Email HTML:
{contextSnippet}

Extract ONLY the complete URL from the href attribute. Return the full URL including all parameters. If no unsubscribe link found, return NONE.
<|end|>
<|assistant|>";

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
                var link = ExtractUrlFromText(output);
                
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

    private List<string> ExtractMultipleUnsubscribeContexts(string emailBody)
    {
        var contexts = new List<string>();
        var keywords = new[] { "unsubscribe", "opt-out", "opt out", "optout", "manage preferences", "email preferences", "no longer wish to receive" };
        
        // Find all occurrences of keywords and extract contexts around them
        var keywordPositions = new List<(int position, string keyword)>();
        
        foreach (var keyword in keywords)
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
        const int searchRadius = 500; // Search within 500 chars before and after keyword
        
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
        var keywords = new[] { "unsubscribe", "opt-out", "optout", "preferences" };
        int bestIndex = -1;
        string? foundKeyword = null;
        
        // Find the first occurrence of any keyword
        foreach (var keyword in keywords)
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
                
                // Look for common action phrases like "click here", "here", etc.
                var actionPhrases = new[] { "click here", "here", "click", "tap here", "this link", "follow this link" };
                
                if (actionPhrases.Any(phrase => anchorText.Contains(phrase)))
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

    private string? ExtractUrlFromText(string text)
    {
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
