using Microsoft.AspNetCore.SignalR;

namespace UnsubscribeEmail.Hubs;

public class UnsubscribeProgressHub : Hub
{
    public async Task StartProcessing()
    {
        await Clients.Caller.SendAsync("ProgressUpdate", "Starting email processing...");
    }
}
