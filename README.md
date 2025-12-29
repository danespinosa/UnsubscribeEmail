# UnsubscribeEmail

A web application that connects to your Microsoft email account to automatically find and organize unsubscribe links from all your emails, making it easy to clean up your inbox subscriptions.

## Features

- 🔐 **Secure Microsoft Authentication** - Uses OAuth 2.0 to securely access your emails
- 📧 **Automatic Email Scanning** - Fetches emails from the current year and processes them in real-time
- 🤖 **Smart Link Extraction** - Uses regex patterns first, with AI (Phi-3) model fallback for complex cases
- ⚡ **Real-time Progress Updates** - SignalR-powered live updates as emails are processed
- 🎯 **Sender Grouping** - Organizes results by sender email address
- 📊 **Summary Statistics** - Shows how many senders have unsubscribe links

## Prerequisites

- **.NET 10.0 SDK** or later
- **Microsoft Azure Account** (for Azure AD app registration)
- **Git** (for cloning the Phi-3 model)
- **Git LFS** (Large File Storage) for downloading the Phi-3 model files

## Setup Instructions

### 1. Clone the Repository

```bash
git clone <your-repository-url>
cd UnsubscribeEmail
```

### 2. Set Up Azure AD Application

1. Go to [Azure Portal](https://portal.azure.com)
2. Navigate to **Azure Active Directory** > **App registrations**
3. Click **New registration**
4. Configure:
   - **Name**: UnsubscribeEmail (or your preferred name)
   - **Supported account types**: Choose appropriate option (e.g., "Accounts in any organizational directory and personal Microsoft accounts")
   - **Redirect URI**: 
     - Platform: Web
     - URI: `https://localhost:7074/signin-oidc` (adjust port if needed)
5. Click **Register**
6. Note the **Application (client) ID** and **Directory (tenant) ID**

### 3. Configure API Permissions

1. In your app registration, go to **API permissions**
2. Click **Add a permission**
3. Select **Microsoft Graph** > **Delegated permissions**
4. Add the following permissions:
   - `User.Read`
   - `Mail.Read`
5. Click **Add permissions**
6. (Optional) Click **Grant admin consent** if required by your organization

### 4. Create Client Secret

1. Go to **Certificates & secrets**
2. Click **New client secret**
3. Add a description and set expiration
4. Click **Add**
5. **Copy the secret value immediately** (it won't be shown again)

### 5. Configure Application Settings

Create or update `appsettings.json` in the `UnsubscribeEmail` project folder:

```json
{
  "AzureAd": {
    "Instance": "https://login.microsoftonline.com/",
    "Domain": "your-domain.onmicrosoft.com",
    "TenantId": "common",
    "ClientId": "YOUR_APPLICATION_CLIENT_ID",
    "ClientSecret": "YOUR_CLIENT_SECRET",
    "CallbackPath": "/signin-oidc",
    "SignedOutCallbackPath": "/signout-callback-oidc"
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "AllowedHosts": "*"
}
```

**For Personal Microsoft Accounts (outlook.com, hotmail.com, live.com):**
- Set `"TenantId": "common"`
- Or use `"TenantId": "consumers"` to restrict to personal accounts only

**For Work/School Accounts:**
- Set `"TenantId": "organizations"` or use your specific tenant ID

### 6. Download the Phi-3 Model (Optional but Recommended)

The application uses the Phi-3 AI model as a fallback for complex unsubscribe link extraction. While the app works without it using regex patterns, having the model improves accuracy.

#### Install Git LFS

**Windows:**
```bash
# Download and install from https://git-lfs.github.com/
# Or use Chocolatey:
choco install git-lfs

# Initialize Git LFS
git lfs install
```

**macOS:**
```bash
brew install git-lfs
git lfs install
```

**Linux:**
```bash
sudo apt-get install git-lfs  # Debian/Ubuntu
# Or
sudo yum install git-lfs      # RedHat/CentOS

git lfs install
```

#### Clone the Phi-3 Model

```bash
# Navigate to a directory where you want to store the model
# (outside of your project directory is recommended)
cd C:\Users\YourUsername\source\repos  # Windows
# or
cd ~/models  # macOS/Linux

# Clone the model repository (this will download ~2.4GB)
git clone https://huggingface.co/microsoft/Phi-3-mini-4k-instruct-onnx

# The model files will be in:
# Phi-3-mini-4k-instruct-onnx/cpu_and_mobile/cpu-int4-rtn-block-32-acc-level-4/
```

#### Configure Model Path

Update `appsettings.json` to point to the model location:

```json
{
  "Phi3ModelPath": "C:\\Users\\YourUsername\\source\\repos\\Phi-3-mini-4k-instruct-onnx\\cpu_and_mobile\\cpu-int4-rtn-block-32-acc-level-4",
  "AzureAd": {
    // ... rest of config
  }
}
```

**Note:** 
- Use double backslashes (`\\`) in Windows paths in JSON
- Or use forward slashes: `C:/Users/YourUsername/source/repos/...`
- For macOS/Linux: `/Users/YourUsername/models/Phi-3-mini-4k-instruct-onnx/cpu_and_mobile/cpu-int4-rtn-block-32-acc-level-4`

### 7. Run the Application

```bash
cd UnsubscribeEmail
dotnet restore
dotnet build
dotnet run
```

The application will start at `https://localhost:7074` (or the configured port).

## Usage

1. **Navigate to the app** in your browser
2. **Sign in** with your Microsoft account
3. **Go to Unsubscribe Links** page
4. **Click "Start Processing Emails"**
5. **Watch real-time progress** as emails are fetched and processed
6. **Review results** - each sender is listed with their unsubscribe link (if found)
7. **Click "Unsubscribe"** button to visit the unsubscribe page for any sender

## How It Works

### Two-Tier Extraction Strategy

1. **Regex First (Fast)** 
   - Searches for common unsubscribe patterns in email body
   - Finds anchor tags with "unsubscribe", "opt-out", "preferences" text
   - Matches URLs containing unsubscribe-related keywords
   - Returns immediately if found (~90% of cases)

2. **Phi-3 Model Fallback (AI-Powered)**
   - Activates only if regex fails to find a link
   - Extracts 200 characters of context around unsubscribe keywords
   - Uses Microsoft's Phi-3 mini language model for intelligent extraction
   - Handles complex HTML structures and obfuscated links

### Real-Time Updates

- **SignalR** provides live progress updates
- Shows email fetching progress (page by page from Graph API)
- Updates as each sender is processed
- Displays final summary with statistics

## Architecture

- **ASP.NET Core 10** - Web framework
- **Razor Pages** - UI rendering
- **SignalR** - Real-time communication
- **Microsoft Graph API** - Email access via REST API
- **Microsoft Identity Platform** - Authentication
- **Phi-3 ONNX Model** - AI-powered link extraction
- **Regex Patterns** - Fast pattern matching

## Testing

Run the test suite:

```bash
cd UnsubscribeEmail.Tests
dotnet test
```

The test suite includes:
- 27 unit tests for regex extraction and service logic
- 9 integration tests for Phi-3 model (when available)

## Security Notes

- ✅ OAuth 2.0 authentication - no password storage
- ✅ Tokens stored in memory only (cleared on logout)
- ✅ HTTPS enforced in production
- ✅ CSRF protection on all forms
- ✅ Access tokens passed securely to background tasks
- ⚠️ Never commit `appsettings.json` with secrets to source control

## Troubleshooting

### "No account or login hint was passed"
- Ensure you've configured the correct tenant ID for your account type
- For personal accounts, use `"TenantId": "common"` or `"consumers"`
- Clear browser cookies and sign in again

### "MailboxNotEnabledForRESTAPI"
- This account type doesn't support Microsoft Graph
- Ensure you're using a Microsoft 365, Outlook.com, or Office 365 account
- On-premises Exchange mailboxes are not supported

### Model Not Loading
- Verify the `Phi3ModelPath` in `appsettings.json` is correct
- Ensure all model files were downloaded via Git LFS
- Check file permissions on the model directory
- The app will still work using regex-only mode

## License

[Your License Here]

## Contributing

Contributions are welcome! Please open an issue or submit a pull request.

