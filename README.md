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

## MCP tool example

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
