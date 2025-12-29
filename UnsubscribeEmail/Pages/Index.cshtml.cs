using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace UnsubscribeEmail.Pages;

public class IndexModel : PageModel
{
    private readonly IConfiguration _configuration;
    
    public bool IsAzureAdConfigured { get; set; }

    public IndexModel(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public void OnGet()
    {
        var clientId = _configuration["AzureAd:ClientId"];
        IsAzureAdConfigured = !string.IsNullOrEmpty(clientId);
    }
}
