# UnsubscribeEmail MCP Server

A .NET MCP (Model Context Protocol) server that exposes email reading capabilities as tools for LLM agents like GitHub Copilot and Claude.

## What it does

The MCP server lets an LLM agent:
1. Configure an Azure AD app (provide existing credentials or auto-create via Azure CLI)
2. Log in to a Microsoft email account via interactive browser auth
3. Read and aggregate emails by sender, including HTML content
4. Fetch full email content from specific senders
5. Search bounded message metadata without changing mailbox state
6. List attachment metadata for an exact Graph message ID
7. Download bounded file or item attachment content as base64

The LLM agent then inspects the HTML to find unsubscribe links and presents them in a table.

## MCP Tools

| Tool | Description |
|------|-------------|
| `configure_aad_app` | Set up AAD app credentials (manual or auto-create via `az cli`) |
| `login` | Interactive browser login to Microsoft account |
| `read_emails` | Fetch emails aggregated by sender with HTML content |
| `get_email_content` | Get full HTML body for a specific sender's emails |
| `search_emails` | Search newest-first message metadata (default 20, maximum 50) without fetching attachment metadata |
| `mark_emails_as_read` | Mark matching messages as read or preview the changes |
| `list_email_attachments` | List bounded attachment metadata for an exact message ID |
| `download_email_attachment` | Download a bounded file or item attachment as base64 |

## Setup

### Prerequisites
- .NET 10.0 SDK
- Azure CLI (`az`) if you want to auto-create an AAD app

### Build the DLL

```bash
dotnet publish UnsubscribeEmail.McpServer -c Release -o ./publish
```

This produces `publish/UnsubscribeEmail.McpServer.dll` which can be distributed and run with `dotnet UnsubscribeEmail.McpServer.dll`.

### Configuration for VS Code (GitHub Copilot)

Add to your VS Code `settings.json` or `.vscode/mcp.json`:

```json
{
  "mcpServers": {
    "unsubscribe-email": {
      "type": "stdio",
      "command": "dotnet",
      "args": ["C:/path/to/UnsubscribeEmail.McpServer.dll"]
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
      "args": ["C:/path/to/UnsubscribeEmail.McpServer.dll"]
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
6. **Search (optional)**: The LLM calls `search_emails` with at least one filter for a capped, newest-first metadata-only result. It returns exact message IDs and does not issue N+1 attachment requests.
7. **List Attachments**: The LLM passes the exact `messageId` from `get_email_content`, `mark_emails_as_read`, `read_emails`, or `search_emails` to `list_email_attachments`
8. **Download Attachment**: The LLM passes that exact `messageId` and an exact `attachmentId` from the list response to `download_email_attachment`

Attachment listing returns Graph base metadata plus file-attachment `contentId` through a derived-type OData select, and never selects or includes other subtype-only fields or `contentBytes`. Downloaded content is bounded (4,000,000 bytes by default and 10,000,000 bytes maximum) and returned in the `base64Content` field with the response MIME type and inline/content ID metadata. Graph's metadata `size` and the actual `downloadedSize` from `/$value` can differ because they describe different representations; differing values do not imply truncation when the bounded stream completes. File and item attachments use Graph's `/$value` endpoint; reference attachments are reported by type but rejected for download because they expose a URL rather than message content. `get_email_content` retains its legacy `Id` property and also returns additive camel-case `messageId`; `read_emails` returns the exact `sampleMessageId` for the message represented by each sample body.

The LLM will then present a formatted table of senders and their unsubscribe links.
