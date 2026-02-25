namespace c_server.Tools;

/// <summary>Registry for discovering and accessing MCP tools.</summary>
public sealed class McpToolRegistry
{
  /// <summary>Lookup of tools by name.</summary>
  private readonly Dictionary<string, IMcpTool> tools;

  /// <summary>Creates a registry from a set of tool implementations.</summary>
  /// <param name="tools">Tools to register.</param>
  public McpToolRegistry(IEnumerable<IMcpTool> tools)
  {
    this.tools = tools.ToDictionary(t => t.Name, StringComparer.Ordinal);
  }

  /// <summary>Gets all registered tools.</summary>
  public IReadOnlyCollection<IMcpTool> GetAll() => tools.Values;

  /// <summary>Attempts to retrieve a tool by name.</summary>
  public bool TryGetTool(string name, out IMcpTool? tool) => tools.TryGetValue(name, out tool);
}
