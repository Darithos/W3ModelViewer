using System.Numerics;
using System.Text;
using Wc3ModelViewer.Core.Formats;

namespace Wc3ModelViewer.Core.Convert;

/// <summary>Diagnostic selection of baked bone-track data for <see cref="M3ExportOptions.AnimData"/>.</summary>
public enum M3AnimTestData
{
    Full,
    /// <summary>No bone tracks at all — the model holds its bind pose through every sequence.</summary>
    None,
    /// <summary>Location tracks only (rotation and scale stripped).</summary>
    LocationsOnly,
    /// <summary>Rotation tracks only (location and scale stripped).</summary>
    RotationsOnly,
}

/// <summary>What to export and how.</summary>
public sealed class M3ExportOptions
{
    /// <summary>Names of the sequences to include, or null for all of them.</summary>
    public HashSet<string>? Sequences { get; init; }

    /// <summary>
    /// Per-sequence export names (WC3 name → m3 name). A sequence without an entry falls back to
    /// the automatic SC2 mapping.
    /// </summary>
    public IReadOnlyDictionary<string, string>? SequenceNames { get; init; }

    /// <summary>Geoset indices to export; null = every geoset at <see cref="Lod"/>.</summary>
    public HashSet<int>? Geosets { get; init; }

    /// <summary>Which LOD's geosets to export. The guide recommends LOD 1 for units.</summary>
    public int Lod { get; init; }

    /// <summary>Uniform scale. 1.0 keeps native WC3 units — what Renee's war3mod expects.</summary>
    public float Scale { get; init; } = 1f;

    /// <summary>
    /// Player slot for previews and for the glTF export. An <c>.m3</c> ignores it: StarCraft II
    /// supplies the player colour itself through the material's team channel, so the same exported
    /// file is correct for all eight players.
    /// </summary>
    public int TeamColor { get; init; }

    /// <summary>Sample rate for baking animation keys.</summary>
    public int Fps { get; init; } = 30;

    /// <summary>Export geoset-visibility animation (GEOA) as layer colour tracks.</summary>
    public bool GeosetVisibility { get; init; } = true;

    /// <summary>Convert Reforged PBR sets to SC2 diffuse/specular/normal maps.</summary>
    public bool ConvertPbr { get; init; } = true;

    /// <summary>
    /// Export PRE2 particle emitters as StarCraft II particle systems. Models with no emitters are
    /// unaffected either way. Reforged's PopcornFX (CORN) emitters can never be exported — they
    /// reference external baked effect files — and are reported as dropped regardless.
    /// </summary>
    public bool ExportEffects { get; init; } = true;

    /// <summary>
    /// Path prefix baked into the .m3's texture references. SC2 resolves these against the mod or
    /// map archive ROOT; any root-relative path works, the folder name carries no meaning.
    /// <para>
    /// References are baked as <c>Assets/textures/&lt;ModelName&gt;/*.dds</c>, and the export writes
    /// a real <c>Assets\</c> folder on disk holding exactly that layout, so the whole package is
    /// one folder to merge into a map or mod root. An earlier layout wrote the .m3 and
    /// <c>textures\</c> loose and relied on the user pasting the folder's *contents* into
    /// <c>Assets\</c>; splitting a two-part copy is easy to get half-right, and a textures folder
    /// that lands beside <c>Assets\</c> instead of inside it leaves every reference dangling —
    /// which SC2 shows as an untextured model rather than an error. The per-model subfolder keeps
    /// several imported units from colliding on a texture filename.
    /// </para>
    /// </summary>
    public string TexturePrefix
    {
        get => _texturePrefix ?? $"Assets/textures/{FolderSafe(ModelName)}/";
        init => _texturePrefix = value;
    }
    private readonly string? _texturePrefix;

    /// <summary>
    /// Where to write the .dds files, relative to the exported .m3 — <see cref="TexturePrefix"/>
    /// minus the archive-side <c>Assets/</c> head, because the .m3 is itself written inside the
    /// export's <c>Assets\</c> folder. Always use this (never <see cref="TexturePrefix"/>) when
    /// deciding where to write the .dds files.
    /// </summary>
    public string TextureFolder
    {
        get
        {
            string p = TexturePrefix.Replace('\\', '/').TrimEnd('/');
            const string head = "Assets/";
            if (p.StartsWith(head, StringComparison.OrdinalIgnoreCase)) p = p[head.Length..];
            return p.Replace('/', Path.DirectorySeparatorChar);
        }
    }

    /// <summary>Model name reduced to a folder/file-safe token (invalid chars → underscore).</summary>
    internal static string FolderSafe(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (char c in name) sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        string s = sb.ToString().Trim();
        return s.Length > 0 ? s : "model";
    }

    /// <summary>
    /// Diagnostic switch: which baked bone tracks reach the file. Isolates which piece of the
    /// synthesized animation data SC2 disagrees with (sequences and events are kept either way).
    /// </summary>
    public M3AnimTestData AnimData { get; init; } = M3AnimTestData.Full;

    public string ModelName { get; init; } = "Model";
}

/// <summary>A texture file the export produced alongside the .m3.</summary>
public sealed record ExportedTexture(string FileName, byte[] Data);

public sealed class M3ExportResult
{
    public required byte[] M3 { get; init; }
    public required List<ExportedTexture> Textures { get; init; }
    public required List<string> Log { get; init; }

    /// <summary>Texture paths parsed back out of <see cref="M3"/> — see <see cref="M3TextureAudit"/>.</summary>
    public List<string> TextureReferences { get; init; } = [];
}

/// <summary>
/// Writes a StarCraft II MD34 <c>.m3</c> from a parsed Warcraft III model. The container mechanics
/// (section order, reference patching, <c>len % 16</c> 0xAA padding) reproduce the sibling Diablo III
/// exporter, whose output the SC2 editor accepts; what differs is everything Warcraft-specific.
/// </summary>
/// <remarks>
/// The animation mapping rests on one identity. Warcraft III node worlds are identity at rest
/// (vertices live in model space; keys are offsets about the node's pivot), while m3 skins through
/// explicit rest poses and inverse-bind matrices. Putting the m3 rest world at the pivot —
/// <c>W_m3 = T(pivot) · W_wc3</c>, so <c>IREF = T(-pivot)</c> and rest location = pivot −
/// parentPivot — makes both systems produce identical skinned vertices. Baked per-frame locals are
/// then <c>L(t) = T(pivot_c) · W_c(t) · W_p(t)⁻¹ · T(-pivot_p)</c>, evaluated through
/// <see cref="MdxAnimator"/> so hermite/bezier curves, global sequences and inheritance flags all
/// collapse into plain linear keys the m3 format can hold.
/// <para>
/// UVs are written <c>v * 2048</c> with no flip: MDX V is already top-down, unlike the sibling
/// exporter's bottom-up Diablo input — reusing its <c>(1-v)</c> would mirror every texture.
/// </para>
/// </remarks>
public sealed class M3Exporter
{
    private const uint VertexFlags = 0x0182007D;    // pos + skin + packed normal + uv0 + packed tangent
    private const uint ModelFlags = 0x00180D53;
    private const uint BndsAnimId = 0x001F9BD2;
    private const uint EvntAnimId = 0x65BD3215;

    private readonly MdxModel _mdx;
    private readonly MdxAnimator _animator;
    private readonly M3ExportOptions _opt;
    private readonly List<string> _log = [];

    public M3Exporter(MdxModel mdx, M3ExportOptions options)
    {
        _mdx = mdx;
        _opt = options;
        _animator = new MdxAnimator(mdx);
    }

    // ---------------------------------------------------------------- assembly model

    private sealed class ExportBone
    {
        public required string Name;
        public int Parent = -1;                     // export-bone index
        public Vector3 RestLocation;                // parent-local
        public Vector3 PivotWorld;                  // for IREF
        public Quaternion RestRotation = Quaternion.Identity;
        public int MdxNodeIndex = -1;               // -1 for synthesized bones
    }

    private sealed class ExportRegion
    {
        public required MdxGeoset Geoset;
        public required CompositeMaterial Material;
        public int MaterialIndex;                   // into _materials
        public float StaticAlpha = 1f;              // GEOA static value
        public MdxGeosetAnim? Anim;
    }

    private sealed class ExportMaterial
    {
        public required string Name;
        public required CompositeBlend Blend;
        public bool TwoSided;
        public bool Unshaded;
        public int Priority;
        public byte DefaultAlpha = 255;             // visibility when a sequence has no key
        public string DiffusePath = "";             // export file names
        public string NormalPath = "";
        public string SpecularPath = "";
        public string EmissivePath = "";
        public string TeamPath = "";                // bitmap whose alpha scales the live player colour
        public int TeamBlendSlot;                   // 4 = emis1, 5 = emis2, 0 = no player colour
        public MdxGeosetAnim? VisibilityAnim;       // GEOA feeding this material's colour track
        public uint ColorAnimId;                    // LAYR color_value anim id, filled during write
        public bool IsParticle;                     // built for an emitter's sprite, not a geoset
        public int FlipbookCells = 1;               // sprite-sheet cells the emitter walks through
        public int ToggleBone = -1;                 // BAT_.bone whose batching flag hides this draw call
        public uint BatchAnimId;                    // SDFG rides this id
        public bool RestVisible = true;             // batching flag with no sequence driving it
    }

    private sealed class SeqDef
    {
        public required string Name;
        public required MdxSequence Source;
        public int EndMs;
        public List<(uint Id, int[] FramesMs, Vector3[] Vals)> Vec3Tracks = [];
        public List<(uint Id, int[] FramesMs, Quaternion[] Vals)> QuatTracks = [];
        public List<(uint Id, int[] FramesMs, uint[] Vals)> ColorTracks = [];   // BGRA for SDCC
        public List<(uint Id, int[] FramesMs, float[] Vals)> FloatTracks = [];  // REAL for SDR3
        public List<(uint Id, int[] FramesMs, uint[] Vals)> FlagTracks = [];    // FLAG for SDFG
        public bool Animated => Vec3Tracks.Count > 0 || QuatTracks.Count > 0
                                || ColorTracks.Count > 0 || FloatTracks.Count > 0
                                || FlagTracks.Count > 0;
    }

    private readonly List<ExportBone> _bones = [];
    private readonly List<ExportRegion> _regions = [];
    private readonly List<ExportMaterial> _materials = [];
    private readonly List<(string Name, int Bone)> _attachments = [];
    private readonly List<(MdxCamera Camera, int Bone)> _cameras = [];
    private readonly List<(int Bone, byte Type)> _billboards = [];
    private readonly List<ExportedTexture> _textures = [];

    // ---------------------------------------------------------------- public entry

    public M3ExportResult Export(Casc.Wc3TextureCache textureCache, string modelCascName)
    {
        BuildBones();
        BuildBillboards();
        BuildRegions(textureCache, modelCascName);
        BuildAttachments();
        BuildCameras();
        BuildBatchToggles();
        var sequences = BuildSequences();

        if (_regions.Count == 0)
            throw new InvalidOperationException($"No geosets at LOD {_opt.Lod} — nothing to export.");

        var m3 = WriteM3(sequences);

        // Read the texture paths back out of the bytes just written and confirm each one names a
        // file this export actually produced. StarCraft II renders an unresolved layer black
        // instead of complaining, so a mismatch here surfaces much later as a shading bug.
        var refs = M3TextureAudit.ReferencedPaths(m3);
        var produced = _textures.Select(t => t.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var orphans = refs.Where(p => !produced.Contains(Path.GetFileName(p))).ToList();
        _log.Add(orphans.Count == 0
            ? $"{refs.Count} texture references, all produced by this export"
            : $"{orphans.Count} texture reference(s) name no exported file: {string.Join(", ", orphans)}");

        ReportTextureSources(textureCache, modelCascName);

        return new M3ExportResult { M3 = m3, Textures = _textures, Log = _log, TextureReferences = refs };
    }

    /// <summary>
    /// Says where the packaged textures came from. This matters most for a custom model opened off
    /// disk: it usually ships only its own art and leaves the stock references to the game install,
    /// so "12 textures taken from the game install" is the difference between a complete package and
    /// one full of magenta placeholders the user only discovers in the StarCraft II editor.
    /// </summary>
    private void ReportTextureSources(Casc.Wc3TextureCache cache, string modelCascName)
    {
        var p = cache.ProvenanceOf(_mdx, modelCascName, _opt.TeamColor);
        var byName = p.MatchedByName.ToList();

        var parts = new List<string>();
        if (p.BesideModel > 0) parts.Add($"{p.BesideModel} shipped with the model");
        if (p.FromGameInstall > 0) parts.Add($"{p.FromGameInstall} from the game install");
        if (parts.Count > 0)
            // Files written and references resolved are different numbers and both matter: a
            // flipbook resolves fifty frames but exports one, so reporting either alone reads as a
            // miscount. Say both.
            _log.Add($"{_textures.Count} texture file(s) written, from {p.Found.Count} resolved "
                     + $"reference(s): {string.Join(", ", parts)}");

        // Worth saying out loud: it is the difference between a unit that recolours per player in
        // the editor and one that is permanently red, and nothing else in the export shows it.
        int teamed = _materials.Count(m => m.TeamPath.Length > 0);
        if (teamed > 0)
            _log.Add($"{teamed} material(s) take their player colour live from StarCraft II "
                     + "(Team Color Emissive Add) instead of having it painted in");

        // A name-only match found real pixels but may have found the wrong file — texture names are
        // not unique across the archive. Name them so a wrong guess is checkable rather than silent,
        // and say how many files the name could have meant.
        foreach (var o in byName)
            _log.Add($"   {o.Reference} is not in the game install at that path; used {o.From}"
                     + (o.IsAmbiguous
                         ? $" — {o.Alternatives} files share that name, so check this one"
                         : " (the only file with that name)"));

        if (p.Missing.Count > 0)
            _log.Add($"{p.Missing.Count} texture(s) could not be found beside the model or in the game "
                     + $"install and export as magenta placeholders: {string.Join(", ", p.Missing.Take(8))}"
                     + (p.Missing.Count > 8 ? $" (+{p.Missing.Count - 8} more)" : ""));
    }

    // ---------------------------------------------------------------- bones

    private void BuildBones()
    {
        // Every MDX node becomes an m3 bone at its pivot; index = node index. Cameras append later.
        var nodes = _mdx.Nodes;
        var byObjectId = new Dictionary<int, int>();
        for (int i = 0; i < nodes.Count; i++) byObjectId.TryAdd(nodes[i].ObjectId, i);

        for (int i = 0; i < nodes.Count; i++)
        {
            var n = nodes[i];
            int parent = n.ParentId >= 0 && byObjectId.TryGetValue(n.ParentId, out int pi) ? pi : -1;
            var parentPivot = parent >= 0 ? nodes[parent].Pivot : Vector3.Zero;
            _bones.Add(new ExportBone
            {
                Name = UniqueBoneName(n.Name, i),
                Parent = parent,
                RestLocation = ToSc2(n.Pivot - parentPivot) * _opt.Scale,
                PivotWorld = ToSc2(n.Pivot) * _opt.Scale,
                MdxNodeIndex = i,
            });
        }
    }

    /// <summary>
    /// Carries Warcraft III's billboard flags over as BBSC entries, so a card that turns to face the
    /// camera in Warcraft III — the priest's staff orb, most spell glows — still does in SC2.
    /// </summary>
    /// <remarks>
    /// Without this the card is frozen in its rest pose: a flat picture of a ball that goes edge-on
    /// as the camera moves. SC2 does not billboard through a bone flag but through the model's BBSC
    /// list, which this exporter never wrote.
    /// <para>
    /// No basis correction is needed, and that is measured, not assumed. Of the flat cards on fully
    /// billboarded nodes, 3,059 of 3,073 in SD and 308 of 308 in HD lie on node-local X, and the
    /// dominant texture layout (2,604 SD, 282 HD; the rest are the same plane with the art rolled)
    /// is right = +Y, up = +Z, facing +X. Blizzard's type 6 cards with identity up/forward read
    /// right = +X, up = +Z, facing -Y in 331 of 346 cases. The facing
    /// turn in <see cref="ToSc2(Vector3)"/> maps the first onto the second exactly, and bone rest
    /// rotations stay identity, so identity up/forward quaternions are correct.
    /// </para>
    /// <para>
    /// Lock Z maps to type 2, the axis-locked billboard Blizzard uses on 854 bones, with the same
    /// handedness on its upright cards and names like <c>WorldZ</c> and <c>Dummy_Vertical</c>.
    /// Lock X and Lock Y (96 SD nodes, mostly arrow missiles) have no measured SC2 equivalent and
    /// are left unbillboarded rather than guessed.
    /// </para>
    /// </remarks>
    private void BuildBillboards()
    {
        int unmapped = 0;
        for (int i = 0; i < _mdx.Nodes.Count; i++)
        {
            var flags = _mdx.Nodes[i].Flags;
            // Warcraft III honours one mode per node, full billboard first.
            if ((flags & MdxNodeFlags.Billboarded) != 0) _billboards.Add((i, BillboardFull));
            else if ((flags & MdxNodeFlags.BillboardLockZ) != 0) _billboards.Add((i, BillboardWorldZ));
            else if ((flags & (MdxNodeFlags.BillboardLockX | MdxNodeFlags.BillboardLockY)) != 0) unmapped++;
        }
        if (_billboards.Count > 0)
            _log.Add($"{_billboards.Count} billboarded bone(s) face the camera in SC2: "
                     + string.Join(", ", _billboards.Take(6).Select(bb => _bones[bb.Bone].Name))
                     + (_billboards.Count > 6 ? $" (+{_billboards.Count - 6} more)" : ""));
        if (unmapped > 0)
            _log.Add($"{unmapped} bone(s) billboard around their X or Y axis, which has no SC2 equivalent "
                     + "— they keep their rest orientation");
    }

    private const byte BillboardWorldZ = 2;
    private const byte BillboardFull = 6;

    private readonly HashSet<string> _usedBoneNames = new(StringComparer.OrdinalIgnoreCase);

    private string UniqueBoneName(string name, int index)
    {
        string candidate = name.Length > 0 ? name : $"Node{index:000}";
        while (!_usedBoneNames.Add(candidate)) candidate += "_";
        return candidate;
    }

    // ---------------------------------------------------------------- regions & materials

    private void BuildRegions(Casc.Wc3TextureCache textures, string modelCascName)
    {
        foreach (var g in _mdx.Geosets)
        {
            if (g.LodId != _opt.Lod || g.VertexCount == 0 || g.Indices.Length < 3) continue;
            if (_opt.Geosets is not null && !_opt.Geosets.Contains(g.Index)) continue;
            if ((uint)g.MaterialId >= (uint)_mdx.Materials.Count) continue;

            var anim = _mdx.GeosetAnims.FirstOrDefault(a => a.GeosetId == g.Index);

            // Fully invisible geosets (static GEOA alpha 0, no track) carry corpses and alternate
            // forms; exporting them visible is the classic "unit holds two swords" bug.
            float staticAlpha = anim?.AlphaTrack is null ? anim?.Alpha ?? 1f : 1f;

            var mdxMat = _mdx.Materials[g.MaterialId];
            var composite = MaterialCompositor.Compose(_mdx, mdxMat, textures, modelCascName, _opt.TeamColor,
                                                       bakeTeam: false);

            // Reforged marks every HD layer FilterMode.Transparent and shares one atlas between
            // solid body parts and cut-out cards, so the material alone cannot say whether *this*
            // geoset is a cutout. Measure the texels its UVs actually reach: the grunt's torso and
            // his loincloth straps use the same material, and the torso reaches none of the
            // transparent 5%. Calling it a cutout anyway costs real geometry — see WriteM3.
            if (composite.Blend == CompositeBlend.AlphaTest)
            {
                float cut = MaterialCompositor.CutoutCoverage(composite.Texture, g);
                if (cut < CutoutCoverageMin) composite = composite.WithBlend(CompositeBlend.Opaque);
                _cutoutCoverage[g.Index] = cut;
            }

            int matIndex = GetOrAddMaterial(g, mdxMat, composite, textures, modelCascName, anim);

            _regions.Add(new ExportRegion
            {
                Geoset = g, Material = composite, MaterialIndex = matIndex,
                StaticAlpha = staticAlpha, Anim = anim,
            });
        }
        _log.Add($"{_regions.Count} geosets at LOD {_opt.Lod}" +
                 (_regions.Count(r => r.StaticAlpha < 0.01f) is var hidden && hidden > 0
                     ? $" ({hidden} statically hidden, kept hidden via visibility tracks)" : ""));

        int cutouts = _regions.Count(r => r.Material.Blend == CompositeBlend.AlphaTest);
        if (_cutoutCoverage.Count > 0)
            _log.Add($"{cutouts} of {_cutoutCoverage.Count} transparent-flagged geosets are real cutouts " +
                     $"(the rest sample no transparent texels and stay opaque so they keep writing depth)");

        BuildEffects(textures, modelCascName);
    }

    /// <summary>
    /// Resolves each particle emitter to a material and a bone, and reports what cannot come across.
    /// A particle system in StarCraft II draws through a standard material like any mesh does, so an
    /// emitter needs one built from its sprite before it can be written.
    /// </summary>
    private void BuildEffects(Casc.Wc3TextureCache textures, string modelCascName)
    {
        // Reforged's PopcornFX emitters point at .pkb bakes that do ship in the archive, but their
        // per-particle behaviour is compiled bytecode with no mapping onto PAR_'s fixed fields. Not
        // yet converted, so say so rather than let a third of Warcraft III's effect models silently
        // lose their effects.
        if (_mdx.PopcornEmitterCount > 0)
            _log.Add($"{_mdx.PopcornEmitterCount} PopcornFX (CORN) emitter(s) dropped — Reforged's "
                     + "own effect system, not yet converted");

        if (!_opt.ExportEffects)
        {
            if (_mdx.ParticleEmitters.Count > 0)
                _log.Add($"{_mdx.ParticleEmitters.Count} particle emitter(s) skipped (effects export is off)");
            return;
        }

        int skipped = 0;
        foreach (var e in _mdx.ParticleEmitters)
        {
            // A PREM emitter spawns models rather than sprites and parses with no texture; there is
            // nothing to draw, so it is dropped rather than exported as an invisible system.
            // A team-glow emitter has no sprite file — its art *is* the player's colour, shaped by
            // a falloff. Those used to be dropped as "no usable sprite"; now the falloff becomes the
            // player-colour mask and the sprite itself is black, so StarCraft II supplies the colour.
            bool glow = e.ReplaceableId == 2
                        || ((uint)e.TextureId < (uint)_mdx.Textures.Count && _mdx.Textures[e.TextureId].IsTeamGlow);

            if (!glow && (uint)e.TextureId >= (uint)_mdx.Textures.Count) { skipped++; continue; }
            var tex = glow ? new MdxTexture { ReplaceableId = 2, FileName = "" } : _mdx.Textures[e.TextureId];
            if (!glow && (tex.IsReplaceable || tex.FileName.Length == 0)) { skipped++; continue; }

            // Glow art is read at player 0 deliberately, whatever slot the preview is on: the mask
            // is the falloff recovered as the art's brightest channel, and only player 0's colour
            // has a 255 channel to divide out. Player 4's purple would hand back a mask barely half
            // as bright, and the export would carry that dimming forever.
            var image = textures.Load(modelCascName, tex, glow ? 0 : _opt.TeamColor);
            if (image is null) { skipped++; continue; }

            var glowMask = glow ? MaterialCompositor.GlowMask(image) : null;
            if (glow && glowMask is null) { skipped++; continue; }

            _emitters.Add(new ExportEmitter
            {
                Source = e,
                MaterialIndex = AddParticleMaterial(e, glowMask is null ? image : BlackSprite, glowMask),
                NodeIndex = e.NodeIndex,
                RestEmissionRate = RestEmissionRateOf(e),
            });
        }

        if (_emitters.Count > 0)
            _log.Add($"{_emitters.Count} particle emitter(s) exported as SC2 particle systems"
                     + (skipped > 0 ? $" ({skipped} skipped: no usable sprite)" : ""));
        else if (skipped > 0)
            _log.Add($"{skipped} particle emitter(s) dropped — no usable sprite texture");
    }

    /// <summary>
    /// A material for one emitter's sprite. Particles are never lit and never cut out: the sheet's
    /// alpha is the shape of the flame, so the material is unshaded and blended by the emitter's own
    /// blend mode rather than run through the geoset cutout machinery.
    /// </summary>
    /// <summary>A sprite that contributes nothing itself — the player-colour channel draws it.</summary>
    private static readonly RgbaImage BlackSprite = RgbaImage.Solid(8, 8, 0, 0, 0);

    private int AddParticleMaterial(MdxParticleEmitter2 e, RgbaImage image, TeamMask? teamMask)
    {
        var blend = e.Blend switch
        {
            MdxParticleBlend.Add or MdxParticleBlend.AlphaKey => CompositeBlend.Additive,
            MdxParticleBlend.Modulate or MdxParticleBlend.Modulate2X => CompositeBlend.AlphaBlend,
            _ => CompositeBlend.AlphaBlend,
        };
        string stem = TexStem((uint)e.TextureId < (uint)_mdx.Textures.Count
                              ? _mdx.Textures[e.TextureId].FileName : "", _materials.Count);

        for (int i = 0; i < _materials.Count; i++)
            if (_materials[i].IsParticle && _materials[i].DiffusePath == stem + "_diff.dds"
                && _materials[i].Blend == blend
                && _materials[i].FlipbookCells == Math.Max(e.Rows, 1) * Math.Max(e.Columns, 1))
                return i;

        var mat = new ExportMaterial
        {
            Name = stem,
            Blend = blend,
            TwoSided = true,          // a billboard is seen from either side
            Unshaded = true,
            IsParticle = true,
            FlipbookCells = Math.Max(e.Rows, 1) * Math.Max(e.Columns, 1),
            DiffusePath = AddTexture(stem + "_diff.dds", image),
        };
        if (teamMask is not null)
            mat.TeamPath = AddTexture(stem + "_team.dds", TeamMaskTexture(teamMask, null));
        _materials.Add(mat);
        return _materials.Count - 1;
    }

    /// <summary>
    /// The emission rate SC2 falls back to when nothing drives the track — sampled inside the
    /// primary Stand sequence, the same rule the geoset visibility default follows. A death-only
    /// emitter reads 0 here, so an unposed preview does not show the model mid-death.
    /// </summary>
    private float RestEmissionRateOf(MdxParticleEmitter2 e)
    {
        var primary = _mdx.Sequences.FirstOrDefault(s => s.Name.StartsWith("Stand", StringComparison.OrdinalIgnoreCase))
                      ?? _mdx.Sequences.FirstOrDefault();
        if (primary is null) return MathF.Max(e.EmissionRate, 0);
        return EmitterVisible(e, primary, primary.IntervalStart)
            ? MathF.Max(_animator.SampleFloat(e.EmissionRateTrack, primary, primary.IntervalStart, e.EmissionRate), 0)
            : 0;
    }

    /// <summary>One emitter that survived to the write stage, with everything it needs resolved.</summary>
    private sealed class ExportEmitter
    {
        public required MdxParticleEmitter2 Source;
        public required int MaterialIndex;
        public required int NodeIndex;
        public uint EmitRateAnimId;                 // PAR_.emit_rate; the KP2V gate rides this id
        public float RestEmissionRate;              // rate with no sequence driving it
    }

    private readonly List<ExportEmitter> _emitters = [];

    /// <summary>
    /// Below this share of transparent texels under a geoset's own UVs, a "transparent" Reforged
    /// material is really solid. Deliberately small: one genuinely cut-out strap is worth an
    /// alpha test, but stray transparent texels in an atlas corner are not.
    /// </summary>
    private const float CutoutCoverageMin = 0.005f;

    private readonly Dictionary<int, float> _cutoutCoverage = [];

    private int GetOrAddMaterial(MdxGeoset g, MdxMaterial mdxMat, CompositeMaterial composite,
                                 Casc.Wc3TextureCache textures, string modelCascName, MdxGeosetAnim? anim)
    {
        // Only a geoset that actually disappears somewhere counts as visibility-driven. Reforged
        // gives nearly every geoset a GEOA alpha track that never leaves 1.0; treating those as
        // animated would force the whole model into alpha blending for no reason.
        bool everHides = anim is not null
                         && (anim.AlphaTrack is not null
                             ? anim.AlphaTrack.Values.Any(v => v < 0.99f)
                             : anim.Alpha < 0.999f);
        bool animated = _opt.GeosetVisibility && everHides;

        string stem = TexStem(composite.PrimaryTexturePath, g.Index);

        // Materials can be shared, but a GEOA-animated geoset needs its own copy: visibility rides
        // the material's colour track, and sharing it would blink unrelated geosets.
        if (!animated)
        {
            for (int i = 0; i < _materials.Count; i++)
                if (_materials[i].VisibilityAnim is null && _materials[i].DiffusePath == stem + "_diff.dds"
                    && _materials[i].Blend == composite.Blend && _materials[i].TwoSided == composite.TwoSided)
                    return i;
        }

        // A geoset hidden at rest (corpse, alternate form) must default to invisible: SC2 falls
        // back to the layer's colour_value default in any sequence that carries no key, and its
        // Archive-Browser preview shows the model *unposed*, on that very default. Sampling the
        // track with a null sequence returns the track's default of 1 — even for a corpse, which
        // then draws its rotting mesh straight over the living body. Sample inside the primary
        // Stand sequence instead, the same value the baked track carries: corpse 0, body 1.
        var primarySeq = _mdx.Sequences.FirstOrDefault(s => s.Name.StartsWith("Stand", StringComparison.OrdinalIgnoreCase))
                         ?? _mdx.Sequences.FirstOrDefault();
        float restAlpha = anim is null ? 1f
            : anim.AlphaTrack is null ? anim.Alpha
            : primarySeq is not null ? SampleGeosetAlpha(anim, primarySeq, primarySeq.IntervalStart)
            : 1f;

        var mat = new ExportMaterial
        {
            Name = animated ? $"{stem}_g{g.Index}" : stem,
            Blend = composite.Blend,
            TwoSided = composite.TwoSided,
            Unshaded = composite.Unshaded,
            Priority = mdxMat.PriorityPlane,
            VisibilityAnim = animated ? anim : null,
            DefaultAlpha = animated ? (byte)Math.Clamp(restAlpha * 255f + 0.5f, 0, 255) : (byte)255,
        };

        // Player colour is NOT painted into the art. StarCraft II carries it live, the way every
        // Blizzard unit from the marine up does: a material layer sampling one channel, with that
        // layer's MAT_ blend mode set to 4, "Team Color Emissive Add". Compose was asked not to
        // bake it (it composited over black instead), so the diffuse below already holds exactly
        // the part of the surface that is not the player's, and this mask holds the rest.
        var teamMask = MaterialCompositor.TeamMaskOf(_mdx, mdxMat, textures, modelCascName);
        if (teamMask is not null && teamMask.Coverage < TeamCoverageMin) teamMask = null;

        // Texture set. HD layers with PBR maps get the full conversion; everything else exports the
        // composited diffuse (layers already flattened).
        var hdLayer = mdxMat.Layers.FirstOrDefault(l => l.IsPbr);
        if (_opt.ConvertPbr && hdLayer is not null)
        {
            var diffuse = LoadSlot(textures, modelCascName, hdLayer, MdxTextureSlot.Diffuse);
            var normal = LoadSlot(textures, modelCascName, hdLayer, MdxTextureSlot.Normal);
            var orm = LoadSlot(textures, modelCascName, hdLayer, MdxTextureSlot.Orm);
            var emissive = LoadSlot(textures, modelCascName, hdLayer, MdxTextureSlot.Emissive);

            if (diffuse is not null)
            {
                // Compose's output is the albedo whenever it differs from the raw diffuse in a way
                // that matters: it is opaque-ified for a solid material, and — the reason a
                // cut-out material has to take it too — it has had the player's contribution
                // subtracted. Handing the raw diffuse to a masked material would let SC2 add the
                // player colour on top of art that still contains it.
                var set = PbrConverter.Convert(
                    composite.Blend == CompositeBlend.Opaque || teamMask is not null ? composite.Texture : diffuse,
                    normal, orm, emissive);

                mat.DiffusePath = AddTexture(stem + "_diff.dds", set.Diffuse);
                mat.SpecularPath = AddTexture(stem + "_spec.dds", set.Specular);
                if (set.Normal is not null) mat.NormalPath = AddTexture(stem + "_norm.dds", set.Normal);
                if (set.Emissive is not null) mat.EmissivePath = AddTexture(stem + "_emis.dds", set.Emissive);
            }
        }
        if (mat.DiffusePath.Length == 0)
            mat.DiffusePath = AddTexture(stem + "_diff.dds", composite.Texture);

        // The mask gets a file of its own rather than riding the diffuse's spare alpha. Sharing was
        // tried and it aliases: the SD footman's four materials all resolve to one `footman_diff`,
        // and three of them are cutouts whose alpha1 layer reads that same alpha as coverage. One
        // channel cannot be both a cutout and a team mask, and whichever material was written first
        // would have silently decided which.
        if (teamMask is not null)
        {
            var hdDiffuse = _opt.ConvertPbr && hdLayer is not null
                ? LoadSlot(textures, modelCascName, hdLayer, MdxTextureSlot.Diffuse) : null;
            mat.TeamPath = AddTexture(stem + "_team.dds", TeamMaskTexture(teamMask, hdDiffuse));
        }

        _materials.Add(mat);
        return _materials.Count - 1;
    }

    /// <summary>A mask below this share of the texture is noise, not art, and buys only a texture.</summary>
    private const float TeamCoverageMin = 0.0005f;

    /// <summary>
    /// The mask as a bitmap StarCraft II can sample: black, with the scalar in the alpha channel.
    /// </summary>
    /// <param name="tintSource">
    /// The Reforged diffuse, when this is HD art. Reforged <i>tints</i> the masked region rather
    /// than replacing it — the footman's tabard keeps its folds in every player colour — so an
    /// added player colour has to arrive already multiplied by the art's own brightness. Folding
    /// that brightness into the mask is what makes an additive channel land where a multiply would.
    /// Classic art needs none of it: there the player colour is a flat fill lying <i>under</i> the
    /// skin, so <c>team x (1 - diffuse.a)</c> added to the composite reproduces Warcraft III exactly.
    /// </param>
    private static RgbaImage TeamMaskTexture(TeamMask mask, RgbaImage? tintSource)
    {
        int w = mask.Width, h = mask.Height;
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int m = mask.Values[y * w + x];
                if (m != 0 && tintSource is not null)
                {
                    int sx = x * tintSource.Width / w, sy = y * tintSource.Height / h;
                    int o = (sy * tintSource.Width + sx) * 4;
                    int lum = (tintSource.Pixels[o + 2] * 77 + tintSource.Pixels[o + 1] * 151
                             + tintSource.Pixels[o] * 28) >> 8;
                    m = m * lum / 255;
                }
                px[(y * w + x) * 4 + 3] = (byte)m;
            }
        return new RgbaImage { Width = w, Height = h, Pixels = px };
    }

    private RgbaImage? LoadSlot(Casc.Wc3TextureCache textures, string modelCascName, MdxLayer layer, MdxTextureSlot slot)
    {
        int texId = layer.Slot(slot);
        if ((uint)texId >= (uint)_mdx.Textures.Count) return null;
        var tex = _mdx.Textures[texId];
        if (tex.IsReplaceable || tex.FileName.Length == 0) return null;
        return textures.Load(modelCascName, tex, _opt.TeamColor);
    }

    private string AddTexture(string fileName, RgbaImage image)
    {
        if (!_textures.Any(t => t.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase)))
            _textures.Add(new ExportedTexture(fileName, DdsWriter.Write(image)));
        return fileName;
    }

    private string TexStem(string primaryPath, int geosetIndex)
    {
        if (primaryPath.Length == 0) return $"{San(_opt.ModelName)}_mat{geosetIndex:00}";
        string stem = Path.GetFileNameWithoutExtension(primaryPath.Replace('/', '\\').Split('\\')[^1]);
        return San(stem.Length > 0 ? stem : $"{_opt.ModelName}_mat{geosetIndex:00}");

        static string San(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (char c in s) sb.Append(char.IsLetterOrDigit(c) || c is '_' or '-' ? char.ToLowerInvariant(c) : '_');
            return sb.ToString();
        }
    }

    // ---------------------------------------------------------------- attachments

    /// <summary>WC3 attachment names → the SC2 Editor's closed Ref_* vocabulary.</summary>
    private static readonly Dictionary<string, string> AttachmentMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Overhead"] = "Ref_Overhead", ["Origin"] = "Ref_Origin", ["Head"] = "Ref_Head",
        ["Chest"] = "Ref_Chest", ["Hand Left"] = "Ref_Hand Left", ["Hand Right"] = "Ref_Hand Right",
        ["Weapon"] = "Ref_Weapon", ["Weapon Left"] = "Ref_Weapon Left", ["Weapon Right"] = "Ref_Weapon Right",
        ["Foot Left"] = "Ref_Foot Left", ["Foot Right"] = "Ref_Foot Right",
        ["Left Foot"] = "Ref_Foot Left", ["Right Foot"] = "Ref_Foot Right",
        ["Sprite First"] = "Ref_Sprite First", ["Sprite Second"] = "Ref_Sprite Second",
        ["Sprite Third"] = "Ref_Sprite Third", ["Target"] = "Ref_Target", ["Damage"] = "Ref_Damage",
        ["Shield"] = "Ref_Shield", ["Back"] = "Ref_Back", ["Face"] = "Ref_Face", ["Turret"] = "Ref_Turret",
        ["Mount"] = "Ref_Chest Mount", ["Small"] = "Ref_Hardpoint Small", ["Medium"] = "Ref_Hardpoint Medium",
        ["Large"] = "Ref_Hardpoint Large", ["RallyPoint"] = "Ref_RallyPoint", ["Rally Point"] = "Ref_RallyPoint",
    };

    private void BuildAttachments()
    {
        int fallback = 0;
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < _mdx.Nodes.Count; i++)
        {
            var n = _mdx.Nodes[i];
            if (n.Kind != MdxNodeKind.Attachment) continue;

            // "Overhead Ref", "Head - Ref", "Foot Left Ref " → "Overhead", "Head", "Foot Left"
            string bare = n.Name;
            int refAt = bare.LastIndexOf(" Ref", StringComparison.OrdinalIgnoreCase);
            if (refAt > 0) bare = bare[..refAt];
            bare = bare.Trim(' ', '-');

            string sc2 = AttachmentMap.GetValueOrDefault(bare) ?? $"Ref_Attacher {fallback++:00}";
            while (!used.Add(sc2)) sc2 = $"Ref_Attacher {fallback++:00}";

            _attachments.Add((sc2, i));
            if (!sc2.StartsWith("Ref_Attacher")) _log.Add($"attachment '{n.Name}' -> {sc2}");
            else _log.Add($"attachment '{n.Name}' has no SC2 equivalent -> {sc2}");
        }
    }

    // ---------------------------------------------------------------- cameras

    private void BuildCameras()
    {
        foreach (var cam in _mdx.Cameras)
        {
            // m3 cameras aim along their bone's local -Z, so synthesize a root bone at the camera
            // position oriented from position toward target. Animated cameras bake in BuildSequences.
            var bone = new ExportBone
            {
                Name = UniqueBoneName($"Cam_{(cam.Name.Length > 0 ? cam.Name : "Portrait")}", _bones.Count),
                Parent = -1,
                RestLocation = ToSc2(cam.Position) * _opt.Scale,
                PivotWorld = ToSc2(cam.Position) * _opt.Scale,
                RestRotation = LookRotation(ToSc2(cam.Position), ToSc2(cam.TargetPosition)),
            };
            _bones.Add(bone);
            _cameras.Add((cam, _bones.Count - 1));
        }
        // SC2 picks a camera by name, so say which name its data has to ask for.
        foreach (var (cam, _) in _cameras)
            _log.Add($"camera '{(cam.Name.Length > 0 ? cam.Name : "Portrait")}' exported — SC2 selects it by that name");
    }

    /// <summary>
    /// Gives every GEOA-driven material a bone of its own whose <c>batching</c> flag switches its
    /// draw call off — the m3 answer to a Warcraft III geoset that appears in one animation only.
    /// </summary>
    /// <remarks>
    /// Fading the material's colour alpha, which is what this exporter did on its own, cannot hide
    /// anything additive: SC2 blend mode 3 weights the additive contribution by alpha, but a glow
    /// card whose art is opaque RGB on black still adds its full colour at alpha 0 in the general
    /// case, and Andromath's spell sparkle burned through every Stand because of it. A batch
    /// toggle removes the draw call outright, whatever the blend mode.
    /// <para>
    /// The bone carries no geometry — it exists only to hold the flag — so it is appended after
    /// the MDX nodes and the camera bones, where nothing indexes by node number any more.
    /// Confirmed against 7,544 Blizzard batch bones: BAT_.bone points at a real bone on 5,600 of
    /// 9,673 batches, and the batching anim ref reads interpolation 0, flags 6 when animated,
    /// init 0 or 1 (the rest state) and null 1.
    /// </para>
    /// </remarks>
    private void BuildBatchToggles()
    {
        if (!_opt.GeosetVisibility) return;
        foreach (var m in _materials)
        {
            if (m.VisibilityAnim is null) continue;
            m.RestVisible = m.DefaultAlpha >= 128;
            _bones.Add(new ExportBone
            {
                Name = UniqueBoneName($"Batch_{m.Name}", _bones.Count),
                Parent = -1,
            });
            m.ToggleBone = _bones.Count - 1;
        }
    }

    // ---------------------------------------------------------------- facing

    /// <summary>
    /// Warcraft III builds a model facing +X; StarCraft II expects one facing -Y. Everything that
    /// leaves this exporter in model space goes through here, a -90 degree turn about Z.
    /// </summary>
    /// <remarks>
    /// Without it a unit walks and attacks square to the way it is pointed — the "moonwalking" a
    /// user hit first, since SC2 turns the actor by its own facing and the art disagrees by a
    /// quarter turn. The angle is measured, not assumed: the Reforged azure dragon, bandit, satyr
    /// and murloc meshes each register onto their hand-converted SC2 counterparts at 270 degrees
    /// with a 1-2% histogram mismatch, against 44-66% at every other quarter turn. It agrees with
    /// the populations either side — 170 of 200 sampled WC3 unit models mirror across Y=0 (so they
    /// face along X) while 714 of 877 Blizzard SC2 unit models mirror across X=0, and Blizzard's
    /// portrait cameras, which stand in front of their subject, average (-0.20, -0.86) from the
    /// model centre while Warcraft III's average (+0.83, -0.11).
    /// </remarks>
    private static Vector3 ToSc2(Vector3 v) => new(v.Y, -v.X, v.Z);

    /// <summary>
    /// The same turn applied to a rotation. Every WC3 node's local frame is world-axis aligned, so
    /// turning the model conjugates each local rotation: <c>R q R⁻¹</c>, which for a quarter turn
    /// about Z is just the axis part run through <see cref="ToSc2(Vector3)"/>.
    /// </summary>
    private static Quaternion ToSc2(Quaternion q) => new(q.Y, -q.X, q.Z, q.W);

    /// <summary>An axis-aligned box after the turn, re-derived as a box.</summary>
    private static (Vector3 Min, Vector3 Max) ToSc2(Vector3 min, Vector3 max)
    {
        var a = ToSc2(min);
        var c = ToSc2(max);
        return (Vector3.Min(a, c), Vector3.Max(a, c));
    }

    /// <summary>
    /// Rotation whose local -Z aims from <paramref name="from"/> at <paramref name="to"/>, with
    /// local +Y up — the axes an m3 camera bone is read along.
    /// </summary>
    /// <remarks>
    /// This used to put the look direction on local -Y, which points an m3 camera 90 degrees away
    /// from its target: with a level shot the exported portrait camera stared straight down and the
    /// in-game portrait came back solid black. The SC2 editor's preview never showed it because the
    /// preview frames the model with its own orbit camera and ignores CAM_ entirely.
    /// <para>
    /// The axes are measured, not assumed: over 537 Blizzard models carrying a CAM_, the mean dot
    /// product between the camera bone's local -Z and the direction from the bone to the model's
    /// MSEC bounds centre is +0.986, while local -Y averages -0.041 (i.e. square to the shot). The
    /// bone's local +Y against world up averages +0.845, so +Y is the up axis.
    /// </para>
    /// </remarks>
    private static Quaternion LookRotation(Vector3 from, Vector3 to)
    {
        var f = to - from;
        if (f.LengthSquared() < 1e-10f) return Quaternion.Identity;
        f = Vector3.Normalize(f);

        // A camera aimed straight up or down has no roll a world-up reference can define, so fall
        // back to an axis it is not parallel to and let the roll land wherever it lands.
        var up = Math.Abs(Vector3.Dot(f, Vector3.UnitZ)) > 0.99f ? Vector3.UnitX : Vector3.UnitZ;
        var z = -f;                                             // local +Z points back from the target
        var x = Vector3.Normalize(Vector3.Cross(up, z));        // right
        var y = Vector3.Cross(z, x);                            // up
        var m = new Matrix4x4(
            x.X, x.Y, x.Z, 0,
            y.X, y.Y, y.Z, 0,
            z.X, z.Y, z.Z, 0,
            0, 0, 0, 1);
        return Quaternion.CreateFromRotationMatrix(m);
    }

    // ---------------------------------------------------------------- sequences

    /// <summary>
    /// Strips variant suffixes and maps WC3 names onto SC2's token vocabulary. Classic writes
    /// variants as "Attack - 1"; Reforged writes "Stand 1" — both normalise to a two-digit
    /// variation number, which SC2 reads as variations of the same logical animation.
    /// </summary>
    public static string MapSequenceName(string wc3Name)
    {
        string name = wc3Name.Trim();

        string variant = "";
        int dash = name.LastIndexOf(" - ", StringComparison.Ordinal);
        if (dash > 0 && int.TryParse(name[(dash + 3)..], out int v))
        {
            variant = $" {v:00}";
            name = name[..dash].TrimEnd();
        }
        else
        {
            int space = name.LastIndexOf(' ');
            if (space > 0 && int.TryParse(name[(space + 1)..], out int v2))
            {
                variant = $" {v2:00}";
                name = name[..space].TrimEnd();
            }
        }

        name = name switch
        {
            "Portrait Talk" => "Talk",
            "Decay Flesh" => "Death Flesh",
            "Decay Bone" => "Death Bone",
            "Dissipate" => "Death",
            "Sleep" => "Stand Sleep",
            _ => name,
        };
        return name + variant;
    }

    private List<SeqDef> BuildSequences()
    {
        uint nextAnimId = 1;
        uint AnimId()
        {
            while (nextAnimId == BndsAnimId || nextAnimId == EvntAnimId) nextAnimId++;
            return nextAnimId++;
        }

        // Stable per-bone track ids shared across sequences (the m3 pattern: one id per property).
        var boneIds = new (uint Loc, uint Rot, uint Scl)[_bones.Count];
        for (int i = 0; i < _bones.Count; i++) boneIds[i] = (AnimId(), AnimId(), AnimId());
        _boneAnimIds = boneIds;
        foreach (var m in _materials) m.ColorAnimId = AnimId();
        foreach (var em in _emitters) em.EmitRateAnimId = AnimId();
        foreach (var m in _materials) if (m.ToggleBone >= 0) m.BatchAnimId = AnimId();
        _nextAnimId = AnimId;

        var wanted = _mdx.Sequences
            .Where(s => _opt.Sequences is null || _opt.Sequences.Contains(s.Name))
            .ToList();

        var defs = new List<SeqDef>();
        int step = Math.Max(1000 / Math.Max(_opt.Fps, 1), 10);

        foreach (var seq in wanted)
        {
            string exportName = _opt.SequenceNames?.GetValueOrDefault(seq.Name) is { Length: > 0 } custom
                ? custom
                : MapSequenceName(seq.Name);
            var def = new SeqDef
            {
                Name = exportName,
                Source = seq,
                EndMs = Math.Max(seq.DurationMs, 33),
            };

            // Sample times: fixed step plus the exact end. All frames become sequence-relative.
            var times = new List<int>();
            for (int t = seq.IntervalStart; t < seq.IntervalEnd; t += step) times.Add(t);
            times.Add(seq.IntervalEnd);

            BakeBoneTracks(def, times, boneIds);
            BakeEmitterTracks(def, times);
            BakeBatchTracks(def, times);
            if (_opt.GeosetVisibility) BakeVisibilityTracks(def, times);
            BakeCameraTracks(def, times, boneIds);

            // Diagnostic stripping: keep only the requested track kind. Vec3Tracks holds both
            // location and scale tracks, so scale is filtered out by id in both partial modes.
            if (_opt.AnimData != M3AnimTestData.Full)
            {
                var locIds = new HashSet<uint>(boneIds.Select(b => b.Loc));
                switch (_opt.AnimData)
                {
                    case M3AnimTestData.None:
                        def.Vec3Tracks.Clear();
                        def.QuatTracks.Clear();
                        break;
                    case M3AnimTestData.LocationsOnly:
                        def.QuatTracks.Clear();
                        def.Vec3Tracks.RemoveAll(t => !locIds.Contains(t.Id));
                        break;
                    case M3AnimTestData.RotationsOnly:
                        def.Vec3Tracks.Clear();
                        break;
                }
            }

            defs.Add(def);
        }

        // SC2 falls back to Stand; "Stand NN" variations satisfy a Stand request, so only a model
        // with no Stand of any kind needs one synthesised.
        bool hasStand = defs.Any(d => d.Name == "Stand" || d.Name.StartsWith("Stand 0", StringComparison.Ordinal)
                                                        || d.Name.StartsWith("Stand 1", StringComparison.Ordinal));
        if (!hasStand)
        {
            var first = defs.FirstOrDefault();
            _log.Add("no Stand sequence selected — SC2 shows bind pose without one" +
                     (first is not null ? $"; duplicating '{first.Name}' as Stand" : ""));
            if (first is not null)
                defs.Add(new SeqDef
                {
                    Name = "Stand", Source = first.Source, EndMs = first.EndMs,
                    Vec3Tracks = first.Vec3Tracks, QuatTracks = first.QuatTracks, ColorTracks = first.ColorTracks,
                });
        }
        return defs;
    }

    private (uint Loc, uint Rot, uint Scl)[] _boneAnimIds = [];
    private Func<uint> _nextAnimId = () => 0;

    /// <summary>
    /// Evaluates the whole skeleton at each sample and extracts every bone's m3-local TRS:
    /// <c>L(t) = T(pivot_c) · W_c(t) · W_p(t)⁻¹ · T(-pivot_p)</c>. Constant-at-rest tracks are culled.
    /// </summary>
    private void BakeBoneTracks(SeqDef def, List<int> times, (uint Loc, uint Rot, uint Scl)[] boneIds)
    {
        int n = _mdx.Nodes.Count;
        var locs = new Vector3[n][];
        var rots = new Quaternion[n][];
        var scls = new Vector3[n][];
        for (int b = 0; b < n; b++) { locs[b] = new Vector3[times.Count]; rots[b] = new Quaternion[times.Count]; scls[b] = new Vector3[times.Count]; }

        for (int ti = 0; ti < times.Count; ti++)
        {
            _animator.Evaluate(def.Source, times[ti], times[ti]);
            for (int b = 0; b < n; b++)
            {
                var (loc, rot, scale) = _animator.LocalTrs(b);
                locs[b][ti] = ToSc2(loc) * _opt.Scale;
                var turned = ToSc2(rot);
                rots[b][ti] = ti > 0 && Quaternion.Dot(rots[b][ti - 1], turned) < 0 ? -turned : turned;
                scls[b][ti] = scale;
            }
        }

        var frames = times.Select(t => t - def.Source.IntervalStart).ToArray();
        for (int b = 0; b < n; b++)
        {
            var bone = _bones[b];
            if (NotAll(locs[b], v => (v - bone.RestLocation).Length() < 0.001f * Math.Max(_opt.Scale, 1)))
                def.Vec3Tracks.Add((boneIds[b].Loc, frames, locs[b]));
            if (NotAll(rots[b], q => QuatDist(q, Quaternion.Identity) < 1e-6f))
                def.QuatTracks.Add((boneIds[b].Rot, frames, rots[b]));
            if (NotAll(scls[b], v => (v - Vector3.One).Length() < 1e-4f))
                def.Vec3Tracks.Add((boneIds[b].Scl, frames, scls[b]));
        }
    }

    /// <summary>GEOA alpha as a hard on/off, onto the material's batch-toggle bone (SDFG).</summary>
    private void BakeBatchTracks(SeqDef def, List<int> times)
    {
        if (!_opt.GeosetVisibility) return;
        foreach (var mat in _materials)
        {
            if (mat.ToggleBone < 0 || mat.VisibilityAnim is null) continue;
            var vals = new uint[times.Count];
            for (int ti = 0; ti < times.Count; ti++)
                vals[ti] = SampleGeosetAlpha(mat.VisibilityAnim, def.Source, times[ti]) >= 0.5f ? 1u : 0u;

            uint rest = mat.RestVisible ? 1u : 0u;
            if (NotAll(vals, v => v == rest))
                def.FlagTracks.Add((mat.BatchAnimId, times.Select(t => t - def.Source.IntervalStart).ToArray(), vals));
        }
    }

    /// <summary>GEOA alpha → the owning material's layer colour track (BGRA, alpha animated).</summary>
    private void BakeVisibilityTracks(SeqDef def, List<int> times)
    {
        foreach (var mat in _materials)
        {
            var anim = mat.VisibilityAnim;
            if (anim is null) continue;

            var vals = new uint[times.Count];
            bool any = false;
            for (int ti = 0; ti < times.Count; ti++)
            {
                float alpha = SampleGeosetAlpha(anim, def.Source, times[ti]);
                byte a8 = (byte)Math.Clamp(alpha * 255f + 0.5f, 0, 255);
                vals[ti] = 0x00FFFFFFu | (uint)a8 << 24;
                any |= a8 < 255;
            }
            if (!any) continue;

            def.ColorTracks.Add((mat.ColorAnimId, times.Select(t => t - def.Source.IntervalStart).ToArray(), vals));
        }
    }

    private float SampleGeosetAlpha(MdxGeosetAnim anim, MdxSequence? seq, int timeMs)
        => anim.AlphaTrack is null
            ? anim.Alpha
            : _animator.GeosetAlpha(anim.GeosetId, seq, timeMs, timeMs);

    /// <summary>Animated cameras: KCTR/KTTR have no m3 form, so bake position + look-at rotation keys.</summary>
    private void BakeCameraTracks(SeqDef def, List<int> times, (uint Loc, uint Rot, uint Scl)[] boneIds)
    {
        foreach (var (cam, boneIndex) in _cameras)
        {
            if (cam.TranslationTrack is null && cam.TargetTranslationTrack is null) continue;

            var locs = new Vector3[times.Count];
            var rots = new Quaternion[times.Count];
            for (int ti = 0; ti < times.Count; ti++)
            {
                var pos = cam.Position + SampleTrack(cam.TranslationTrack, def.Source, times[ti]);
                var target = cam.TargetPosition + SampleTrack(cam.TargetTranslationTrack, def.Source, times[ti]);
                locs[ti] = ToSc2(pos) * _opt.Scale;
                var q = LookRotation(ToSc2(pos), ToSc2(target));
                rots[ti] = ti > 0 && Quaternion.Dot(rots[ti - 1], q) < 0 ? -q : q;
            }

            var frames = times.Select(t => t - def.Source.IntervalStart).ToArray();
            var bone = _bones[boneIndex];
            if (NotAll(locs, v => (v - bone.RestLocation).Length() < 0.001f))
                def.Vec3Tracks.Add((boneIds[boneIndex].Loc, frames, locs));
            if (NotAll(rots, q => QuatDist(q, bone.RestRotation) < 1e-6f))
                def.QuatTracks.Add((boneIds[boneIndex].Rot, frames, rots));
        }
    }

    /// <summary>
    /// Emission rate per sequence, gated by the emitter's KP2V visibility track.
    /// </summary>
    /// <remarks>
    /// Warcraft III switches an emitter on and off per animation through KP2V — that is how one
    /// model carries a death cloud, a walking dust puff and a standing shimmer without all three
    /// running at once. Exported as a constant rate they all run all the time: the sea witch stood
    /// in the middle of her own death effect, 64-cell cloud cards and all. SC2 has no per-animation
    /// emitter switch, but PAR_.emit_rate is an animatable float, and a rate of zero is the same
    /// thing.
    /// </remarks>
    private void BakeEmitterTracks(SeqDef def, List<int> times)
    {
        foreach (var em in _emitters)
        {
            var src = em.Source;
            var rates = new float[times.Count];
            for (int ti = 0; ti < times.Count; ti++)
            {
                float rate = _animator.SampleFloat(src.EmissionRateTrack, def.Source, times[ti], src.EmissionRate);
                rates[ti] = EmitterVisible(src, def.Source, times[ti]) ? MathF.Max(rate, 0) : 0;
            }
            float rest = em.RestEmissionRate;
            if (NotAll(rates, r => Math.Abs(r - rest) < 0.001f))
                def.FloatTracks.Add((em.EmitRateAnimId, times.Select(t => t - def.Source.IntervalStart).ToArray(), rates));
        }
    }

    /// <summary>
    /// True when the emitter's KP2V track has it switched on at this point in this sequence.
    /// </summary>
    /// <remarks>
    /// Deliberately the viewer's own sampler, so an exported model emits exactly where the app
    /// shows it emitting. It is not interchangeable with a naive global-timeline lookup: WC3 reads
    /// only the keys that fall INSIDE the playing sequence and falls back to the rest value when a
    /// sequence contains none — so a torch whose two KP2V keys sit in Death still burns during
    /// Stand. Clamping across the whole timeline instead reads that torch as off and silences it.
    /// </remarks>
    private bool EmitterVisible(MdxParticleEmitter2 e, MdxSequence? seq, int timeMs)
        => _animator.SampleFloat(e.VisibilityTrack, seq, timeMs, 1f) >= 0.5f;

    private Vector3 SampleTrack(MdxTrack<Vector3>? track, MdxSequence seq, int timeMs)
    {
        if (track is null || track.Count == 0) return Vector3.Zero;
        // Linear resample is fine here — cameras bake densely anyway.
        var times = track.Times;
        int lo = 0, hi = track.Count - 1;
        if (timeMs <= times[lo]) return track.Values[lo];
        if (timeMs >= times[hi]) return track.Values[hi];
        int k = lo;
        while (k < hi && times[k + 1] <= timeMs) k++;
        int span = times[k + 1] - times[k];
        float f = span > 0 ? (timeMs - times[k]) / (float)span : 0;
        return Vector3.Lerp(track.Values[k], track.Values[k + 1], f);
    }

    private static bool NotAll<T>(T[] vals, Predicate<T> pred)
    {
        foreach (var v in vals) if (!pred(v)) return true;
        return false;
    }

    private static float QuatDist(Quaternion a, Quaternion c)
    {
        float dp = (a.X - c.X) * (a.X - c.X) + (a.Y - c.Y) * (a.Y - c.Y) + (a.Z - c.Z) * (a.Z - c.Z) + (a.W - c.W) * (a.W - c.W);
        float dn = (a.X + c.X) * (a.X + c.X) + (a.Y + c.Y) * (a.Y + c.Y) + (a.Z + c.Z) * (a.Z + c.Z) + (a.W + c.W) * (a.W + c.W);
        return Math.Min(dp, dn);
    }

    // ---------------------------------------------------------------- geometry packing

    private sealed class RegionData
    {
        public byte[] VertexBytes = [];
        public int VertexCount;
        public List<ushort> Lookup = [];
        public int LookupsUsed;
        public ushort[] Faces = [];                 // region-local vertex indices
        public Vector3 Min, Max;
        public int FirstVertex, FirstFace, FirstLookup;
        public int SourceRegion;                    // index into _regions (material + geoset)
    }

    /// <summary>
    /// Largest bone palette a single region may reference.
    /// </summary>
    /// <remarks>
    /// SC2 skins a draw call from a fixed-size matrix palette, so a region that references more
    /// bones than fit renders garbage — invisibly at rest (every bone resolves to identity there)
    /// and as exploded geometry the moment anything animates. Blizzard's own models never exceed
    /// 45 in one region: <c>sm_raynormarine</c> has 118 bones but splits into 10 regions of ≤36,
    /// and even the largest sampled unit stops at 45. Reforged geosets routinely need 80+, so they
    /// are split here. Classic models sit far below the limit and are unaffected.
    /// </remarks>
    private const int MaxRegionBones = 45;

    private readonly record struct Influence(int Bone, int W8);

    /// <summary>
    /// Packs one geoset into 32-byte vertices, splitting it into as many regions as the bone-palette
    /// limit requires. Triangles are grouped greedily so each group's distinct bone set fits.
    /// </summary>
    private List<RegionData> PackRegions(MdxGeoset g, int sourceRegion)
    {
        int n = g.VertexCount;
        var perVertex = new Influence[n][];

        var boneChunk = Enumerable.Range(0, _mdx.Nodes.Count)
                                  .Where(i => _mdx.Nodes[i].Kind == MdxNodeKind.Bone)
                                  .ToArray();
        var byObjectId = new Dictionary<int, int>();
        for (int i = 0; i < _mdx.Nodes.Count; i++) byObjectId.TryAdd(_mdx.Nodes[i].ObjectId, i);

        Span<Influence> slots = stackalloc Influence[4];
        for (int v = 0; v < n; v++)
        {
            int used = 0;
            if (g.HasSkin)
            {
                for (int k = 0; k < 4; k++)
                {
                    int w8 = g.SkinBoneWeights[v * 4 + k];
                    if (w8 == 0) continue;
                    int chunkIndex = g.SkinBoneIndices[v * 4 + k];
                    if (chunkIndex >= boneChunk.Length) continue;
                    slots[used++] = new Influence(boneChunk[chunkIndex], w8);
                }
            }
            else if (g.VertexGroups.Length > v && g.MatrixGroupSizes.Length > 0)
            {
                int grp = Math.Min(g.VertexGroups[v], g.MatrixGroupSizes.Length - 1);
                int at = 0;
                for (int gi = 0; gi < grp; gi++) at += g.MatrixGroupSizes[gi];
                int size = g.MatrixGroupSizes[grp];

                // Equal weights over the group, clipped to the format's 4 slots.
                int take = Math.Min(size, 4);
                for (int k = 0; k < take && at + k < g.MatrixIndices.Length; k++)
                    if (byObjectId.TryGetValue(g.MatrixIndices[at + k], out int ni))
                        slots[used++] = new Influence(ni, 255 / take);
            }
            if (used == 0) slots[used++] = new Influence(0, 255);

            // Renormalise to exactly 255.
            int total = 0;
            for (int k = 0; k < used; k++) total += slots[k].W8;
            if (total != 255 && total > 0)
            {
                int acc = 0;
                for (int k = 0; k < used; k++)
                {
                    int w = k == used - 1 ? 255 - acc : slots[k].W8 * 255 / total;
                    slots[k] = new Influence(slots[k].Bone, w);
                    acc += w;
                }
            }
            perVertex[v] = slots[..used].ToArray();
        }

        // Greedy triangle partition: extend the current group while its bone set still fits.
        var groups = new List<List<int>>();          // each = triangle start indices into g.Indices
        var current = new List<int>();
        var currentBones = new HashSet<int>();
        var triBones = new HashSet<int>();
        for (int t = 0; t + 2 < g.Indices.Length; t += 3)
        {
            triBones.Clear();
            for (int c = 0; c < 3; c++)
                foreach (var inf in perVertex[g.Indices[t + c]])
                    triBones.Add(inf.Bone);

            int wouldBe = currentBones.Count;
            foreach (int b in triBones) if (!currentBones.Contains(b)) wouldBe++;

            if (current.Count > 0 && wouldBe > MaxRegionBones)
            {
                groups.Add(current);
                current = [];
                currentBones.Clear();
            }
            current.Add(t);
            foreach (int b in triBones) currentBones.Add(b);
        }
        if (current.Count > 0) groups.Add(current);
        if (groups.Count == 0) return [];

        if (groups.Count > 1)
            _log.Add($"geoset {g.Index} needs {groups.Count} regions to stay within " +
                     $"{MaxRegionBones} bones per region (SC2 skinning palette limit)");

        var result = new List<RegionData>(groups.Count);
        foreach (var group in groups)
            result.Add(PackGroup(g, group, perVertex, sourceRegion));
        return result;
    }

    /// <summary>Builds one region from a triangle group, renumbering its vertices from zero.</summary>
    private RegionData PackGroup(MdxGeoset g, List<int> triangles, Influence[][] perVertex, int sourceRegion)
    {
        float scale = _opt.Scale;
        var r = new RegionData { SourceRegion = sourceRegion };

        var lookupOf = new Dictionary<int, int>();
        int LookupIndex(int m3Bone)
        {
            if (!lookupOf.TryGetValue(m3Bone, out int li))
            {
                li = r.Lookup.Count;
                lookupOf[m3Bone] = li;
                r.Lookup.Add((ushort)m3Bone);
            }
            return li;
        }

        // Vertex remap: only the vertices this group's triangles touch, in first-use order.
        var remap = new Dictionary<int, ushort>();
        var order = new List<int>();
        var faces = new ushort[triangles.Count * 3];
        int f = 0;
        foreach (int t in triangles)
        {
            for (int c = 0; c < 3; c++)
            {
                int src = g.Indices[t + c];
                if (!remap.TryGetValue(src, out ushort local))
                {
                    local = (ushort)order.Count;
                    remap[src] = local;
                    order.Add(src);
                }
                faces[f++] = local;
            }
        }

        var mn = new Vector3(float.MaxValue);
        var mx = new Vector3(float.MinValue);
        var bytes = new byte[order.Count * 32];
        var uvs = g.Uvs;
        bool hasUvs = uvs.Length >= g.VertexCount;

        for (int i = 0; i < order.Count; i++)
        {
            int v = order[i];
            int o = i * 32;
            var pos = ToSc2(g.Positions[v]) * scale;
            mn = Vector3.Min(mn, pos); mx = Vector3.Max(mx, pos);
            BitConverter.TryWriteBytes(bytes.AsSpan(o), pos.X);
            BitConverter.TryWriteBytes(bytes.AsSpan(o + 4), pos.Y);
            BitConverter.TryWriteBytes(bytes.AsSpan(o + 8), pos.Z);

            var infl = perVertex[v];
            r.LookupsUsed = Math.Max(r.LookupsUsed, infl.Length);
            for (int k = 0; k < 4; k++)
            {
                bytes[o + 12 + k] = (byte)(k < infl.Length ? infl[k].W8 : 0);
                bytes[o + 16 + k] = (byte)(k < infl.Length ? LookupIndex(infl[k].Bone) : 0);
            }

            // ---- normal / uv / tangent ----
            var nrm = ToSc2(g.Normals.Length > v ? g.Normals[v] : Vector3.UnitZ);
            if (nrm.LengthSquared() > 1e-10f) nrm = Vector3.Normalize(nrm);
            bytes[o + 20] = PackUnit(nrm.X); bytes[o + 21] = PackUnit(nrm.Y); bytes[o + 22] = PackUnit(nrm.Z);
            bytes[o + 23] = 255;

            // MDX V is top-down already — the m3 stored value IS v * 2048, no flip.
            float u = hasUvs ? uvs[v].X : 0;
            float vv = hasUvs ? uvs[v].Y : 0;
            BitConverter.TryWriteBytes(bytes.AsSpan(o + 24), (short)Math.Clamp(Math.Round(u * 2048), short.MinValue, short.MaxValue));
            BitConverter.TryWriteBytes(bytes.AsSpan(o + 26), (short)Math.Clamp(Math.Round(vv * 2048), short.MinValue, short.MaxValue));

            var tan = g.Tangents.Length > v
                ? ToSc2(new Vector3(g.Tangents[v].X, g.Tangents[v].Y, g.Tangents[v].Z))
                : OrthogonalTo(nrm);
            if (tan.LengthSquared() > 1e-10f) tan = Vector3.Normalize(tan);
            bytes[o + 28] = PackUnit(tan.X); bytes[o + 29] = PackUnit(tan.Y); bytes[o + 30] = PackUnit(tan.Z);
            bytes[o + 31] = 0;
        }

        r.VertexBytes = bytes;
        r.VertexCount = order.Count;
        r.Faces = faces;
        r.Min = mn; r.Max = mx;
        return r;
    }

    private static byte PackUnit(float c) => (byte)Math.Clamp(Math.Round((c + 1) / 2 * 255), 0, 255);

    private static Vector3 OrthogonalTo(Vector3 nrm)
    {
        if (nrm.LengthSquared() < 1e-10f) return Vector3.UnitX;
        var axis = Math.Abs(nrm.Z) < 0.9f ? Vector3.UnitZ : Vector3.UnitX;
        var t = Vector3.Cross(axis, nrm);
        return t.LengthSquared() > 1e-10f ? Vector3.Normalize(t) : Vector3.UnitX;
    }

    // ---------------------------------------------------------------- the file

    private byte[] WriteM3(List<SeqDef> seqDefs)
    {
        var b = new M3Builder();

        // SC2 composes bone worlds in a single forward pass over the bone array, so every parent
        // must precede its children. MDX node order gives no such guarantee (bones parented to
        // later helpers are common) — a child hitting a not-yet-computed parent world is the
        // classic "exploded spikes" import. Emit bones in stable parent-first order and remap
        // every bone index written into the file.
        var order = new int[_bones.Count];      // order[si] = original index
        var boneMap = new int[_bones.Count];    // boneMap[original] = sorted index
        {
            int emitted = 0;
            var visited = new bool[_bones.Count];
            void Emit(int i)
            {
                if (visited[i]) return;
                visited[i] = true;
                if (_bones[i].Parent >= 0) Emit(_bones[i].Parent);
                boneMap[i] = emitted;
                order[emitted++] = i;
            }
            for (int i = 0; i < _bones.Count; i++) Emit(i);
        }

        // ---- geometry prepass ----
        var regions = new List<RegionData>();
        for (int i = 0; i < _regions.Count; i++)
            regions.AddRange(PackRegions(_regions[i].Geoset, i));

        var lookupTable = new List<ushort>();
        int totalVerts = 0, totalFaces = 0;
        var bmin = new Vector3(float.MaxValue);
        var bmax = new Vector3(float.MinValue);
        foreach (var r in regions)
        {
            for (int k = 0; k < r.Lookup.Count; k++) r.Lookup[k] = (ushort)boneMap[r.Lookup[k]];
            r.FirstVertex = totalVerts;
            r.FirstFace = totalFaces;
            r.FirstLookup = lookupTable.Count;
            lookupTable.AddRange(r.Lookup);
            totalVerts += r.VertexCount;
            totalFaces += r.Faces.Length;
            bmin = Vector3.Min(bmin, r.Min);
            bmax = Vector3.Max(bmax, r.Max);
        }
        if (totalVerts > 65536) throw new InvalidOperationException($"vertex count {totalVerts} exceeds the m3 limit of 65536 — pick a higher LOD");
        float boundsRadius = (bmax - bmin).Length() / 2;

        var boneSkinned = new bool[_bones.Count];
        foreach (ushort bi in lookupTable) boneSkinned[bi] = true;
        int skinBoneCount = 0;
        foreach (ushort bi in lookupTable) skinBoneCount = Math.Max(skinBoneCount, bi + 1);

        var boneAnimated = new bool[_bones.Count];
        foreach (var def in seqDefs)
        {
            foreach (var t in def.Vec3Tracks) MarkAnimated(t.Id, boneAnimated);
            foreach (var t in def.QuatTracks) MarkAnimated(t.Id, boneAnimated);
        }

        // ---- header & MODL ----
        var header = b.Add("MD34", 11, 24);
        var modl = b.Add("MODL", 29, 856);
        var modlName = b.AddString(_opt.ModelName);

        // ---- sequences ----
        var seqs = b.Add("SEQS", 2, 92);
        var seqNames = new M3Builder.Section[seqDefs.Count];
        for (int i = 0; i < seqDefs.Count; i++) seqNames[i] = b.AddString(seqDefs[i].Name);
        var stc = b.Add("STC_", 4, 204);
        var stcIdLists = new uint[seqDefs.Count][];

        for (int i = 0; i < seqDefs.Count; i++)
        {
            var def = seqDefs[i];
            var src = def.Source;
            {
                var w = seqs.W;
                w.Write(-1); w.Write(-1);
                b.Ref(seqs, seqNames[i]);
                w.Write(0u); w.Write((uint)def.EndMs);
                w.Write(src.MoveSpeed * _opt.Scale);
                w.Write(src.NonLooping ? 1u : 0u);                          // 0x1 not_looping
                w.Write(src.Rarity <= 0 ? 100u : (uint)Math.Max(1, Math.Round(100 / (1 + src.Rarity))));
                w.Write(1u); w.Write(1u);
                w.Write(100u);                                              // ms_blend
                // Per-sequence bounds from the WC3 SEQS entry when it has one.
                var (turnedMin, turnedMax) = ToSc2(src.Min, src.Max);
                var (mn, mx, rad) = src.BoundsRadius > 0
                    ? (turnedMin * _opt.Scale, turnedMax * _opt.Scale, src.BoundsRadius * _opt.Scale)
                    : (bmin, bmax, boundsRadius);
                WriteBnds(w, mn, mx, rad);
                b.NullRef(seqs);
            }

            var ids = new List<uint> { EvntAnimId };
            var refs = new List<uint> { 0 };
            for (int k = 0; k < def.Vec3Tracks.Count; k++) { ids.Add(def.Vec3Tracks[k].Id); refs.Add(2u << 16 | (uint)k); }
            for (int k = 0; k < def.QuatTracks.Count; k++) { ids.Add(def.QuatTracks[k].Id); refs.Add(3u << 16 | (uint)k); }
            for (int k = 0; k < def.ColorTracks.Count; k++) { ids.Add(def.ColorTracks[k].Id); refs.Add(4u << 16 | (uint)k); }
            for (int k = 0; k < def.FloatTracks.Count; k++) { ids.Add(def.FloatTracks[k].Id); refs.Add(5u << 16 | (uint)k); }
            for (int k = 0; k < def.FlagTracks.Count; k++) { ids.Add(def.FlagTracks[k].Id); refs.Add(11u << 16 | (uint)k); }
            if (def.Animated) { ids.Add(BndsAnimId); refs.Add(12u << 16); }
            stcIdLists[i] = ids.ToArray();

            var idsSec = b.AddU32s(ids);
            var refsSec = b.AddU32s(refs);
            var stcName = b.AddString(def.Name + "_full");

            var sdev = b.Add("SDEV", 0, 32);
            var sdevFrames = b.AddI32s([def.EndMs]);
            var evnt = b.Add("EVNT", 2, 108);
            var evntName = b.AddString("Evt_SeqEnd");
            {
                var w = sdev.W;
                b.Ref(sdev, sdevFrames);
                w.Write(1u);
                w.Write((uint)def.EndMs);
                b.Ref(sdev, evnt);
                sdev.Count = 1;
            }
            {
                var w = evnt.W;
                b.Ref(evnt, evntName);
                w.Write(-1);
                w.Write((short)-1); w.Write((ushort)0);
                for (int r = 0; r < 4; r++)
                    for (int c = 0; c < 4; c++)
                        w.Write(r == c ? 1f : 0f);
                w.Write(4u);
                b.NullRef(evnt);
                w.Write(0u); w.Write(0u);
                evnt.Count = 1;
            }

            M3Builder.Section? sd3v = null, sd4q = null, sdcc = null, sdr3 = null, sdfg = null, sdmb = null;
            if (def.Vec3Tracks.Count > 0)
            {
                sd3v = b.Add("SD3V", 0, 32);
                foreach (var t in def.Vec3Tracks)
                {
                    var frames = b.AddI32s(t.FramesMs);
                    var keys = b.Add("VEC3", 0, 12);
                    foreach (var v in t.Vals) WriteVec3(keys.W, v);
                    keys.Count = t.Vals.Length;
                    b.Ref(sd3v, frames);
                    sd3v.W.Write(0u);
                    sd3v.W.Write((uint)def.EndMs);
                    b.Ref(sd3v, keys);
                    sd3v.Count++;
                }
            }
            if (def.QuatTracks.Count > 0)
            {
                sd4q = b.Add("SD4Q", 0, 32);
                foreach (var t in def.QuatTracks)
                {
                    var frames = b.AddI32s(t.FramesMs);
                    var keys = b.Add("QUAT", 0, 16);
                    foreach (var q in t.Vals) { keys.W.Write(q.X); keys.W.Write(q.Y); keys.W.Write(q.Z); keys.W.Write(q.W); }
                    keys.Count = t.Vals.Length;
                    b.Ref(sd4q, frames);
                    sd4q.W.Write(0u);
                    sd4q.W.Write((uint)def.EndMs);
                    b.Ref(sd4q, keys);
                    sd4q.Count++;
                }
            }
            if (def.ColorTracks.Count > 0)
            {
                sdcc = b.Add("SDCC", 0, 32);
                foreach (var t in def.ColorTracks)
                {
                    var frames = b.AddI32s(t.FramesMs);
                    // 3-letter tag: on disk it must read 'L','O','C',0 — reversed name, NUL-padded
                    // last. "COL_" is not a real tag and makes SC2 reject the whole file.
                    var keys = b.Add("\0COL", 0, 4);
                    foreach (uint c in t.Vals) keys.W.Write(c);
                    keys.Count = t.Vals.Length;
                    b.Ref(sdcc, frames);
                    sdcc.W.Write(0u);
                    sdcc.W.Write((uint)def.EndMs);
                    b.Ref(sdcc, keys);
                    sdcc.Count++;
                }
            }
            // SDR3 — plain animated floats. Slot 5 of STC_, so anim refs point at it with 5 << 16,
            // the same (sd_type << 16) | index packing the vector and colour tracks use.
            if (def.FloatTracks.Count > 0)
            {
                sdr3 = b.Add("SDR3", 0, 32);
                foreach (var t in def.FloatTracks)
                {
                    var frames = b.AddI32s(t.FramesMs);
                    var keys = b.Add("REAL", 0, 4);
                    foreach (float v in t.Vals) keys.W.Write(v);
                    keys.Count = t.Vals.Length;
                    b.Ref(sdr3, frames);
                    sdr3.W.Write(0u);
                    sdr3.W.Write((uint)def.EndMs);
                    b.Ref(sdr3, keys);
                    sdr3.Count++;
                }
            }
            // SDFG — on/off flags. Slot 11 of STC_, keys in a FLAG section holding 0 or 1.
            if (def.FlagTracks.Count > 0)
            {
                sdfg = b.Add("SDFG", 0, 32);
                foreach (var t in def.FlagTracks)
                {
                    var frames = b.AddI32s(t.FramesMs);
                    var keys = b.Add("FLAG", 0, 4);
                    foreach (uint v in t.Vals) keys.W.Write(v);
                    keys.Count = t.Vals.Length;
                    b.Ref(sdfg, frames);
                    sdfg.W.Write(0u);
                    sdfg.W.Write((uint)def.EndMs);
                    b.Ref(sdfg, keys);
                    sdfg.Count++;
                }
            }
            if (def.Animated)
            {
                sdmb = b.Add("SDMB", 0, 32);
                var frames = b.AddI32s([0]);
                var keys = b.Add("BNDS", 0, 28);
                WriteBnds(keys.W, bmin, bmax, boundsRadius);
                keys.Count = 1;
                b.Ref(sdmb, frames);
                sdmb.W.Write(0u);
                sdmb.W.Write((uint)def.EndMs);
                b.Ref(sdmb, keys);
                sdmb.Count = 1;
            }

            {
                var w = stc.W;
                b.Ref(stc, stcName);
                w.Write((ushort)0); w.Write((ushort)0);
                w.Write((ushort)i); w.Write((ushort)i);
                b.Ref(stc, idsSec);
                b.Ref(stc, refsSec);
                w.Write(0u);
                b.Ref(stc, sdev);
                b.NullRef(stc);                                 // sd2v
                if (sd3v is not null) b.Ref(stc, sd3v); else b.NullRef(stc);
                if (sd4q is not null) b.Ref(stc, sd4q); else b.NullRef(stc);
                if (sdcc is not null) b.Ref(stc, sdcc); else b.NullRef(stc);
                if (sdr3 is not null) b.Ref(stc, sdr3); else b.NullRef(stc);
                for (int k = 0; k < 5; k++) b.NullRef(stc);     // sds6..sdu3
                if (sdfg is not null) b.Ref(stc, sdfg); else b.NullRef(stc);
                if (sdmb is not null) b.Ref(stc, sdmb); else b.NullRef(stc);
            }
        }
        seqs.Count = seqDefs.Count;
        stc.Count = seqDefs.Count;

        var stg = b.Add("STG_", 0, 24);
        for (int i = 0; i < seqDefs.Count; i++)
        {
            var name = b.AddString(seqDefs[i].Name);
            var indices = b.AddU32s([(uint)i]);
            b.Ref(stg, name);
            b.Ref(stg, indices);
        }
        stg.Count = seqDefs.Count;

        var sts = b.Add("STS_", 0, 28);
        for (int i = 0; i < seqDefs.Count; i++)
        {
            var ids = b.AddU32s(stcIdLists[i]);
            var w = sts.W;
            b.Ref(sts, ids);
            w.Write(-1); w.Write(-1); w.Write(-1);
            w.Write((short)-1); w.Write((ushort)0);
        }
        sts.Count = seqDefs.Count;

        // ---- bones ----
        var boneSec = b.Add("BONE", 1, 160);
        var boneNames = new M3Builder.Section[_bones.Count];
        for (int i = 0; i < _bones.Count; i++) boneNames[i] = b.AddString(_bones[i].Name);

        var verts = b.Add("U8__", 0, 1);
        var div = b.Add("DIV_", 2, 52);
        var faces = b.Add("U16_", 0, 2);
        var regn = b.Add("REGN", 5, 48);
        var bat = b.Add("BAT_", 1, 14);
        var msec = b.Add("MSEC", 1, 72);
        var boneLookup = b.Add("U16_", 0, 2);

        // ---- attachments ----
        M3Builder.Section? att = null, attAddon = null;
        if (_attachments.Count > 0)
        {
            att = b.Add("ATT_", 1, 20);
            var attNames = _attachments.Select(a => b.AddString(a.Name)).ToArray();
            for (int i = 0; i < _attachments.Count; i++)
            {
                var w = att.W;
                w.Write(-1);
                b.Ref(att, attNames[i]);
                w.Write((uint)boneMap[_attachments[i].Bone]);
            }
            att.Count = _attachments.Count;

            attAddon = b.Add("U16_", 0, 2);
            for (int i = 0; i < _attachments.Count; i++) attAddon.W.Write((ushort)0xFFFF);
            attAddon.Count = _attachments.Count;
        }

        // ---- cameras ----
        M3Builder.Section? cam = null, camAddon = null;
        if (_cameras.Count > 0)
        {
            cam = b.Add("CAM_", 5, 264);
            var camNames = _cameras.Select(c => b.AddString(c.Camera.Name.Length > 0 ? c.Camera.Name : "Portrait")).ToArray();
            for (int i = 0; i < _cameras.Count; i++)
            {
                var (mdxCam, bone) = _cameras[i];
                var w = cam.W;
                w.Write((uint)boneMap[bone]);
                b.Ref(cam, camNames[i]);
                WriteFloatAnimRef(w, mdxCam.FieldOfView, _nextAnimId());
                w.Write(1u);                                    // use_vertical_fov
                w.Write(3u);                                    // depth_of_field_type
                WriteFloatAnimRef(w, mdxCam.FarClip * _opt.Scale, _nextAnimId());
                WriteFloatAnimRef(w, mdxCam.NearClip * _opt.Scale, _nextAnimId());
                WriteFloatAnimRef(w, 0, _nextAnimId());         // clip2
                WriteFloatAnimRef(w, 0, _nextAnimId());         // focal_depth
                WriteFloatAnimRef(w, 0, _nextAnimId());         // falloff_start
                WriteFloatAnimRef(w, mdxCam.FarClip * _opt.Scale, _nextAnimId());   // falloff_end
                WriteFloatAnimRef(w, 0, _nextAnimId());
                WriteFloatAnimRef(w, 0, _nextAnimId());
                WriteFloatAnimRef(w, 0, _nextAnimId());         // depth_of_field
                WriteFloatAnimRef(w, 0, _nextAnimId());
                WriteFloatAnimRef(w, 0, _nextAnimId());
            }
            cam.Count = _cameras.Count;

            camAddon = b.Add("U16_", 0, 2);
            for (int i = 0; i < _cameras.Count; i++) camAddon.W.Write((ushort)0xFFFF);
            camAddon.Count = _cameras.Count;
        }

        // ---- materials ----
        var matm = b.Add("MATM", 0, 8);
        var mats = b.Add("MAT_", 20, 352);
        var matNames = new M3Builder.Section[_materials.Count];
        var matLayers = new M3Builder.Section?[_materials.Count][];
        for (int i = 0; i < _materials.Count; i++)
        {
            var m = _materials[i];
            matNames[i] = b.AddString(m.Name);
            matLayers[i] = new M3Builder.Section?[18];
            // In a modern (V20) standard material the diffuse alpha is a TEAM-COLOUR mask when
            // sampled (ARGB), and alpha_test_threshold reads the dedicated alpha-mask layer
            // (layer_alpha1) — NOT the diffuse alpha. Both wrong guesses are on record: RGB with
            // no alpha1 layer gave the test nothing to read (cutouts drew as opaque black cards),
            // and ARGB team-tinted them white instead. The V15-era hightemplar cape (ARGB +
            // alpha_test, cut out fine) is a false friend — the semantics changed after Liberty.
            // The V20 recipe, from smx2_aiur02_foliage grass / smx2_stasiscore (the latter shares
            // our exact material flags 0x80004008): diffuse RGB, alpha1 = the same bitmap with
            // A-only channels, same wrap. So the diffuse stays RGB always, and any material that
            // consumes its alpha (cutout or alpha-blend) gets the alpha1 mask layer.
            bool alphaUsed = m.Blend is CompositeBlend.AlphaTest or CompositeBlend.AlphaBlend
                                      or CompositeBlend.Additive;
            matLayers[i][0] = WriteLayer(b, _opt.TexturePrefix + m.DiffusePath, m.ColorAnimId, wrap: true,
                                         defaultAlpha: m.DefaultAlpha,
                                         flipbook: m.FlipbookCells > 1);                                       // diff
            if (m.SpecularPath.Length > 0) matLayers[i][2] = WriteLayer(b, _opt.TexturePrefix + m.SpecularPath, _nextAnimId(), wrap: true);
            if (m.EmissivePath.Length > 0) matLayers[i][4] = WriteLayer(b, _opt.TexturePrefix + m.EmissivePath, _nextAnimId(), wrap: true);
            // The alpha layer samples the same sheet, so it needs the same flipbook flag or it
            // reads the whole atlas while the diffuse reads one cell. Blizzard flags it more often
            // than any other slot: 83% of alpha1 layers behind a multi-cell emitter carry 0x100.
            if (alphaUsed) matLayers[i][8] = WriteLayer(b, _opt.TexturePrefix + m.DiffusePath, _nextAnimId(), wrap: true,
                                                        colorChannels: ChannelsAlphaOnly,
                                                        flipbook: m.FlipbookCells > 1);                        // alpha1
            if (m.NormalPath.Length > 0) matLayers[i][10] = WriteLayer(b, _opt.TexturePrefix + m.NormalPath, _nextAnimId(), wrap: true);
            // Player colour. StarCraft II has no team-colour texture slot: it has a *blend mode*.
            // Set blend_mode_emis1 (or emis2) to 4 and the engine adds the live player colour
            // scaled by that emissive layer's single sampled channel — which is why the layer takes
            // A-only channels. Blizzard does exactly this on 1,204 of the 8,161 materials across
            // 2,018 Heroes unit models, and the emis2 spelling below is theirs verbatim: where a
            // material already has a real emissive glow in emis1 (RGB, add), the team mask goes to
            // emis2 (A-only, mode 4) rather than displacing it.
            if (m.TeamPath.Length > 0)
            {
                m.TeamBlendSlot = m.EmissivePath.Length > 0 ? 5 : 4;
                matLayers[i][m.TeamBlendSlot] = WriteLayer(b, _opt.TexturePrefix + m.TeamPath, _nextAnimId(),
                                                           wrap: true, colorChannels: ChannelsAlphaOnly);
            }
        }
        var nullLayer = b.Add("LAYR", 26, 464);
        WriteNullLayer(nullLayer.W);
        nullLayer.Count = 1;
        var iref = b.Add("IREF", 0, 64);

        // ---- BBSC billboards ----
        // camera_look_at stays 0: Warcraft III aligns a billboard with the view plane rather than
        // aiming it at the camera's position, and the hand-converted War3_Priest.m3 that renders
        // its orb correctly in game writes 0 for the same card.
        M3Builder.Section? bbsc = null;
        if (_billboards.Count > 0)
        {
            bbsc = b.Add("BBSC", 0, 48);
            foreach (var (bone, type) in _billboards)
            {
                var w = bbsc.W;
                b.NullRef(bbsc);                                    // dependents
                w.Write((ushort)boneMap[bone]);
                w.Write(type);
                w.Write((byte)0);                                   // camera_look_at
                w.Write(0f); w.Write(0f); w.Write(0f); w.Write(1f); // up
                w.Write(0f); w.Write(0f); w.Write(0f); w.Write(1f); // forward
            }
            bbsc.Count = _billboards.Count;
        }

        // ---- PAR_ particle systems ----
        // Written straight from M3ParticleWriter's byte template so every field this exporter does
        // not understand keeps the default StarCraft II already accepts. Emitters sit on the bone
        // built from their MDX node, which is why parsing them as nodes matters.
        M3Builder.Section? par = null;
        if (_emitters.Count > 0)
        {
            par = b.Add("PAR_", M3ParticleWriter.Version, M3ParticleWriter.Size);
            foreach (var em in _emitters)
            {
                int bone = (uint)em.NodeIndex < (uint)boneMap.Length ? boneMap[em.NodeIndex] : 0;
                par.W.Write(M3ParticleWriter.Build(em.Source, bone, em.MaterialIndex,
                                                   _opt.Scale, _nextAnimId, em.EmitRateAnimId,
                                                   em.RestEmissionRate));
            }
            par.Count = _emitters.Count;
        }

        // ---- MODL V29 ----
        {
            var w = modl.W;
            b.Ref(modl, modlName);
            w.Write(ModelFlags);
            b.Ref(modl, seqs);
            b.Ref(modl, stc);
            b.Ref(modl, stg);
            b.NullRef(modl);
            w.Write(0u);
            b.Ref(modl, sts);
            b.Ref(modl, boneSec);
            w.Write((uint)skinBoneCount);
            w.Write(VertexFlags);
            b.Ref(modl, verts);
            b.Ref(modl, div);
            b.Ref(modl, boneLookup);
            WriteBnds(w, bmin, bmax, boundsRadius);
            WriteBnds(w, default, default, 0);
            for (int i = 0; i < 3; i++) b.NullRef(modl);        // collision refs
            if (att is not null) b.Ref(modl, att); else b.NullRef(modl);
            if (attAddon is not null) b.Ref(modl, attAddon); else b.NullRef(modl);
            b.NullRef(modl);                                    // lights
            b.NullRef(modl);                                    // shadow boxes
            if (cam is not null) b.Ref(modl, cam); else b.NullRef(modl);
            if (camAddon is not null) b.Ref(modl, camAddon); else b.NullRef(modl);
            b.Ref(modl, matm);
            b.Ref(modl, mats);
            for (int i = 0; i < 10; i++) b.NullRef(modl);   // materials_displacement .. materials_lensflare
            // particle_systems is the first of the next 17 refs (through `turrets`).
            if (par is not null) b.Ref(modl, par); else b.NullRef(modl);
            for (int i = 0; i < 16; i++) b.NullRef(modl);
            b.Ref(modl, iref);
            w.Write(new byte[108]);
            for (int i = 0; i < 4; i++) b.NullRef(modl);        // hittests, attachment volumes + addons
            if (bbsc is not null) b.Ref(modl, bbsc); else b.NullRef(modl);
            b.NullRef(modl);                                    // tmd_data
            w.Write(0u);
            b.NullRef(modl);
            modl.Count = 1;
        }

        // ---- bone structs (emitted in parent-first order; si = file index, oi = build index) ----
        for (int si = 0; si < _bones.Count; si++)
        {
            int oi = order[si];
            var bone = _bones[oi];
            var w = boneSec.W;
            w.Write(-1);
            b.Ref(boneSec, boneNames[oi]);
            bool animated = oi < boneAnimated.Length && boneAnimated[oi];
            w.Write(0x2000u | (boneSkinned[si] ? 0x800u : 0u) | (animated ? 0x200u : 0u));
            w.Write((short)(bone.Parent >= 0 ? boneMap[bone.Parent] : -1)); w.Write((ushort)0);
            WriteAnimHeader(w, 1, 6, _boneAnimIds[oi].Loc);
            WriteVec3(w, bone.RestLocation); WriteVec3(w, Vector3.Zero); w.Write(-1);
            WriteAnimHeader(w, 1, 6, _boneAnimIds[oi].Rot);
            var q = bone.RestRotation;
            w.Write(q.X); w.Write(q.Y); w.Write(q.Z); w.Write(q.W);
            w.Write(0f); w.Write(0f); w.Write(0f); w.Write(1f); w.Write(-1);
            WriteAnimHeader(w, 1, 6, _boneAnimIds[oi].Scl);
            WriteVec3(w, Vector3.One); WriteVec3(w, Vector3.One); w.Write(-1);
            // batching: a BAT_ pointing at this bone is switched on and off by this flag.
            var toggled = _materials.Find(m => m.ToggleBone == oi);
            if (toggled is not null)
            {
                WriteAnimHeader(w, 0, 6, toggled.BatchAnimId);
                w.Write(toggled.RestVisible ? 1u : 0u); w.Write(1u); w.Write(-1);
            }
            else
            {
                WriteAnimHeader(w, 0, 0, _nextAnimId());
                w.Write(1u); w.Write(1u); w.Write(-1);
            }
        }
        boneSec.Count = _bones.Count;

        // IREF: inverse of the rest world. Rest world = T(pivot) (rotation only for camera bones).
        for (int si = 0; si < _bones.Count; si++)
        {
            var bone = _bones[order[si]];
            var world = Matrix4x4.CreateFromQuaternion(bone.RestRotation) * Matrix4x4.CreateTranslation(bone.PivotWorld);
            if (!Matrix4x4.Invert(world, out var inv)) inv = Matrix4x4.Identity;
            var w = iref.W;
            w.Write(inv.M11); w.Write(inv.M12); w.Write(inv.M13); w.Write(inv.M14);
            w.Write(inv.M21); w.Write(inv.M22); w.Write(inv.M23); w.Write(inv.M24);
            w.Write(inv.M31); w.Write(inv.M32); w.Write(inv.M33); w.Write(inv.M34);
            w.Write(inv.M41); w.Write(inv.M42); w.Write(inv.M43); w.Write(inv.M44);
        }
        iref.Count = _bones.Count;

        // ---- division ----
        for (int i = 0; i < regions.Count; i++)
        {
            var r = regions[i];
            verts.W.Write(r.VertexBytes);
            foreach (ushort idx in r.Faces) faces.W.Write(idx);

            var w = regn.W;
            w.Write(0u); w.Write(0u);
            w.Write((uint)r.FirstVertex); w.Write((uint)r.VertexCount);
            w.Write((uint)r.FirstFace); w.Write((uint)r.Faces.Length);
            w.Write((ushort)r.Lookup.Count);
            w.Write((ushort)r.FirstLookup); w.Write((ushort)r.Lookup.Count);
            w.Write((ushort)0);
            w.Write((byte)Math.Max(r.LookupsUsed, 1)); w.Write((byte)1);
            w.Write(r.Lookup.Count > 0 ? r.Lookup[0] : (ushort)0);
            w.Write(0u);
            w.Write(16f); w.Write(0f);

            var bw = bat.W;
            bw.Write((ushort)0);
            bw.Write((ushort)0);        // priority_plane: Blizzard always writes 0 (WC3's priority lives in MAT_.priority)
            bw.Write((ushort)i); bw.Write((ushort)0); bw.Write((ushort)0);
            int matIndex = _regions[r.SourceRegion].MaterialIndex;
            bw.Write((ushort)matIndex);
            int toggleBone = _materials[matIndex].ToggleBone;
            bw.Write((short)(toggleBone >= 0 ? boneMap[toggleBone] : -1));
        }
        verts.Count = totalVerts * 32;
        faces.Count = totalFaces;
        regn.Count = regions.Count;
        bat.Count = regions.Count;

        {
            var w = div.W;
            b.Ref(div, faces);
            b.Ref(div, regn);
            b.Ref(div, bat);
            b.Ref(div, msec);
            w.Write(1u);
            div.Count = 1;
        }
        {
            var w = msec.W;
            w.Write(0u);
            WriteAnimHeader(w, 1, 6, BndsAnimId);
            WriteBnds(w, bmin, bmax, boundsRadius);
            WriteBnds(w, default, default, 0);
            w.Write(-1);
            msec.Count = 1;
        }
        foreach (ushort bi in lookupTable) boneLookup.W.Write(bi);
        boneLookup.Count = lookupTable.Count;

        // ---- material structs ----
        for (int i = 0; i < _materials.Count; i++)
        {
            var m = _materials[i];
            matm.W.Write(1u);
            matm.W.Write((uint)i);

            var w = mats.W;
            b.Ref(mats, matNames[i]);
            w.Write(0u);                                        // additional_flags
            // Do NOT add 0x4 (unfogged) here. It is set on 31 of 31 sampled Blizzard unit
            // materials, but those set it *instead of* geometry_visible, never alongside it —
            // and 0x80000000 | 0x4 is what crashed the SC2 editor on every model we exported.
            // Proven by bisection: flags 0x80000000 alone loads, 0x4 alone (the marine) loads,
            // 0x80000004 crashes even with alpha_test zeroed. Each bit is harmless; only the
            // pair is fatal. Nothing about geometry, skeleton, animation or textures was ever
            // involved, so re-adding this bit will silently resurrect a whole-file crash.
            uint flags = 0x80000000u;                           // geometry_visible
            if (m.TwoSided) flags |= 0x8;
            if (m.Unshaded) flags |= 0x10;
            bool cutout = m.Blend == CompositeBlend.AlphaTest;
            bool translucent = m.Blend is CompositeBlend.AlphaBlend or CompositeBlend.Additive;
            if (cutout || translucent) flags |= 0x4000;         // transparent_shadows
            if (translucent) flags |= 0x10000;                  // transparent_depth_effects
            // A glow, a smoke card or a team-colour ground disc has no business casting a shadow.
            // Warcraft III hero models put a large additive quad flat on the ground for the team
            // glow; exported without these bits SC2 shadows the quad, and the unit stands in a
            // solid black square the size of the plane with nothing visible casting it. Blizzard
            // agrees: across 18,305 of their models, no_shadows_cast is set on 95% of alpha-blend,
            // 97% of add and 98% of alpha-add materials, against 16% of opaque ones — so this
            // follows blend mode and deliberately leaves cutouts (blend 0 + alpha test) casting.
            if (translucent) flags |= 0x20 | 0x80;              // no_shadows_cast | no_shadows_receive
            w.Write(flags);
            bool visibilityDriven = m.VisibilityAnim is not null || m.DefaultAlpha < 255;
            // A cutout stays in the *opaque* pass — blend_mode 0 does not ignore the alpha channel,
            // it just does not blend it, and alpha_test_threshold still discards. Blizzard does
            // exactly this (hightemplar: blend 0, alpha_test 20, two_sided). Making cutouts
            // alpha-blended instead drops them out of the depth-writing pass, and then a body
            // sharing that material clips through itself and through everything drawn after it.
            // Additive goes to 3 "Alpha Add", not 2 "Add". SC2's mode 2 has no alpha path at
            // all — across 2,553 Blizzard materials using it, particle and geoset alike, not one
            // carries an alpha layer — so everything drawn with it is fully and permanently
            // opaque. Two bugs fell out of that: Warcraft III sprite sheets keep their shape in
            // alpha over flat white RGB, so a cloud puff drew as a solid coloured square; and an
            // additive card whose GEOA alpha hides it outside one animation could never hide,
            // leaving a spell sparkle burning through every Stand. Mode 3 is the alpha-weighted
            // additive Blizzard actually uses — 93% of their additive geoset materials and 99% of
            // their additive particle materials pair it with an alpha layer, and they reach for it
            // 5.5x more often than mode 2. Where a glow's alpha is solid 255 the two modes render
            // identically, so this costs nothing on art that was already correct.
            w.Write(m.Blend switch                              // blend_mode
            {
                CompositeBlend.AlphaBlend => 1u,
                CompositeBlend.Additive => 3u,
                _ => 0u,
            });
            w.Write(m.Priority);
            w.Write(0u);                                        // rtt_channels
            // Specular exponent. 80 is what Blizzard uses on most opaque unit materials (24 of 49
            // sampled; 20 appears only 9 times) — a low exponent spreads the highlight into a
            // broad sheen across the whole surface instead of a tight glint.
            w.Write(80f);                                       // specularity
            w.Write(0f);                                        // depth_blend_falloff
            // alpha_test_threshold — Blizzard's own range (20 on hightemplar's cloth, 32 on
            // raynor's hair), not the midpoint. Reforged feathers its card edges, and the texels
            // in that feathered band carry the *object's* colour, not black (the grunt's fur reads
            // RGB 53,45,36 below alpha 32 against 116,104,89 opaque), so keeping them costs a
            // slightly thicker edge while discarding them eats the strands and punches holes.
            // A geoset whose GEOA visibility rides the layer's colour alpha (corpses, alternate
            // forms, decay) needs *some* test or it could never hide, but stays low enough to cut
            // nothing while visible.
            w.Write(cutout ? 32u : visibilityDriven ? 8u : 0u);
            w.Write(1f); w.Write(1f); w.Write(1f); w.Write(0f); w.Write(0f);
            for (int L = 0; L < 18; L++)
            {
                var layer = matLayers[i][L];
                if (layer is not null) b.Ref(mats, layer); else b.Ref(mats, nullLayer);
            }
            w.Write(0u);                                        // material_class
            // blend_mode_layer/emis1/emis2. 2 is "Add", the default; 4 is "Team Color Emissive
            // Add", which turns the layer in that slot into the player-colour channel.
            w.Write(2u);
            w.Write(m.TeamBlendSlot == 4 ? 4u : 2u);
            w.Write(m.TeamBlendSlot == 5 ? 4u : 2u);
            w.Write(0u);                                        // spec_mode
            WriteAnimHeader(w, 1, 8, _nextAnimId());
            w.Write(0f); w.Write(0f); w.Write(-1);
            WriteAnimHeader(w, 1, 0, _nextAnimId());
            w.Write(0f); w.Write(0f); w.Write(-1);
            b.NullRef(mats);
        }
        matm.Count = _materials.Count;
        mats.Count = _materials.Count;

        // ---- header ----
        {
            var w = header.W;
            w.Write((uint)(('M' << 24) | ('D' << 16) | ('3' << 8) | '4'));
            w.Write(0u);
            w.Write(0u);
            b.Ref(header, modl);
            header.Count = 1;
        }

        return b.Build();
    }

    private void MarkAnimated(uint animId, bool[] boneAnimated)
    {
        for (int i = 0; i < _boneAnimIds.Length; i++)
            if (_boneAnimIds[i].Loc == animId || _boneAnimIds[i].Rot == animId || _boneAnimIds[i].Scl == animId)
            {
                boneAnimated[i] = true;
                return;
            }
    }

    // ---------------------------------------------------------------- struct helpers

    private static void WriteAnimHeader(BinaryWriter w, ushort interpolation, ushort flags, uint id)
    {
        w.Write(interpolation); w.Write(flags); w.Write(id);
    }

    private static void WriteFloatAnimRef(BinaryWriter w, float value, uint id)
    {
        WriteAnimHeader(w, 1, 0, id);
        w.Write(value); w.Write(0f); w.Write(-1);
    }

    private static void WriteVec3(BinaryWriter w, Vector3 v)
    {
        w.Write(v.X); w.Write(v.Y); w.Write(v.Z);
    }

    private static void WriteBnds(BinaryWriter w, Vector3 min, Vector3 max, float radius)
    {
        WriteVec3(w, min); WriteVec3(w, max); w.Write(radius);
    }

    /// <summary>
    /// <c>color_channels</c> values, in the order m3studio's <c>material_layer_channel</c> enum
    /// declares them. On a V20 diffuse, ARGB means the alpha is a team-colour mask (neutral
    /// renders it white) — never use it for coverage alpha. A-only is what the alpha1 mask layer
    /// uses; <c>alpha_test_threshold</c> reads that layer, not the diffuse.
    /// </summary>
    private const uint ChannelsRgb = 0, ChannelsArgb = 1, ChannelsAlphaOnly = 2;

    /// <summary>LAYR V26 with a bitmap path — the m3studio defaults, uv-wrapped.</summary>
    /// <param name="defaultAlpha">
    /// Alpha of the layer's colour_value default. Sequences that carry no visibility key fall back
    /// to it, so a geoset hidden at rest (corpses, alternate forms) must default to 0.
    /// </param>
    /// <param name="colorChannels">Which texture channels the engine samples — see the constants above.</param>
    private M3Builder.Section WriteLayer(M3Builder b, string bitmapPath, uint colorAnimId, bool wrap,
                                         byte defaultAlpha = 255, uint colorChannels = ChannelsRgb,
                                         bool flipbook = false)
    {
        var layer = b.Add("LAYR", 26, 464);
        var w = layer.W;
        var pathSec = bitmapPath.Length > 0 ? b.AddString(bitmapPath) : null;

        w.Write(0u);
        if (pathSec is not null) b.Ref(layer, pathSec); else b.NullRef(layer);
        WriteAnimHeader(w, 1, 6, colorAnimId);                  // color_value — GEOA rides this id
        w.Write(0x00FFFFFFu | ((uint)defaultAlpha << 24)); w.Write(0u); w.Write(-1);
        // 0x100 particle_uv_flipbook makes a particle sample ONE cell of a sprite sheet. Without
        // it SC2 maps the whole sheet onto every quad, so Warcraft III's 8x8 cloud atlas draws as a
        // grid of 64 little puffs on each particle. Blizzard sets it on 82% of the materials behind
        // a multi-cell emitter and on only 6% of those behind a single-cell one.
        w.Write((wrap ? 204u : 192u) | (flipbook ? 0x100u : 0u));  // uv_wrap_x/y | color_add | color_mult
        w.Write(0u);                                            // uv_source (UV0)
        w.Write(colorChannels);
        WriteAnimHeader(w, 1, 0, _nextAnimId());
        w.Write(1f); w.Write(1f); w.Write(-1);
        WriteAnimHeader(w, 1, 0, _nextAnimId());
        w.Write(0f); w.Write(0f); w.Write(-1);
        w.Write(0u);
        w.Write(0.8f); w.Write(0.5f);
        w.Write(-1);
        w.Write(0u); w.Write(0u); w.Write(0); w.Write(0u); w.Write(0u);
        WriteAnimHeader(w, 0, 0, _nextAnimId());
        w.Write(0u); w.Write(0u); w.Write(-1);
        WriteAnimHeader(w, 1, 0, _nextAnimId());
        w.Write(0u); w.Write(0u); w.Write(-1);
        w.Write(0u); w.Write(0u);
        WriteAnimHeader(w, 0, 0, _nextAnimId());
        w.Write((ushort)0); w.Write((ushort)0); w.Write(-1);
        WriteAnimHeader(w, 1, 6, _nextAnimId());
        w.Write(0f); w.Write(0f); w.Write(0f); w.Write(0f); w.Write(-1);
        WriteAnimHeader(w, 1, 6, _nextAnimId());
        WriteVec3(w, Vector3.Zero); WriteVec3(w, Vector3.Zero); w.Write(-1);
        WriteAnimHeader(w, 1, 6, _nextAnimId());
        w.Write(1f); w.Write(1f); w.Write(1f); w.Write(1f); w.Write(-1);
        WriteAnimHeader(w, 1, 0, _nextAnimId());
        w.Write(0f); w.Write(0f); w.Write(-1);
        WriteAnimHeader(w, 1, 0, _nextAnimId());
        w.Write(1f); w.Write(0f); w.Write(-1);
        WriteAnimHeader(w, 1, 0, _nextAnimId());
        w.Write(1f); w.Write(1f); w.Write(-1);
        WriteAnimHeader(w, 1, 6, _nextAnimId());
        WriteVec3(w, Vector3.Zero); WriteVec3(w, Vector3.Zero); w.Write(-1);
        WriteAnimHeader(w, 1, 6, _nextAnimId());
        WriteVec3(w, Vector3.One); WriteVec3(w, Vector3.One); w.Write(-1);
        w.Write(-1);
        w.Write(0u);
        w.Write(4f); w.Write(0f); w.Write(1f);
        WriteVec3(w, Vector3.Zero);
        WriteVec3(w, Vector3.One);
        w.Write(0f); w.Write(0f);
        layer.Count = 1;
        return layer;
    }

    private static void WriteNullLayer(BinaryWriter w)
    {
        var bytes = new byte[464];
        void F(int off, float v) => BitConverter.TryWriteBytes(bytes.AsSpan(off), v);
        void I(int off, int v) => BitConverter.TryWriteBytes(bytes.AsSpan(off), v);
        I(36, 236);
        F(48 + 12, 1f);
        F(92, 0.8f); F(96, 0.5f);
        I(100, -1);
        F(252 + 16, 1f); F(252 + 20, 1f);
        F(320 + 12, 1f);
        I(412, -1);
        F(420, 4f);
        F(428, 1f);
        w.Write(bytes);
    }

    // ---------------------------------------------------------------- MD34 container

    /// <summary>
    /// Ordered section list with deferred reference patching — the exact container scheme the
    /// sibling Diablo III exporter ships, whose files the SC2 editor accepts. Every section is
    /// written followed by (length % 16) bytes of 0xAA, then the 16-byte index entries with the
    /// tag reversed ("MODL" appears as "LDOM").
    /// </summary>
    private sealed class M3Builder
    {
        internal sealed class Section
        {
            public string Tag = "";
            public uint Version;
            public int EntrySize;
            public int Count;
            public int Index;
            public readonly MemoryStream Ms = new();
            public BinaryWriter W = null!;
        }

        private readonly List<Section> _sections = [];
        private readonly List<(Section Owner, long Offset, Section Target)> _refs = [];

        public Section Add(string tag, uint version, int entrySize)
        {
            var s = new Section { Tag = tag, Version = version, EntrySize = entrySize };
            s.W = new BinaryWriter(s.Ms);
            _sections.Add(s);
            return s;
        }

        public Section AddString(string value)
        {
            var s = Add("CHAR", 0, 1);
            var bytes = Encoding.UTF8.GetBytes(value);
            s.W.Write(bytes);
            s.W.Write((byte)0);
            s.Count = bytes.Length + 1;
            return s;
        }

        public Section AddU32s(IReadOnlyList<uint> values)
        {
            var s = Add("U32_", 0, 4);
            foreach (uint v in values) s.W.Write(v);
            s.Count = values.Count;
            return s;
        }

        public Section AddI32s(IReadOnlyList<int> values)
        {
            var s = Add("I32_", 0, 4);
            foreach (int v in values) s.W.Write(v);
            s.Count = values.Count;
            return s;
        }

        public void Ref(Section owner, Section target)
        {
            _refs.Add((owner, owner.Ms.Position, target));
            owner.W.Write(0u); owner.W.Write(0u); owner.W.Write(0u);
        }

        public void NullRef(Section owner)
        {
            owner.W.Write(0u); owner.W.Write(0u); owner.W.Write(0u);
        }

        public byte[] Build()
        {
            foreach (var s in _sections)
                if (s.Ms.Length != (long)s.Count * s.EntrySize)
                    throw new InvalidOperationException($"section {s.Tag}: {s.Ms.Length} bytes != {s.Count} x {s.EntrySize}");

            for (int i = 0; i < _sections.Count; i++) _sections[i].Index = i;
            foreach (var (owner, offset, target) in _refs)
            {
                owner.Ms.Position = offset;
                owner.W.Write((uint)target.Count);
                owner.W.Write((uint)target.Index);
                owner.Ms.Position = owner.Ms.Length;
            }

            uint indexOffset = 0;
            var offsets = new uint[_sections.Count];
            for (int i = 0; i < _sections.Count; i++)
            {
                offsets[i] = indexOffset;
                long len = _sections[i].Ms.Length;
                indexOffset += (uint)(len + len % 16);
            }

            var headerSec = _sections[0];
            headerSec.Ms.Position = 4;
            headerSec.W.Write(indexOffset);
            headerSec.W.Write((uint)_sections.Count);
            headerSec.Ms.Position = headerSec.Ms.Length;

            var outMs = new MemoryStream();
            var ow = new BinaryWriter(outMs);
            foreach (var s in _sections)
            {
                var payload = s.Ms.ToArray();
                ow.Write(payload);
                for (int p = 0; p < payload.Length % 16; p++) ow.Write((byte)0xAA);
            }
            for (int i = 0; i < _sections.Count; i++)
            {
                var s = _sections[i];
                ow.Write((uint)(s.Tag[0] << 24 | s.Tag[1] << 16 | s.Tag[2] << 8 | s.Tag[3]));
                ow.Write(offsets[i]);
                ow.Write((uint)s.Count);
                ow.Write(s.Version);
            }
            return outMs.ToArray();
        }
    }
}
