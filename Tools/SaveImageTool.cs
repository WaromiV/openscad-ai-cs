using System.Text.Json;

namespace c_server.Tools;

/// <summary>Saves a base64-encoded image payload to disk and returns the file path.</summary>
public sealed class SaveImageTool : IMcpTool
{
  /// <summary>Tool name exposed to MCP clients.</summary>
  public string Name => "save_image";

  /// <summary>Human-readable description for MCP tool discovery.</summary>
  public string Description => string.Concat(
    "Save a base64-encoded image payload to disk under the server workspace. ",
    "Use this to persist in-chat reference images so they can be compared later."
  );

  /// <summary>JSON schema describing input arguments for the tool.</summary>
  public object InputSchema => new
  {
    type = "object",
    properties = new
    {
      image_base64 = new
      {
        type = "string",
        description = "Base64-encoded image data (PNG or JPEG).",
      },
      file_name = new
      {
        type = "string",
        description = "File name to save as (e.g. reference_top_ne.png).",
      },
      subdir = new
      {
        type = "string",
        description = "Optional subdirectory under the workspace to store the image (default: reference).",
      },
    },
    required = (string[])["image_base64", "file_name"],
    additionalProperties = false,
  };

  /// <summary>Saves the provided base64 image to disk.</summary>
  /// <param name="args">JSON arguments containing the image payload and target name.</param>
  /// <returns>Structured MCP response payload.</returns>
  public Task<object> ExecuteAsync(JsonElement args)
  {
    // Validate input arguments and resolve the target output path.
    var imageBase64 = args.TryGetProperty("image_base64", out var imageBase64Element)
      ? imageBase64Element.GetString()
      : null;
    var fileName = args.TryGetProperty("file_name", out var fileNameElement)
      ? fileNameElement.GetString()
      : null;
    var subdir = args.TryGetProperty("subdir", out var subdirElement)
      ? subdirElement.GetString()
      : null;

    if (string.IsNullOrWhiteSpace(imageBase64))
    {
      throw new ArgumentException("'image_base64' must be a non-empty string");
    }

    if (string.IsNullOrWhiteSpace(fileName))
    {
      throw new ArgumentException("'file_name' must be a non-empty string");
    }

    var projectRoot = Directory.GetCurrentDirectory();
    var targetDir = Path.Combine(projectRoot, string.IsNullOrWhiteSpace(subdir) ? "reference" : subdir!);
    Directory.CreateDirectory(targetDir);

    var safeFileName = Path.GetFileName(fileName);
    var targetPath = Path.Combine(targetDir, safeFileName);

    // Decode the base64 payload and persist the image to disk.
    byte[] imageBytes;
    try
    {
      imageBytes = Convert.FromBase64String(imageBase64);
    }
    catch (FormatException ex)
    {
      throw new ArgumentException($"Invalid base64 payload: {ex.Message}");
    }

    File.WriteAllBytes(targetPath, imageBytes);

    // Return the stored path so downstream tools can consume it.
    return Task.FromResult<object>(new
    {
      content = (object[])
      [
        new
        {
          type = "text",
          text = $"Image saved to {targetPath}",
        },
      ],
      structuredContent = new
      {
        image_path = targetPath,
      },
      isError = false,
    });
  }
}
