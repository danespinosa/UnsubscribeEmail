using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.FileProviders;
using Microsoft.Identity.Web;
using Microsoft.Identity.Web.UI;
using UnsubscribeEmail.Hubs;
using UnsubscribeEmail.Services;

var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder(args);

// Check if Azure AD is configured
var azureAdSection = builder.Configuration.GetSection("AzureAd");
var clientId = azureAdSection["ClientId"];
var hasAzureAdConfig = !string.IsNullOrEmpty(clientId);


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
    .EnableTokenAcquisitionToCallDownstreamApi(
        builder.Configuration.GetSection("MicrosoftGraph:Scopes").Value?.Split(' ') ?? new[] { "User.Read", "Mail.Read" })
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
builder.Services.AddSingleton<IEmailManagementBackgroundService, EmailManagementBackgroundService>();
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.All;
});

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseForwardedHeaders();

// Configure path to generate https cert
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(
                   Path.Combine(builder.Environment.WebRootPath, "StaticFiles")),
    RequestPath = "/.well-known",
    ServeUnknownFileTypes = true,
});

app.UseHttpsRedirection();

app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();
app.MapStaticAssets();
app.MapRazorPages()
   .WithStaticAssets();
app.MapControllers();
app.MapHub<UnsubscribeProgressHub>("/unsubscribeProgressHub");
app.MapHub<EmailManagementHub>("/emailManagementHub");

app.Run();
