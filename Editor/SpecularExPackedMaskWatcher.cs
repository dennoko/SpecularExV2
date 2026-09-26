#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using Unity.Profiling;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Dennokoworks.SpecularExV2
{
    // Decides WHEN SpecularExPackedMaskStore.EnsureAll runs. Every trigger only queues work; the queue
    // is processed once per editor update outside of imports, compilation and play-mode transitions. There is no retry, backoff or loop
    // guard: EnsureAll is idempotent, so a repeated or overly broad trigger is a cheap no-op, and a
    // failure is logged and waits for the next real change (or the manual rebuild button).
    //
    // Triggers:
    //   * Scene content: after a domain reload, when a scene or Prefab Stage is opened, when objects
    //     are created (prefab placement, paste) and when a Renderer changes (material swap). All
    //     renderers count, active or not. This also migrates materials from the old in-memory preview.
    //   * Inspector: whenever the drawn material's mask slot references differ from what was last
    //     checked (covers opening, editing, paste, Undo while inspected, switching to SpecularExV2).
    //   * Material property changes published by the editor (edits from other windows, Undo), when
    //     the slot references differ from the last check.
    //   * Imported material containers (.mat, .asset, models) that depend on a SpecularExV2 shader: every
    //     material inside is checked. Others are skipped without loading them.
    //   * Imported or deleted source textures: saved materials that depend on them, and in-memory
    //     materials (scene-embedded, script-created clones) checked earlier that use them.
    //   * The VRChat avatar build hook calls EnsureAll directly (SpecularExPackedMaskBuildHook).
    [InitializeOnLoad]
    public static class SpecularExPackedMaskWatcher
    {
        static readonly HashSet<Material> _materials = new HashSet<Material>();
        static readonly HashSet<string> _containerPaths = new HashSet<string>();
        static readonly HashSet<string> _texturePaths = new HashSet<string>();
        static bool _texturesDeleted;
        static bool _scanScenes;
        static bool _scheduled;

        // Material instance ID -> its slot references (sources and packed) when last queued or checked.
        static readonly Dictionary<int, int[]> _slotState = new Dictionary<int, int[]>();

        // Materials that are not assets (scene-embedded, created by scripts) and were checked before.
        // AssetDatabase searches cannot find them, so source texture changes are matched against this set.
        static readonly HashSet<Material> _inMemory = new HashSet<Material>();

        // .mat files just saved by EnsureAll, with the material's slots at that moment. The reimport the
        // save causes is skipped once if the material still matches; anything else (another save, an
        // external edit, a late or missing notification) is processed normally.
        struct SelfSave
        {
            public int id;
            public int[] state;
            public double expires;
        }

        const double SelfSaveLifetime = 5; // seconds
        static readonly Dictionary<string, SelfSave> _selfSaved = new Dictionary<string, SelfSave>(System.StringComparer.OrdinalIgnoreCase);

        static readonly HashSet<string> TextureExtensions = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".tga", ".psd", ".tif", ".tiff", ".bmp", ".gif", ".exr", ".hdr",
            ".iff", ".pict", ".asset", ".rendertexture",
        };

        // Files that can hold materials (models embed them as sub-assets).
        static readonly HashSet<string> MaterialContainerExtensions = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase)
        {
            ".mat", ".asset", ".fbx", ".obj", ".blend", ".dae", ".3ds", ".max", ".ma", ".mb",
        };

        static readonly ProfilerMarker ProcessMarker = new ProfilerMarker("SpecularExV2.Watcher.Process");
        static readonly ProfilerMarker SceneMarker = new ProfilerMarker("SpecularExV2.Watcher.CollectScene");
        static readonly ProfilerMarker AffectedMarker = new ProfilerMarker("SpecularExV2.Watcher.CollectAffected");

        // Debug timing log: one line per processed batch. Off by default; toggled from the menu.
        const string DebugTimingPref = "SpecularExV2.DebugTiming";
        const string DebugTimingMenu = "Window/SpecularExV2/Packed Masks/Debug Timing Log";
        static bool DebugTiming => EditorPrefs.GetBool(DebugTimingPref, false);

        [MenuItem(DebugTimingMenu)]
        static void ToggleDebugTiming() => EditorPrefs.SetBool(DebugTimingPref, !DebugTiming);

        [MenuItem(DebugTimingMenu, true)]
        static bool ToggleDebugTimingValidate()
        {
            Menu.SetChecked(DebugTimingMenu, DebugTiming);
            return true;
        }

        static SpecularExPackedMaskWatcher()
        {
            ObjectChangeEvents.changesPublished += OnChangesPublished;
            EditorSceneManager.sceneOpened += (_, __) => RequestSceneScan();
            PrefabStage.prefabStageOpened += _ => RequestSceneScan();
            // Domain reload: covers opening the project and the first load after upgrading from the
            // in-memory preview, whose textures were never saved.
            RequestSceneScan();
        }

        public static void Request(Material m)
        {
            if (m == null) return;
            _materials.Add(m);
            Schedule();
        }

        // Cheap enough for OnGUI and per-frame change events: compares slot references only (sources
        // and packed), queues when they changed. Changed source *contents* arrive as texture imports.
        public static void RequestIfSlotsChanged(Material m)
        {
            if (!SpecularExMaskPacker.HasPackedSlot(m)) return;
            var state = SpecularExPackedMaskStore.SlotSnapshot(m);
            int id = m.GetInstanceID();
            if (_slotState.TryGetValue(id, out var prev) && SpecularExPackedMaskStore.SnapshotEquals(prev, state)) return;
            _slotState[id] = state;
            Request(m);
        }

        public static void NoteSelfSave(Material m)
        {
            string path = AssetDatabase.GetAssetPath(m);
            if (string.IsNullOrEmpty(path)) return;
            _selfSaved[path] = new SelfSave
            {
                id = m.GetInstanceID(),
                state = SpecularExPackedMaskStore.SlotSnapshot(m),
                expires = EditorApplication.timeSinceStartup + SelfSaveLifetime,
            };
        }

        public static void ForgetSelfSave(Material m)
        {
            string path = AssetDatabase.GetAssetPath(m);
            if (!string.IsNullOrEmpty(path)) _selfSaved.Remove(path);
        }

        // Whether this container import is the reimport of our own save and the material is still what
        // was saved. Consumes the registration either way.
        static bool IsOwnSave(string path, List<Material> materials)
        {
            if (!_selfSaved.TryGetValue(path, out var save)) return false;
            _selfSaved.Remove(path);
            return EditorApplication.timeSinceStartup <= save.expires
                   && materials.Count == 1
                   && materials[0].GetInstanceID() == save.id
                   && SpecularExPackedMaskStore.SnapshotEquals(save.state, SpecularExPackedMaskStore.SlotSnapshot(materials[0]));
        }

        static void PurgeExpiredSelfSaves()
        {
            if (_selfSaved.Count == 0) return;
            double now = EditorApplication.timeSinceStartup;
            var expired = new List<string>();
            foreach (var kv in _selfSaved)
                if (now > kv.Value.expires) expired.Add(kv.Key);
            foreach (var path in expired) _selfSaved.Remove(path);
        }

        static void RequestSceneScan()
        {
            _scanScenes = true;
            Schedule();
        }

        static void Schedule()
        {
            if (_scheduled) return;
            _scheduled = true;
            EditorApplication.update += Process;
        }

        static bool Busy =>
            EditorApplication.isCompiling ||
            EditorApplication.isUpdating ||
            BuildPipeline.isBuildingPlayer ||
            (EditorApplication.isPlayingOrWillChangePlaymode && !EditorApplication.isPlaying);

        static void Process()
        {
            if (Busy) return;
            EditorApplication.update -= Process;
            _scheduled = false;

            using var _ = ProcessMarker.Auto();
            var timer = DebugTiming ? System.Diagnostics.Stopwatch.StartNew() : null;
            int requested = _materials.Count, containers = _containerPaths.Count, textures = _texturePaths.Count;
            bool scanned = _scanScenes, deleted = _texturesDeleted;
            long collectMs = 0;
            int ownSaves = 0;

            var targets = new List<Material>(_materials);
            _materials.Clear();

            if (_scanScenes)
            {
                _scanScenes = false;
                using (SceneMarker.Auto())
                    CollectSceneMaterials(targets);
            }

            // A full project import reports every model and .asset here; loading them all is what
            // made first imports slow, so only files that reference a SpecularExV2 shader are loaded.
            foreach (var path in _containerPaths)
            {
                if (!SpecularExPackedMaskStore.UsesSpecularExShader(path)) { _selfSaved.Remove(path); continue; }
                var materials = SpecularExPackedMaskStore.LoadMaterialsAtPath(path);
                if (IsOwnSave(path, materials)) { ownSaves++; continue; }
                targets.AddRange(materials);
            }
            _containerPaths.Clear();
            PurgeExpiredSelfSaves();

            if (_texturePaths.Count > 0 || _texturesDeleted)
            {
                using (AffectedMarker.Auto())
                {
                    CollectAffectedAssetMaterials(targets, _texturePaths, _texturesDeleted);
                    CollectAffectedInMemoryMaterials(targets, _texturePaths, _texturesDeleted);
                }
            }
            _texturePaths.Clear();
            _texturesDeleted = false;
            if (timer != null) collectMs = timer.ElapsedMilliseconds;

            try { SpecularExPackedMaskStore.EnsureAll(targets, persist: true, useCache: true); }
            catch (System.Exception e) { Debug.LogException(e); }

            // The check may have assigned packed textures; record the result so the change events
            // caused by our own SetTexture do not queue the same materials again.
            foreach (var m in targets)
                if (SpecularExMaskPacker.HasPackedSlot(m))
                    _slotState[m.GetInstanceID()] = SpecularExPackedMaskStore.SlotSnapshot(m);

            if (timer != null)
            {
                var st = SpecularExPackedMaskStore.LastStats;
                Debug.Log($"[SpecularExV2] Watcher: {timer.ElapsedMilliseconds} ms (collect {collectMs} ms) | " +
                          $"requested {requested}, containers {containers} (own saves {ownSaves}), textures {textures}, deleted {deleted}, scene scan {scanned} | " +
                          $"targets {targets.Count}, examined {st.materials}, written {st.written}, reimported {st.reimported}, assigned {st.assigned}");
            }

            foreach (var m in targets)
                if (SpecularExMaskPacker.HasPackedSlot(m) && !EditorUtility.IsPersistent(m))
                    _inMemory.Add(m);
        }

        static void CollectSceneMaterials(List<Material> targets)
        {
            for (int s = 0; s < SceneManager.sceneCount; s++)
            {
                var scene = SceneManager.GetSceneAt(s);
                if (!scene.isLoaded) continue;
                foreach (var root in scene.GetRootGameObjects())
                    CollectRendererMaterials(root, targets);
            }
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage != null && stage.prefabContentsRoot != null)
                CollectRendererMaterials(stage.prefabContentsRoot, targets);
        }

        static void CollectRendererMaterials(GameObject root, List<Material> targets)
        {
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                foreach (var m in r.sharedMaterials)
                    if (SpecularExMaskPacker.HasPackedSlot(m)) targets.Add(m);
        }

        // Saved SpecularExV2 materials that use one of the changed textures. A deleted path no longer shows
        // up as a dependency (neither a source nor a generated mask), so after a deletion every material
        // using a SpecularExV2 shader is re-checked instead; EnsureAll is a no-op for the unaffected ones.
        static void CollectAffectedAssetMaterials(List<Material> targets, HashSet<string> changed, bool anyDeleted)
        {
            foreach (var path in SpecularExPackedMaskStore.FindMaterialContainerPaths())
            {
                var deps = AssetDatabase.GetDependencies(path, false);
                if (!SpecularExPackedMaskStore.UsesSpecularExShader(deps)) continue;
                bool hit = anyDeleted;
                for (int i = 0; !hit && i < deps.Length; i++) hit = changed.Contains(deps[i]);
                if (hit) targets.AddRange(SpecularExPackedMaskStore.LoadMaterialsAtPath(path));
            }
        }

        static void CollectAffectedInMemoryMaterials(List<Material> targets, HashSet<string> changed, bool anyDeleted)
        {
            _inMemory.RemoveWhere(m => !SpecularExMaskPacker.HasPackedSlot(m)); // destroyed or shader switched
            foreach (var m in _inMemory)
            {
                // A deleted source turns the slot into a missing reference; just re-check everything.
                bool hit = anyDeleted;
                for (int i = 0; !hit && i < SpecularExMaskPacker.SourceProps.Length; i++)
                {
                    var t = SpecularExMaskPacker.GetSource(m, i);
                    hit = t != null && changed.Contains(AssetDatabase.GetAssetPath(t));
                }
                if (hit) targets.Add(m);
            }
        }

        static void OnChangesPublished(ref ObjectChangeEventStream stream)
        {
            for (int i = 0; i < stream.length; i++)
            {
                switch (stream.GetEventType(i))
                {
                    case ObjectChangeKind.ChangeAssetObjectProperties:
                    {
                        // Fired for every property edit (slider drags publish one per frame); only slot
                        // reference changes matter.
                        stream.GetChangeAssetObjectPropertiesEvent(i, out var data);
                        if (EditorUtility.InstanceIDToObject(data.instanceId) is Material m)
                            RequestIfSlotsChanged(m);
                        break;
                    }
                    case ObjectChangeKind.CreateGameObjectHierarchy:
                    {
                        stream.GetCreateGameObjectHierarchyEvent(i, out var data);
                        if (EditorUtility.InstanceIDToObject(data.instanceId) is GameObject go)
                            RequestRenderers(go);
                        break;
                    }
                    case ObjectChangeKind.ChangeGameObjectOrComponentProperties:
                    {
                        // Only Renderers matter (material swaps); ignore transforms etc. to stay cheap.
                        // BlendShape drags publish one event per frame, so unchanged materials are skipped.
                        stream.GetChangeGameObjectOrComponentPropertiesEvent(i, out var data);
                        if (EditorUtility.InstanceIDToObject(data.instanceId) is Renderer r)
                            foreach (var m in r.sharedMaterials)
                                RequestIfSlotsChanged(m);
                        break;
                    }
                }
            }
        }

        static void RequestRenderers(GameObject root)
        {
            var list = new List<Material>();
            CollectRendererMaterials(root, list);
            foreach (var m in list) RequestIfSlotsChanged(m);
        }

        // Only the static OnPostprocessAllAssets callback. Do NOT add per-type callbacks such as
        // OnPreprocessTexture or override GetVersion: those become an import dependency of every asset of
        // that type, so installing or updating SpecularExV2 would reimport the whole project's textures.
        // Generated files get their import settings from SpecularExPackedMaskStore instead.
        class Postprocessor : AssetPostprocessor
        {
            static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
            {
                bool queued = false;
                foreach (var path in imported)
                {
                    if (SpecularExPackedMaskStore.IsGeneratedPath(path))
                    {
                        // Our own writes and manual edits alike: re-verify its import settings next time.
                        SpecularExPackedMaskStore.InvalidateImportSettings(path);
                        continue;
                    }
                    string ext = Path.GetExtension(path);
                    // .asset can be either, so it is checked both ways.
                    if (MaterialContainerExtensions.Contains(ext))
                    {
                        _containerPaths.Add(path);
                        queued = true;
                    }
                    if (TextureExtensions.Contains(ext))
                    {
                        _texturePaths.Add(path);
                        queued = true;
                    }
                }
                // Moves keep the GUID and contents, so they do not change any packed mask. They can
                // still move a generated file or the shaders, which the cached lookups depend on.
                if (deleted.Length > 0 || moved.Length > 0) SpecularExPackedMaskStore.InvalidateShaderFolder();
                foreach (var path in moved) SpecularExPackedMaskStore.InvalidateImportSettings(path);
                foreach (var path in movedFrom) SpecularExPackedMaskStore.InvalidateImportSettings(path);
                foreach (var path in deleted)
                {
                    SpecularExPackedMaskStore.InvalidateImportSettings(path);
                    if (SpecularExPackedMaskStore.IsGeneratedPath(path)
                        || TextureExtensions.Contains(Path.GetExtension(path)))
                    {
                        _texturesDeleted = true;
                        queued = true;
                    }
                }
                if (queued) Schedule();
            }
        }
    }
}
#endif
