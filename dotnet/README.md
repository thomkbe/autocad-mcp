# AutoCAD MCP (.NET prototype)

A C# alternative to the Python/LISP server in this repo, for **full AutoCAD 2025 and 2026**
(the releases that run on .NET 8). Instead of typing LISP into the command line and
exchanging JSON files, a plugin inside AutoCAD uses the AutoCAD .NET API directly.

```
Claude Code ──stdio──► AutoCadMcp.Server.exe ──named pipe──► AutoCadMcp.Plugin.dll (inside acad.exe)
                       (MCP C# SDK)              "autocad-mcp"   (AutoCAD .NET API, main thread)
```

- **AutoCadMcp.Plugin** is loaded with `NETLOAD`. It serves a named pipe that only your Windows
  user can open and runs each request on AutoCAD's main thread, inside a document lock and a
  transaction. It depends on nothing beyond .NET 8 and AutoCAD, so it can't conflict with
  assemblies AutoCAD loads itself.
- **AutoCadMcp.Server** is the MCP server Claude Code starts. It forwards each tool call to the
  plugin and works even when AutoCAD isn't running (`status` then reports `connected: false`).

AutoCAD 2027 runs on .NET 10 and is not targeted by this build. AutoCAD LT has no .NET API;
use the Python server for LT.

## Build

Requires a .NET SDK 8 or newer.

```powershell
cd dotnet
dotnet build -c Release
```

The plugin compiles against `C:\Program Files\Autodesk\AutoCAD 2026`. For AutoCAD 2025 add
`-p:AcadDir="C:\Program Files\Autodesk\AutoCAD 2025"`.

## Load the plugin in AutoCAD

1. Open a drawing, run `NETLOAD`, and pick
   `dotnet\src\AutoCadMcp.Plugin\bin\Release\net8.0-windows\AutoCadMcp.Plugin.dll`.
   AutoCAD asks for confirmation because the DLL is unsigned and outside a trusted location.
2. The command line shows `AutoCAD MCP: listening on pipe 'autocad-mcp'`.
3. `MCPSTATUS` shows the request count and the last error, if any.

AutoCAD keeps the DLL locked until it exits, so close AutoCAD before rebuilding the plugin.

## Register with Claude Code

```powershell
claude mcp add autocad-dotnet -- <repo>\dotnet\src\AutoCadMcp.Server\bin\Release\net8.0\AutoCadMcp.Server.exe
```

## Tools

| Tool | What it does |
|------|--------------|
| `status` | AutoCAD version, active drawing, units, and whether AutoCAD is busy |
| `create_line` | Line in model space between two 2D points, optional layer |
| `create_circle` | Circle in model space, optional layer |
| `list_entities` | Model space entities with handle, type, layer and key geometry; filter by layer or DXF type |
| `erase_entities` | Erase entities by handle; reports per-handle failures (locked layer, unknown handle) |
| `list_layers` | Layers with color, on/frozen/locked state, and the current layer |
| `create_layer` | Create a layer (ACI color) and optionally make it current |
| `zoom_extents` | Zoom the Model tab to the drawing extents |

## Behavior worth knowing

- **Busy AutoCAD:** requests are refused while a command is running in AutoCAD, with a
  message naming the command. If AutoCAD's main thread doesn't pick up a request within 10 s,
  the request is dropped unexecuted, so a stalled AutoCAD never applies stale changes later.
- **Errors** come back to the model as tool errors with the plugin's message, e.g.
  `Layer 'Walls' does not exist. Create it with create_layer first.`
- **One AutoCAD at a time:** the second AutoCAD session to load the plugin stays inactive and
  says so. Set `AUTOCAD_MCP_PIPE` (for AutoCAD and the server alike) to use another pipe name.

## Tests

```powershell
dotnet test
```

The default run needs no AutoCAD. It covers the plugin's pipe server and main-thread
dispatcher (against a stand-in dispatcher thread), and runs the real MCP server over stdio
against a fake plugin.

`LiveAutoCadTests` exercise the real plugin. They draw into the active drawing and erase what
they drew. Opt in with AutoCAD running, the plugin loaded and a drawing open:

```powershell
$env:AUTOCAD_MCP_LIVE = "1"; dotnet test --filter LiveAutoCadTests
```

## Not done yet

- Live-tested on AutoCAD 2026 only; 2025 should work (same .NET 8 API) but is untested.
- `create_layer` and `zoom_extents` have no live tests yet.
- Undo behavior of changes made through the plugin hasn't been checked.
- 2D only (Z = 0), model space only, and the active drawing only.
- No autoloader bundle yet, so the plugin has to be `NETLOAD`ed in each session.
