# CGAL Worker (C++)

This worker performs mesh validation with CGAL and is called by the C# MCP server.

## Checks

- Connected components (`DETACHED_PARTS` warning when > 1)
- Self-intersections (`SELF_INTERSECTIONS` warning when any found)

## Build

Prerequisites:

- CGAL development libraries
- CMake
- C++17 compiler

Example (Linux):

```bash
cmake -S . -B build
cmake --build build -j
mkdir -p ../cgal_worker/bin
cp build/cgal_worker ../cgal_worker/bin/cgal_worker
```

Expected runtime location for the C# server:

- `c_server/Validation/cgal_worker/bin/cgal_worker`

Or set:

- `CGAL_WORKER_PATH=/absolute/path/to/cgal_worker`

## CLI contract

Input:

```bash
cgal_worker /absolute/path/to/model.stl
```

Output JSON to stdout:

```json
{
  "ok": false,
  "engine": "cgal",
  "warnings": [
    {
      "code": "DETACHED_PARTS",
      "severity": "high",
      "message": "Mesh has disconnected components (possible detached chair legs).",
      "suggested_fix": "Ensure all load-bearing parts intersect and are united with boolean union."
    }
  ],
  "metrics": {
    "connected_components": 3,
    "self_intersections": 0
  }
}
```
