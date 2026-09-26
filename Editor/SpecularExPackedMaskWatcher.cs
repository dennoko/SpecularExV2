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
    // is processed once per editor update outside of imports, compilation and play-mode transitions.
    // There is no retry or backoff: EnsureAll is idempotent, and a failure is logged and waits for the
    // next real change (or the manual rebuild button). Triggers are kept narrow anyway, because a
    // no-op check still costs dependency hashes and file lookups per material.
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
    //   * Imported or deleted source textures and generated masks: saved materials that depend on them
    //     (found through the dependency index), and tracked materials that referenced them when last
    //     checked.
    //   * The VRChat avatar build hook calls EnsureAll directly (SpecularExPackedMaskBuildHook).
    //
    // Tracking: every material the watcher checks is remembered with the asset paths its slots
    // (sources and packed) referenced at that time. It is the only record of which materials used a
    // file once the file is deleted, and the only way to find materials that AssetDatabase searches
    // cannot: scene-embedded ones, script-created clones and unsaved edits of .mat files. The pending
    // queue and the tracking survive domain reloads through SessionState; if they are lost, the next
    // batch checks every material instead (full check).
    //
    // Dependency index: Unity cannot list the assets that use a texture, so the saved SpecularExV2
    // material files are indexed by their direct dependencies. It is built by the first texture event
    // (one project-wide dependency scan, as every event cost before), then updated from container
    // imports, deletions and moves. A deleted file is no longer anyone's dependency, so it can only be
    // looked up in an index that existed before the deletion; otherwise the batch is a full check.
    [InitializeOnLoad]
    public static class SpecularExPackedMaskWatcher
    {
        static readonly HashSet<Material> _materials = new HashSet<Material>();
        static readonly HashSet<string> _containerPaths = NewPathSet();
        static readonly HashSet<string> _texturePaths = NewPathSet();
        static readonly HashSet<string> _deletedPaths = NewPathSet(); // source textures and generated masks
        static readonly HashSet<string> _deletedContainers = NewPathSet();
        static bool _scanScenes;
        static bool _scheduled;

        // Why the next batch must check every material (lost state), or null.
        static string _fullCheckReason;

        static HashSet<string> NewPathSet() => new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);

        // Material instance ID -> its slot references (sources and packed) when last queued or checked.
        static readonly Dictionary<int, int[]> _slotState = new Dictionary<int, int[]>();

        // Instance ID -> a checked material and the asset paths its slots referenced then (see Tracking).
        class Tracked
        {
            public Material material;
            public string[] paths;
        }

        static readonly Dictionary<int, Tracked> _tracked = new Dictionary<int, Tracked>();

        // Dependency index (see above): SpecularExV2 container path -> its direct dependencies, and the
        // reverse. null until built.
        static Dictionary<string, string[]> _containerDeps;
        static Dictionary<string, HashSet<string>> _depContainers;
        static bool IndexBuilt => _containerDeps != null;

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
        static readonly ProfilerMarker IndexMarker = new ProfilerMarker("SpecularExV2.Watcher.BuildIndex");

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
            LoadState();
            AssemblyReloadEvents.beforeAssemblyReload += SaveState;
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
            ApplyRestoredState();
            int requested = _materials.Count, containers = _containerPaths.Count;
            int textures = _texturePaths.Count, deleted = _deletedPaths.Count;
            string fullCheck = _fullCheckReason;
            _fullCheckReason = null;
            if (fullCheck == null && _deletedPaths.Count > 0 && !IndexBuilt)
                fullCheck = "a file was deleted before the dependency index was built";
            long collectMs = 0, indexMs = -1;
            int ownSaves = 0;

            var targets = new List<Material>(_materials);
            _materials.Clear();
            // Everything from here up to the scene scan is affected by an asset change, so a cached
            // check of it cannot be trusted.
            int affectedFrom = targets.Count;

            // Affected materials are looked up before the index and the tracking are updated: after a
            // deletion, the state recorded before this batch is the only record of who used the file.
            using (AffectedMarker.Auto())
            {
                if (fullCheck != null)
                {
                    SpecularExPackedMaskStore.InvalidateAllCaches();
                    var t0 = System.Diagnostics.Stopwatch.StartNew();
                    RebuildIndex(targets, showProgress: false);
                    indexMs = t0.ElapsedMilliseconds;
                    CollectTrackedMaterials(targets, null);
                    _scanScenes = true;
                }
                else if (_texturePaths.Count > 0 || _deletedPaths.Count > 0)
                {
                    // Only imports can reach this unbuilt: current dependencies already include them.
                    if (!IndexBuilt)
                    {
                        var t0 = System.Diagnostics.Stopwatch.StartNew();
                        RebuildIndex(null, showProgress: false);
                        indexMs = t0.ElapsedMilliseconds;
                    }
                    var changed = NewPathSet();
                    changed.UnionWith(_texturePaths);
                    changed.UnionWith(_deletedPaths);
                    foreach (var path in IndexedContainers(changed))
                        targets.AddRange(SpecularExPackedMaskStore.LoadMaterialsAtPath(path));
                    CollectTrackedMaterials(targets, changed);
                }
            }
            _texturePaths.Clear();
            _deletedPaths.Clear();

            if (IndexBuilt)
                foreach (var path in _deletedContainers) RemoveIndexEntry(path);
            _deletedContainers.Clear();

            // A full project import reports every model and .asset here; loading them all is what
            // made first imports slow, so only files that reference a SpecularExV2 shader are loaded.
            // The index is updated for every one, including our own saves.
            foreach (var path in _containerPaths)
            {
                var deps = AssetDatabase.GetDependencies(path, false);
                bool uses = SpecularExPackedMaskStore.UsesSpecularExShader(deps);
                if (IndexBuilt)
                {
                    if (uses) SetIndexEntry(path, deps);
                    else RemoveIndexEntry(path);
                }
                if (!uses) { _selfSaved.Remove(path); continue; }
                var materials = SpecularExPackedMaskStore.LoadMaterialsAtPath(path);
                if (IsOwnSave(path, materials)) { ownSaves++; continue; }
                targets.AddRange(materials);
            }
            _containerPaths.Clear();
            PurgeExpiredSelfSaves();
            for (int i = affectedFrom; i < targets.Count; i++) SpecularExPackedMaskStore.InvalidateVerified(targets[i]);

            // Scene content is re-registered (tracked) on every scan, even when its check is cheap.
            bool scanned = _scanScenes;
            if (_scanScenes)
            {
                _scanScenes = false;
                using (SceneMarker.Auto())
                    CollectSceneMaterials(targets);
            }
            if (timer != null) collectMs = timer.ElapsedMilliseconds;

            // Verified materials are skipped cheaply; a full check has cleared that cache above.
            try { SpecularExPackedMaskStore.EnsureAll(targets, persist: true, useCache: true); }
            catch (System.Exception e) { Debug.LogException(e); }

            // The check may have assigned packed textures; record the result so the change events
            // caused by our own SetTexture do not queue the same materials again.
            foreach (var m in targets)
            {
                if (!SpecularExMaskPacker.HasPackedSlot(m)) continue;
                _slotState[m.GetInstanceID()] = SpecularExPackedMaskStore.SlotSnapshot(m);
                Track(m);
            }

            if (timer != null)
            {
                var st = SpecularExPackedMaskStore.LastStats;
                Debug.Log($"[SpecularExV2] Watcher: {timer.ElapsedMilliseconds} ms (collect {collectMs} ms) | " +
                          (fullCheck != null ? $"FULL CHECK ({fullCheck}) | " : "") +
                          (indexMs >= 0 ? $"index built in {indexMs} ms ({_containerDeps.Count} containers) | " : "") +
                          $"requested {requested}, containers {containers} (own saves {ownSaves}), textures {textures}, deleted {deleted}, scene scan {scanned} | " +
                          $"targets {targets.Count}, examined {st.materials} (cached {st.cached}), written {st.written}, reimported {st.reimported}, assigned {st.assigned}, tracked {_tracked.Count}");
            }
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

        // ------------------------------------------------------------------------------------------
        //  Dependency index
        // ------------------------------------------------------------------------------------------

        // Scans every material file of the project and replaces the index. load: receives every saved
        // SpecularExV2 material (null = do not load). With showProgress the scan can be cancelled;
        // it then returns false and keeps the previous index.
        public static bool RebuildIndex(List<Material> load, bool showProgress)
        {
            using var _ = IndexMarker.Auto();
            var containerDeps = new Dictionary<string, string[]>(System.StringComparer.OrdinalIgnoreCase);
            var loaded = new List<Material>();
            var paths = SpecularExPackedMaskStore.FindMaterialContainerPaths();
            try
            {
                for (int i = 0; i < paths.Count; i++)
                {
                    if (showProgress && EditorUtility.DisplayCancelableProgressBar("SpecularExV2", paths[i], (float)i / paths.Count))
                        return false;
                    var deps = AssetDatabase.GetDependencies(paths[i], false);
                    if (!SpecularExPackedMaskStore.UsesSpecularExShader(deps)) continue;
                    containerDeps[paths[i]] = deps;
                    if (load != null) loaded.AddRange(SpecularExPackedMaskStore.LoadMaterialsAtPath(paths[i]));
                }
            }
            finally
            {
                if (showProgress) EditorUtility.ClearProgressBar();
            }

            _containerDeps = new Dictionary<string, string[]>(System.StringComparer.OrdinalIgnoreCase);
            _depContainers = new Dictionary<string, HashSet<string>>(System.StringComparer.OrdinalIgnoreCase);
            foreach (var kv in containerDeps) SetIndexEntry(kv.Key, kv.Value);
            load?.AddRange(loaded);
            return true;
        }

        static void SetIndexEntry(string container, string[] deps)
        {
            RemoveIndexEntry(container);
            _containerDeps[container] = deps;
            foreach (var dep in deps)
            {
                if (!_depContainers.TryGetValue(dep, out var set))
                    _depContainers[dep] = set = NewPathSet();
                set.Add(container);
            }
        }

        static void RemoveIndexEntry(string container)
        {
            if (!_containerDeps.TryGetValue(container, out var deps)) return;
            _containerDeps.Remove(container);
            foreach (var dep in deps)
            {
                if (!_depContainers.TryGetValue(dep, out var set)) continue;
                set.Remove(container);
                if (set.Count == 0) _depContainers.Remove(dep);
            }
        }

        static HashSet<string> IndexedContainers(HashSet<string> deps)
        {
            var result = NewPathSet();
            foreach (var dep in deps)
                if (_depContainers.TryGetValue(dep, out var set)) result.UnionWith(set);
            return result;
        }

        // A moved container or dependency keeps its relations under the new path.
        static void MoveIndexPath(string from, string to)
        {
            if (!IndexBuilt) return;
            if (_containerDeps.TryGetValue(from, out var deps))
            {
                RemoveIndexEntry(from);
                SetIndexEntry(to, deps);
            }
            if (_depContainers.TryGetValue(from, out var containers))
            {
                foreach (var c in new List<string>(containers))
                {
                    var d = (string[])_containerDeps[c].Clone();
                    ReplacePath(d, from, to);
                    SetIndexEntry(c, d);
                }
            }
        }

        // ------------------------------------------------------------------------------------------
        //  Tracking
        // ------------------------------------------------------------------------------------------

        static void Track(Material m)
        {
            var paths = new List<string>();
            foreach (var prop in SpecularExMaskPacker.AllSourceProps) AddSlotPath(m, prop, paths);
            foreach (var pack in SpecularExMaskPacker.Packs) AddSlotPath(m, pack.prop, paths);
            _tracked[m.GetInstanceID()] = new Tracked { material = m, paths = paths.ToArray() };
        }

        static void AddSlotPath(Material m, string prop, List<string> paths)
        {
            var t = m.HasProperty(prop) ? m.GetTexture(prop) : null;
            string path = t != null ? AssetDatabase.GetAssetPath(t) : null;
            if (!string.IsNullOrEmpty(path)) paths.Add(path);
        }

        // Tracked materials that referenced one of `paths` when last checked (every one if null). Drops
        // destroyed materials and those no longer using a SpecularExV2 shader.
        static void CollectTrackedMaterials(List<Material> targets, HashSet<string> paths)
        {
            var dead = new List<int>();
            foreach (var kv in _tracked)
            {
                var m = kv.Value.material;
                if (!SpecularExMaskPacker.HasPackedSlot(m)) { dead.Add(kv.Key); continue; }
                if (paths == null) { targets.Add(m); continue; }
                foreach (var p in kv.Value.paths)
                {
                    if (!paths.Contains(p)) continue;
                    targets.Add(m);
                    break;
                }
            }
            foreach (var id in dead) _tracked.Remove(id);
        }

        // Generated files are not queued (they are derived data), but a changed one invalidates the
        // cached checks of the materials that referenced it.
        static void InvalidateVerifiedUsing(string path)
        {
            foreach (var t in _tracked.Values)
                foreach (var p in t.paths)
                {
                    if (!string.Equals(p, path, System.StringComparison.OrdinalIgnoreCase)) continue;
                    SpecularExPackedMaskStore.InvalidateVerified(t.material);
                    break;
                }
        }

        static void MoveTrackedPath(string from, string to)
        {
            foreach (var t in _tracked.Values) ReplacePath(t.paths, from, to);
            if (_restored?.tracked != null)
                foreach (var t in _restored.tracked) ReplacePath(t.paths, from, to);
        }

        static void ReplacePath(string[] paths, string from, string to)
        {
            if (paths == null) return;
            for (int i = 0; i < paths.Length; i++)
                if (string.Equals(paths[i], from, System.StringComparison.OrdinalIgnoreCase)) paths[i] = to;
        }

        // ------------------------------------------------------------------------------------------
        //  Domain reload
        // ------------------------------------------------------------------------------------------
        // Static fields are lost on every domain reload (script compile, entering play mode). A script
        // imported in the same refresh as a texture reloads the domain before Process runs, so the
        // pending queue and the tracking are saved right before the reload and restored afterwards.
        // The saved value is erased once read, so it is never applied twice.

        const string SessionKey = "SpecularExV2.Watcher.Session";
        const string StateKey = "SpecularExV2.Watcher.State";
        const int StateVersion = 1;
        const int MaxStateLength = 1 << 20; // characters

        [System.Serializable]
        class SavedState
        {
            public int version;
            public bool overflow;
            public string fullCheckReason;
            public bool scanScenes;
            public int[] materials;
            public string[] containers;
            public string[] textures;
            public string[] deleted;
            public string[] deletedContainers;
            public SavedTracked[] tracked;
            public bool hasIndex;
            public SavedContainer[] index;
        }

        [System.Serializable]
        class SavedContainer
        {
            public string path;
            public string[] deps;
        }

        [System.Serializable]
        class SavedTracked
        {
            public int id;
            public string[] paths;
        }

        // Instance IDs are resolved by the first Process, not in the static constructor, because
        // resolving can load assets.
        static SavedState _restored;

        static void SaveState()
        {
            var materials = new List<int>();
            foreach (var m in _materials)
                if (m != null) materials.Add(m.GetInstanceID());
            var tracked = new List<SavedTracked>();
            foreach (var kv in _tracked)
                if (kv.Value.material != null) tracked.Add(new SavedTracked { id = kv.Key, paths = kv.Value.paths });
            if (_restored?.tracked != null)
                foreach (var t in _restored.tracked)
                    if (!_tracked.ContainsKey(t.id)) tracked.Add(t);

            var state = new SavedState
            {
                version = StateVersion,
                fullCheckReason = _fullCheckReason,
                scanScenes = _scanScenes,
                materials = materials.ToArray(),
                containers = new List<string>(_containerPaths).ToArray(),
                textures = new List<string>(_texturePaths).ToArray(),
                deleted = new List<string>(_deletedPaths).ToArray(),
                deletedContainers = new List<string>(_deletedContainers).ToArray(),
                tracked = tracked.ToArray(),
            };
            if (IndexBuilt)
            {
                var index = new List<SavedContainer>();
                foreach (var kv in _containerDeps) index.Add(new SavedContainer { path = kv.Key, deps = kv.Value });
                state.hasIndex = true;
                state.index = index.ToArray();
            }
            string json = JsonUtility.ToJson(state);
            // Too large: drop the index first (it is rebuilt on demand; a deletion then falls back to
            // a full check), then everything (the next batch is a full check).
            if (json.Length > MaxStateLength && state.hasIndex)
            {
                state.hasIndex = false;
                state.index = null;
                json = JsonUtility.ToJson(state);
            }
            if (json.Length > MaxStateLength)
                json = JsonUtility.ToJson(new SavedState { version = StateVersion, overflow = true });
            SessionState.SetString(StateKey, json);
        }

        static void LoadState()
        {
            // SessionState is cleared when the editor quits, so on a fresh start nothing was pending.
            bool reload = SessionState.GetBool(SessionKey, false);
            SessionState.SetBool(SessionKey, true);
            string json = SessionState.GetString(StateKey, "");
            SessionState.EraseString(StateKey);
            if (!reload) return;

            SavedState state = null;
            try { if (json.Length > 0) state = JsonUtility.FromJson<SavedState>(json); }
            catch (System.Exception e) { Debug.LogException(e); }
            if (state == null || state.version != StateVersion || state.overflow)
            {
                _fullCheckReason = "watcher state was not preserved across the domain reload";
                return;
            }

            _restored = state;
            _fullCheckReason = state.fullCheckReason;
            _scanScenes |= state.scanScenes;
            if (state.containers != null) _containerPaths.UnionWith(state.containers);
            if (state.textures != null) _texturePaths.UnionWith(state.textures);
            if (state.deleted != null) _deletedPaths.UnionWith(state.deleted);
            if (state.deletedContainers != null) _deletedContainers.UnionWith(state.deletedContainers);
            if (state.hasIndex && state.index != null)
            {
                _containerDeps = new Dictionary<string, string[]>(System.StringComparer.OrdinalIgnoreCase);
                _depContainers = new Dictionary<string, HashSet<string>>(System.StringComparer.OrdinalIgnoreCase);
                foreach (var c in state.index)
                    if (!string.IsNullOrEmpty(c.path)) SetIndexEntry(c.path, c.deps ?? new string[0]);
            }
        }

        static void ApplyRestoredState()
        {
            var state = _restored;
            if (state == null) return;
            _restored = null;
            if (state.materials != null)
                foreach (int id in state.materials)
                    if (EditorUtility.InstanceIDToObject(id) is Material m) _materials.Add(m);
            if (state.tracked != null)
                foreach (var t in state.tracked)
                    if (!_tracked.ContainsKey(t.id) && EditorUtility.InstanceIDToObject(t.id) is Material m)
                        _tracked[t.id] = new Tracked { material = m, paths = t.paths ?? new string[0] };
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
                // still move a generated file or the shaders, which the cached lookups depend on, and
                // the recorded paths are renamed right away.
                if (deleted.Length > 0 || moved.Length > 0) SpecularExPackedMaskStore.InvalidateShaderFolder();
                for (int i = 0; i < moved.Length; i++)
                {
                    SpecularExPackedMaskStore.InvalidateImportSettings(moved[i]);
                    if (i < movedFrom.Length)
                    {
                        SpecularExPackedMaskStore.InvalidateImportSettings(movedFrom[i]);
                        InvalidateVerifiedUsing(movedFrom[i]);
                        MoveTrackedPath(movedFrom[i], moved[i]);
                        MoveIndexPath(movedFrom[i], moved[i]);
                    }
                }
                foreach (var path in deleted)
                {
                    SpecularExPackedMaskStore.InvalidateImportSettings(path);
                    // Removed from the index only after the affected lookup (Process).
                    if (MaterialContainerExtensions.Contains(Path.GetExtension(path)))
                    {
                        _deletedContainers.Add(path);
                        queued = true;
                    }
                    if (SpecularExPackedMaskStore.IsGeneratedPath(path)
                        || TextureExtensions.Contains(Path.GetExtension(path)))
                    {
                        _deletedPaths.Add(path);
                        queued = true;
                    }
                }
                if (queued) Schedule();
            }
        }
    }
}
#endif
