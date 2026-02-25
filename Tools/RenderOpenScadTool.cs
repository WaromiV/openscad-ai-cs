using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using c_server.Validation;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace c_server.Tools;

/// <summary>Renders OpenSCAD files into deterministic multi-view PNG outputs.</summary>
public sealed class RenderOpenScadTool : IMcpTool
{
  /// <summary>Fixed render size string reported to MCP clients.</summary>
  private const string FixedRenderSize = "1600x1200";
  /// <summary>Fixed render width in pixels.</summary>
  private const int FixedWidth = 1600;
  /// <summary>Fixed render height in pixels.</summary>
  private const int FixedHeight = 1200;
  /// <summary>Label for the zoomed top-right render.</summary>
  private const string ZoomShotLabel = "zoomed x3 iso with top + NORTH EAST seen";

  /// <summary>CGAL validation service used for mesh checks.</summary>
  private readonly CgalWorkerValidationService validationService;
  /// <summary>Content root used to resolve default paths.</summary>
  private readonly string contentRoot;

  /// <summary>Base render shots produced for each request.</summary>
  private readonly List<ShotSpec> baseShots =
  [
    new("top_ne", "iso with top + NORTH EAST seen", 55, 0, 45),
    new("top_sw", "iso with top+SOUTH WEST", 55, 0, 225),
    new("bottom_ne", "iso with BOTTOM NORTH EAST", -55, 0, 45),
    new("bottom_sw", "iso with BOTTOM SOUTH WEST", -55, 0, 225),
    new("top", "top view", 0, 0, 0),
  ];

  /// <summary>Creates a render tool using the provided validation service.</summary>
  /// <param name="validationService">CGAL validation service.</param>
  /// <param name="contentRoot">Content root for resolving output paths.</param>
  public RenderOpenScadTool(CgalWorkerValidationService validationService, string contentRoot)
  {
    this.validationService = validationService;
    this.contentRoot = contentRoot;
  }

  /// <summary>Tool name exposed to MCP clients.</summary>
  public string Name => "render_openscad";

  /// <summary>Human-readable description for MCP tool discovery.</summary>
  public string Description => string.Concat(
    "Render an OpenSCAD .scad file into 6 deterministic PNG views. ",
    "Returns MCP image content blocks with base64 payloads and fixed XYZ color legend.",
    "The 6 views are: top_ne, top_sw, bottom_ne, bottom_sw, top, and a zoomed x3 version of top_ne. ",
    "OpenSCAD facet controls (the practical FN settings to use) are: ",
    "$fn: fixed number of fragments (explicit polygon sides; great for final quality cylinders/spheres), ",
    "$fa: minimum angle per fragment in degrees (adaptive smoothness based on curvature; good global quality control), ",
    "$fs: minimum fragment size in model units (adaptive smoothness based on physical segment length; good for " +
    "scale-consistent tessellation). ",
    "How they interact: if $fn is set > 0 it overrides $fa/$fs; if $fn is 0 or unset then OpenSCAD derives fragments from " +
    "$fa and $fs together. ",
    "Common usage guidance: quick preview uses lower detail (example $fn=24 or coarse $fa/$fs), final export uses higher " +
    "detail (example $fn=96+, or tighter $fa/$fs such as $fa=4 and $fs=0.5). ",
    "For threaded, press-fit, and screw interfaces, increase tessellation to reduce fit error from faceting. ",
    "IF YOU SEE A MISMATCH YOU MUST CALL THIS TOOL AGAIN AND REVIEW RESULTS AGAIN. YOU MUST PAY ATTENTION TO DETAILS. " +
    "MOST OF THE TIME YOU WILL NOT BE ABLE TO ONE-SHOT THIS, SO PLEASE REFACTOR UNTIL YOU ARE REALLY CONFIDENT. ",
    "When integrating some parts into you design like bits or screws, you must always think about how they will fit or " +
    "integrate with the rest of the design. You should always consider the dimensions, tolerances, and how the parts will " +
    "be assembled together including convergence of holes allowing for screws or press fit, and how the parts will " +
    "interact with each other in the final design. Always keep in mind the practical aspects of manufacturing and " +
    "assembly when designing with OpenSCAD or any CAD software. If we are planning to make a hole, check if it doesn't " +
    "pass through another hole. Mind the fn= parameter."
  );

  /// <summary>JSON schema describing input arguments for the tool.</summary>
  public object InputSchema => new
  {
    type = "object",
    properties = new
    {
      scad_file_path = new
      {
        type = "string",
        description = "Absolute or workspace-relative path to a .scad file on disk. Image size is fixed by server policy.",
      },
    },
    required = (string[])["scad_file_path"],
    additionalProperties = false,
  };

  /// <summary>Executes the render pipeline and returns MCP content.</summary>
  /// <param name="args">JSON arguments containing the SCAD file path.</param>
  /// <returns>Structured MCP response payload.</returns>
  public async Task<object> ExecuteAsync(JsonElement args)
  {
    // Extract and validate the SCAD file path argument.
    var scadPathRaw = args.TryGetProperty("scad_file_path", out var scadPathElement)
      ? scadPathElement.GetString()
      : null;

    if (string.IsNullOrWhiteSpace(scadPathRaw))
    {
      throw new ArgumentException("'scad_file_path' must be a non-empty string");
    }

    var scadPath = ResolveScadPath(scadPathRaw!);
    if (!scadPath.EndsWith(".scad", StringComparison.OrdinalIgnoreCase))
    {
      throw new ArgumentException("'scad_file_path' must point to a .scad file");
    }

    // Verify the target file exists before rendering.
    if (!File.Exists(scadPath))
    {
      throw new FileNotFoundException($"SCAD file not found: {scadPath}");
    }

    // Render the model and gather the resulting payload.
    var scadCode = await File.ReadAllTextAsync(scadPath);
    var payload = RenderFixedShots(scadCode, scadPath);

    var renders = payload.Renders;
    if (renders.Count != 6)
    {
      throw new InvalidOperationException($"Renderer returned {renders.Count} images; expected 6");
    }

    // Build the content array with status and image payloads.
    var content = new List<object>
    {
      new
      {
        type = "text",
        text = $"OpenSCAD render completed. images={renders.Count}, fixed_size={FixedRenderSize}, source={scadPath}, edge_counter={payload.EdgeCounter}",
      },
    };

    // Emit validation warnings when present.
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
        text = $"Image {index}: view={render.ViewLabel}, path={render.ImagePath}, size={render.ImageSize}, mime={render.MimeType}",
      });
      content.Add(new
      {
        type = "image",
        data = imageB64,
        mimeType = render.MimeType,
      });
    }

    // Return content plus structured metadata for downstream use.
    return new
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
  }

  /// <summary>Runs the fixed-shot render pipeline and returns the payload.</summary>
  /// <param name="scadCode">Source SCAD code.</param>
  /// <param name="scadPath">SCAD file path.</param>
  /// <returns>Render payload with images and metadata.</returns>
  private RenderPayload RenderFixedShots(string scadCode, string scadPath)
  {
    // Prepare the render output directory.
    var projectRoot = ResolveProjectRoot();
    var rendersDir = Path.Combine(projectRoot, "renders");
    Directory.CreateDirectory(rendersDir);

    // Export to STL so we can extract mesh stats and validation data before rendering.
    var stlPath = Path.Combine(rendersDir, $"mcp_mesh_{DateTime.UtcNow:yyyyMMdd_HHmmss_fffffff}.stl");
    try
    {
      ExportAsciiStl(scadPath, stlPath);
      var meshStats = ParseMeshStats(stlPath);
      var validation = validationService.Validate(stlPath);
      var center = ComputeCenter(meshStats);
      var distance = ComputeDistance(meshStats);

      // Render each base shot in a temporary wrapper SCAD file.
      var shotOutputs = new Dictionary<string, RenderItem>(StringComparer.Ordinal);
      foreach (var shot in baseShots)
      {
        var wrapperScad = BuildViewScad(stlPath, shot, center, distance);
        var wrapperPath = Path.Combine(rendersDir, $"mcp_view_{shot.Key}_{DateTime.UtcNow:yyyyMMdd_HHmmss_fffffff}.scad");
        File.WriteAllText(wrapperPath, wrapperScad);
        try
        {
          var outputPng = Path.Combine(rendersDir, $"render_{FixedWidth}x{FixedHeight}_{DateTime.UtcNow:yyyyMMdd_HHmmss_fffffff}.png");
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

      // Create a zoomed image variant for the top_ne view.
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

      // Order the renders deterministically for downstream consumers.
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
        ["axes", "scales", "edges", "top_label", "xyz_legend"],
        meshStats,
        meshStats.UniqueEdgeCount,
        "fixed_6_shot_manifest_v2",
        ordered.Select(x => x.ViewLabel).ToArray(),
        new CameraInfo([center.X, center.Y, center.Z], distance, "model_bbox_center"),
        validation
      );
    }
    finally
    {
      TryDelete(stlPath);
    }
  }

  /// <summary>Resolves the project root for render output storage.</summary>
  /// <returns>Absolute path to the project root.</returns>
  private string ResolveProjectRoot()
  {
    var cwd = Directory.GetCurrentDirectory();
    if (Path.GetFileName(cwd).Equals("c_server", StringComparison.OrdinalIgnoreCase))
    {
      return Directory.GetParent(cwd)?.FullName ?? cwd;
    }

    return cwd;
  }

  /// <summary>Normalizes a SCAD file path to an absolute path.</summary>
  /// <param name="rawPath">User-provided path.</param>
  /// <returns>Absolute SCAD file path.</returns>
  private string ResolveScadPath(string rawPath)
  {
    if (Path.IsPathRooted(rawPath))
    {
      return Path.GetFullPath(rawPath);
    }

    return Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), rawPath));
  }

  /// <summary>Exports an ASCII STL file from OpenSCAD.</summary>
  /// <param name="scadPath">SCAD file path.</param>
  /// <param name="stlPath">Output STL path.</param>
  private void ExportAsciiStl(string scadPath, string stlPath)
  {
    RunOpenScad(
    [
      "-o", stlPath,
      "--export-format", "asciistl",
      scadPath,
      "--render",
    ]);
  }

  /// <summary>Renders a PNG image from OpenSCAD.</summary>
  /// <param name="scadPath">SCAD file path.</param>
  /// <param name="outputPng">Output PNG path.</param>
  /// <param name="width">Image width in pixels.</param>
  /// <param name="height">Image height in pixels.</param>
  private void RenderPng(string scadPath, string outputPng, int width, int height)
  {
    RunOpenScad(
    [
      "-o", outputPng,
      scadPath,
      "--render",
      $"--imgsize={width},{height}",
      "--projection=o",
      "--view=axes,scales,edges",
    ]);
  }

  /// <summary>Invokes the OpenSCAD CLI with the provided arguments.</summary>
  /// <param name="args">Argument list for the OpenSCAD process.</param>
  private void RunOpenScad(IEnumerable<string> args)
  {
    // Configure the OpenSCAD CLI invocation with redirected output for error handling.
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

    // Execute and capture stdout/stderr for diagnostics.
    using var process = Process.Start(psi);
    if (process is null)
    {
      throw new InvalidOperationException("Failed to start openscad process");
    }

    var stdout = process.StandardOutput.ReadToEnd();
    var stderr = process.StandardError.ReadToEnd();
    process.WaitForExit();

    // Fail fast with the most relevant output if the process reports errors.
    if (process.ExitCode != 0)
    {
      var detail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
      throw new InvalidOperationException(string.IsNullOrWhiteSpace(detail) ? "Unknown OpenSCAD error" : detail.Trim());
    }
  }

  /// <summary>Builds a temporary SCAD wrapper that sets view parameters and imports an STL.</summary>
  /// <param name="stlPath">STL file path.</param>
  /// <param name="shot">Shot metadata to apply.</param>
  /// <param name="center">Model center in XYZ.</param>
  /// <param name="vpd">View distance.</param>
  /// <returns>SCAD source string.</returns>
  private string BuildViewScad(string stlPath, ShotSpec shot, Vec3 center, double vpd)
  {
    var escapedPath = stlPath.Replace("\\", "/").Replace("\"", "\\\"");
    var sb = new StringBuilder();
    // Build a minimal SCAD snippet that sets the view and imports the STL.
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

  /// <summary>Parses STL geometry to compute mesh statistics.</summary>
  /// <param name="stlPath">STL file path.</param>
  /// <returns>Computed mesh statistics.</returns>
  private MeshStats ParseMeshStats(string stlPath)
  {
    var vertices = new List<Vec3>();
    var triangles = new List<(Vec3 A, Vec3 B, Vec3 C)>();
    var current = new List<Vec3>(3);

    // Parse STL vertex lines, collecting triangles as we go.
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
      return new MeshStats(0, 0, 0, [0d, 0d, 0d], [0d, 0d, 0d], [0d, 0d, 0d]);
    }

    // Compute unique vertex/edge counts for mesh metrics.
    var uniqueVertices = new HashSet<string>(vertices.Select(VertexKey));
    var uniqueEdges = new HashSet<string>(StringComparer.Ordinal);
    foreach (var (a, b, c) in triangles)
    {
      AddEdge(a, b, uniqueEdges);
      AddEdge(b, c, uniqueEdges);
      AddEdge(c, a, uniqueEdges);
    }

    // Derive bounding box extents from the vertex list.
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
      [minX, minY, minZ],
      [maxX, maxY, maxZ],
      [
        Math.Round(maxX - minX, 6),
        Math.Round(maxY - minY, 6),
        Math.Round(maxZ - minZ, 6),
      ]
    );
  }

  /// <summary>Adds an undirected edge between two vertices to the set.</summary>
  /// <param name="p">First vertex.</param>
  /// <param name="q">Second vertex.</param>
  /// <param name="edges">Edge set to update.</param>
  private void AddEdge(Vec3 p, Vec3 q, HashSet<string> edges)
  {
    var a = VertexKey(p);
    var b = VertexKey(q);
    var key = string.CompareOrdinal(a, b) <= 0 ? $"{a}|{b}" : $"{b}|{a}";
    edges.Add(key);
  }

  /// <summary>Creates a stable string key for a vertex coordinate.</summary>
  /// <param name="v">Vertex to serialize.</param>
  /// <returns>Stable vertex key.</returns>
  private string VertexKey(Vec3 v) =>
    FormattableString.Invariant($"{v.X:0.######},{v.Y:0.######},{v.Z:0.######}");

  /// <summary>Computes the center of the bounding box.</summary>
  /// <param name="stats">Mesh statistics with bounding box data.</param>
  /// <returns>Center point.</returns>
  private Vec3 ComputeCenter(MeshStats stats)
  {
    return new Vec3(
      Math.Round((stats.BboxMin[0] + stats.BboxMax[0]) / 2.0, 6),
      Math.Round((stats.BboxMin[1] + stats.BboxMax[1]) / 2.0, 6),
      Math.Round((stats.BboxMin[2] + stats.BboxMax[2]) / 2.0, 6)
    );
  }

  /// <summary>Computes a camera distance based on mesh extents.</summary>
  /// <param name="stats">Mesh statistics with bounding box size.</param>
  /// <returns>Distance value for the camera.</returns>
  private double ComputeDistance(MeshStats stats)
  {
    var sx = stats.BboxSize[0];
    var sy = stats.BboxSize[1];
    var sz = stats.BboxSize[2];
    var diag = Math.Sqrt((sx * sx) + (sy * sy) + (sz * sz));
    return Math.Round(Math.Max(40.0, diag * 2.4), 4);
  }

  /// <summary>Overlays the view label and XYZ legend on the rendered image.</summary>
  /// <param name="imagePath">Path to the image to update.</param>
  /// <param name="labelText">Label to render at the top.</param>
  private void BakeTopLabelAndLegend(string imagePath, string labelText)
  {
    using var image = Image.Load<Rgba32>(imagePath);
    var width = image.Width;
    var height = image.Height;

    // Draw the top label banner.
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

    // Draw the XYZ legend box and axis indicators.
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

    (string Label, Color Color)[] legend =
    [
      ("X", Color.FromRgb(235, 64, 52)),
      ("Y", Color.FromRgb(60, 179, 113)),
      ("Z", Color.FromRgb(65, 105, 225)),
    ];

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

  /// <summary>Resolves a system font for overlay text.</summary>
  /// <param name="size">Font size in points.</param>
  /// <returns>Resolved font or null if unavailable.</returns>
  private Font? ResolveFont(float size)
  {
    var candidates = (string[])
    [
      "DejaVu Sans",
      "Liberation Sans",
      "Arial",
    ];
    foreach (var family in candidates)
    {
      if (SystemFonts.TryGet(family, out var found))
      {
        return found.CreateFont(size, FontStyle.Bold);
      }
    }

    return null;
  }

  /// <summary>Creates a zoomed-in image and applies overlays.</summary>
  /// <param name="sourcePath">Source image path.</param>
  /// <param name="outputPath">Output image path.</param>
  /// <param name="zoomFactor">Zoom factor to apply.</param>
  /// <param name="labelText">Label to render on the zoomed image.</param>
  private void CreateZoomedImage(string sourcePath, string outputPath, int zoomFactor, string labelText)
  {
    using var source = Image.Load<Rgba32>(sourcePath);
    var width = source.Width;
    var height = source.Height;

    // Crop the center region, then scale back up to the original size.
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
    // Apply the same labeling and legend overlays as the base renders.
    BakeTopLabelAndLegend(outputPath, labelText);
  }

  /// <summary>Attempts to delete a file and suppresses errors.</summary>
  /// <param name="path">File path to delete.</param>
  private void TryDelete(string path)
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
}
