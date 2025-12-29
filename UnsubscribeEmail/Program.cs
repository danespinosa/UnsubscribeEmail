using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Identity.Web;
using Microsoft.Identity.Web.UI;
using UnsubscribeEmail.Services;

var builder = WebApplication.CreateBuilder(args);

// Check if Azure AD is configured
var azureAdSection = builder.Configuration.GetSection("AzureAd");
var clientId = azureAdSection["ClientId"];
var hasAzureAdConfig = !string.IsNullOrEmpty(clientId);

if (hasAzureAdConfig)
{
    // Add Microsoft Identity authentication
    builder.Services.AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme)
        .AddMicrosoftIdentityWebApp(azureAdSection)
        .EnableTokenAcquisitionToCallDownstreamApi(new[] { "User.Read", "Mail.Read" })
        .AddInMemoryTokenCaches();

    builder.Services.AddAuthorization(options =>
    {
        options.FallbackPolicy = options.DefaultPolicy;
    });

    // Add services to the container.
    builder.Services.AddRazorPages()
        .AddMicrosoftIdentityUI();
}
else
{
    // No authentication configured - show information page
    builder.Services.AddRazorPages();
}

// Register services
builder.Services.AddScoped<IEmailService, EmailService>();
builder.Services.AddSingleton<IUnsubscribeLinkExtractor, Phi3UnsubscribeLinkExtractor>();
builder.Services.AddScoped<IUnsubscribeService, UnsubscribeService>();

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

app.Run();
