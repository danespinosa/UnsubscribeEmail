# UnsubscribeEmail MCP Server

A .NET MCP (Model Context Protocol) server that exposes email reading capabilities as tools for LLM agents like GitHub Copilot and Claude.

## What it does

The MCP server lets an LLM agent:
1. Configure an Azure AD app (provide existing credentials or auto-create via Azure CLI)
2. Log in to a Microsoft email account via interactive browser auth
3. Read and aggregate emails by sender, including HTML content
4. Fetch full email content from specific senders

The LLM agent then inspects the HTML to find unsubscribe links and presents them in a table.

## MCP Tools

| Tool | Description |
|------|-------------|
| `configure_aad_app` | Set up AAD app credentials (manual or auto-create via `az cli`) |
| `login` | Interactive browser login to Microsoft account |
| `read_emails` | Fetch emails aggregated by sender with HTML content |
| `get_email_content` | Get full HTML body for a specific sender's emails |

## Setup

### Prerequisites
- .NET 10.0 SDK
- Azure CLI (`az`) if you want to auto-create an AAD app

### Configuration for VS Code (GitHub Copilot)

Add to your VS Code `settings.json` or `.vscode/mcp.json`:

```json
{
  "mcpServers": {
    "unsubscribe-email": {
      "type": "stdio",
      "command": "dotnet",
      "args": ["run", "--project", "C:/path/to/UnsubscribeEmail.McpServer"]
    }
  }
}
```

### Configuration for Claude Desktop

Add to `claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "unsubscribe-email": {
      "command": "dotnet",
      "args": ["run", "--project", "C:/path/to/UnsubscribeEmail.McpServer"]
    }
  }
}
```

## Usage Flow

1. **Configure**: The LLM calls `configure_aad_app` with your Azure AD credentials (or auto-creates one)
2. **Login**: The LLM calls `login` which opens your browser for Microsoft authentication
3. **Read Emails**: The LLM calls `read_emails` with a day range (e.g., 30 days) to get all emails aggregated by sender
4. **Extract Links**: The LLM inspects the HTML content to find unsubscribe links
5. **Deep Dive**: If needed, the LLM calls `get_email_content` for more emails from a specific sender

The LLM will then present a formatted table of senders and their unsubscribe links.
