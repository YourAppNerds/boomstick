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

    // Legend:  # stone wall   B brick wall   M metal wall   (space) solid void
    //          . floor   , tile floor   L floor with a ceiling lamp + light   P player start
    public const string DefaultMap = @"
MMMMMMMMMMMMMMMM################
M,,,,,,,,,,,,,,M#..............#
M,,P,,,,,,,,,,,M#......L.......#
M,,,,,,L,,,,,,,,...............#
M,,,,,,,,,,,,,,M#..............#
M,,,,,,,,,,,,,,M#..............#
MMMMMMM,,MMMMMMM#.....###......#
      M,,M      #.....###......#
      M,,M      ########..######
      M,,M             #..#
      M,,M             #..#
      M,,M             #..#
BBBBBBB..BBBBBBBBBBBBB##..######
B....................B#........#
B.........L..........B#...L....#
B....BB.......BB.....B#........#
B....BB.......BB.....B#........#
B..........................,,,,#
B....BB.......BB.....B#....,,,,#
B....BB.......BB.....B#....,,,,#
B.........L..........B#....,L,,#
B....................B#........#
BBBBBBBBBBBBBBBBBBBBBB##########";

    public static Level Current { get; private set; }

    public Vector3 PlayerStart { get; private set; }
    public IReadOnlyList<Vector2I> OpenCells => _open;
    public int Width { get; private set; }
    public int Depth { get; private set; }

    private string[] _rows = Array.Empty<string>();
    private readonly List<Vector2I> _open = new();
    private readonly List<Vector2I> _lamps = new();

    private StandardMaterial3D _stone, _brick, _metal, _floor, _tiles, _ceiling, _lamp;

    public static bool IsFloorChar(char c) => c is '.' or ',' or 'L' or 'P';

    public char At(int x, int z) =>
        z < 0 || z >= _rows.Length || x < 0 || x >= _rows[z].Length ? ' ' : _rows[z][x];

    public bool IsSolidCell(int x, int z) => !IsFloorChar(At(x, z));

    public bool IsSolidAt(Vector3 p) => IsSolidCell(Mathf.FloorToInt(p.X / Cell), Mathf.FloorToInt(p.Z / Cell));

    public static Vector3 CellCenter(Vector2I c) => new((c.X + 0.5f) * Cell, 0, (c.Y + 0.5f) * Cell);

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

        for (int z = 0; z < Depth; z++)
        for (int x = 0; x < Width; x++)
        {
            char c = At(x, z);
            if (!IsFloorChar(c)) continue;
            _open.Add(new Vector2I(x, z));
            if (c == 'P') PlayerStart = CellCenter(new Vector2I(x, z));
            if (c == 'L') _lamps.Add(new Vector2I(x, z));
        }

        CreateMaterials();
        BuildMeshes();
        BuildCollision();
        BuildLights();
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
        if (c == ',') return _tiles;
        if (c == '.') return _floor;
        // L and P cells take the tile floor if any neighbour has tiles.
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
    }
}
