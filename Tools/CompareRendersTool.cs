using System.Globalization;
using System.Text.Json;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace c_server.Tools;

/// <summary>Compares two images and reports similarity metrics.</summary>
public sealed class CompareRendersTool : IMcpTool
{
  /// <summary>Tool name exposed to MCP clients.</summary>
  public string Name => "compare_renders";

  /// <summary>Human-readable description for MCP tool discovery.</summary>
  public string Description => string.Concat(
    "Compare two images and return quantitative similarity metrics. ",
    "CRITICAL: Use this tool AFTER EVERY render to objectively measure how well your OpenSCAD model matches the reference image. ",
    "Returns SSIM (structural similarity, 0.0-1.0 where 1.0=identical), MSE (mean squared error, lower=better), ",
    "histogram correlation (color distribution match, -1.0 to 1.0 where 1.0=perfect), and edge alignment score. ",
    "WHEN TO USE: (1) After generating OpenSCAD code, render it and compare to reference image. ",
    "(2) After each iteration/refinement to measure if changes improved accuracy. ",
    "(3) To identify which view angles show the biggest mismatches. ",
    "INTERPRETATION: SSIM > 0.95 = excellent match, 0.85-0.95 = good match, 0.70-0.85 = moderate match, < 0.70 = poor match requiring redesign. ",
    "MSE < 100 = excellent, 100-500 = acceptable, > 500 = significant differences. ",
    "Histogram correlation > 0.90 = colors/tones match well, < 0.70 = color distribution differs substantially. ",
    "WORKFLOW: Always compare the same view angles (e.g., compare reference top_ne view with rendered top_ne view). ",
    "Use structured metrics to guide iterative improvements - don't rely on visual inspection alone."
  );

  /// <summary>JSON schema describing input arguments for the tool.</summary>
  public object InputSchema => new
  {
    type = "object",
    properties = new
    {
      reference_image_path = new
      {
        type = "string",
        description = "Absolute or relative path to the reference/target image (e.g., original product photo).",
      },
      rendered_image_path = new
      {
        type = "string",
        description = "Absolute or relative path to the rendered image from OpenSCAD to compare against reference.",
      },
    },
    required = ["reference_image_path", "rendered_image_path"],
    additionalProperties = false,
  };

  /// <summary>Executes the comparison workflow.</summary>
  /// <param name="args">JSON arguments containing image paths.</param>
  /// <returns>Structured MCP response payload.</returns>
  public async Task<object> ExecuteAsync(JsonElement args)
  {
    // Extract and validate input paths.
    var refPathRaw = args.TryGetProperty("reference_image_path", out var refPathElement)
      ? refPathElement.GetString()
      : null;
    var renderedPathRaw = args.TryGetProperty("rendered_image_path", out var renderedPathElement)
      ? renderedPathElement.GetString()
      : null;

    if (string.IsNullOrWhiteSpace(refPathRaw))
    {
      throw new ArgumentException("'reference_image_path' must be a non-empty string");
    }

    if (string.IsNullOrWhiteSpace(renderedPathRaw))
    {
      throw new ArgumentException("'rendered_image_path' must be a non-empty string");
    }

    var refPath = ResolveImagePath(refPathRaw!);
    var renderedPath = ResolveImagePath(renderedPathRaw!);

    // Confirm both images exist before comparison.
    if (!File.Exists(refPath))
    {
      throw new FileNotFoundException($"Reference image not found: {refPath}");
    }

    if (!File.Exists(renderedPath))
    {
      throw new FileNotFoundException($"Rendered image not found: {renderedPath}");
    }

    // Execute the comparison in a background task.
    var comparisonResult = await Task.Run(() => CompareImages(refPath, renderedPath));

    // Return a summary plus structured metrics.
    return new
    {
      content =
      [
        new
        {
          type = "text",
          text = comparisonResult.Summary,
        },
      ],
      structuredContent = new
      {
        ssim = comparisonResult.Ssim,
        mse = comparisonResult.Mse,
        histogram_correlation = comparisonResult.HistogramCorrelation,
        edge_alignment = comparisonResult.EdgeAlignment,
        reference_path = refPath,
        rendered_path = renderedPath,
        interpretation = comparisonResult.Interpretation,
      },
      isError = false,
    };
  }

  /// <summary>Resolves an image path to an absolute path.</summary>
  /// <param name="rawPath">User-provided path.</param>
  /// <returns>Absolute image path.</returns>
  private string ResolveImagePath(string rawPath)
  {
    if (Path.IsPathRooted(rawPath))
    {
      return Path.GetFullPath(rawPath);
    }

    return Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), rawPath));
  }

  /// <summary>Compares two images and returns computed metrics.</summary>
  /// <param name="refPath">Reference image path.</param>
  /// <param name="renderedPath">Rendered image path.</param>
  /// <returns>Comparison metrics and summary.</returns>
  private ComparisonResult CompareImages(string refPath, string renderedPath)
  {
    using var refImage = Image.Load<Rgba32>(refPath);
    using var renderedImage = Image.Load<Rgba32>(renderedPath);

    // Normalize size to ensure metrics compare like-for-like pixels.
    if (refImage.Width != renderedImage.Width || refImage.Height != renderedImage.Height)
    {
      renderedImage.Mutate(ctx => ctx.Resize(refImage.Width, refImage.Height, KnownResamplers.Lanczos3));
    }

    // Compute similarity metrics across multiple dimensions.
    var ssim = ComputeSSIM(refImage, renderedImage);
    var mse = ComputeMSE(refImage, renderedImage);
    var histCorr = ComputeHistogramCorrelation(refImage, renderedImage);
    var edgeAlign = ComputeEdgeAlignment(refImage, renderedImage);

    // Build the interpretation and summary.
    var interpretation = InterpretMetrics(ssim, mse, histCorr, edgeAlign);
    var summary = string.Format(CultureInfo.InvariantCulture,
      "Comparison complete: SSIM={0:F4} ({1}), MSE={2:F2} ({3}), Histogram={4:F4} ({5}), Edge={6:F4} ({7}). {8}",
      ssim, InterpretSSIM(ssim),
      mse, InterpretMSE(mse),
      histCorr, InterpretHistogram(histCorr),
      edgeAlign, InterpretEdgeAlignment(edgeAlign),
      interpretation
    );

    return new ComparisonResult(ssim, mse, histCorr, edgeAlign, summary, interpretation);
  }

  /// <summary>Computes the structural similarity index (SSIM).</summary>
  /// <param name="img1">First image.</param>
  /// <param name="img2">Second image.</param>
  /// <returns>SSIM score.</returns>
  private double ComputeSSIM(Image<Rgba32> img1, Image<Rgba32> img2)
  {
    const double c1 = 6.5025;
    const double c2 = 58.5225;
    var width = img1.Width;
    var height = img1.Height;

    double meanX = 0, meanY = 0;
    double varX = 0, varY = 0, covXY = 0;
    var n = width * height;

    // First pass: compute mean luminance values.
    for (var y = 0; y < height; y++)
    {
      for (var x = 0; x < width; x++)
      {
        var px1 = img1[x, y];
        var px2 = img2[x, y];
        var gray1 = (0.299 * px1.R) + (0.587 * px1.G) + (0.114 * px1.B);
        var gray2 = (0.299 * px2.R) + (0.587 * px2.G) + (0.114 * px2.B);
        meanX += gray1;
        meanY += gray2;
      }
    }

    meanX /= n;
    meanY /= n;

    // Second pass: compute variance and covariance.
    for (var y = 0; y < height; y++)
    {
      for (var x = 0; x < width; x++)
      {
        var px1 = img1[x, y];
        var px2 = img2[x, y];
        var gray1 = (0.299 * px1.R) + (0.587 * px1.G) + (0.114 * px1.B);
        var gray2 = (0.299 * px2.R) + (0.587 * px2.G) + (0.114 * px2.B);
        var dx = gray1 - meanX;
        var dy = gray2 - meanY;
        varX += dx * dx;
        varY += dy * dy;
        covXY += dx * dy;
      }
    }

    varX /= n;
    varY /= n;
    covXY /= n;

    // Apply the SSIM formula with stability constants.
    var numerator = ((2 * meanX * meanY) + c1) * ((2 * covXY) + c2);
    var denominator = ((meanX * meanX) + (meanY * meanY) + c1) * (varX + varY + c2);

    return Math.Clamp(numerator / denominator, 0.0, 1.0);
  }

  /// <summary>Computes mean squared error across RGB channels.</summary>
  /// <param name="img1">First image.</param>
  /// <param name="img2">Second image.</param>
  /// <returns>MSE score.</returns>
  private double ComputeMSE(Image<Rgba32> img1, Image<Rgba32> img2)
  {
    var width = img1.Width;
    var height = img1.Height;
    var sumSquaredError = 0.0;

    // Sum squared channel differences for each pixel.
    for (var y = 0; y < height; y++)
    {
      for (var x = 0; x < width; x++)
      {
        var px1 = img1[x, y];
        var px2 = img2[x, y];
        var dr = px1.R - px2.R;
        var dg = px1.G - px2.G;
        var db = px1.B - px2.B;
        sumSquaredError += (dr * dr) + (dg * dg) + (db * db);
      }
    }

    return sumSquaredError / (width * height * 3.0);
  }

  /// <summary>Computes grayscale histogram correlation.</summary>
  /// <param name="img1">First image.</param>
  /// <param name="img2">Second image.</param>
  /// <returns>Correlation coefficient.</returns>
  private double ComputeHistogramCorrelation(Image<Rgba32> img1, Image<Rgba32> img2)
  {
    var hist1 = new int[256];
    var hist2 = new int[256];

    // Build grayscale histograms for each image.
    for (var y = 0; y < img1.Height; y++)
    {
      for (var x = 0; x < img1.Width; x++)
      {
        var px1 = img1[x, y];
        var px2 = img2[x, y];
        var gray1 = (int)((0.299 * px1.R) + (0.587 * px1.G) + (0.114 * px1.B));
        var gray2 = (int)((0.299 * px2.R) + (0.587 * px2.G) + (0.114 * px2.B));
        hist1[Math.Clamp(gray1, 0, 255)]++;
        hist2[Math.Clamp(gray2, 0, 255)]++;
      }
    }

    // Compute correlation between histogram vectors.
    var mean1 = hist1.Average();
    var mean2 = hist2.Average();
    var numerator = 0.0;
    var sum1 = 0.0;
    var sum2 = 0.0;

    for (var i = 0; i < 256; i++)
    {
      var d1 = hist1[i] - mean1;
      var d2 = hist2[i] - mean2;
      numerator += d1 * d2;
      sum1 += d1 * d1;
      sum2 += d2 * d2;
    }

    var denominator = Math.Sqrt(sum1 * sum2);
    return denominator > 0 ? Math.Clamp(numerator / denominator, -1.0, 1.0) : 0.0;
  }

  /// <summary>Computes edge alignment score using simple gradient magnitude.</summary>
  /// <param name="img1">First image.</param>
  /// <param name="img2">Second image.</param>
  /// <returns>Edge alignment score.</returns>
  private double ComputeEdgeAlignment(Image<Rgba32> img1, Image<Rgba32> img2)
  {
    var edges1 = DetectEdges(img1);
    var edges2 = DetectEdges(img2);
    var matches = 0;
    var total = 0;

    // Compare edge masks to compute overlap ratio.
    for (var y = 0; y < img1.Height; y++)
    {
      for (var x = 0; x < img1.Width; x++)
      {
        if (edges1[x, y] || edges2[x, y])
        {
          total++;
          if (edges1[x, y] && edges2[x, y])
          {
            matches++;
          }
        }
      }
    }

    return total > 0 ? (double)matches / total : 0.0;
  }

  /// <summary>Detects edges using a simple gradient magnitude threshold.</summary>
  /// <param name="img">Image to process.</param>
  /// <returns>Edge mask.</returns>
  private bool[,] DetectEdges(Image<Rgba32> img)
  {
    var width = img.Width;
    var height = img.Height;
    var edges = new bool[width, height];
    const int threshold = 30;

    // Apply a small Sobel-like kernel to estimate gradients.
    for (var y = 1; y < height - 1; y++)
    {
      for (var x = 1; x < width - 1; x++)
      {
        var center = img[x, y];
        var centerGray = (0.299 * center.R) + (0.587 * center.G) + (0.114 * center.B);

        var gx = 0.0;
        var gy = 0.0;

        for (var dy = -1; dy <= 1; dy++)
        {
          for (var dx = -1; dx <= 1; dx++)
          {
            var neighbor = img[x + dx, y + dy];
            var gray = (0.299 * neighbor.R) + (0.587 * neighbor.G) + (0.114 * neighbor.B);
            gx += dx * gray;
            gy += dy * gray;
          }
        }

        var magnitude = Math.Sqrt((gx * gx) + (gy * gy));
        edges[x, y] = magnitude > threshold;
      }
    }

    return edges;
  }

  /// <summary>Interprets SSIM into a qualitative label.</summary>
  /// <param name="ssim">SSIM score.</param>
  /// <returns>Interpretation label.</returns>
  private string InterpretSSIM(double ssim)
  {
    if (ssim >= 0.95) return "excellent";
    if (ssim >= 0.85) return "good";
    if (ssim >= 0.70) return "moderate";
    return "poor";
  }

  /// <summary>Interprets MSE into a qualitative label.</summary>
  /// <param name="mse">MSE score.</param>
  /// <returns>Interpretation label.</returns>
  private string InterpretMSE(double mse)
  {
    if (mse < 100) return "excellent";
    if (mse < 500) return "acceptable";
    return "high difference";
  }

  /// <summary>Interprets histogram correlation into a qualitative label.</summary>
  /// <param name="corr">Correlation coefficient.</param>
  /// <returns>Interpretation label.</returns>
  private string InterpretHistogram(double corr)
  {
    if (corr >= 0.90) return "strong match";
    if (corr >= 0.70) return "moderate match";
    return "weak match";
  }

  /// <summary>Interprets edge alignment into a qualitative label.</summary>
  /// <param name="align">Edge alignment score.</param>
  /// <returns>Interpretation label.</returns>
  private string InterpretEdgeAlignment(double align)
  {
    if (align >= 0.80) return "well aligned";
    if (align >= 0.60) return "partially aligned";
    return "misaligned";
  }

  /// <summary>Combines metric thresholds into an overall interpretation.</summary>
  /// <param name="ssim">SSIM score.</param>
  /// <param name="mse">MSE score.</param>
  /// <param name="histCorr">Histogram correlation.</param>
  /// <param name="edgeAlign">Edge alignment score.</param>
  /// <returns>Overall interpretation string.</returns>
  private string InterpretMetrics(double ssim, double mse, double histCorr, double edgeAlign)
  {
    // Apply tiered thresholds to produce a consolidated interpretation.
    if (ssim >= 0.95 && mse < 100 && histCorr >= 0.90)
    {
      return "Overall: Excellent match. Model is very close to reference.";
    }

    if (ssim >= 0.85 && mse < 500 && histCorr >= 0.70)
    {
      return "Overall: Good match. Minor refinements may improve accuracy.";
    }

    if (ssim >= 0.70)
    {
      return "Overall: Moderate match. Significant differences remain; review dimensions and geometry.";
    }

    return "Overall: Poor match. Major redesign needed; check basic shape, proportions, and features.";
  }
}
