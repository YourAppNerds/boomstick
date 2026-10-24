# Boomstick

A small Doom-like boomer shooter made with Godot 4 and C#. Its main job is to test a custom
Godot C# **Web** export (the .NET runtime running in the browser on the Mono interpreter). The
whole game is built in code: the level comes from an ASCII map, and every texture is generated
procedurally, so the project has no art assets.

Current state (step D1): you can walk around a few rooms and corridors. There is a debug
overlay for measuring performance and a stress test that spawns wandering billboard sprites.
Weapons and enemies come later.

## Controls

| Input | Action |
| --- | --- |
| Click | Capture the mouse (pointer lock) |
| Mouse | Look |
| W A S D / arrow keys | Move |
| Shift | Sprint |
| Space | Jump |
| Esc | Release the mouse |
| F3 | Toggle the debug overlay |
| F4 | Spawn 50 more wandering dummies |
| F5 | Remove all dummies |

## Debug overlay and stress test

The overlay (`src/DebugOverlay.cs`, an autoload) shows:

- FPS and frame time.
- Time spent inside C# `_Process` and `_PhysicsProcess` bodies, measured with `Stopwatch`
  through `Perf`.
- Engine process time, which also includes the native-to-C# call overhead.
- Node count, object count and draw calls.
- GC collection counts (gen 0/1/2) and managed memory.

It also prints a `[perf]` line to stdout every 5 seconds. On web, that goes to the browser
console.

For a scripted run, the game adds 50 dummies per step and prints one `STRESS ...` line per step:

- Desktop: `godot --path . -- --autostress=20 --quit-when-done`
- Web: open `index.html?autostress=20`

## Building

This project needs the custom engine branch **YAN-kyle/godot `web-dotnet-rebased`**: a Godot
4.8 beta editor with .NET support, plus the Web export templates built from that branch. It
also needs the matching GodotSharp 4.8.0-beta NuGet packages in a local folder; `nuget.config`
points at `D:\source\repos\godot-web-dotnet\nupkgs`, so change that path for your machine.
Stock Godot releases cannot export C# projects to Web.

```bash
# Desktop: open in the custom editor, or run directly
godot --path .

# Web: release build without threads, written to build/web
GODOT=/path/to/godot.windows.editor.x86_64.mono.console.exe scripts/export-web.sh
python -m http.server -d build/web 8000   # then open http://localhost:8000
```

The Web preset is built without threads, so it does not need COOP/COEP headers and can be
hosted on GitHub Pages. The renderer is `gl_compatibility` (WebGL 2).

## Layout

- `src/Main.cs`: scene setup, input actions, environment, HUD, dummy spawning, autostress.
- `src/Level.cs`: the ASCII map, chunked mesh building, collision and lights.
- `src/ProcTex.cs`: procedural pixel-art textures.
- `src/Player.cs`: first-person controller.
- `src/Dummy.cs`: the stress-test billboard entity.
- `src/DebugOverlay.cs` and `src/Perf.cs`: performance measurement.

## Legal

"Doom" is a trademark of id Software / ZeniMax Media. Boomstick is an unaffiliated, non-commercial
homage to the genre. It uses no assets, code or data from Doom.
