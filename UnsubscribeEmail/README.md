# Email Unsubscribe Link Finder

A .NET 10 web application that connects to Outlook email via Microsoft Graph API and extracts unsubscribe links from all emails received in the current year.

## Features

- **Microsoft Graph Integration**: Connects to Outlook/Office365 email accounts using modern OAuth authentication
- **Secure Authentication**: Login/Logout functionality with Microsoft Identity
- **Smart Processing**: Reads emails from the current year and extracts unsubscribe links
- **Efficient Caching**: Once an unsubscribe link is found for a sender, it skips processing new emails from that sender
- **AI-Powered Extraction**: Uses ONNX Runtime with Phi3 model for intelligent link extraction (with regex fallback)
- **Clean UI**: Displays all senders and their unsubscribe links in an easy-to-use HTML table

## Prerequisites

- .NET 10 SDK
- An Azure AD (Entra ID) application registration
- An Outlook/Office365 email account
- (Optional) Phi3 ONNX model for AI-powered link extraction

## Azure AD Application Setup

1. Go to the [Azure Portal](https://portal.azure.com)
2. Navigate to **Azure Active Directory** > **App registrations**
3. Click **New registration**
4. Configure:
   - **Name**: UnsubscribeEmail
   - **Supported account types**: Accounts in any organizational directory and personal Microsoft accounts
   - **Redirect URI**: Web - `https://localhost:5001/signin-oidc` (or your URL)
5. After creation, note the **Application (client) ID** and **Directory (tenant) ID**
6. Go to **Certificates & secrets** > **New client secret**
7. Create a secret and copy its value immediately
8. Go to **API permissions**:
   - Add **Microsoft Graph** > **Delegated permissions**
   - Add `User.Read` and `Mail.Read`
   - Grant admin consent if required

## Configuration

1. Open `appsettings.json` in the `UnsubscribeEmail` folder
2. Update the `AzureAd` section with your Azure AD app details:

```json
{
  "AzureAd": {
    "Instance": "https://login.microsoftonline.com/",
    "TenantId": "your-tenant-id",
    "ClientId": "your-client-id",
    "ClientSecret": "your-client-secret",
    "CallbackPath": "/signin-oidc"
  }
}
```

**Security Note**: Never commit your `appsettings.json` with real credentials to source control. Use environment variables or Azure Key Vault for production.

## Optional: Phi3 Model Setup

If you want to use the AI-powered link extraction (recommended for better accuracy):

1. Download the Phi3 ONNX model from Hugging Face or Microsoft
2. Extract the model files to a directory (e.g., `./phi3-model`)
3. Update the `Phi3ModelPath` in `appsettings.json`:

```json
{
  "Phi3ModelPath": "./phi3-model"
}
```

If the model is not available, the application will automatically fall back to regex-based extraction.

## Running the Application

1. Navigate to the project directory:
```bash
cd UnsubscribeEmail
```

2. Run the application:
```bash
dotnet run
```

3. Open your browser and navigate to `https://localhost:5001` (or the URL shown in the console)

4. Click "Login" in the navigation bar to sign in with your Microsoft account

5. After authentication, click "View Unsubscribe Links" to start processing your emails

## How It Works

1. **Authentication**: Sign in with your Microsoft account using OAuth
2. **Authorization**: The app requests permission to read your emails via Microsoft Graph API
3. **Fetching**: It retrieves all emails from the current year using Microsoft Graph
4. **Grouping**: Emails are grouped by sender
5. **Processing**: For each sender:
   - If we already have an unsubscribe link, skip to the next sender
   - If not, process emails from that sender until an unsubscribe link is found
6. **Extraction**: Uses Phi3 AI model (or regex fallback) to extract unsubscribe links from email content
7. **Display**: Shows all senders and their unsubscribe links in a sortable table

## Security Considerations

- **Never commit your `appsettings.json` with real credentials** - Store sensitive data in environment variables or Azure Key Vault
- **OAuth2 Authentication**: The app uses modern OAuth authentication instead of password-based authentication
- **Minimal Permissions**: Only requests `User.Read` and `Mail.Read` permissions
- **Read-Only Access**: The application only reads emails; it does not modify or delete anything
- **Token Management**: Uses Microsoft Identity Web for secure token management

## Technologies Used

- **.NET 10**: Latest .NET framework
- **ASP.NET Core Razor Pages**: For the web UI
- **Microsoft Graph API**: For accessing Outlook emails
- **Microsoft Identity Web**: For OAuth authentication
- **ONNX Runtime GenAI**: For running the Phi3 AI model
- **Bootstrap 5**: For responsive UI design

## Troubleshooting

### Authentication Errors
- Ensure your Azure AD app is properly configured
- Verify TenantId, ClientId, and ClientSecret are correct
- Check that redirect URIs match your application URLs
- Ensure API permissions (User.Read, Mail.Read) are granted

### Model Loading Errors
- Verify the Phi3 model path is correct
- Ensure all model files are present in the directory
- The app will use regex fallback if the model fails to load

### No Emails Found
- Verify you have emails from the current year
- Check that you've granted Mail.Read permission
- Review the application logs for detailed error messages

## License

MIT License - See LICENSE file for details
