#if UNITY_EDITOR
using System.Collections.Generic;
using Unity.Profiling;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Dennokoworks.SpecularExV2
{
    // Decides WHEN SpecularExPackedMaskStore.EnsureAll runs. Every trigger only queues work; the queue
    // is processed once per editor update outside of imports, compilation and play-mode transitions.
    // There is no retry or backoff: EnsureAll is idempotent, and a failure is logged and waits for the
    // next real change (or a manual repair).
    //
    // Scope: the watcher keeps the materials the user is looking at correct, not the whole project.
    // It tracks the materials of the renderers in the loaded scenes and the Prefab Stage (inactive ones
    // included), plus every material it was asked to check (inspector, change events). Anything else
    // (a .mat that is not in a scene, a clip-only material) is fixed when it enters a scene, is
    // inspected, is repaired from the menus below, or is built: the VRChat build hook and the NDMF
    // plugin check every avatar material without any cache. Nothing here scans the project.
    //
    // Triggers:
    //   * Scene rescan: after a domain reload, when a scene or Prefab Stage is opened or closed, when
    //     play mode starts or ends, and when a tracked material was destroyed.
    //     Materials of active renderers are checked; materials used only by inactive renderers are
    //     tracked but deferred (they are not drawn) until they are activated, inspected, edited,
    //     repaired or built.
    //   * Inspector: whenever the drawn material's slot references differ from the last check.
    //   * Change events: material slot edits, renderer changes and created objects (when the slot
    //     references differ from the last check), and activated GameObjects (their deferred materials).
    //   * Asset changes: an imported or deleted file that a tracked material referenced when last
    //     checked (source slots, packed slots, or the material's own file). A lookup table, no loading.
    //   * Background bakes: the materials waiting on a file once it is imported.
    //
    // The automatic checks bake missing files in the background (EnsureAll background: true), so
    // setting a mask does not block the editor; the material shows Pending until the file exists.
    // The manual repair commands bake synchronously.
    //
    // Invariant: the slot state and the store's verified cache only hold tracked materials. A material
    // that leaves the tracking loses both, so it is fully checked again when it comes back.
    [InitializeOnLoad]
    public static class SpecularExPackedMaskWatcher
    {
        static readonly HashSet<Material> _materials = new HashSet<Material>();
        static readonly HashSet<int> _dirtyInputs = new HashSet<int>();     // a referenced texture changed
        static readonly HashSet<int> _dirtyContainers = new HashSet<int>(); // the material's own file was imported
        static bool _rescan;
        static bool _scheduled;

        static HashSet<string> NewPathSet() => new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);

        // Material instance ID -> its slot references (sources and packed) when last queued or checked.
        static readonly Dictionary<int, int[]> _slotState = new Dictionary<int, int[]>();

        // Instance ID -> a tracked material and the asset paths it referenced when last tracked: its slot
        // textures and its own file (null when scene-embedded or in memory).
        class Tracked
        {
            public Material material;
            public string self;
            public string[] paths;
        }

        static readonly Dictionary<int, Tracked> _tracked = new Dictionary<int, Tracked>();
        static readonly Dictionary<string, HashSet<int>> _byPath = new Dictionary<string, HashSet<int>>(System.StringComparer.OrdinalIgnoreCase);

        // Tracked materials used only by inactive renderers and not checked yet.
        static readonly HashSet<int> _deferred = new HashSet<int>();

        static readonly ProfilerMarker ProcessMarker = new ProfilerMarker("SpecularExV2.Watcher.Process");
        static readonly ProfilerMarker SceneMarker = new ProfilerMarker("SpecularExV2.Watcher.RescanScenes");

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
            EditorSceneManager.sceneClosed += _ => RequestSceneScan();
            PrefabStage.prefabStageOpened += _ => RequestSceneScan();
            PrefabStage.prefabStageClosing += _ => RequestSceneScan();
            // Without a domain reload on play mode changes, the scene is recreated with new material
            // instances (scene-embedded ones) that nothing else reports.
            SpecularExPackedMaskStore.BakesImported += OnBakesImported;
            EditorApplication.playModeStateChanged += state =>
            {
                if (state == PlayModeStateChange.EnteredEditMode || state == PlayModeStateChange.EnteredPlayMode)
                    RequestSceneScan();
            };
            // Domain reload: static state is gone, and the scenes are all the watcher has to rebuild.
            // Also covers opening the project and the first load after upgrading from the in-memory
            // preview, whose textures were never saved.
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

        static void RequestSceneScan()
        {
            _rescan = true;
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
            int requested = _materials.Count, inputs = _dirtyInputs.Count, containers = _dirtyContainers.Count;

            var targets = new List<Material>(_materials);
            _materials.Clear();

            // A changed input invalidates the cached check. The material's own file needs no
            // invalidation: its slot references are compared anyway (a self-save is a cheap no-op).
            // Deferred materials stay deferred; they are checked in full when activated.
            foreach (int id in _dirtyInputs) CollectDirty(id, invalidate: true, targets);
            foreach (int id in _dirtyContainers) CollectDirty(id, invalidate: false, targets);
            _dirtyInputs.Clear();
            _dirtyContainers.Clear();

            bool scanned = _rescan;
            if (_rescan)
            {
                _rescan = false;
                using (SceneMarker.Auto())
                    RescanScenes(targets);
            }
            long collectMs = timer?.ElapsedMilliseconds ?? 0;

            try { SpecularExPackedMaskStore.EnsureAll(targets, persist: true, useCache: true, background: true); }
            catch (System.Exception e) { Debug.LogException(e); }
            NoteChecked(targets);

            if (timer != null)
            {
                var st = SpecularExPackedMaskStore.LastStats;
                Debug.Log($"[SpecularExV2] Watcher: {timer.ElapsedMilliseconds} ms (collect {collectMs} ms) | " +
                          $"requested {requested}, changed inputs {inputs}, changed files {containers}, scene scan {scanned} | " +
                          $"targets {targets.Count}, examined {st.materials} (cached {st.cached}), written {st.written}, background {st.background}, reimported {st.reimported}, assigned {st.assigned} | " +
                          $"tracked {_tracked.Count}, deferred {_deferred.Count}");
            }
        }

        // Their files exist now; the next check assigns them. They were not recorded as verified.
        static void OnBakesImported(List<Material> materials)
        {
            if (DebugTiming)
                Debug.Log($"[SpecularExV2] Background bakes: imported in {SpecularExPackedMaskStore.LastBakeImportMs} ms " +
                          $"for {materials.Count} material(s), {SpecularExPackedMaskStore.PendingBakes} bake(s) still running");
            foreach (var m in materials) Request(m);
        }

        static void CollectDirty(int id, bool invalidate, List<Material> targets)
        {
            if (!_tracked.TryGetValue(id, out var t)) return;
            var m = t.material;
            if (!SpecularExMaskPacker.HasPackedSlot(m))
            {
                // Destroyed (e.g. a model reimport recreated its materials) or no longer SpecularExV2:
                // the renderers may reference a replacement now.
                Untrack(id);
                _rescan = true;
                return;
            }
            if (_deferred.Contains(id)) return;
            if (invalidate) SpecularExPackedMaskStore.InvalidateVerified(m);
            targets.Add(m);
        }

        // The check may have assigned packed textures; record the result so the change events caused
        // by our own SetTexture do not queue the same materials again.
        static void NoteChecked(IEnumerable<Material> materials)
        {
            foreach (var m in materials)
            {
                if (!SpecularExMaskPacker.HasPackedSlot(m)) continue;
                int id = m.GetInstanceID();
                _slotState[id] = SpecularExPackedMaskStore.SlotSnapshot(m);
                _deferred.Remove(id);
                Track(m);
            }
        }

        // ------------------------------------------------------------------------------------------
        //  Scene content
        // ------------------------------------------------------------------------------------------

        // Rebuilds the tracking from the loaded scenes. Materials of active renderers are added to
        // `targets` (cheap when verified); inactive-only ones are deferred unless already checked.
        // Tracked materials no longer in a scene are dropped, except this batch's targets.
        static void RescanScenes(List<Material> targets)
        {
            var found = new Dictionary<Material, bool>(); // material -> used by an active renderer
            for (int s = 0; s < SceneManager.sceneCount; s++)
            {
                var scene = SceneManager.GetSceneAt(s);
                if (!scene.isLoaded) continue;
                foreach (var root in scene.GetRootGameObjects())
                    CollectRendererMaterials(root, found);
            }
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage != null && stage.prefabContentsRoot != null)
                CollectRendererMaterials(stage.prefabContentsRoot, found);

            var keep = new HashSet<int>();
            foreach (var m in targets)
                if (m != null) keep.Add(m.GetInstanceID());
            foreach (var m in found.Keys) keep.Add(m.GetInstanceID());
            var gone = new List<int>();
            foreach (int id in _tracked.Keys)
                if (!keep.Contains(id)) gone.Add(id);
            foreach (int id in gone) Untrack(id);

            foreach (var kv in found)
            {
                if (kv.Value) targets.Add(kv.Key);
                else Defer(kv.Key);
            }
        }

        static void CollectRendererMaterials(GameObject root, Dictionary<Material, bool> found)
        {
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                bool active = r.enabled && r.gameObject.activeInHierarchy;
                foreach (var m in r.sharedMaterials)
                {
                    if (!SpecularExMaskPacker.HasPackedSlot(m)) continue;
                    found[m] = active || (found.TryGetValue(m, out bool a) && a);
                }
            }
        }

        // Tracks a material of an inactive renderer without checking it. One already checked (slot
        // state recorded) stays checked: asset changes keep it up to date like any other.
        static void Defer(Material m)
        {
            int id = m.GetInstanceID();
            if (_slotState.ContainsKey(id) || _materials.Contains(m)) return;
            Track(m);
            _deferred.Add(id);
        }

        static void RequestRenderers(GameObject root)
        {
            var found = new Dictionary<Material, bool>();
            CollectRendererMaterials(root, found);
            foreach (var kv in found)
            {
                if (kv.Value) RequestIfSlotsChanged(kv.Key);
                else Defer(kv.Key);
            }
        }

        // An activated object: check the deferred materials its active renderers now draw.
        static void RequestActivated(GameObject go)
        {
            if (_deferred.Count == 0 || !go.activeInHierarchy) return;
            foreach (var r in go.GetComponentsInChildren<Renderer>(false))
            {
                if (!r.enabled) continue;
                foreach (var m in r.sharedMaterials)
                    if (m != null && _deferred.Remove(m.GetInstanceID())) Request(m);
            }
        }

        // ------------------------------------------------------------------------------------------
        //  Tracking
        // ------------------------------------------------------------------------------------------

        static void Track(Material m)
        {
            int id = m.GetInstanceID();
            var paths = new List<string>();
            foreach (var prop in SpecularExMaskPacker.AllSourceProps) AddSlotPath(m, prop, paths);
            foreach (var pack in SpecularExMaskPacker.Packs) AddSlotPath(m, pack.prop, paths);
            string self = AssetDatabase.GetAssetPath(m);
            if (string.IsNullOrEmpty(self)) self = null;
            else paths.Add(self);

            RemovePaths(id);
            _tracked[id] = new Tracked { material = m, self = self, paths = paths.ToArray() };
            foreach (var p in paths)
            {
                if (!_byPath.TryGetValue(p, out var ids)) _byPath[p] = ids = new HashSet<int>();
                ids.Add(id);
            }
        }

        static void AddSlotPath(Material m, string prop, List<string> paths)
        {
            var t = m.HasProperty(prop) ? m.GetTexture(prop) : null;
            string path = t != null ? AssetDatabase.GetAssetPath(t) : null;
            if (!string.IsNullOrEmpty(path)) paths.Add(path);
        }

        static void Untrack(int id)
        {
            RemovePaths(id);
            _tracked.Remove(id);
            _slotState.Remove(id);
            _deferred.Remove(id);
            SpecularExPackedMaskStore.InvalidateVerified(id);
        }

        static void RemovePaths(int id)
        {
            if (!_tracked.TryGetValue(id, out var t)) return;
            foreach (var p in t.paths)
            {
                if (!_byPath.TryGetValue(p, out var ids)) continue;
                ids.Remove(id);
                if (ids.Count == 0) _byPath.Remove(p);
            }
        }

        // Queues the tracked materials that referenced `path`. inputsOnly: ignore materials whose own
        // file it is.
        static bool MarkDirty(string path, bool inputsOnly = false)
        {
            if (!_byPath.TryGetValue(path, out var ids)) return false;
            bool queued = false;
            foreach (int id in ids)
            {
                bool own = string.Equals(_tracked[id].self, path, System.StringComparison.OrdinalIgnoreCase);
                if (own && inputsOnly) continue;
                (own ? _dirtyContainers : _dirtyInputs).Add(id);
                queued = true;
            }
            return queued;
        }

        // Generated files are not queued on import (every write of ours imports one), but a changed
        // one invalidates the cached checks of the materials that referenced it.
        static void InvalidateVerifiedUsing(string path)
        {
            if (!_byPath.TryGetValue(path, out var ids)) return;
            foreach (int id in ids) SpecularExPackedMaskStore.InvalidateVerified(id);
        }

        static void MoveTrackedPath(string from, string to)
        {
            if (!_byPath.TryGetValue(from, out var ids)) return;
            _byPath.Remove(from);
            if (!_byPath.TryGetValue(to, out var target)) _byPath[to] = target = new HashSet<int>();
            target.UnionWith(ids);
            foreach (int id in ids)
            {
                var t = _tracked[id];
                if (string.Equals(t.self, from, System.StringComparison.OrdinalIgnoreCase)) t.self = to;
                for (int i = 0; i < t.paths.Length; i++)
                    if (string.Equals(t.paths[i], from, System.StringComparison.OrdinalIgnoreCase)) t.paths[i] = to;
            }
        }

        // ------------------------------------------------------------------------------------------
        //  Manual repair
        // ------------------------------------------------------------------------------------------

        // Checks `materials` now, trusting no cached result for them; they are tracked afterwards.
        //   rebake: regenerate the files even if they exist (slow: every file is recompressed).
        public static bool Repair(ICollection<Material> materials, bool rebake)
        {
            bool ok;
            if (rebake)
            {
                ok = SpecularExPackedMaskStore.EnsureAll(materials, persist: true, rebake: true);
            }
            else
            {
                // Import settings stay cached: every import, deletion or move of a generated file
                // invalidates them. The verified results of these materials do not.
                foreach (var m in materials) SpecularExPackedMaskStore.InvalidateVerified(m);
                ok = SpecularExPackedMaskStore.EnsureAll(materials, persist: true, useCache: true);
            }
            NoteChecked(materials);
            return ok;
        }

        static void LogRepair(int count, bool ok, bool rebake)
            => Debug.Log($"[SpecularExV2] {(rebake ? "Rebaked" : "Repaired")} the packed masks of {count} material(s)" +
                         (ok ? "." : "; some failed, see the errors above."));

        [MenuItem("Window/SpecularExV2/Packed Masks/Repair Scene Materials")]
        static void RepairScene()
        {
            var found = new Dictionary<Material, bool>();
            for (int s = 0; s < SceneManager.sceneCount; s++)
            {
                var scene = SceneManager.GetSceneAt(s);
                if (!scene.isLoaded) continue;
                foreach (var root in scene.GetRootGameObjects())
                    CollectRendererMaterials(root, found);
            }
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage != null && stage.prefabContentsRoot != null)
                CollectRendererMaterials(stage.prefabContentsRoot, found);
            var materials = new List<Material>(found.Keys);
            LogRepair(materials.Count, Repair(materials, rebake: false), rebake: false);
        }

        // Hierarchy context menu. Invoked once per selected object; only the call for the active one
        // runs, and it handles the whole selection.
        [MenuItem("GameObject/SpecularExV2/Repair Packed Masks", false, 49)]
        static void RepairSelectedObjects(MenuCommand command)
        {
            if (command.context != null && command.context != Selection.activeGameObject) return;
            var materials = new HashSet<Material>();
            foreach (var go in Selection.gameObjects) CollectObjectMaterials(go, materials);
            var list = new List<Material>(materials);
            LogRepair(list.Count, Repair(list, rebake: false), rebake: false);
        }

        [MenuItem("GameObject/SpecularExV2/Repair Packed Masks", true)]
        static bool RepairSelectedObjectsValidate() => Selection.gameObjects.Length > 0;

        // Renderers (inactive included) and the clips of their Animators, so materials swapped in by
        // animation are repaired too. VRChat's playable layers are covered by the build hook.
        static void CollectObjectMaterials(GameObject root, HashSet<Material> materials)
        {
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                foreach (var m in r.sharedMaterials)
                    if (SpecularExMaskPacker.HasPackedSlot(m)) materials.Add(m);

            foreach (var animator in root.GetComponentsInChildren<Animator>(true))
            {
                var controller = animator.runtimeAnimatorController;
                if (controller == null) continue;
                foreach (var clip in controller.animationClips)
                {
                    if (clip == null) continue;
                    foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                        foreach (var key in AnimationUtility.GetObjectReferenceCurve(clip, binding))
                            if (key.value is Material m && SpecularExMaskPacker.HasPackedSlot(m)) materials.Add(m);
                }
            }
        }

        [MenuItem("Assets/SpecularExV2/Rebake Packed Masks")]
        static void RebakeSelectedAssets()
        {
            var materials = SelectedMaterialAssets();
            LogRepair(materials.Count, Repair(materials, rebake: true), rebake: true);
        }

        [MenuItem("Assets/SpecularExV2/Rebake Packed Masks", true)]
        static bool RebakeSelectedAssetsValidate() => SelectedMaterialAssets().Count > 0;

        static List<Material> SelectedMaterialAssets()
        {
            var result = new List<Material>();
            foreach (var m in Selection.GetFiltered<Material>(SelectionMode.Assets))
                if (SpecularExMaskPacker.HasPackedSlot(m)) result.Add(m);
            return result;
        }

        // ------------------------------------------------------------------------------------------
        //  Events
        // ------------------------------------------------------------------------------------------

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
                        // Renderers: material swaps and enabling. GameObjects: activation. Everything else
                        // (transforms etc.) is ignored to stay cheap. BlendShape drags publish one event
                        // per frame, so unchanged materials are skipped.
                        stream.GetChangeGameObjectOrComponentPropertiesEvent(i, out var data);
                        var o = EditorUtility.InstanceIDToObject(data.instanceId);
                        if (o is Renderer r)
                        {
                            foreach (var m in r.sharedMaterials)
                                RequestIfSlotsChanged(m);
                        }
                        else if (o is GameObject go)
                        {
                            RequestActivated(go);
                        }
                        break;
                    }
                }
            }
        }

        // Only the static OnPostprocessAllAssets callback. Do NOT add per-type callbacks such as
        // OnPreprocessTexture or override GetVersion: those become an import dependency of every asset of
        // that type, so installing or updating SpecularExV2 would reimport the whole project's textures.
        // Generated files get their import settings from SpecularExPackedMaskStore instead.
        class Postprocessor : AssetPostprocessor
        {
            static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
            {
                SpecularExPackedMaskStore.NoteAssetChange();
                bool queued = false;
                foreach (var path in imported)
                {
                    if (SpecularExPackedMaskStore.IsGeneratedPath(path))
                    {
                        // Our own writes and manual edits alike: re-verify its import settings next time.
                        SpecularExPackedMaskStore.InvalidateImportSettings(path);
                        InvalidateVerifiedUsing(path);
                        continue;
                    }
                    queued |= MarkDirty(path);
                }
                // Moves keep the GUID and contents, so they do not change any packed mask. They can
                // still move a generated file (the expected path no longer matches) or the shaders,
                // which the cached lookups depend on, and the recorded paths are renamed right away.
                if (deleted.Length > 0 || moved.Length > 0) SpecularExPackedMaskStore.InvalidateShaderFolder();
                for (int i = 0; i < moved.Length; i++)
                {
                    SpecularExPackedMaskStore.InvalidateImportSettings(moved[i]);
                    if (i >= movedFrom.Length) continue;
                    SpecularExPackedMaskStore.InvalidateImportSettings(movedFrom[i]);
                    if (SpecularExPackedMaskStore.IsGeneratedPath(movedFrom[i])) queued |= MarkDirty(movedFrom[i]);
                    MoveTrackedPath(movedFrom[i], moved[i]);
                }
                foreach (var path in deleted)
                {
                    SpecularExPackedMaskStore.InvalidateImportSettings(path);
                    // A deleted material file destroys the material; nothing to check.
                    queued |= MarkDirty(path, inputsOnly: true);
                }
                if (queued) Schedule();
            }
        }
    }
}
#endif
