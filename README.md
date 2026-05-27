# OEE Mod Loader

A BepInEx plugin for Heroes of Might and Magic: Olden Era that swaps unit meshes and textures at runtime from `.glb` files. Companion to [Olden Era Explorer](https://github.com/laszlo-gilanyi/OldenEraExplorer), which extracts vanilla unit models as GLBs that you can edit in Blender and drop back in.

## Requirements

- Heroes of Might and Magic: Olden Era (Steam).
- BepInEx 6 bleeding-edge IL2CPP build. The game uses IL2CPP and Unity 6, which the stable BepInEx 5.x release does not support; only the bleeding-edge 6.x line at [builds.bepinex.dev/projects/bepinex_be](https://builds.bepinex.dev/projects/bepinex_be) does.

## Install BepInEx

Olden Era runs as a 64-bit Windows IL2CPP build (Linux users launch the same `.exe` through Proton), so the same archive applies in both cases.

1. Open [builds.bepinex.dev/projects/bepinex_be](https://builds.bepinex.dev/projects/bepinex_be). Scroll to the **Artifacts** section. Below it is a list of build rows, each a collapsed chevron labelled `#<build-number>` (`#755`, `#754`, etc.) with the commit hash and build date. The topmost row is the latest build. Click it to expand it.
2. The expanded row shows a **Downloads** list. Each row has a filename link on the left and a description on the right. Click the link whose description reads **"BepInEx Unity (IL2CPP) for Windows (x64) games"**. The filename looks like `BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.<build>+<hash>.zip`.
3. Extract its contents directly into the game folder (the one containing `HeroesOldenEra.exe`).
4. Launch the game once. The first run with BepInEx takes longer than usual because it has to generate the IL2CPP interop assemblies under `BepInEx/interop/`. Wait until you reach the main menu, then quit.
5. After this step a `BepInEx/plugins/` folder exists and `BepInEx/LogOutput.log` shows BepInEx and Unity version banners.

For the upstream procedure and any platform-specific notes see [docs.bepinex.dev/master/articles/user_guide/installation/unity_il2cpp.html](https://docs.bepinex.dev/master/articles/user_guide/installation/unity_il2cpp.html).

## Install OEE Mod Loader

Copy these four files into `<GameDir>/BepInEx/plugins/`:

- `OEEModLoader.dll`
- `SharpGLTF.Core.dll`
- `SharpGLTF.Runtime.dll`
- `SharpGLTF.Toolkit.dll`

That folder is in:

- **Windows native**: `C:\Program Files (x86)\Steam\steamapps\common\Heroes of Might and Magic Olden Era\BepInEx\plugins\`
- **Linux Proton**: `~/.local/share/Steam/steamapps/common/Heroes of Might and Magic Olden Era/BepInEx/plugins/`

Launch the game once and check `BepInEx/LogOutput.log` for:

```
[Message:OEE Mod Loader] === OEE Mod Loader v0.10.0 loading ===
[Message:OEE Mod Loader] Scanning .../BepInEx/plugins for *.glb: found 0 file(s).
```

If you see that, the plugin loaded.

## Make a mod

1. Run OEE, open the unit you want to mod in the Viewer. Note the unit ID shown under the model name (e.g. `lava_larva`).
2. Click the export GLB button.
3. Open the exported GLB in Blender. Edit the mesh, the materials, the textures. Keep the armature and the bone names intact.
4. Export back to GLB from Blender (`File > Export > glTF 2.0 (.glb/.gltf)`).
5. Rename the exported file to `<unit_id>.glb` (e.g. `lava_larva.glb`) and drop it into `BepInEx/plugins/`.
6. Launch the game. The plugin loads the GLB at startup and swaps the unit's appearance whenever the unit shows up in a battle or scene.

The unit ID is matched as a case-insensitive substring against runtime GameObject names, so a name like `lava_larva #8 S0` matches `lava_larva.glb`.

## Constraints for the GLB

- **Bone names must match the vanilla skeleton.** Bone weights remap by name. A bone in the mod GLB with no vanilla counterpart silently falls through to bone 0, which usually produces broken deformation.
- **One swarm-instance per logical mesh in the GLB.** A unit like `lava_larva` is three creatures in one stack; vanilla has one mesh per creature, weighted to its own bone subtree (no prefix, `Bug2_*`, `Bug3_*`). Your mod GLB should also have one mesh per swarm-instance in the same order. If your mod has fewer meshes than vanilla has swarm-instances, the plugin reuses your meshes cyclically.
- **Single-creature units use one mesh.** A unit with no swarm-instances expects one mesh in the GLB.
- **Body part heuristic.** When a unit has multiple part groups (`body_upg`, `belly_inner_upg`), the plugin picks the largest group as "main body" and hides the rest. If you want to mod a different part (e.g. the inner mouth), the current version does not support that without picking a name match for that group.

## Hotkeys

- **F1**: dumps every loaded `GameObject` whose name contains a registered mod ID, into the BepInEx log. Useful for diagnosing whether the plugin sees the unit at all and what name the game uses for it.

## Build from source

```
cd OEEModLoader
dotnet build -c Release
```

The project's csproj has a `DeployToPlugins` target that copies the built DLLs into `<GameDir>/BepInEx/plugins/` automatically. The default `GameDir` is hardcoded for one specific install path; override on the command line if yours is elsewhere:

```
dotnet build -c Release -p:GameDir="/path/to/Heroes of Might and Magic Olden Era"
```

References (BepInEx core DLLs, IL2CPP interop assemblies) are resolved from `<GameDir>/BepInEx/core/` and `<GameDir>/BepInEx/interop/`, so the game must be installed with BepInEx running before the project will build.

## How it works

1. **Load.** On `BasePlugin.Load`, the plugin enumerates `BepInEx/plugins/*.glb`. Each file is parsed with SharpGLTF; positions, normals, UVs, bone weights, bind poses, joint names, and embedded texture bytes are kept in memory.
2. **glTF to Unity conversion.** glTF stores meshes in a right-handed coordinate system with UV origin at top-left. Unity uses left-handed with UV origin at bottom-left. Position Z and normal Z are negated, triangle winding is reversed, UV V is flipped, and bind poses get an `S * M * S` (with `S = diag(1,1,-1,1)`) transform to land in Unity space.
3. **Watcher.** A MonoBehaviour scans `Resources.FindObjectsOfTypeAll<SkinnedMeshRenderer>` once per second. For each renderer, the transform tree is walked up looking for a `GameObject` whose name contains a registered mod ID.
4. **Patch.** Renderers under the same matched ancestor are grouped together; each group is patched once. The plugin partitions a unit's renderers by part name (stripping trailing `_<digit>` swarm-index suffixes), keeps the largest part group, applies one mod mesh per renderer in it (with bone-name-based weight remapping), and force-hides every renderer in the other part groups.
5. **Materials.** Vanilla materials are cloned per renderer and their `mainTexture` is replaced with the first image from the GLB. Other vanilla material properties (shader, normal map, PBR factors) are preserved.

## Limitations

- One main-body group per unit; non-main part groups (inner parts, secondary attachments) are hidden, not modded. A future revision could let mods address specific part groups.
- Mod materials beyond the diffuse texture (normal map, emission, etc.) are not exposed; the vanilla material is reused with only `mainTexture` overridden.
- Units whose LOD switching uses a `_<digit>` suffix on renderer names (rather than swarm-instance indices) will over-render LOD1/LOD2 on top of LOD0. The plugin currently treats `_<digit>` as instance index. Not observed yet on test cases.
- No data overrides. Stats, scale (`scale` in the unit's `_v.json` inside `Core.zip`), abilities, etc. stay vanilla.

## Licence

MIT.
