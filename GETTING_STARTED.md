# Getting Started with UnsubscribeEmail

This guide will help you set up and run the UnsubscribeEmail web application.

## Prerequisites

- .NET 10 SDK installed on your system
- An Outlook/Office365 email account
- An Azure AD (Entra ID) application registration

## Quick Start

### 1. Create Azure AD Application

1. Go to the [Azure Portal](https://portal.azure.com)
2. Navigate to **Azure Active Directory** > **App registrations**
3. Click **New registration**
4. Configure:
   - **Name**: UnsubscribeEmail
   - **Supported account types**: Accounts in any organizational directory and personal Microsoft accounts
   - **Redirect URI**: Web - `https://localhost:5001/signin-oidc`
5. After creation, note:
   - **Application (client) ID**
   - **Directory (tenant) ID**
6. Go to **Certificates & secrets** > **New client secret**
   - Create a secret and **copy its value immediately** (you won't see it again)
7. Go to **API permissions**:
   - Click **Add a permission** > **Microsoft Graph** > **Delegated permissions**
   - Add `User.Read` and `Mail.Read`
   - Click **Grant admin consent** (if required for your organization)

### 2. Configure Application Settings

Edit the file `UnsubscribeEmail/appsettings.json` and update the Azure AD configuration:

```json
{
  "AzureAd": {
    "Instance": "https://login.microsoftonline.com/",
    "TenantId": "your-tenant-id-from-step-1",
    "ClientId": "your-client-id-from-step-1",
    "ClientSecret": "your-client-secret-from-step-1",
    "CallbackPath": "/signin-oidc"
  }
}
```

**Security Note**: 
- Never commit this file with real credentials to source control
- For production, use environment variables or Azure Key Vault
- The client secret is sensitive - treat it like a password

### 3. Run the Application

```bash
cd UnsubscribeEmail
dotnet run
```

The application will start and display the URL (typically `https://localhost:5001`)

### 4. Use the Application

1. Open your web browser and navigate to the URL shown
2. Click the **Login** button in the navigation bar
3. Sign in with your Microsoft account
4. Grant the requested permissions (User.Read, Mail.Read)
5. After successful login, click **View Unsubscribe Links**
6. Wait while the application processes your emails (this may take a few minutes)
7. View the list of senders and their unsubscribe links
8. Click the "Unsubscribe" button next to any sender to visit their unsubscribe page
9. Use the **Logout** button when finished

## Features

### Secure Authentication
- OAuth 2.0 authentication via Microsoft Identity
- No password storage - uses secure token-based authentication
- Login/Logout functionality built-in

### Smart Processing
- Only reads emails from the current year via Microsoft Graph API
- Groups emails by sender
- Stops processing emails from a sender once an unsubscribe link is found
- Caches results to avoid re-processing

### AI-Powered Extraction (Optional)
The application can use the Phi3 AI model via ONNX Runtime for more accurate link extraction:

1. Download the Phi3 ONNX model
2. Extract to a directory (e.g., `./phi3-model`)
3. Update `Phi3ModelPath` in `appsettings.json`

If the model is not available, the application automatically falls back to regex-based extraction.

### Fallback Extraction
The regex-based fallback looks for common unsubscribe link patterns:
- URLs containing "unsubscribe"
- URLs containing "preferences"
- URLs containing "opt-out" or "optout"
- Links in HTML anchor tags with these patterns

## Troubleshooting

### Authentication/Login Errors
- Verify your Azure AD app configuration is correct
- Ensure TenantId, ClientId, and ClientSecret match your Azure app
- Check that redirect URI in Azure matches your application URL
- Verify API permissions (User.Read, Mail.Read) are granted
- Try clearing browser cookies and cache

### "No emails found"
- Make sure you have emails from the current year
- Check the date range in the application logs
- Verify your email account is accessible via IMAP

### Model Loading Errors
- The application will automatically use regex fallback if the Phi3 model is not found
- Check the `Phi3ModelPath` in `appsettings.json`
- Ensure all model files are present in the specified directory

## Architecture

```
UnsubscribeEmail/
├── Models/                    # Data models
│   ├── EmailConfiguration.cs  # Email settings
│   ├── EmailMessage.cs        # Email representation
│   └── SenderUnsubscribeInfo.cs # Sender and link mapping
├── Services/                  # Business logic
│   ├── EmailManagementBackgroundService.cs # Microsoft Graph email operations
│   ├── Phi3UnsubscribeLinkExtractor.cs # AI-powered extraction
│   └── UnsubscribeService.cs  # Orchestration and caching
├── Pages/                     # Razor Pages
│   ├── Index.cshtml           # Home page
│   └── UnsubscribeLinks/      # Unsubscribe links page
└── appsettings.json           # Configuration
```

## Technologies Used

- **ASP.NET Core 10** - Web framework
- **Razor Pages** - Server-side rendering
- **MailKit** - IMAP email connectivity
- **ONNX Runtime GenAI** - AI model execution
- **Bootstrap 5** - Responsive UI

## Security Best Practices

1. **Never commit credentials** - Use `.gitignore` patterns for sensitive files
2. **Use app-specific passwords** - Don't use your main account password
3. **Environment variables** - For production, store credentials securely
4. **Read-only access** - The app only reads emails, never modifies them
5. **HTTPS** - Always use HTTPS in production

## Support

For issues or questions:
1. Check the troubleshooting section above
2. Review the application logs for detailed error messages
3. Verify your email configuration settings
4. Ensure IMAP is properly enabled on your account

## License

MIT License
