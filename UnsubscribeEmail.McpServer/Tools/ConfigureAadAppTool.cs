using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using UnsubscribeEmail.McpServer.Services;

namespace UnsubscribeEmail.McpServer.Tools;

[McpServerToolType]
public class ConfigureAadAppTool
{
    [McpServerTool(Name = "configure_aad_app"), Description(
        "Configure the Azure AD application for email access. " +
        "Either provide clientId, tenantId, and clientSecret to use an existing AAD app, " +
        "or call with no arguments to auto-create one via Azure CLI (requires 'az' to be installed and logged in). " +
        "This must be called before using login or any email tools.")]
    public static async Task<string> ConfigureAadApp(
        AuthService authService,
        [Description("The Azure AD Application (client) ID. Leave empty to auto-create via Azure CLI.")] string? clientId = null,
        [Description("The Azure AD Tenant ID. Use 'common' for multi-tenant or 'consumers' for personal accounts. Defaults to 'common'.")] string? tenantId = null,
        [Description("The Azure AD client secret. Leave empty to auto-create via Azure CLI.")] string? clientSecret = null)
    {
        try
        {
            if (!string.IsNullOrEmpty(clientId) && !string.IsNullOrEmpty(clientSecret))
            {
                authService.Configure(clientId, tenantId ?? "common", clientSecret);
                return JsonSerializer.Serialize(new
                {
                    status = "configured",
                    clientId,
                    tenantId = tenantId ?? "common",
                    message = "AAD app configured successfully. Call the 'login' tool next to authenticate."
                });
            }

            // Auto-create via Azure CLI
            var newClientId = await authService.CreateAadAppViaAzCliAsync();
            var config = authService.GetConfiguration();
            return JsonSerializer.Serialize(new
            {
                status = "created",
                clientId = newClientId,
                tenantId = config.TenantId,
                message = "AAD app created and configured via Azure CLI. Call the 'login' tool next to authenticate."
            });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                status = "error",
                message = ex.Message
            });
        }
    }
}
