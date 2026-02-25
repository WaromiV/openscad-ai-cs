using System.Text.Json;

namespace c_server.Tools;

/// <summary>Represents an MCP tool that can be invoked via the tools/call method.</summary>
public interface IMcpTool
{
  /// <summary>Unique tool name exposed to MCP clients.</summary>
  string Name { get; }

  /// <summary>Human-readable description with usage guidance for LLMs.</summary>
  string Description { get; }

  /// <summary>JSON Schema definition for tool input parameters.</summary>
  object InputSchema { get; }

  /// <summary>Executes the tool with the provided arguments.</summary>
  /// <param name="args">JSON element containing tool arguments.</param>
  /// <returns>MCP result object containing content and structured data.</returns>
  Task<object> ExecuteAsync(JsonElement args);
}
