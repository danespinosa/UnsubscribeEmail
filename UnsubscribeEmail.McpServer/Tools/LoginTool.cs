using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using UnsubscribeEmail.McpServer.Services;

namespace UnsubscribeEmail.McpServer.Tools;

[McpServerToolType]
public class LoginTool
{
    [McpServerTool(Name = "login"), Description(
        "Log in to your Microsoft email account using the configured AAD app. " +
        "This opens a browser window for interactive authentication. " +
        "You must call configure_aad_app first before using this tool.")]
    public static async Task<string> Login(AuthService authService)
    {
        try
        {
            if (!authService.IsConfigured)
            {
                return JsonSerializer.Serialize(new
                {
                    status = "error",
                    message = "AAD app is not configured. Call the 'configure_aad_app' tool first."
                });
            }

            var userEmail = await authService.LoginAsync();
            return JsonSerializer.Serialize(new
            {
                status = "authenticated",
                userEmail,
                message = $"Successfully logged in as {userEmail}. You can now use 'read_emails' and 'get_email_content' tools."
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
