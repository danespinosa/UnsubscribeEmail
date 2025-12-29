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
        await InitializeModelAsync();

        // If model is not available, use fallback method
        if (_model == null || _tokenizer == null)
        {
            return ExtractUnsubscribeLinkFallback(emailBody);
        }

        try
        {
            // Find the word "unsubscribe" or related keywords and extract context around it
            var contextSnippet = ExtractUnsubscribeContext(emailBody);
            
            // If no unsubscribe context found, use fallback
            if (string.IsNullOrEmpty(contextSnippet))
            {
                return ExtractUnsubscribeLinkFallback(emailBody);
            }

            // Create prompt for the model
            var prompt = $@"Extract the unsubscribe link from the following email. Return only the URL or 'NONE' if no unsubscribe link is found.

Email content:
{contextSnippet}

Unsubscribe link:";

            var sequences = _tokenizer.Encode(prompt);
            
            using var generatorParams = new GeneratorParams(_model);
            // max_length is total tokens (input + output), set high enough to accommodate both
            generatorParams.SetSearchOption("max_length", 512);
            
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
            return link;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error using Phi3 model to extract unsubscribe link");
            return ExtractUnsubscribeLinkFallback(emailBody);
        }
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

        // Calculate positions
        // The last character of the keyword is at: bestIndex + foundKeyword.Length - 1
        var endOfKeyword = bestIndex + foundKeyword.Length - 1;
        
        // Start position: 100 characters before the last char of the keyword
        var startPos = Math.Max(0, endOfKeyword - 99);
        
        // End position: include 100 characters after the keyword to capture the URL
        var endPos = Math.Min(emailBody.Length, bestIndex + foundKeyword.Length + 100);
        
        // Extract the context snippet
        var length = endPos - startPos;
        return emailBody.Substring(startPos, length);
    }

    private string? ExtractUnsubscribeLinkFallback(string emailBody)
    {
        // First, try to find anchor tags with "unsubscribe" in the text
        var anchorMatches = AnchorTagRegex.Matches(emailBody);
        
        foreach (Match anchorMatch in anchorMatches)
        {
            var anchorText = anchorMatch.Groups[2].Value;
            var href = anchorMatch.Groups[1].Value;
            
            // Check if anchor text or href contains unsubscribe-related keywords
            if (UnsubscribeKeywordsRegex.IsMatch(anchorText))
            {
                if (href.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || 
                    href.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation($"Found unsubscribe link in anchor tag: {href}");
                    return href;
                }
            }
        }

        // Fallback method using regex to find unsubscribe links directly in URLs
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

    private string? ExtractUrlFromText(string text)
    {
        // Extract URL from the model output
        var match = UrlRegex.Match(text);
        
        if (match.Success)
        {
            return match.Value;
        }

        return null;
    }
}
