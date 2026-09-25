using System.Numerics;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using Wc3ModelViewer.Core.Casc;
using Wc3ModelViewer.Core.Formats;
using Wc3ModelViewer.Core.Formats.Popcorn;

namespace Wc3ModelViewer;

/// <summary>
/// Draws a model's particle and ribbon emitters into the WPF viewport.
/// </summary>
/// <remarks>
/// Each emitter owns one <see cref="MeshGeometry3D"/> whose vertex buffer is rewritten every frame:
/// particles are camera-facing quads, ribbons a triangle strip through the trail. Geometry is rebuilt
/// rather than transformed because the particle count changes constantly, and one mesh per emitter
/// (not per particle) keeps the visual-tree cost flat — a few hundred particles in one mesh render
/// far cheaper than a few hundred models.
///
/// WPF 3D has no additive blend mode. Warcraft III's particles are overwhelmingly additive, so an
/// emissive-looking approximation is used: the sprite is drawn through an <see cref="EmissiveMaterial"/>,
/// which adds its colour to what is already there. That is exactly additive for the common case of
/// glows and fire, and merely close for Blend/Modulate, which are rare by comparison.
/// </remarks>
public sealed class EffectLayer
{
    private readonly MdxModel _model;
    private readonly MdxEffectSimulator _sim;
    private readonly List<EmitterVisual> _particleVisuals = [];
    private readonly List<EmitterVisual> _ribbonVisuals = [];

    private sealed class EmitterVisual
    {
        public required MeshGeometry3D Mesh { get; init; }
        public required GeometryModel3D Model { get; init; }
        public required Brush Brush { get; init; }
        public required Material Material { get; init; }

        // Rebuilt each frame and then assigned to the mesh. Mutating the collections already
        // attached to the mesh and reassigning the same references would raise no change
        // notification, and the viewport would keep drawing the first frame forever.
        public Point3DCollection Positions = [];
        public PointCollection Uvs = [];
        public Int32Collection Indices = [];
    }

    public EffectLayer(MdxModel model, Wc3TextureCache textures, string modelCascName)
    {
        _model = model;
        _sim = new MdxEffectSimulator(model);

        foreach (var e in model.ParticleEmitters)
            _particleVisuals.Add(Build(TextureFor(model, textures, modelCascName, e.TextureId), Emissive(e.Blend)));

        // PopcornFX effects whose scripts loaded run for real; their fixed-field stand-ins (kept for
        // export) would only draw a second, cruder copy on top, so they are switched off here.
        foreach (var corn in model.PopcornEmitters)
        {
            if (corn.Runtime is null) continue;
            var visual = new CornVisual { Player = new PkCornPlayer(model, corn) };
            foreach (var layer in corn.Runtime.Layers)
                foreach (var r in layer.Renderers)
                {
                    if (r.Kind is not (PkRendererKind.Billboard or PkRendererKind.Distortion)) continue;
                    visual.Renderers[r] = new RendererVisual { Def = r, Bitmap = PopcornTexture(textures, modelCascName, r.Texture) };
                }
            _corns.Add(visual);
            for (int i = 0; i < model.ParticleEmitters.Count; i++)
            {
                var e = model.ParticleEmitters[i];
                if (e.IsPopcorn && e.NodeIndex == corn.NodeIndex && e.Name.StartsWith(corn.Name + "/", StringComparison.Ordinal))
                    _sim.HiddenParticleEmitters.Add(i);
            }
        }

        foreach (var e in model.RibbonEmitters)
        {
            int texId = -1;
            if ((uint)e.MaterialId < (uint)model.Materials.Count)
            {
                var layers = model.Materials[e.MaterialId].Layers;
                if (layers.Count > 0) texId = layers[Math.Clamp(e.TextureSlot, 0, layers.Count - 1)].TextureId;
            }
            _ribbonVisuals.Add(Build(TextureFor(model, textures, modelCascName, texId), emissive: true));
        }
    }

    /// <summary>Particle emitters that should not be simulated or drawn.</summary>
    public HashSet<int> HiddenParticleEmitters => _sim.HiddenParticleEmitters;

    public bool HasAnything => _particleVisuals.Count > 0 || _ribbonVisuals.Count > 0 || _corns.Count > 0;

    /// <summary>Player slot whose colour team-coloured PopcornFX effects (the hero glow) take.</summary>
    public int PlayerSlot
    {
        set { foreach (var c in _corns) c.Player.PlayerColor = Wc3TextureCache.PlayerColor(value); }
    }

    /// <summary>Live PopcornFX particles across every CORN emitter, for the status bar and scripted checks.</summary>
    public int PopcornParticles => _corns.Sum(c => c.Player.LiveParticles);

    public void Reset()
    {
        _sim.Reset();
        foreach (var c in _corns) c.Player.Reset();
    }

    /// <summary>Adds every emitter's mesh to the scene. Call after the solid geometry is added.</summary>
    public void AddTo(Model3DGroup group)
    {
        foreach (var v in _particleVisuals) group.Children.Add(v.Model);
        foreach (var v in _ribbonVisuals) group.Children.Add(v.Model);
        foreach (var c in _corns)
            foreach (var r in c.Renderers.Values) group.Children.Add(r.Group);
    }

    /// <summary>
    /// Steps the simulation and rebuilds the geometry. <paramref name="right"/> and
    /// <paramref name="up"/> are the camera's axes, used to face the particle quads at the viewer;
    /// <paramref name="cameraPosition"/> is in model units.
    /// </summary>
    public void Update(float dt, MdxAnimator animator, MdxSequence? sequence, int timeMs, long wallMs,
                       Vector3 right, Vector3 up, Vector3 cameraPosition)
    {
        _sim.Update(dt, animator, sequence, timeMs, wallMs);
        BuildParticles(right, up);
        BuildRibbons();
        foreach (var c in _corns)
        {
            c.Player.Update(dt, animator, sequence, timeMs, wallMs, cameraPosition);
            BuildPopcorn(c, right, up);
        }
    }

    // ---- PopcornFX ---------------------------------------------------------------------------

    private readonly List<CornVisual> _corns = [];

    private sealed class CornVisual
    {
        public required PkCornPlayer Player { get; init; }
        public Dictionary<PkRendererDef, RendererVisual> Renderers { get; } = new(ReferenceEqualityComparer.Instance);
    }

    /// <summary>
    /// One PopcornFX renderer. WPF's 3D materials have no per-vertex colour, so its sprites are split
    /// into buckets of similar colour, each a mesh with its own tinted material; a bucket that is not
    /// used this frame keeps its mesh but draws nothing.
    /// </summary>
    private sealed class RendererVisual
    {
        public required PkRendererDef Def { get; init; }
        public BitmapSource? Bitmap { get; init; }
        public Model3DGroup Group { get; } = new();
        public Dictionary<int, Bucket> Buckets { get; } = [];
    }

    private sealed class Bucket
    {
        public required EmitterVisual Visual { get; init; }
        public Vector4 ColourSum;
        public int Count;
    }

    private const int ColourLevels = 6;

    private void BuildPopcorn(CornVisual corn, Vector3 right, Vector3 up)
    {
        var look = Vector3.Cross(up, right);
        foreach (var rv in corn.Renderers.Values)
            foreach (var b in rv.Buckets.Values) { Begin(b.Visual); b.ColourSum = Vector4.Zero; b.Count = 0; }

        foreach (var batch in corn.Player.CollectSprites())
        {
            if (!corn.Renderers.TryGetValue(batch.Renderer, out var rv)) continue;
            var def = rv.Def;
            bool additive = def.Blend is PopcornBlend.Additive or PopcornBlend.AdditiveNoAlpha;
            int cols = Math.Max(1, def.AtlasColumns), rows = Math.Max(1, def.AtlasRows);

            foreach (var s in batch.Sprites)
            {
                // Additive light is colour x alpha, so that product decides the bucket; blended
                // sprites keep colour and alpha apart.
                var c = s.Color;
                Vector4 shade = additive
                    ? new Vector4(Math.Clamp(c.X * c.W, 0, 1), Math.Clamp(c.Y * c.W, 0, 1), Math.Clamp(c.Z * c.W, 0, 1), 1)
                    : new Vector4(Math.Clamp(c.X, 0, 1), Math.Clamp(c.Y, 0, 1), Math.Clamp(c.Z, 0, 1), Math.Clamp(c.W, 0, 1));
                if (additive && shade.X + shade.Y + shade.Z < 0.004f) continue;
                int key = Q(shade.X) * ColourLevels * ColourLevels * ColourLevels + Q(shade.Y) * ColourLevels * ColourLevels + Q(shade.Z) * ColourLevels + (additive ? 0 : Q(shade.W));
                if (!rv.Buckets.TryGetValue(key, out var bucket))
                {
                    bucket = new Bucket { Visual = Build(rv.Bitmap, additive, overBlend: !additive) };
                    rv.Buckets[key] = bucket;
                    rv.Group.Children.Add(bucket.Visual.Model);
                    Begin(bucket.Visual);
                }
                bucket.ColourSum += shade;
                bucket.Count++;

                Vector3 centre = s.Position;
                PkSpriteGeometry.Quad(in s, def.Billboard, right, up, look, out var r, out var u);
                var (u0, v0, u1, v1) = PkSpriteGeometry.CellUv(s.Frame, cols, rows);
                var v = bucket.Visual;
                int bi = v.Positions.Count;
                Add(v.Positions, centre - r - u);
                Add(v.Positions, centre + r - u);
                Add(v.Positions, centre + r + u);
                Add(v.Positions, centre - r + u);
                v.Uvs.Add(new System.Windows.Point(u0, v1));
                v.Uvs.Add(new System.Windows.Point(u1, v1));
                v.Uvs.Add(new System.Windows.Point(u1, v0));
                v.Uvs.Add(new System.Windows.Point(u0, v0));
                v.Indices.Add(bi); v.Indices.Add(bi + 1); v.Indices.Add(bi + 2);
                v.Indices.Add(bi); v.Indices.Add(bi + 2); v.Indices.Add(bi + 3);
            }
        }

        foreach (var rv in corn.Renderers.Values)
        {
            bool additive = rv.Def.Blend is PopcornBlend.Additive or PopcornBlend.AdditiveNoAlpha;
            foreach (var b in rv.Buckets.Values)
            {
                var mean = b.Count == 0 ? Vector4.Zero : b.ColourSum / b.Count;
                End(b.Visual, b.Count == 0 ? 0 : additive ? 1 : mean.W, new Vector3(mean.X, mean.Y, mean.Z));
            }
        }

        static int Q(float x) => Math.Clamp((int)(x * (ColourLevels - 1) + 0.5f), 0, ColourLevels - 1);
    }

    /// <summary>A bake texture path (<c>_HD.w3mod/Textures/FX/Flare/Flare_BW.tif</c>) resolved like the model's own HD textures.</summary>
    private static BitmapSource? PopcornTexture(Wc3TextureCache textures, string cascName, string bakePath)
    {
        if (bakePath.Length == 0) return null;
        var img = textures.Load(cascName, new MdxTexture { ReplaceableId = 0, FileName = PopcornApproximation.TextureRelPath(bakePath), Flags = 0 });
        if (img is null) return null;
        var bmp = BitmapSource.Create(img.Width, img.Height, 96, 96, PixelFormats.Bgra32, null, img.Pixels, img.Width * 4);
        bmp.Freeze();
        return bmp;
    }

    private void BuildParticles(Vector3 right, Vector3 up)
    {
        foreach (var v in _particleVisuals) Begin(v);

        var look = Vector3.Cross(up, right);
        foreach (var p in _sim.Particles)
        {
            if ((uint)p.Emitter >= (uint)_particleVisuals.Count) continue;
            var v = _particleVisuals[p.Emitter];
            var (color, alpha, scale) = _sim.Appearance(p);
            if (alpha <= 0.004f || scale <= 0) continue;

            var (u0, v0, u1, v1) = _sim.CellUv(p);
            float half = scale * 0.5f;
            Vector3 r, u, centre = p.Position;
            switch (_model.ParticleEmitters[p.Emitter].Orientation)
            {
                case MdxParticleOrientation.Ray:
                {
                    // A beam: the card runs from the birth point along its axis, faces the camera
                    // about that axis, and is `scale` wide. A fixed-length beam has its whole
                    // axis from birth; any other ray reaches to wherever the particle is now.
                    float fixedLength = _model.ParticleEmitters[p.Emitter].BeamLength;
                    var axis = fixedLength > 0 ? p.Direction * fixedLength : p.Position - p.Origin;
                    if (axis.LengthSquared() < 1e-6f) axis = Vector3.UnitZ * 0.01f;
                    var side = Vector3.Cross(axis, look);
                    if (side.LengthSquared() < 1e-8f) side = right;
                    r = Vector3.Normalize(side) * half;
                    u = axis * 0.5f;
                    centre = p.Origin + axis * 0.5f;
                    break;
                }
                case MdxParticleOrientation.Ground:
                    r = Vector3.UnitX * half;
                    u = Vector3.UnitY * half;
                    break;
                default:
                    r = right * half;
                    u = up * half;
                    break;
            }

            int b = v.Positions.Count;
            Add(v.Positions, centre - r - u);
            Add(v.Positions, centre + r - u);
            Add(v.Positions, centre + r + u);
            Add(v.Positions, centre - r + u);
            v.Uvs.Add(new System.Windows.Point(u0, v1));
            v.Uvs.Add(new System.Windows.Point(u1, v1));
            v.Uvs.Add(new System.Windows.Point(u1, v0));
            v.Uvs.Add(new System.Windows.Point(u0, v0));
            v.Indices.Add(b); v.Indices.Add(b + 1); v.Indices.Add(b + 2);
            v.Indices.Add(b); v.Indices.Add(b + 2); v.Indices.Add(b + 3);
        }

        // Per-particle colour would need per-vertex colours, which WPF's 3D materials do not carry.
        // The emitter's brush opacity and material colour instead follow the mean of its live
        // particles, so a fading effect still fades and a tinted one is tinted. The tint matters
        // most for PopcornFX layers: their sprites are greyscale (`_BW`) and every bit of colour —
        // Holy Light's gold — comes from the colour curve.
        for (int i = 0; i < _particleVisuals.Count; i++)
        {
            var (colour, alpha) = MeanAppearance(i);
            End(_particleVisuals[i], alpha, colour);
        }

        (Vector3 Colour, float Alpha) MeanAppearance(int emitter)
        {
            float alphaSum = 0; Vector3 colourSum = Vector3.Zero; int n = 0;
            foreach (var p in _sim.Particles)
            {
                if (p.Emitter != emitter) continue;
                var (c, a, _) = _sim.Appearance(p);
                colourSum += c; alphaSum += a; n++;
            }
            return n == 0 ? (Vector3.One, 0) : (colourSum / n, alphaSum / n);
        }
    }

    private void BuildRibbons()
    {
        for (int i = 0; i < _ribbonVisuals.Count && i < _sim.Trails.Count; i++)
        {
            var v = _ribbonVisuals[i];
            var e = _model.RibbonEmitters[i];
            var edges = _sim.Trails[i].Edges;
            Begin(v);

            if (edges.Count >= 2)
            {
                float maxAge = MathF.Max(e.EdgeLifetime, 0.25f);
                for (int k = 0; k < edges.Count; k++)
                {
                    // U runs along the trail so the texture stretches from head to tail.
                    float t = edges.Count == 1 ? 0 : k / (float)(edges.Count - 1);
                    Add(v.Positions, edges[k].Above);
                    Add(v.Positions, edges[k].Below);
                    v.Uvs.Add(new System.Windows.Point(t, 0));
                    v.Uvs.Add(new System.Windows.Point(t, 1));
                }
                for (int k = 0; k + 1 < edges.Count; k++)
                {
                    int b = k * 2;
                    v.Indices.Add(b); v.Indices.Add(b + 1); v.Indices.Add(b + 3);
                    v.Indices.Add(b); v.Indices.Add(b + 3); v.Indices.Add(b + 2);
                }
                _ = maxAge;
            }
            End(v, e.Alpha);
        }
    }

    // ---- geometry plumbing -------------------------------------------------------------------
    // The collections are detached from the mesh while being filled: attached, every Add raises a
    // change notification and WPF re-validates the whole mesh, which dominates the frame cost.

    private static void Begin(EmitterVisual v)
    {
        v.Positions = [];
        v.Uvs = [];
        v.Indices = [];
    }

    private static void End(EmitterVisual v, float opacity, Vector3? tint = null)
    {
        v.Mesh.Positions = v.Positions;
        v.Mesh.TextureCoordinates = v.Uvs;
        v.Mesh.TriangleIndices = v.Indices;
        v.Brush.Opacity = Math.Clamp(opacity, 0, 1);
        if (tint is { } t)
        {
            // A material's Color multiplies its brush, which is exactly the emitter tint x sprite
            // product both games draw. Left unfrozen for this reason.
            var c = Color.FromRgb(Channel(t.X), Channel(t.Y), Channel(t.Z));
            switch (v.Material)
            {
                case EmissiveMaterial em: em.Color = c; break;
                case DiffuseMaterial dm: dm.Color = c; break;
                case MaterialGroup g:
                    foreach (var m in g.Children) if (m is EmissiveMaterial gem) gem.Color = c;
                    break;
            }
        }

        static byte Channel(float x) => (byte)Math.Clamp(x * 255f + 0.5f, 0, 255);
    }

    private static void Add(Point3DCollection c, Vector3 p) => c.Add(new Point3D(p.X, p.Y, p.Z));

    private static EmitterVisual Build(BitmapSource? bitmap, bool emissive, bool overBlend = false)
    {
        // Left unfrozen on purpose: Opacity is written every frame to carry the emitter's fade.
        var brush = bitmap is null
            ? (Brush)new SolidColorBrush(Colors.White)
            : new ImageBrush(bitmap) { ViewportUnits = BrushMappingMode.Absolute, TileMode = TileMode.None };
        brush.Opacity = 0;

        // "Over" blending without a blend state: a black diffuse layer darkens what is behind by the
        // sprite's coverage, then an emissive layer adds the sprite's colour — unlit, like PopcornFX's
        // alpha-blended billboards.
        Material material = overBlend
            ? new MaterialGroup { Children = { new DiffuseMaterial(brush) { Color = Colors.Black, AmbientColor = Colors.Black }, new EmissiveMaterial(brush) } }
            : emissive ? new EmissiveMaterial(brush) : new DiffuseMaterial(brush);
        var mesh = new MeshGeometry3D();
        return new EmitterVisual
        {
            Mesh = mesh,
            Model = new GeometryModel3D(mesh, material) { BackMaterial = material },
            Brush = brush,
            Material = material,
        };
    }

    /// <summary>Emitter blend modes that add light. Blend and Modulate are approximated by it too.</summary>
    private static bool Emissive(MdxParticleBlend blend) => blend != MdxParticleBlend.Modulate2X;

    private static BitmapSource? TextureFor(MdxModel model, Wc3TextureCache textures, string cascName, int textureId)
    {
        if ((uint)textureId >= (uint)model.Textures.Count) return null;
        var img = textures.Load(cascName, model.Textures[textureId]);
        if (img is null) return null;
        var bmp = BitmapSource.Create(img.Width, img.Height, 96, 96, PixelFormats.Bgra32,
                                      null, img.Pixels, img.Width * 4);
        bmp.Freeze();
        return bmp;
    }
}
