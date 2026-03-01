using c_server.Validation;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Server;

namespace c_server;

/// <summary>Entry point for the MCP server.</summary>
public static class Program
{
  /// <summary>Configures and runs the HTTP server.</summary>
  /// <param name="args">Command-line arguments.</param>
  public static void Main(string[] args)
  {
    // Build the ASP.NET Core application and dependencies.
    var builder = WebApplication.CreateBuilder(args);
    builder.Services.AddSingleton(new CgalWorkerValidationService(builder.Environment.ContentRootPath));
    builder.Services.AddMcpServer()
      .WithHttpTransport()
      .WithToolsFromAssembly();
    var app = builder.Build();

    // Map HTTP endpoints for health and MCP.
    app.MapGet("/", () => Results.Text("server up", "text/plain"));
    app.MapMcp("/mcp");

    // Start the server.
    app.Run();
  }
}
