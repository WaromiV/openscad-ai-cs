using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace c_server.Tools;

/// <summary>Saves a base64-encoded image payload to disk and returns the file path.</summary>
[McpServerToolType]
public sealed class SaveImageTool
{
  /// <summary>Tool description presented to MCP clients.</summary>
  private const string ToolDescription = "Save a base64-encoded image payload to disk under the server workspace. " +
    "Use this to persist in-chat reference images so they can be compared later.";

  /// <summary>Saves the provided base64 image to disk.</summary>
  /// <param name="image_base64">Base64-encoded image data (PNG or JPEG).</param>
  /// <param name="file_name">File name to save as.</param>
  /// <param name="subdir">Optional subdirectory under the workspace to store the image.</param>
  /// <param name="cancellationToken">Token used to cancel the operation.</param>
  /// <returns>Structured MCP response payload.</returns>
  [McpServerTool(Name = "save_image")]
  [Description(ToolDescription)]
  public Task<CallToolResult> SaveImage(
    [Description("Base64-encoded image data (PNG or JPEG).")]
    string image_base64,
    [Description("File name to save as (e.g. reference_top_ne.png).")]
    string file_name,
    [Description("Optional subdirectory under the workspace to store the image (default: reference).")]
    string? subdir,
    CancellationToken cancellationToken)
  {
    // Validate input arguments and resolve the target output path.
    cancellationToken.ThrowIfCancellationRequested();
    if (string.IsNullOrWhiteSpace(image_base64))
    {
      throw new ArgumentException("'image_base64' must be a non-empty string");
    }

    if (string.IsNullOrWhiteSpace(file_name))
    {
      throw new ArgumentException("'file_name' must be a non-empty string");
    }

    var projectRoot = Directory.GetCurrentDirectory();
    var targetDir = Path.Combine(projectRoot, string.IsNullOrWhiteSpace(subdir) ? "reference" : subdir!);
    Directory.CreateDirectory(targetDir);

    var safeFileName = Path.GetFileName(file_name);
    var targetPath = Path.Combine(targetDir, safeFileName);

    // Decode the base64 payload and persist the image to disk.
    byte[] imageBytes;
    try
    {
      imageBytes = Convert.FromBase64String(image_base64);
    }
    catch (FormatException ex)
    {
      throw new ArgumentException($"Invalid base64 payload: {ex.Message}");
    }

    File.WriteAllBytes(targetPath, imageBytes);

    // Return the stored path so downstream tools can consume it.
    var structuredContent = JsonSerializer.SerializeToNode(new
    {
      image_path = targetPath,
    });

    return Task.FromResult(new CallToolResult
    {
      Content = new List<ContentBlock>
      {
        new TextContentBlock
        {
          Text = $"Image saved to {targetPath}",
        },
      },
      StructuredContent = structuredContent,
      IsError = false,
    });
  }
}
