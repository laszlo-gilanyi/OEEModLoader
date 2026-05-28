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
[Message:OEE Mod Loader] === OEE Mod Loader v0.14.0 loading ===
[Message:OEE Mod Loader] Scanning .../BepInEx/plugins for *.glb: found 0 file(s).
```

If you see that, the plugin loaded.

## Make a mod

1. Run OEE, open the unit you want to mod in the Viewer. Note the unit ID shown under the model name (e.g. `esquire`, `crossbowman`, `griffin_upg`).
2. Click the export GLB button.
3. Open the exported GLB in Blender. Edit the mesh, the materials, the textures, or even the animations. The vanilla animations (idle, walk, attack, damage, victory, fly_*, idle_rare, death) are kept in the export and the loader plays whichever one matches the vanilla unit's current Mecanim state.
4. Export back to GLB from Blender (`File > Export > glTF 2.0 (.glb/.gltf)`). Tick **Include > Animations** and **Data > Materials > Export** if you want those round-tripped.
5. Rename the exported file to `<unit_id>.glb` (e.g. `esquire.glb`) and drop it into `BepInEx/plugins/`.
6. Launch the game. The plugin loads the GLB at startup and swaps the unit's appearance + animation whenever the unit spawns in a battle, the preview window, or any other scene.

The unit ID matches the runtime GameObject name **exactly** (case-insensitive, with the Unity `(Clone)` suffix stripped). So `esquire.glb` swaps `esquire` / `Esquire(Clone)`, but leaves `esquire_upg`, `esquire_upg_alt`, and `esquire_map` untouched. If you want to swap one of those, drop a separately-named `esquire_upg.glb`, `esquire_upg_alt.glb`, or `esquire_map.glb` alongside it. The `_map` variants are the strategic-map appearance and are intentionally skipped by `<unit_id>.glb`, so a battle-only swap doesn't ripple onto the world map.

## Constraints for the GLB

- **Bone names should match the vanilla skeleton.** The mod skeleton is rebuilt from the GLB and animated by the GLB's own glTF channels, but the loader still locates the "Root" joint by name to suppress baked walk-cycle root motion (which the game already applies via the unit transform). Renaming `Root` to something else means the mod will slide forward through its own walk cycle.
- **Material alpha mode and double-sidedness.** `alphaMode = MASK` enables alpha clipping with the GLB's cutoff. `alphaMode = BLEND` enables transparent rendering. `doubleSided = true` disables backface culling. All three are read straight from the glTF material.
- **One texture per material.** The base-color texture is taken from the material's `pbrMetallicRoughness.baseColorTexture` channel. Normal maps, metallic-roughness maps, emissive maps, and occlusion maps are intentionally NOT carried over: the loader clones the vanilla unit's material (so the game's `Hex/Lit` shader and lighting rig are preserved) and overrides only its main texture with the GLB's diffuse.
- **Multi-skin GLBs are supported.** Units like `crossbowman` (2 skins) or `griffin` (3 skins) are rebuilt as one SkinnedMeshRenderer per mesh, each bound to its own skin's joint subset.

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

1. **Load.** On `BasePlugin.Load`, the plugin enumerates `BepInEx/plugins/*.glb`. Each file is parsed with SharpGLTF; meshes (positions, normals, UVs, bone weights), bind poses, every glTF node's local TRS (used as the rebuilt skeleton's rest pose), embedded textures, and every animation's translation / rotation / scale channels are kept in memory. The X axis is mirrored on positions, normals and rotations, triangle winding is reversed, UV V is flipped, and bind poses are conjugated through the X mirror to land in Unity left-handed space.
2. **Watcher.** A MonoBehaviour scans `Resources.FindObjectsOfTypeAll<SkinnedMeshRenderer>` once per second. Prefab assets (scene-invalid), already-hidden vanilla renderers, and renderers under our own `OEEMod_*` hierarchy are skipped. The remaining renderers have their transform chain walked up; the first ancestor whose name matches a loaded mod (exact, Clone-stripped) becomes the patch target.
3. **Re-patch guard.** On the first patch of a unit root we add an empty `OEEPatchedMarker` MonoBehaviour to it. The marker survives the game's `Instantiate` respawn cycle (which would otherwise hand us a fresh InstanceID and trigger a full rebuild every scan), so each logical unit is patched exactly once.
4. **Build the mod hierarchy.** Under the unit root we drop a `OEEMod_<unit_id>` GameObject. Below it we rebuild every glTF node as a Unity `Transform`, with parent / child relationships and local TRS taken from the file. For each glTF mesh we add a `SkinnedMeshRenderer` GameObject whose `bones` array points at the rebuilt joints for its skin, with the bind poses, vertex bone weights, and triangle indices carried over.
5. **Materials.** For each mesh we clone the vanilla unit's body material (`Hex/Lit` family) and replace its base color texture with the GLB's `baseColorTexture` image (resolved through `material -> texture -> primary image`). Normal / emission / occlusion / metallic maps are nulled out (they reference the vanilla UV layout). `alphaMode = MASK` is wired up to `_AlphaClipEnabled` + `_AlphaCutoff`; `alphaMode = BLEND` flips the clone to Transparent surface mode and a SrcAlpha / OneMinusSrcAlpha blend; `doubleSided = true` sets `_CullMode = 0`.
6. **Hide vanilla mesh, keep vanilla state machine.** The unit's vanilla `SkinnedMeshRenderer`s get `forceRenderingOff = true` plus a large `localBounds`. The vanilla Mecanim Animator was authored with `cullingMode = CullCompletely`, which would normally halt its state machine when the renderer is culled; the giant bounds keep it visible to the culling system so the state machine keeps ticking. We also pin `cullingMode = AlwaysAnimate` once at patch time as a belt-and-braces measure.
7. **Drive the mod skeleton from the vanilla state machine.** Every frame, a `ModAnimator` MonoBehaviour reads `Animator.GetCurrentAnimatorStateInfo(0).shortNameHash` on the vanilla animator and maps the hash to one of the GLB's animation clips via a static table (`move 0` -> `walk`, `Attack Tree 2` -> `attack`, `damage 0` -> `damage`, etc., taken straight from `_Battle_Unit_Anim_ctrl_v2`). The matching clip is sampled into the rebuilt skeleton's transforms each frame. Non-looping clips (attack, damage, death, victory) clamp on their final pose instead of restarting; loop clips (idle, walk, fly, idle_rare) wrap. The mod skeleton's world TRS is synced from the vanilla animator GameObject every frame so the facing direction, position, and sign-preserving size match the side of the battlefield the unit is on.

## Limitations

- **No bone retargeting.** The mod's animations play on the GLB's own skeleton, not on the vanilla rig. The result is a mod-side rebuild that lives in parallel with the vanilla rig; the vanilla rig itself stays hidden and unused.
- **Single base-color texture per material.** Normal, metallic, occlusion, and emissive maps from the GLB are not currently passed through.
- **Map-variant `_map` swaps require their own GLB.** The default `<unit_id>.glb` deliberately does not touch strategic-map clones.
- **No data overrides.** Stats, abilities, scale (`scale` in the unit's `_v.json` inside `Core.zip`), and any other game data stay vanilla.

## Licence

MIT.
