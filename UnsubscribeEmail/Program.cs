using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Identity.Web;
using Microsoft.Identity.Web.UI;
using UnsubscribeEmail.Hubs;
using UnsubscribeEmail.Services;
using MSGraph = Microsoft.Graph;

var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder(args);

// Check if Azure AD is configured
var azureAdSection = builder.Configuration.GetSection("AzureAd");
var clientId = azureAdSection["ClientId"];
var hasAzureAdConfig = !string.IsNullOrEmpty(clientId);

if (hasAzureAdConfig)
{
    // Add Microsoft Identity authentication
    builder.Services.AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme)
        .AddMicrosoftIdentityWebApp(options =>
        {
            builder.Configuration.Bind("AzureAd", options);
            options.Events = new OpenIdConnectEvents
            {
                OnRedirectToIdentityProviderForSignOut = context =>
                {
                    context.ProtocolMessage.PostLogoutRedirectUri = context.Request.Scheme + "://" + context.Request.Host;
                    return Task.CompletedTask;
                }
            };
        })
        .EnableTokenAcquisitionToCallDownstreamApi(new[] { "User.Read", "Mail.Read" })
        .AddMicrosoftGraph(builder.Configuration.GetSection("MicrosoftGraph"))
        .AddInMemoryTokenCaches();

    builder.Services.AddAuthorization();

    // Add services to the container.
    builder.Services.AddRazorPages()
        .AddMicrosoftIdentityUI();
    
    // Add SignalR
    builder.Services.AddSignalR();
    
    // Add HttpClient for REST API calls
    builder.Services.AddHttpClient();
    
    // Register services
    builder.Services.AddScoped<IEmailService, EmailService>();
    builder.Services.AddSingleton<IUnsubscribeLinkExtractor, Phi3UnsubscribeLinkExtractor>();
    builder.Services.AddScoped<IUnsubscribeService, UnsubscribeService>();
    builder.Services.AddSingleton<IUnsubscribeBackgroundService, UnsubscribeBackgroundService>();
}
else
{
    // No authentication configured - show information page
    builder.Services.AddRazorPages();
}

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseRouting();

if (hasAzureAdConfig)
{
    app.UseAuthentication();
    app.UseAuthorization();
}

app.MapStaticAssets();
app.MapRazorPages()
   .WithStaticAssets();

if (hasAzureAdConfig)
{
    app.MapControllers();
    app.MapHub<UnsubscribeProgressHub>("/unsubscribeProgressHub");
}

app.Run();
