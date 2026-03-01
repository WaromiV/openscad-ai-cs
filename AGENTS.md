# Repository Guidelines

## Project Structure & Module Organization
- `Program.cs` hosts the minimal ASP.NET Core HTTP MCP server with tool routing.
- `Tools/` contains MCP tool implementations:
  - `RenderOpenScadTool.cs` - OpenSCAD rendering implementation
  - `CompareRendersTool.cs` - Image comparison implementation
  - `Models.cs` - Shared data models (ShotSpec, MeshStats, etc.)
- `Validation/` contains C# validation services plus the optional CGAL worker integration.
- `Validation/cgal_worker/` is a C++ CGAL tool with its own `CMakeLists.txt` and build instructions.
- `Properties/launchSettings.json` defines local run profiles.
- `appsettings.json` and `appsettings.Development.json` carry runtime configuration.

## Build, Test, and Development Commands
- `dotnet build` builds the server for the current configuration.
- `dotnet run --urls http://127.0.0.1:8770` runs the HTTP MCP server locally.
- `CGAL_WORKER_PATH=/abs/path/to/cgal_worker dotnet run --urls http://127.0.0.1:8770` enables the CGAL validator.
- CGAL worker build (Linux example):
  - `cmake -S Validation/cgal_worker -B Validation/cgal_worker/build`
  - `cmake --build Validation/cgal_worker/build -j`
  - `mkdir -p Validation/cgal_worker/bin && cp Validation/cgal_worker/build/cgal_worker Validation/cgal_worker/bin/cgal_worker`

## Coding Style & Naming Conventions
- C# uses nullable reference types and implicit usings (see `c_server.csproj`).
- Follow existing formatting: 2-space indentation, `var` for locals when the type is obvious, and expression-bodied helpers when concise.
- Always use 2-space indentation in edits.
- Prefer collection expressions (`[a, b, c]`) over `new[] { a, b, c }`.
- Document every class and every class member with XML doc comments, and add documentation for any undocumented members you touch.
- For methods longer than 10 lines, explain internal operations with `//` comments; update existing methods that violate this.
- File and type naming follows standard C# conventions (PascalCase for types, camelCase for locals/parameters).
- **Field Naming Convention**: All fields (private, protected, public, static, readonly, const) must use camelCase starting with a lowercase letter. Never use underscore prefix (`_field`) or PascalCase for fields.
  - ✅ Good: `private readonly string contentRoot;`
  - ✅ Good: `private static McpServerTool? tool;`
  - ✅ Good: `private const int MaxRetries = 3;` (exception: const can use PascalCase for constants that act like static readonly configuration)
  - ❌ Bad: `private readonly string _contentRoot;`
  - ❌ Bad: `private readonly string ContentRoot;`
  - Use `this.` qualifier when field name conflicts with parameter name in constructors.

## Testing Guidelines
- There are no automated tests in this repository yet.
- If you add tests, keep them close to the feature area and document how to run them (e.g., `dotnet test`) in this file.
- After each batch of changes, verify the codebase is valid (at minimum `dotnet build`) before continuing.
- After confirming builds (and tests, if any, pass), commit the changes with an appropriate message.

## Adding New MCP Tools
- Create a new class in `Tools/` and mark it with `[McpServerToolType]`
- Add one or more `[McpServerTool]` methods with `[Description]` metadata
- Register shared dependencies with DI in `Program.cs` (tools are discovered via `WithToolsFromAssembly`)
- Add shared data models to `Tools/Models.cs` if needed
- Tools are automatically discovered from the assembly and exposed via MCP

## Commit & Pull Request Guidelines
- Commit messages are short, imperative, and sentence case (e.g., "Add CGAL worker validation integration scaffolding").
- PRs should include:
  - A brief summary of behavior changes.
  - How you tested (commands and results), or why testing is not applicable.
  - Notes about config changes or required dependencies (e.g., CGAL/OpenSCAD).

## Configuration & Runtime Notes
- OpenSCAD CLI must be available on `PATH` as `openscad`.
- Rendered artifacts are written to a local `renders/` directory created at runtime; do not commit outputs.
- CGAL worker is optional; set `CGAL_WORKER_PATH` to override the default location.
