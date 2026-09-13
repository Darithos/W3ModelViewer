namespace Wc3ModelViewer.Core.Formats;

/// <summary>
/// The replaceable texture IDs Warcraft III fills in from the map's tileset — cliffs and the
/// tileset trees — resolved to the Lordaeron Summer art so a model that uses them has something to
/// show outside a map.
/// </summary>
/// <remarks>
/// A TEXS entry with a replaceable ID carries no path: the engine binds the tileset's cliff or tree
/// texture at load time (ID 11 the cliff, 31–37 one tree family each). Team colour and glow (1, 2)
/// are generated and stay with <see cref="Casc.Wc3TextureCache"/>; everything else here is a real
/// file in the archive whose name only the tileset decides. Without a map there is no tileset, so
/// every cliff piece and every tileset tree rendered as the magenta placeholder.
/// <para>
/// HD models bind one replaceable entry per PBR slot — the Lordaeron tree's TEXS 0, 1, 2 are all
/// ID 31, slotted as diffuse, normal and ORM — and the archive stores each map as its own file
/// (<c>LordaeronSummerTree_Diffuse.dds</c>, <c>_Normal</c>, <c>_ORM</c>). The slot each entry is
/// bound to therefore picks the suffix. Classic models have one texture per entry and no suffix.
/// The result is an ordinary reference, so the texture panel lists it and the user can remap it to
/// another season or tileset by hand.
/// </para>
/// </remarks>
public static class Wc3ReplaceableTextures
{
    /// <summary>Default art for each tileset-bound replaceable ID, without extension or PBR suffix.</summary>
    private static readonly Dictionary<uint, string> Defaults = new()
    {
        [11] = @"ReplaceableTextures\Cliff\Cliff1",
        [31] = @"ReplaceableTextures\LordaeronTree\LordaeronSummerTree",
        [32] = @"ReplaceableTextures\AshenvaleTree\AshenTree",
        [33] = @"ReplaceableTextures\BarrensTree\BarrensTree",
        [34] = @"ReplaceableTextures\NorthrendTree\NorthTree",
        [35] = @"ReplaceableTextures\Mushroom\MushroomTree",
        [36] = @"ReplaceableTextures\RuinsTree\RuinsTree",
        [37] = @"ReplaceableTextures\OutlandMushroomTree\MushroomTree",
    };

    /// <summary>True for an ID this class can stand in for.</summary>
    public static bool IsTilesetBound(uint replaceableId) => Defaults.ContainsKey(replaceableId);

    /// <summary>
    /// Gives every tileset-bound replaceable entry in <paramref name="model"/> a default file name.
    /// Entries that already name a file, and the engine-generated team slots, are left alone.
    /// </summary>
    public static void Substitute(MdxModel model)
    {
        bool hd = model.Materials.Any(m => m.Layers.Any(l => l.IsPbr));
        for (int i = 0; i < model.Textures.Count; i++)
        {
            var tex = model.Textures[i];
            if (tex.FileName.Length > 0 || !Defaults.TryGetValue(tex.ReplaceableId, out string? basePath)) continue;

            string suffix = hd
                ? SlotOf(model, i) switch
                {
                    MdxTextureSlot.Normal => "_Normal",
                    MdxTextureSlot.Orm => "_ORM",
                    MdxTextureSlot.Emissive => "_Emissive",
                    _ => "_Diffuse",
                }
                : "";
            model.Textures[i] = new MdxTexture
            {
                ReplaceableId = tex.ReplaceableId,
                FileName = basePath + suffix + (hd ? ".dds" : ".blp"),
                Flags = tex.Flags,
            };
        }
    }

    /// <summary>The first PBR slot any layer binds TEXS entry <paramref name="textureIndex"/> to.</summary>
    private static MdxTextureSlot? SlotOf(MdxModel model, int textureIndex)
    {
        foreach (var slot in new[] { MdxTextureSlot.Diffuse, MdxTextureSlot.Normal, MdxTextureSlot.Orm, MdxTextureSlot.Emissive })
            foreach (var material in model.Materials)
                foreach (var layer in material.Layers)
                    if (layer.Slot(slot) == textureIndex) return slot;
        return null;
    }
}
