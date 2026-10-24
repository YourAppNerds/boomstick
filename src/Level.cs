using System;
using System.Collections.Generic;
using Godot;

namespace Boomstick;

/// <summary>
/// Builds the level from an ASCII map. All static geometry is merged into a single mesh with one
/// material (textures in a Texture2DArray, lamp lighting baked into vertex colours), so the whole
/// level is one draw call and needs no real-time lamp lights. Collision is one StaticBody3D with a
/// box per exposed wall cell plus floor and ceiling slabs.
/// </summary>
public partial class Level : Node3D
{
    public const float Cell = 2f;
    public const float Height = 3f;

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
        BuildMeshes();
        BuildCollision();
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

    // Texture layers of the level's Texture2DArray.
    private const int TexStone = 0, TexBrick = 1, TexMetal = 2, TexFloor = 3, TexTiles = 4, TexCeiling = 5, TexLamp = 6, TexExit = 7;

    /// <summary>
    /// The whole level is one surface with one material: the textures are layers of a
    /// Texture2DArray (layer index in UV2.x), and the lamp lighting is baked into vertex colours,
    /// stored at half scale so values up to 2 survive the 8-bit colour format. The baked light goes
    /// out as EMISSION, so the base pass needs no lights at all. The shader stays lit so that the
    /// dynamic muzzle flash and explosion lights still add on top while they are visible.
    /// </summary>
    private const string LevelShader = @"
shader_type spatial;
render_mode diffuse_lambert, specular_disabled, ambient_light_disabled;
uniform sampler2DArray textures : source_color, filter_nearest_mipmap, repeat_enable;
void fragment() {
	vec3 t = texture(textures, vec3(UV, UV2.x)).rgb;
	ALBEDO = t;
	EMISSION = t * COLOR.rgb * 2.0;
}
";

    // Baked light model: the D2 scene lighting, evaluated per vertex with Godot's omni light
    // falloff (no shadows, like the real-time lights it replaces).
    private static readonly Color AmbientLight = new Color(0.55f, 0.5f, 0.48f) * 0.5f;
    private static readonly Color LampColor = new Color(1f, 0.85f, 0.65f) * 1.8f;
    private const float LampRange = 10f, LampDecay = 1.2f;
    private static readonly Color ExitColor = new Color(0.4f, 1f, 0.45f) * 1.5f;
    private const float ExitRange = 8f, ExitDecay = 1f;

    private readonly List<(Vector3 Pos, Color Color, float Range, float Decay)> _bakeLights = new();

    // Mesh arrays, filled by Face() and committed once in BuildMeshes().
    private readonly List<Vector3> _verts = new();
    private readonly List<Vector3> _normals = new();
    private readonly List<Vector2> _uvs = new();
    private readonly List<Vector2> _uv2s = new();
    private readonly List<Color> _colors = new();
    private readonly List<int> _indices = new();

    private static float OmniAttenuation(float distance, float range, float decay)
    {
        float nd = distance / range;
        nd *= nd;
        nd *= nd;
        nd = Mathf.Max(1f - nd, 0f);
        nd *= nd;
        return nd * Mathf.Pow(Mathf.Max(distance, 0.0001f), -decay);
    }

    private Color BakedLight(Vector3 p, Vector3 n)
    {
        var c = AmbientLight;
        foreach (var (pos, color, range, decay) in _bakeLights)
        {
            var to = pos - p;
            float d2 = to.LengthSquared();
            if (d2 >= range * range) continue;
            float d = Mathf.Sqrt(d2);
            float ndl = n.Dot(to) / Mathf.Max(d, 0.0001f);
            if (ndl <= 0f) continue;
            c += color * (ndl * OmniAttenuation(d, range, decay));
        }
        return c;
    }

    private void CollectBakeLights()
    {
        foreach (var c in _lamps)
            _bakeLights.Add((CellCenter(c) + new Vector3(0, Height - 0.3f, 0), LampColor, LampRange, LampDecay));
        if (_exits.Count > 0)
        {
            var mid = Vector3.Zero;
            foreach (var c in _exits) mid += CellCenter(c);
            _bakeLights.Add((mid / _exits.Count + new Vector3(0, 1.2f, 0), ExitColor, ExitRange, ExitDecay));
        }
    }

    private static int WallTex(char c) => c switch
    {
        'B' => TexBrick,
        'M' => TexMetal,
        _ => TexStone,
    };

    private int FloorTex(int x, int z)
    {
        char c = At(x, z);
        if (c == 'X') return TexExit;
        if (c == ',') return TexTiles;
        if (c == '.') return TexFloor;
        // Lamp, start and spawn cells take the tile floor if any neighbour has tiles.
        return At(x - 1, z) == ',' || At(x + 1, z) == ',' || At(x, z - 1) == ',' || At(x, z + 1) == ',' ? TexTiles : TexFloor;
    }

    private static Texture2DArray LevelTextures()
    {
        var images = new Godot.Collections.Array<Image>();
        foreach (var tex in new[]
                 {
                     ProcTex.Stone(), ProcTex.Brick(), ProcTex.Metal(), ProcTex.Floor(), ProcTex.Tiles(),
                     ProcTex.Ceiling(), ProcTex.Lamp(), ProcTex.ExitFloor(),
                 })
            images.Add(tex.GetImage());
        var array = new Texture2DArray();
        array.CreateFromImages(images);
        return array;
    }

    private void BuildMeshes()
    {
        CollectBakeLights();
        for (int z = 0; z < Depth; z++)
        for (int x = 0; x < Width; x++)
        {
            if (IsSolidCell(x, z)) continue;
            float x0 = x * Cell, x1 = x0 + Cell, z0 = z * Cell, z1 = z0 + Cell;
            int floorTex = FloorTex(x, z);
            bool lamp = At(x, z) == 'L';

            // Floor (normal +Y, seen from above) and ceiling (normal -Y, seen from below).
            Face(new(x0, 0, z0), new(x1, 0, z0), new(x1, 0, z1), new(x0, 0, z1), Vector3.Up, 1f, 1f,
                floorTex, fullBright: floorTex == TexExit);
            Face(new(x1, Height, z0), new(x0, Height, z0), new(x0, Height, z1), new(x1, Height, z1), Vector3.Down, 1f, 1f,
                lamp ? TexLamp : TexCeiling, fullBright: lamp);

            // Walls: a face wherever a neighbouring cell is solid, facing into this cell.
            WallFace(x, z, 0, -1);
            WallFace(x, z, 0, 1);
            WallFace(x, z, -1, 0);
            WallFace(x, z, 1, 0);
        }

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = _verts.ToArray();
        arrays[(int)Mesh.ArrayType.Normal] = _normals.ToArray();
        arrays[(int)Mesh.ArrayType.TexUV] = _uvs.ToArray();
        arrays[(int)Mesh.ArrayType.TexUV2] = _uv2s.ToArray();
        arrays[(int)Mesh.ArrayType.Color] = _colors.ToArray();
        arrays[(int)Mesh.ArrayType.Index] = _indices.ToArray();
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        var mat = new ShaderMaterial { Shader = new Shader { Code = LevelShader } };
        mat.SetShaderParameter("textures", LevelTextures());
        mesh.SurfaceSetMaterial(0, mat);
        VertexCount = _verts.Count;
        AddChild(new MeshInstance3D
        {
            Name = "LevelMesh",
            Mesh = mesh,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        });
        _verts.Clear(); _normals.Clear(); _uvs.Clear(); _uv2s.Clear(); _colors.Clear(); _indices.Clear();
    }

    /// <summary>Vertices in the merged level mesh (reported in the READY line).</summary>
    public int VertexCount { get; private set; }

    private void WallFace(int x, int z, int dx, int dz)
    {
        int nx = x + dx, nz = z + dz;
        if (!IsSolidCell(nx, nz)) return;
        // The face sits on the shared edge; its normal points back into the open cell.
        var normal = new Vector3(-dx, 0, -dz);
        var center = CellCenter(new Vector2I(x, z)) + new Vector3(dx, 0, dz) * (Cell * 0.5f);
        var forward = -normal; // the viewer looks at the wall along this direction
        var right = forward.Cross(Vector3.Up) * (Cell * 0.5f);
        var up = new Vector3(0, Height, 0);
        Face(center - right + up, center + right + up, center + right, center - right, normal, 1f, Height / Cell,
            WallTex(At(nx, nz)), fullBright: false);
    }

    /// <summary>
    /// Adds a quad given its corners as seen from the front: top-left, top-right, bottom-right,
    /// bottom-left (Godot treats clockwise triangles as front-facing). The quad is split into a
    /// grid of roughly 1 m pieces so the per-vertex lighting has enough resolution.
    /// </summary>
    private void Face(Vector3 tl, Vector3 tr, Vector3 br, Vector3 bl, Vector3 n, float uMax, float vMax, int layer, bool fullBright)
    {
        int nu = Mathf.Max(1, Mathf.RoundToInt(tl.DistanceTo(tr))), nv = Mathf.Max(1, Mathf.RoundToInt(tl.DistanceTo(bl)));
        int baseIndex = _verts.Count;
        var uv2 = new Vector2(layer, 0);
        for (int j = 0; j <= nv; j++)
        for (int i = 0; i <= nu; i++)
        {
            float u = i / (float)nu, v = j / (float)nv;
            var p = (tl + (tr - tl) * u) + (bl - tl) * v;
            _verts.Add(p);
            _normals.Add(n);
            _uvs.Add(new Vector2(u * uMax, v * vMax));
            _uv2s.Add(uv2);
            var light = fullBright ? Colors.White : BakedLight(p, n);
            _colors.Add(new Color(Mathf.Min(light.R * 0.5f, 1f), Mathf.Min(light.G * 0.5f, 1f), Mathf.Min(light.B * 0.5f, 1f)));
        }
        for (int j = 0; j < nv; j++)
        for (int i = 0; i < nu; i++)
        {
            int a = baseIndex + j * (nu + 1) + i; // top-left of this piece
            int b = a + 1, c = a + nu + 2, d = a + nu + 1;
            _indices.Add(a); _indices.Add(b); _indices.Add(c);
            _indices.Add(a); _indices.Add(c); _indices.Add(d);
        }
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
}
