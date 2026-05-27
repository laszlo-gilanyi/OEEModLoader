using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using BepInEx.Logging;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Injection;
using SharpGLTF.Schema2;
using UnityEngine;
using UMesh = UnityEngine.Mesh;
using UMaterial = UnityEngine.Material;
using SysMat4 = System.Numerics.Matrix4x4;

namespace OEEModLoader;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public class Plugin : BasePlugin
{
    public const string PluginGuid = "com.oldenera.explorer.modloader";
    public const string PluginName = "OEE Mod Loader";
    public const string PluginVersion = "0.10.0";

    // Match is substring-based against any ancestor name in the scene tree,
    // so "lava_larva" matches runtime names like "lava_larva #8 S0".
    public static readonly Dictionary<string, ModData> Mods =
        new Dictionary<string, ModData>(StringComparer.OrdinalIgnoreCase);

    internal static new ManualLogSource Log;

    public override void Load()
    {
        Log = base.Log;
        Log.LogMessage($"=== {PluginName} v{PluginVersion} loading ===");

        LoadGlbMods();

        if (Mods.Count == 0)
        {
            Log.LogMessage("No mods loaded; watcher skipped.");
            return;
        }

        try
        {
            ClassInjector.RegisterTypeInIl2Cpp<ModWatcher>();
        }
        catch (Exception e)
        {
            Log.LogError($"RegisterTypeInIl2Cpp<ModWatcher> failed: {e}");
            return;
        }

        var host = new GameObject("OEEModLoader_Watcher");
        UnityEngine.Object.DontDestroyOnLoad(host);
        host.hideFlags = HideFlags.HideAndDontSave;
        host.AddComponent<ModWatcher>();

        Log.LogMessage($"Watcher running. {Mods.Count} mod(s) active. F1 = dump matched GameObjects.");
    }

    private void LoadGlbMods()
    {
        string pluginsDir = Paths.PluginPath;
        if (!Directory.Exists(pluginsDir))
        {
            Log.LogWarning($"Plugins dir not found: {pluginsDir}");
            return;
        }

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
                LogGlbSummary(data);
                Mods[unitId] = data;
            }
            catch (Exception e)
            {
                Log.LogError($"Failed to load mod '{unitId}' from {glbPath}: {e}");
            }
        }
    }

    private static void ExtractGlbData(ModData data, ModelRoot root)
    {
        if (root.LogicalMeshes.Count == 0) return;

        var meshes = new List<ModMesh>(root.LogicalMeshes.Count);
        foreach (var src in root.LogicalMeshes)
            meshes.Add(ExtractMesh(src));
        data.Meshes = meshes.ToArray();

        var skin = root.LogicalSkins.FirstOrDefault();
        if (skin != null)
        {
            data.GlbBindPoses = new Matrix4x4[skin.JointsCount];
            data.GlbJointNames = new string[skin.JointsCount];
            for (int i = 0; i < skin.JointsCount; i++)
            {
                var (joint, ibm) = skin.GetJoint(i);
                data.GlbJointNames[i] = joint?.Name ?? "";
                data.GlbBindPoses[i] = ConvertMatrix(ibm);
            }
        }

        var imgs = new List<byte[]>();
        foreach (var img in root.LogicalImages)
            imgs.Add(img.Content.Content.ToArray());
        data.TextureBytes = imgs.ToArray();
    }

    private static ModMesh ExtractMesh(SharpGLTF.Schema2.Mesh src)
    {
        var verts = new List<Vector3>();
        var norms = new List<Vector3>();
        var uvs = new List<Vector2>();
        var bws = new List<RawBoneWeight>();
        var submeshes = new List<int[]>();
        int vOffset = 0;

        foreach (var prim in src.Primitives)
        {
            var pos = prim.GetVertexAccessor("POSITION")?.AsVector3Array();
            if (pos == null) continue;

            var nrm = prim.GetVertexAccessor("NORMAL")?.AsVector3Array();
            var tex0 = prim.GetVertexAccessor("TEXCOORD_0")?.AsVector2Array();
            var j0 = prim.GetVertexAccessor("JOINTS_0")?.AsVector4Array();
            var w0 = prim.GetVertexAccessor("WEIGHTS_0")?.AsVector4Array();

            // glTF is right-handed +Z forward; Unity is left-handed +Z forward.
            // Negate Z on positions and normals so the mesh sits in Unity space.
            // V is flipped because glTF UV origin is top-left, Unity is bottom-left.
            for (int i = 0; i < pos.Count; i++)
            {
                var p = pos[i]; verts.Add(new Vector3(p.X, p.Y, -p.Z));
                if (nrm != null && i < nrm.Count) { var n = nrm[i]; norms.Add(new Vector3(n.X, n.Y, -n.Z)); }
                else norms.Add(Vector3.up);
                if (tex0 != null && i < tex0.Count) { var u = tex0[i]; uvs.Add(new Vector2(u.X, 1.0f - u.Y)); }
                else uvs.Add(Vector2.zero);

                if (j0 != null && w0 != null && i < j0.Count && i < w0.Count)
                {
                    var jj = j0[i]; var ww = w0[i];
                    bws.Add(new RawBoneWeight
                    {
                        b0 = (int)jj.X, b1 = (int)jj.Y, b2 = (int)jj.Z, b3 = (int)jj.W,
                        w0 = ww.X, w1 = ww.Y, w2 = ww.Z, w3 = ww.W,
                    });
                }
                else
                {
                    bws.Add(default);
                }
            }

            // Z-negation flips winding (CCW becomes CW); reverse indices per triangle
            // so Unity sees CCW front-faces again.
            var idx = prim.GetIndices();
            int triCount = idx.Count / 3;
            var triBuf = new int[idx.Count];
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
            Positions = verts.ToArray(),
            Normals = norms.ToArray(),
            UVs = uvs.ToArray(),
            RawBoneWeights = bws.ToArray(),
            Submeshes = submeshes.ToArray(),
        };
    }

    // S * M * S with S = diag(1,1,-1,1): puts a glTF bindpose into Unity's
    // left-handed system to match the Z-negated mesh data.
    private static Matrix4x4 ConvertMatrix(SysMat4 m) => new Matrix4x4(
        new Vector4( m.M11,  m.M21, -m.M31,  m.M41),
        new Vector4( m.M12,  m.M22, -m.M32,  m.M42),
        new Vector4(-m.M13, -m.M23,  m.M33, -m.M43),
        new Vector4( m.M14,  m.M24, -m.M34,  m.M44));

    private static void LogGlbSummary(ModData data)
    {
        int totalV = 0;
        if (data.Meshes != null)
            for (int i = 0; i < data.Meshes.Length; i++)
                totalV += data.Meshes[i].Positions?.Length ?? 0;
        Log.LogMessage(
            $"  '{data.UnitId}': meshes={data.Meshes?.Length ?? 0}, totalVerts={totalV}, " +
            $"bones={data.GlbJointNames?.Length ?? 0}, " +
            $"images={data.TextureBytes?.Length ?? 0}");
    }

    public static bool TryFindModForName(string name, out ModData mod)
    {
        if (!string.IsNullOrEmpty(name))
        {
            foreach (var kv in Mods)
            {
                if (name.IndexOf(kv.Key, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    mod = kv.Value;
                    return true;
                }
            }
        }
        mod = null;
        return false;
    }
}

public class ModData
{
    public string UnitId;
    public ModMesh[] Meshes;
    public Matrix4x4[] GlbBindPoses;
    public string[] GlbJointNames;
    public byte[][] TextureBytes;
    public Texture2D[] BuiltTextures;
}

public class ModMesh
{
    public Vector3[] Positions;
    public Vector3[] Normals;
    public Vector2[] UVs;
    public RawBoneWeight[] RawBoneWeights;
    public int[][] Submeshes;
}

public struct RawBoneWeight
{
    public int b0, b1, b2, b3;
    public float w0, w1, w2, w3;
}

public class ModWatcher : MonoBehaviour
{
    public ModWatcher(IntPtr ptr) : base(ptr) { }

    private float _nextScanTime;
    private const float ScanInterval = 1.0f;

    // We track unit-root GameObject InstanceIDs, not individual renderers,
    // because per-unit logic (pick primary group, hide other groups) needs
    // the renderers seen together.
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
            if (Input.GetKeyDown(KeyCode.F1)) DumpMatched();
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"ModWatcher.Update threw: {e}");
        }
    }

    private static (Transform root, ModData mod) FindMatchedAncestor(SkinnedMeshRenderer r)
    {
        var t = r.transform;
        while (t != null)
        {
            var go = t.gameObject;
            if (go != null && Plugin.TryFindModForName(go.name, out var mod))
                return (t, mod);
            t = t.parent;
        }
        return (null, null);
    }

    private struct UnitGroup
    {
        public Transform Root;
        public ModData Mod;
        public List<SkinnedMeshRenderer> Renderers;
    }

    private void Scan()
    {
        Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppArrayBase<SkinnedMeshRenderer> renderers;
        try { renderers = Resources.FindObjectsOfTypeAll<SkinnedMeshRenderer>(); }
        catch (Exception e) { Plugin.Log.LogWarning($"FindObjectsOfTypeAll failed: {e.Message}"); return; }

        var groups = new Dictionary<int, UnitGroup>();
        for (int i = 0; i < renderers.Length; i++)
        {
            var r = renderers[i];
            if (r == null) continue;
            var (root, mod) = FindMatchedAncestor(r);
            if (root == null) continue;
            int rootId = root.gameObject.GetInstanceID();
            if (_processedRoots.Contains(rootId)) continue;

            if (!groups.TryGetValue(rootId, out var g))
            {
                g = new UnitGroup { Root = root, Mod = mod, Renderers = new List<SkinnedMeshRenderer>() };
                groups[rootId] = g;
            }
            g.Renderers.Add(r);
        }

        foreach (var kv in groups)
        {
            int rootId = kv.Key;
            var g = kv.Value;
            _processedRoots.Add(rootId);
            try { ApplyUnitGroup(g); }
            catch (Exception e)
            {
                Plugin.Log.LogError($"ApplyUnitGroup failed on root '{g.Root.gameObject.name}': {e}");
            }
        }
    }

    // Strips a trailing "_<digits>" suffix so swarm-instance variants
    // ("body_upg", "body_upg_2", "body_upg_3") collapse to one part key.
    private static string GetPartName(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        int us = name.LastIndexOf('_');
        if (us > 0 && us < name.Length - 1)
        {
            var suf = name.Substring(us + 1);
            bool allDigit = suf.Length > 0;
            for (int i = 0; i < suf.Length; i++)
                if (suf[i] < '0' || suf[i] > '9') { allDigit = false; break; }
            if (allDigit) return name.Substring(0, us);
        }
        return name;
    }

    // Picks the largest part group as "main body" and applies one mod mesh per
    // renderer inside it; every renderer in other part groups (inner mouths,
    // accessories the mod GLB does not address) is force-hidden.
    // Heuristic: this works for swarm units where _N is an instance index.
    // A unit using _N for LOD levels would over-render LOD1/LOD2 on top of LOD0.
    private static void ApplyUnitGroup(UnitGroup g)
    {
        if (g.Renderers.Count == 0) return;

        var partGroups = new Dictionary<string, List<SkinnedMeshRenderer>>(StringComparer.Ordinal);
        for (int i = 0; i < g.Renderers.Count; i++)
        {
            var r = g.Renderers[i];
            if (r == null) continue;
            var part = GetPartName(r.gameObject.name);
            if (!partGroups.TryGetValue(part, out var list))
            {
                list = new List<SkinnedMeshRenderer>();
                partGroups[part] = list;
            }
            list.Add(r);
        }

        // Score = rcount * 1M + total vertex count. Pure renderer count ties
        // (body_upg and belly_inner_upg both 3 on lava_larva); total vert count
        // breaks the tie in favor of the heavier mesh deterministically.
        List<SkinnedMeshRenderer> mainGroup = null;
        string mainPart = null;
        int mainScore = -1;
        foreach (var kv in partGroups)
        {
            int rcount = kv.Value.Count;
            int vsum = 0;
            for (int i = 0; i < rcount; i++)
            {
                var sm = kv.Value[i]?.sharedMesh;
                if (sm != null) vsum += sm.vertexCount;
            }
            int score = rcount * 1000000 + vsum;
            if (score > mainScore)
            {
                mainScore = score;
                mainGroup = kv.Value;
                mainPart = kv.Key;
            }
        }
        if (mainGroup == null) return;

        // Stable name order so per-renderer mod mesh mapping does not flip
        // between scans.
        mainGroup.Sort((a, b) => string.CompareOrdinal(
            a != null ? a.gameObject.name : "",
            b != null ? b.gameObject.name : ""));

        int patched = 0;
        int meshCount = g.Mod.Meshes?.Length ?? 0;
        for (int i = 0; i < mainGroup.Count; i++)
        {
            var r = mainGroup[i];
            if (r == null) continue;
            int meshIdx = meshCount > 0 ? i % meshCount : 0;
            try { ModApplier.ApplyTo(r, g.Mod, meshIdx); patched++; }
            catch (Exception e)
            {
                Plugin.Log.LogError($"ApplyTo failed on '{r.gameObject.name}': {e}");
            }
        }

        int hidden = 0;
        foreach (var kv in partGroups)
        {
            if (ReferenceEquals(kv.Value, mainGroup)) continue;
            for (int i = 0; i < kv.Value.Count; i++)
            {
                var r = kv.Value[i];
                if (r == null) continue;
                r.forceRenderingOff = true;
                hidden++;
            }
        }

        Plugin.Log.LogMessage(
            $"Patched unit '{g.Root.gameObject.name}': main part '{mainPart}' " +
            $"({patched} renderer(s) swapped), {hidden} other renderer(s) hidden across " +
            $"{partGroups.Count - 1} other part group(s).");
    }

    private void DumpMatched()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"--- F1 dump: GameObjects matching mod keys ---");
        Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppArrayBase<GameObject> all;
        try { all = Resources.FindObjectsOfTypeAll<GameObject>(); }
        catch (Exception e) { Plugin.Log.LogError($"F1 FindObjectsOfTypeAll<GameObject> failed: {e}"); return; }

        int hits = 0;
        for (int i = 0; i < all.Length; i++)
        {
            var go = all[i];
            if (go == null) continue;
            var name = go.name;
            if (string.IsNullOrEmpty(name)) continue;

            foreach (var kv in Plugin.Mods)
            {
                if (name.IndexOf(kv.Key, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    int smrCount = 0;
                    try { smrCount = go.GetComponentsInChildren<SkinnedMeshRenderer>(true).Length; }
                    catch { }
                    bool inScene = go.scene.IsValid();
                    sb.AppendLine($"  '{name}' (inScene={inScene}, SkinnedMeshRenderers={smrCount})");
                    hits++;
                    break;
                }
            }
        }
        sb.AppendLine($"--- end dump: {hits} match(es) ---");
        Plugin.Log.LogMessage(sb.ToString());
    }
}

public static class ModApplier
{
    public static void ApplyTo(SkinnedMeshRenderer r, ModData mod, int meshIndex)
    {
        EnsureTextures(mod);

        if (mod.Meshes != null && meshIndex >= 0 && meshIndex < mod.Meshes.Length)
        {
            var src = mod.Meshes[meshIndex];
            if (src != null && src.Positions != null && src.Positions.Length > 0)
            {
                var mesh = BuildMesh(src, mod, r);
                if (mesh != null) r.sharedMesh = mesh;
            }
        }

        if (mod.BuiltTextures != null && mod.BuiltTextures.Length > 0)
        {
            var mats = r.sharedMaterials;
            if (mats != null && mats.Length > 0)
            {
                var newMats = new UMaterial[mats.Length];
                for (int i = 0; i < mats.Length; i++)
                {
                    if (mats[i] == null) { newMats[i] = null; continue; }
                    var clone = new UMaterial(mats[i]);
                    clone.mainTexture = mod.BuiltTextures[0];
                    newMats[i] = clone;
                }
                r.sharedMaterials = newMats;
            }
        }
    }

    private static void EnsureTextures(ModData mod)
    {
        if (mod.BuiltTextures != null) return;
        if (mod.TextureBytes == null || mod.TextureBytes.Length == 0)
        {
            mod.BuiltTextures = new Texture2D[0];
            return;
        }
        var built = new List<Texture2D>(mod.TextureBytes.Length);
        for (int i = 0; i < mod.TextureBytes.Length; i++)
        {
            var bytes = mod.TextureBytes[i];
            if (bytes == null || bytes.Length == 0) continue;
            try
            {
                var tex = new Texture2D(2, 2);
                tex.name = $"OEEMod_{mod.UnitId}_img{i}";
                ImageConversion.LoadImage(tex, bytes);
                built.Add(tex);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Mod '{mod.UnitId}' image {i} LoadImage failed: {e.Message}");
            }
        }
        mod.BuiltTextures = built.ToArray();
    }

    private static UMesh BuildMesh(ModMesh src, ModData mod, SkinnedMeshRenderer renderer)
    {
        var vanillaBones = renderer.bones;
        if (vanillaBones == null)
        {
            Plugin.Log.LogWarning($"Mod '{mod.UnitId}': renderer has null bones array; skipping mesh swap.");
            return null;
        }

        int[] glbToVanilla = null;
        int unmatched = 0;
        if (mod.GlbJointNames != null && mod.GlbJointNames.Length > 0)
        {
            glbToVanilla = new int[mod.GlbJointNames.Length];
            for (int g = 0; g < mod.GlbJointNames.Length; g++)
            {
                glbToVanilla[g] = -1;
                var name = mod.GlbJointNames[g];
                for (int v = 0; v < vanillaBones.Length; v++)
                {
                    var vb = vanillaBones[v];
                    if (vb != null && vb.name == name) { glbToVanilla[g] = v; break; }
                }
                if (glbToVanilla[g] < 0) unmatched++;
            }
            if (unmatched > 0)
            {
                Plugin.Log.LogWarning(
                    $"Mod '{mod.UnitId}': {unmatched}/{mod.GlbJointNames.Length} bone names " +
                    $"have no match in vanilla skeleton; those weights fall through to bone 0.");
            }
        }

        var mesh = new UMesh();
        mesh.name = $"OEEMod_{mod.UnitId}";

        mesh.vertices = src.Positions;
        if (src.Normals != null && src.Normals.Length == src.Positions.Length)
            mesh.normals = src.Normals;
        if (src.UVs != null && src.UVs.Length == src.Positions.Length)
            mesh.uv = src.UVs;

        mesh.subMeshCount = src.Submeshes.Length;
        for (int s = 0; s < src.Submeshes.Length; s++)
            mesh.SetTriangles(src.Submeshes[s], s);

        if (src.RawBoneWeights != null && src.RawBoneWeights.Length == src.Positions.Length && glbToVanilla != null)
        {
            var bws = new BoneWeight[src.RawBoneWeights.Length];
            for (int i = 0; i < bws.Length; i++)
            {
                var rb = src.RawBoneWeights[i];
                bws[i] = new BoneWeight
                {
                    boneIndex0 = Remap(rb.b0, glbToVanilla),
                    boneIndex1 = Remap(rb.b1, glbToVanilla),
                    boneIndex2 = Remap(rb.b2, glbToVanilla),
                    boneIndex3 = Remap(rb.b3, glbToVanilla),
                    weight0 = rb.w0, weight1 = rb.w1, weight2 = rb.w2, weight3 = rb.w3,
                };
            }
            mesh.boneWeights = bws;

            var bp = new Matrix4x4[vanillaBones.Length];
            for (int v = 0; v < vanillaBones.Length; v++) bp[v] = Matrix4x4.identity;
            if (mod.GlbBindPoses != null)
            {
                for (int g = 0; g < glbToVanilla.Length; g++)
                {
                    var vi = glbToVanilla[g];
                    if (vi >= 0 && vi < bp.Length) bp[vi] = mod.GlbBindPoses[g];
                }
            }
            mesh.bindposes = bp;
        }

        mesh.RecalculateBounds();
        // Blender's glTF exporter omits TANGENT; vanilla Unity meshes have it.
        // Without tangents, normal-map sampling produces wrongly-shaded surfaces.
        mesh.RecalculateTangents();
        return mesh;
    }

    private static int Remap(int glbIdx, int[] map)
    {
        if (glbIdx < 0 || glbIdx >= map.Length) return 0;
        var v = map[glbIdx];
        return v >= 0 ? v : 0;
    }
}
