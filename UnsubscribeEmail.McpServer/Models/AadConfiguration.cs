namespace UnsubscribeEmail.McpServer.Models;

public class AadConfiguration
{
    public string ClientId { get; set; } = string.Empty;
    public string TenantId { get; set; } = "common";
    public string ClientSecret { get; set; } = string.Empty;
    public bool IsConfigured => !string.IsNullOrEmpty(ClientId);
}
