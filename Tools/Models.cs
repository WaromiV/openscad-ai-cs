using System.Text.Json.Serialization;
using c_server.Validation.Models;

namespace c_server.Tools;

public record ShotSpec(string Key, string Label, int Rx, int Ry, int Rz);

public record RenderItem(
  string ImagePath,
  string ImageSize,
  string MimeType,
  string ViewKey,
  string ViewLabel,
  int ZoomFactor
);

public record CameraInfo(
  [property: JsonPropertyName("vpt_center")] double[] VptCenter,
  [property: JsonPropertyName("vpd")] double Vpd,
  [property: JsonPropertyName("mode")] string Mode
);

public record MeshStats(
  [property: JsonPropertyName("vertex_count")] int VertexCount,
  [property: JsonPropertyName("triangle_count")] int TriangleCount,
  [property: JsonPropertyName("unique_edge_count")] int UniqueEdgeCount,
  [property: JsonPropertyName("bbox_min")] double[] BboxMin,
  [property: JsonPropertyName("bbox_max")] double[] BboxMax,
  [property: JsonPropertyName("bbox_size")] double[] BboxSize
);

public record RenderPayload(
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

public record Vec3(double X, double Y, double Z);

public record ComparisonResult(
  double Ssim,
  double Mse,
  double HistogramCorrelation,
  double EdgeAlignment,
  string Summary,
  string Interpretation
);
