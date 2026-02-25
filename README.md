# OpenSCAD AI C# MCP Server

HTTP MCP server in C# that renders OpenSCAD models into deterministic multi-view PNG images for LLM tool consumption.

## Features

- MCP over HTTP (`POST /mcp`) with JSON-RPC 2.0 methods: `initialize`, `ping`, `tools/list`, `tools/call`
- Health/root endpoint (`GET /`) returning `server up`
- Tool: `render_openscad`
  - Input: `scad_file_path` (path to `.scad` file)
  - Fixed render size: `1600x1200` (not user-configurable)
  - Output: 6 deterministic views with MCP `image` content blocks (base64 PNG)
  - Views: top_ne, zoomed top_ne x3, top_sw, bottom_ne, bottom_sw, top
- Tool: `compare_renders`
  - Input: `reference_image_path` (target/original image), `rendered_image_path` (OpenSCAD render)
  - Output: Quantitative similarity metrics with interpretation
  - Metrics: SSIM (structural similarity), MSE (mean squared error), histogram correlation, edge alignment
  - Auto-resizes images to match dimensions for accurate comparison
- F6-style render pipeline (mesh export + render), not OpenSCAD preview mode
- Camera centered to model bbox center (`$vpt`) with deterministic distance (`$vpd`)
- On-image overlays: top label + XYZ legend (X red, Y green, Z blue)
- Optional CGAL validation worker integration for disconnected parts and self-intersections

## Requirements

- .NET SDK 10+
- OpenSCAD CLI available in `PATH` as `openscad`

## Run

```bash
dotnet run --urls http://127.0.0.1:8770
```

To run with CGAL worker:

```bash
CGAL_WORKER_PATH=/absolute/path/to/cgal_worker dotnet run --urls http://127.0.0.1:8770
```

Default worker path (if env var not set):

- `Validation/cgal_worker/bin/cgal_worker`

Then:

- `GET http://127.0.0.1:8770/` -> `server up`
- `POST http://127.0.0.1:8770/mcp` for MCP JSON-RPC calls

## MCP tool examples

### Render OpenSCAD model

```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "tools/call",
  "params": {
    "name": "render_openscad",
    "arguments": {
      "scad_file_path": "/absolute/path/to/model.scad"
    }
  }
}
```

### Compare rendered image to reference

```json
{
  "jsonrpc": "2.0",
  "id": 2,
  "method": "tools/call",
  "params": {
    "name": "compare_renders",
    "arguments": {
      "reference_image_path": "/path/to/original_photo.png",
      "rendered_image_path": "/path/to/render_top_ne.png"
    }
  }
}
```

Returns:
- `ssim`: 0.0-1.0 (1.0=identical structure)
- `mse`: Lower is better (< 100 excellent, 100-500 acceptable, > 500 significant difference)
- `histogram_correlation`: -1.0 to 1.0 (> 0.90 colors match well)
- `edge_alignment`: 0.0-1.0 (> 0.80 edges well aligned)
