using System.Text.Json;
using c_server.Tools;
using c_server.Validation;

namespace c_server;

public static class Program
{
  private const string ServerName = "openscad-render-mcp-csharp";
  private const string ServerVersion = "0.1.0";
  private const string ProtocolVersion = "2025-06-18";

  private static McpToolRegistry toolRegistry = null!;

  public static void Main(string[] args)
  {
    var builder = WebApplication.CreateBuilder(args);
    var app = builder.Build();

    var validationService = new CgalWorkerValidationService(builder.Environment.ContentRootPath);
    toolRegistry = new McpToolRegistry(
    [
      new RenderOpenScadTool(validationService, builder.Environment.ContentRootPath),
      new CompareRendersTool(),
    ]);

    app.MapGet("/", () => Results.Text("server up", "text/plain"));
    app.MapPost("/mcp", HandleMcpRequest);

    app.Run();
  }

  private static async Task<IResult> HandleMcpRequest(HttpRequest request)
  {
    JsonDocument body;
    try
    {
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

      if (method == "notifications/initialized" || requestId is null)
      {
        return Results.Accepted();
      }

      if (!root.TryGetProperty("jsonrpc", out var jsonrpcElement) || jsonrpcElement.GetString() != "2.0")
      {
        return Results.Ok(JsonRpcError(requestId, -32600, "Only JSON-RPC 2.0 is supported"));
      }

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

      if (method == "ping")
      {
        return Results.Ok(JsonRpcResult(requestId, new { }));
      }

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

      if (method == "tools/call")
      {
        if (!root.TryGetProperty("params", out var paramsElement) || paramsElement.ValueKind != JsonValueKind.Object)
        {
          return Results.Ok(JsonRpcError(requestId, -32602, "params must be a JSON object"));
        }

        var name = paramsElement.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
        if (string.IsNullOrEmpty(name) || !toolRegistry.TryGetTool(name, out var tool))
        {
          return Results.Ok(JsonRpcError(requestId, -32602, $"Unknown tool: {name}"));
        }

        if (!paramsElement.TryGetProperty("arguments", out var argsElement) ||
            argsElement.ValueKind != JsonValueKind.Object)
        {
          return Results.Ok(JsonRpcError(requestId, -32602, "'arguments' must be a JSON object"));
        }

        try
        {
          var result = await tool!.ExecuteAsync(argsElement);
          return Results.Ok(JsonRpcResult(requestId, result));
        }
        catch (Exception ex)
        {
          return Results.Ok(JsonRpcError(requestId, -32603, $"Tool execution failed: {ex.Message}"));
        }
      }

      return Results.Ok(JsonRpcError(requestId, -32601, $"Method not found: {method}"));
    }
  }

  private static object JsonRpcResult(object id, object result) => new
  {
    jsonrpc = "2.0",
    id,
    result,
  };

  private static object JsonRpcError(object id, int code, string message) => new
  {
    jsonrpc = "2.0",
    id,
    error = new { code, message },
  };
}
