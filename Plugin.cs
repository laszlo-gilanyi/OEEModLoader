using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using BepInEx.Logging;
using Il2CppInterop.Runtime.Injection;
using SharpGLTF.Schema2;
using UnityEngine;
using UnityEngine.Playables;
using UMesh = UnityEngine.Mesh;
using UMaterial = UnityEngine.Material;
using UObject = UnityEngine.Object;
using SysMat4 = System.Numerics.Matrix4x4;
using AnimInterp = SharpGLTF.Schema2.AnimationInterpolationMode;
using GltfAnimation = SharpGLTF.Schema2.Animation;

namespace OEEModLoader;

// Template + Clone architecture.
//
// For each <unit_id>.glb in BepInEx/plugins/, on plugin Load we parse the GLB
// once into a hidden TEMPLATE GameObject (full bone tree + SkinnedMeshRenderers
// + Meshes + Materials) kept under DontDestroyOnLoad. When the watcher spots a
// vanilla unit whose name carries the mod's unit_id substring, we Instantiate
// the template under the vanilla Animator GameObject (the 'swordsman' /
// 'crossbowman' / 'griffin' nodes inside the unit prefab clone, where the
// game writes the battle facing rotation each frame). Unity's Instantiate
// automatically remaps internal Transform references inside the clone, so the
// SMR.bones arrays continue to point at the clone's bones, not the template's.
//
// Runtime work per instance is then trivial:
//   - hide the vanilla SkinnedMeshRenderers (forceRenderingOff = true so the
//     vanilla Mecanim state machine keeps ticking through its culling logic);
//   - per-renderer material: clone the vanilla material so we inherit the URP
//     shader + keywords, point its main / base texture at the mod's diffuse,
//     null out vanilla normal/emission/occlusion maps that don't match the
//     mod UV layout, and turn on alpha-clip if the glTF material asked for it;
//   - a ModAnimator MonoBehaviour on the clone reads the live vanilla
//     animator's current clip name each frame and samples the matching mod
//     animation channels directly onto the clone's bone Transforms (no
//     AnimationClip / AnimationCurve, which strip in IL2CPP).
//
// Skips that keep the watcher honest:
//   * SMRs without a valid scene (prefab/template asset) are ignored, so we
//     never bake a mod hierarchy into a prefab and double it on every Clone;
//   * SMRs we already turned off (forceRenderingOff) or that live under an
//     OEEMod_* parent are ignored so we don't re-detect the same patch;
//   * "_map" variants (strategic-map unit names) are skipped unless the mod
//     file itself carries an _map suffix.
//
// Coordinate conventions match OldenEraExplorer's exporter: X-axis mirror on
// positions / normals, Y+Z negation on rotations, triangle winding reversed,
// UV V flipped (compensates LoadImage's row order), inverse bind matrices
// conjugated through the X mirror.
[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public class Plugin : BasePlugin
{
    public const string PluginGuid = "com.oldenera.explorer.modloader";
    public const string PluginName = "OEE Mod Loader";
    public const string PluginVersion = "0.14.0";

    public static readonly Dictionary<string, ModData> Mods =
        new Dictionary<string, ModData>(StringComparer.OrdinalIgnoreCase);

    internal static new ManualLogSource Log;

    public override void Load()
    {
        Log = base.Log;
        Log.LogMessage($"=== {PluginName} v{PluginVersion} loading ===");

        try { ClassInjector.RegisterTypeInIl2Cpp<ModWatcher>(); }
        catch (Exception e) { Log.LogError($"RegisterTypeInIl2Cpp(ModWatcher) failed: {e}"); return; }
        try { ClassInjector.RegisterTypeInIl2Cpp<ModAnimator>(); }
        catch (Exception e) { Log.LogError($"RegisterTypeInIl2Cpp(ModAnimator) failed: {e}"); return; }
        try { ClassInjector.RegisterTypeInIl2Cpp<OEEPatchedMarker>(); }
        catch (Exception e) { Log.LogError($"RegisterTypeInIl2Cpp(OEEPatchedMarker) failed: {e}"); return; }

        LoadGlbMods();

        if (Mods.Count == 0)
        {
            Log.LogMessage("No mods loaded; watcher skipped.");
            return;
        }

        var host = new GameObject("OEEModLoader_Watcher");
        UObject.DontDestroyOnLoad(host);
        host.hideFlags = HideFlags.HideAndDontSave;
        host.AddComponent<ModWatcher>();

        Log.LogMessage($"Watcher running. {Mods.Count} mod(s) active.");
    }

    private void LoadGlbMods()
    {
        string pluginsDir = Paths.PluginPath;
        if (!Directory.Exists(pluginsDir)) { Log.LogWarning($"Plugins dir not found: {pluginsDir}"); return; }

        var glbFiles = Directory.GetFiles(pluginsDir, "*.glb");
        Log.LogMessage($"Scanning {pluginsDir} for *.glb: found {glbFiles.Length} file(s).");

        foreach (var glbPath in glbFiles)
        {
            var unitId = Path.GetFileNameWithoutExtension(glbPath);
            try
            {
                var root = ModelRoot.Load(glbPath);
                var data = new ModData { UnitId = unitId };
                ExtractGlbData(data, root);
                BuildTextures(data);
                LogGlbSummary(data);
                Mods[unitId] = data;
            }
            catch (Exception e) { Log.LogError($"Failed to load mod '{unitId}' from {glbPath}: {e}"); }
        }
    }

    private static void ExtractGlbData(ModData data, ModelRoot root)
    {
        var allNodes = root.LogicalNodes.ToList();
        int nodeCount = allNodes.Count;
        data.JointNames = new string[nodeCount];
        data.JointLocalPos = new Vector3[nodeCount];
        data.JointLocalRot = new Quaternion[nodeCount];
        data.JointLocalScale = new Vector3[nodeCount];
        data.JointParents = new int[nodeCount];
        for (int i = 0; i < nodeCount; i++)
        {
            var node = allNodes[i];
            data.JointNames[i] = node.Name ?? $"node{i}";

            var t = node.LocalTransform;
            data.JointLocalPos[i] = new Vector3(-t.Translation.X, t.Translation.Y, t.Translation.Z);
            data.JointLocalRot[i] = new Quaternion(t.Rotation.X, -t.Rotation.Y, -t.Rotation.Z, t.Rotation.W);
            data.JointLocalScale[i] = new Vector3(t.Scale.X, t.Scale.Y, t.Scale.Z);

            data.JointParents[i] = node.VisualParent != null ? node.VisualParent.LogicalIndex : -1;
        }

        var skins = root.LogicalSkins.ToList();
        data.SkinBindPoses = new Matrix4x4[skins.Count][];
        data.SkinSlotToJoint = new int[skins.Count][];
        for (int s = 0; s < skins.Count; s++)
        {
            var skin = skins[s];
            int n = skin.JointsCount;
            data.SkinBindPoses[s] = new Matrix4x4[n];
            data.SkinSlotToJoint[s] = new int[n];
            for (int i = 0; i < n; i++)
            {
                var (joint, ibm) = skin.GetJoint(i);
                data.SkinBindPoses[s][i] = ConvertMatrix(ibm);
                data.SkinSlotToJoint[s][i] = joint != null ? joint.LogicalIndex : -1;
            }
        }

        var meshSkinMap = new Dictionary<int, int>();
        foreach (var node in root.LogicalNodes)
        {
            if (node.Mesh == null) continue;
            int mi = node.Mesh.LogicalIndex;
            int si = node.Skin != null ? node.Skin.LogicalIndex : -1;
            if (!meshSkinMap.ContainsKey(mi)) meshSkinMap[mi] = si;
        }

        var meshList = new List<ModMesh>(root.LogicalMeshes.Count);
        foreach (var m in root.LogicalMeshes)
        {
            int skinIdx = meshSkinMap.TryGetValue(m.LogicalIndex, out var si) ? si : -1;
            meshList.Add(ExtractMesh(m, skinIdx));
        }
        data.Meshes = meshList.ToArray();

        var imgs = new List<byte[]>();
        foreach (var img in root.LogicalImages) imgs.Add(img.Content.Content.ToArray());
        data.TextureBytes = imgs.ToArray();

        var anims = new List<ModAnim>();
        foreach (var src in root.LogicalAnimations) anims.Add(ExtractAnimation(src));
        data.Animations = anims.ToArray();
    }

    private static ModMesh ExtractMesh(SharpGLTF.Schema2.Mesh src, int skinIndex)
    {
        var verts = new List<Vector3>();
        var norms = new List<Vector3>();
        var uvs = new List<Vector2>();
        var bws = new List<BoneWeight>();
        var submeshes = new List<int[]>();
        int baseColorImageIdx = -1;
        int alphaMode = 0;
        float alphaCutoff = 0.5f;
        bool doubleSided = false;
        int vOffset = 0;

        foreach (var prim in src.Primitives)
        {
            var pos = prim.GetVertexAccessor("POSITION")?.AsVector3Array();
            if (pos == null) continue;
            var nrm = prim.GetVertexAccessor("NORMAL")?.AsVector3Array();
            var tex0 = prim.GetVertexAccessor("TEXCOORD_0")?.AsVector2Array();
            var j0 = prim.GetVertexAccessor("JOINTS_0")?.AsVector4Array();
            var w0 = prim.GetVertexAccessor("WEIGHTS_0")?.AsVector4Array();
            if (prim.Material != null && baseColorImageIdx < 0)
            {
                var ch = prim.Material.FindChannel("BaseColor");
                if (ch.HasValue && ch.Value.Texture != null && ch.Value.Texture.PrimaryImage != null)
                    baseColorImageIdx = ch.Value.Texture.PrimaryImage.LogicalIndex;
                switch (prim.Material.Alpha)
                {
                    case SharpGLTF.Schema2.AlphaMode.MASK: alphaMode = 1; break;
                    case SharpGLTF.Schema2.AlphaMode.BLEND: alphaMode = 2; break;
                    default: alphaMode = 0; break;
                }
                alphaCutoff = prim.Material.AlphaCutoff;
                doubleSided = prim.Material.DoubleSided;
            }

            for (int i = 0; i < pos.Count; i++)
            {
                var p = pos[i]; verts.Add(new Vector3(-p.X, p.Y, p.Z));
                if (nrm != null && i < nrm.Count) { var n = nrm[i]; norms.Add(new Vector3(-n.X, n.Y, n.Z)); }
                else norms.Add(Vector3.up);
                if (tex0 != null && i < tex0.Count) { var u = tex0[i]; uvs.Add(new Vector2(u.X, 1.0f - u.Y)); }
                else uvs.Add(Vector2.zero);
                if (j0 != null && w0 != null && i < j0.Count && i < w0.Count)
                {
                    var jj = j0[i]; var ww = w0[i];
                    bws.Add(new BoneWeight
                    {
                        boneIndex0 = (int)jj.X, boneIndex1 = (int)jj.Y,
                        boneIndex2 = (int)jj.Z, boneIndex3 = (int)jj.W,
                        weight0 = ww.X, weight1 = ww.Y, weight2 = ww.Z, weight3 = ww.W,
                    });
                }
                else bws.Add(default);
            }

            var idx = prim.GetIndices();
            var triBuf = new int[idx.Count];
            int triCount = idx.Count / 3;
            for (int t = 0; t < triCount; t++)
            {
                triBuf[t * 3 + 0] = (int)idx[t * 3 + 0] + vOffset;
                triBuf[t * 3 + 1] = (int)idx[t * 3 + 2] + vOffset;
                triBuf[t * 3 + 2] = (int)idx[t * 3 + 1] + vOffset;
            }
            submeshes.Add(triBuf);
            vOffset += pos.Count;
        }

        return new ModMesh
        {
            Name = src.Name ?? "",
            Positions = verts.ToArray(),
            Normals = norms.ToArray(),
            UVs = uvs.ToArray(),
            BoneWeights = bws.ToArray(),
            Submeshes = submeshes.ToArray(),
            SkinIndex = skinIndex,
            BaseColorImageIndex = baseColorImageIdx,
            AlphaMode = alphaMode,
            AlphaCutoff = alphaCutoff,
            DoubleSided = doubleSided,
        };
    }

    private static ModAnim ExtractAnimation(GltfAnimation src)
    {
        var anim = new ModAnim { Name = src.Name ?? "", Duration = src.Duration };
        var channels = new List<ModAnimChannel>();
        foreach (var ch in src.Channels)
        {
            var node = ch.TargetNode;
            if (node == null) continue;
            int nodeIdx = node.LogicalIndex;
            var path = ch.TargetNodePath;
            try
            {
                if (path == PropertyPath.translation)
                {
                    var s = ch.GetTranslationSampler();
                    if (s == null) continue;
                    channels.Add(ExtractV3Channel(s, nodeIdx, ChannelPath.Translation, mirrorX: true));
                }
                else if (path == PropertyPath.rotation)
                {
                    var s = ch.GetRotationSampler();
                    if (s == null) continue;
                    channels.Add(ExtractQuatChannel(s, nodeIdx));
                }
                else if (path == PropertyPath.scale)
                {
                    var s = ch.GetScaleSampler();
                    if (s == null) continue;
                    channels.Add(ExtractV3Channel(s, nodeIdx, ChannelPath.Scale, mirrorX: false));
                }
            }
            catch (Exception e)
            {
                Log.LogWarning($"  anim '{src.Name}' channel for node '{node.Name}'/{path} skipped: {e.Message}");
            }
        }
        anim.Channels = channels.ToArray();
        return anim;
    }

    private static ModAnimChannel ExtractV3Channel(IAnimationSampler<System.Numerics.Vector3> s, int jointIdx, ChannelPath path, bool mirrorX)
    {
        var times = new List<float>();
        var values = new List<float>();
        if (s.InterpolationMode == AnimInterp.CUBICSPLINE)
        {
            foreach (var k in s.GetCubicKeys())
            {
                times.Add(k.Key);
                var v = k.Value.Value;
                values.Add(mirrorX ? -v.X : v.X); values.Add(v.Y); values.Add(v.Z);
            }
        }
        else
        {
            foreach (var k in s.GetLinearKeys())
            {
                times.Add(k.Key);
                var v = k.Value;
                values.Add(mirrorX ? -v.X : v.X); values.Add(v.Y); values.Add(v.Z);
            }
        }
        return new ModAnimChannel
        {
            TargetJoint = jointIdx,
            Path = (int)path,
            Interpolation = s.InterpolationMode == AnimInterp.STEP ? 1 : 0,
            Times = times.ToArray(),
            Values = values.ToArray(),
        };
    }

    private static ModAnimChannel ExtractQuatChannel(IAnimationSampler<System.Numerics.Quaternion> s, int jointIdx)
    {
        var times = new List<float>();
        var values = new List<float>();
        if (s.InterpolationMode == AnimInterp.CUBICSPLINE)
        {
            foreach (var k in s.GetCubicKeys())
            {
                times.Add(k.Key);
                var q = k.Value.Value;
                values.Add(q.X); values.Add(-q.Y); values.Add(-q.Z); values.Add(q.W);
            }
        }
        else
        {
            foreach (var k in s.GetLinearKeys())
            {
                times.Add(k.Key);
                var q = k.Value;
                values.Add(q.X); values.Add(-q.Y); values.Add(-q.Z); values.Add(q.W);
            }
        }
        return new ModAnimChannel
        {
            TargetJoint = jointIdx,
            Path = (int)ChannelPath.Rotation,
            Interpolation = s.InterpolationMode == AnimInterp.STEP ? 1 : 0,
            Times = times.ToArray(),
            Values = values.ToArray(),
        };
    }

    private static Matrix4x4 ConvertMatrix(SysMat4 m) => new Matrix4x4(
        new Vector4( m.M11, -m.M12, -m.M13, -m.M14),
        new Vector4(-m.M21,  m.M22,  m.M23,  m.M24),
        new Vector4(-m.M31,  m.M32,  m.M33,  m.M34),
        new Vector4(-m.M41,  m.M42,  m.M43,  m.M44));

    private static void BuildTextures(ModData data)
    {
        if (data.TextureBytes == null || data.TextureBytes.Length == 0)
        {
            data.BuiltTextures = new Texture2D[0];
            return;
        }
        var built = new Texture2D[data.TextureBytes.Length];
        for (int i = 0; i < data.TextureBytes.Length; i++)
        {
            var bytes = data.TextureBytes[i];
            if (bytes == null || bytes.Length == 0) continue;
            try
            {
                var tex = new Texture2D(2, 2);
                tex.name = $"OEEMod_{data.UnitId}_img{i}";
                ImageConversion.LoadImage(tex, bytes);
                // HideAndDontSave keeps the texture alive across scene loads
                // without going through the DontDestroyOnLoad/IL2CPP interop
                // path which silently drops Texture2D references.
                tex.hideFlags = HideFlags.HideAndDontSave;
                built[i] = tex;
            }
            catch (Exception e)
            {
                Log.LogWarning($"Mod '{data.UnitId}' image {i} LoadImage failed: {e.Message}");
            }
        }
        data.BuiltTextures = built;
    }

    private static void LogGlbSummary(ModData data)
    {
        int totalV = 0;
        if (data.Meshes != null) for (int i = 0; i < data.Meshes.Length; i++) totalV += data.Meshes[i].Positions?.Length ?? 0;
        Log.LogMessage(
            $"  '{data.UnitId}': meshes={data.Meshes?.Length ?? 0}, totalVerts={totalV}, " +
            $"joints={data.JointNames?.Length ?? 0}, skins={data.SkinSlotToJoint?.Length ?? 0}, " +
            $"anims={data.Animations?.Length ?? 0}, images={data.TextureBytes?.Length ?? 0}");
    }

    public static bool TryFindModForName(string name, out ModData mod)
    {
        if (!string.IsNullOrEmpty(name))
        {
            // Strict exact match (after stripping the Unity Instantiate
            // "(Clone)" suffix). The vanilla unit naming uses 'esquire',
            // 'esquire_upg', 'esquire_upg_alt', 'esquire_map', etc. as
            // distinct units; a substring match on the unit-id would let a
            // single 'esquire.glb' patch all four. The user wants one mod
            // file -> exactly one unit id, so we require equality.
            string stripped = StripCloneSuffix(name);
            foreach (var kv in Mods)
            {
                if (string.Equals(kv.Key, stripped, StringComparison.OrdinalIgnoreCase))
                {
                    mod = kv.Value;
                    return true;
                }
            }
        }
        mod = null;
        return false;
    }

    private static string StripCloneSuffix(string name)
    {
        const string sfx = "(Clone)";
        if (name.EndsWith(sfx, StringComparison.Ordinal))
            return name.Substring(0, name.Length - sfx.Length);
        return name;
    }
}

public enum ChannelPath { Translation = 0, Rotation = 1, Scale = 2 }

// Empty marker component dropped on each unit root after the first patch.
// Stable across the vanilla respawn cycle: when the game Instantiate-clones a
// unit, the marker comes along on the clone, so the watcher recognises a
// patched unit even though its InstanceID changed. When the unit is destroyed
// the marker (and our OEEMod_* hierarchy under it) goes with it.
public class OEEPatchedMarker : MonoBehaviour
{
    public OEEPatchedMarker(IntPtr ptr) : base(ptr) { }
}

public class ModData
{
    public string UnitId;
    public ModMesh[] Meshes;
    public string[] JointNames;
    public int[] JointParents;
    public Vector3[] JointLocalPos;
    public Quaternion[] JointLocalRot;
    public Vector3[] JointLocalScale;
    public Matrix4x4[][] SkinBindPoses;
    public int[][] SkinSlotToJoint;
    public byte[][] TextureBytes;
    public Texture2D[] BuiltTextures;
    public ModAnim[] Animations;
    // Lazy-built shared assets cached after the first patch on this mod.
    // SharedMeshes[mi] is the UnityEngine.Mesh that all clones of this mod use
    // for mesh index mi; modRoot / clone instantiation creates new bones each
    // time but the Mesh + bindposes + boneWeights are immutable per mod.
    public UMesh[] SharedMeshes;
}

public class ModMesh
{
    public string Name;
    public Vector3[] Positions;
    public Vector3[] Normals;
    public Vector2[] UVs;
    public BoneWeight[] BoneWeights;
    public int[][] Submeshes;
    public int SkinIndex;
    public int BaseColorImageIndex;
    public int AlphaMode;          // 0=Opaque, 1=Mask, 2=Blend
    public float AlphaCutoff;
    public bool DoubleSided;
}

public class ModAnim
{
    public string Name;
    public float Duration;
    public ModAnimChannel[] Channels;
}

public class ModAnimChannel
{
    public int TargetJoint;
    public int Path;
    public int Interpolation;
    public float[] Times;
    public float[] Values;
}

public class ModWatcher : MonoBehaviour
{
    public ModWatcher(IntPtr ptr) : base(ptr) { }

    private float _nextScanTime;
    private const float ScanInterval = 1.0f;
    private static readonly HashSet<int> _processedRoots = new HashSet<int>();

    public void Update()
    {
        try
        {
            if (Time.unscaledTime >= _nextScanTime)
            {
                _nextScanTime = Time.unscaledTime + ScanInterval;
                Scan();
            }
        }
        catch (Exception e) { Plugin.Log.LogError($"ModWatcher.Update threw: {e}"); }
    }

    private static bool IsUnderModRoot(Transform t)
    {
        while (t != null)
        {
            var go = t.gameObject;
            if (go != null && go.name != null && go.name.StartsWith("OEEMod_", StringComparison.Ordinal))
                return true;
            t = t.parent;
        }
        return false;
    }

    private static (Transform root, ModData mod) FindMatchedAncestor(SkinnedMeshRenderer r)
    {
        var t = r.transform != null ? r.transform.parent : null;
        Transform best = null;
        ModData bestMod = null;
        while (t != null)
        {
            var go = t.gameObject;
            if (go != null && Plugin.TryFindModForName(go.name, out var mod))
            {
                best = t;
                bestMod = mod;
            }
            t = t.parent;
        }
        return (best, bestMod);
    }

    private void Scan()
    {
        Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppArrayBase<SkinnedMeshRenderer> renderers;
        try { renderers = Resources.FindObjectsOfTypeAll<SkinnedMeshRenderer>(); }
        catch (Exception e) { Plugin.Log.LogWarning($"FindObjectsOfTypeAll failed: {e.Message}"); return; }

        var groups = new Dictionary<int, (Transform Root, ModData Mod, List<SkinnedMeshRenderer> Renderers)>();
        for (int i = 0; i < renderers.Length; i++)
        {
            var r = renderers[i];
            if (r == null) continue;
            if (!r.gameObject.scene.IsValid()) continue;        // prefab/template asset
            if (r.forceRenderingOff) continue;                  // we already hid this one
            if (IsUnderModRoot(r.transform)) continue;          // our own mod SMR

            var (root, mod) = FindMatchedAncestor(r);
            if (root == null) continue;
            // Stable identity across respawn: an OEEPatchedMarker survives the
            // vanilla Instantiate that hands us a "new" InstanceID for the
            // same logical unit, so we skip re-patching it (which otherwise
            // tears down and recreates 88-177 bone GameObjects every scan,
            // froze the game inside a battle).
            if (root.gameObject.GetComponent<OEEPatchedMarker>() != null) continue;
            int rootId = root.gameObject.GetInstanceID();
            if (_processedRoots.Contains(rootId)) continue;

            if (!groups.TryGetValue(rootId, out var g))
            {
                g = (root, mod, new List<SkinnedMeshRenderer>());
                groups[rootId] = g;
            }
            g.Renderers.Add(r);
            groups[rootId] = g;
        }

        foreach (var kv in groups)
        {
            int rootId = kv.Key;
            var (root, mod, vrs) = kv.Value;
            _processedRoots.Add(rootId);
            try { ApplyMod(root, mod, vrs); }
            catch (Exception e) { Plugin.Log.LogError($"ApplyMod failed on root '{root.gameObject.name}': {e}"); }
        }
    }

    private static readonly HashSet<string> _loggedAnimatorPicks = new HashSet<string>();

    private static Animator PickBattleAnimator(Transform unitRoot)
    {
        Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppArrayBase<Animator> ans;
        try { ans = unitRoot.GetComponentsInChildren<Animator>(true); }
        catch { return null; }
        Animator best = null;
        int bestClips = -1;
        bool bestActive = false;
        var diag = new StringBuilder();
        for (int i = 0; i < ans.Length; i++)
        {
            var an = ans[i];
            if (an == null) continue;
            int clipCount = 0;
            string ctrlName = "<none>";
            try
            {
                var rac = an.runtimeAnimatorController;
                if (rac != null)
                {
                    ctrlName = rac.name;
                    var clips = rac.animationClips;
                    if (clips != null) clipCount = clips.Length;
                }
            }
            catch { }
            bool active = an.gameObject.activeInHierarchy;
            if (diag.Length > 0) diag.Append(", ");
            diag.Append($"{an.gameObject.name}[{ctrlName},{clipCount},act={active}]");
            // Prefer an active GO with the richest controller. We can't ride an
            // inactive Animator (its state machine doesn't tick), so active
            // always beats inactive regardless of clip count; ties on activity
            // go to the higher clip count.
            bool better;
            if (active && !bestActive) better = true;
            else if (!active && bestActive) better = false;
            else better = clipCount > bestClips;
            if (better)
            {
                best = an;
                bestClips = clipCount;
                bestActive = active;
            }
        }
        string key = unitRoot.gameObject.name + ":" + (best != null ? best.gameObject.name : "<null>");
        if (_loggedAnimatorPicks.Add(key))
        {
            Plugin.Log.LogMessage(
                $"  animator pick on '{unitRoot.gameObject.name}': chose '{(best != null ? best.gameObject.name : "<null>")}' " +
                $"(clips={bestClips}, active={bestActive}); candidates=[{diag}]");
        }
        return best;
    }

    private static UMaterial PickVanillaMaterial(List<SkinnedMeshRenderer> vrs)
    {
        // Prefer a body material (Hex/Lit, URP/Lit, Standard, ...) over rim or
        // VFX shaders. The vanilla griffin SMR list, for instance, exposes a
        // 'fresnel_emissary_vfx' material that draws only an edge highlight;
        // cloning that as the mod body material made the mod render as a
        // transparent gold outline instead of a textured mesh.
        UMaterial firstAny = null;
        for (int i = 0; i < vrs.Count; i++)
        {
            var r = vrs[i]; if (r == null) continue;
            var ms = r.sharedMaterials; if (ms == null) continue;
            for (int j = 0; j < ms.Length; j++)
            {
                var m = ms[j];
                if (m == null || m.shader == null) continue;
                if (firstAny == null) firstAny = m;
                string sn = m.shader.name;
                if (sn != null && sn.IndexOf("Lit", StringComparison.OrdinalIgnoreCase) >= 0
                                && sn.IndexOf("fresnel", StringComparison.OrdinalIgnoreCase) < 0)
                    return m;
            }
        }
        return firstAny;
    }

    private static int PickVanillaLayer(List<SkinnedMeshRenderer> vrs)
    {
        for (int i = 0; i < vrs.Count; i++)
        {
            var r = vrs[i]; if (r == null) continue;
            return r.gameObject.layer;
        }
        return 0;
    }

    private static void EnsureSharedMeshes(ModData mod)
    {
        if (mod.SharedMeshes != null) return;
        var meshes = mod.Meshes;
        var built = new UMesh[meshes?.Length ?? 0];
        for (int mi = 0; mi < built.Length; mi++)
        {
            var m = meshes[mi];
            if (m == null || m.Positions == null || m.Positions.Length == 0) continue;
            if (m.SkinIndex < 0 || m.SkinIndex >= mod.SkinSlotToJoint.Length) continue;

            var uMesh = new UMesh();
            uMesh.name = $"OEEMod_{mod.UnitId}_{m.Name}";
            uMesh.vertices = m.Positions;
            if (m.Normals != null && m.Normals.Length == m.Positions.Length) uMesh.normals = m.Normals;
            if (m.UVs != null && m.UVs.Length == m.Positions.Length) uMesh.uv = m.UVs;
            uMesh.subMeshCount = m.Submeshes.Length;
            for (int s = 0; s < m.Submeshes.Length; s++) uMesh.SetTriangles(m.Submeshes[s], s);
            if (m.BoneWeights != null && m.BoneWeights.Length == m.Positions.Length)
                uMesh.boneWeights = m.BoneWeights;
            uMesh.bindposes = mod.SkinBindPoses[m.SkinIndex];
            uMesh.RecalculateBounds();
            uMesh.RecalculateTangents();
            uMesh.hideFlags = HideFlags.HideAndDontSave;
            built[mi] = uMesh;
        }
        mod.SharedMeshes = built;
    }

    private static void ApplyMod(Transform unitRoot, ModData mod, List<SkinnedMeshRenderer> vanillaRenderers)
    {
        var vanillaMat = PickVanillaMaterial(vanillaRenderers);
        if (vanillaMat == null)
        {
            Plugin.Log.LogWarning($"Mod '{mod.UnitId}': no vanilla material to clone; aborting.");
            return;
        }
        int vanillaLayer = PickVanillaLayer(vanillaRenderers);
        EnsureSharedMeshes(mod);

        for (int i = 0; i < vanillaRenderers.Count; i++)
        {
            var r = vanillaRenderers[i];
            if (r == null) continue;
            // Stop drawing the vanilla mesh - but only the draw call. The
            // renderer component must remain "visible" from the camera's
            // point of view, because the unit's Animator ships with
            // CullingMode=CullCompletely (per the AssetRipper rip of
            // Esquire.prefab). With Renderer.isVisible=false the Animator
            // halts entirely, and the friendly-side mod was therefore stuck
            // on idle - the state machine never even reached walk/attack.
            // Forcing a huge localBounds keeps the renderer permanently in
            // view from any reasonable battlefield camera, so isVisible=true,
            // the state machine keeps ticking, and our clip detection reads
            // the actual current state.
            try { r.localBounds = new Bounds(Vector3.zero, new Vector3(200f, 200f, 200f)); }
            catch { }
            r.forceRenderingOff = true;
        }

        // Pick the BATTLE Animator on this unit. Note that at patch time the
        // 'swordsman' / 'crossbowman' / 'griffin' battle-rig GO can still be
        // inactive (the game flips it on once the battle stage spawns the
        // unit). We keep a reference anyway and a per-Update fallback re-picks
        // if it's still inactive when our ModAnimator runs.
        Animator vanillaAnimator = PickBattleAnimator(unitRoot);
        // Heroes drives per-state animations through a PlayableDirector on the
        // unit root, swapping its playableAsset to swordsman@walk / @attack /
        // etc. Hunt for it on the unit root and any ancestor / child so we can
        // read it from ModAnimator.Update.
        PlayableDirector vanillaDirector = unitRoot.GetComponentInChildren<PlayableDirector>(true);
        // One-shot AlwaysAnimate. Combined with the giant localBounds above,
        // this guarantees that the friendly-side Animator state machine keeps
        // running even though we suppressed the vanilla mesh draw. (We do NOT
        // re-apply this per-Update; previous attempts crashed the game inside
        // the native cullingMode setter when the game's own rig manager was
        // mid-update on the same Animator.)
        if (vanillaAnimator != null)
        {
            try { vanillaAnimator.cullingMode = AnimatorCullingMode.AlwaysAnimate; } catch { }
        }
        // For modParent we always use the unit root: it's guaranteed active,
        // and we sync the mod root's world position/rotation/scale to the
        // vanilla animator GO each frame (in ModAnimator.Update). This avoids
        // the trap where parenting under an inactive battle-rig GO leaves the
        // entire mod hierarchy inactive (Update never runs) until the game
        // toggles it on, which can be later or never on the friendly side.
        Transform modParent = unitRoot;

        // Strip any stale OEEMod_* hierarchy left behind by an earlier patch
        // or a prefab-clone-leaking-our-bake scenario.
        for (int i = modParent.childCount - 1; i >= 0; i--)
        {
            var c = modParent.GetChild(i);
            if (c != null && c.gameObject != null && c.gameObject.name != null
                && c.gameObject.name.StartsWith("OEEMod_", StringComparison.Ordinal))
            {
                UObject.Destroy(c.gameObject);
            }
        }

        // Build the mod hierarchy fresh under modParent.
        var modRoot = new GameObject($"OEEMod_{mod.UnitId}");
        modRoot.transform.SetParent(modParent, false);
        modRoot.layer = vanillaLayer;
        // Initial scale: identity. Per-frame sync in ModAnimator.Update will
        // overwrite this every frame with the vanilla animator GO's world
        // rotation + a size-normalized world scale that preserves the
        // friendly/enemy facing-mirror sign.

        int jointCount = mod.JointNames?.Length ?? 0;
        var bones = new Transform[jointCount];
        for (int i = 0; i < jointCount; i++)
        {
            var boneGo = new GameObject(mod.JointNames[i]);
            boneGo.layer = vanillaLayer;
            bones[i] = boneGo.transform;
        }
        for (int i = 0; i < jointCount; i++)
        {
            var parent = mod.JointParents[i] >= 0 ? bones[mod.JointParents[i]] : modRoot.transform;
            bones[i].SetParent(parent, false);
            bones[i].localPosition = mod.JointLocalPos[i];
            bones[i].localRotation = mod.JointLocalRot[i];
            bones[i].localScale = mod.JointLocalScale[i];
        }

        int rendererCount = 0;
        for (int mi = 0; mi < (mod.Meshes?.Length ?? 0); mi++)
        {
            var m = mod.Meshes[mi];
            if (m == null || m.Positions == null || m.Positions.Length == 0) continue;
            if (m.SkinIndex < 0 || m.SkinIndex >= mod.SkinSlotToJoint.Length) continue;

            var slotMap = mod.SkinSlotToJoint[m.SkinIndex];
            int slotCount = slotMap.Length;

            var smrBones = new Transform[slotCount];
            for (int s = 0; s < slotCount; s++)
            {
                int ji = slotMap[s];
                smrBones[s] = (ji >= 0 && ji < bones.Length) ? bones[ji] : modRoot.transform;
            }

            var meshGo = new GameObject($"OEEModRenderer_{m.Name}");
            meshGo.transform.SetParent(modRoot.transform, false);
            meshGo.layer = vanillaLayer;
            var smr = meshGo.AddComponent<SkinnedMeshRenderer>();

            smr.sharedMesh = mod.SharedMeshes[mi];
            smr.bones = smrBones;
            smr.rootBone = modRoot.transform;
            smr.updateWhenOffscreen = true;

            UnityEngine.Texture modTex = null;
            int chosenTexIdx = -1;
            if (mod.BuiltTextures != null && mod.BuiltTextures.Length > 0)
            {
                int ti = (m.BaseColorImageIndex >= 0 && m.BaseColorImageIndex < mod.BuiltTextures.Length)
                    ? m.BaseColorImageIndex : 0;
                modTex = mod.BuiltTextures[ti];
                if (modTex == null) { modTex = mod.BuiltTextures[0]; chosenTexIdx = 0; }
                else chosenTexIdx = ti;
            }
            var matClone = new UMaterial(vanillaMat);
            matClone.name = $"OEEMod_{mod.UnitId}_mat_{m.Name}";
            ApplyModTextureToClone(matClone, modTex, m.AlphaMode, m.AlphaCutoff, m.DoubleSided);
            smr.sharedMaterial = matClone;
            if (rendererCount == 0)
            {
                string shaderName = "<unknown>";
                try { shaderName = matClone.shader != null ? matClone.shader.name : "<null>"; } catch { }
                string mainTexName = "<null>";
                try { mainTexName = matClone.mainTexture != null ? matClone.mainTexture.name : "<null>"; } catch { }
                Plugin.Log.LogMessage(
                    $"  mat clone for '{m.Name}': shader='{shaderName}', mainTexture='{mainTexName}', " +
                    $"chosenModImg={chosenTexIdx}/{mod.BuiltTextures?.Length ?? 0}, alphaMode={m.AlphaMode}, doubleSided={m.DoubleSided}");
            }

            rendererCount++;
        }

        if (mod.Animations != null && mod.Animations.Length > 0 && bones.Length > 0)
        {
            int rootJointIdx = -1;
            for (int i = 0; i < mod.JointNames.Length; i++)
            {
                if (string.Equals(mod.JointNames[i], "Root", StringComparison.OrdinalIgnoreCase))
                {
                    rootJointIdx = i;
                    break;
                }
            }
            // Register the per-animator state BEFORE adding the component, so
            // the first Update finds it. Managed fields on an IL2CPP-injected
            // MonoBehaviour don't reliably round-trip through the interop, so
            // we key the state off the modRoot's instance id instead.
            ModAnimator.RegisterState(modRoot.GetInstanceID(), new ModAnimatorState
            {
                UnitName = unitRoot.gameObject.name,
                UnitRoot = unitRoot,
                VanillaAnimator = vanillaAnimator,
                VanillaAnimatorXf = vanillaAnimator != null ? vanillaAnimator.transform : null,
                VanillaDirector = vanillaDirector,
                ModRoot = modRoot.transform,
                Bones = bones,
                Animations = mod.Animations,
                RestPos = mod.JointLocalPos,
                RestRot = mod.JointLocalRot,
                RestScale = mod.JointLocalScale,
                CurrentClip = ModAnimator.SelectDefaultClip(mod.Animations),
                Elapsed = 0f,
                RootJointIdx = rootJointIdx,
            });
            modRoot.AddComponent<ModAnimator>();
            Plugin.Log.LogMessage(
                $"  '{unitRoot.gameObject.name}' rig: animator='{(vanillaAnimator != null ? vanillaAnimator.gameObject.name : "<null>")}'" +
                $", director='{(vanillaDirector != null ? vanillaDirector.gameObject.name : "<null>")}'");
        }

        // Stamp the unit root so subsequent scans skip it regardless of any
        // respawn that issues a fresh InstanceID.
        if (unitRoot.gameObject.GetComponent<OEEPatchedMarker>() == null)
            unitRoot.gameObject.AddComponent<OEEPatchedMarker>();

        Plugin.Log.LogMessage(
            $"Patched '{unitRoot.gameObject.name}': {rendererCount} mod renderer(s), " +
            $"{vanillaRenderers.Count} vanilla hidden, {bones.Length} bones, " +
            $"{(mod.Animations?.Length ?? 0)} anims, parent='{modParent.gameObject.name}'.");
    }

    // Replace the cloned vanilla material's diffuse with the mod texture (both
    // URP _BaseMap and Standard _MainTex). Vanilla normal / emission / occlusion
    // / metallic maps refer to the vanilla unit UV layout, so they get nulled
    // out to keep them from smearing as garbage over the mod mesh. If the glTF
    // material asks for alpha MASK, enable alpha clip with its cutoff.
    private static void ApplyModTextureToClone(UMaterial clone, UnityEngine.Texture modTex, int alphaMode, float alphaCutoff, bool doubleSided)
    {
        if (modTex != null)
        {
            try { clone.mainTexture = modTex; } catch { }
            try { clone.SetTexture("_BaseMap", modTex); } catch { }
            try { clone.SetTexture("_MainTex", modTex); } catch { }
        }
        try { clone.SetTexture("_BumpMap", null); } catch { }
        try { clone.SetTexture("_NormalMap", null); } catch { }
        try { clone.SetTexture("_EmissionMap", null); } catch { }
        try { clone.SetTexture("_EmissiveMap", null); } catch { }
        try { clone.SetTexture("_DetailNormalMap", null); } catch { }
        try { clone.SetTexture("_MetallicGlossMap", null); } catch { }
        try { clone.SetTexture("_OcclusionMap", null); } catch { }
        try { clone.DisableKeyword("_NORMALMAP"); } catch { }
        try { clone.DisableKeyword("_EMISSION"); } catch { }
        try { clone.SetColor("_EmissionColor", Color.black); } catch { }
        try { clone.SetColor("_EmissiveColor", Color.black); } catch { }

        if (alphaMode == 1)
        {
            // MASK: alpha-tested cutout. The Hex/Lit shader uses
            // _AlphaClipEnabled + HEX_ALPHA_CLIP_ENABLED keyword (per the
            // AssetRipper shader dump). _AlphaClip / _ALPHATEST_ON are the
            // URP/Lit equivalents; setting both keeps either shader family
            // happy. SetFloat / EnableKeyword silently no-op on absent props.
            try { clone.SetFloat("_AlphaClipEnabled", 1.0f); } catch { }
            try { clone.EnableKeyword("HEX_ALPHA_CLIP_ENABLED"); } catch { }
            try { clone.SetFloat("_AlphaClip", 1.0f); } catch { }
            try { clone.EnableKeyword("_ALPHATEST_ON"); } catch { }
            try { clone.SetFloat("_AlphaCutoff", alphaCutoff); } catch { }
            try { clone.SetFloat("_Cutoff", alphaCutoff); } catch { }
        }
        else if (alphaMode == 2)
        {
            // BLEND: alpha-blended transparency. The vanilla material clone is
            // typically Opaque mode; without flipping it to Transparent the
            // texture alpha is ignored and transparent regions render as solid
            // base color (e.g. the black face-card on the moddolt esquire).
            try { clone.SetFloat("_Surface", 1.0f); } catch { } // URP/Lit: 1=Transparent
            try { clone.SetFloat("_Blend", 0.0f); } catch { }   // URP/Lit: 0=Alpha
            try { clone.SetFloat("_ZWrite", 0.0f); } catch { }
            try { clone.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha); } catch { }
            try { clone.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha); } catch { }
            try { clone.EnableKeyword("_SURFACE_TYPE_TRANSPARENT"); } catch { }
            try { clone.EnableKeyword("_ALPHABLEND_ON"); } catch { }
            try { clone.DisableKeyword("_ALPHATEST_ON"); } catch { }
            try { clone.renderQueue = 3000; } catch { }         // Transparent queue
        }

        if (doubleSided)
        {
            // glTF doubleSided=true -> disable backface culling. Hex/Lit
            // exposes the property as _CullMode (Enum=CullMode, Off=0);
            // URP/Lit and Standard use _Cull. Set both, the irrelevant one
            // no-ops.
            try { clone.SetFloat("_CullMode", 0.0f); } catch { }
            try { clone.SetFloat("_Cull", 0.0f); } catch { }
        }
    }
}

public class ModAnimatorState
{
    public string UnitName;
    public Transform UnitRoot;
    public Animator VanillaAnimator;
    public Transform VanillaAnimatorXf;
    public PlayableDirector VanillaDirector;
    public Transform ModRoot;
    public Transform[] Bones;
    public ModAnim[] Animations;
    public Vector3[] RestPos;
    public Quaternion[] RestRot;
    public Vector3[] RestScale;
    public int CurrentClip;
    public float Elapsed;
    public bool FirstUpdateLogged;
    public int RootJointIdx;
    // Diagnostics: track what the director's last asset name was so we can log
    // changes (which fire when the game swaps the timeline for a one-shot
    // action like attack).
    public string LastDirectorAsset;
    public string LastAnimatorClip;
    public int LastStateHash;
}

// Per-instance keyframe sampler. State lives in a static dictionary keyed by the
// modRoot GameObject's InstanceID. We tried managed fields directly on the
// MonoBehaviour and the IL2CPP interop dropped them between Register and the
// first Update, leaving every patched unit frozen in its rest pose.
public class ModAnimator : MonoBehaviour
{
    public ModAnimator(IntPtr ptr) : base(ptr) { }

    private static readonly Dictionary<int, ModAnimatorState> _states = new Dictionary<int, ModAnimatorState>();

    public static void RegisterState(int hostId, ModAnimatorState st) => _states[hostId] = st;
    public static void UnregisterState(int hostId) => _states.Remove(hostId);

    public void OnDestroy()
    {
        try { _states.Remove(gameObject.GetInstanceID()); } catch { }
    }

    public void Update()
    {
        try
        {
            if (!_states.TryGetValue(gameObject.GetInstanceID(), out var s)) return;
            if (s.Animations == null || s.Animations.Length == 0) return;
            if (s.CurrentClip < 0 || s.CurrentClip >= s.Animations.Length) return;

            // Re-pick the battle Animator each frame if the cached one is
            // missing or still inactive. At patch time the friendly-side
            // battle rig GO can be inactive; once the game's battle stage
            // toggles it on, we want to start reading clip info from that
            // newly-active controller without needing a re-patch.
            if (s.VanillaAnimator == null || !s.VanillaAnimator.gameObject.activeInHierarchy)
            {
                var fresh = PickBattleAnimatorForState(s);
                if (fresh != null && fresh.gameObject.activeInHierarchy)
                {
                    s.VanillaAnimator = fresh;
                    s.VanillaAnimatorXf = fresh.transform;
                }
            }
            // Re-locate the PlayableDirector if we missed it at patch time.
            if (s.VanillaDirector == null && s.UnitRoot != null)
            {
                try { s.VanillaDirector = s.UnitRoot.GetComponentInChildren<PlayableDirector>(true); } catch { }
            }

            // Sync the mod root's world TRS to the vanilla animator GO each
            // frame: world rotation tracks the unit's facing direction (which
            // the game writes on the animator GO every frame), and world scale
            // is normalized to identity-magnitude while preserving any
            // negative-axis mirror the enemy side uses to flip the facing.
            if (s.VanillaAnimatorXf != null && s.ModRoot != null)
            {
                var src = s.VanillaAnimatorXf;
                s.ModRoot.position = src.position;
                s.ModRoot.rotation = src.rotation;
                var lossy = src.lossyScale;
                var dst = new Vector3(
                    lossy.x != 0f ? Mathf.Sign(lossy.x) : 1f,
                    lossy.y != 0f ? Mathf.Sign(lossy.y) : 1f,
                    lossy.z != 0f ? Mathf.Sign(lossy.z) : 1f);
                // The mod root's own lossy scale equals modParent.lossyScale *
                // modRoot.localScale. We want modRoot.lossyScale == dst, so
                // localScale = dst / modParent.lossyScale.
                var parent = s.ModRoot.parent;
                if (parent != null)
                {
                    var pl = parent.lossyScale;
                    s.ModRoot.localScale = new Vector3(
                        pl.x != 0f ? dst.x / pl.x : dst.x,
                        pl.y != 0f ? dst.y / pl.y : dst.y,
                        pl.z != 0f ? dst.z / pl.z : dst.z);
                }
                else s.ModRoot.localScale = dst;
            }

            if (!s.FirstUpdateLogged)
            {
                s.FirstUpdateLogged = true;
                Plugin.Log.LogMessage(
                    $"  ModAnimator running on '{s.UnitName}' [active={gameObject.activeInHierarchy}, " +
                    $"bones={s.Bones.Length}, anims={s.Animations.Length}, " +
                    $"animator='{(s.VanillaAnimator != null ? s.VanillaAnimator.gameObject.name : "<null>")}'" +
                    $", animatorActive={(s.VanillaAnimator != null ? s.VanillaAnimator.gameObject.activeInHierarchy : false)}]");
            }

            FollowVanillaAnimator(s);

            var anim = s.Animations[s.CurrentClip];
            float duration = anim.Duration > 0 ? anim.Duration : 1.0f;
            bool isLooping = IsLoopingClipName(anim.Name);
            s.Elapsed += Time.deltaTime;
            if (s.Elapsed >= duration)
            {
                if (isLooping) s.Elapsed %= duration;
                else s.Elapsed = duration;
            }
            float time = s.Elapsed;

            var bones = s.Bones;
            for (int i = 0; i < bones.Length; i++)
            {
                var b = bones[i];
                if (b == null) continue;
                b.localPosition = s.RestPos[i];
                b.localRotation = s.RestRot[i];
                b.localScale = s.RestScale[i];
            }

            var chs = anim.Channels;
            for (int c = 0; c < chs.Length; c++)
            {
                var ch = chs[c];
                if (ch.TargetJoint < 0 || ch.TargetJoint >= bones.Length) continue;
                // Root-joint translation is the baked walk-cycle stride. The
                // game advances the unit world position itself; applying the
                // channel on top makes the mod skeleton slide while the legs
                // stay frozen. Rotation on the same joint is fine (face turn
                // sweeps), and other joints' translations (Hips bob, foot
                // plant) are the actual animation we want to keep.
                if (ch.TargetJoint == s.RootJointIdx && ch.Path == 0) continue;
                var t = bones[ch.TargetJoint];
                if (t == null) continue;
                SampleChannel(ch, time, t);
            }
        }
        catch (Exception e) { Plugin.Log.LogError($"ModAnimator threw: {e}"); }
    }

    public static int SelectDefaultClip(ModAnim[] anims)
    {
        for (int i = 0; i < anims.Length; i++)
            if (string.Equals(anims[i].Name, "idle", StringComparison.OrdinalIgnoreCase)) return i;
        return 0;
    }

    // Mirror of ModWatcher.PickBattleAnimator but only called for refresh,
    // without the verbose pick log (we'd spam it otherwise).
    private static Animator PickBattleAnimatorForState(ModAnimatorState s)
    {
        if (s.UnitRoot == null) return null;
        Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppArrayBase<Animator> ans;
        try { ans = s.UnitRoot.GetComponentsInChildren<Animator>(true); }
        catch { return null; }
        Animator best = null;
        int bestClips = -1;
        bool bestActive = false;
        for (int i = 0; i < ans.Length; i++)
        {
            var an = ans[i];
            if (an == null) continue;
            int clipCount = 0;
            try
            {
                var rac = an.runtimeAnimatorController;
                if (rac != null && rac.animationClips != null) clipCount = rac.animationClips.Length;
            }
            catch { }
            bool active = an.gameObject.activeInHierarchy;
            bool better;
            if (active && !bestActive) better = true;
            else if (!active && bestActive) better = false;
            else better = clipCount > bestClips;
            if (better) { best = an; bestClips = clipCount; bestActive = active; }
        }
        return best;
    }

    // Exact Mecanim state names taken from the live AssetRipper-imported
    // _Battle_Unit_Anim_ctrl_v2 controller (Unity Editor dump, not a guess).
    // The state suffix " 0" is a Unity duplication marker from when the
    // states were authored, and StringToHash treats "move" and "move 0" as
    // entirely different hashes - so the previous mapping never matched.
    // "Attack Tree 2" is a BlendTree state; GetCurrentAnimatorStateInfo.
    // shortNameHash returns the STATE name (the tree), so we map it directly.
    private static readonly (string state, string modClip)[] _stateToModClip = new[]
    {
        ("idle",            "idle"),
        ("rareIdle 0",      "idle_rare"),
        ("move 0",          "walk"),
        ("moveAbility",     "walk"),
        ("moveAttack",      "attack"),
        ("Attack Tree 2",   "attack"),
        ("damage 0",        "damage"),
        ("die 0",           "death"),
        ("Victory",         "victory"),
        ("fly 0",           "fly"),
        ("flyStart 0",      "fly_start"),
        ("flyEnd 0",        "fly_end"),
        ("flyAbility",      "fly"),
        ("flyAbilityStart", "fly_start"),
        ("flyAbilityEnd",   "fly_end"),
        ("Stance_In",       "idle"),
        ("Stance",          "idle"),
        ("Stance_out",      "idle"),
        ("TpStart",         "idle"),
        ("TpEnd",           "idle"),
        ("TpAbilityStart",  "idle"),
        ("TpAbilityEnd",    "idle"),
    };

    private static Dictionary<int, string> _stateHashToModName;

    private static string ResolveStateHashToModClip(int stateHash)
    {
        if (_stateHashToModName == null)
        {
            var d = new Dictionary<int, string>(_stateToModClip.Length);
            for (int i = 0; i < _stateToModClip.Length; i++)
            {
                int h = Animator.StringToHash(_stateToModClip[i].state);
                d[h] = _stateToModClip[i].modClip;
            }
            _stateHashToModName = d;
        }
        return _stateHashToModName.TryGetValue(stateHash, out var name) ? name : null;
    }

    // Loop-vs-single-shot classification: vanilla Mecanim loops 'idle', 'walk'
    // and a pure 'fly' indefinitely; everything else (attack, damage, victory,
    // death, ability_*, fly_start, fly_end, *_rare counted as a single play
    // alongside its parent loop) is single-shot. NormalizeClipName lowercases
    // and strips non-alphanumerics, so 'Idle_Rare' becomes 'idlerare', which
    // we still treat as a loop because the vanilla controller loops it.
    private static bool IsLoopingClipName(string name)
    {
        var n = NormalizeClipName(name);
        if (n == "idle" || n == "idlerare" || n == "walk" || n == "fly") return true;
        // Tail-loops like 'walkability' / 'walkabilityloop' / '01walk' should
        // still loop. Use a token check instead of a blanket Contains("fly")
        // (which would falsely include fly_start / fly_end).
        if (n.EndsWith("idle") || n.EndsWith("walk") || n.EndsWith("fly")) return true;
        return false;
    }

    private static readonly HashSet<string> _seenClipMatches = new HashSet<string>();
    private static readonly HashSet<string> _seenClipNoMatches = new HashSet<string>();

    private static void FollowVanillaAnimator(ModAnimatorState s)
    {
        // Primary: read the Mecanim state hash on layer 0 and map it to a mod
        // clip via the known _Battle_Unit_Anim_ctrl_v2 state names. The state
        // hash is what actually identifies what the unit is doing (move /
        // attack / damage / ...); the override clip name returned by
        // GetCurrentAnimatorClipInfo is unit-specific decoration.
        var an = s.VanillaAnimator;
        string bestClip = null;
        int stateHash = 0;
        if (an != null)
        {
            try
            {
                var info = an.GetCurrentAnimatorStateInfo(0);
                stateHash = info.shortNameHash;
                bestClip = ResolveStateHashToModClip(stateHash);
            }
            catch { }
        }

        if (stateHash != s.LastStateHash)
        {
            s.LastStateHash = stateHash;
            Plugin.Log.LogMessage($"  [{s.UnitName}] state hash {stateHash} -> mod '{bestClip ?? "<unmapped>"}'");
        }

        // Hold a non-looping mod clip until it finishes (vanilla attack states
        // can be much shorter than the corresponding GLB clip duration).
        var cur = s.Animations[s.CurrentClip];
        if (cur != null && !IsLoopingClipName(cur.Name) && s.Elapsed < cur.Duration) return;

        if (string.IsNullOrEmpty(bestClip)) return;

        int matchIdx = -1;
        for (int i = 0; i < s.Animations.Length; i++)
        {
            if (ClipNamesMatch(s.Animations[i].Name, bestClip))
            {
                matchIdx = i;
                break;
            }
        }

        if (matchIdx < 0)
        {
            if (_seenClipNoMatches.Add(bestClip))
            {
                var sb = new StringBuilder();
                sb.Append($"  clip NO MATCH: vanilla state-mapped '{bestClip}'; mod clips: [");
                for (int i = 0; i < s.Animations.Length; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append(s.Animations[i].Name);
                }
                sb.Append("]");
                Plugin.Log.LogMessage(sb.ToString());
            }
            return;
        }

        if (s.CurrentClip != matchIdx)
        {
            s.CurrentClip = matchIdx;
            s.Elapsed = 0f;
        }
        // No retrigger when the state hash is unchanged. The clip plays to its
        // end and stays clamped on the final pose; if the vanilla state ever
        // transitions away and back, the CurrentClip switch above will reset
        // Elapsed for us. Without this guard, a unit that died (Mecanim sits
        // permanently in 'die 0') would replay its death animation forever.
    }

    // Tolerant clip-name match. The vanilla controller and the GLB export
    // sometimes disagree on punctuation / case ('attack_2' vs 'attack2',
    // 'Idle_Rare' vs 'idle_rare', 'Esquire@attack' vs 'attack'). Compare with
    // all non-alphanumerics stripped and lowercased.
    private static bool ClipNamesMatch(string modName, string vanillaName)
    {
        if (string.IsNullOrEmpty(modName) || string.IsNullOrEmpty(vanillaName)) return false;
        if (string.Equals(modName, vanillaName, StringComparison.OrdinalIgnoreCase)) return true;
        return NormalizeClipName(modName) == NormalizeClipName(vanillaName);
    }

    private static string NormalizeClipName(string name)
    {
        var sb = new StringBuilder(name.Length);
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')) sb.Append(c);
            else if (c >= 'A' && c <= 'Z') sb.Append((char)(c + 32));
        }
        return sb.ToString();
    }

    private static void SampleChannel(ModAnimChannel ch, float time, Transform t)
    {
        var times = ch.Times;
        if (times == null || times.Length == 0) return;
        int n = times.Length;
        if (time <= times[0]) { Apply(ch, t, 0); return; }
        if (time >= times[n - 1]) { Apply(ch, t, n - 1); return; }

        int lo = 0, hi = n - 1;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) >> 1;
            if (times[mid] <= time) lo = mid; else hi = mid;
        }
        int idx0 = lo, idx1 = lo + 1;
        float t0 = times[idx0], t1 = times[idx1];
        float u = (t1 - t0) > 0.000001f ? (time - t0) / (t1 - t0) : 0f;
        if (ch.Interpolation == 1) { Apply(ch, t, idx0); return; }
        Interp(ch, t, idx0, idx1, u);
    }

    private static void Apply(ModAnimChannel ch, Transform t, int idx)
    {
        var v = ch.Values;
        switch (ch.Path)
        {
            case 0: t.localPosition = new Vector3(v[idx * 3], v[idx * 3 + 1], v[idx * 3 + 2]); break;
            case 1: t.localRotation = new Quaternion(v[idx * 4], v[idx * 4 + 1], v[idx * 4 + 2], v[idx * 4 + 3]); break;
            case 2: t.localScale = new Vector3(v[idx * 3], v[idx * 3 + 1], v[idx * 3 + 2]); break;
        }
    }

    private static void Interp(ModAnimChannel ch, Transform t, int i0, int i1, float u)
    {
        var v = ch.Values;
        switch (ch.Path)
        {
            case 0:
                t.localPosition = Vector3.Lerp(
                    new Vector3(v[i0 * 3], v[i0 * 3 + 1], v[i0 * 3 + 2]),
                    new Vector3(v[i1 * 3], v[i1 * 3 + 1], v[i1 * 3 + 2]), u);
                break;
            case 1:
                t.localRotation = Quaternion.Slerp(
                    new Quaternion(v[i0 * 4], v[i0 * 4 + 1], v[i0 * 4 + 2], v[i0 * 4 + 3]),
                    new Quaternion(v[i1 * 4], v[i1 * 4 + 1], v[i1 * 4 + 2], v[i1 * 4 + 3]), u);
                break;
            case 2:
                t.localScale = Vector3.Lerp(
                    new Vector3(v[i0 * 3], v[i0 * 3 + 1], v[i0 * 3 + 2]),
                    new Vector3(v[i1 * 3], v[i1 * 3 + 1], v[i1 * 3 + 2]), u);
                break;
        }
    }
}
