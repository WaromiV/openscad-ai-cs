using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using c_server.Validation;
using c_server.Validation.Models;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
var validationService = new CgalWorkerValidationService(builder.Environment.ContentRootPath);

const string ServerName = "openscad-render-mcp-csharp";
const string ServerVersion = "0.1.0";
const string ProtocolVersion = "2025-06-18";
const string ToolName = "render_openscad";
const string FixedRenderSize = "1600x1200";
const int FixedWidth = 1600;
const int FixedHeight = 1200;

var toolDescription = string.Concat(
  "Render an OpenSCAD .scad file into 6 deterministic PNG views. ",
  "Returns MCP image content blocks with base64 payloads and fixed XYZ color legend.",
  "The 6 views are: top_ne, top_sw, bottom_ne, bottom_sw, top, and a zoomed x3 version of top_ne. ",
  "OpenSCAD facet controls (the practical FN settings to use) are: ",
  "$fn: fixed number of fragments (explicit polygon sides; great for final quality cylinders/spheres), ",
  "$fa: minimum angle per fragment in degrees (adaptive smoothness based on curvature; good global quality control), ",
  "$fs: minimum fragment size in model units (adaptive smoothness based on physical segment length; good for scale-consistent tessellation). ",
  "How they interact: if $fn is set > 0 it overrides $fa/$fs; if $fn is 0 or unset then OpenSCAD derives fragments from $fa and $fs together. ",
  "Common usage guidance: quick preview uses lower detail (example $fn=24 or coarse $fa/$fs), final export uses higher detail (example $fn=96+, or tighter $fa/$fs such as $fa=4 and $fs=0.5). ",
  "For threaded, press-fit, and screw interfaces, increase tessellation to reduce fit error from faceting. ",
  "IF YOU SEE A MISMATCH YOU MUST CALL THIS TOOL AGAIN AND REVIEW RESULTS AGAIN. YOU MUST PAY ATTENTION TO DETAILS. MOST OF THE TIME YOU WILL NOT BE ABLE TO ONE-SHOT THIS, SO PLEASE REFACTOR UNTIL YOU ARE REALLY CONFIDENT. ",
  "When integrating some parts into you design like bits or screws, you must always think about how they will fit or integrate with the rest of the design. You should always consider the dimensions, tolerances, and how the parts will be assembled together including convergence of holes alowing for screws or press fit, and how the parts will interact with each other in the final design. Always keep in mind the practical aspects of manufacturing and assembly when designing with OpenSCAD or any CAD software. If we are planning to make a hole, check if it doesn't pass through another hole. Mind the fn= parameter."
);

var baseShots = new List<ShotSpec>
{
  new("top_ne", "iso with top + NORTH EAST seen", 55, 0, 45),
  new("top_sw", "iso with top+SOUTH WEST", 55, 0, 225),
  new("bottom_ne", "iso with BOTTOM NORTH EAST", -55, 0, 45),
  new("bottom_sw", "iso with BOTTOM SOUTH WEST", -55, 0, 225),
  new("top", "top view", 0, 0, 0),
};
const string ZoomShotLabel = "zoomed x3 iso with top + NORTH EAST seen";

app.MapGet("/", () => Results.Text("server up", "text/plain"));

app.MapPost("/mcp", async (HttpRequest request) =>
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
      return Results.Ok(JsonRpcResult(requestId, new
      {
        tools = new[]
        {
          new
          {
            name = ToolName,
            description = toolDescription,
            inputSchema = new
            {
              type = "object",
              properties = new
              {
                scad_file_path = new
                {
                  type = "string",
                  description =
                    "Absolute or workspace-relative path to a .scad file on disk. Image size is fixed by server policy.",
                },
              },
              required = new[] { "scad_file_path" },
              additionalProperties = false,
            },
          },
        },
      }));
    }

    if (method == "tools/call")
    {
      if (!root.TryGetProperty("params", out var paramsElement) || paramsElement.ValueKind != JsonValueKind.Object)
      {
        return Results.Ok(JsonRpcError(requestId, -32602, "params must be a JSON object"));
      }

      var name = paramsElement.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
      if (name != ToolName)
      {
        return Results.Ok(JsonRpcError(requestId, -32602, $"Unknown tool: {name}"));
      }

      if (!paramsElement.TryGetProperty("arguments", out var argsElement) ||
          argsElement.ValueKind != JsonValueKind.Object)
      {
        return Results.Ok(JsonRpcError(requestId, -32602, "'arguments' must be a JSON object"));
      }

      var scadPathRaw = argsElement.TryGetProperty("scad_file_path", out var scadPathElement)
        ? scadPathElement.GetString()
        : null;

      if (string.IsNullOrWhiteSpace(scadPathRaw))
      {
        return Results.Ok(JsonRpcError(requestId, -32602, "'scad_file_path' must be a non-empty string"));
      }

      var scadPath = ResolveScadPath(scadPathRaw!);
      if (!scadPath.EndsWith(".scad", StringComparison.OrdinalIgnoreCase))
      {
        return Results.Ok(JsonRpcError(requestId, -32602, "'scad_file_path' must point to a .scad file"));
      }

      if (!File.Exists(scadPath))
      {
        return Results.Ok(JsonRpcError(requestId, -32602, $"SCAD file not found: {scadPath}"));
      }

      try
      {
        var scadCode = await File.ReadAllTextAsync(scadPath);
        var payload = RenderFixedShots(scadCode, scadPath);

        var renders = payload.Renders;
        if (renders.Count != 6)
        {
          return Results.Ok(JsonRpcError(requestId, -32603, $"Renderer returned {renders.Count} images; expected 6"));
        }

        var content = new List<object>
        {
          new
          {
            type = "text",
            text =
              $"OpenSCAD render completed. images={renders.Count}, fixed_size={FixedRenderSize}, source={scadPath}, edge_counter={payload.EdgeCounter}",
          },
        };

        if (!payload.Validation.Ok && payload.Validation.Warnings.Count > 0)
        {
          var summary = string.Join(
            " | ",
            payload.Validation.Warnings.Select(w => $"{w.Code}:{w.Message}")
          );
          content.Add(new
          {
            type = "text",
            text = $"Validation warnings ({payload.Validation.Engine}): {summary}",
          });
        }

        var imageInfo = new List<object>();
        for (var i = 0; i < renders.Count; i++)
        {
          var render = renders[i];
          var imageBytes = await File.ReadAllBytesAsync(render.ImagePath);
          var imageB64 = Convert.ToBase64String(imageBytes);
          var index = i + 1;

          imageInfo.Add(new
          {
            index,
            image_path = render.ImagePath,
            image_size = render.ImageSize,
            mime_type = render.MimeType,
            view_label = render.ViewLabel,
          });

          content.Add(new
          {
            type = "text",
            text =
              $"Image {index}: view={render.ViewLabel}, path={render.ImagePath}, size={render.ImageSize}, mime={render.MimeType}",
          });
          content.Add(new
          {
            type = "image",
            data = imageB64,
            mimeType = render.MimeType,
          });
        }

        var result = new
        {
          content,
          structuredContent = new
          {
            annotation_mode = payload.AnnotationMode,
            overlays = payload.Overlays,
            mesh_stats = payload.MeshStats,
            edge_counter = payload.EdgeCounter,
            shot_policy = payload.ShotPolicy,
            shot_manifest = payload.ShotManifest,
            camera = payload.Camera,
            validation = payload.Validation,
            images = imageInfo,
          },
          isError = false,
        };

        return Results.Ok(JsonRpcResult(requestId, result));
      }
      catch (Exception ex)
      {
        return Results.Ok(JsonRpcError(requestId, -32603, $"OpenSCAD render failed: {ex.Message}"));
      }
    }

    return Results.Ok(JsonRpcError(requestId, -32601, $"Method not found: {method}"));
  }
});

app.Run();

return;

RenderPayload RenderFixedShots(string scadCode, string scadPath)
{
  var projectRoot = ResolveProjectRoot();
  var rendersDir = Path.Combine(projectRoot, "renders");
  Directory.CreateDirectory(rendersDir);

  var stlPath = Path.Combine(rendersDir, $"mcp_mesh_{DateTime.UtcNow:yyyyMMdd_HHmmss_fffffff}.stl");
  try
  {
    ExportAsciiStl(scadPath, stlPath);
    var meshStats = ParseMeshStats(stlPath);
    var validation = validationService.Validate(stlPath);
    var center = ComputeCenter(meshStats);
    var distance = ComputeDistance(meshStats);

    var shotOutputs = new Dictionary<string, RenderItem>(StringComparer.Ordinal);
    foreach (var shot in baseShots)
    {
      var wrapperScad = BuildViewScad(stlPath, shot, center, distance);
      var wrapperPath = Path.Combine(rendersDir, $"mcp_view_{shot.Key}_{DateTime.UtcNow:yyyyMMdd_HHmmss_fffffff}.scad");
      File.WriteAllText(wrapperPath, wrapperScad);
      try
      {
        var outputPng = Path.Combine(rendersDir,
          $"render_{FixedWidth}x{FixedHeight}_{DateTime.UtcNow:yyyyMMdd_HHmmss_fffffff}.png");
        RenderPng(wrapperPath, outputPng, FixedWidth, FixedHeight);
        BakeTopLabelAndLegend(outputPng, shot.Label);

        shotOutputs[shot.Key] = new RenderItem(
          outputPng,
          FixedRenderSize,
          "image/png",
          shot.Key,
          shot.Label,
          1
        );
      }
      finally
      {
        TryDelete(wrapperPath);
      }
    }

    if (!shotOutputs.TryGetValue("top_ne", out var topNeRender))
    {
      throw new InvalidOperationException("Fixed shot 'top_ne' missing");
    }

    var zoomPath = Path.Combine(rendersDir, $"render_zoomx3_{DateTime.UtcNow:yyyyMMdd_HHmmss_fffffff}.png");
    CreateZoomedImage(topNeRender.ImagePath, zoomPath, 3, ZoomShotLabel);
    var zoomRender = new RenderItem(
      zoomPath,
      FixedRenderSize,
      "image/png",
      "top_ne_zoom_x3",
      ZoomShotLabel,
      3
    );

    var ordered = new List<RenderItem>
    {
      shotOutputs["top_ne"],
      zoomRender,
      shotOutputs["top_sw"],
      shotOutputs["bottom_ne"],
      shotOutputs["bottom_sw"],
      shotOutputs["top"],
    };

    return new RenderPayload(
      ordered,
      "deterministic_cli_overlays_with_fixed_view_labels",
      new[] { "axes", "scales", "edges", "top_label", "xyz_legend" },
      meshStats,
      meshStats.UniqueEdgeCount,
      "fixed_6_shot_manifest_v2",
      ordered.Select(x => x.ViewLabel).ToArray(),
      new CameraInfo(new[] { center.X, center.Y, center.Z }, distance, "model_bbox_center"),
      validation
    );
  }
  finally
  {
    TryDelete(stlPath);
  }
}

string ResolveProjectRoot()
{
  var cwd = Directory.GetCurrentDirectory();
  if (Path.GetFileName(cwd).Equals("c_server", StringComparison.OrdinalIgnoreCase))
  {
    return Directory.GetParent(cwd)?.FullName ?? cwd;
  }

  return cwd;
}

string ResolveScadPath(string rawPath)
{
  if (Path.IsPathRooted(rawPath))
  {
    return Path.GetFullPath(rawPath);
  }

  return Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), rawPath));
}

void ExportAsciiStl(string scadPath, string stlPath)
{
  RunOpenScad(new[]
  {
    "-o", stlPath,
    "--export-format", "asciistl",
    scadPath,
    "--render",
  });
}

void RenderPng(string scadPath, string outputPng, int width, int height)
{
  RunOpenScad(new[]
  {
    "-o", outputPng,
    scadPath,
    "--render",
    $"--imgsize={width},{height}",
    "--projection=o",
    "--view=axes,scales,edges",
  });
}

void RunOpenScad(IEnumerable<string> args)
{
  var psi = new ProcessStartInfo
  {
    FileName = "openscad",
    RedirectStandardOutput = true,
    RedirectStandardError = true,
    UseShellExecute = false,
  };
  foreach (var arg in args)
  {
    psi.ArgumentList.Add(arg);
  }

  using var process = Process.Start(psi);
  if (process is null)
  {
    throw new InvalidOperationException("Failed to start openscad process");
  }

  var stdout = process.StandardOutput.ReadToEnd();
  var stderr = process.StandardError.ReadToEnd();
  process.WaitForExit();

  if (process.ExitCode != 0)
  {
    var detail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
    throw new InvalidOperationException(string.IsNullOrWhiteSpace(detail) ? "Unknown OpenSCAD error" : detail.Trim());
  }
}

string BuildViewScad(string stlPath, ShotSpec shot, Vec3 center, double vpd)
{
  var escapedPath = stlPath.Replace("\\", "/").Replace("\"", "\\\"");
  var sb = new StringBuilder();
  sb.Append("$vpr=[")
    .Append(shot.Rx).Append(',').Append(shot.Ry).Append(',').Append(shot.Rz).AppendLine("]; ")
    .Append("$vpt=[")
    .Append(center.X.ToString(CultureInfo.InvariantCulture)).Append(',')
    .Append(center.Y.ToString(CultureInfo.InvariantCulture)).Append(',')
    .Append(center.Z.ToString(CultureInfo.InvariantCulture)).AppendLine("]; ")
    .Append("$vpd=")
    .Append(vpd.ToString(CultureInfo.InvariantCulture)).AppendLine("; ")
    .Append("import(\"").Append(escapedPath).AppendLine("\", convexity=10);");
  return sb.ToString();
}

MeshStats ParseMeshStats(string stlPath)
{
  var vertices = new List<Vec3>();
  var triangles = new List<(Vec3 A, Vec3 B, Vec3 C)>();
  var current = new List<Vec3>(3);

  foreach (var line in File.ReadLines(stlPath))
  {
    var trimmed = line.Trim();
    if (!trimmed.StartsWith("vertex ", StringComparison.Ordinal))
    {
      continue;
    }

    var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    if (parts.Length < 4)
    {
      continue;
    }

    if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ||
        !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var y) ||
        !double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
    {
      continue;
    }

    var v = new Vec3(Math.Round(x, 6), Math.Round(y, 6), Math.Round(z, 6));
    vertices.Add(v);
    current.Add(v);
    if (current.Count == 3)
    {
      triangles.Add((current[0], current[1], current[2]));
      current.Clear();
    }
  }

  if (vertices.Count == 0)
  {
    return new MeshStats(0, 0, 0, new[] { 0d, 0d, 0d }, new[] { 0d, 0d, 0d }, new[] { 0d, 0d, 0d });
  }

  var uniqueVertices = new HashSet<string>(vertices.Select(VertexKey));
  var uniqueEdges = new HashSet<string>(StringComparer.Ordinal);
  foreach (var (a, b, c) in triangles)
  {
    AddEdge(a, b, uniqueEdges);
    AddEdge(b, c, uniqueEdges);
    AddEdge(c, a, uniqueEdges);
  }

  var minX = vertices.Min(v => v.X);
  var minY = vertices.Min(v => v.Y);
  var minZ = vertices.Min(v => v.Z);
  var maxX = vertices.Max(v => v.X);
  var maxY = vertices.Max(v => v.Y);
  var maxZ = vertices.Max(v => v.Z);

  return new MeshStats(
    uniqueVertices.Count,
    triangles.Count,
    uniqueEdges.Count,
    new[] { minX, minY, minZ },
    new[] { maxX, maxY, maxZ },
    new[]
    {
      Math.Round(maxX - minX, 6),
      Math.Round(maxY - minY, 6),
      Math.Round(maxZ - minZ, 6),
    }
  );
}

void AddEdge(Vec3 p, Vec3 q, HashSet<string> edges)
{
  var a = VertexKey(p);
  var b = VertexKey(q);
  var key = string.CompareOrdinal(a, b) <= 0 ? $"{a}|{b}" : $"{b}|{a}";
  edges.Add(key);
}

string VertexKey(Vec3 v) =>
  FormattableString.Invariant($"{v.X:0.######},{v.Y:0.######},{v.Z:0.######}");

Vec3 ComputeCenter(MeshStats stats)
{
  return new Vec3(
    Math.Round((stats.BboxMin[0] + stats.BboxMax[0]) / 2.0, 6),
    Math.Round((stats.BboxMin[1] + stats.BboxMax[1]) / 2.0, 6),
    Math.Round((stats.BboxMin[2] + stats.BboxMax[2]) / 2.0, 6)
  );
}

double ComputeDistance(MeshStats stats)
{
  var sx = stats.BboxSize[0];
  var sy = stats.BboxSize[1];
  var sz = stats.BboxSize[2];
  var diag = Math.Sqrt((sx * sx) + (sy * sy) + (sz * sz));
  return Math.Round(Math.Max(40.0, diag * 2.4), 4);
}

void BakeTopLabelAndLegend(string imagePath, string labelText)
{
  using var image = Image.Load<Rgba32>(imagePath);
  var width = image.Width;
  var height = image.Height;

  var bannerHeight = Math.Max(46, (int)(height * 0.09));
  image.Mutate(ctx => ctx.Fill(new Rgba32(0, 0, 0, 200), new Rectangle(0, 0, width, bannerHeight)));

  var font = ResolveFont(Math.Max(18, (int)(height * 0.032f)));
  if (font is not null)
  {
    var options = new TextOptions(font)
    {
      Dpi = 72,
    };
    var textSize = TextMeasurer.MeasureSize(labelText, options);
    var x = Math.Max(12, (width - textSize.Width) / 2f);
    var y = Math.Max(6, (bannerHeight - textSize.Height) / 2f);
    image.Mutate(ctx =>
    {
      ctx.DrawText(labelText, font, Color.Black, new PointF(x + 1, y + 1));
      ctx.DrawText(labelText, font, Color.White, new PointF(x, y));
    });
  }

  var pad = Math.Max(8, (int)(Math.Min(width, height) * 0.02));
  var boxW = Math.Max(150, (int)(width * 0.18));
  var boxH = Math.Max(72, (int)(height * 0.12));
  image.Mutate(ctx => ctx.Fill(new Rgba32(0, 0, 0, 170), new Rectangle(pad, pad, boxW, boxH)));

  var legendFont = ResolveFont(Math.Max(14, (int)(height * 0.028f)));
  var x0 = pad + 14;
  var y0 = pad + 16;
  var spacing = Math.Max(18, (int)(boxH * 0.28));
  var lineLen = Math.Max(22, (int)(boxW * 0.24));
  var lineWidth = Math.Max(2f, (float)(height * 0.004));

  var legend = new (string Label, Color Color)[]
  {
    ("X", Color.FromRgb(235, 64, 52)),
    ("Y", Color.FromRgb(60, 179, 113)),
    ("Z", Color.FromRgb(65, 105, 225)),
  };

  image.Mutate(ctx =>
  {
    foreach (var (idx, entry) in legend.Select((value, idx) => (idx, value)))
    {
      var y = y0 + (idx * spacing);
      ctx.DrawLine(entry.Color, lineWidth, new PointF(x0, y), new PointF(x0 + lineLen, y));
      if (legendFont is not null)
      {
        ctx.DrawText(entry.Label, legendFont, Color.Black,
          new PointF(x0 + lineLen + 11, y - (legendFont.Size * 0.45f) + 1));
        ctx.DrawText(entry.Label, legendFont, entry.Color,
          new PointF(x0 + lineLen + 10, y - (legendFont.Size * 0.45f)));
      }
    }
  });

  image.Save(imagePath, new PngEncoder());
}

Font? ResolveFont(float size)
{
  var candidates = new[]
  {
    "DejaVu Sans",
    "Liberation Sans",
    "Arial",
  };
  foreach (var family in candidates)
  {
    if (SystemFonts.TryGet(family, out var found))
    {
      return found.CreateFont(size, FontStyle.Bold);
    }
  }

  return null;
}

void CreateZoomedImage(string sourcePath, string outputPath, int zoomFactor, string labelText)
{
  using var source = Image.Load<Rgba32>(sourcePath);
  var width = source.Width;
  var height = source.Height;

  var cropW = Math.Max(2, width / zoomFactor);
  var cropH = Math.Max(2, height / zoomFactor);
  var left = Math.Max(0, (width - cropW) / 2);
  var top = Math.Max(0, (height - cropH) / 2);

  source.Mutate(ctx =>
  {
    ctx.Crop(new Rectangle(left, top, cropW, cropH));
    ctx.Resize(width, height, KnownResamplers.Lanczos3);
  });
  source.Save(outputPath, new PngEncoder());
  BakeTopLabelAndLegend(outputPath, labelText);
}

void TryDelete(string path)
{
  try
  {
    if (File.Exists(path))
    {
      File.Delete(path);
    }
  }
  catch
  {
  }
}

object JsonRpcResult(object id, object result) => new
{
  jsonrpc = "2.0",
  id,
  result,
};

object JsonRpcError(object id, int code, string message) => new
{
  jsonrpc = "2.0",
  id,
  error = new { code, message },
};

record ShotSpec(string Key, string Label, int Rx, int Ry, int Rz);

record RenderItem(
  string ImagePath,
  string ImageSize,
  string MimeType,
  string ViewKey,
  string ViewLabel,
  int ZoomFactor
);

record CameraInfo(
  [property: JsonPropertyName("vpt_center")]
  double[] VptCenter,
  [property: JsonPropertyName("vpd")] double Vpd,
  [property: JsonPropertyName("mode")] string Mode
);

record MeshStats(
  [property: JsonPropertyName("vertex_count")]
  int VertexCount,
  [property: JsonPropertyName("triangle_count")]
  int TriangleCount,
  [property: JsonPropertyName("unique_edge_count")]
  int UniqueEdgeCount,
  [property: JsonPropertyName("bbox_min")]
  double[] BboxMin,
  [property: JsonPropertyName("bbox_max")]
  double[] BboxMax,
  [property: JsonPropertyName("bbox_size")]
  double[] BboxSize
);

record RenderPayload(
  List<RenderItem> Renders,
  string AnnotationMode,
  string[] Overlays,
  MeshStats MeshStats,
  int EdgeCounter,
  string ShotPolicy,
  string[] ShotManifest,
  CameraInfo Camera,
  ValidationReport Validation
);

record Vec3(double X, double Y, double Z);