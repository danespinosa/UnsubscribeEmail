using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Identity.Web;
using UnsubscribeEmail.Models;
using UnsubscribeEmail.Services;
using System.Security.Claims;

namespace UnsubscribeEmail.Pages.UnsubscribeLinks;

[Authorize]
public class IndexModel : PageModel
{
    private readonly IUnsubscribeBackgroundService _backgroundService;
    private readonly ITokenAcquisition _tokenAcquisition;
    private readonly ILogger<IndexModel> _logger;

    public List<SenderUnsubscribeInfo> SenderInfos { get; set; } = new();
    public string? JobId { get; set; }

    public IndexModel(
        IUnsubscribeBackgroundService backgroundService,
        ITokenAcquisition tokenAcquisition,
        ILogger<IndexModel> logger)
    {
        _backgroundService = backgroundService;
        _tokenAcquisition = tokenAcquisition;
        _logger = logger;
    }

    public void OnGet()
    {
        // Page loads empty, processing starts via JavaScript
    }

    public async Task<IActionResult> OnPostStartProcessingAsync([FromBody] ProcessingRequest request)
    {
        try
        {
            // Acquire token for the authenticated user
            var scopes = new[] { "Mail.Read" };
            var accessToken = await _tokenAcquisition.GetAccessTokenForUserAsync(scopes);
            
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.Identity?.Name ?? "anonymous";
            
            // Validate daysBack parameter (1-365 days)
            var daysBack = request.DaysBack ?? 365;
            if (daysBack < 1) daysBack = 1;
            if (daysBack > 365) daysBack = 365;
            
            var jobId = await _backgroundService.StartProcessingAsync(userId, accessToken, daysBack);
            
            return new JsonResult(new { success = true, jobId });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting background processing");
            return new JsonResult(new { success = false, error = ex.Message });
        }
    }
}

public class ProcessingRequest
{
    public int? DaysBack { get; set; }
}
