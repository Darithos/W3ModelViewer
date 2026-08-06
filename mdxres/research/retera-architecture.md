# ReterasModelStudio + mdx-m3-viewer as architectural references for a C#/.NET WPF Warcraft III model viewer with .m3 export

## [certain] Both reference repos are cloned locally (sparse) and are current HEAD; read them there rather than re-fetching.

mdx-m3-viewer (TypeScript, GPL-ish/MIT, ghostwolf/flowtsohg): C:\Users\Darithos\AppData\Local\Temp\claude\c--Projects-Wc3-Model-Viewer\54ab26fb-e693-4e6b-87b3-6a884757b1cf\scratchpad\mdxv\src  (HEAD 2ff0bc00, 2025-08-27). Sparse-checkout of 'src/**'.
ReterasModelStudio (Java, Retera/Eric Theller): ...\scratchpad\rms\craft3data\src (HEAD e0c0dc34, 2026-02-22). Sparse-checkout of 'craft3data/src/**'.
NOTE on package naming: the widely-cited refactor to `com.hiveworkshop.rms.parsers.mdlx.*` did NOT land on master. On current master only `com.hiveworkshop.rms.editor.render3d` exists (4 files: GLTestCanvas, GLTestCanvas2, GLTestMain, NGGLDP). Everything else is still under `com.hiveworkshop.wc3.*`:
  - com.hiveworkshop.wc3.mdx.*   = raw MDX binary chunk structs + reader/writer (BoneChunk, GeosetChunk, LayerChunk, MaterialChunk, BindPoseChunk, CornChunk, FaceEffectsChunk, MdxModel, MdxUtils, Tracks, ~85 files)
  - com.hiveworkshop.wc3.mdl.*   = the editable in-memory model (EditableModel, Geoset, GeosetVertex, IdObject, Bone, Helper, Layer, Material, AnimFlag, GeosetAnim, Animation, Matrix, ...)
  - com.hiveworkshop.wc3.mdl.render3d.* = the runtime animation/particle layer (RenderModel, RenderNode, RenderParticleEmitter2*, RenderRibbon*, SoftwareParticleEmitterShader)
  - com.hiveworkshop.wc3.gui.modeledit.PerspectiveViewport = the actual 3D render loop
  - com.hiveworkshop.blizzard.casc.* = a pure-Java CASC reader (info/, io/, nio/, storage/, vfs/) — useful reference if you ever want to drop CascLib
Rebuild the clones with:
  git clone --depth 1 --filter=blob:none --no-checkout <url> X; cd X; MSYS_NO_PATHCONV=1 git sparse-checkout set --no-cone 'src/**'; git checkout master

_evidence: local: scratchpad/rms/craft3data/src ; scratchpad/mdxv/src ; https://github.com/Retera/ReterasModelStudio ; https://github.com/flowtsohg/mdx-m3-viewer_

## [certain] mdx-m3-viewer separates a pure format layer (parsers/mdlx) from a runtime layer (viewer/handlers/mdx); copy this split verbatim for the C# port.

Layer 1 — src/parsers/mdlx/ (format only, no GL): model.ts (chunk dispatcher), sequence.ts, material.ts, layer.ts, texture.ts, textureanimation.ts, geoset.ts, geosetanimation.ts, genericobject.ts, bone.ts, helper.ts, light.ts, attachment.ts, particleemitter{,2,popcorn}.ts, ribbonemitter.ts, camera.ts, eventobject.ts, collisionshape.ts, faceeffect.ts, extent.ts, animations.ts, animatedobject.ts, animationmap.ts, unknownchunk.ts, tokenstream.ts (MDL text), isformat.ts. This layer does BOTH MDX (binary) and MDL (text) read+write for every class — readMdx/writeMdx/readMdl/writeMdl/getByteLength. That symmetry is what makes it trustworthy: getByteLength() is an independent check on readMdx().
Layer 2 — src/viewer/handlers/mdx/: model.ts (MdxModel: builds render-ready structures), modelinstance.ts (per-instance animation state), genericobject.ts, animatedobject.ts, sd.ts (animation evaluator), layer.ts, material.ts, geoset.ts, batch.ts, batchgroup.ts, emittergroup.ts, setupgeosets.ts, setupgroups.ts, texture.ts, filtermode.ts, replaceableids.ts, sequence.ts, node.ts, handler.ts (shader compilation + team-color loading), shaders/{sd.vert,sd.frag,hd.vert,hd.frag,particles.vert,particles.frag,transforms.glsl}.ts, plus emitters.
Layer 3 — src/viewer/: model.ts, modelinstance.ts, node.ts, skeletalnode.ts, scene.ts, camera.ts, bounds.ts, texture.ts, gl/{shader,datatexture}.ts. skeletalnode.ts is format-agnostic and is where billboarding lives.
Recommended C# mapping:
  Wc3.Formats.Mdlx      -> parsers/mdlx (POCOs + BinaryReader/Writer, no rendering types)
  Wc3.Render.Mdx        -> viewer/handlers/mdx
  Wc3.Render.Core       -> viewer/{skeletalnode,scene,camera,bounds}
Keep the MDL text writer: it is the single best debugging tool (round-trip MDX->MDL and diff against Retera's output).

_evidence: scratchpad/mdxv/src/parsers/mdlx/*.ts ; scratchpad/mdxv/src/viewer/handlers/mdx/*.ts_

## [certain] MDX chunk dispatch: mdx-m3-viewer's loop is fully order-independent and preserves unknown chunks; Retera's is capped at exactly 23 chunks. Port ghostwolf's.

parsers/mdlx/model.ts::loadMdx():
  stream.skip(4)  // 'MDLX'
  while (stream.remaining > 0) { tag = readBinary(4); size = readUint32(); switch(tag) {...} else unknownChunks.push(new UnknownChunk(stream, size, tag)); }
Tag -> handler and element size:
  VERS -> version = readUint32()  (800 = RoC/TFT; 900/1000/1100/1200 = Reforged)
  MODL -> name(80 chars) + animationFile(260 chars) + Extent + blendTime(uint32)   [= 80+260+28+4 = 372 bytes]
  SEQS -> size/132 fixed records
  GLBS -> size/4 uint32s (each is a global-sequence duration in ms)
  MTLS -> variable-size records until size consumed
  TEXS -> size/268 fixed records
  TXAN, GEOS, GEOA, BONE, LITE, HELP, ATCH, PREM, PRE2, CORN, RIBB, CAMS, EVTS, CLID -> variable-size records
  PIVT -> size/12 float3s
  FAFX -> size/340 fixed records
  BPOS -> readUint32() count, then count * 12 floats (a 4x3 matrix, row-major, implicit m33=1)
  anything else -> UnknownChunk (kept, and re-written on save). 3rd-party tools stash metadata this way.
Extent (parsers/mdlx/extent.ts) = boundsRadius(float) + min[3](float) + max[3](float) = 28 bytes. NOTE ORDER: radius FIRST.
Retera equivalent com/hiveworkshop/wc3/mdx/MdxModel.java:306 uses `for (int i = 0; i < 23; i++)` with mark/reset peeking of the 4-byte tag — so a model with >23 chunks silently truncates, and an unknown chunk is only skipped if all 4 tag bytes are alphabetic (Character.isAlphabetic). Do NOT port that. Also Retera's MaterialChunk.load computes remaining bytes by subtracting each material's *recomputed* getSize() rather than trusting the stream position, which makes any size-model mismatch cascade.
ghostwolf's constructor also wraps parser.load() in try/catch and continues rendering with partial data (viewer/handlers/mdx/model.ts, comment: 'I have encountered a model that is missing data, but still works in-game'). Port that resilience.

_evidence: scratchpad/mdxv/src/parsers/mdlx/model.ts:107-170 ; scratchpad/rms/craft3data/src/com/hiveworkshop/wc3/mdx/MdxModel.java:306-400_

## [certain] THE REFORGED HD FRAGMENT SHADER (mdx-m3-viewer hd.frag.ts) — transcribed exactly. This is the best available spec for how Reforged HD models are shaded.

File: src/viewer/handlers/mdx/shaders/hd.frag.ts. Uniforms: sampler2D u_diffuseMap, u_normalsMap, u_ormMap, u_emissiveMap, u_teamColorMap, u_environmentMap; float u_filterMode. Varyings: vec2 v_uv; float v_layerAlpha; vec3 v_lightDir (TANGENT space); vec3 v_eyeVec; vec3 v_normal.

vec3 decodeNormal() {
  vec2 xy = texture2D(u_normalsMap, v_uv).xy * 2.0 - 1.0;
  return vec3(xy, sqrt(1.0 - dot(xy, xy)));
}

const vec2 invAtan = vec2(0.1591, 0.3183);   // (1/2pi, 1/pi)
vec2 sampleEnvironmentMap(vec3 n) { vec2 uv = vec2(atan(n.x, n.y), -asin(n.z)); uv *= invAtan; uv += 0.5; return uv; }

vec4 getDiffuseColor() {
  vec4 color = texture2D(u_diffuseMap, v_uv);
  if (u_filterMode == 1.0 && color.a < 0.75) discard;   // 1-bit alpha = FilterMode.Transparent
  return color;
}
vec4 getOrmColor()     { return texture2D(u_ormMap, v_uv); }        // r=AO, g=Roughness, b=Metallic, a=TEAM COLOR FACTOR
vec3 getEmissiveColor(){ return texture2D(u_emissiveMap, v_uv).rgb; }
vec3 getTeamColor()    { return texture2D(u_teamColorMap, v_uv).rgb; }

void lambert() {   // <-- the default main()
  vec4 baseColor = getDiffuseColor();
  vec3 normal    = decodeNormal();
  vec4 orm       = getOrmColor();
  vec3 emissive  = getEmissiveColor();
  vec3 tc        = getTeamColor();
  float aoFactor = orm.r;
  float tcFactor = orm.a;
  float lambertFactor = clamp(dot(normal, v_lightDir), 0.0, 1.0);
  vec3 color = baseColor.rgb;
  if (tcFactor > 0.1) { color *= tc * tcFactor; }
  color *= clamp(lambertFactor * aoFactor + 0.1, 0.0, 1.0);   // 0.1 = ambient floor
  color += emissive;
  gl_FragColor = vec4(color, baseColor.a);
}

main() is a #if chain: ONLY_DIFFUSE / ONLY_NORMAL_MAP / ONLY_OCCLUSION / ONLY_ROUGHNESS / ONLY_METALLIC / ONLY_TC_FACTOR / ONLY_EMISSIVE / ONLY_TEXCOORDS / ONLY_NORMALS / ONLY_TANGENTS, else lambert(). A full PBR path (Cook-Torrance + IBL with lut/envDiffuse/envSpecular RGBM atlases) exists but is entirely commented out and unused — do not chase it.

BUGS / CAVEATS to fix in your port:
  (a) lambert() never uses v_layerAlpha. Layer alpha only gates the *whole batch* on the CPU side (`if (layerAlpha > 0)` in batchgroup.ts). Multiply it in: gl_FragColor.a = baseColor.a * v_layerAlpha.
  (b) The team-color formula `color *= tc * tcFactor` DARKENS the diffuse wherever tcFactor<1 — it is a multiply, not a blend. Retera uses a lerp (see separate finding) which looks correct in-game. Prefer Retera's.
  (c) u_environmentMap is bound to texture unit 5 and sampleEnvironmentMap() is defined, but neither is used in lambert(). Reforged's 6th HD layer (Reflections) is effectively unimplemented in mdx-m3-viewer.
  (d) Reforged HD is rendered with BLEND DISABLED unconditionally (batchgroup.ts, isHd branch: gl.disable(gl.BLEND); gl.enable(gl.DEPTH_TEST); gl.depthMask(true)). So HD is opaque or 1-bit-alpha-tested only. If your user's 'Reforged alpha mapping' problem is semi-transparent HD geometry, that is why: mdx-m3-viewer literally cannot draw it.

_evidence: scratchpad/mdxv/src/viewer/handlers/mdx/shaders/hd.frag.ts (full file, 304 lines) ; https://github.com/flowtsohg/mdx-m3-viewer/blob/master/src/viewer/handlers/mdx/shaders/hd.frag.ts_

## [certain] The HD vertex shader builds a TBN basis and passes the light direction in tangent space; the normal map is therefore consumed in tangent space with no per-fragment TBN.

src/viewer/handlers/mdx/shaders/hd.vert.ts:
uniforms: mat4 u_VP, mat4 u_MV, vec3 u_eyePos, vec3 u_lightPos, float u_layerAlpha, bool u_hasBones.
attributes: vec3 a_position, vec3 a_normal, vec2 a_uv, vec4 a_tangent (xyz = tangent, w = bitangent handedness sign).

vec3 TBN(vec3 v, vec3 t, vec3 b, vec3 n) { return vec3(dot(v,t), dot(v,b), dot(v,n)); }

main():
  vec3 position = a_position; vec3 normal = a_normal; vec3 tangent = a_tangent.xyz;
  tangent = normalize(tangent - dot(tangent, normal) * normal);        // Gram-Schmidt re-orthogonalize
  vec3 binormal = cross(normal, tangent) * a_tangent.w;
  if (u_hasBones) { #ifdef SKIN transformSkin(pos,nrm,tan,binorm) #else transformVertexGroupsHD(...) #endif }
  vec3 position_mv = vec3(u_MV * vec4(position,1));
  mat3 mv = mat3(u_MV);
  vec3 t = normalize(mv*tangent); vec3 b = normalize(mv*binormal); vec3 n = normalize(mv*normal);
  v_eyeVec  = normalize(u_eyePos - position_mv);
  vec3 lightDir = normalize(u_lightPos - position_mv);
  v_lightDir = normalize(TBN(lightDir, t, b, n));
  v_uv = a_uv; v_layerAlpha = u_layerAlpha; v_normal = normal;
  gl_Position = u_VP * vec4(position, 1.0);

Note the inconsistency (a real bug worth knowing): position is skinned into WORLD space and then multiplied by u_VP, but the TBN is built from u_MV (view matrix) — so v_lightDir mixes world-space and view-space quantities. Retera's HDDiffuseShaderPipeline does the same kind of mixing. Neither is physically right; both look 'close enough'. For a fresh C# port, build the TBN in world space and pass a world-space light dir; you will get a more stable result.
u_lightPos comes from scene.lightPosition (viewer/scene.ts).

_evidence: scratchpad/mdxv/src/viewer/handlers/mdx/shaders/hd.vert.ts_

## [certain] Retera's HD shader emulation (NGGLDP.HDDiffuseShaderPipeline) differs from ghostwolf's in three material ways: team-color blend, normal-map channel order, and it actually implements fresnel + emissive gain + reflections.

File: craft3data/src/com/hiveworkshop/rms/editor/render3d/NGGLDP.java, class HDDiffuseShaderPipeline (line 724). GLSL 330. Vertex stride = 4(pos)+4(normal)+4(tangent)+2(uv)+4(color) = 18 floats.
Uniforms: u_textureDiffuse, u_textureNormal, u_textureORM, u_textureEmissive, u_textureTeamColor, u_textureReflections, int u_textureUsed, int u_alphaTest, int u_lightingEnabled, float u_fresnelTeamColor, float u_emissiveGain, vec4 u_fresnelColor, vec2 u_viewportSize.

Fragment main (verbatim semantics):
  vec4 ormTexel       = texture2D(u_textureORM, v_uv);
  vec4 teamColorTexel = texture2D(u_textureTeamColor, v_uv);
  if (u_textureUsed != 0) {
    vec4 texel = texture2D(u_textureDiffuse, v_uv);
    color = vec4(texel.rgb * ((1.0 - ormTexel.a) + (teamColorTexel.rgb * ormTexel.a)), texel.a) * v_color;
  } else color = v_color;
  if (v_color.a == 1.0 && u_alphaTest != 0 && color.a < 0.75) discard;
  if (color.a == 0.0) discard;
  if (u_lightingEnabled != 0) {
    vec3 normalXYZ = texture2D(u_textureNormal, v_uv).xyz;
    vec2 normalXY  = normalXYZ.yx * 2.0 - 1.0;          // <-- .yx SWIZZLE, ghostwolf uses .xy
    vec3 normal    = vec3(normalXY, sqrt(1.0 - dot(normalXY, normalXY)));
    vec4 emissiveTexel    = texture2D(u_textureEmissive, v_uv);
    vec4 reflectionsTexel = clamp(0.2 + 2.0*texture2D(u_textureReflections, vec2(gl_FragCoord.x/u_viewportSize.x, -gl_FragCoord.y/u_viewportSize.y)), 0.0, 1.0);
    vec3 lightDir = normalize(v_tangentLightPos);
    float cosTheta = dot(lightDir, normal) * 0.5 + 0.5;         // half-lambert
    float lambertFactor = clamp(cosTheta, 0.0, 1.0);
    vec3 diffuse = clamp(lambertFactor,0.0,1.0) * color.xyz;
    vec3 viewDir = normalize(v_tangentViewPos - v_tangentFragPos);
    vec3 halfwayDir = normalize(lightDir + viewDir);
    float spec = pow(max(dot(normal, halfwayDir)*0.5 + 0.5, 0.0), 32.0);
    vec3 specular = vec3(max(-ormTexel.g + 0.5, 0.0) + ormTexel.b) * spec * (reflectionsTexel.xyz*(1.0-ormTexel.g) + ormTexel.g*color.xyz);
    vec3 fresnelColor = vec3(u_fresnelColor.rgb*(1.0-u_fresnelTeamColor) + teamColorTexel.rgb*u_fresnelTeamColor) * v_color.rgb;
    vec3 fresnel = fresnelColor * pow(1.0 - dot(normalize(v_tangentViewPos), normal), 1.0) * u_fresnelColor.a;
    FragColor = vec4(emissiveTexel.xyz*sqrt(u_emissiveGain) + specular + diffuse + fresnel, color.a);
  } else FragColor = color;

KEY DIFFERENCES vs mdx-m3-viewer, and which to trust:
 1. TEAM COLOR. Retera: lerp(1, teamColor, orm.a)  =>  texel.rgb * ((1-orm.a) + tc*orm.a). ghostwolf: `if (orm.a > 0.1) color *= tc*orm.a` — a straight multiply that darkens. TRUST RETERA. The ORM alpha channel is a team-color MASK; where mask=0 the diffuse must pass through unmodified, which only Retera's form guarantees (at orm.a=0 Retera gives texel*1, ghostwolf gives texel*tc*0 = black if the >0.1 guard weren't there, and a hard discontinuity at 0.1 when it is).
 2. NORMAL MAP CHANNELS. Retera reads .yx, ghostwolf reads .xy. They cannot both be right. Reforged normal maps ship as BC5/ATI2 (two-channel R,G). ghostwolf's .xy is the conventional interpretation; Retera's .yx implies a green/red swap. Verify empirically against a known model (a cylinder-ish unit arm) before committing; I lean ghostwolf (.xy) because BC5 stores X in R and Y in G by convention, and because ghostwolf's ONLY_NORMAL_MAP debug mode was presumably eyeballed.
 3. Retera implements FresnelColor/FresnelOpacity/FresnelTeamColor and EmissiveGain (all animatable v1000+ layer fields). ghostwolf parses them and then ignores them entirely. If you want fidelity on Reforged hero/glow materials you need Retera's fresnel term.
 4. Retera's alpha test is `v_color.a == 1.0 && u_alphaTest != 0 && color.a < 0.75` — i.e. alpha-test is suppressed once geoset-anim/layer alpha fades the object, so a fading unit doesn't pop. ghostwolf's is unconditional on filterMode==1. Retera's behavior is nicer.
 5. Retera also has a plain `if (color.a == 0.0) discard;`.

_evidence: scratchpad/rms/craft3data/src/com/hiveworkshop/rms/editor/render3d/NGGLDP.java:724-880_

## [certain] The six HD layers of a Reforged material map 1:1 to ORM/normal/emissive/teamcolor/env by ORDINAL, and 'HD' is detected by the material's 80-char shader string being exactly "Shader_HD_DefaultUnit".

Enum order (authoritative, both projects agree):
  0 Diffuse, 1 Normal, 2 ORM, 3 Emissive, 4 TeamColor, 5 Reflections(environment)
Retera: com/hiveworkshop/wc3/mdl/ShaderTextureTypeHD.java — `enum ShaderTextureTypeHD { Diffuse, Normal, ORM, Emissive, TeamColor, Reflections }`.
ghostwolf: viewer/handlers/mdx/batchgroup.ts destructures `const [diffuseLayer, normalsLayer, ormLayer, emissiveLayer, teamColorLayer, environmentMapLayer] = material.layers;` and binds them to texture units 0..5.

Detection:
  ghostwolf viewer/handlers/mdx/setupgeosets.ts: `const isHd = material.shader === 'Shader_HD_DefaultUnit';`
  ghostwolf viewer/handlers/mdx/model.ts: `if (material.shader !== '') this.hd = true;`  <-- note the INCONSISTENCY: model.hd is true for ANY non-empty shader string, but a batch is only HD for the exact 'Shader_HD_DefaultUnit'. A material with some other shader string gets rendered as 6 separate SD batches. Guard against that.
  Retera com/hiveworkshop/wc3/mdl/Material.java:40: `public static final String SHADER_HD_DEFAULT_UNIT = "Shader_HD_DefaultUnit";` and Material(MaterialChunk.Material, EditableModel) at line ~131 CONDENSES the 6 MDX layers into ONE editable Layer carrying an EnumMap<ShaderTextureTypeHD,Bitmap>. That condensation is the right model for an editor; for a renderer, ghostwolf's 'keep 6 layers, index them' is simpler.

Animated HD texture IDs: for layer index i>0 Retera renames the KMTF track to (TypeName + "TextureID"), e.g. "NormalTextureID", "ORMTextureID" (Material.java:140-150, Layer.java:394-410). ghostwolf does not support animated HD texture ids at all — it only reads diffuseLayer.textureId etc. as statics in batchgroup.ts.

Team-color texture substitution (batchgroup.ts, HD path):
  let teamColorTexture = textures[teamColorId];
  if (teamColorTexture.replaceableId === 0 || teamColorTexture.replaceableId === 1) teamColorTexture = teamColors[instance.teamColor];
  (i.e. an HD teamcolor slot with replaceableId 0 OR 1 gets swapped for ReplaceableTextures\TeamColor\TeamColorNN.dds)
SD path uses: replaceableId===1 -> teamColors[teamColor]; replaceableId===2 -> teamGlows[teamColor].
Team count: 16 teams for classic (.blp), 28 teams for Reforged (.dds) — viewer/handlers/mdx/handler.ts::loadTeamTextures: `const teams = reforged ? 28 : 16; const ext = reforged ? 'dds' : 'blp';` paths `ReplaceableTextures\TeamColor\TeamColor{NN}.{ext}` and `...\TeamGlow\TeamGlow{NN}.{ext}` with NN zero-padded to 2.

_evidence: scratchpad/rms/craft3data/src/com/hiveworkshop/wc3/mdl/ShaderTextureTypeHD.java ; scratchpad/mdxv/src/viewer/handlers/mdx/batchgroup.ts:95-140 ; scratchpad/mdxv/src/viewer/handlers/mdx/handler.ts (loadTeamTextures)_

## [certain] CRITICAL: mdx-m3-viewer's LAYS/MTLS parser is WRONG for MDX version 1100/1200 (current Reforged, which is what a 2.0.4 install ships). Use Retera's LayerChunk/MaterialChunk layout instead.

mdx-m3-viewer parsers/mdlx/layer.ts::readMdx reads, for ANY version > 800:
  size(u32), filterMode(u32), flags(u32), textureId(i32), textureAnimationId(i32), coordId(u32), alpha(f32),
  emissiveGain(f32), fresnelColor(f32[3]), fresnelOpacity(f32), fresnelTeamColor(f32)   [= 52 bytes]
  then readAnimations(size - consumed)
and parsers/mdlx/material.ts reads an 80-char `shader` string for ANY version > 800.

Retera's version gates (com/hiveworkshop/wc3/util/ModelUtils.java:311-345) — TRUST THESE:
  isShaderStringSupported(v)     -> v >= 900 && v <= 1000        // 80-char shader name in MTLS ONLY for 900/1000
  isEmissiveLayerSupported(v)    -> v in {900,1000,1100,1200}
  isFresnelColorLayerSupported(v)-> v in {1000,1100,1200}        // NOT 900!
  isTangentAndSkinSupported(v)   -> v in {900,1000,1100,1200}
  isBindPoseSupported(v)         -> v in {900,1000,1100,1200}
  isLevelOfDetailSupported(v)    -> v in {900,1000,1100,1200}
  isCornSupported(v)             -> v in {900,1000,1100,1200}
  isCombinedHDLayerSupported(v)  -> v in {1100,1200}
  isLightShadowIntensitySupported(v) -> v >= 1200                // extra float in LITE

So the CORRECT layer layout (com/hiveworkshop/wc3/mdx/LayerChunk.java, class Layer.load):
  i32 inclusiveSize
  i32 filterMode
  i32 shadingFlags
  i32 textureId
  i32 textureAnimationId
  i32 coordID
  f32 alpha
  if (v>=900):  f32 emissiveGain     // if NaN, treat as 1.0 — Retera explicitly does this
  if (v>=1000): f32[3] fresnelColor; f32 fresnelOpacity; f32 fresnelTeamColor
  if (v>=1100): i32 shaderTypeId     // 0 = SD, 1 = HD.  Anything else -> warn
                i32 textureIdCount
                repeat textureIdCount times: { i32 textureId; i32 textureTypeIndex(0..5 = ShaderTextureTypeHD ordinal);
                                               optional inline 'KMTF' MaterialTextureId track }
  then up to 6 optional keyed tracks in any order: KMTA (alpha), KMTF (textureId), KMTE (emissiveGain),
       KFC3 (fresnelColor), KFCA (fresnelOpacity), KFTC (fresnelTeamColor)

MTLS material layout:
  i32 inclusiveSize; i32 priorityPlane; i32 flags;
  if (900<=v<=1000): char[80] shader
  'LAYS' + i32 layerCount + layers
In v1100+ there is NO shader string — HD-ness is carried by the per-layer shaderTypeId, and all 6 texture types are inside ONE layer.

This is corroborated by the local TaylorMouse MaxScript, GriffonStudios_Warcraft_3_Reforged_Read.ms:
  line 344: `if ( _mdx_version == 1000 ) then shdr.Name = _helper.ReadFixedString stream 80`  (v1100 has no name)
  line 387: `if ( _mdx_version >= 1100 ) then _helper.SkipBytes stream 56`
  lines 391-392: animations present if (v==1000 && size>52) or (v>=1100 && size>108)
TaylorMouse's blind 56-byte skip == 8 (shaderTypeId+count) + 6*8 (six texture entries) — i.e. it only works for a FULL 6-texture HD layer and will corrupt an SD layer in a v1100 file (which has count=1 => 16 bytes) or an HD layer missing Reflections (count=5 => 48 bytes). TaylorMouse's reader is buggy here; Retera's is correct. Also note Retera's LayerChunk has a `textureTypeIndex = i` override commented `// TODO this is blizztarded` when an inline KMTF follows — Blizzard reuses the loop index rather than the declared type index in that case.

ACTION FOR THE C# PORT: implement the layer parser from LayerChunk.java, not from layer.ts. Determine your local install's actual MDX version first (read VERS on any extracted .mdx); 2.0.x almost certainly emits 1100 or 1200.

_evidence: scratchpad/rms/craft3data/src/com/hiveworkshop/wc3/mdx/LayerChunk.java:82-160 ; scratchpad/rms/craft3data/src/com/hiveworkshop/wc3/util/ModelUtils.java:311-345 ; C:\Program Files\Autodesk\3ds Max 2016\scripts\Startup\Warcraft_3_Reforged_Tools\GriffonStudios_Warcraft_3_Reforged_Read.ms:334-405 ; scratchpad/mdxv/src/parsers/mdlx/layer.ts:76-115_

## [certain] Animation evaluation at a time value — mdx-m3-viewer's Sd/SdSequence is the algorithm to port; it pre-slices every track per sequence at load time.

File: src/viewer/handlers/mdx/sd.ts.
Classes: abstract Sd (fields: defval, model, name, globalSequence: SdSequence|null, sequences: SdSequence[], interpolationType) with concrete ScalarSd / VectorSd / QuatSd; factory createTypedSd(model, animation).

CONSTRUCTION (Sd ctor):
  - if animation.globalSequenceId !== -1 and model.globalSequences exists:
        globalSequence = new SdSequence(this, 0, globalSequences[gsId], animation, /*isGlobal*/true)
    else: for each model sequence -> sequences.push(new SdSequence(this, interval[0], interval[1], animation, false))
  - FORCED INTERPOLATION OVERRIDE (important, matches the game): these tracks are forced to DontInterp regardless of what the file says — KLAV, KATV, KPEV, KP2V, KRVS (i.e. all *visibility* tracks). Comment: 'The game seems to do this with visibility tracks... came up as a bug report by a user who used the wrong interpolation type.'
  - DEFAULT VALUES table (defVals) when a sequence has zero keys in range:
      KMTF=0f, KMTA=1f, KTAT=(0,0,0), KTAR=(0,0,0,1), KTAS=(1,1,1), KGAO=1f, KGAC=(0,0,0)*,
      KLAS/KLAE/KLAI/KLBI=0f, KLAC/KLBC=(0,0,0)*, KLAV=1, KATV=1, KPE*=0f, KPEV=1, KP2*=0f, KP2V=1,
      KRHA/KRHB/KRTX=0f, KRAL=0f  <-- ribbon alpha defaults to 0, not 1 (explicit comment), KRCO=(0,0,0)*, KRVS=1,
      KCTR/KTTR=(0,0,0), KCRL=0u, KGTR=(0,0,0), KGRT=(0,0,0,1), KGSC=(1,1,1)
      (* colorDefval aliases translationDefval = (0,0,0) — a latent bug: a missing color track yields black, not white. GeosetAnimation overrides this by passing its own static color as defaultValue, so it doesn't bite in practice.)

SdSequence ctor(start, end, animation, isGlobal):
  - if (isGlobal && frames[0] > end) { frames[0]=frames[0]; values[0]=values[0]; }   // global seq whose first key is beyond the seq length becomes a constant. Long comment explains that WE/game/Magos all disagree on the mixed case, so only this case is handled. Fixes e.g. HeroMountainKing.
  - copy every key with start <= frame <= end (and its inTan/outTan when interpolationType > 1)
  - if 0 keys -> constant=true, frames[0]=start, values[0]=defval
  - if 1 key  -> constant=true
  - else constant = all values identical

SdSequence.getValue(out, frame)  ('Fixed implementation copied directly from Retera's code. Thank you!'):
  if (constant || frame < start) { copy(out, values[0]); return -1; }
  let s = -1, e = -1, L = frames.length, Lm1 = L-1;
  if (frame < frames[0] || frame >= frames[Lm1]) { s = Lm1; e = 0; }        // WRAP-AROUND: last key -> first key
  else { for (i=1..L-1) if (frames[i] > frame) { s = i-1; e = i; break; } }
  let startFrame = frames[s]; const endFrame = frames[e];
  let dt = endFrame - startFrame;
  if (dt < 0) { dt += (this.end - this.start); if (frame < startFrame) startFrame = endFrame; }
  const t = (dt == 0) ? 0 : (frame - startFrame) / dt;
  interpolate(out, values, inTans, outTans, s, e, t);

Sd.getValue(out, sequence, frame, counter):
  if (globalSequence) return globalSequence.getValue(out, counter % globalSequence.end);
  return sequences[sequence].getValue(out, frame);
(counter is a monotonically increasing ms accumulator on the instance; frame is the current frame within the sequence interval.)

Sd.isVariant(sequence) -> !constant. Used to build per-sequence 'variants' bitmaps so unchanged tracks are skipped each frame (see AnimatedObject.addVariants / addVariantIntersection).

INTERPOLATORS (src/common/math.ts + gl-matrix):
  lerp(a,b,t) = a + t*(b-a)
  hermite(a, aOutTan, bInTan, b, t):
     t2 = t*t; f1 = t2*(2t-3)+1; f2 = t2*(t-2)+t; f3 = t2*(t-1); f4 = t2*(3-2t);
     return a*f1 + aOutTan*f2 + bInTan*f3 + b*f4
  bezier(a, aOutTan, bInTan, b, t):
     u = 1-t; t2 = t*t; u2 = u*u;
     f1 = u2*u; f2 = 3*t*u2; f3 = 3*t2*u; f4 = t2*t;
     return a*f1 + aOutTan*f2 + bInTan*f3 + b*f4
  Scalar: DontInterp->values[s]; Linear->lerp; Hermite->hermite; Bezier->bezier
  Vector3: same four, componentwise (vec3.lerp / vec3.hermite / vec3.bezier)
  Quaternion: DontInterp->copy; Linear->quat.slerp; Hermite AND Bezier -> quat.sqlerp (SQUAD)

Retera's identical squad, com/hiveworkshop/wc3/mdl/QuaternionRotation.java:361 ghostwolfSquad(out,a,aOutTan,bInTan,b,t):
     slerp(temp1, a, b, t); slerp(temp2, aOutTan, bInTan, t); slerp(out, temp1, temp2, 2*t*(1-t));
That 3-slerp form is exactly what you should write in C# (System.Numerics.Quaternion.Slerp x3).
Retera's MathUtils.hermite/bezier (com/hiveworkshop/wc3/util/MathUtils.java:48-69) are byte-identical to ghostwolf's.

InterpolationType enum (parsers/mdlx/animations.ts): DontInterp=0, Linear=1, Hermite=2, Bezier=3. inTans/outTans are present in the file only when interpolationType > 1.

_evidence: scratchpad/mdxv/src/viewer/handlers/mdx/sd.ts (full, 329 lines) ; scratchpad/mdxv/src/common/math.ts:29-62 ; scratchpad/rms/craft3data/src/com/hiveworkshop/wc3/mdl/QuaternionRotation.java:361 ; scratchpad/rms/craft3data/src/com/hiveworkshop/wc3/util/MathUtils.java:48-69_

## [certain] Retera's own evaluator (AnimFlag.interpolateAt) is a much messier binary-search-per-call design with several documented model-specific hacks; use it only as a source of edge cases, not as the algorithm.

File: com/hiveworkshop/wc3/mdl/AnimFlag.java, ~2900 lines. Key entry point: `Object interpolateAt(AnimatedRenderEnvironment env, Object identity, LayerShader shaderType)` at line 2550. It is untyped (returns Object; Double / Vertex / QuaternionRotation / Integer) — do not replicate that.

Mechanics worth stealing:
  - ceilIndex(time)/floorIndex(time) are binary searches over the shared `times` list (lines 2418-2486), adapted from geeksforgeeks ceiling/floor-in-sorted-array. ceilIndex returns times.size()-1 when no ceiling exists; floorIndex returns -1.
  - Documented quirks/hacks, each with a named culprit model:
      * `if (ceilIndex < floorIndex) ceilIndex = floorIndex;` — comment: 'retarded repeated keyframes issue, see Peasant's Bone_Chest at time 18300'. DUPLICATE KEYFRAME TIMES EXIST IN SHIPPING MODELS.
      * Global sequence path: if floorIndexTime > globalSeq duration -> return values.get(floorIndex) ('out of range global sequences end up just using the higher value keyframe'). If ceilIndexTime < 0 -> return identity. If floor<0 && ceil>globalSeq -> identity. If floor<0 -> floorValue = identity.
      * Non-global path: if the whole sequence range collapses to one keyframe index and that key lies inside [start,end], return it directly (a fast constant path equivalent to ghostwolf's SdSequence.constant).
      * The wrap-around branch (`ceilIndexTime > animation.getEnd() || (ceilIndexTime < time && times.get(floorAnimEndIndex) < time)`) carries the comment 'NOTE: we just let it be in this case, based on Water Elemental's birth'.
  - HD-only override at line ~2680: `if (shaderType == LayerShader.HD && typeid == ALPHA && interp == DONT_INTERP) interp = LINEAR;` — Reforged HD alpha tracks are forced to LINEAR even when the file says DontInterp. ghostwolf has no equivalent. If your Reforged fades look steppy, this is the fix.
  - Identity/default values (AnimFlag.identity()): ALPHA->1.0, TRANSLATION->(0,0,0), SCALING and COLOR->(1,1,1), ROTATION->(0,0,0,1). NOTE: Retera's COLOR identity is WHITE (1,1,1); ghostwolf's colorDefval is BLACK (0,0,0). Retera is right.
  - `if (typeid == ROTATION && values.get(0) instanceof Double) typeid = ALPHA;` — camera rotation (KCRL) is a scalar stored in a rotation-typed flag. Handle KCRL as float.
Also note ghostwolf's own comment in sd.ts: the SdSequence.getValue body was 'copied directly from Retera's code', so the two converge — Retera's per-call search is just the unoptimized version of the same semantics.

_evidence: scratchpad/rms/craft3data/src/com/hiveworkshop/wc3/mdl/AnimFlag.java:2418-2810_

## [certain] World-matrix composition: local TRS is built with a PIVOT ORIGIN (fromRotationTranslationScaleOrigin), then world = parent.world * local. Both projects use the identical formula, lifted from gl-matrix.

C#-ready formula (Retera com/hiveworkshop/wc3/util/MathUtils.java:73-110; gl-matrix mat4.fromRotationTranslationScaleOrigin used by ghostwolf skeletalnode.ts:169). Column-major, m[col][row] naming as in Java LWJGL (mNM = column N, row M):
  x2=2x, y2=2y, z2=2z
  xx=x*x2, xy=x*y2, xz=x*z2, yy=y*y2, yz=y*z2, zz=z*z2, wx=w*x2, wy=w*y2, wz=w*z2
  m00=(1-(yy+zz))*sx  m01=(xy+wz)*sx      m02=(xz-wy)*sx      m03=0
  m10=(xy-wz)*sy      m11=(1-(xx+zz))*sy  m12=(yz+wx)*sy      m13=0
  m20=(xz+wy)*sz      m21=(yz-wx)*sz      m22=(1-(xx+yy))*sz  m23=0
  m30=(v.x+p.x) - (m00*p.x + m10*p.y + m20*p.z)
  m31=(v.y+p.y) - (m01*p.x + m11*p.y + m21*p.z)
  m32=(v.z+p.z) - (m02*p.x + m12*p.y + m22*p.z)
  m33=1
where q=(x,y,z,w) local rotation, v = local translation, s = local scale, p = the node's PIVOT POINT (from the PIVT chunk, indexed by objectId).

Then (skeletalnode.ts::recalculateTransformation, lines 168-206):
  worldMatrix = parent.worldMatrix * localMatrix
  worldLocation = worldMatrix * pivot           (written out longhand, not via transformMat4)
  inverseWorldLocation = -worldLocation
  worldRotation = parent.worldRotation * computedRotation
  inverseWorldRotation = conjugate(worldRotation) = (-x,-y,-z,+w)
  worldScale = parent.worldScale * computedScaling ; inverseWorldScale = 1/worldScale

Inheritance flags (GenericObject.flags, parsers/mdlx/genericobject.ts):
  0x1 DontInheritTranslation, 0x2 DontInheritScaling, 0x4 DontInheritRotation,
  0x8 Billboarded, 0x10 BillboardedLockX, 0x20 BillboardedLockY, 0x40 BillboardedLockZ, 0x80 CameraAnchored
Application (skeletalnode.ts:97-166):
  dontInheritTranslation: computedLocation = parent.inverseWorldLocation + worldLocation + localLocation
  dontInheritScaling:     computedScaling  = parent.inverseWorldScale * instance.worldScale * localScale
  dontInheritRotation:    computedRotation = (parent.inverseWorldRotation * instance.worldRotation) * computedRotation
Retera's RenderNode.recalculateTransformation only implements dontInheritScaling (and does it differently: computedScaling = parentInverseScale*localScale, worldScale = localScale) and ignores dontInheritTranslation/Rotation entirely. ghostwolf is more complete — port ghostwolf.

Self-parenting guard (viewer/handlers/mdx/genericobject.ts): `if (object.objectId === object.parentId) this.parentId = -1;` — shipping models do this.
Pivot fallback: `this.pivot = model.pivotPoints[object.objectId] || vec3.create();` — PIVT can be shorter than the object count.
Root: parentId === -1 -> node.parent = the ModelInstance itself (which supplies identity world matrix/rotation/scale), so there is never a null-parent branch.

_evidence: scratchpad/mdxv/src/viewer/skeletalnode.ts:78-207 ; scratchpad/rms/craft3data/src/com/hiveworkshop/wc3/util/MathUtils.java:73-110 ; scratchpad/rms/craft3data/src/com/hiveworkshop/wc3/mdl/render3d/RenderNode.java:95-165_

## [certain] Node hierarchy must be flattened into a parents-before-children order once at model load; per-frame updating is then a flat loop with dirty propagation.

viewer/handlers/mdx/model.ts:
  genericObjects = [...bones, ...lights, ...helpers, ...attachments, ...particleEmitters, ...particleEmitters2, ...ribbonEmitters, ...eventObjects, ...collisionShapes]   <-- THIS ORDER IS THE objectId ORDER. Cameras are NOT generic objects (they have no objectId).
  objectId is assigned by a single counter incremented in exactly that order.
  setupHierarchy(parent) { for i in genericObjects: if (obj.parentId === parent) { hierarchy.push(i); setupHierarchy(obj.objectId); } }  called with -1.
  sortedGenericObjects[i] = genericObjects[hierarchy[i]]
modelinstance.ts mirrors it: sortedNodes[i] = nodes[hierarchy[i]].
Caution: setupHierarchy is recursive with no cycle detection. A model with a parent cycle will stack-overflow, and a node whose parentId points at a nonexistent object simply never enters `hierarchy` (so it is silently dropped from updates but still occupies a slot in `nodes`). In C# use an explicit stack + a visited set, and append orphans at the end.

Per-frame (modelinstance.ts::updateNodes(dt, forced)):
  for i in sortedNodes:
    genericObject = sortedGenericObjects[i]; node = sortedNodes[i]; parent = node.parent;
    wasDirty = forced || parent.wasDirty || genericObject.anyBillboarding;
    if (forced || variants['generic'][sequence]) { wasDirty = true;
        if (forced||variants['translation'][seq]) getTranslation(node.localLocation, seq, frame, counter);
        if (forced||variants['rotation'][seq])    getRotation(node.localRotation, ...);
        if (forced||variants['scale'][seq])       getScale(node.localScale, ...); }
    node.wasDirty = wasDirty;
    if (wasDirty) node.recalculateTransformation(this);
    if (node.object) { getVisibility(visHeap,...); if (visHeap[0] > 0) node.object.update(dt); }
    for child in node.children: { if (wasDirty) child.recalculateTransformation(); child.update(dt); }
`anyBillboarding` forces a recompute every frame because billboarding depends on the camera.
Retera's equivalent RenderModel.updateNodes has all the variant checks stubbed to `true` (`if (forced || true /* variants */)`) — i.e. it recomputes everything every frame. ghostwolf's variant bitmaps are a genuine, cheap optimization: AnimatedObject.addVariants(trackName, variantName) builds a Uint8Array[sequenceCount] of !isVariant, and addVariantIntersection(['translation','rotation','scale'],'generic') ORs them.
Also Retera gates node updates on visibility: `objectVisible = idObject.getRenderVisibility(env) >= 0.02` (RenderModel.MAGIC_RENDER_SHOW_CONSTANT = 0.02) and skips the whole subtree when the parent is invisible — a correctness hazard (an invisible bone's children stop animating), don't copy it.

_evidence: scratchpad/mdxv/src/viewer/handlers/mdx/model.ts:236-258 ; scratchpad/mdxv/src/viewer/handlers/mdx/modelinstance.ts:265-329 ; scratchpad/rms/craft3data/src/com/hiveworkshop/wc3/mdl/render3d/RenderModel.java:170-300_

## [certain] Billboarding: full billboard cancels parent+camera rotation and applies an MDX-specific basis change (-90deg Y then -90deg X); axis-locked billboards use an atan2 of the camera ray transformed into node-local space.

viewer/skeletalnode.ts:113-161.
FULL BILLBOARD (flag 0x8):
  computedRotation = parent.inverseWorldRotation;
  computedRotation = computedRotation * scene.camera.inverseRotation;
  this.convertBasis(computedRotation);                 // virtual
  computedRotation = computedRotation * localRotation;
viewer/handlers/mdx/node.ts overrides convertBasis for MDX:
  quat.rotateY(rotation, rotation, -PI/2);
  quat.rotateX(rotation, rotation, -PI/2);
(Retera's RenderModel does the same with two static quaternions HALF_PI_Y = axisAngle(0,1,0,-PI/2) and HALF_PI_X = axisAngle(1,0,0,-PI/2), applied in that order: localRotation = HALF_PI_X * HALF_PI_Y * camera.inverseRotation * parent.inverseWorldRotation.)

AXIS-LOCKED (0x10 X / 0x20 Y / 0x40 Z), skeletalnode.ts:125-157:
  if (billboardedX) { computedScaling = copy(localScale); computedScaling[2] *= -1; }
     // verbatim comment, originally from Retera's Warsmash: 'It took me many hours to deduce from playing around
     // that this negative one multiplier should be here. I suggest a lot of testing before you remove it.'
  q2 = conjugate-ish of localRotation: (-lx, -ly, -lz, lw)   // note w is NOT negated in the code; only xyz are set
  q2 = q2 * parent.inverseWorldRotation
  cameraRay = vec3.transformQuat(scene.camera.billboardedVectors[6], q2)     // index 6 = (0,0,1)
  if (billboardedX) q2 = setAxisAngle(UNIT_X, atan2(cameraRay[2], cameraRay[1]));
  else if (billboardedY) q2 = setAxisAngle(UNIT_Y, atan2(-cameraRay[2], cameraRay[0]));
  else                   q2 = setAxisAngle(UNIT_Z, atan2(cameraRay[1], cameraRay[0]));
  computedRotation = localRotation * q2;
Only ONE billboard mode is honoured per node — modelinstance.ts::initNode uses an if/else-if chain: Billboarded wins, then X, then Y, then Z.
camera.billboardedVectors is a 7-element array; [6] is the forward/Z basis vector. spacialVectors in Retera's RenderModel documents the layout: {(-1,1,0),(1,1,0),(1,-1,0),(-1,-1,0),(1,0,0),(0,1,0),(0,0,1)}.
Retera's RenderNode/RenderModel billboardedY and billboardedZ paths are explicitly unfinished (`// TODO not correct`, `// TODO face camera`). PORT GHOSTWOLF'S, NOT RETERA'S, FOR BILLBOARDING.

_evidence: scratchpad/mdxv/src/viewer/skeletalnode.ts:113-161 ; scratchpad/mdxv/src/viewer/handlers/mdx/node.ts ; scratchpad/rms/craft3data/src/com/hiveworkshop/wc3/mdl/render3d/RenderModel.java:216-270_

## [certain] GEOA (geoset animation) drives per-geoset color+alpha; colors are stored RGB in the static field but BGR in the animated track, and the SD shader re-swizzles with .bgra.

Parser (parsers/mdlx/geosetanimation.ts): i32 inclusiveSize; f32 alpha; u32 flags; f32[3] color; i32 geosetId; then tracks. Header is 28 bytes -> readAnimations(size-28). flags: 0x1 = DropShadow, 0x2 = Color (i.e. 'the color field is meaningful').
Runtime (viewer/handlers/mdx/geosetanimation.ts):
  this.color = vec3(color[2], color[1], color[0]);   // comment: 'Stored as RGB, but animated colors are stored as BGR, so sizzle.'
  getAlpha -> getScalarValue('KGAO', ..., default this.alpha)
  getColor -> getVectorValue('KGAC', ..., default this.color)
Geoset<->GeosetAnimation binding (viewer/handlers/mdx/geoset.ts ctor): linear scan `for (ga of model.geosetAnimations) if (ga.geosetId === index) this.geosetAnimation = ga;` — LAST match wins if a model has duplicates.
Per-frame (modelinstance.ts::updateBatches):
  geosetColor[0..2] = KGAC value (or the static, or 1,1,1 when there is no GEOA); geosetColor[3] = KGAO value.
Rendering (batchgroup.ts SD path): a batch is SKIPPED entirely when `geosetColor[3] > 0.01 && layerAlpha > 0.01` fails. Then `gl.uniform4fv(u_geosetColor, geosetColor)`.
sd.vert.ts line 45:  v_color = u_vertexColor * u_geosetColor.bgra * vec4(1.0, 1.0, 1.0, u_layerAlpha);
  -> the BGR->RGB swap happens IN THE SHADER via .bgra, on top of the CPU-side swizzle in the ctor. Net effect: KGAC values are consumed as-is from the file in BGR order, ending up correct. Be careful not to double-swizzle when you port.
sd.frag.ts: color = texel * v_color; then `if (u_filterMode == 1.0 && color.a < 0.75) discard;` and `if (u_filterMode >= 5.0 && color.a < 0.02) discard;` (the second is 'close to 0 alpha' for Modulate/Modulate2x, which otherwise render as black).
HD path IGNORES geosetColor entirely — batchgroup.ts's isHd branch only reads layerAlphas[diffuseLayer.index]. That is a real gap for Reforged models that fade via GEOA.
Retera does it right: PerspectiveViewport.render(Geoset,...) lines 851-935:
  geosetAnimVisibility = geosetAnim.getRenderVisibility(env); if (< 0.02) return;
  glColor4f(renderColor.z, renderColor.y, renderColor.x, geosetAnimVisibility * layerVisibility)   // z,y,x = BGR->RGB
  and this applies to HD layers too, feeding v_color in the HD shader.
GeosetAnim.getRenderVisibility (com/hiveworkshop/wc3/mdl/GeosetAnim.java:270) returns the interpolated 'Alpha' flag or the static alpha.

_evidence: scratchpad/mdxv/src/parsers/mdlx/geosetanimation.ts ; scratchpad/mdxv/src/viewer/handlers/mdx/geosetanimation.ts ; scratchpad/mdxv/src/viewer/handlers/mdx/shaders/sd.vert.ts:45 ; scratchpad/rms/craft3data/src/com/hiveworkshop/wc3/gui/modeledit/PerspectiveViewport.java:851-935_

## [certain] Skinning: classic MDX uses matrix GROUPS (unweighted, averaged); Reforged uses a SKIN chunk with 4 bone indices + 4 byte weights. mdx-m3-viewer supports three vertex layouts and picks per geoset.

SkinningType enum (viewer/handlers/mdx/batch.ts): VertexGroups=0 (SD, <=4 bones/vertex), ExtendedVertexGroups=1 (SD, <=8 bones/vertex), Skin=2 (HD/Reforged, 4 bones + 4 weights).

GEOS classic data (parsers/mdlx/geoset.ts::readMdx, sub-chunks in FIXED order — no tags are validated, only skipped):
  'VRTX' u32 count -> f32[count*3] vertices
  'NRMS' u32 count -> f32[count*3] normals
  'PTYP' u32 n -> u32[n] faceTypeGroups   (4 = GL_TRIANGLES)
  'PCNT' u32 n -> u32[n] faceGroups
  'PVTX' u32 n -> u16[n] faces
  'GNDX' u32 n -> u8[n]  vertexGroups     (per-vertex index INTO matrixGroups)
  'MTGC' u32 n -> u32[n] matrixGroups     (sizes: group i uses matrixGroups[i] entries)
  'MATS' u32 n -> u32[n] matrixIndices    (flat concatenation of all groups; each entry is an objectId)
  u32 materialId; u32 selectionGroup; u32 selectionFlags   (4 = Unselectable)
  if version>800: i32 lod; char[80] lodName
  Extent extent (28 bytes: radius, min[3], max[3])
  u32 sequenceExtentCount, then that many Extents
  if version>800: OPTIONAL 'TANG' u32 count -> f32[count*4]; OPTIONAL 'SKIN' u32 byteCount -> u8[byteCount]
     (both are peeked with readBinary(4) and stream.skip(-4) on mismatch — 'Non-reforged models that come with
      reforged are saved with version >800, however they don't have TANG and SKIN.')
  'UVAS' u32 setCount, then per set: 'UVBS' u32 count -> f32[count*2]
SKIN layout: every 8 consecutive BYTES = [B0,B1,B2,B3,W0,W1,W2,W3], weights normalized as Wn/255.
TANG layout: 4 floats per vertex, .xyz = tangent, .w = bitangent handedness (+/-1).

CPU-side conversion (viewer/handlers/mdx/setupgeosets.ts) — port this:
  per geoset: if (geoset.lod !== 0 && geoset.lod !== -1) SKIP IT ENTIRELY (only LOD0 is rendered).
  if skin.length -> SkinningType.Skin, 8 bytes/vertex, use as-is.
  else: biggestGroup = max(matrixGroups); maxBones = (biggestGroup > 4) ? 8 : 4;
        skin = new Uint8Array(vertices * (maxBones+1));
        slice matrixIndices into groups by matrixGroups sizes;
        for each vertex i: mg = matrixGroups[vertexGroups[i]];
            if (mg) { n = min(mg.length, maxBones); for j<n: skin[i*(maxBones+1)+j] = mg[j] + 1;  // +1 so 0 == 'no matrix'
                      skin[i*(maxBones+1)+maxBones] = n; }
            // comment: 'Somehow in some bad models a vertex group index refers to an invalid matrix group.
            //           Such models are still loaded by the game.'  -> leave all-zero, boneNumber 0.
  If model.bones.length > 255 the whole skin array becomes Uint16 (skinDataType = GL_UNSIGNED_SHORT).
     ** NOTE THE BUG: the threshold is bones.length, but the matrix indices are objectIds spanning bones+lights+
        helpers+attachments+... A model with 200 bones and 100 helpers overflows the byte. Use
        genericObjects.length > 255 in your port. **

GPU-side (shaders/transforms.glsl.ts):
  #ifdef SKIN:
    attribute vec4 a_bones; attribute vec4 a_weights;
    mat4 bone; bone += fetchMatrix(a_bones[k], 0.0) * a_weights[k]  for k=0..3;
    position = bone * vec4(position,1); normal/tangent/binormal = mat3(bone) * ...  (NOT re-normalized)
  #else (vertex groups):
    attribute vec4 a_bones; [#ifdef EXTENDED_BONES attribute vec4 a_extendedBones;] attribute float a_boneNumber;
    mat4 getVertexGroupMatrix() {
      mat4 bone;
      if (a_boneNumber > 0.0) {                 // comment: 'For the broken models out there, since the game supports this.'
        for i in 0..3: if (a_bones[i] > 0.0) bone += fetchMatrix(a_bones[i] - 1.0, 0.0);
        #ifdef EXTENDED_BONES: same for a_extendedBones
      }
      return bone / a_boneNumber;               // <-- UNWEIGHTED AVERAGE; division by 0 yields NaN when boneNumber==0
    }
    transformVertexGroups: position = bone*pos; normal = normalize(mat3(bone)*normal);
    transformVertexGroupsHD: same + normalize tangent and binormal
  ** a_boneNumber == 0 gives bone/0 = NaN/Inf. Guard it in C# (return identity). Retera does exactly this:
     PerspectiveViewport.java:620 `if (!processedBones) skinBonesMatrixSumHeap.setIdentity(); else if (sumWeight>0) divide by sumWeight`. **

BONE PALETTE (viewer/handlers/shaders/bonetexture.glsl.ts + gl/datatexture.ts):
  A float RGBA texture of width = bones.length*4, height 1, one mat4 per bone as 4 consecutive texels.
  uniform sampler2D u_boneMap; uniform float u_vectorSize (= 1/textureWidth); uniform float u_rowSize (= 1);
  mat4 fetchMatrix(float column, float row) {
    column *= u_vectorSize * 4.0; row *= u_rowSize;
    column += 0.5 * u_vectorSize; row += 0.5 * u_rowSize;    // half-texel offset; comment explains NPOT sampling errors
    return mat4(tex(column), tex(column+u_vectorSize), tex(column+2*u_vectorSize), tex(column+3*u_vectorSize));
  }
  Bound to texture unit 15. Created only `if (model.bones.length)` — modelinstance.ts:167. Instances of boneless
  models have boneTexture === null and batchgroup.ts sets `u_hasBones = 0`, skipping all skinning.
  IMPORTANT: it is indexed by BONE index (0..bones.length-1), but the matrix indices in MATS/SKIN are OBJECT IDs.
  Because bones are emitted first in genericObjects and objectId is assigned in that order, boneIndex == objectId
  for bones — which is why this works, and why it silently breaks for any matrix index pointing at a helper/attachment.
  Retera sidesteps this by resolving GeosetVertexBoneLink.bone to an actual node and reading getWorldMatrix().
  In D3D11/C#: use a StructuredBuffer<matrix> or a cbuffer array of float3x4, indexed the same way; you do not need
  the texture trick.

_evidence: scratchpad/mdxv/src/parsers/mdlx/geoset.ts:46-105 ; scratchpad/mdxv/src/viewer/handlers/mdx/setupgeosets.ts ; scratchpad/mdxv/src/viewer/handlers/mdx/shaders/transforms.glsl.ts ; scratchpad/mdxv/src/viewer/handlers/shaders/bonetexture.glsl.ts ; scratchpad/mdxv/src/viewer/handlers/mdx/geoset.ts (bindVertexGroups/bindVertexGroupsExtended/bindSkin) ; scratchpad/rms/craft3data/src/com/hiveworkshop/wc3/gui/modeledit/PerspectiveViewport.java:585-660_

## [certain] Batching and draw ordering: opaque (filterMode<2) first in geoset order, then translucent sorted by filterMode then by priorityPlane, merged with emitters.

viewer/handlers/mdx/setupgroups.ts:
  split model.batches: filterMode < 2 (None=0, Transparent=1) -> opaque; else translucent.
  opaque: walk in order, start a new BatchGroup whenever (skinningType, isHd) changes. No sorting.
  translucent: first `sort((a,b) => a.layer.filterMode - b.layer.filterMode)`,
               then merge with model.eventObjects + particleEmitters2 + ribbonEmitters and
               `sort((a,b) => getPrio(a) - getPrio(b))` where getPrio = layer.priorityPlane for Batch/RibbonEmitterObject,
               .priorityPlane for ParticleEmitter2Object, 0 for event objects.
               Note: Array.prototype.sort is stable in modern JS, so the filterMode ordering survives ties.
  A Batch for an HD material is ONE batch referencing the Material (layer = material.layers[0]);
  for an SD material it is ONE BATCH PER LAYER (setupgeosets.ts: `for (const layer of material.layers) model.batches.push(new Batch(...))`) — that is how SD multi-layer/multitexture materials get drawn.

Layer render state (viewer/handlers/mdx/layer.ts + filtermode.ts):
  FilterMode enum: None=0, Transparent=1, Blend=2, Additive=3, AddAlpha=4, Modulate=5, Modulate2x=6.
  `if (filterMode > Modulate2x) filterMode = Blend;`  (clamp garbage)
  blend funcs:  Blend -> (SRC_ALPHA, ONE_MINUS_SRC_ALPHA);  Additive -> (SRC_ALPHA, ONE);  AddAlpha -> (SRC_ALPHA, ONE);
                Modulate -> (ZERO, SRC_COLOR);  Modulate2x -> (DST_COLOR, SRC_COLOR);  else (0,0)
  blended = filterMode > Transparent
  depthMaskValue = (filterMode === None || filterMode === Transparent)
  Layer flags (parsers/mdlx/layer.ts): Unshaded=0x1, SphereEnvMap=0x2, TwoSided=0x10, Unfogged=0x20,
                                       NoDepthTest=0x40, NoDepthSet=0x80, Unlit=0x100
  bind(): twoSided -> disable CULL_FACE; noDepthTest -> disable DEPTH_TEST; noDepthSet -> depthMask(false) else depthMask(depthMaskValue).
Retera's identical mapping is in PerspectiveViewport.bindLayer() (~line 730), plus it uses a real GL alpha test for TRANSPARENT: `glAlphaFunc(GL_GREATER, 0.75f)`.
Material flags (parsers/mdlx/material.ts): ConstantColor=0x1, TwoSided=0x2 (v900+ only), SortPrimsNearZ=0x8, SortPrimsFarZ=0x10, FullResolution=0x20.
  ** Retera reads TwoSided from material flag 0x02 only when isShaderStringSupported(version); ghostwolf always exposes it but never uses material.flags at render time at all. **

_evidence: scratchpad/mdxv/src/viewer/handlers/mdx/setupgroups.ts ; scratchpad/mdxv/src/viewer/handlers/mdx/layer.ts ; scratchpad/mdxv/src/viewer/handlers/mdx/filtermode.ts ; scratchpad/mdxv/src/parsers/mdlx/{layer,material}.ts_

## [certain] Sequence playback, global-sequence counter, and bounds fallback for corrupt extents.

viewer/handlers/mdx/modelinstance.ts::updateAnimations(dt):
  if (sequence !== -1) {
    interval = model.sequences[seq].interval;  frameTime = dt * 1000;
    this.frame += frameTime;  this.counter += frameTime;  this.allowParticleSpawn = true;
    if (this.frame >= interval[1]) {
      if (loopMode === 2 || (loopMode === 0 && sequence.nonLooping === 0)) { frame = interval[0]; resetEventEmitters(); }
      else { frame = interval[1]; counter -= frameTime; allowParticleSpawn = false; }
      sequenceEnded = true;
    } else sequenceEnded = false;
  }
  if (sequence !== -1 || forced) { updateNodes(dt, forced); updateBoneTexture(); updateBatches(forced); }
  this.forced = false;
loopMode: 0 = follow the model's nonLooping flag, 1 = never loop, 2 = always loop.
`counter` is the GLOBAL-SEQUENCE clock: never reset, and Sd.getValue does `globalSequence.getValue(out, counter % globalSequence.end)` where end = the GLBS duration in ms.
setSequence(id): out-of-range -> sequence=-1, frame=0, allowParticleSpawn=false; else frame = interval[0]. Always resets event emitters + attachments and sets forced=true.

SEQS record (132 bytes, parsers/mdlx/sequence.ts): char[80] name; u32[2] interval{start,end}; f32 moveSpeed;
  u32 nonLooping; f32 rarity; u32 syncPoint; Extent extent(28).  80+8+4+4+4+4+28 = 132. Confirmed by model.ts `size/132`.
TEXS record (268 bytes, parsers/mdlx/texture.ts): u32 replaceableId; char[260] path; u32 wrapMode.
  WrapMode: RepeatBoth=0, WrapWidth=1, WrapHeight=2, WrapBoth=3. viewer/handlers/mdx/texture.ts maps
  WrapWidth->wrapS=REPEAT(0x2901) else CLAMP_TO_EDGE(0x812F); WrapHeight->wrapT likewise.
  ** Note the naming is inverted from intuition: 'RepeatBoth = 0' actually means CLAMP on both axes in the handler. **

CORRUPT EXTENTS (viewer/bounds.ts::fromExtents):
  w = max[0]-min[0]; d = max[1]-min[1]; h = max[2]-min[2];
  center = min + size/2;  r = Math.max(0, Math.max(w,d,h)/2);
  comment: 'Ensure the radius is actually 0 or bigger. Some models apparently have reversed extents, go figure.'
modelinstance.getBounds(): if sequence === -1 return model.bounds; bounds = model.sequences[seq].bounds; if (bounds.r === 0) return model.bounds; return bounds.
  -> a per-sequence extent of radius 0 falls back to the model extent. Port both guards.
Retera similarly guards degenerate faces: Geoset.java skips any triangle where an index equals 0xFFFF, and converts the signed Java short with `x & 0xFFFF`; vertexGroups are read as `(256 + b) % 256` (unsigned byte) and set to -1 when the GNDX array is shorter than the vertex count.

_evidence: scratchpad/mdxv/src/viewer/handlers/mdx/modelinstance.ts:462-565 ; scratchpad/mdxv/src/viewer/bounds.ts ; scratchpad/mdxv/src/parsers/mdlx/{sequence,texture}.ts ; scratchpad/rms/craft3data/src/com/hiveworkshop/wc3/mdl/Geoset.java:70-135_

## [certain] Animation-track tag table (MDX 4CC -> MDL name -> value type). This is the complete map; port it as a static dictionary.

src/parsers/mdlx/animationmap.ts. Value types: Uint=u32 scalar, Float=f32 scalar, Vector3=f32[3], Vector4=f32[4] (quaternion x,y,z,w).
Layer:            KMTF TextureID/Uint, KMTA Alpha/Float, KMTE EmissiveGain/Float, KFC3 FresnelColor/Vector3, KFCA FresnelOpacity/Float, KFTC FresnelTeamColor/Uint
TextureAnimation: KTAT Translation/Vector3, KTAR Rotation/Vector4, KTAS Scaling/Vector3
GeosetAnimation:  KGAO Alpha/Float, KGAC Color/Vector3
GenericObject:    KGTR Translation/Vector3, KGRT Rotation/Vector4, KGSC Scaling/Vector3
Light:            KLAS AttenuationStart/Float, KLAE AttenuationEnd/Float, KLAC Color/Vector3, KLAI Intensity/Float, KLBI AmbIntensity/Float, KLBC AmbColor/Vector3, KLAV Visibility/Float
Attachment:       KATV Visibility/Float
ParticleEmitter:  KPEE EmissionRate, KPEG Gravity, KPLN Longitude, KPLT Latitude, KPEL LifeSpan, KPES InitVelocity, KPEV Visibility (all Float)
ParticleEmitter2: KP2S Speed, KP2R Variation, KP2L Latitude, KP2G Gravity, KP2E EmissionRate, KP2N Width, KP2W Length, KP2V Visibility (all Float)
                  ** note KP2N='Width' and KP2W='Length' — the letters are swapped relative to intuition **
Popcorn (CORN):   KPPA Alpha/Float, KPPC Color/Vector3, KPPE EmissionRate/Float, KPPL LifeSpan/Float, KPPS Speed/Float, KPPV Visibility/Float
RibbonEmitter:    KRHA HeightAbove/Float, KRHB HeightBelow/Float, KRAL Alpha/Float, KRCO Color/Vector3, KRTX TextureSlot/Uint, KRVS Visibility/Float
Camera:           KCTR Translation/Vector3 (source), KTTR Translation/Vector3 (target), KCRL Rotation/Float (scalar roll!)

On-disk track format (parsers/mdlx/animations.ts::readMdx, after the 4-byte tag):
  u32 tracksCount; u32 interpolationType; i32 globalSequenceId (-1 = none);
  then tracksCount times: i32 frame; VALUE; if (interpolationType > 1) { VALUE inTan; VALUE outTan; }
getByteLength = 16 + tracksCount*(4 + (interpType>1 ? 3 : 1)*sizeof(VALUE)).
AnimatedObject.readAnimations(stream, size) just loops `while (index < end) { name = readBinary(4); new animationMap[name][1]().readMdx(stream, name); }` — an unknown 4CC throws. Add a fallback that bails out of the object rather than the whole model.
GenericObject.readMdx: u32 inclusiveSize; char[80] name; i32 objectId; i32 parentId; u32 flags; then readAnimations(size - 96).
  KGTR/KGRT/KGSC belong to the GenericObject part; every other track belongs to the subclass — see GenericObject.eachAnimation(wantGeneric) and getGenericByteLength() (=96 + only the KG* tracks). This matters for writing: the generic inclusiveSize covers ONLY the KG* tracks, and subclass-specific tracks are written after the subclass fields.

_evidence: scratchpad/mdxv/src/parsers/mdlx/animationmap.ts ; scratchpad/mdxv/src/parsers/mdlx/animations.ts:27-75 ; scratchpad/mdxv/src/parsers/mdlx/genericobject.ts:36-60,155-185_

## [likely] Reforged texture pipeline: .dds replaces .blp, and mdx-m3-viewer's DDS handler drops DXT1 alpha — a very likely cause of the reported 'Reforged texture ALPHA MAPPING' problem.

Path substitution (viewer/handlers/mdx/model.ts): `const texturesExt = reforged ? '.dds' : '.blp';` where reforged = parser.version > 800. When a TEXS entry has an empty path and replaceableId != 0, the path becomes `ReplaceableTextures\{replaceableIds[id]}{ext}`.
replaceableids.ts: 1 -> 'TeamColor/TeamColor00', 2 -> 'TeamGlow/TeamGlow00', 11 -> 'Cliff/Cliff0', 21 -> '' (all cursor models), 31 'LordaeronTree/LordaeronSummerTree', 32 'AshenvaleTree/AshenTree', 33 'BarrensTree/BarrensTree', 34 'NorthrendTree/NorthTree', 35 'Mushroom/MushroomTree', 36 'RuinsTree/RuinsTree', 37 'OutlandMushroomTree/MushroomTree'.
Retera's equivalent (PerspectiveViewport.loadToTexMap, line 260) covers only 1, 2, 11, and lumps every other non-zero id to 'replaceabletextures\lordaerontree\lordaeronsummertree'.

DDS support (src/parsers/dds/image.ts):
  Accepts FOURCC DXT1(0x31545844), DXT3(0x33545844), DXT5(0x35545844), ATI2/BC5(0x32495441).
  DX10 header (0x30315844) -> skip 20 bytes and map DXGI_FORMAT_BC1_UNORM(0x47)->DXT1, BC2_UNORM(0x4A)->DXT3,
  BC3_UNORM(0x4D)->DXT5, BC5_UNORM(0x53)->ATI2. ANYTHING ELSE THROWS.
  ** BC7 (DXGI_FORMAT_BC7_UNORM = 98/0x62) and BC7_UNORM_SRGB (99) are NOT handled. If Reforged 2.x ships BC7
     textures, mdx-m3-viewer simply fails on them. Check your install; if BC7 is present you need a BC7 decoder
     (or just hand the DDS straight to D3D11 — Direct3D 11 supports BC1-BC7 natively, so in WPF/D3D you can
     CreateTexture2D from the raw blocks and skip decoding entirely). **
  DXT1 = 8 bytes/block; DXT3/DXT5/RGTC = 16 bytes/block.

>>> THE LIKELY ALPHA BUG <<< viewer/handlers/dds/texture.ts:
  if (format === FOURCC_DXT1) internalFormat = COMPRESSED_RGB_S3TC_DXT1_EXT;
  — it uses the RGB (no-alpha) variant, not COMPRESSED_RGBA_S3TC_DXT1_EXT. Any DXT1 texture carrying 1-bit alpha
  renders fully opaque. Since Reforged HD relies on FilterMode.Transparent + `color.a < 0.75 -> discard`, a DXT1
  diffuse with punch-through alpha will show solid black/garbage where it should be cut out. In D3D11 just use
  DXGI_FORMAT_BC1_UNORM (which is RGBA) and you avoid this entirely.
  Also note ATI2/BC5 has NO GPU path at all in the handler (internalFormat stays 0), so BC5 normal maps are always
  CPU-decoded via decodeRgtc — fine, but slow.
  gl.pixelStorei(UNPACK_ALIGNMENT, 2) is set for DXT1/ATI2 because their 1x2 / 2x1 mipmaps are 2 bytes wide.

Second alpha consideration: the ORM texture's ALPHA channel is the team-color mask, NOT opacity. If you feed an
ORM DDS through a loader that premultiplies or discards alpha you lose all team coloring. Keep ORM alpha raw.

_evidence: scratchpad/mdxv/src/viewer/handlers/dds/texture.ts:26-40 ; scratchpad/mdxv/src/parsers/dds/image.ts:7-120 ; scratchpad/mdxv/src/viewer/handlers/mdx/model.ts:143-170 ; scratchpad/mdxv/src/viewer/handlers/mdx/replaceableids.ts_

## [certain] Retera's in-memory editable model (EditableModel / Geoset / GeosetVertex / IdObject) is the right shape for an EXPORTER, and is quite different from the render-oriented MdxModel.

com/hiveworkshop/wc3/mdl/:
  EditableModel (3875 lines) — the document. Holds geosets, materials, textures (Bitmap), idObjects (List<IdObject>),
    pivots, anims (Animation = a named [start,end] interval + ExtLog), globalSeqs, cameras, faceEffects, bindPose,
    formatVersion. Has `getIdObject(int)`, `sortedIdObjects(Class)`, extensive add/remove/rebuild-hierarchy logic,
    and doSavePreProcess/doPostRead passes.
  IdObject (abstract, 297 lines) — name, objectId, parentId, pivotPoint(Vertex), bindPose(float[12]),
    List<AnimFlag>, children. `enum NodeFlags { DONTINHERIT_TRANSLATION("DontInherit { Translation }"),
    DONTINHERIT_SCALING, DONTINHERIT_ROTATION, BILLBOARDED("Billboarded"),
    BILLBOARD_LOCK_X("BillboardedLockX","BillboardLockX"), BILLBOARD_LOCK_Y, BILLBOARD_LOCK_Z, CAMERA_ANCHORED }`.
    ** hasFlag() does a STRING COMPARE (RenderNode.java has the comment 'hasFlag is idiot code with string compare').
       Use a real [Flags] enum in C#. **
    Subclasses: Bone, Helper, Light, Attachment, ParticleEmitter, ParticleEmitter2, ParticleEmitterPopcorn,
    RibbonEmitter, EventObject, CollisionShape, Camera.SourceNode/TargetNode.
  Geoset — vertex:List<GeosetVertex>, normals, uvlayers:List<UVLayer>, triangles:List<Triangle>,
    matrix:List<Matrix> (a Matrix is just a list of bone ids = one MTGC group), anims:List<Animation> (per-sequence
    extents), materialID/material, selectionGroup, levelOfDetail + levelOfDetailName, skin:List<byte[8]>,
    tangents:List<float[4]>, geosetAnim:GeosetAnim, boolean skinFormat.
  GeosetVertex extends Vertex — normal:Normal, VertexGroup:int, tverts:List<TVertex>,
    links:List<GeosetVertexBoneLink>{short weight; Bone bone;}, triangles, skinBoneIndexes:byte[4], tangent:float[4].
    Helpers: initV900Tangent(), initV900Skin() (truncates links to 4 then equalizeWeights()),
    equalizeWeights() (weight = 255/n with the remainder added to link 0 — so weights always sum to exactly 255),
    un900Heuristic() (drops tangents/skin, removes null-bone and zero-weight links, re-equalizes) — this is the
    HD->SD downgrade path and is exactly the transform you need in reverse for building an .m3 skin.
  Layer — EnumMap<ShaderTextureTypeHD,Integer> shaderTextureIds + EnumMap<...,Bitmap> shaderTextures,
    LayerShader layerShader {SD,HD}, filterMode, flags, coordId, staticAlpha, emissive, fresnelColor/Opacity/TeamColor,
    List<AnimFlag>. getRenderTexture(env, model, ShaderTextureTypeHD) resolves the animated TextureID track named
    (TypeName + "TextureID") for non-Diffuse slots.
  AnimFlag — name:String, typeid:int, interpType:int, globalSeq:Integer, times:ArrayList<Integer>,
    values/inTans/outTans:ArrayList<Object>. Untyped; in C# make it AnimFlag<T> with T in {float, Vector3, Quaternion, uint}.
  Matrix — the MTGC/MATS group abstraction: a list of bone object-ids that a vertex group averages over.
For your .m3 exporter this is the model to mirror: you need per-vertex bone links with weights (SC2 M3 uses 4
weighted influences, same as Reforged SKIN), which maps directly onto GeosetVertex.links / skinBoneIndexes.
For SD models you must synthesize weights: Retera's equalizeWeights() (uniform 255/n) is exactly the classic
MDX matrix-group semantics and is what your M3Writer should emit.

_evidence: scratchpad/rms/craft3data/src/com/hiveworkshop/wc3/mdl/{EditableModel,Geoset,GeosetVertex,IdObject,Layer,Matrix,AnimFlag}.java ; scratchpad/rms/craft3data/src/com/hiveworkshop/wc3/mdl/GeosetVertex.java:38-80_

## [certain] Retera's actual rendering path is immediate-mode-emulating and slow; do not use it as a rendering template. Its value is the NGGLDP pipeline abstraction and the HD fragment shader.

com/hiveworkshop/wc3/gui/modeledit/PerspectiveViewport.java (1426 lines) does, EVERY FRAME, for EVERY triangle, for EVERY vertex: build a weighted sum of 4x4 bone matrices in Java (16 scalar mul-adds per bone), divide by sumWeight, transform position and normal, and push through NGGLDP.pipeline.glVertex3f/glNormal3f/glTexCoord2f. NGGLDP (com/hiveworkshop/rms/editor/render3d/NGGLDP.java, 1587 lines) is a shim that re-implements the fixed-function immediate-mode API on top of a modern GL 3.3 VBO+shader, batching between glBegin/glEnd. Classes: ShaderSwitchingPipeline (line 41), SimpleDiffuseShaderPipeline (266, STRIDE = pos4+normal4+uv2+color4), HDDiffuseShaderPipeline (724, STRIDE = pos4+normal4+tangent4+uv2+color4 = 18 floats), FixedFunctionPipeline (1351).
Pipeline selection is by `pipeline.setCurrentPipeline(layer.getLayerShader().ordinal())` — SD=0, HD=1. Textures are keyed per pipeline (GlTextureRef{textureId, pipelineId}), so the SAME Bitmap is uploaded twice if used by both an SD and an HD layer.
HD texture binding: `NGGLDP.pipeline.glActiveHDTexture(shaderTextureTypeHD.ordinal())` for each of the 6 slots, with bindLayer() (blend state) called only for the first non-null one.
Draw order: opaque pass then translucent pass, both in geoset order, no priority-plane sort for geosets (unlike ghostwolf). Particles/ribbons are drawn last with depthMask(false), CULL_FACE off, BLEND on.
Lights: two hardcoded directional-ish lights — GL_LIGHT0 diffuse (0.8,0.8,0.8) at (40,100,80); GL_LIGHT1 diffuse (0.2,0.2,0.2) at (-100,100.5,0.5).
For a WPF app: use a real GPU pipeline (SharpDX/Vortice D3D11 or Silk.NET/OpenTK hosted in a D3DImage/WinForms host). Follow ghostwolf's structure (one VB/IB per model, per-batch draw calls, bone matrices in a constant/structured buffer) and Retera's HD *math*.

_evidence: scratchpad/rms/craft3data/src/com/hiveworkshop/wc3/gui/modeledit/PerspectiveViewport.java:500-1000 ; scratchpad/rms/craft3data/src/com/hiveworkshop/rms/editor/render3d/NGGLDP.java:41,266,724,1351_

## [certain] Concrete port-verbatim vs reimplement list.

PORT VERBATIM (these encode hard-won game-behaviour knowledge you cannot rediscover cheaply):
 1. sd.ts SdSequence pre-slicing + getValue wrap-around logic, including the `if (isGlobal && frames[0] > end)` special case and the `dt < 0 -> dt += (end-start)` wrap.
 2. The forced-DontInterp list for visibility tracks: KLAV, KATV, KPEV, KP2V, KRVS.
 3. Retera's HD-only override: HD Alpha tracks with DontInterp are treated as Linear.
 4. math.ts hermite() and bezier() coefficient forms, and the 3-slerp squad (slerp(a,b,t), slerp(aOut,bIn,t), slerp of those two at 2t(1-t)).
 5. fromRotationTranslationScaleOrigin (the pivot-origin TRS matrix) — exact formula above.
 6. skeletalnode.ts billboarding, INCLUDING `computedScaling[2] *= -1` for BillboardedLockX and the MDX convertBasis rotateY(-PI/2) then rotateX(-PI/2).
 7. setupgeosets.ts vertex-group -> (bones[4|8], boneNumber) packing with the +1 offset, and the `if (matrixGroup)` null guard for invalid group indices.
 8. setupgroups.ts translucent sort: by filterMode, then stable-sort everything (batches + PRE2 + RIBB + EVTS) by priorityPlane.
 9. filtermode.ts blend-func table and layer.ts depthMask/cull/depth-test derivation.
10. Retera's HD team-color blend: diffuse.rgb * ((1 - orm.a) + teamColor.rgb * orm.a).
11. bounds.ts fromExtents clamp (r >= 0) and getBounds() fallback when a sequence's r === 0.
12. replaceableids.ts table (ghostwolf's is the complete one).
13. Geoset.java's `idx & 0xFFFF` + skip-triangle-if-any-index-is-0xFFFF, and `(256 + gndx) % 256`.

REIMPLEMENT / DO NOT COPY:
 1. mdx-m3-viewer's LAYS/MTLS version gating — it is wrong for v900 (fresnel) and absent for v1100/1200. Use ModelUtils.java's gates + LayerChunk.java's layout.
 2. Retera's AnimFlag.interpolateAt binary-search-per-call and Object-typed values. Use generics + precomputed per-sequence slices.
 3. Retera's 23-chunk cap and alphabetic-tag unknown-chunk skip. Use ghostwolf's `while (remaining > 0)` + UnknownChunk.
 4. Retera's billboardY/billboardZ (marked TODO/incorrect in the source).
 5. Retera's per-vertex CPU skinning in the render loop.
 6. The bone-matrix float texture — use a StructuredBuffer/cbuffer of float3x4.
 7. mdx-m3-viewer's `skinBytes *= 2 if model.bones.length > 255` — the correct threshold is genericObjects.length.
 8. mdx-m3-viewer's DXT1 -> RGB (no alpha) internal format.
 9. The commented-out PBR/IBL block in hd.frag.ts.

ADD WHAT NEITHER HAS:
 - HD geosets with a real blend filter mode (both projects force HD opaque).
 - HD geoset-animation (GEOA) alpha/color (ghostwolf ignores it for HD; Retera has it — take Retera's).
 - Animated HD texture IDs (the *TextureID tracks) — only Retera parses them, neither renders them well.
 - BC7 DDS.
 - Cycle/orphan-safe hierarchy flattening.

_evidence: synthesis of all files cited in the other findings_

## [certain] Known bugs and edge cases in shipping WC3 models, with the specific models named in the source comments.

 - DUPLICATE KEYFRAME TIMES: Peasant.mdx, bone 'Bone_Chest', around time 18300. Causes ceilIndex < floorIndex. Fix: `if (ceil < floor) ceil = floor;`  (AnimFlag.java:2583 and :2629)
 - GLOBAL SEQUENCE FIRST KEY OUT OF RANGE: HeroMountainKing. WE previewer, WE, and the game all render it differently. ghostwolf handles only 'first key beyond the sequence end -> constant'.  (sd.ts:31-41)
 - SEQUENCE WRAPPING AMBIGUITY: Water Elemental 'Birth' — the ceil-index-past-end branch; Retera's comment: 'NOTE: we just let it be in this case, based on Water Elemental's birth'.  (AnimFlag.java:2658-2672)
 - >4 BONES PER VERTEX in an SD geoset: Water Elemental. Needs the 8-bone EXTENDED_BONES shader variant.  (setupgeosets.ts comment)
 - INVALID VERTEX GROUP INDEX (GNDX points past MTGC): 'Somehow in some bad models a vertex group index refers to an invalid matrix group. Such models are still loaded by the game.' Leave the vertex unskinned.  (setupgeosets.ts)
 - ZERO BONES: modelinstance.ts creates no boneTexture; batchgroup.ts sets u_hasBones=0 and everything renders in model space. Handle explicitly.
 - a_boneNumber == 0 yields `bone / 0.0` = NaN in the vertex shader (transforms.glsl.ts). Guard.
 - HELPER-ONLY HIERARCHIES: helpers get objectIds and participate in the node tree, but the bone palette is sized bones.length and indexed by objectId. A geoset weighted to a helper reads out of bounds. Retera avoids this by resolving to a node object; do the same, or size your palette to genericObjects.length.
 - SELF-PARENTING: `if (objectId === parentId) parentId = -1` (viewer/handlers/mdx/genericobject.ts).
 - PIVT SHORTER THAN OBJECT COUNT: `pivotPoints[objectId] || vec3.create()`.
 - EVENT OBJECT WITH A NON-EVENT NAME: 'Units\Critters\BlackStagMale\BlackStagMale.mdx has an event object named "Point01"' — validate the 3-letter type prefix is one of SPN/SPL/UBR/SND before looking it up.  (handler.ts::getEventObjectData)
 - REVERSED EXTENTS: bounds.ts comment 'Some models apparently have reversed extents, go figure.'
 - 0xFFFF FACE INDICES: Geoset.java drops such triangles.
 - MODELS THAT FAIL TO FULLY PARSE BUT STILL RENDER: viewer/handlers/mdx/model.ts wraps parser.load() in try/catch.
 - NON-REFORGED MODELS SAVED WITH version > 800: they lack TANG and SKIN despite the version. Peek the 4CC and rewind.  (geoset.ts readMdx)
 - FILTER MODE > 6: clamped to Blend  (viewer/handlers/mdx/layer.ts).
 - Naga water / 'REFORGED 2022 FORMAT': LayerChunk.WRITE_JANK_REFORGED_2022_FORMAT_FILE_FIXES_NAGA_WATER — Blizzard's own 2022 files write ALL six texture-id slots including -1 entries, so textureIdCount can exceed the number of real textures. Accept both when reading.
 - LayerChunk.load has `textureTypeIndex = i; // TODO this is blizztarded` — when an inline KMTF follows a texture entry, the declared type index is ignored in favour of the loop index.

_evidence: scratchpad/mdxv/src/viewer/handlers/mdx/{sd.ts,setupgeosets.ts,handler.ts,model.ts,layer.ts} ; scratchpad/mdxv/src/viewer/bounds.ts ; scratchpad/rms/craft3data/src/com/hiveworkshop/wc3/mdl/AnimFlag.java:2583,2629,2658 ; scratchpad/rms/craft3data/src/com/hiveworkshop/wc3/mdx/LayerChunk.java:16,120-135_

## [certain] Where the two projects disagree, and which to trust.

1. HD TEAM COLOR. ghostwolf: `if (orm.a > 0.1) color *= tc * orm.a`. Retera: `texel.rgb * ((1-orm.a) + tc.rgb*orm.a)`. TRUST RETERA — it is a proper mask lerp, continuous at orm.a=0, and preserves diffuse where the mask is empty.
2. NORMAL MAP CHANNEL ORDER. ghostwolf `.xy`, Retera `.yx`. UNRESOLVED. BC5 convention says X in R, Y in G, favouring ghostwolf. Test both against a Reforged unit with strong surface detail.
3. MDX v1100/1200 LAYER LAYOUT. ghostwolf does not support it at all (treats every >800 identically). Retera's LayerChunk.java is correct and is corroborated by TaylorMouse's 56-byte skip arithmetic. TRUST RETERA.
4. v900 FRESNEL. ghostwolf reads fresnelColor/Opacity/TeamColor for any version>800; Retera gates it at >=1000. TRUST RETERA (a v900 model parsed ghostwolf's way will be 20 bytes out of sync per layer).
5. COLOR IDENTITY when a track has no keys in range. ghostwolf's colorDefval is (0,0,0) (aliased to translationDefval); Retera's is (1,1,1). TRUST RETERA — white is the neutral multiplier.
6. DONT_INHERIT flags. ghostwolf implements all three; Retera only scaling. TRUST GHOSTWOLF.
7. BILLBOARD LOCK Y/Z. ghostwolf implemented; Retera explicitly TODO. TRUST GHOSTWOLF.
8. HD ALPHA. Retera applies geoset-anim + layer alpha to HD via v_color and suppresses the alpha test when v_color.a != 1; ghostwolf ignores both for HD and always disables blending. TRUST RETERA — this is directly relevant to the reported alpha problem.
9. CHUNK ITERATION. ghostwolf order-independent + unknown-chunk preservation; Retera 23-iteration cap. TRUST GHOSTWOLF.
10. HD DETECTION. ghostwolf: material.shader === 'Shader_HD_DefaultUnit' (for batching) but material.shader !== '' (for model.hd). Retera: same constant, plus per-layer shaderTypeId for v1100+. TRUST RETERA for v1100+, where there is no shader string at all.
11. ANIMATED HD TEXTURE IDs. Only Retera parses them (renamed to 'NormalTextureID', 'ORMTextureID', ...). ghostwolf drops them.
12. TaylorMouse's MaxScript reader is a distant third source: correct on the v1000-has-name / v1100-does-not point, but its unconditional 56-byte layer skip is wrong for any v1100 layer that is not a full 6-texture HD layer.

_evidence: cross-reference of scratchpad/mdxv/src/viewer/handlers/mdx/shaders/hd.frag.ts vs scratchpad/rms/.../NGGLDP.java:764-870 ; scratchpad/mdxv/src/parsers/mdlx/layer.ts vs scratchpad/rms/.../mdx/LayerChunk.java + util/ModelUtils.java:311-345 ; GriffonStudios_Warcraft_3_Reforged_Read.ms:344,387_

## Open questions

- What MDX version do the models in the local C:\games\Warcraft III (build 2.0.4.23745) CASC actually use? Extract any unit .mdx and read the VERS chunk. If it is 1100 or 1200, mdx-m3-viewer's layer/material parser is unusable as-is and you must implement Retera's LayerChunk layout (shaderTypeId + textureIdCount + (textureId, textureTypeIndex) pairs, no 80-char shader string in MTLS).
- Normal map channel order: .xy (mdx-m3-viewer) or .yx (Retera)? Unresolved from source alone. Decode a known Reforged _normal.dds, render an ONLY_NORMAL_MAP debug view both ways, and compare lighting direction against the game.
- Do Reforged 2.x DDS textures use BC7 (DXGI_FORMAT_BC7_UNORM = 98)? mdx-m3-viewer throws on it. Check a few extracted .dds headers. In D3D11 this is a non-issue if you upload the compressed blocks directly.
- What exactly are the extra fields, if any, in v1200 beyond the v1200-only LITE shadowIntensity float? Retera's ModelUtils only flags isLightShadowIntensitySupported(v>=1200); TaylorMouse's reader accepts 1200 but treats it identically to 1100 for layers. There may be undocumented v1200 additions.
- In LayerChunk.load, when an inline KMTF track follows a texture entry Retera overrides textureTypeIndex with the loop index ('// TODO this is blizztarded'). Is that a workaround for a Blizzard quirk or a Retera bug? Needs verification against a model with animated HD texture ids.
- Does the Reforged HD pipeline ever use a real blend mode (FilterMode 2-6) on a Shader_HD_DefaultUnit material? Both viewers assume no. If yes, neither project's render path is correct and you need a custom translucent HD pass.
- How does the game actually combine layer alpha, GEOA alpha, and the 1-bit alpha test for HD? Retera's rule (suppress the alpha test once v_color.a < 1) is a heuristic, not documented behaviour.
- Retera's repo master still uses com.hiveworkshop.wc3.*; some online references and newer RMS builds use com.hiveworkshop.rms.parsers.mdlx.*. If you want the newer code you may need a release JAR or a fork, not github.com/Retera/ReterasModelStudio master.

## Sources

- https://github.com/flowtsohg/mdx-m3-viewer — src/parsers/mdlx/{model,geoset,layer,material,animations,animatedobject,animationmap,genericobject,geosetanimation,sequence,texture,extent}.ts
- https://github.com/flowtsohg/mdx-m3-viewer/blob/master/src/viewer/handlers/mdx/shaders/hd.frag.ts
- https://github.com/flowtsohg/mdx-m3-viewer/blob/master/src/viewer/handlers/mdx/shaders/hd.vert.ts
- https://github.com/flowtsohg/mdx-m3-viewer/blob/master/src/viewer/handlers/mdx/shaders/sd.vert.ts
- https://github.com/flowtsohg/mdx-m3-viewer/blob/master/src/viewer/handlers/mdx/shaders/sd.frag.ts
- https://github.com/flowtsohg/mdx-m3-viewer/blob/master/src/viewer/handlers/mdx/shaders/transforms.glsl.ts
- https://github.com/flowtsohg/mdx-m3-viewer/blob/master/src/viewer/handlers/shaders/bonetexture.glsl.ts
- https://github.com/flowtsohg/mdx-m3-viewer/blob/master/src/viewer/handlers/mdx/sd.ts
- https://github.com/flowtsohg/mdx-m3-viewer/blob/master/src/viewer/skeletalnode.ts
- https://github.com/flowtsohg/mdx-m3-viewer/blob/master/src/viewer/handlers/mdx/modelinstance.ts
- https://github.com/flowtsohg/mdx-m3-viewer/blob/master/src/viewer/handlers/mdx/{model,batch,batchgroup,setupgeosets,setupgroups,geoset,layer,material,texture,handler,node,filtermode,replaceableids,animatedobject,genericobject,geosetanimation,sequence}.ts
- https://github.com/flowtsohg/mdx-m3-viewer/blob/master/src/parsers/dds/image.ts
- https://github.com/flowtsohg/mdx-m3-viewer/blob/master/src/viewer/handlers/dds/texture.ts
- https://github.com/flowtsohg/mdx-m3-viewer/blob/master/src/common/math.ts
- https://github.com/flowtsohg/mdx-m3-viewer/blob/master/src/viewer/bounds.ts
- https://github.com/Retera/ReterasModelStudio — craft3data/src/com/hiveworkshop/wc3/mdl/AnimFlag.java
- https://github.com/Retera/ReterasModelStudio/blob/master/craft3data/src/com/hiveworkshop/rms/editor/render3d/NGGLDP.java
- https://github.com/Retera/ReterasModelStudio/blob/master/craft3data/src/com/hiveworkshop/wc3/mdl/render3d/RenderModel.java
- https://github.com/Retera/ReterasModelStudio/blob/master/craft3data/src/com/hiveworkshop/wc3/mdl/render3d/RenderNode.java
- https://github.com/Retera/ReterasModelStudio/blob/master/craft3data/src/com/hiveworkshop/wc3/mdx/LayerChunk.java
- https://github.com/Retera/ReterasModelStudio/blob/master/craft3data/src/com/hiveworkshop/wc3/mdx/MaterialChunk.java
- https://github.com/Retera/ReterasModelStudio/blob/master/craft3data/src/com/hiveworkshop/wc3/mdx/MdxModel.java
- https://github.com/Retera/ReterasModelStudio/blob/master/craft3data/src/com/hiveworkshop/wc3/mdx/BindPoseChunk.java
- https://github.com/Retera/ReterasModelStudio/blob/master/craft3data/src/com/hiveworkshop/wc3/util/ModelUtils.java
- https://github.com/Retera/ReterasModelStudio/blob/master/craft3data/src/com/hiveworkshop/wc3/util/MathUtils.java
- https://github.com/Retera/ReterasModelStudio/blob/master/craft3data/src/com/hiveworkshop/wc3/mdl/{Geoset,GeosetVertex,GeosetAnim,IdObject,Layer,Material,LayerShader,ShaderTextureTypeHD,QuaternionRotation}.java
- https://github.com/Retera/ReterasModelStudio/blob/master/craft3data/src/com/hiveworkshop/wc3/gui/modeledit/PerspectiveViewport.java
- C:\Program Files\Autodesk\3ds Max 2016\scripts\Startup\Warcraft_3_Reforged_Tools\GriffonStudios_Warcraft_3_Reforged_Read.ms (TaylorMouse, lines 195-420 for VERS/MTLS/LAYS/TEXS)
- Local sparse clones: C:\Users\Darithos\AppData\Local\Temp\claude\c--Projects-Wc3-Model-Viewer\54ab26fb-e693-4e6b-87b3-6a884757b1cf\scratchpad\{mdxv,rms}
