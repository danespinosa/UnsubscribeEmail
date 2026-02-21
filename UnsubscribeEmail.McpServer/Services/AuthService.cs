using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Identity.Client;
using UnsubscribeEmail.McpServer.Models;

namespace UnsubscribeEmail.McpServer.Services;

/// <summary>
/// Manages AAD configuration and MSAL authentication for Microsoft Graph API access.
/// </summary>
public class AuthService
{
    private readonly ILogger<AuthService> _logger;
    private AadConfiguration _config = new();
    private IPublicClientApplication? _msalClient;
    private AuthenticationResult? _authResult;

    private static readonly string[] GraphScopes = ["User.Read", "Mail.Read", "Mail.ReadWrite"];
    private static readonly string ConfigFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".unsubscribe-email", "aad-config.json");

    public AuthService(ILogger<AuthService> logger)
    {
        _logger = logger;
        TryLoadSavedConfig();
    }

    public bool IsConfigured => _config.IsConfigured;
    public bool IsAuthenticated => _authResult != null && _authResult.ExpiresOn > DateTimeOffset.UtcNow;
    public string? UserEmail => _authResult?.Account?.Username;

    public void Configure(string clientId, string tenantId, string clientSecret, bool saveLocally = false)
    {
        _config = new AadConfiguration
        {
            ClientId = clientId,
            TenantId = string.IsNullOrEmpty(tenantId) ? "common" : tenantId,
            ClientSecret = clientSecret
        };
        _msalClient = null;
        _authResult = null;
        _logger.LogInformation("AAD configuration updated for client {ClientId}", clientId);

        if (saveLocally)
            SaveConfigToFile();
    }

    public AadConfiguration GetConfiguration() => _config;

    public async Task<string> CreateAadAppViaAzCliAsync(bool saveLocally = false)
    {
        var appName = $"UnsubscribeEmail-MCP-{DateTime.UtcNow:yyyyMMddHHmmss}";

        // Create the app registration
        var createResult = await RunAzCliAsync($"ad app create --display-name \"{appName}\" --sign-in-audience AzureADandPersonalMicrosoftAccount --query appId -o tsv");
        var clientId = createResult.Trim();
        if (string.IsNullOrEmpty(clientId))
            throw new InvalidOperationException("Failed to create AAD app registration. Ensure Azure CLI is installed and you are logged in (run 'az login').");

        // Add Microsoft Graph delegated permissions
        // User.Read: 00000003-0000-0000-c000-000000000000/e1fe6dd8-ba31-4d61-89e7-88639da4683d
        // Mail.Read: 00000003-0000-0000-c000-000000000000/570282fd-fa5c-430d-a7fd-fc8dc98a9dca
        // Mail.ReadWrite: 00000003-0000-0000-c000-000000000000/024d486e-b451-40bb-833d-3e66d98c5c73
        await RunAzCliAsync($"ad app permission add --id {clientId} --api 00000003-0000-0000-c000-000000000000 --api-permissions e1fe6dd8-ba31-4d61-89e7-88639da4683d=Scope 570282fd-fa5c-430d-a7fd-fc8dc98a9dca=Scope 024d486e-b451-40bb-833d-3e66d98c5c73=Scope");

        // Add redirect URI for interactive auth (localhost)
        await RunAzCliAsync($"ad app update --id {clientId} --public-client-redirect-uris http://localhost");

        // Create client secret
        var secretResult = await RunAzCliAsync($"ad app credential reset --id {clientId} --display-name \"MCP Secret\" --query password -o tsv");
        var clientSecret = secretResult.Trim();

        // Get tenant ID
        var tenantResult = await RunAzCliAsync("account show --query tenantId -o tsv");
        var tenantId = tenantResult.Trim();

        Configure(clientId, tenantId, clientSecret, saveLocally);

        return clientId;
    }

    public async Task<string> LoginAsync()
    {
        if (!IsConfigured)
            throw new InvalidOperationException("AAD app is not configured. Call configure-aad-app first.");

        EnsureMsalClient();

        try
        {
            // Clear all cached accounts so the user is always prompted to pick an account
            var accounts = await _msalClient!.GetAccountsAsync();
            foreach (var cachedAccount in accounts)
            {
                await _msalClient.RemoveAsync(cachedAccount);
            }

            // Always do interactive browser login with account selection
            _authResult = await _msalClient.AcquireTokenInteractive(GraphScopes)
                .WithUseEmbeddedWebView(false)
                .WithPrompt(Microsoft.Identity.Client.Prompt.SelectAccount)
                .ExecuteAsync();

            _logger.LogInformation("Logged in as {User}", _authResult.Account.Username);
            return _authResult.Account.Username;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Login failed");
            throw new InvalidOperationException($"Login failed: {ex.Message}", ex);
        }
    }

    public string GetAccessToken()
    {
        if (_authResult == null || string.IsNullOrEmpty(_authResult.AccessToken))
            throw new InvalidOperationException("Not authenticated. Call the login tool first.");

        if (_authResult.ExpiresOn <= DateTimeOffset.UtcNow)
            throw new InvalidOperationException("Access token has expired. Call the login tool again.");

        return _authResult.AccessToken;
    }

    public HttpClient CreateAuthenticatedHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GetAccessToken());
        return client;
    }

    private void EnsureMsalClient()
    {
        if (_msalClient != null) return;

        var builder = PublicClientApplicationBuilder
            .Create(_config.ClientId)
            .WithAuthority($"https://login.microsoftonline.com/{_config.TenantId}")
            .WithRedirectUri("http://localhost");

        _msalClient = builder.Build();
    }

    private static async Task<string> RunAzCliAsync(string arguments)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "az",
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start Azure CLI. Ensure 'az' is installed and in PATH.");

        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Azure CLI command failed: {error}");

        return output;
    }

    private void SaveConfigToFile()
    {
        try
        {
            var dir = Path.GetDirectoryName(ConfigFilePath)!;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(new
            {
                clientId = _config.ClientId,
                tenantId = _config.TenantId,
                clientSecret = _config.ClientSecret
            }, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ConfigFilePath, json);
            _logger.LogInformation("AAD configuration saved to {Path}", ConfigFilePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save AAD configuration to file");
        }
    }

    private void TryLoadSavedConfig()
    {
        try
        {
            if (!File.Exists(ConfigFilePath)) return;

            var json = File.ReadAllText(ConfigFilePath);
            var doc = JsonSerializer.Deserialize<JsonElement>(json);
            var clientId = doc.GetProperty("clientId").GetString() ?? "";
            var tenantId = doc.GetProperty("tenantId").GetString() ?? "common";
            var clientSecret = doc.GetProperty("clientSecret").GetString() ?? "";

            if (!string.IsNullOrEmpty(clientId) && !string.IsNullOrEmpty(clientSecret))
            {
                _config = new AadConfiguration
                {
                    ClientId = clientId,
                    TenantId = tenantId,
                    ClientSecret = clientSecret
                };
                _logger.LogInformation("Loaded saved AAD configuration for client {ClientId}", clientId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load saved AAD configuration");
        }
    }
}
