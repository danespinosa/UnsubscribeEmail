using UnsubscribeEmail.Models;
using UnsubscribeEmail.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorPages();

// Configure email settings
var emailConfig = new EmailConfiguration();
builder.Configuration.GetSection("EmailConfiguration").Bind(emailConfig);
builder.Services.AddSingleton(emailConfig);

// Register services
builder.Services.AddSingleton<IEmailService, EmailService>();
builder.Services.AddSingleton<IUnsubscribeLinkExtractor, Phi3UnsubscribeLinkExtractor>();
builder.Services.AddSingleton<IUnsubscribeService, UnsubscribeService>();

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

app.UseAuthorization();

app.MapStaticAssets();
app.MapRazorPages()
   .WithStaticAssets();

app.Run();
