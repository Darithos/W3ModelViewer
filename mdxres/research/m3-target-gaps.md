# Gap analysis: making the existing D3ModelViewer M3Writer (MD34/MODL v29) a good export target for Warcraft III (classic + Reforged) models

## [certain] The existing writer's MODL V29 field layout is byte-exact correct against both m3addon and m3studio; the null slots it writes are attachments@228, attachment_addon@240, lights@252, shadow_boxes@264, cameras@276, cameras_addon@288 — these are the exact holes to fill.

MODL V29 = 856 bytes. Verified offset map (Reference = 12 bytes {entries u32, index u32, flags u32}; BNDSV0 = 28 bytes {min VEC3, max VEC3, radius f32}; SSGSV1 = 108 bytes):

  0 model_name Ref->CHAR
 12 flags u32                    (writer: 0x00180D53)
 16 sequences Ref->SEQS
 28 sequence_transformation_collections Ref->STC_
 40 sequence_transformation_groups Ref->STG_
 52 bone_anim_sets Ref->BSET     (writer: null)
 64 split_count u32              (writer: 0)
 68 sts Ref->STS_
 80 bones Ref->BONE
 92 skin_bone_count u32
 96 vertex_flags u32             (writer: 0x0182007D)
100 vertices Ref->U8__
112 divisions Ref->DIV_
124 bone_lookup Ref->U16_
136 boundings BNDSV0 (28)
164 collision_boundings BNDSV0 (28)
192 collision_faces Ref->U16_
204 collision_verts Ref->VEC3
216 collision_face_normals Ref->VEC3
228 attachment_points Ref->ATT_          <-- ADD
240 attachment_points_addon Ref->U16_    <-- ADD (one 0xFFFF per attachment)
252 lights Ref->LITE
264 shadow_boxes Ref->SHBX
276 cameras Ref->CAM_                    <-- ADD (portraits)
288 cameras_addon Ref->U16_              <-- ADD (one 0xFFFF per camera)
300 material_references Ref->MATM
312 materials_standard Ref->MAT_
324 materials_displacement / 336 composite / 348 terrain / 360 volume / 372 hair / 384 creep / 396 volumenoise / 408 splatterrainbake / 420 reflection / 432 lensflare   (10 nulls; writer's `for(10) NullRef`)
444 particle_systems Ref->PAR_           <-- optional
456 particle_copies / 468 ribbons Ref->RIB_ / 480 projections / 492 forces / 504 warps / 516 view_volumes / 528 physics_rigidbodies / 540 physics_constraints / 552 physics_joints / 564 physics_cloths / 576 ik_two_joints / 588 ik_ccd / 600 ik_joints / 612 one_bone_solvers / 624 turret_parts / 636 turrets   (17 refs; writer's `for(17) NullRef`)
648 bone_rests Ref->IREF
660 hittest_tight SSGSV1 (108 bytes, writer emits zeros)
768 hittests Ref->SSGS                   <-- ADD (WC3 CLID/collision shapes)
780 attachment_volumes Ref->ATVL         <-- optional
792 attachment_volumes_addon0 Ref->U16_
804 attachment_volumes_addon1 Ref->U16_
816 billboards Ref->BBSC                 <-- ADD (WC3 billboarded bones)
828 tmd_data Ref->TMD_
840 m3a_hash u32
844 m3a_hashes Ref->U32_
=856

MODL.flags bits (m3studio names): 0x1 e_mdfTangents, 0x2 e_mdfBonesFixed, 0x4 e_mdfUVDensitiesComputed, 0x8 e_mdfRelativeBounds, 0x10 e_mdfSectionBoundsFixed, 0x20 e_mdfTrackSetsComputed, 0x40 e_mdfTrackCollectionSorted, 0x80 e_mdModelAcceptsSplats, 0x800 e_mdTrackAnimatedBaseFlagValid, 0x1000 e_mdFileDirty, 0x4000 e_mdFOWDoNotUseTint, 0x8000 e_mdInstancedVB, 0x10000 e_mdForceSampledFOW, 0x20000 e_mdInstancedModel, 0x40000 e_mdNeverUseFOW, 0x80000 e_mdBoneAnimatedFlagSolved, 0x100000 e_mdAllowLocalLightShadows, 0x200000 e_mdAvoidSampledFOW. m3addon documents 0x100000 as 'hasMesh'; the two disagree — trust m3studio (its names come from Blizzard's own enum strings) but keep the bit set either way since 0x00180D53 sets it.

VERTEX FLAGS (MODL.vertex_flags) bits: 0x1 pos(12B), 0x20 skin0(2 lookup/weight pairs), 0x40 skin1(2 more pairs), 0x200 color(COL 4B), 0x2000..0x10000 fuv0..fuv3 (float vec2 UVs), 0x20000 uv0, 0x40000 uv1, 0x80000 uv2, 0x100000 uv3, 0x40000000 uv4 (each int16 vec2, 4B), 0x800000 normal (4B packed+sign), 0x1000000 tangent (4B packed). The writer's 0x0182007D = base 0x1D | pos | skin0 | skin1 | uv0 | normal | tangent = 12+4+4+4+4+4 = 32 bytes. For WC3 you need exactly this; add 0x40000 (uv1, +4 bytes, vertex becomes 36) only if a WC3 layer uses CoordId=1.

_evidence: https://github.com/Solstice245/m3studio/blob/main/structures.xml (MODL, lines ~2470-2612; vertex_flags bits ~2520-2556); https://github.com/SC2Mapster/m3addon/blob/master/structures.xml (MODL, lines 2835-2942); cross-checked against C:\Projects\D3 Model Viewer\src\D3ModelViewer.Core\Formats\M3Writer.cs:276-306_

## [certain] MAT_ V20 is 352 bytes with this exact field/offset map; everything WC3 needs (additive, alpha-test, two-sided, unlit, no-depth, priority) is expressible.

MAT_ V20 (352 bytes), offsets from struct start:

  0 name Ref->CHAR
 12 additional_flags u32
 16 flags u32
 20 blend_mode u32
 24 priority i32
 28 rtt_channels_used u32
 32 specularity f32
 36 depth_blend_falloff f32
 40 alpha_test_threshold u32  (m3addon calls it cutoutThresh u8 + 3 pad bytes; same 4 bytes — write 0..255 as a u32, low byte is what matters)
 44 hdr_spec f32   (m3addon: specMult, default 1.0)
 48 hdr_emis f32   (m3addon: emisMult, default 1.0)
 52 hdr_envi_const f32 (v20+, default 1.0)
 56 hdr_envi_diff  f32 (v20+, default 0.0)
 60 hdr_envi_spec  f32 (v20+, default 0.0)
 64 layer_diff Ref->LAYR ... 18 layer refs, 12 bytes each, 64..280 (see separate finding)
280 material_class u32  (m3addon: unknown2481ae8a; 0 = normal)
284 blend_mode_layer u32
288 blend_mode_emis1 u32
292 blend_mode_emis2 u32
296 spec_mode u32
300 parallax_height FloatAnimationReferenceV0 (20 bytes)
320 motion_blur FloatAnimationReferenceV0 (20 bytes) (m3addon calls it unknownAnimationRef2 / UInt32AnimationReference — same 20 bytes)
340 normal_blend_mask_factor Ref->SR32 (v19+)
=352

MAT_.additional_flags bits: 0x1 depth_blend_falloff-in-use (set whenever depth_blend_falloff != 0), 0x4 makes_use_of_vertex_color, 0x8 makes_use_of_vertex_alpha, 0x200 unknown.

MAT_.flags bits (m3studio / m3addon names, identical values):
0x00000001 vertex_color        (must also set additional_flags 0x4 and MODL.vertex_flags 0x200)
0x00000002 vertex_alpha        (must also set additional_flags 0x8 and MODL.vertex_flags 0x200)
0x00000004 unfogged            <-- WC3 layer flag 0x20 Unfogged
0x00000008 two_sided           <-- WC3 layer flag 0x10 TwoSided (cull off)
0x00000010 unshaded            <-- WC3 layer flag 0x1 Unshaded (full-bright)
0x00000020 no_shadows_cast
0x00000040 no_hittest
0x00000080 no_shadows_receive
0x00000100 depth_prepass       (Z-fill pass)
0x00000200 terrain_hdr
0x00000400 unknown0x400
0x00000800 simulate_roughness  (formerly splat_uv_fix)
0x00001000 pixel_forward_lighting (formerly soft_blending)
0x00002000 depth_fog
0x00004000 transparent_shadows (formerly for_particles)
0x00008000 decal_lighting
0x00010000 transparent_depth_effects (formerly 'transparency')
0x00020000 transparent_local_lights
0x00040000 disable_soft
0x00080000 double_lambert (formerly dark_normal_mapping)
0x00100000 hair_layer_sorting
0x00200000 accept_splats
0x00400000 decal_low_required
0x00800000 emis_low_required
0x01000000 spec_low_required
0x02000000 accept_splats_only
0x04000000 background_object
0x08000000 unknown0x8000000
0x10000000 depth_prepass_low_required
0x20000000 no_highlighting
0x40000000 clamp_output
0x80000000 geometry_visible    (v17+; MUST be set or the mesh is invisible — the writer already sets it)

MAT_.blend_mode enum (uint32): 0 Opaque, 1 Alpha Blend, 2 Add, 3 Alpha Add, 4 Mod, 5 Mod 2x, 6/7 unknown.
MAT_.blend_mode_layer / blend_mode_emis1 / blend_mode_emis2 enum: 0 Mod, 1 Mod 2x, 2 Add (default), 3 Blend, 4 Team Color Emissive Add, 5 Team Color Diffuse Add.
MAT_.spec_mode enum: 0 RGB, 1 Alpha Only.
MAT_.priority: int32; SC2 sorts transparent materials high->low within a model. This is where WC3 MTLS.PriorityPlane goes (also see BAT_.priority_plane).

WC3 -> m3 RECIPE TABLE (WC3 layer FilterMode / ShadingFlags -> MAT_ v20):
  FilterMode 0 None/Opaque   -> blend_mode=0, alpha_test_threshold=0
  FilterMode 1 Transparent   -> blend_mode=0, alpha_test_threshold=192 (WC3 hard-cuts at alpha 0.75 => 0.75*255 = 191; use 192). Do NOT use blend_mode=1 here or you lose depth-writes.
  FilterMode 2 Blend         -> blend_mode=1 (Alpha Blend)
  FilterMode 3 Additive      -> blend_mode=2 (Add)
  FilterMode 4 AddAlpha      -> blend_mode=3 (Alpha Add)
  FilterMode 5 Modulate      -> blend_mode=4 (Mod)
  FilterMode 6 Modulate2x    -> blend_mode=5 (Mod 2x)
  ShadingFlags 0x001 Unshaded     -> flags |= 0x10 (unshaded); also set layer_emis1 = the diffuse layer with blend_mode_emis1=3 (Blend) if you want it to actually glow rather than merely be unlit.
  ShadingFlags 0x002 SphereEnvMap -> put the texture in layer_envi (offset 136) with LAYR.uv_source = 3 (Ref Spherical Env) or 8 (Spherical Environment).
  ShadingFlags 0x004 WrapWidth    -> LAYR.flags 0x4 uv_wrap_x
  ShadingFlags 0x008 WrapHeight   -> LAYR.flags 0x8 uv_wrap_y
  ShadingFlags 0x010 TwoSided     -> flags |= 0x8 (two_sided)
  ShadingFlags 0x020 Unfogged     -> flags |= 0x4 (unfogged)
  ShadingFlags 0x040 NoDepthTest  -> no exact m3 equivalent. Best approximation: flags |= 0x10000 (transparent_depth_effects) OFF and rely on blend_mode + high MAT_.priority so it draws last. There is no 'disable depth test' bit in MAT_ v20.
  ShadingFlags 0x080 NoDepthSet   -> use blend_mode 1/2/3 (any transparent blend mode already disables depth-write in SC2) and DO NOT set flags 0x100 depth_prepass.
  ShadingFlags 0x100 Unlit (Reforged 'no fallback') -> ignore.
  MTLS.PriorityPlane           -> MAT_.priority (int32) AND BAT_.priority_plane (uint16).
  MTLS.Flags 0x8 SortPrimitivesFarZ / 0x10 NearZ -> nothing in m3; nudge MAT_.priority instead.

IMPORTANT CAVEAT on depth flags: there is genuine disagreement in the WC3 sources. TaylorMouse's Write.ms (line 261-262) writes 0x40=nodepthset / 0x80=nodepthtest, while his own Build.ms (line 119-120) reads 0x40=nodepthtest / 0x80=nodepthset. mdx-m3-viewer's layer.ts and the Hive MDX spec both say 0x40 = NoDepthTest, 0x80 = NoDepthSet. Trust mdx-m3-viewer/Build.ms; Write.ms has the bug.

_evidence: https://github.com/Solstice245/m3studio/blob/main/structures.xml lines 1096-1208 (MAT_); https://github.com/SC2Mapster/m3addon/blob/master/structures.xml lines 1521-1635; blend enums at https://github.com/SC2Mapster/m3addon/blob/master/__init__.py lines 1116-1139 and https://github.com/Solstice245/m3studio/blob/main/bl_enum.py lines 632-644; WC3 side: mdx-m3-viewer src/parsers/mdlx/layer.ts; C:\Program Files\Autodesk\3ds Max 2016\scripts\Startup\Warcraft_3_Reforged_Tools\GriffonStudios_Warcraft_3_Reforged_Plugins_Material.ms:90 and ...Build.ms:113-121 and ...Write.ms:255-263_

## [certain] The 18 LAYR reference slots in MAT_ V20 are, in file order: diff, decal, spec, gloss, emis1, emis2, envi(evio), envi_mask, alpha1(alphaMask), alpha2, norm, height, light(lightmap), ao, norm_blend1_mask, norm_blend2_mask, norm_blend1, norm_blend2.

Byte offsets inside MAT_ v20 (each 12-byte Reference->LAYR):

 slot  offset  m3studio name          m3addon name                use for WC3
  0     64     layer_diff             diffuseLayer                WC3 SD texture / Reforged _diffuse
  1     76     layer_decal            decalLayer                  (unused for WC3)
  2     88     layer_spec             specularLayer               Reforged ORM (B channel) -> optional
  3    100     layer_gloss            glossLayer   (v16+)         Reforged ORM (G=roughness) -> optional
  4    112     layer_emis1            emissiveLayer               Reforged _emissive; ALSO the WC3 'Unshaded' trick; ALSO team-color emissive
  5    124     layer_emis2            emissive2Layer              second emissive / team glow
  6    136     layer_envi             evioLayer                   WC3 SphereEnvMap (ShadingFlags 0x2)
  7    148     layer_envi_mask        evioMaskLayer               env mask
  8    160     layer_alpha1           alphaMaskLayer              *** WC3 ALPHA MAPPING (see detail below)
  9    172     layer_alpha2           alphaMask2Layer             second alpha mask
 10    184     layer_norm             normalLayer                 Reforged _normal
 11    196     layer_height           heightLayer                 parallax; unused
 12    208     layer_light            lightMapLayer               unused
 13    220     layer_ao               ambientOcclussionLayer      Reforged ORM (R channel)
 14    232     layer_norm_blend1_mask normalBlendMask1  (v19+)    unused
 15    244     layer_norm_blend2_mask normalBlendMask2  (v19+)    unused
 16    256     layer_norm_blend1      normalBlendNormal1 (v19+)   unused
 17    268     layer_norm_blend2      normalBlendNormal2 (v19+)   unused

All 18 slots must be present and each must be a valid Reference. The current writer's trick of pointing all 17 unused slots at one shared 'null LAYR' section is exactly what m3studio does and is correct — do not emit 0/0/0 references for them.

*** THE WC3 ALPHA MAPPING ANSWER (the user's reported pain point):
SC2's standard material does NOT take opacity from the diffuse layer's alpha by default. If your WC3 texture carries alpha in the diffuse map you must ALSO wire it into layer_alpha1:
  - layer_alpha1 = a second LAYR that points at the SAME image path as layer_diff,
  - with LAYR.color_channels = 2 (Alpha Only)  [enum: 0 RGB, 1 RGBA, 2 Alpha Only, 3 Red, 4 Green, 5 Blue],
  - MAT_.blend_mode = 1 (Alpha Blend) or alpha_test_threshold != 0 for cutout.
For Reforged, the diffuse .dds normally has no useful alpha; opacity comes from the material-level Alpha float and its KMTA track. Map WC3 layer.Alpha (and its KMTA animation) onto LAYR.color_value.a of the diffuse layer (with LAYR.flags 0x400 'color' set) or, better, animate it via an SDCC track on the diffuse layer's color_value (see the animation finding).

_evidence: https://github.com/Solstice245/m3studio/blob/main/structures.xml lines 1180-1197; https://github.com/SC2Mapster/m3addon/blob/master/structures.xml lines 1607-1624; colorChannelSetting enum documented at m3addon structures.xml lines 1470-1479 and cm/material.py lines 70-77_

## [certain] LAYR V26 is 464 bytes; the flag bits the writer needs are uv_wrap_x 0x4, uv_wrap_y 0x8, color_invert 0x10, color_clamp 0x20, color_add 0x40, color_mult 0x80, particle_uv_flipbook 0x100, video 0x200, color 0x400, fresnel bits 0x2000..0x20000. There is NO team_color bit in LAYR — team color lives in MAT_.blend_mode_layer/emis.

LAYR V26 (464 bytes) offset map (AnimationReferenceHeader = 8 bytes {u16 interpolation, u16 flags, u32 id}; FloatAnimationReferenceV0 = 20; Vector2AnimationReferenceV0 = 28; Vector3AnimationReferenceV0 = 36; ColorAnimationReferenceV0 = 20; UInt16AnimationReferenceV0 = 16; UInt32/Flag AnimationReferenceV0 = 20):

  0 id u32 (0)
  4 color_bitmap Ref->CHAR      (the texture path string, e.g. "Assets\\Textures\\foo.dds")
 16 color_value ColorAnimationReferenceV0 (20)  [header8 + COL(bgra) + COL(bgra) + i32]
 36 flags u32 (default 0xEC)
 40 uv_source u32
 44 color_channels u32          (0 RGB, 1 RGBA, 2 Alpha Only, 3 Red, 4 Green, 5 Blue)
 48 color_multiply FloatAnimRefV0 (20)   (m3addon: brightMult)
 68 color_add FloatAnimRefV0 (20)        (m3addon: midtoneOffset)
 88 noise_type u32 (0; must be 2 for volume-noise materials)
 92 noise_amplitude f32 (0.8, v24+)
 96 noise_frequency f32 (0.5, v24+)
100 video_channel i32 (-1)
104 video_frame_rate u32
108 video_frame_start u32
112 video_frame_end i32
116 video_mode u32 (0 loop, 1 hold)
120 video_sync_timing u32
124 video_play UInt32AnimRefV0 (20)
144 video_restart FlagAnimRefV0 (20)
164 uv_flipbook_rows u32
168 uv_flipbook_cols u32
172 uv_flipbook_frame UInt16AnimRefV0 (16)
188 uv_offset Vector2AnimRefV0 (28)   <-- WC3 TXAN translation goes here (SD2V track)
216 uv_angle Vector3AnimRefV0 (36)    <-- WC3 TXAN rotation (SD3V track)
252 uv_tiling Vector2AnimRefV0 (28)   <-- WC3 TXAN scale (SD2V track)
280 uv_w_translation FloatAnimRefV0 (20)
300 uv_w_scale FloatAnimRefV0 (20)
320 color_brightness FloatAnimRefV0 (20)
340 uv_triplanar_offset Vector3AnimRefV0 (36) (v23+)
376 uv_triplanar_scale Vector3AnimRefV0 (36) (v23+)
412 uv_source_related i32 (-1)
416 fresnel_type u32 (0 disabled, 1 enabled, 2 enabled-inverted)
420 fresnel_exponent f32 (4.0)
424 fresnel_min f32 (0.0)
428 fresnel_max_offset f32 (1.0)   [fresnel_max = min + max_offset]
432 fresnel_translation VEC3 (12) (v25+)
444 fresnel_invert_mask VEC3 (12) (v25+)  (default 1,1,1)
456 fresnel_yaw f32 (v25+)
460 fresnel_pitch f32 (v25+)
=464

This map is byte-exact with the writer's WriteNullLayer offsets (I(36,236) flags, F(60,1) color_multiply.null, F(92,.8)/F(96,.5) noise, I(100,-1) video_channel, F(268,1)/F(272,1) uv_tiling, F(332,1) color_brightness, I(412,-1), F(420,4), F(428,1)) — the writer is already correct here.

LAYR.flags bits (m3studio names / m3addon names):
0x00004 uv_wrap_x       / textureWrapX
0x00008 uv_wrap_y       / textureWrapY
0x00010 color_invert    / invertColor
0x00020 color_clamp     / clampColor
0x00040 color_add       (enables the color_add / midtoneOffset animatable)
0x00080 color_mult      (enables the color_multiply / brightMult animatable)
0x00100 particle_uv_flipbook
0x00200 video           (path is an .ogv)
0x00400 color           / colorEnabled — USE THE color_value CONSTANT INSTEAD OF A BITMAP
0x02000 unknown2000
0x04000 fresnel_transform     / ignoredFresnelFlag1
0x08000 fresnel_normalize     / ignoredFresnelFlag2
0x10000 fresnel_local_transform
0x20000 fresnel_do_not_mirror
Struct default is 0xEC = uv_wrap_x|uv_wrap_y|color_clamp|color_add|color_mult. The writer emits 0xCC (drops color_clamp) — that is m3studio's bitmap-layer default and is fine.

There is NO 'alpha_only' / 'color_only' / 'team_color_diffuse' / 'team_color_add' bit in LAYR v26 in either structures.xml. Those names in the task brief conflate three separate mechanisms:
  - 'alpha only' / 'color only'  -> LAYR.color_channels (2 = Alpha Only, 0 = RGB)
  - 'team color diffuse/add'     -> MAT_.blend_mode_layer = 5 / MAT_.blend_mode_emis1 = 4
  - 'uv wrap'                    -> LAYR.flags 0x4 / 0x8

COL byte order in the file is B,G,R,A (structures.xml COL v0). The writer's 0xFFFFFFFF white is order-agnostic, but any WC3 vertex/team color you write must be byte-swapped to BGRA.

_evidence: https://github.com/Solstice245/m3studio/blob/main/structures.xml lines 1013-1083 (LAYR) and 105-116 (COL); https://github.com/SC2Mapster/m3addon/blob/master/structures.xml lines 1440-1519; verified against C:\Projects\D3 Model Viewer\src\D3ModelViewer.Core\Formats\M3Writer.cs:737-803_

## [likely] SC2 team color is a MATERIAL-level blend mode, not a layer flag: set MAT_.blend_mode_layer = 5 (Team Color Diffuse Add) or MAT_.blend_mode_emis1 = 4 (Team Color Emissive Add), with the team mask supplied as the alpha (or a single color channel) of the diffuse / emissive layer.

Enum matLayerAndEmisBlendModeList (applies identically to MAT_.blend_mode_layer @284, blend_mode_emis1 @288, blend_mode_emis2 @292):
  0 Mod
  1 Mod 2x
  2 Add            (struct default; what the writer currently emits for all three)
  3 Blend
  4 Team Color Emissive Add   — 'Render output is team colored from the alpha channel (or single color channel)'
  5 Team Color Diffuse Add

RECIPE: WC3 replaceable texture -> SC2 team color
WC3 side: a layer whose TextureId points at a TEXS entry with ReplaceableId 1 (Team Color, a flat team-tinted square) or 2 (Team Glow, an additive team-tinted glow). In Reforged the material plugin exposes this as replacableTexture = {0 Not Replaceable, 1 Team Color, 2 Team Glow} plus a teamcolor_multiplier float and a per-layer TeamColorMultiplier field.

m3 side, two options:
  A) ReplaceableId 1 (Team Color, a solid tinted region):
     - Build a 'team mask' greyscale texture: white where the WC3 geoset used the replaceable-1 layer, black elsewhere. Practically, since WC3 puts replaceable-1 on its OWN layer/geoset, you can instead just give that geoset its own MAT_ and set:
         MAT_.blend_mode_layer = 5   (Team Color Diffuse Add)
         layer_diff = LAYR with flags |= 0x400 (color) and color_value = white, i.e. no bitmap
         MAT_.flags |= 0x10 (unshaded) if the WC3 layer was Unshaded
     - SC2 then tints that geometry with the player color.
  B) ReplaceableId 2 (Team Glow, additive):
     - MAT_.blend_mode = 2 (Add), MAT_.blend_mode_emis1 = 4 (Team Color Emissive Add), layer_emis1 = the glow texture with color_channels = 2 (Alpha Only) if the mask is in alpha, or 3/4/5 if it is in a single color channel.
  C) Reforged HD units (the common case): the HD material has 6 layers in fixed order — diffuse, normal, ORM, emissive, TEAM COLOR, TEAM GLOW. Map layer[4] (team color mask) -> MAT_.layer_diff's alpha channel + blend_mode_layer = 5; map layer[5] (team glow) -> MAT_.layer_emis1 + blend_mode_emis1 = 4. The WC3 layer's TeamColorMultiplier scales the effect — fold it into MAT_.hdr_emis (offset 48) for the glow, and into LAYR.color_multiply for the diffuse tint.

Note there is also LITE.flags 0x20 'team_color' for lights, irrelevant here.

_evidence: https://github.com/SC2Mapster/m3addon/blob/master/__init__.py lines 1127-1134 (matLayerAndEmisBlendModeList) and lines 1200-1203 (defaults); https://github.com/Solstice245/m3studio/blob/main/bl_enum.py lines 632-639; WC3 replaceable enum from C:\Program Files\Autodesk\3ds Max 2016\scripts\Startup\Warcraft_3_Reforged_Tools\GriffonStudios_Warcraft_3_Reforged_Plugins_Material.ms:91 and :57-65 (diffuse/normal/orm/emissive/reflection map slots)_

## [certain] ATT_ V1 is 20 bytes and MUST be paired with a same-length U16_ 'attachment_points_addon' section filled with 0xFFFF; attachment names must carry the literal 'Ref_' prefix and match the SC2 Editor's fixed vocabulary or the actor system will not find them.

ATT_ version 1, size 20:
   0 unknown00 i32   (always -1)
   4 name Reference->CHAR   (the full name INCLUDING the 'Ref_' prefix)
  16 bone u32        (index into MODL.bones)

MODL.attachment_points_addon (offset 240) = Reference->U16_ with exactly one entry per ATT_, value 0xFFFF. Both m3addon and m3studio do this unconditionally; omitting it is a known cause of the SC2 previewer ignoring attachments.

ATVL version 0, size 116 (optional attachment VOLUMES — only needed for hit volumes):
   0 bone0 u32
   4 bone1 u32
   8 shape u32   (0 Cuboid: width=size0,length=size1,height=size2; 1 Sphere: radius=size0; 2 Cylinder: radius=size0,height=size1)
  12 bone2 u32
  16 matrix Matrix44 (64)
  80 vertices Reference->VEC3
  92 face_data Reference->U16_
 104 size0 f32
 108 size1 f32
 112 size2 f32
When a volume exists the bone is named 'Vol_<name>' instead of 'Ref_<name>', but the ATT_ name still starts with 'Ref_'. Volumes also require attachment_volumes_addon0/1 (offsets 792/804).

Bone convention: m3addon/m3studio name the bone that carries an attachment 'Ref_<Name>' (shared.attachmentPointPrefix = "Ref_"). You do NOT have to create a separate bone — you can point ATT_.bone at the existing WC3 bone — but the round-trip tools expect the Ref_ bone, and the SC2 Editor previewer is happier with a dedicated leaf bone. Recommendation: create a leaf bone named 'Ref_<Name>' parented to the WC3 attachment's parent bone, with the WC3 attachment's local transform, and point ATT_ at it.

WC3 -> SC2 attachment name mapping (WC3 ATCH names always end in ' Ref'; strip that suffix, then map):
  Overhead Ref              -> Ref_Overhead
  Origin Ref                -> Ref_Origin
  Head Ref                  -> Ref_Head
  Head Ref (2nd)            -> Ref_Head Alternate
  Chest Ref                 -> Ref_Chest
  Chest Ref (left/right)    -> Ref_Chest Left / Ref_Chest Right
  Hand Left Ref             -> Ref_Hand Left
  Hand Right Ref            -> Ref_Hand Right
  Hand Left Ref (alt)       -> Ref_Hand Left Alternate
  Weapon Ref / Weapon Left  -> Ref_Weapon / Ref_Weapon Left
  Weapon Right Ref          -> Ref_Weapon Right
  Foot Left Ref             -> Ref_Foot Left
  Foot Right Ref            -> Ref_Foot Right
  Left Foot Ref / Right Foot Ref (older naming) -> same as above
  Sprite First Ref          -> Ref_Sprite First
  Sprite Second Ref         -> Ref_Sprite Second
  Sprite Third Ref          -> Ref_Sprite Third
  Sprite Fourth..Tenth      -> no SC2 equivalent; emit as Ref_Attacher 01..19 or drop
  Target Ref                -> Ref_Target
  Medium/Large/Small Ref    -> Ref_Hardpoint Medium / Large / Small
  Bone_Chest / Bone_Head    -> not attachments, they are bones
  Damage Ref                -> Ref_Damage
  Rally Point / RallyPoint  -> Ref_RallyPoint
  Mount Ref                 -> Ref_Chest Mount
  Shield Ref                -> Ref_Shield
  Back Ref                  -> Ref_Back
  Face Ref                  -> Ref_Face
  Turret Ref                -> Ref_Turret
Unmapped WC3 names: fall back to 'Ref_Attacher NN' (00-19) rather than inventing a name — SC2's Editor dropdown is a closed list and an unknown 'Ref_Foo' is simply never referenced by any actor.

The full canonical SC2 list (≈380 entries) is in m3studio bl_enum.py attachment_names and is explicitly annotated 'these are based on the possible values given in the SC2 editor; this list should never be changed without consulting that list'. Ship a copy of it in the tool and snap WC3 names to the nearest entry.

_evidence: https://github.com/Solstice245/m3studio/blob/main/bl_enum.py lines 59-380 (attachment_names); https://github.com/Solstice245/m3studio/blob/main/structures.xml lines 833-843 (ATT_) and 2041-2066 (ATVL); addon 0xFFFF: https://github.com/SC2Mapster/m3addon/blob/master/m3export.py line 1742 and https://github.com/Solstice245/m3studio/blob/main/io_m3_export.py line 1942; prefix: https://github.com/SC2Mapster/m3addon/blob/master/shared.py lines 117-121_

## [likely] Exporting WC3 PREM/PRE2/RIBB emitters to m3 PAR_/RIB_ is NOT worth doing. PAR_ v24 is 1496 bytes with ~150 fields and a completely different simulation model; RIB_ v9 is 760 bytes. Emit attachment points / helper bones instead and let the SC2 actor system attach real SC2 particles.

PAR_ versions and sizes: v10=1300, v11=1304, v12=1316, v14=1356, v17=1460, v18/19/21=1464, v22=1484, v23=1492, v24=1496. Leading fields: bone u32 @0, material_reference_index u32 @4, additional_flags u32 @8 (v17+: 0x1 emit_speed_randomize, 0x2 lifespan_randomize, 0x4 mass_randomize, 0x8 world_space), then ~40 FloatAnimationReferenceV0 (20 B each) for emit_speed, emit_speed_random, emit_angle_x/y, emit_spread_x/y, lifespan, lifespan_random, then distance_limit, gravity_x/y (must be 0), gravity, size/color/alpha/rotation anim_mid + hold floats, size Vector3AnimRef, rotation Vector3AnimRef, color_init/mid/end ColorAnimRef, drag, mass, mass2, mass_size_factor, local/world force channels (+fb copies), noise amp/freq/cohesion/edge, index_plus_length, emit_max, emit_rate FloatAnimRef, emit_shape, emit_type, ... plus emit-shape mesh region indices for shape==7. There is no field-by-field correspondence with WC3 PRE2 (WC3 has Speed/Variation/Latitude/Gravity/EmissionRate/Width/Length/Rows/Cols/HeadOrTail/LifeSpan/TailLength/TimeMiddle/SegmentColor[3]/Alpha[3]/ParticleScaling[3]/LifeSpanUVAnim/DecayUVAnim/TailUVAnim/Squirt/PriorityPlane and a 15-bit flag word). Any mapping is guesswork and the visual result will not match.

RIB_ versions: v4=744, v5=748, v6=748, v8=756, v9=760. bone u16 @0, material_reference_index u32 @4, additional_flags u32 @8 (v8+, 0x8 world_space), then ~30 anim refs; field ORDER CHANGES between v6 and v8 (pitch/yaw swap, mass/massProbablyUnused swap, pitch/yawVariation blocks swap) — a classic source of silently corrupt files. WC3 RIBB (HeightAbove/HeightBelow/Alpha/Color/LifeSpan/TextureSlot/EmissionRate/Rows/Cols/MaterialId/Gravity) maps only loosely onto RIB_ size/lifespan/base+center+tipColoring.

RECOMMENDATION (strong): skip PAR_/RIB_ entirely in v1.
  - For each WC3 PREM/PRE2/RIBB node, emit a real BONE at that node's position/parent with its full animation, and an ATT_ named 'Ref_Attacher NN' (or 'Ref_Hardpoint NN') bound to it.
  - Export a machine-readable sidecar (JSON) listing emitter name -> attachment name -> WC3 emitter parameters, so the user can build SC2 actor events (ActorCreation / AttachMethod) pointing SC2 particle models at those attachment points.
  - This is exactly the workflow SC2 modders use, keeps the .m3 valid, and avoids the 1496-byte PAR_ struct entirely.
If you ever do implement PAR_, target v24 only and require MAT_.additional_flags 0x4 (makes_use_of_vertex_color) on every particle material — both addons set that unconditionally for particle materials.

_evidence: https://github.com/Solstice245/m3studio/blob/main/structures.xml lines 1436-1520+ (PAR_); https://github.com/SC2Mapster/m3addon/blob/master/structures.xml lines 1863-2486 (PAR_) and 2588-2729 (RIB_, note the till-version-6 / since-version-8 field reordering); WC3 PRE2 field list from C:\Program Files\Autodesk\3ds Max 2016\scripts\Startup\Warcraft_3_Reforged_Tools\GriffonStudios_Warcraft_3_Reforged_Read.ms:1140-1290_

## [likely] CAM_ V5 (264 bytes) is the right version for modern SC2; SC2 portraits use a camera whose name is matched by the actor's CameraName, conventionally the model's own portrait camera. WC3 portrait models put the camera in a separate _Portrait.mdx — merge it into the main .m3 as a CAM_ + a bone.

CAM_ versions: v2=148, v3=180, v5=264. v5 layout:
   0 bone u32
   4 name Reference->CHAR
  16 field_of_view FloatAnimationReferenceV0 (20)  [radians]
  36 use_vertical_fov u32 (default 1)
  40 depth_of_field_type u32 (default 3)   [v5+]
  44 far_clip FloatAnimRefV0 (20)
  64 near_clip FloatAnimRefV0 (20)
  84 clip2 FloatAnimRefV0 (20)
 104 focal_depth FloatAnimRefV0 (20)
 124 falloff_start FloatAnimRefV0 (20)
 144 falloff_end FloatAnimRefV0 (20)
 164 unknown587dc7fb FloatAnimRefV0 (20)  [v5+]
 184 unknownfff8cb33 FloatAnimRefV0 (20)  [v5+]
 204 depth_of_field FloatAnimRefV0 (20)
 224 unknownf726f834 FloatAnimRefV0 (20)  [v5+]
 244 unknownd506807d FloatAnimRefV0 (20)  [v5+]
 =264
MODL.cameras @276, and MODL.cameras_addon @288 = Reference->U16_ with one 0xFFFF per camera (same rule as attachments; m3studio io_m3_export.py:1997).

Camera ORIENTATION comes from the bone, not from CAM_: the camera looks down the bone's **-Z**, with the bone's **+Y** as up, so you must synthesise a bone whose rest rotation aims from the WC3 camera Position toward its TargetPosition along -Z. This was written here as -Y (the Blender bone convention) and the writer followed it; the result was a portrait camera pointing 90 degrees off its subject — level shots stared straight at the ground and the in-game portrait came back solid black, while the SC2 editor's preview looked right because the preview frames the model with its own orbit camera and never reads CAM_. MEASURED, not assumed: over the 537 Blizzard/HotS models that carry a CAM_, the mean dot product between the camera bone's local -Z and the direction from the bone to the model's MSEC bounds centre is +0.986, while local -Y averages -0.041 (square to the shot) and +X/-X average 0.00; the bone's local +Y against world up averages +0.845. WC3 CAMS chunk gives: Name (80 chars), Position VEC3, FieldOfView (radians), FarClip, NearClip, TargetPosition VEC3, plus optional KCTR (position track) and KTTR (target track) and KCRL (roll).
  CAM_.field_of_view.init  = WC3 FieldOfView (already radians)
  CAM_.near_clip.init      = WC3 NearClip
  CAM_.far_clip.init       = WC3 FarClip
  bone rest translation    = WC3 Position
  bone rest rotation       = look-at(Position -> TargetPosition), roll from KCRL
  animated KCTR/KTTR       -> SD3V on the bone's location + a recomputed rotation SD4Q (you cannot animate a target in m3; you must bake the look-at into rotation keys)

Naming for SC2: name the camera exactly what the SC2 actor expects. Blizzard's own portrait models name it after the portrait usage; the practical convention that works in the SC2 previewer and in CActorUnit's PortraitModel is a single camera per model. For WC3, name the merged portrait camera 'Portrait' and also emit an attachment 'Ref_Portrait' at the head so actor-based portrait framing works even without the camera.

Practical note for WC3: portrait animations live in <Model>_Portrait.mdx as sequences named 'Portrait', 'Portrait Talk', 'Portrait Listen'. Merge those sequences into the main .m3 SEQS list (SC2 has no separate portrait file concept) and name them 'Portrait', 'Talk', 'Listen' to match SC2's anim tokens.

_evidence: https://github.com/Solstice245/m3studio/blob/main/structures.xml lines 2366-2394 (CAM_); cameras_addon at https://github.com/Solstice245/m3studio/blob/main/io_m3_export.py lines 1992-1997; https://github.com/SC2Mapster/m3addon/blob/master/structures.xml lines 2767-2795_

## [certain] A Warcraft III model must be turned -90 degrees about Z on export: WC3 builds a model facing +X, StarCraft II expects one facing -Y.

Exported unturned, a unit walks, attacks and idles square to the way its actor points it — reported as "moonwalking: they do turn, they just don't turn towards their facing". The turn is (x, y, z) -> (y, -x, z) and, because every WC3 node's local frame is world-axis aligned, each animated local rotation conjugates the same way: q=(x,y,z,w) -> (y,-x,z,w). Positions, normals, tangents, bone pivots, parent-local rest offsets, baked location keys, baked rotation keys, camera positions/targets and per-sequence bounds all go through it; the turn is a proper rotation, so triangle winding is untouched.

MEASURED four ways:
  1. Registration. The Reforged azuredragon, bandit, satyr and murloc meshes were histogram-matched against their hand-converted SC2 counterparts (AlleyV_Wc3R_*.m3, which work in-game) at each quarter turn: 270 degrees scored 0.013-0.025 mismatch, every other angle 0.44-0.66. After the exporter applies the turn, our own exports of the same four register onto those counterparts at 0 degrees with 0.015-0.026.
  2. Mesh symmetry. A biped/quadruped is mirror-symmetric across the plane square to its facing. 170 of 200 sampled WC3 unit models mirror across Y=0 (facing along X); 714 of 877 Blizzard SC2 unit models mirror across X=0 (facing along Y).
  3. Portrait cameras, which stand in front of their subject. Mean unit vector from model centre to camera: Blizzard SC2 (-0.20, -0.86), Warcraft III (+0.83, -0.11).
  4. Foot geometry (toes lead). WC3 mean toe offset from body centre X +0.215 / Y -0.038; Blizzard SC2 X +0.017 / Y -0.062.

Portrait models do not strictly need the turn — SC2 frames them with the camera the model carries, which turns with it — so applying it uniformly is safe and keeps one code path.

_evidence: spike/MdxProbe --symmetry (WC3 side, reads the CASC install); the SC2 side measured against C:\games\StarCraft II\Mods\HotS.SC2Mod (Blizzard storm_* art and the AlleyV_Wc3R_* WC3 conversions)_

## [likely] The hard m3 limits are 65535 vertices PER REGION (face indices are region-relative uint16) and 256 bones PER REGION (per-vertex bone lookup indices are uint8). The writer's global 65536-vertex throw matches both Blender addons but is stricter than the format; splitting is done with extra REGN + extra BAT_ inside the SINGLE existing DIV_.

Mechanics, verified from the importer:
  - REGN v5.first_vertex_index is uint32 (it was uint16 only in v2), so the U8__ vertex buffer can legitimately exceed 65536 vertices.
  - DIV_.faces is a flat U16_ array. For REGN v3+ the importer computes the real vertex index as `region.first_vertex_index + faces[i]`, i.e. face indices are REGION-RELATIVE. (For REGN v2 they were absolute; the importer subtracts first_vertex_index back off.) The existing writer already writes geoset-local indices, so it is doing the right thing.
  => therefore one region may hold at most 65536 vertices, but a model may hold many regions.
  - Per-vertex bone binding uses lookup0..lookup3, each uint8, indexing REGN's slice of MODL.bone_lookup. => at most 256 distinct bones per region. REGN.bone_count/bone_lookup_count are uint16 but that does not help; the uint8 in the vertex is the binding constraint. The writer's 256 check is correct.

HOW TO SPLIT a WC3 geoset that violates either limit:
  1. Partition the geoset's TRIANGLES (not vertices) into chunks such that each chunk's distinct-vertex count <= 65535 and distinct-bone count <= 256. Greedy triangle accumulation with a running bone set works; when adding a triangle would push the bone set past 256, close the chunk.
  2. Per chunk emit: one REGN (first_vertex_index = running vertex total, vertex_count, first_face_index = running index total, face_count, bone_count = bone_lookup_count = size of chunk bone set, first_bone_lookup_index = running lookup total, vertex_lookups_used = max influences on any vertex in the chunk (1..4), unknown04 = 1, root_bone = bone_lookup[first_bone_lookup_index], flags = 0, uv_multiply = 16.0, uv_offset = 0.0).
  3. Per chunk emit one BAT_ pointing at the same material_reference_index.
  4. All chunks stay in the SAME DIV_ (DIV_.instances = 1). Do NOT create a second DIV_ — SC2 uses divisions for LOD/split streaming and both Blender addons only ever read divisions[0] for geometry.
  5. MSEC stays a single entry per division (it holds only the animated bounding box). BAT_.bounds_index is the MSEC index, so it stays 0 for every batch.
  6. MODL.skin_bone_count must be (highest bone index that has BONE.flags 0x800 'skinned') + 1, computed AFTER splitting.

BAT_ V1 (14 bytes) full layout — the writer already matches it byte for byte, and note two fields it currently zeroes that are directly useful for WC3:
   0 flags u16 (0)
   2 priority_plane u16   <-- WC3 MTLS.PriorityPlane belongs here (in addition to MAT_.priority)
   4 region_index u16
   6 bounds_index u16 (MSEC index, 0)
   8 color_index u16 (0)
  10 material_reference_index u16 (index into MODL.material_references / MATM)
  12 bone i16 (default -1)  <-- pointing this at a bone makes that bone's BONE.batching FlagAnimationReference act as a per-sequence ON/OFF toggle for this whole draw call.

That last field is the m3 answer to WC3 GEOA (geoset animation): a WC3 geoset whose GEOA alpha animates 0<->1 as a hard visibility switch maps to BAT_.bone = <a dedicated bone> plus an SDFG (flag) track on that bone's `batching` anim id (BONE offset 140, the 4th anim ref). Smoothly-animated GEOA alpha instead has to go onto the material (SDCC on the diffuse LAYR color_value alpha, or SDR3 on a layer's color_multiply).

_evidence: Region-relative faces: https://github.com/SC2Mapster/m3addon/blob/master/m3import.py lines 1364-1391 and https://github.com/Solstice245/m3studio/blob/main/io_m3_import.py lines 1061-1068; 65536 total check: https://github.com/Solstice245/m3studio/blob/main/io_m3_export.py lines 1115-1116; REGN/BAT_ structs: https://github.com/Solstice245/m3studio/blob/main/structures.xml lines 755-831; BONE.batching semantics: same file line 741 ('BAT_ instances which point to bone are toggled by this property')_

## [likely] m3 SEQS V2 has a per-sequence not_looping flag (0x1), a frequency field, and always_global/global_in_previewer flags. SC2 picks animations purely by the SEQS name string, matched as a space-separated token sequence against the actor's requested animation name.

SEQS V2 (92 bytes) — the writer's layout is correct; only the flags value needs work:
   0 id i32 (-1)
   4 index i32 (-1)
   8 name Reference->CHAR
  20 anim_ms_start u32 (0)
  24 anim_ms_end u32
  28 movement_speed f32   <-- set this from WC3 SEQS MoveSpeed for Walk/Run so SC2 can sync foot speed
  32 flags u32:
        0x1 not_looping        <-- WC3 SEQS NonLooping flag (chunk field 'Flags' bit 0x1) maps 1:1
        0x2 always_global
        0x4 unknown0x4
        0x8 global_in_previewer
  36 frequency u32          <-- WC3 SEQS Rarity: WC3 rarity 0 = common. Map SC2 frequency = (WC3 Rarity == 0 ? 100 : round(100 / (1 + Rarity))). SC2 picks among same-named variations weighted by frequency.
  40 replay_start u32 (1)
  44 replay_end u32 (1)
  48 ms_blend u32 (100)
  52 bounding_sphere BNDS (28) — min VEC3, max VEC3, radius. WC3 SEQS carries per-sequence Extent (min/max/radius): USE IT here instead of the model bounds the writer currently copies.
  80 anim_sets Reference->U8__ (null)
 =92

The writer currently hardcodes flags=0 (always looping) and frequency=100. Both must come from the WC3 SEQS chunk.

SC2 name matching: SC2's CActor system builds an animation request from tokens (e.g. 'Stand Work Start', 'Attack Ready'). The engine scores every SEQS name by token overlap; unmatched requests fall back to 'Stand'. m3studio codifies the legal token vocabulary in bl_enum.anim_tokens. Tokens are Capitalised, space separated. Two-digit numeric suffixes ('01'..'99') mark variations of the same logical animation. STG_ names should equal the SEQS names; STC_ names are conventionally '<SeqName>_full' (the writer already does this).

WC3 -> SC2 SEQUENCE NAME MAP (WC3 names are ' - N' suffixed for variations; convert ' - 1' -> ' 01'):
  Stand                    -> Stand
  Stand - 2 / - 3          -> Stand 02 / Stand 03
  Stand Ready              -> Stand Ready
  Stand Alternate          -> Stand Alternate
  Stand Work               -> Stand Work
  Stand Channel            -> Stand Channel
  Stand Victory            -> Stand Victory
  Stand Defend             -> Stand Defend
  Stand Hit                -> Stand Hit
  Stand - 4 (fidgets)      -> Fidget 01/02 (SC2 prefers 'Stand Fidget NN' or 'Fidget NN'; 'Stand' + variation index is safer)
  Walk                     -> Walk
  Walk Fast                -> Walk Fast
  Walk Defend              -> Walk Defend
  Walk Alternate           -> Walk Alternate
  Run                      -> Run
  Attack - 1 / - 2         -> Attack 01 / Attack 02
  Attack Slam              -> Attack Slam
  Attack Throw             -> Attack Throw
  Attack Spin              -> Attack Spin
  Attack Alternate         -> Attack Alternate
  Spell                    -> Spell
  Spell Throw              -> Spell Throw
  Spell Channel            -> Spell Channel
  Spell Slam               -> Spell Slam
  Death                    -> Death
  Death Alternate          -> Death Alternate
  Dissipate                -> Death (with flags 0x1 not_looping) or 'Death Disintegrate'
  Decay Flesh              -> Death Flesh   (token 'Flesh' exists in the legacy token list)
  Decay Bone               -> Death Bone    (token 'Bone' exists)
  Birth                    -> Birth
  Birth Stand              -> Birth Stand
  Morph                    -> Morph
  Morph Alternate          -> Morph Alternate
  Portrait                 -> Portrait
  Portrait Talk            -> Talk
  Stand Work Gold/Lumber   -> Stand Work Gold / Stand Work Lumber   ('Gold','Lumber','Work' are all legal tokens)
  Gather / Gather Gold     -> Gather / Gather Gold
  Sleep / Stand Sleep      -> Stand Sleep  (no 'Sleep' token; use 'Stand Dead' or 'Stand' + not_looping)
  Swim / Stand Swim        -> Swim / Stand Swim  ('Swim' is a legacy token)
  Upgrade / Stand Upgrade  -> Upgrade / Stand Upgrade
  Burrow / Unburrow        -> Burrow / Unburrow
  Stand Channel Loop       -> Stand Channel
  <anything unmapped>      -> keep the WC3 name verbatim; SC2 will just never request it, which is harmless.
Always guarantee a sequence literally named 'Stand' exists (the writer already appends one) — SC2 falls back to it and a model with no 'Stand' renders in bind pose.

The m3studio legacy token list explicitly contains WC3-era words (Berserk, Bone, Decay, Defend, Drain, EatTree, Entangle, Fill, Flesh, Gold, Lumber, Puke, Slam, Spiked, Spin, StageFirst..StageFifth, Swim, Throw, Upgrade) — i.e. Blizzard's own token table already anticipates WC3-style names, so a light-touch mapping is fine.

_evidence: SEQS struct + flags: https://github.com/Solstice245/m3studio/blob/main/structures.xml lines 634-663; https://github.com/SC2Mapster/m3addon/blob/master/structures.xml lines 1162-1190; token vocabulary: https://github.com/Solstice245/m3studio/blob/main/bl_enum.py lines 25-57; verified against C:\Projects\D3 Model Viewer\src\D3ModelViewer.Core\Formats\M3Writer.cs:109-121_

## [certain] BUG / RISK: the writer's section padding rule `pad = len % 16` is wrong as alignment; it faithfully reproduces m3studio's io_m3.py but does NOT match m3addon or real Blizzard files, which round each section UP to a multiple of 16.

m3addon (m3.py:31-38) does:
    def increaseToValidSectionSize(size):
        r = size % 16
        return size if r == 0 else size + (16 - r)
and fills the gap with 0xAA (m3.py:70-78).

m3studio (io_m3.py:564,570) does:
    section.raw_bytes.extend([0xaa for ii in range(0, len(section.raw_bytes) % 16)])
i.e. it appends `len % 16` bytes, which only coincides with proper alignment when len%16 is 0 or 8. For len=20 the correct pad is 12, m3studio pads 4, so the next section starts at offset 24 rather than 32.

The existing C# writer (M3Writer.cs:895 `indexOffset += (uint)(len + len % 16);` and :911 `for (int p = 0; p < payload.Length % 16; p++) ow.Write((byte)0xAA);`) reproduces m3studio exactly.

WHY IT USUALLY STILL WORKS: neither reader validates alignment. Section byte-ranges are recovered purely from the index (each MDIndexEntry is {tag u32 reversed, offset u32, repetitions u32, version u32}); m3addon derives each section's length from the delta to the NEXT sorted offset (m3.py:1139-1148) and only ever decodes repetitions*struct_size bytes; m3studio reads at index_entry.offset for repetitions*size. So both Blender tools round-trip the file fine.

WHY YOU SHOULD FIX IT ANYWAY: the SC2 engine memory-maps m3 sections and every Blizzard-authored .m3 has 16-byte-aligned section offsets. Unaligned offsets are the single most plausible cause of 'loads in Blender, crashes/blank in the SC2 Editor previewer'. Changing to the round-up rule is strictly safer — it is a superset that every reader accepts.

EXACT FIX (two places in M3Builder.Build):
    // offsets
    long len = _sections[i].Ms.Length;
    indexOffset += (uint)(len + ((16 - len % 16) % 16));
    // payload emission
    int pad = (int)((16 - payload.Length % 16) % 16);
    for (int p = 0; p < pad; p++) ow.Write((byte)0xAA);
Also pad the index block itself is NOT required (the index is the last thing in the file and is already 16 bytes per entry).

OTHER container rules the writer already gets right and should keep:
  - index entry tag is the 4-char section name REVERSED as a big-endian u32 ('MODL' appears in the file as bytes 'L','D','O','M'). The writer's `s.Tag[0]<<24 | ...` written little-endian produces exactly that.
  - MD34 header is itself section 0 at offset 0; header.index_offset = total padded payload size, header.index_size = section COUNT (not bytes).
  - Every Reference is {entries u32, index u32, flags u32}; a null reference is 0,0,0. The writer emits flags=0 for real references; Blizzard files also use 0 there, so that is fine.
  - Section entry counts: CHAR sections' count = strlen+1 (includes the NUL); U8__ vertices count = TOTAL BYTES not vertex count. Both correct in the writer.

_evidence: https://github.com/SC2Mapster/m3addon/blob/master/m3.py lines 31-38, 64-78, 1139-1148; https://github.com/Solstice245/m3studio/blob/main/io_m3.py lines 555-580; C:\Projects\D3 Model Viewer\src\D3ModelViewer.Core\Formats\M3Writer.cs:889-912_

## [likely] BUG for WC3 input: the writer's UV conversion `(1 - v) * 2048` is correct for D3's bottom-up V but WRONG for MDX, which stores V top-down (D3D convention). For WC3 you must write `v * 2048` directly.

m3studio's importer (io_m3_import.py:37-41) is the ground truth for the encoding:
    u_blender = raw.x * uv_multiply / 32768 + uv_offset
    v_blender = -raw.y * uv_multiply / 32768 - uv_offset + 1
With the REGN v5 defaults uv_multiply = 16.0, uv_offset = 0.0 this reduces to
    u_blender = raw.x / 2048
    v_blender = 1 - raw.y / 2048
Blender's V is bottom-up. Therefore the stored value is
    raw.y = (1 - v_blender) * 2048 = v_topdown * 2048
MDX/WC3 UVs are already top-down (v=0 at the top of the texture, the Direct3D convention), so the correct WC3 encoding is:
    raw.x = round(u_mdx * 2048)
    raw.y = round(v_mdx * 2048)      // NO flip
The existing writer at M3Writer.cs:682-683 applies `(1.0 - vv) * 2048`, which is right for D3 and would vertically mirror every WC3 texture. Make the flip a parameter of the packing routine.

RANGE: int16 with uv_multiply 16 gives a usable UV range of about -16.0 .. +16.0 with 1/2048 precision. WC3 models with large tiling factors (water, cliffs, TXAN-scrolled layers) can exceed that. Fix by raising REGN.uv_multiply (offset 40 in REGN v5) — the general inverse is
    raw = (uv - uv_offset) * 32768 / uv_multiply
Pick uv_multiply = 16 while |uv| <= 16, else the smallest power of two such that max|uv| * 32768 / uv_multiply < 32767. Set it PER REGION, which is exactly what the field is for.

Alternatively, MODL.vertex_flags bits 0x2000..0x10000 (fuv0..fuv3) select FLOAT vec2 UVs instead of int16 (+8 bytes/UV set instead of +4). structures.xml notes float and integer UV flags are mutually exclusive. If you hit precision problems with WC3 scrolled/tiled layers, fuv0 is the clean escape hatch — but it is much rarer in Blizzard content, so treat it as a fallback only.

_evidence: https://github.com/Solstice245/m3studio/blob/main/io_m3_import.py lines 37-41 and 1063-1064; REGN uv_multiply/uv_offset at https://github.com/Solstice245/m3studio/blob/main/structures.xml lines 787-788; vertex_flags fuv bits same file lines 2535-2543; current behaviour at C:\Projects\D3 Model Viewer\src\D3ModelViewer.Core\Formats\M3Writer.cs:682-683_

## [likely] Animation coverage gap: the writer only emits SD3V/SD4Q bone tracks plus SDEV/SDMB. WC3 needs at minimum SDCC (colour), SDR3 (float), SD2V (vec2) and SDFG (flag) tracks, wired through the STC_ anim_ids/anim_refs table with the correct sub-block type indices.

STC_ V4 (204 bytes) layout and the anim_refs encoding — this is the mechanism the writer already uses but only for 3 of 14 block types:
   0 name Reference->CHAR   ('<Sequence>_full')
  12 concurrent u16 (0)
  14 priority u16 (0)
  16 sts_index u16
  18 sts_index_fb u16
  20 anim_ids Reference->U32_
  32 anim_refs Reference->U32_
  44 ref_count u32 (0)
  48 sdev  Reference->SDEV   sub-block type 0
  60 sd2v  Reference->SD2V   type 1
  72 sd3v  Reference->SD3V   type 2
  84 sd4q  Reference->SD4Q   type 3
  96 sdcc  Reference->SDCC   type 4   (COL colour keys)
 108 sdr3  Reference->SDR3   type 5   (float keys)
 120 sdu8  Reference->SDU8   type 6   (m3addon calls this unknownRef8)
 132 sds6  Reference->SDS6   type 7
 144 sdu6  Reference->SDU6   type 8
 156 sds3  Reference->SDS3   type 9   (m3addon calls this unknownRef11)
 168 sdu3  Reference->SDU3   type 10
 180 sdfg  Reference->SDFG   type 11  (flag/bool keys)
 192 sdmb  Reference->SDMB   type 12  (BNDS keys)
=204
Each anim_refs[i] is a packed u32: (blockTypeIndex << 16) | indexWithinThatBlock — exactly what M3Writer.cs:125-127 already builds (2<<16 for SD3V, 3<<16 for SD4Q, 12<<16 for SDMB). anim_ids[i] must be the same random-but-unique u32 that appears in the AnimationReferenceHeader.id of the property being animated, and the header's flags field must be 6 (m3addon shared.py:111 animFlagsForAnimatedProperty = 6) for the track to be honoured; 0 means 'not animated'. The writer already writes flags=6 on bone location/rotation/scale, which is why they animate.

Every SDxx block is the same 32-byte shape: {frames Reference->I32_, flags u32, fend u32, keys Reference->{VEC2|VEC3|QUAT|COL|REAL|...}}.

WHAT TO ADD FOR WC3:
  - WC3 layer KMTA (alpha over time)  -> SDCC track on LAYR.color_value (offset 16, ColorAnimationReferenceV0). Keys are COL (b,g,r,a bytes). Set LAYR.flags |= 0x400 (color) so the constant/animated colour is actually used.
    Alternative if you keep the texture: SDR3 on LAYR.color_multiply (offset 48) — a plain float 0..1 fade, and set LAYR.flags |= 0x80 (color_mult).
  - WC3 layer KMTE (emissive over time) -> SDR3 on MAT_.hdr_emis? No — hdr_emis is not animatable. Use SDCC on layer_emis1's color_value instead.
  - WC3 TXAN KTAT (uv translation)   -> SD2V on LAYR.uv_offset (offset 188)
  - WC3 TXAN KTAR (uv rotation)      -> SD3V on LAYR.uv_angle (offset 216)
  - WC3 TXAN KTAS (uv scale)         -> SD2V on LAYR.uv_tiling (offset 252)
  - WC3 GEOA alpha as a hard on/off   -> SDFG on BONE.batching (BONE offset 140) with BAT_.bone pointing at that bone
  - WC3 GEOA colour                  -> SDCC on the material's diffuse LAYR color_value
  - WC3 CAMS KCTR/KTTR               -> SD3V/SD4Q on the camera's bone (bake the look-at into rotation)

OTHER animation-side notes on the existing code:
  - MsOf(frame) = frame*1000/30 assumes 30 fps. MDX is authored in MILLISECONDS already (WC3 tracks store integer ms directly, and MDLX SEQS IntervalStart/End are ms). For WC3 you should pass the MDX time values straight through with NO fps conversion — this is very likely a large part of the user's 'animations are broken' complaint.
  - WC3 sequence intervals do not start at 0: SEQS[i] occupies [IntervalStart, IntervalEnd] on one global timeline, and every KGTR/KGRT/KGSC key is on that same global timeline. m3 STC_ blocks are PER SEQUENCE with times relative to 0. So for each WC3 sequence you must (a) select only keys with IntervalStart <= t <= IntervalEnd, (b) subtract IntervalStart, (c) synthesise a key at t=0 and t=(End-Start) by interpolating if the sequence boundaries fall between keys. Missing (c) is the classic 'first frame pops' bug.
  - WC3 interpolation types are 0 None, 1 Linear, 2 Hermite, 3 Bezier. m3 AnimationReferenceHeader.interpolation only supports 0 constant / 1 linear. Hermite/Bezier tracks must be BAKED — resample at a fixed step (e.g. every 33 ms, or at every WC3 key plus midpoints) and emit linear.
  - Bone rest pose: BONE.location/rotation/scale defaults are PARENT-LOCAL, and IREF holds the absolute INVERSE bind matrix per bone, one IREF per BONE (MODL description: 'bone must have same size like IREF'). WC3 PIVT gives ABSOLUTE pivot points, so you must subtract the parent pivot to get the m3 local translation, and build IREF as inverse(absolute rest matrix).
  - BONE.flags: 0x2000 'real' must be set on every exported bone; 0x800 'skinned' on bones referenced by bone_lookup; 0x200 'animated' on bones with any track; 0x1/0x2/0x4 are inherit translation/scale/rotation — WC3 nodes have DONT_INHERIT_TRANSLATION/SCALING/ROTATION flags (0x1/0x2/0x4 in the MDX node flags word) which INVERT this: set the m3 inherit bit when the WC3 don't-inherit bit is CLEAR. 0x10/0x40 billboard1/billboard2 correspond to WC3 node flags Billboarded/BillboardedLockX etc, and pair with a BBSC entry at MODL offset 816.

_evidence: STC_ struct: https://github.com/Solstice245/m3studio/blob/main/structures.xml lines 593-632; anim flags 6: https://github.com/SC2Mapster/m3addon/blob/master/shared.py line 111; BONE flags: m3studio structures.xml lines 712-743; SDxx shape: https://github.com/SC2Mapster/m3addon/blob/master/structures.xml lines 944-990; current implementation at C:\Projects\D3 Model Viewer\src\D3ModelViewer.Core\Formats\M3Writer.cs:100-245, 451-452_

## [likely] Concrete change list for M3Writer.cs, ordered by impact on WC3 export quality.

1. (correctness, blocking) Time base: stop converting frames->ms at 30 fps; MDX is already in ms. Slice the global WC3 timeline into per-sequence [0, End-Start] ranges, interpolating boundary keys. M3Writer.cs:451-452, 473, 523-529.
2. (correctness, blocking) UV V flip: add a `bool flipV` and pass false for MDX. M3Writer.cs:682-683.
3. (correctness, high) Padding: change `len % 16` to `(16 - len % 16) % 16` in both places. M3Writer.cs:895 and :911.
4. (visual, high) Materials: replace the single hardcoded material with a WC3-driven one. Add parameters per material: blend_mode (0-5 per the FilterMode table), alpha_test_threshold (192 for FilterMode 1), flags bits 0x4/0x8/0x10 from ShadingFlags 0x20/0x10/0x1, priority from MTLS.PriorityPlane, and 0x80000000 geometry_visible always. M3Writer.cs:399-423.
5. (visual, high) Alpha: emit a real layer_alpha1 (slot 8, MAT_ offset 160) pointing at the diffuse image with LAYR.color_channels=2 whenever the WC3 texture has meaningful alpha or the layer FilterMode is 1/2. Currently all 17 non-diffuse slots share one null LAYR. M3Writer.cs:413-414.
6. (visual, high) Team colour: for WC3 replaceable-1/2 layers set MAT_.blend_mode_layer=5 or blend_mode_emis1=4 and populate layer_emis1. M3Writer.cs:416.
7. (visual, medium) Reforged HD: wire layer_norm (slot 10), layer_ao (13), layer_spec (2), layer_gloss (3), layer_emis1 (4) from the 6-slot HD layer list; set MAT_.specularity/hdr_spec/hdr_emis from the WC3 material's EmissiveMultiplier.
8. (feature, medium) Attachments: emit ATT_ v1 (20 B) at MODL offset 228 plus the 0xFFFF U16_ addon at 240, using the Ref_ name mapping.
9. (feature, medium) Per-batch visibility: emit BAT_.priority_plane from MTLS.PriorityPlane and BAT_.bone + SDFG batching tracks for WC3 GEOA on/off geosets. M3Writer.cs:366-370.
10. (feature, medium) Sequence metadata: SEQS.flags 0x1 from WC3 NonLooping, SEQS.frequency from Rarity, SEQS.movement_speed from MoveSpeed, SEQS.bounding_sphere from the WC3 per-sequence Extent instead of the model bounds. M3Writer.cs:109-121.
11. (feature, low) Material/UV animation: SDCC on LAYR.color_value for KMTA, SD2V on uv_offset / uv_tiling and SD3V on uv_angle for TXAN. Requires extending the STC_ id/ref builder with block types 1, 4 and 11.
12. (feature, low) Cameras: CAM_ v5 at MODL offset 276 + cameras_addon 0xFFFF at 288, with a synthesised look-at bone; merge the _Portrait.mdx sequences.
13. (feature, low) Hit tests: WC3 CLID collision shapes -> SSGS v1 (108 B) list at MODL offset 768; also fill hittest_tight at offset 660 with a sphere covering the model instead of 108 zero bytes.
14. (robustness) Replace the global 65536 throw with per-region splitting (see the limits finding); keep the 256-bones-per-region check.
15. (skip) PAR_/RIB_ — emit Ref_Attacher attachment points and a JSON sidecar instead.

_evidence: C:\Projects\D3 Model Viewer\src\D3ModelViewer.Core\Formats\M3Writer.cs (whole file, read in full); struct facts as cited in the other findings_

## Open questions

- Does the SC2 engine / Editor previewer actually reject 16-byte-unaligned section offsets, or is m3studio's `len % 16` padding harmless in practice? Test: export one model both ways and open both in the SC2 Editor Previewer. No source I found settles this; every Blizzard file is aligned, and m3addon rounds up, so aligning is the safe default regardless.
- Does SC2 tolerate a model whose total vertex count exceeds 65536 (multiple regions, each under 65536, with region-relative uint16 faces)? Both Blender addons hard-error at 65536 total, but nothing in REGN v5 (uint32 first_vertex_index) requires it. Needs an empirical test against a large Heroes of the Storm .m3 from C:\games\Heroes of the Storm — check whether any shipped model exceeds 65536.
- MDX V-coordinate handedness: I inferred top-down (D3D) from the format's Direct3D lineage and from the fact that MDX->OBJ converters flip V, but I did not find an explicit statement in mdx-m3-viewer or the local MaxScript. Verify by exporting one WC3 model with a visibly asymmetric texture and comparing against the in-game render.
- Exact alpha-test threshold WC3 uses for FilterMode 1 (Transparent). The commonly cited value is 0.75 (=> 191/192 of 255) based on mdx-m3-viewer's shader `if (color.a < 0.75) discard;`, but I did not read that shader directly this session.
- Reforged HD material layer slot ORDER. TaylorMouse's Max plugin exposes diffuse / normal / ORM / emissive / reflection and separately a replaceable-texture dropdown, implying the on-disk LAYS order is diffuse, normal, ORM, emissive, team-color, team-glow (6 layers), but the Read.ms parser reads layers positionally without naming them. Confirm by dumping a known Reforged HD unit's MTLS chunk.
- Whether MAT_ has any true 'no depth test' equivalent. I found none in the v20 flags bitfield; WC3's NoDepthTest (0x40) currently has no faithful m3 mapping and can only be approximated with MAT_.priority ordering.
- SC2's exact animation-name scoring algorithm (how many tokens must match, whether order matters). m3studio's anim_tokens gives the vocabulary but not the matcher; the mapping table above is best-effort.

## Sources

- C:\Projects\D3 Model Viewer\src\D3ModelViewer.Core\Formats\M3Writer.cs (read in full, 924 lines)
- https://github.com/Solstice245/m3studio/blob/main/structures.xml (the writer's actual reference; MAT_ 1096-1208, LAYR 1013-1083, MODL 2470-2613, STC_ 593-632, SEQS 634-663, BONE 712-743, REGN 755-790, BAT_ 792-806, MSEC 808-817, DIV_ 819-831, ATT_ 833-843, PAR_ 1436+, RIB_ 2135+, CAM_ 2366-2394, SSGS 2017-2039, ATVL 2041-2066, COL 105-116)
- https://github.com/Solstice245/m3studio/blob/main/io_m3.py (save(), padding rule at lines 555-580)
- https://github.com/Solstice245/m3studio/blob/main/io_m3_import.py (to_bl_uv lines 37-41; region/face decoding 1051-1155)
- https://github.com/Solstice245/m3studio/blob/main/io_m3_export.py (65536 check 1115-1116; vertex_flags 1599-1647; attachments 1937-1945; cameras 1992-1997)
- https://github.com/Solstice245/m3studio/blob/main/io_shared.py (io_material_standard, io_material_layer field order)
- https://github.com/Solstice245/m3studio/blob/main/bl_enum.py (anim_tokens 25-57; attachment_names 59-380; mat_layer_blend 632-639; mat_spec 641-644)
- https://github.com/SC2Mapster/m3addon/blob/master/structures.xml (independent cross-check of every struct above)
- https://github.com/SC2Mapster/m3addon/blob/master/m3.py (increaseToValidSectionSize 31-38; 0xAA fill 64-78; section sizing from offset deltas 1139-1148)
- https://github.com/SC2Mapster/m3addon/blob/master/m3export.py (attachmentPointAddons 0xffff line 1742; region/BAT_ construction 850-870)
- https://github.com/SC2Mapster/m3addon/blob/master/m3import.py (region-relative faces 1364-1391)
- https://github.com/SC2Mapster/m3addon/blob/master/shared.py (attachmentPointPrefix 'Ref_' 119; animFlagsForAnimatedProperty 6 at 111; standard-material transfer order 1995-2022)
- https://github.com/SC2Mapster/m3addon/blob/master/__init__.py (matBlendModeList 1116-1125; matLayerAndEmisBlendModeList 1127-1134; matSpecularTypeList 1136-1139; MAT_ property defaults 1187-1220)
- https://github.com/SC2Mapster/m3addon/blob/master/cm/material.py (uvSourceList, colorChannelSettingList, fresnelTypeList)
- https://github.com/flo/m3addon/blob/master/structures.xml (original upstream, consulted via WebFetch)
- https://github.com/flowtsohg/mdx-m3-viewer/blob/master/src/parsers/mdlx/layer.ts (WC3 FilterMode 0-6 and shading flag bits 0x1..0x100)
- C:\Program Files\Autodesk\3ds Max 2016\scripts\Startup\Warcraft_3_Reforged_Tools\GriffonStudios_Warcraft_3_Reforged_Read.ms (MTLS/LAYR/ATCH/PRE2 binary layouts, lines 326-400, 698-720, 1140-1290)
- C:\Program Files\Autodesk\3ds Max 2016\scripts\Startup\Warcraft_3_Reforged_Tools\GriffonStudios_Warcraft_3_Reforged_Plugins_Material.ms (Reforged material model: filter modes, shading flags, replaceable texture enum, HD texture slots)
- C:\Program Files\Autodesk\3ds Max 2016\scripts\Startup\Warcraft_3_Reforged_Tools\GriffonStudios_Warcraft_3_Reforged_Build.ms (ShadingFlags bit decode, lines 113-125)
- C:\Program Files\Autodesk\3ds Max 2016\scripts\Startup\Warcraft_3_Reforged_Tools\GriffonStudios_Warcraft_3_Reforged_Write.ms (ShadingFlags bit encode, lines 255-263 — contradicts Build.ms on 0x40/0x80)
- https://www.hiveworkshop.com/threads/mdx-specifications.240487/ (WC3 layer shading flag values 16/32/64/128)
