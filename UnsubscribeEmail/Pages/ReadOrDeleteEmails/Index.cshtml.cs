using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Identity.Web;
using UnsubscribeEmail.Hubs;
using UnsubscribeEmail.Services;

namespace UnsubscribeEmail.Pages.ReadOrDeleteEmails;

[Authorize]
public class IndexModel : PageModel
{
    private readonly ITokenAcquisition _tokenAcquisition;
    private readonly IEmailManagementBackgroundService _backgroundService;
    private readonly IHubContext<EmailManagementHub> _hubContext;
    private readonly ILogger<IndexModel> _logger;

    public IndexModel(
        ITokenAcquisition tokenAcquisition,
        IEmailManagementBackgroundService backgroundService,
        IHubContext<EmailManagementHub> hubContext,
        ILogger<IndexModel> logger)
    {
        _tokenAcquisition = tokenAcquisition;
        _backgroundService = backgroundService;
        _hubContext = hubContext;
        _logger = logger;
    }

    public void OnGet()
    {
        // Page loads empty, processing starts via JavaScript
    }

    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> OnPostStartProcessingAsync([FromBody] ProcessingRequest request)
    {
        try
        {
            // Acquire token for the authenticated user to ensure they're authenticated
            var scopes = new[] { "Mail.Read", "Mail.ReadWrite" };
            var accessToken = await _tokenAcquisition.GetAccessTokenForUserAsync(scopes);
            
            // Validate daysBack parameter (1-365 days)
            var daysBack = request.DaysBack ?? 365;
            if (daysBack < 1) daysBack = 1;
            if (daysBack > 365) daysBack = 365;
            
            // Get the SignalR connection ID from the request header
            var connectionId = Request.Headers["X-SignalR-ConnectionId"].FirstOrDefault();
            if (string.IsNullOrEmpty(connectionId))
            {
                return new JsonResult(new { success = false, error = "SignalR connection ID not provided" });
            }
            
            // Start background processing (fire and forget)
            _ = Task.Run(async () =>
            {
                try
                {
                    await _backgroundService.ProcessEmailsAsync(daysBack, connectionId, accessToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in background email processing");
                    await _hubContext.Clients.Client(connectionId).SendAsync("ReceiveError", $"Processing failed: {ex.Message}");
                }
            });
            
            return new JsonResult(new { success = true });
        }
        catch (MicrosoftIdentityWebChallengeUserException)
        {
            _logger.LogWarning("User needs to re-authenticate");
            return new JsonResult(new { success = false, error = "REQUIRE_LOGIN" });
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
