using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Identity.Web;
using UnsubscribeEmail.Models;
using UnsubscribeEmail.Services;

namespace UnsubscribeEmail.Pages.UnsubscribeLinks;

[Authorize]
public class IndexModel : PageModel
{
    private readonly IUnsubscribeService _unsubscribeService;
    private readonly ITokenAcquisition _tokenAcquisition;
    private readonly ILogger<IndexModel> _logger;

    public List<SenderUnsubscribeInfo> SenderInfos { get; set; } = new();
    public bool IsLoading { get; set; }
    public string? ErrorMessage { get; set; }

    public IndexModel(
        IUnsubscribeService unsubscribeService, 
        ITokenAcquisition tokenAcquisition,
        ILogger<IndexModel> logger)
    {
        _unsubscribeService = unsubscribeService;
        _tokenAcquisition = tokenAcquisition;
        _logger = logger;
    }

    public async Task OnGetAsync()
    {
        try
        {
            IsLoading = true;
            _logger.LogInformation("Fetching unsubscribe links...");
            
            // Get access token for Microsoft Graph
            var accessToken = await _tokenAcquisition.GetAccessTokenForUserAsync(new[] { "User.Read", "Mail.Read" });
            
            SenderInfos = await _unsubscribeService.GetSenderUnsubscribeLinksAsync(accessToken);
            IsLoading = false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching unsubscribe links");
            ErrorMessage = $"Error: {ex.Message}. Please make sure you have granted the required permissions.";
            IsLoading = false;
        }
    }
}
