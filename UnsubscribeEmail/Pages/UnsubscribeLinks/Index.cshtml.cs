using Microsoft.AspNetCore.Mvc.RazorPages;
using UnsubscribeEmail.Models;
using UnsubscribeEmail.Services;

namespace UnsubscribeEmail.Pages.UnsubscribeLinks;

public class IndexModel : PageModel
{
    private readonly IUnsubscribeService _unsubscribeService;
    private readonly ILogger<IndexModel> _logger;

    public List<SenderUnsubscribeInfo> SenderInfos { get; set; } = new();
    public bool IsLoading { get; set; }
    public string? ErrorMessage { get; set; }

    public IndexModel(IUnsubscribeService unsubscribeService, ILogger<IndexModel> logger)
    {
        _unsubscribeService = unsubscribeService;
        _logger = logger;
    }

    public async Task OnGetAsync()
    {
        try
        {
            IsLoading = true;
            _logger.LogInformation("Fetching unsubscribe links...");
            SenderInfos = await _unsubscribeService.GetSenderUnsubscribeLinksAsync();
            IsLoading = false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching unsubscribe links");
            ErrorMessage = $"Error: {ex.Message}. Please check your email configuration in appsettings.json";
            IsLoading = false;
        }
    }
}
