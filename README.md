# Boomstick

A small Doom-like boomer shooter made with Godot 4 and C#. Its main job is to test a custom
Godot C# **Web** export (the .NET runtime running in the browser on the Mono interpreter). The
whole game is built in code. The level comes from an ASCII map, and all textures, sprites and
sounds are generated procedurally, so the project has no art or audio assets.

Current state (step D2): one short level to play through. It has three weapons, three enemy
types, pickups, a Doom-style status bar and an exit. Touch controls are not in yet.

## Controls

| Input | Action |
| --- | --- |
| Click | Capture the mouse (pointer lock) |
| Mouse | Look |
| Left mouse / Ctrl | Fire |
| 1 / 2 / 3, mouse wheel | Pistol / shotgun / rocket launcher |
| W A S D / arrow keys | Move |
| Shift | Run |
| Space | Jump |
| R | Restart (after you die or finish the level) |
| Esc | Release the mouse |
| F3 | Toggle the debug overlay |
| F4 | Spawn 50 managed enemies (full AI, one manager node) |
| F6 | Spawn 50 per-node dummies (one C# `_Process` each, D1 style) |
| F5 | Remove all stress-test spawns |

## Game

- **Weapons.** The pistol and the shotgun (8 pellets) are hitscan: a physics ray finds the
  wall, and C# cylinder tests find the enemies. The rocket launcher fires projectiles with
  splash damage that knock you back. The weapon sprite has fire and recoil frames, and a
  muzzle flash light fires with each shot.
- **Enemies.**
  - Fiend: a fast melee rusher.
  - Imp: throws fireballs and keeps its distance.
  - Brute: tough, with a three-shot plasma volley.

  Enemies sleep until they see you (a grid check, then a physics raycast) or hear a shot (path
  distance). Then they follow a BFS distance field on the map grid.
- **Pickups.** Stimpack, medikit, armor, mega armor, bullets, shells, rockets, shotgun and
  rocket launcher.
- **Exit.** Reach the green exit pad. The game shows your time and kills.

Map letters (`src/Level.cs`): `f i O` enemies, `h m a A c s r G R` pickups, `X` exit, and
`P` start.

## Architecture notes for the web build

- On the web interpreter, each C# script callback costs about 11 µs per node. So every
  enemy, projectile, effect and pickup is plain C# data that one `EntityManager._Process`
  updates.
- Enemies and projectiles are drawn with one `MultiMeshInstance3D` per type. A billboard
  shader takes the atlas frame and light level from `INSTANCE_CUSTOM`, and the instance buffer
  is uploaded once per frame (`MultiMesh.Buffer`). That costs a few draw calls for any number
  of enemies. `enemyrender=nodes` switches to one Sprite3D per enemy, for comparison.
- Sounds are synthesized into `AudioStreamWAV`s at startup and played through a pool of 12
  players.

## Debug overlay and stress test

The overlay (`src/DebugOverlay.cs`, an autoload) shows:

- FPS and frame time.
- Time spent inside C# `_Process` / `_PhysicsProcess` bodies.
- **cpu frame**: from the first callback of the frame to `RenderingServer.frame_post_draw`.
  This covers scripts, engine process and render submission, but not the vsync wait.
- **render submit**: from the last process callback to `frame_post_draw`.
- **engine process max/1s**: the engine's `TIME_PROCESS`. It is the *maximum* over the last
  second and includes rendering, so it is a worst-case figure, not an average.
- Node and object counts, draw calls, GC counts and managed memory.

Scripted stress runs add 50 entities per step and print one `STRESS ...` line per step:

- Desktop: `godot --path . -- --autostress=20 [--stressmode=enemies|wander|nodes] [--enemyrender=nodes] --quit-when-done`
- Web: `index.html?autostress=20[&stressmode=wander|nodes][&enemyrender=nodes]`

The modes:

- `enemies` (the default): real AI through the manager.
- `wander`: managed, but running the same trivial logic as the D1 dummy.
- `nodes`: the D1 per-node dummies.

Desktop test arguments: `--warp=x,z,yawDeg`, `--giveall`, `--weapon=1..3`, `--godmode`,
`--autofire`. They are useful with Godot's `--write-movie` for screenshots.

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
hosted on GitHub Pages. The renderer is `gl_compatibility` (WebGL 2). `build/` holds a
`.gdignore` so that screenshots and logs there never end up in the pck.

## Layout

- `src/Main.cs`: scene setup, input, spawning from the map, restart, exit, stress tests.
- `src/Level.cs`: the ASCII map, meshes, collision, lights, light levels, line of sight, distance field.
- `src/EntityManager.cs`: enemies, AI, projectiles, effects, pickups, batched rendering.
- `src/Defs.cs`: enemy and weapon tuning, and shared game state.
- `src/Player.cs`, `src/Weapons.cs`: the player, health and armor, and the weapons.
- `src/Hud.cs`: the status bar, weapon view, face, flashes and messages.
- `src/Pix.cs`, `src/Art.cs`, `src/HudArt.cs`, `src/ProcTex.cs`: procedural pixel art and the 5x7 font.
- `src/Sfx.cs`: procedural sound.
- `src/Dummy.cs`: the D1 per-node stress entity.
- `src/DebugOverlay.cs` and `src/Perf.cs`: performance measurement.

## Legal

"Doom" is a trademark of id Software / ZeniMax Media. Boomstick is an unaffiliated, non-commercial
homage to the genre. It uses no assets, code or data from Doom.
