using System.Text.Json;
using c_server.Tools;
using c_server.Validation;

namespace c_server;

/// <summary>Entry point and MCP JSON-RPC router for the server.</summary>
public static class Program
{
  /// <summary>Server name exposed during MCP initialization.</summary>
  private const string ServerName = "openscad-render-mcp-csharp";
  /// <summary>Server version exposed during MCP initialization.</summary>
  private const string ServerVersion = "0.1.0";
  /// <summary>Supported MCP protocol version.</summary>
  private const string ProtocolVersion = "2025-06-18";

  /// <summary>Registry of MCP tools available to JSON-RPC callers.</summary>
  private static McpToolRegistry toolRegistry = null!;

  /// <summary>Configures and runs the HTTP server.</summary>
  /// <param name="args">Command-line arguments.</param>
  public static void Main(string[] args)
  {
    // Build the ASP.NET Core application and dependencies.
    var builder = WebApplication.CreateBuilder(args);
    var app = builder.Build();

    // Register tool implementations for MCP requests.
    var validationService = new CgalWorkerValidationService(builder.Environment.ContentRootPath);
    toolRegistry = new McpToolRegistry(
    [
      new RenderOpenScadTool(validationService, builder.Environment.ContentRootPath),
      new CompareRendersTool(),
      new SaveImageTool(),
    ]);

    // Map HTTP endpoints for health and MCP JSON-RPC.
    app.MapGet("/", () => Results.Text("server up", "text/plain"));
    app.MapPost("/mcp", HandleMcpRequest);

    // Start the server.
    app.Run();
  }

  /// <summary>Handles MCP JSON-RPC requests for tool discovery and execution.</summary>
  /// <param name="request">Incoming HTTP request.</param>
  /// <returns>JSON-RPC response.</returns>
  private static async Task<IResult> HandleMcpRequest(HttpRequest request)
  {
    JsonDocument body;
    try
    {
      // Parse the incoming JSON payload.
      body = await JsonDocument.ParseAsync(request.Body);
    }
    catch (JsonException ex)
    {
      return Results.BadRequest(new
      {
        jsonrpc = "2.0",
        error = new { code = -32700, message = $"Invalid JSON: {ex.Message}" },
      });
    }

    using (body)
    {
      // Extract and validate the JSON-RPC method name.
      var root = body.RootElement;
      if (!root.TryGetProperty("method", out var methodElement) || methodElement.ValueKind != JsonValueKind.String)
      {
        return Results.BadRequest(new
        {
          jsonrpc = "2.0",
          error = new { code = -32600, message = "Request method must be a string" },
        });
      }

      var method = methodElement.GetString() ?? string.Empty;
      object? requestId = null;
      if (root.TryGetProperty("id", out var idElement))
      {
        requestId = JsonSerializer.Deserialize<object>(idElement.GetRawText());
      }

      // Ignore notifications and requests without an id.
      if (method == "notifications/initialized" || requestId is null)
      {
        return Results.Accepted();
      }

      // Enforce JSON-RPC 2.0.
      if (!root.TryGetProperty("jsonrpc", out var jsonrpcElement) || jsonrpcElement.GetString() != "2.0")
      {
        return Results.Ok(JsonRpcError(requestId, -32600, "Only JSON-RPC 2.0 is supported"));
      }

      // MCP initialization handshake.
      if (method == "initialize")
      {
        return Results.Ok(JsonRpcResult(requestId, new
        {
          protocolVersion = ProtocolVersion,
          capabilities = new { tools = new { listChanged = false } },
          serverInfo = new { name = ServerName, version = ServerVersion },
          instructions = "Use render_openscad with scad_file_path. Image size is fixed by server policy.",
        }));
      }

      // Health check endpoint for MCP clients.
      if (method == "ping")
      {
        return Results.Ok(JsonRpcResult(requestId, new { }));
      }

      // List all available tools.
      if (method == "tools/list")
      {
        var tools = toolRegistry.GetAll().Select(tool => new
        {
          name = tool.Name,
          description = tool.Description,
          inputSchema = tool.InputSchema,
        }).ToArray();

        return Results.Ok(JsonRpcResult(requestId, new { tools }));
      }

      // Execute a specific tool with the provided arguments.
      if (method == "tools/call")
      {
        if (!root.TryGetProperty("params", out var paramsElement) || paramsElement.ValueKind != JsonValueKind.Object)
        {
          return Results.Ok(JsonRpcError(requestId, -32602, "params must be a JSON object"));
        }

        // Resolve the tool by name from the registry.
        var name = paramsElement.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
        if (string.IsNullOrEmpty(name) || !toolRegistry.TryGetTool(name, out var tool))
        {
          return Results.Ok(JsonRpcError(requestId, -32602, $"Unknown tool: {name}"));
        }

        // Extract and validate arguments.
        if (!paramsElement.TryGetProperty("arguments", out var argsElement) ||
            argsElement.ValueKind != JsonValueKind.Object)
        {
          return Results.Ok(JsonRpcError(requestId, -32602, "'arguments' must be a JSON object"));
        }

        try
        {
          // Delegate execution to the tool implementation.
          var result = await tool!.ExecuteAsync(argsElement);
          return Results.Ok(JsonRpcResult(requestId, result));
        }
        catch (Exception ex)
        {
          // Surface tool errors as JSON-RPC failures.
          return Results.Ok(JsonRpcError(requestId, -32603, $"Tool execution failed: {ex.Message}"));
        }
      }

      // Default JSON-RPC method not found handler.
      return Results.Ok(JsonRpcError(requestId, -32601, $"Method not found: {method}"));
    }
  }

  /// <summary>Wraps a successful JSON-RPC result.</summary>
  /// <param name="id">Request identifier.</param>
  /// <param name="result">Result payload.</param>
  /// <returns>JSON-RPC 2.0 result object.</returns>
  private static object JsonRpcResult(object id, object result) => new
  {
    jsonrpc = "2.0",
    id,
    result,
  };

  /// <summary>Wraps a JSON-RPC error response.</summary>
  /// <param name="id">Request identifier.</param>
  /// <param name="code">JSON-RPC error code.</param>
  /// <param name="message">Error message.</param>
  /// <returns>JSON-RPC 2.0 error object.</returns>
  private static object JsonRpcError(object id, int code, string message) => new
  {
    jsonrpc = "2.0",
    id,
    error = new { code, message },
  };
}
