# Getting Started with UnsubscribeEmail

This guide will help you set up and run the UnsubscribeEmail web application.

## Prerequisites

- .NET 10 SDK installed on your system
- An Outlook/Office365 email account
- IMAP access enabled on your email account

## Quick Start

### 1. Configure Email Credentials

Edit the file `UnsubscribeEmail/appsettings.json` and update the email configuration:

```json
{
  "EmailConfiguration": {
    "ImapServer": "outlook.office365.com",
    "ImapPort": 993,
    "UseSsl": true,
    "Email": "your-email@outlook.com",
    "Password": "your-app-specific-password"
  }
}
```

**Security Note**: 
- Use an app-specific password instead of your main password
- Never commit this file with real credentials to source control
- For production, use environment variables or Azure Key Vault

### 2. Enable IMAP on Outlook

1. Go to Outlook settings
2. Navigate to Mail > Sync email
3. Enable IMAP
4. Generate an app-specific password if you have 2FA enabled

### 3. Run the Application

```bash
cd UnsubscribeEmail
dotnet run
```

The application will start and display the URL (typically `https://localhost:5001`)

### 4. Use the Application

1. Open your web browser and navigate to the URL shown
2. Click "View Unsubscribe Links"
3. Wait while the application processes your emails (this may take a few minutes)
4. View the list of senders and their unsubscribe links
5. Click the "Unsubscribe" button next to any sender to visit their unsubscribe page

## Features

### Smart Processing
- Only reads emails from the current year
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

### "Resource temporarily unavailable" Error
- Verify your email and password are correct
- Check that IMAP is enabled in your Outlook settings
- Ensure you're using an app-specific password if you have 2FA

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
│   ├── EmailInfo.cs           # Email representation
│   └── SenderUnsubscribeInfo.cs # Sender and link mapping
├── Services/                  # Business logic
│   ├── EmailService.cs        # IMAP email fetching
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
