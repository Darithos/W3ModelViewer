using System.Numerics;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using Wc3ModelViewer.Core.Casc;
using Wc3ModelViewer.Core.Formats;

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

    public bool HasAnything => _particleVisuals.Count > 0 || _ribbonVisuals.Count > 0;

    public void Reset() => _sim.Reset();

    /// <summary>Adds every emitter's mesh to the scene. Call after the solid geometry is added.</summary>
    public void AddTo(Model3DGroup group)
    {
        foreach (var v in _particleVisuals) group.Children.Add(v.Model);
        foreach (var v in _ribbonVisuals) group.Children.Add(v.Model);
    }

    /// <summary>
    /// Steps the simulation and rebuilds the geometry. <paramref name="right"/> and
    /// <paramref name="up"/> are the camera's axes, used to face the particle quads at the viewer.
    /// </summary>
    public void Update(float dt, MdxAnimator animator, MdxSequence? sequence, int timeMs, long wallMs,
                       Vector3 right, Vector3 up)
    {
        _sim.Update(dt, animator, sequence, timeMs, wallMs);
        BuildParticles(right, up);
        BuildRibbons();
    }

    private void BuildParticles(Vector3 right, Vector3 up)
    {
        foreach (var v in _particleVisuals) Begin(v);

        foreach (var p in _sim.Particles)
        {
            if ((uint)p.Emitter >= (uint)_particleVisuals.Count) continue;
            var v = _particleVisuals[p.Emitter];
            var (color, alpha, scale) = _sim.Appearance(p);
            if (alpha <= 0.004f || scale <= 0) continue;

            var (u0, v0, u1, v1) = _sim.CellUv(p);
            float half = scale * 0.5f;
            var r = right * half;
            var u = up * half;

            int b = v.Positions.Count;
            Add(v.Positions, p.Position - r - u);
            Add(v.Positions, p.Position + r - u);
            Add(v.Positions, p.Position + r + u);
            Add(v.Positions, p.Position - r + u);
            v.Uvs.Add(new System.Windows.Point(u0, v1));
            v.Uvs.Add(new System.Windows.Point(u1, v1));
            v.Uvs.Add(new System.Windows.Point(u1, v0));
            v.Uvs.Add(new System.Windows.Point(u0, v0));
            v.Indices.Add(b); v.Indices.Add(b + 1); v.Indices.Add(b + 2);
            v.Indices.Add(b); v.Indices.Add(b + 2); v.Indices.Add(b + 3);
        }

        // Per-particle colour would need per-vertex colours, which WPF's 3D materials do not carry.
        // The emitter's brush opacity instead follows the mean of its live particles, so a fading
        // effect still fades. Colour tinting is left to the sprite itself.
        for (int i = 0; i < _particleVisuals.Count; i++)
            End(_particleVisuals[i], MeanAlpha(i));

        float MeanAlpha(int emitter)
        {
            float sum = 0; int n = 0;
            foreach (var p in _sim.Particles)
            {
                if (p.Emitter != emitter) continue;
                sum += _sim.Appearance(p).Alpha; n++;
            }
            return n == 0 ? 0 : sum / n;
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

    private static void End(EmitterVisual v, float opacity)
    {
        v.Mesh.Positions = v.Positions;
        v.Mesh.TextureCoordinates = v.Uvs;
        v.Mesh.TriangleIndices = v.Indices;
        v.Brush.Opacity = Math.Clamp(opacity, 0, 1);
    }

    private static void Add(Point3DCollection c, Vector3 p) => c.Add(new Point3D(p.X, p.Y, p.Z));

    private static EmitterVisual Build(BitmapSource? bitmap, bool emissive)
    {
        // Left unfrozen on purpose: Opacity is written every frame to carry the emitter's fade.
        var brush = bitmap is null
            ? (Brush)new SolidColorBrush(Colors.White)
            : new ImageBrush(bitmap) { ViewportUnits = BrushMappingMode.Absolute, TileMode = TileMode.None };
        brush.Opacity = 0;

        Material material = emissive ? new EmissiveMaterial(brush) : new DiffuseMaterial(brush);
        var mesh = new MeshGeometry3D();
        return new EmitterVisual
        {
            Mesh = mesh,
            Model = new GeometryModel3D(mesh, material) { BackMaterial = material },
            Brush = brush,
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
