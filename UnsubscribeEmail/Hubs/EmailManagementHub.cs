using Microsoft.AspNetCore.SignalR;
using Microsoft.Identity.Web;
using UnsubscribeEmail.Services;

namespace UnsubscribeEmail.Hubs
{
    public class EmailManagementHub : Hub
    {
        private readonly IEmailManagementBackgroundService _emailManagementService;
        private readonly ILogger<EmailManagementHub> _logger;
        private readonly ITokenAcquisition _tokenAcquisition;

        public EmailManagementHub(IEmailManagementBackgroundService emailManagementService, ILogger<EmailManagementHub> logger, ITokenAcquisition tokenAcquisition)
        {
            _emailManagementService = emailManagementService;
            _logger = logger;
            _tokenAcquisition = tokenAcquisition;
        }
        public async Task SendProgress(string message)
        {
            await Clients.All.SendAsync("ReceiveProgress", message);
        }

        public async Task SendStatus(string status)
        {
            await Clients.All.SendAsync("ReceiveStatus", status);
        }

        public async Task SendSenderEmail(object senderEmailInfo)
        {
            await Clients.All.SendAsync("ReceiveSenderEmail", senderEmailInfo);
        }

        public async Task SendComplete(object result)
        {
            await Clients.All.SendAsync("ReceiveComplete", result);
        }

        public async Task SendError(string error)
        {
            await Clients.All.SendAsync("ReceiveError", error);
        }

        public async Task SendNotification(string message, string type)
        {
            await Clients.All.SendAsync("ReceiveNotification", new { message, type });
        }

        public async Task MarkEmailsAsRead(string senderEmail, int daysBack)
        {
            try
            {
                await Clients.Caller.SendAsync("ReceiveNotification", new { message = $"Marking emails from {senderEmail} as read...", type = "info" });

                var scopes = new[] { "https://graph.microsoft.com/Mail.ReadWrite" };
                var accessToken = await _tokenAcquisition.GetAccessTokenForUserAsync(scopes);

                var sinceDate = DateTime.UtcNow.AddDays(-daysBack);

                // Mark emails as read
                var result = await _emailManagementService.MarkEmailsAsReadAsync(senderEmail, daysBack, accessToken);
                
                await Clients.Caller.SendAsync("ReceiveNotification", new { message = $"Successfully marked {result.Count} emails as read from {senderEmail}", type = "success" });
            }
            catch (MicrosoftIdentityWebChallengeUserException)
            {
                _logger.LogWarning("User needs to re-authenticate");
                await Clients.Caller.SendAsync("RequireLogin");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error marking emails as read from {senderEmail}");
                await Clients.Caller.SendAsync("ReceiveNotification", new { message = $"Error marking emails as read: {ex.Message}", type = "error" });
            }
        }

        public async Task DeleteEmails(string senderEmail, int daysBack)
        {
            try
            {
                await Clients.Caller.SendAsync("ReceiveNotification", new { message = $"Deleting emails from {senderEmail}...", type = "info" });

                var scopes = new[] { "https://graph.microsoft.com/Mail.ReadWrite" };
                var accessToken = await _tokenAcquisition.GetAccessTokenForUserAsync(scopes);

                // Delete emails
                var result = await _emailManagementService.DeleteEmailsAsync(senderEmail, daysBack, accessToken);
                
                await Clients.Caller.SendAsync("ReceiveNotification", new { message = result.Message, type = result.Success ? "success" : "error" });
            }
            catch (MicrosoftIdentityWebChallengeUserException)
            {
                _logger.LogWarning("User needs to re-authenticate");
                await Clients.Caller.SendAsync("RequireLogin");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error deleting emails from {senderEmail}");
                await Clients.Caller.SendAsync("ReceiveNotification", new { message = $"Error deleting emails: {ex.Message}", type = "error" });
            }
        }

        public async Task MarkManyEmailsAsRead(string[] senderEmails, int daysBack)
        {
            try
            {
                await Clients.Caller.SendAsync("ReceiveNotification", new { message = $"Marking emails from {senderEmails.Length} sender(s) as read...", type = "info" });

                var scopes = new[] { "https://graph.microsoft.com/Mail.ReadWrite" };
                var accessToken = await _tokenAcquisition.GetAccessTokenForUserAsync(scopes);

                var totalMarked = 0;
                foreach (var senderEmail in senderEmails)
                {
                    var result = await _emailManagementService.MarkEmailsAsReadAsync(senderEmail, daysBack, accessToken);
                    totalMarked += result.Count;
                }

                await Clients.Caller.SendAsync("ReceiveNotification", new { message = $"Successfully marked {totalMarked} emails as read from {senderEmails.Length} sender(s)", type = "success" });
            }
            catch (MicrosoftIdentityWebChallengeUserException)
            {
                _logger.LogWarning("User needs to re-authenticate");
                await Clients.Caller.SendAsync("RequireLogin");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error marking emails as read from multiple senders");
                await Clients.Caller.SendAsync("ReceiveNotification", new { message = $"Error marking emails as read: {ex.Message}", type = "error" });
            }
        }

        public async Task DeleteManyEmails(string[] senderEmails, int daysBack)
        {
            try
            {
                await Clients.Caller.SendAsync("ReceiveNotification", new { message = $"Deleting emails from {senderEmails.Length} sender(s)...", type = "info" });

                var scopes = new[] { "https://graph.microsoft.com/Mail.ReadWrite" };
                var accessToken = await _tokenAcquisition.GetAccessTokenForUserAsync(scopes);

                var totalDeleted = 0;
                foreach (var senderEmail in senderEmails)
                {
                    var result = await _emailManagementService.DeleteEmailsAsync(senderEmail, daysBack, accessToken);
                    totalDeleted += result.Count;
                }

                await Clients.Caller.SendAsync("ReceiveNotification", new { message = $"Successfully deleted {totalDeleted} emails from {senderEmails.Length} sender(s)", type = "success" });
            }
            catch (MicrosoftIdentityWebChallengeUserException)
            {
                _logger.LogWarning("User needs to re-authenticate");
                await Clients.Caller.SendAsync("RequireLogin");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting emails from multiple senders");
                await Clients.Caller.SendAsync("ReceiveNotification", new { message = $"Error deleting emails: {ex.Message}", type = "error" });
            }
        }
    }
}
