# Email Unsubscribe Link Finder

A .NET 10 web application that connects to Outlook email and extracts unsubscribe links from all emails received in the current year.

## Features

- **Outlook Integration**: Connects to any Outlook/Office365 email account via IMAP
- **Smart Processing**: Reads emails from the current year and extracts unsubscribe links
- **Efficient Caching**: Once an unsubscribe link is found for a sender, it skips processing new emails from that sender
- **AI-Powered Extraction**: Uses ONNX Runtime with Phi3 model for intelligent link extraction (with regex fallback)
- **Clean UI**: Displays all senders and their unsubscribe links in an easy-to-use HTML table

## Prerequisites

- .NET 10 SDK
- An Outlook/Office365 email account
- (Optional) Phi3 ONNX model for AI-powered link extraction

## Configuration

1. Open `appsettings.json` in the `UnsubscribeEmail` folder
2. Update the `EmailConfiguration` section with your email credentials:

```json
{
  "EmailConfiguration": {
    "ImapServer": "outlook.office365.com",
    "ImapPort": 993,
    "UseSsl": true,
    "Email": "your-email@outlook.com",
    "Password": "your-password-or-app-password"
  }
}
```

**Note**: For security reasons, consider using an app-specific password instead of your main account password.

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

4. Click on "View Unsubscribe Links" to start processing your emails

## How It Works

1. **Connection**: The app connects to your Outlook email via IMAP
2. **Fetching**: It retrieves all emails from the current year
3. **Grouping**: Emails are grouped by sender
4. **Processing**: For each sender:
   - If we already have an unsubscribe link, skip to the next sender
   - If not, process emails from that sender until an unsubscribe link is found
5. **Extraction**: Uses Phi3 AI model (or regex fallback) to extract unsubscribe links from email content
6. **Display**: Shows all senders and their unsubscribe links in a sortable table

## Security Considerations

- **Never commit your `appsettings.json` with real credentials** - The `.gitignore` is configured to exclude `*.env` files
- Consider using environment variables or Azure Key Vault for production deployments
- Use app-specific passwords when available
- The application only reads emails; it does not modify or delete anything

## Technologies Used

- **.NET 10**: Latest .NET framework
- **ASP.NET Core Razor Pages**: For the web UI
- **MailKit**: For IMAP email connectivity
- **ONNX Runtime GenAI**: For running the Phi3 AI model
- **Bootstrap 5**: For responsive UI design

## Troubleshooting

### Authentication Errors
- Ensure you're using the correct email and password
- For Microsoft accounts with 2FA, generate an app-specific password
- Check that IMAP is enabled in your Outlook settings

### Model Loading Errors
- Verify the Phi3 model path is correct
- Ensure all model files are present in the directory
- The app will use regex fallback if the model fails to load

### No Emails Found
- Verify you have emails from the current year
- Check the email configuration settings
- Review the application logs for detailed error messages

## License

MIT License - See LICENSE file for details
