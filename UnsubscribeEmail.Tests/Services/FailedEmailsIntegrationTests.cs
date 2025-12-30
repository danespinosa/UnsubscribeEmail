using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using UnsubscribeEmail.Services;
using Xunit;
using Xunit.Abstractions;

namespace UnsubscribeEmail.Tests.Services;

public class FailedEmailsIntegrationTests
{
    private readonly string _failedEmailsDirectory;
    private readonly string _modelPath;
    private readonly ITestOutputHelper _output;

    public FailedEmailsIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
        _failedEmailsDirectory = Path.Combine(
            "..", "..", "..", "..", "UnsubscribeEmail", "FailedEmails");

        _modelPath = Path.Combine(
            "..", "..", "..", "..", "..",
            "Phi-3-mini-4k-instruct-onnx", "cpu_and_mobile", "cpu-int4-rtn-block-32-acc-level-4");
    }

    private Phi3UnsubscribeLinkExtractor CreateExtractor()
    {
        var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Information);
        });

        var logger = loggerFactory.CreateLogger<Phi3UnsubscribeLinkExtractor>();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                { "Phi3ModelPath", _modelPath }
            })
            .Build();

        return new Phi3UnsubscribeLinkExtractor(logger, configuration);
    }

    [Fact]
    public async Task ProcessAllFailedEmails_ShouldExtractUnsubscribeLinks()
    {
        // Skip if FailedEmails directory doesn't exist
        if (!Directory.Exists(_failedEmailsDirectory))
        {
            _output.WriteLine("FailedEmails directory does not exist - skipping test");
            return;
        }

        // Skip if model doesn't exist
        if (!Directory.Exists(_modelPath))
        {
            _output.WriteLine("Phi-3 model not found - skipping test");
            return;
        }

        var extractor = CreateExtractor();
        var htmlFiles = Directory.GetFiles(_failedEmailsDirectory, "*.html");

        if (htmlFiles.Length == 0)
        {
            _output.WriteLine("No HTML files found in FailedEmails directory");
            return;
        }

        var results = new List<(string FileName, string? Link, bool Success)>();

        foreach (var htmlFile in htmlFiles)
        {
            var fileName = Path.GetFileName(htmlFile);
            var htmlContent = await File.ReadAllTextAsync(htmlFile);

            var link = await extractor.ExtractUnsubscribeLinkAsync(htmlContent);
            var success = !string.IsNullOrEmpty(link);

            results.Add((fileName, link, success));
            
            _output.WriteLine($"{fileName}: {(success ? "SUCCESS - " + link : "FAILED")}");
        }

        // Output results for analysis
        var successCount = results.Count(r => r.Success);
        var failureCount = results.Count(r => !r.Success);

        _output.WriteLine($"\nProcessed {results.Count} failed emails: {successCount} successful, {failureCount} failed");
        _output.WriteLine($"Success rate: {(successCount * 100.0 / results.Count):F2}%");
    }

    [Fact]
    public async Task ProcessSingleFailedEmail_ShouldExtractUnsubscribeLink()
    {
        // Skip if FailedEmails directory doesn't exist
        if (!Directory.Exists(_failedEmailsDirectory))
        {
            _output.WriteLine("FailedEmails directory does not exist - skipping test");
            return;
        }

        // Skip if model doesn't exist
        if (!Directory.Exists(_modelPath))
        {
            _output.WriteLine("Phi-3 model not found - skipping test");
            return;
        }

        var htmlFiles = Directory.GetFiles(_failedEmailsDirectory, "*.html");
        
        if (htmlFiles.Length == 0)
        {
            _output.WriteLine("No HTML files found in FailedEmails directory - skipping test");
            return;
        }

        var extractor = CreateExtractor();
        var firstFile = htmlFiles[0];
        var fileName = Path.GetFileName(firstFile);
        var htmlContent = await File.ReadAllTextAsync(firstFile);

        _output.WriteLine($"Processing: {fileName}");
        
        var link = await extractor.ExtractUnsubscribeLinkAsync(htmlContent);

        if (!string.IsNullOrEmpty(link))
        {
            _output.WriteLine($"Successfully extracted: {link}");
            Assert.True(Uri.IsWellFormedUriString(link, UriKind.Absolute), 
                $"Extracted link is not a valid URL: {link}");
        }
        else
        {
            _output.WriteLine("Failed to extract unsubscribe link");
        }
    }

    [Fact]
    public void FailedEmailsDirectory_ShouldExist()
    {
        var directoryExists = Directory.Exists(_failedEmailsDirectory);
        
        if (!directoryExists)
        {
            _output.WriteLine($"FailedEmails directory does not exist at: {_failedEmailsDirectory}");
            _output.WriteLine("This is expected if no emails have failed processing yet.");
        }
        else
        {
            var fileCount = Directory.GetFiles(_failedEmailsDirectory, "*.html").Length;
            _output.WriteLine($"FailedEmails directory exists with {fileCount} HTML files");
        }
    }
}
