using System;
using System.Collections.Generic;
using Godot;

namespace Boomstick;

/// <summary>
/// Builds the level from an ASCII map. Geometry is merged into one ArrayMesh per 8x8-cell
/// chunk (one surface per material), so the whole map costs a few dozen draw calls and each
/// chunk only receives the lights near it (the compatibility renderer caps lights per object).
/// Collision is one StaticBody3D with a box per exposed wall cell plus floor and ceiling slabs.
/// </summary>
public partial class Level : Node3D
{
    public const float Cell = 2f;
    public const float Height = 3f;
    private const int Chunk = 8;

    // Legend:
    //   walls:    # stone   B brick   M metal   (space) solid void
    //   floors:   . floor   , tiles   L floor with a ceiling lamp   P player start   X exit
    //   enemies:  f fiend (melee rusher)   i imp (fireballs)   O brute (tough, plasma volleys)
    //   pickups:  h stimpack   m medkit   a armor   A mega armor   c bullets   s shells
    //             r rockets   G shotgun   R rocket launcher
    public const string DefaultMap = @"
MMMMMMMMMMMMMMMM################################
M,,,,,,,,,,,,,,M#..............#..i......#....R#
M,,P,,,,,,,,,,,M#....i..L......#.........#..r..#
M,,,,,,L,,,,,,,,...............#...##....#.....#
M,,c,,,,,,,,,,,M#.......G...............i....L.#
M,,,,,h,,,,,,,,M#..............#...##....#.....#
MMMMMMM,,MMMMMMM#.....###......#.L....f..#..a..#
      M,,M      #.....###..i...#.........#######
      M,,M      ########..######...c...s.#
      M,,M             #..#    #...i.....#
      M,,M             #..#    ###########
      M,,M             #..#
BBBBBBB..BBBBBBBBBBBBB##..######
B....f...............B#..i.....#
B.........L..........B#...L....#
B....BB.......BB.....B#........#
B....BB...i...BB..f..B#..m.....#
B..........................,,,,#
B....BB.......BB.....B#....,,,,#
B....BB.......BB.....B#..i.,,,,#
B.........L..........B#....,L,,#
B.......s............B#.....a..#
BBBBBBBBBB..BBBBBBBBBB##########
         B..B
         B.hB
         B.LB
#########B..B#################
#........,..,.........#.......#
#..i.....,..,.....f...#..i....#
#....##.........##....#.......#
#....##....L....##.............MMMMMMMMM
#..s..........................,,,,,,,,M
#..f.....O.........f..#........,,X,,O,M
#....##.........##....#.......#,,,L,,,M
#....##....L....##....#.......#,,,,X,,M
#.........r.......i...#..s..h.#MMMMMMMM
#..m..................#...i...#
##############################";

    public static Level Current { get; private set; }

    public Vector3 PlayerStart { get; private set; }
    public IReadOnlyList<Vector2I> OpenCells => _open;
    public IReadOnlyList<(char Kind, Vector2I Cell)> Spawns => _spawns;
    public int Width { get; private set; }
    public int Depth { get; private set; }

    private string[] _rows = Array.Empty<string>();
    private bool[] _solid = Array.Empty<bool>();
    private float[] _light = Array.Empty<float>();
    private readonly List<Vector2I> _open = new();
    private readonly List<Vector2I> _lamps = new();
    private readonly List<Vector2I> _exits = new();
    private readonly List<(char, Vector2I)> _spawns = new();

    private StandardMaterial3D _stone, _brick, _metal, _floor, _tiles, _ceiling, _lamp, _exit;

    public const string SpawnChars = "fiOhmaAcsrGR";

    public static bool IsFloorChar(char c) => c is '.' or ',' or 'L' or 'P' or 'X' || SpawnChars.IndexOf(c) >= 0;

    public char At(int x, int z) =>
        z < 0 || z >= _rows.Length || x < 0 || x >= _rows[z].Length ? ' ' : _rows[z][x];

    public bool IsSolidCell(int x, int z) =>
        x < 0 || z < 0 || x >= Width || z >= Depth || _solid[z * Width + x];

    public bool IsSolidAt(Vector3 p) => IsSolidCell(Mathf.FloorToInt(p.X / Cell), Mathf.FloorToInt(p.Z / Cell));

    public static Vector2I CellOf(Vector3 p) => new(Mathf.FloorToInt(p.X / Cell), Mathf.FloorToInt(p.Z / Cell));

    public static Vector3 CellCenter(Vector2I c) => new((c.X + 0.5f) * Cell, 0, (c.Y + 0.5f) * Cell);

    public bool IsExitCell(Vector2I c) => At(c.X, c.Y) == 'X';

    /// <summary>True if a circle of radius r at p (on the XZ plane) does not overlap any solid cell.</summary>
    public bool CanOccupy(Vector3 p, float r) =>
        !IsSolidAt(new Vector3(p.X - r, 0, p.Z - r)) && !IsSolidAt(new Vector3(p.X + r, 0, p.Z - r)) &&
        !IsSolidAt(new Vector3(p.X - r, 0, p.Z + r)) && !IsSolidAt(new Vector3(p.X + r, 0, p.Z + r));

    /// <summary>
    /// Sector-style light level of a cell (0.3 .. 1), precomputed from the lamp positions. Sprites
    /// are unshaded and use this as their modulate colour, which is much cheaper than lighting them.
    /// </summary>
    public float LightAt(Vector2I c) =>
        c.X < 0 || c.Y < 0 || c.X >= Width || c.Y >= Depth ? 0.3f : _light[c.Y * Width + c.X];

    /// <summary>
    /// Grid line-of-sight test (DDA over cells). Cheap first check before a physics raycast.
    /// </summary>
    public bool GridLineClear(Vector3 a, Vector3 b)
    {
        float ax = a.X / Cell, az = a.Z / Cell, bx = b.X / Cell, bz = b.Z / Cell;
        int x = Mathf.FloorToInt(ax), z = Mathf.FloorToInt(az);
        int ex = Mathf.FloorToInt(bx), ez = Mathf.FloorToInt(bz);
        float dx = bx - ax, dz = bz - az;
        int sx = dx > 0 ? 1 : -1, sz = dz > 0 ? 1 : -1;
        float tdx = dx != 0 ? Mathf.Abs(1f / dx) : float.MaxValue;
        float tdz = dz != 0 ? Mathf.Abs(1f / dz) : float.MaxValue;
        float tmx = dx != 0 ? (dx > 0 ? (x + 1 - ax) : (ax - x)) * tdx : float.MaxValue;
        float tmz = dz != 0 ? (dz > 0 ? (z + 1 - az) : (az - z)) * tdz : float.MaxValue;
        for (int guard = 0; guard < 256; guard++)
        {
            if (IsSolidCell(x, z)) return false;
            if (x == ex && z == ez) return true;
            if (tmx < tmz) { tmx += tdx; x += sx; }
            else { tmz += tdz; z += sz; }
        }
        return true;
    }

    /// <summary>
    /// Breadth-first distance field (in steps) from a cell to every open cell, with diagonal
    /// moves allowed when both side cells are open. Unreachable cells get int.MaxValue.
    /// Enemies walk down this field towards the player; it doubles as the "sound" distance.
    /// </summary>
    public void DistanceField(Vector2I from, int[] dist, int[] queue)
    {
        Array.Fill(dist, int.MaxValue);
        if (IsSolidCell(from.X, from.Y)) return;
        int head = 0, tail = 0;
        dist[from.Y * Width + from.X] = 0;
        queue[tail++] = from.Y * Width + from.X;
        while (head < tail)
        {
            int idx = queue[head++];
            int cx = idx % Width, cz = idx / Width, d = dist[idx] + 1;
            for (int dz = -1; dz <= 1; dz++)
            for (int dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dz == 0) continue;
                int nx = cx + dx, nz = cz + dz;
                if (IsSolidCell(nx, nz)) continue;
                if (dx != 0 && dz != 0 && (IsSolidCell(cx + dx, cz) || IsSolidCell(cx, cz + dz))) continue;
                int ni = nz * Width + nx;
                if (dist[ni] <= d) continue;
                dist[ni] = d;
                queue[tail++] = ni;
            }
        }
    }

    public void Build(string map)
    {
        Current = this;
        var lines = new List<string>();
        foreach (var raw in map.Replace("\r", "").Split('\n'))
            if (raw.Trim().Length > 0) lines.Add(raw);
        Depth = lines.Count;
        foreach (var l in lines) Width = Math.Max(Width, l.Length);
        _rows = new string[Depth];
        for (int z = 0; z < Depth; z++) _rows[z] = lines[z].PadRight(Width);
        _solid = new bool[Width * Depth];

        for (int z = 0; z < Depth; z++)
        for (int x = 0; x < Width; x++)
        {
            char c = At(x, z);
            _solid[z * Width + x] = !IsFloorChar(c);
            if (!IsFloorChar(c)) continue;
            var cell = new Vector2I(x, z);
            _open.Add(cell);
            if (c == 'P') PlayerStart = CellCenter(cell);
            if (c == 'L') _lamps.Add(cell);
            if (c == 'X') _exits.Add(cell);
            if (SpawnChars.IndexOf(c) >= 0) _spawns.Add((c, cell));
        }

        ComputeLight();
        CreateMaterials();
        BuildMeshes();
        BuildCollision();
        BuildLights();
    }

    private void ComputeLight()
    {
        _light = new float[Width * Depth];
        for (int z = 0; z < Depth; z++)
        for (int x = 0; x < Width; x++)
        {
            float l = 0.32f;
            var p = CellCenter(new Vector2I(x, z));
            foreach (var lamp in _lamps)
            {
                float d = p.DistanceTo(CellCenter(lamp));
                if (d < 10f) l += 0.75f * (1f - d / 10f) * (1f - d / 10f);
            }
            foreach (var ex in _exits)
            {
                float d = p.DistanceTo(CellCenter(ex));
                if (d < 6f) l += 0.4f * (1f - d / 6f);
            }
            _light[z * Width + x] = Mathf.Min(l, 1f);
        }
    }

    private static StandardMaterial3D Mat(Texture2D tex, bool unshaded = false) => new()
    {
        AlbedoTexture = tex,
        TextureFilter = BaseMaterial3D.TextureFilterEnum.NearestWithMipmaps,
        ShadingMode = unshaded ? BaseMaterial3D.ShadingModeEnum.Unshaded : BaseMaterial3D.ShadingModeEnum.PerPixel,
        Roughness = 1f,
        MetallicSpecular = 0.1f,
    };

    private void CreateMaterials()
    {
        _stone = Mat(ProcTex.Stone());
        _brick = Mat(ProcTex.Brick());
        _metal = Mat(ProcTex.Metal());
        _floor = Mat(ProcTex.Floor());
        _tiles = Mat(ProcTex.Tiles());
        _ceiling = Mat(ProcTex.Ceiling());
        _lamp = Mat(ProcTex.Lamp(), unshaded: true);
        _exit = Mat(ProcTex.ExitFloor(), unshaded: true);
    }

    private StandardMaterial3D WallMat(char c) => c switch
    {
        'B' => _brick,
        'M' => _metal,
        _ => _stone,
    };

    private StandardMaterial3D FloorMat(int x, int z)
    {
        char c = At(x, z);
        if (c == 'X') return _exit;
        if (c == ',') return _tiles;
        if (c == '.') return _floor;
        // Lamp, start and spawn cells take the tile floor if any neighbour has tiles.
        return At(x - 1, z) == ',' || At(x + 1, z) == ',' || At(x, z - 1) == ',' || At(x, z + 1) == ',' ? _tiles : _floor;
    }

    private void BuildMeshes()
    {
        var tools = new Dictionary<StandardMaterial3D, SurfaceTool>();
        SurfaceTool Tool(StandardMaterial3D m)
        {
            if (!tools.TryGetValue(m, out var st))
            {
                st = new SurfaceTool();
                st.Begin(Mesh.PrimitiveType.Triangles);
                st.SetMaterial(m);
                tools[m] = st;
            }
            return st;
        }

        for (int cz = 0; cz < Depth; cz += Chunk)
        for (int cx = 0; cx < Width; cx += Chunk)
        {
            tools.Clear();
            for (int z = cz; z < Math.Min(cz + Chunk, Depth); z++)
            for (int x = cx; x < Math.Min(cx + Chunk, Width); x++)
            {
                if (IsSolidCell(x, z)) continue;
                float x0 = x * Cell, x1 = x0 + Cell, z0 = z * Cell, z1 = z0 + Cell;

                // Floor (normal +Y, seen from above) and ceiling (normal -Y, seen from below).
                Quad(Tool(FloorMat(x, z)), new(x0, 0, z0), new(x1, 0, z0), new(x1, 0, z1), new(x0, 0, z1),
                    Vector3.Up, 1f, 1f);
                Quad(Tool(At(x, z) == 'L' ? _lamp : _ceiling),
                    new(x1, Height, z0), new(x0, Height, z0), new(x0, Height, z1), new(x1, Height, z1),
                    Vector3.Down, 1f, 1f);

                // Walls: a face wherever a neighbouring cell is solid, facing into this cell.
                WallFace(x, z, 0, -1, Tool);
                WallFace(x, z, 0, 1, Tool);
                WallFace(x, z, -1, 0, Tool);
                WallFace(x, z, 1, 0, Tool);
            }

            if (tools.Count == 0) continue;
            var mesh = new ArrayMesh();
            foreach (var st in tools.Values)
                st.Commit(mesh);
            AddChild(new MeshInstance3D
            {
                Name = $"Chunk_{cx / Chunk}_{cz / Chunk}",
                Mesh = mesh,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            });
        }
    }

    private void WallFace(int x, int z, int dx, int dz, Func<StandardMaterial3D, SurfaceTool> tool)
    {
        int nx = x + dx, nz = z + dz;
        if (!IsSolidCell(nx, nz)) return;
        // The face sits on the shared edge; its normal points back into the open cell.
        var normal = new Vector3(-dx, 0, -dz);
        var center = CellCenter(new Vector2I(x, z)) + new Vector3(dx, 0, dz) * (Cell * 0.5f);
        var forward = -normal; // the viewer looks at the wall along this direction
        var right = forward.Cross(Vector3.Up) * (Cell * 0.5f);
        var up = new Vector3(0, Height, 0);
        Quad(tool(WallMat(At(nx, nz))), center - right + up, center + right + up, center + right, center - right,
            normal, 1f, Height / Cell);
    }

    /// <summary>
    /// Adds a quad given its corners as seen from the front: top-left, top-right, bottom-right,
    /// bottom-left. Godot treats clockwise triangles as front-facing.
    /// </summary>
    private static void Quad(SurfaceTool st, Vector3 tl, Vector3 tr, Vector3 br, Vector3 bl, Vector3 n, float uMax, float vMax)
    {
        void V(Vector3 p, float u, float v)
        {
            st.SetNormal(n);
            st.SetUV(new Vector2(u, v));
            st.AddVertex(p);
        }
        V(tl, 0, 0); V(tr, uMax, 0); V(br, uMax, vMax);
        V(tl, 0, 0); V(br, uMax, vMax); V(bl, 0, vMax);
    }

    private void BuildCollision()
    {
        var body = new StaticBody3D { Name = "Collision" };
        AddChild(body);
        var wallShape = new BoxShape3D { Size = new Vector3(Cell, Height, Cell) };
        for (int z = 0; z < Depth; z++)
        for (int x = 0; x < Width; x++)
        {
            if (!IsSolidCell(x, z)) continue;
            bool exposed = !IsSolidCell(x - 1, z) || !IsSolidCell(x + 1, z) || !IsSolidCell(x, z - 1) || !IsSolidCell(x, z + 1);
            if (!exposed) continue;
            body.AddChild(new CollisionShape3D
            {
                Shape = wallShape,
                Position = CellCenter(new Vector2I(x, z)) + new Vector3(0, Height * 0.5f, 0),
            });
        }
        var size = new Vector3(Width * Cell, 1f, Depth * Cell);
        var mid = new Vector3(Width * Cell * 0.5f, 0, Depth * Cell * 0.5f);
        body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = size }, Position = mid + new Vector3(0, -0.5f, 0) });
        body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = size }, Position = mid + new Vector3(0, Height + 0.5f, 0) });
    }

    private void BuildLights()
    {
        foreach (var c in _lamps)
        {
            AddChild(new OmniLight3D
            {
                Position = CellCenter(c) + new Vector3(0, Height - 0.3f, 0),
                LightColor = new Color(1f, 0.85f, 0.65f),
                LightEnergy = 1.8f,
                OmniRange = 10f,
                OmniAttenuation = 1.2f,
                ShadowEnabled = false,
            });
        }
        if (_exits.Count > 0)
        {
            var mid = Vector3.Zero;
            foreach (var c in _exits) mid += CellCenter(c);
            AddChild(new OmniLight3D
            {
                Position = mid / _exits.Count + new Vector3(0, 1.2f, 0),
                LightColor = new Color(0.4f, 1f, 0.45f),
                LightEnergy = 1.5f,
                OmniRange = 8f,
                ShadowEnabled = false,
            });
        }
    }
}
