using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using UnsubscribeEmail.McpServer.Services;

var builder = Host.CreateApplicationBuilder(args);

// MCP servers communicate over stdio, so redirect all logs to stderr
builder.Logging.AddConsole(options =>
{
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});

// Register application services as singletons so state persists across tool calls
builder.Services.AddSingleton<AuthService>();
builder.Services.AddSingleton<GraphEmailService>();

// Register MCP server with stdio transport and auto-discover tools from this assembly
builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new()
        {
            Name = "UnsubscribeEmail",
            Version = "1.0.0"
        };
    })
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
