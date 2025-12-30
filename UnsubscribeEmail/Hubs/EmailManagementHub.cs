using Microsoft.AspNetCore.SignalR;

namespace UnsubscribeEmail.Hubs
{
    public class EmailManagementHub : Hub
    {
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
    }
}
