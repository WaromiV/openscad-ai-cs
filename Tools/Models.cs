using System.Text.Json.Serialization;
using c_server.Validation.Models;

namespace c_server.Tools;

/// <summary>Describes a deterministic render shot configuration.</summary>
/// <param name="Key">Stable identifier for the shot.</param>
/// <param name="Label">Human-readable label used in overlays.</param>
/// <param name="Rx">Rotation around the X axis in degrees.</param>
/// <param name="Ry">Rotation around the Y axis in degrees.</param>
/// <param name="Rz">Rotation around the Z axis in degrees.</param>
public record ShotSpec(string Key, string Label, int Rx, int Ry, int Rz);

/// <summary>Represents a rendered image artifact and its metadata.</summary>
/// <param name="ImagePath">Absolute path to the rendered image.</param>
/// <param name="ImageSize">Human-readable image dimensions.</param>
/// <param name="MimeType">MIME type for the image payload.</param>
/// <param name="ViewKey">Stable view key for downstream consumers.</param>
/// <param name="ViewLabel">Human-readable view label.</param>
/// <param name="ZoomFactor">Zoom factor applied to the render.</param>
public record RenderItem(
  string ImagePath,
  string ImageSize,
  string MimeType,
  string ViewKey,
  string ViewLabel,
  int ZoomFactor
);

/// <summary>Camera metadata for the rendered shot set.</summary>
/// <param name="VptCenter">Computed center of the model in view space.</param>
/// <param name="Vpd">Camera distance from the target.</param>
/// <param name="Mode">Camera mode identifier.</param>
public record CameraInfo(
  [property: JsonPropertyName("vpt_center")] double[] VptCenter,
  [property: JsonPropertyName("vpd")] double Vpd,
  [property: JsonPropertyName("mode")] string Mode
);

/// <summary>Mesh statistics derived from the exported STL.</summary>
/// <param name="VertexCount">Count of unique vertices.</param>
/// <param name="TriangleCount">Count of mesh triangles.</param>
/// <param name="UniqueEdgeCount">Count of unique edges.</param>
/// <param name="BboxMin">Minimum XYZ bounds.</param>
/// <param name="BboxMax">Maximum XYZ bounds.</param>
/// <param name="BboxSize">XYZ extents of the bounding box.</param>
public record MeshStats(
  [property: JsonPropertyName("vertex_count")] int VertexCount,
  [property: JsonPropertyName("triangle_count")] int TriangleCount,
  [property: JsonPropertyName("unique_edge_count")] int UniqueEdgeCount,
  [property: JsonPropertyName("bbox_min")] double[] BboxMin,
  [property: JsonPropertyName("bbox_max")] double[] BboxMax,
  [property: JsonPropertyName("bbox_size")] double[] BboxSize
);

/// <summary>Aggregated render payload returned by the MCP tool.</summary>
/// <param name="Renders">Ordered render list.</param>
/// <param name="AnnotationMode">Overlay annotation mode description.</param>
/// <param name="Overlays">Enabled overlay types.</param>
/// <param name="MeshStats">Computed mesh statistics.</param>
/// <param name="EdgeCounter">Count of unique edges in the mesh.</param>
/// <param name="ShotPolicy">Identifier for the shot policy.</param>
/// <param name="ShotManifest">Ordered view labels for the shot set.</param>
/// <param name="Camera">Camera metadata for the renders.</param>
/// <param name="Validation">Validation report returned by the CGAL worker.</param>
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

/// <summary>Simple 3D vector value object.</summary>
/// <param name="X">X coordinate.</param>
/// <param name="Y">Y coordinate.</param>
/// <param name="Z">Z coordinate.</param>
public record Vec3(double X, double Y, double Z);

/// <summary>Quantitative comparison metrics for two images.</summary>
/// <param name="Ssim">Structural similarity index.</param>
/// <param name="Mse">Mean squared error.</param>
/// <param name="HistogramCorrelation">Histogram correlation coefficient.</param>
/// <param name="EdgeAlignment">Edge alignment score.</param>
/// <param name="Summary">Human-readable summary line.</param>
/// <param name="Interpretation">Qualitative interpretation of the metrics.</param>
public record ComparisonResult(
  double Ssim,
  double Mse,
  double HistogramCorrelation,
  double EdgeAlignment,
  string Summary,
  string Interpretation
);
