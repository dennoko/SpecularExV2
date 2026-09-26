#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using System.Text;
using Unity.Profiling;
using UnityEditor;
using UnityEngine;

namespace Dennokoworks.SpecularExV2
{
    // Persists packed masks as ordinary PNG assets and keeps each material's _CustomMaskPacked pointing
    // at the one matching its current mask slots.
    //
    // Model:
    //   * The individual mask slots are the source of truth; the packed PNG is derived data.
    //   * Files are content-addressed and immutable: the name is a hash of the inputs (slot texture
    //     GUIDs + their import dependency hashes + packer version). Changing a slot or a source image
    //     yields a different file, so an existing file is never overwritten under another material —
    //     duplicated materials, Undo and shared inputs all stay consistent by construction.
    //   * EnsureAll is idempotent: when the assigned texture already is the expected file it writes
    //     nothing. Every trigger (inspector, asset changes, build) just calls it, and repeated calls
    //     converge after at most one write, so no loop guard or retry state is needed.
    //   * Compression, mipmaps and streaming come from the TextureImporter, identically in the editor
    //     preview and in uploads. The settings are written to the generated files' own importers
    //     (EnsureImportSettings), NOT by an AssetPostprocessor.OnPreprocessTexture: registering a
    //     texture preprocessor changes the import dependency of every texture, so installing or
    //     updating the extension would reimport all textures of the project.
    public static class SpecularExPackedMaskStore
    {
        public const string Folder = "Assets/dennokoworks/SpecularExV2_Generated/PackedMasks";

        // GUID of Shaders/lts.lilcontainer; its folder holds every shader SpecularExV2 materials can use.
        const string ShaderAssetGuid = "987a9d362921499fb358b61879bcab6a";

        public static bool IsGeneratedPath(string path)
            => !string.IsNullOrEmpty(path)
               && path.Replace('\\', '/').StartsWith(Folder + "/", System.StringComparison.OrdinalIgnoreCase);

        // Makes every SpecularExV2 material in `materials` reference the packed mask matching its slots
        // (or nothing, when all slots are empty). Other materials are ignored.
        //   persist: save changed .mat assets right away, so the reference survives reloads/reimports.
        //            Builds pass false to avoid saving (and reimporting) assets mid-build.
        //   rebake:  regenerate the files even if they exist (manual repair).
        //   useCache: skip re-checking what this editor session already verified: import settings of
        //            generated files, and materials whose last check fully succeeded and whose slot
        //            references did not change since (see Verified). Only the watcher passes true;
        //            builds, manual repair and menu commands always verify everything. Ignored when
        //            rebake is set.
        // Returns false if any material could not be brought up to date; the reason is logged and the
        // material keeps its previous packed texture.
        struct MaterialPackPlan
        {
            public Material material;
            public int packIndex;
            public string path;
        }

        // What the last EnsureAll call did; logged by the watcher when debug timing is enabled.
        public struct EnsureStats
        {
            public int materials;  // SpecularExV2 materials examined
            public int cached;     // of those, skipped as verified earlier
            public int written;    // PNG files baked and written
            public int reimported; // generated files reimported for their import settings
            public int assigned;   // materials whose packed reference changed
        }

        public static EnsureStats LastStats;

        static readonly ProfilerMarker EnsureAllMarker = new ProfilerMarker("SpecularExV2.EnsureAll");
        static readonly ProfilerMarker PlanMarker = new ProfilerMarker("SpecularExV2.EnsureAll.Plan");
        static readonly ProfilerMarker ImportSettingsMarker = new ProfilerMarker("SpecularExV2.EnsureAll.ImportSettings");
        static readonly ProfilerMarker AssignMarker = new ProfilerMarker("SpecularExV2.EnsureAll.Assign");
        static readonly ProfilerMarker GetStateMarker = new ProfilerMarker("SpecularExV2.GetState");

        public static bool EnsureAll(IEnumerable<Material> materials, bool persist, bool rebake = false, bool useCache = false)
        {
            LastStats = default;
            using (EnsureAllMarker.Auto())
                return EnsureAllCore(materials, persist, rebake, useCache && !rebake);
        }

        static bool EnsureAllCore(IEnumerable<Material> materials, bool persist, bool rebake, bool useCache)
        {
            var plans = new List<MaterialPackPlan>();
            var seen = new HashSet<Material>();
            var written = new HashSet<string>();
            // Source path -> dependency hash, shared by every material of this call: many materials
            // (variants, clones in a build) usually use the same few mask images.
            var depHashes = new Dictionary<string, Hash128>();
            // Checked materials that must not be recorded as verified: failures, and inputs that are
            // not saved assets (their contents can change without an import event).
            var uncacheable = new HashSet<Material>();
            bool ok = true;
            bool editing = false;

            // Pass 1: decide each material's file and create missing ones. Imports are batched.
            using (PlanMarker.Auto())
            try
            {
                foreach (var m in materials)
                {
                    if (!SpecularExMaskPacker.HasPackedSlot(m) || !seen.Add(m)) continue;
                    LastStats.materials++;
                    int id = m.GetInstanceID();
                    if (useCache && _verified.TryGetValue(id, out var verified) && SnapshotEquals(verified, SlotSnapshot(m)))
                    {
                        LastStats.cached++;
                        continue;
                    }
                    _verified.Remove(id);

                    for (int p = 0; p < SpecularExMaskPacker.Packs.Length; p++)
                    {
                        string prop = SpecularExMaskPacker.Packs[p].prop;
                        if (!m.HasProperty(prop)) continue;

                        if (!SpecularExMaskPacker.NeedsPacking(m, p))
                        {
                            // A build clone whose slots were stripped keeps whatever was packed (or dropped)
                            // before stripping; there is nothing left to pack it from.
                            if (AreSourcesStripped(m)) { uncacheable.Add(m); continue; }
                            plans.Add(new MaterialPackPlan { material = m, packIndex = p, path = null });
                            continue;
                        }

                        byte[] png = null;
                        string key = InputKey(m, p, depHashes);
                        if (key == null)
                        {
                            // A slot holds a texture that is not a saved asset (e.g. generated by another tool
                            // during the build), so the inputs cannot be identified. Bake, and name the file
                            // by its contents instead, which still de-duplicates repeated builds.
                            uncacheable.Add(m);
                            png = SpecularExMaskPacker.BakePng(m, p);
                            if (png == null) { ok = false; LogFailure(m); continue; }
                            key = "o" + Hash128.Compute(png);
                        }

                        string path = Folder + "/" + key + ".png";
                        if ((rebake || !File.Exists(path)) && written.Add(path))
                        {
                            if (png == null) png = SpecularExMaskPacker.BakePng(m, p);
                            if (png == null) { ok = false; uncacheable.Add(m); LogFailure(m); continue; }
                            if (!editing)
                            {
                                EnsureFolder();
                                AssetDatabase.StartAssetEditing();
                                editing = true;
                            }
                            File.WriteAllBytes(path, png);
                            AssetDatabase.ImportAsset(path);
                            LastStats.written++;
                        }
                        plans.Add(new MaterialPackPlan { material = m, packIndex = p, path = path });
                    }
                }
            }
            finally
            {
                if (editing) AssetDatabase.StopAssetEditing();
            }

            var paths = new HashSet<string>();
            foreach (var plan in plans)
                if (plan.path != null) paths.Add(plan.path);
            HashSet<string> unconfirmed;
            using (ImportSettingsMarker.Auto())
                unconfirmed = EnsureImportSettings(paths, written, useCache);
            foreach (var plan in plans)
                if (plan.path != null && unconfirmed.Contains(plan.path)) uncacheable.Add(plan.material);

            // Pass 2: assign. Only materials whose reference actually changes are touched.
            var changedMaterials = new HashSet<Material>();
            using (AssignMarker.Auto())
            foreach (var plan in plans)
            {
                var m = plan.material;
                int p = plan.packIndex;
                string prop = SpecularExMaskPacker.Packs[p].prop;
                Texture2D tex = null;
                if (plan.path != null)
                {
                    tex = AssetDatabase.LoadAssetAtPath<Texture2D>(plan.path);
                    if (tex == null)
                    {
                        AssetDatabase.ImportAsset(plan.path, ImportAssetOptions.ForceSynchronousImport);
                        tex = AssetDatabase.LoadAssetAtPath<Texture2D>(plan.path);
                    }
                    if (tex == null)
                    {
                        ok = false;
                        uncacheable.Add(m);
                        Debug.LogError($"[SpecularExV2] Packed mask '{plan.path}' for material '{m.name}' could not be loaded.", m);
                        continue;
                    }
                }

                if (m.GetTexture(prop) == tex) continue;
                m.SetTexture(prop, tex);
                EditorUtility.SetDirty(m);
                changedMaterials.Add(m);
            }
            LastStats.assigned = changedMaterials.Count;

            if (persist)
            {
                foreach (var m in changedMaterials)
                {
                    if (!CanSave(m)) continue;
                    // The save reimports the .mat; tell the watcher it is ours so it is not re-checked.
                    SpecularExPackedMaskWatcher.NoteSelfSave(m);
                    try { AssetDatabase.SaveAssetIfDirty(m); }
                    catch { SpecularExPackedMaskWatcher.ForgetSelfSave(m); throw; }
                }
            }

            // Recorded with the references as assigned now. A material with a pack failure keeps none
            // (removed above) and is checked again on the next request.
            if (useCache)
                foreach (var m in seen)
                    if (!uncacheable.Contains(m)) _verified[m.GetInstanceID()] = SlotSnapshot(m);
            return ok;
        }

        // ------------------------------------------------------------------------------------------
        //  Verified materials (editor-only shortcut, useCache)
        // ------------------------------------------------------------------------------------------
        // Material instance ID -> slot snapshot after its last fully successful watcher check. Equal
        // references give the same result unless a referenced texture was reimported or deleted; the
        // watcher invalidates those materials (index and tracking lookups) before checking them.
        // Instance IDs are not trusted across domain reloads: the dictionary starts empty.
        static readonly Dictionary<int, int[]> _verified = new Dictionary<int, int[]>();

        public static void InvalidateVerified(Material m)
        {
            if (m != null) _verified.Remove(m.GetInstanceID());
        }

        // Forgets every cached check (verified materials and import settings).
        public static void InvalidateAllCaches()
        {
            _verified.Clear();
            _verifiedImportSettings.Clear();
        }

        // ------------------------------------------------------------------------------------------
        //  Build-time stripping of the source slots
        // ------------------------------------------------------------------------------------------

        // Material tag set on build clones whose source mask slots were cleared (NDMF plugin). A tag is
        // serialized with the material, so it survives NDMF's asset saving, a manual bake and domain
        // reloads, and needs no shader property.
        public const string SourcesStrippedTag = "SpecularExV2MaskSourcesStripped";

        public static bool AreSourcesStripped(Material m)
            => m != null && m.GetTag(SourcesStrippedTag, false, "") == "1";

        // Clears every authoring-only mask slot so the build no longer depends on the source images, and
        // marks the material so EnsureAll keeps its packed masks instead of clearing them. Only for build
        // clones: the packed masks must already be assigned (see ArePacksAssigned). SetTexture(null) keeps
        // the slot's tiling/offset, which the shader still uses to sample the packed channels.
        public static void StripSources(Material m)
        {
            foreach (var prop in SpecularExMaskPacker.AllSourceProps)
                if (m.HasProperty(prop)) m.SetTexture(prop, null);
            m.SetOverrideTag(SourcesStrippedTag, "1");
        }

        // Every pack that has inputs references a generated texture.
        public static bool ArePacksAssigned(Material m)
        {
            for (int p = 0; p < SpecularExMaskPacker.Packs.Length; p++)
            {
                string prop = SpecularExMaskPacker.Packs[p].prop;
                if (!m.HasProperty(prop) || !SpecularExMaskPacker.NeedsPacking(m, p)) continue;
                if (m.GetTexture(prop) == null) return false;
            }
            return true;
        }

        public enum PackState
        {
            NoMasks,     // every slot empty: the packed slot is (or will be) empty -> white default
            UpToDate,    // all active packs are up to date
            Pending,     // inputs changed or a file is missing; the watcher will (re)bake it
            Unsaved,     // a slot texture is not a saved asset; baked by content at edit/build time
        }

        // Bumped by the watcher's postprocessor on every asset change batch. GetState results depend on
        // source dependency hashes and generated files on disk, which only change through imports,
        // deletions and moves.
        public static int AssetGeneration { get; private set; }

        public static void NoteAssetChange() => AssetGeneration++;

        struct CachedState
        {
            public int[] snapshot;
            public int shader;
            public int generation;
            public PackState state;
            public string fingerprint;
        }

        static readonly Dictionary<int, CachedState> _stateCache = new Dictionary<int, CachedState>();

        // Read-only status for the inspector. Cheap enough for OnGUI (no baking, no imports): the result
        // is cached per material until its slot references, its shader or any asset changes.
        //   fingerprint: the input fingerprint(s) (file name without extension), or null.
        public static PackState GetState(Material m, out string fingerprint)
        {
            using var _ = GetStateMarker.Auto();
            if (m == null) { fingerprint = null; return PackState.NoMasks; }
            var snapshot = SlotSnapshot(m);
            int shader = m.shader != null ? m.shader.GetInstanceID() : 0;
            int id = m.GetInstanceID();
            if (_stateCache.TryGetValue(id, out var c) && c.generation == AssetGeneration && c.shader == shader
                && SnapshotEquals(c.snapshot, snapshot))
            {
                fingerprint = c.fingerprint;
                return c.state;
            }
            var state = ComputeState(m, out fingerprint);
            _stateCache[id] = new CachedState
            {
                snapshot = snapshot, shader = shader, generation = AssetGeneration, state = state, fingerprint = fingerprint,
            };
            return state;
        }

        static PackState ComputeState(Material m, out string fingerprint)
        {
            fingerprint = null;
            if (!SpecularExMaskPacker.HasPackedSlot(m) || !SpecularExMaskPacker.NeedsPacking(m))
                return PackState.NoMasks;

            var states = new List<PackState>();
            var fps = new List<string>();

            for (int p = 0; p < SpecularExMaskPacker.Packs.Length; p++)
            {
                if (!SpecularExMaskPacker.NeedsPacking(m, p)) continue;
                string prop = SpecularExMaskPacker.Packs[p].prop;
                if (!m.HasProperty(prop)) continue;

                string fp = InputKey(m, p);
                var assigned = m.GetTexture(prop);
                if (fp == null)
                {
                    string assignedPath = assigned != null ? AssetDatabase.GetAssetPath(assigned) : null;
                    if (IsGeneratedPath(assignedPath)) fp = Path.GetFileNameWithoutExtension(assignedPath);
                    states.Add(PackState.Unsaved);
                    if (fp != null) fps.Add(fp);
                    continue;
                }

                fps.Add(fp);
                string expected = Folder + "/" + fp + ".png";
                if (assigned != null && AssetDatabase.GetAssetPath(assigned) == expected && File.Exists(expected))
                    states.Add(PackState.UpToDate);
                else
                    states.Add(PackState.Pending);
            }

            fingerprint = fps.Count > 0 ? string.Join(", ", fps) : null;
            if (states.Contains(PackState.Pending)) return PackState.Pending;
            if (states.Contains(PackState.Unsaved)) return PackState.Unsaved;
            return states.Count > 0 ? PackState.UpToDate : PackState.NoMasks;
        }

        // Instance IDs of every source slot and packed slot (0 = empty or missing). Two equal snapshots
        // mean the material references the same textures, so a packed-mask check would reach the same
        // result unless one of those textures was reimported or deleted.
        public static int[] SlotSnapshot(Material m)
        {
            var sources = SpecularExMaskPacker.AllSourceProps;
            var packs = SpecularExMaskPacker.Packs;
            var ids = new int[sources.Length + packs.Length];
            for (int i = 0; i < sources.Length; i++) ids[i] = TextureId(m, sources[i]);
            for (int p = 0; p < packs.Length; p++) ids[sources.Length + p] = TextureId(m, packs[p].prop);
            return ids;
        }

        public static bool SnapshotEquals(int[] a, int[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return false;
            return true;
        }

        static int TextureId(Material m, string prop)
        {
            var t = m.HasProperty(prop) ? m.GetTexture(prop) : null;
            return t != null ? t.GetInstanceID() : 0;
        }

        // Hash of everything the baked pixels depend on, or null if a slot texture is not a saved asset.
        // Deliberately not the material's own dependency hash: that includes the packed texture itself.
        //   depHashes: optional cache of AssetDatabase.GetAssetDependencyHash per source path.
        static string InputKey(Material m, int packIndex = 0, Dictionary<string, Hash128> depHashes = null)
        {
            var sb = new StringBuilder("v").Append(SpecularExMaskPacker.Version);
            if (packIndex > 0) sb.Append(";p").Append(packIndex);
            sb.Append(';');
            var slots = SpecularExMaskPacker.Packs[packIndex].slots;
            for (int i = 0; i < slots.Length; i++)
            {
                if (slots[i] == null) { sb.Append("_;"); continue; }
                var t = m.HasProperty(slots[i]) ? m.GetTexture(slots[i]) : null;
                if (t == null) { sb.Append("_;"); continue; }
                if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(t, out string guid, out long localId)) return null;
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path)) return null;
                // The dependency hash covers the source file and its import settings (sRGB, max size...).
                if (depHashes == null || !depHashes.TryGetValue(path, out var depHash))
                {
                    depHash = AssetDatabase.GetAssetDependencyHash(path);
                    if (depHashes != null) depHashes[path] = depHash;
                }
                sb.Append(guid).Append(':').Append(localId).Append(':').Append(depHash).Append(';');
            }
            return "i" + Hash128.Compute(sb.ToString());
        }

        // Only standalone, writable .mat files are saved. Materials embedded in models, in immutable
        // packages, or created in memory still get the reference, but only for this session/build.
        static bool CanSave(Material m)
            => EditorUtility.IsPersistent(m)
               && AssetDatabase.GetAssetPath(m).EndsWith(".mat", System.StringComparison.OrdinalIgnoreCase)
               && AssetDatabase.IsOpenForEdit(m);

        static void EnsureFolder()
        {
            string current = null;
            foreach (var part in Folder.Split('/'))
            {
                string next = current == null ? part : current + "/" + part;
                if (current != null && !AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(current, part);
                current = next;
            }
        }

        static void LogFailure(Material m)
            => Debug.LogError($"[SpecularExV2] Could not pack the masks of material '{m.name}'. Its packed mask was left unchanged.", m);

        // ------------------------------------------------------------------------------------------
        //  Import settings of the generated files
        // ------------------------------------------------------------------------------------------

        const int ImportMaxSize = 2048;

        static readonly (string platform, TextureImporterFormat format)[] PlatformFormats =
        {
            ("Standalone", TextureImporterFormat.BC7),
            ("Android", TextureImporterFormat.ASTC_6x6),
            ("iPhone", TextureImporterFormat.ASTC_6x6),
        };

        // Generated files whose importer was confirmed to match during this domain. Any import, deletion
        // or move of a generated file removes its entry (InvalidateImportSettings, from the watcher's
        // postprocessor), so a manual change of the settings is repaired on the next check.
        static readonly HashSet<string> _verifiedImportSettings = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);

        public static void InvalidateImportSettings(string path)
        {
            if (!string.IsNullOrEmpty(path)) _verifiedImportSettings.Remove(path.Replace('\\', '/'));
        }

        // Reimports only the files whose importer differs, so it is a no-op once the settings are in
        // the .meta. A new file is imported once with defaults first and once more here.
        //   written:  files written by this call; always checked.
        //   useCache: skip files already verified in this domain.
        // Returns the paths whose settings could not be confirmed (no importer, or still different
        // after the reimport).
        static HashSet<string> EnsureImportSettings(IEnumerable<string> paths, HashSet<string> written, bool useCache)
        {
            var failed = new HashSet<string>();
            var matched = new List<string>();
            var reimported = new List<string>();
            bool editing = false;
            try
            {
                foreach (var path in paths)
                {
                    if (useCache && !written.Contains(path) && _verifiedImportSettings.Contains(path)) continue;
                    if (!(AssetImporter.GetAtPath(path) is TextureImporter ti)) { failed.Add(path); continue; }
                    if (!ApplyImportSettings(ti, apply: true)) { matched.Add(path); continue; }
                    if (!editing)
                    {
                        AssetDatabase.StartAssetEditing();
                        editing = true;
                    }
                    ti.SaveAndReimport();
                    reimported.Add(path);
                    LastStats.reimported++;
                }
            }
            finally
            {
                if (editing) AssetDatabase.StopAssetEditing();
            }

            // Recorded only after the imports ran: the postprocessor invalidates the reimported paths.
            foreach (var path in reimported)
            {
                if (AssetImporter.GetAtPath(path) is TextureImporter ti && !ApplyImportSettings(ti, apply: false))
                    matched.Add(path);
                else
                    failed.Add(path);
            }
            foreach (var path in matched) _verifiedImportSettings.Add(path);
            return failed;
        }

        // The packed channels are four unrelated linear masks:
        //   * sRGB off and alpha taken as-is, so values match what the individual slots produced.
        //   * BC7 on PC rather than DXT5: DXT5 fits RGB to one line per 4x4 block, bleeding the
        //     independent R/G/B masks into each other. ASTC on mobile, which has no BC7.
        //   * Mipmaps + streaming for VRChat's texture memory budget; no CPU copy.
        // Returns true if anything differs. apply: false only compares and leaves the importer untouched.
        static bool ApplyImportSettings(TextureImporter ti, bool apply)
        {
            bool changed = false;
            void Set<T>(T current, T target, System.Action<T> set)
            {
                if (EqualityComparer<T>.Default.Equals(current, target)) return;
                if (apply) set(target);
                changed = true;
            }

            Set(ti.textureType, TextureImporterType.Default, v => ti.textureType = v);
            Set(ti.textureShape, TextureImporterShape.Texture2D, v => ti.textureShape = v);
            Set(ti.sRGBTexture, false, v => ti.sRGBTexture = v);
            Set(ti.alphaSource, TextureImporterAlphaSource.FromInput, v => ti.alphaSource = v);
            Set(ti.alphaIsTransparency, false, v => ti.alphaIsTransparency = v);
            Set(ti.npotScale, TextureImporterNPOTScale.None, v => ti.npotScale = v);
            Set(ti.mipmapEnabled, true, v => ti.mipmapEnabled = v);
            Set(ti.streamingMipmaps, true, v => ti.streamingMipmaps = v);
            Set(ti.isReadable, false, v => ti.isReadable = v);
            Set(ti.wrapMode, TextureWrapMode.Repeat, v => ti.wrapMode = v);
            Set(ti.filterMode, FilterMode.Bilinear, v => ti.filterMode = v);
            Set(ti.maxTextureSize, ImportMaxSize, v => ti.maxTextureSize = v);
            Set(ti.textureCompression, TextureImporterCompression.CompressedHQ, v => ti.textureCompression = v);

            foreach (var (platform, format) in PlatformFormats)
            {
                var s = ti.GetPlatformTextureSettings(platform);
                if (s.overridden && s.format == format && s.maxTextureSize == ImportMaxSize
                    && s.compressionQuality == (int)TextureCompressionQuality.Normal)
                    continue;
                changed = true;
                if (!apply) continue;
                s.overridden = true;
                s.format = format;
                s.maxTextureSize = ImportMaxSize;
                s.compressionQuality = (int)TextureCompressionQuality.Normal;
                ti.SetPlatformTextureSettings(s);
            }
            return changed;
        }

        // ------------------------------------------------------------------------------------------
        //  Project-wide maintenance
        // ------------------------------------------------------------------------------------------

        // Distinct asset paths containing at least one material. FindAssets returns GUIDs per file, and a
        // file (model, .asset) may hold several materials, so callers must use LoadMaterialsAtPath.
        public static List<string> FindMaterialContainerPaths()
        {
            var paths = new List<string>();
            var seen = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            foreach (var guid in AssetDatabase.FindAssets("t:Material"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!string.IsNullOrEmpty(path) && seen.Add(path)) paths.Add(path);
            }
            return paths;
        }

        // Folder of the SpecularExV2 shaders ("…/Shaders/"), or null if they cannot be located. Cached
        // (only when found); the watcher calls InvalidateShaderFolder when assets are moved or deleted.
        static string _shaderFolder;

        public static void InvalidateShaderFolder() => _shaderFolder = null;

        static string ShaderFolder()
        {
            if (_shaderFolder != null) return _shaderFolder;
            string path = AssetDatabase.GUIDToAssetPath(ShaderAssetGuid);
            if (string.IsNullOrEmpty(path)) path = AssetDatabase.GetAssetPath(Shader.Find(SpecularExMaskPacker.ShaderNameRoot + "/lilToon"));
            if (string.IsNullOrEmpty(path)) return null;
            return _shaderFolder = Path.GetDirectoryName(path).Replace('\\', '/') + "/";
        }

        // Whether a file whose direct dependencies are `dependencies` may hold a SpecularExV2 material.
        // Reads the dependency database only, so it is far cheaper than LoadAllAssetsAtPath on models
        // and large .asset files. Without a located shader folder nothing can be ruled out.
        public static bool UsesSpecularExShader(string[] dependencies)
        {
            string folder = ShaderFolder();
            if (folder == null) return true;
            foreach (var dep in dependencies)
                if (dep.StartsWith(folder, System.StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public static bool UsesSpecularExShader(string path)
            => UsesSpecularExShader(AssetDatabase.GetDependencies(path, false));

        // Every SpecularExV2 material stored in the file, main asset or sub-asset.
        public static List<Material> LoadMaterialsAtPath(string path)
        {
            var result = new List<Material>();
            if (string.IsNullOrEmpty(path)) return result;
            foreach (var o in AssetDatabase.LoadAllAssetsAtPath(path))
                if (o is Material m && SpecularExMaskPacker.HasPackedSlot(m)) result.Add(m);
            return result;
        }

        // Also rebuilds the watcher's dependency index from the same scan, and trusts no cached check.
        [MenuItem("Window/SpecularExV2/Packed Masks/Update All Materials")]
        static void UpdateAllMaterials()
        {
            var materials = new List<Material>();
            if (!SpecularExPackedMaskWatcher.RebuildIndex(materials, showProgress: true)) return;

            bool ok = EnsureAll(materials, persist: true);
            Debug.Log($"[SpecularExV2] Checked the packed masks of {materials.Count} material(s)" +
                      (ok ? "." : "; some failed, see the errors above."));
        }

        // Generated files are never deleted automatically: an older file may still be referenced by
        // an Undo step, an unsaved scene material or another project copy.
        [MenuItem("Window/SpecularExV2/Packed Masks/Delete Unused Generated Masks")]
        static void DeleteUnused()
        {
            if (!AssetDatabase.IsValidFolder(Folder)) return;

            var used = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            foreach (var guid in AssetDatabase.FindAssets("t:Material"))
                foreach (var dep in AssetDatabase.GetDependencies(AssetDatabase.GUIDToAssetPath(guid), false))
                    if (IsGeneratedPath(dep)) used.Add(dep);

            var unused = new List<string>();
            foreach (var guid in AssetDatabase.FindAssets("t:Texture2D", new[] { Folder }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!used.Contains(path)) unused.Add(path);
            }

            if (unused.Count == 0)
            {
                EditorUtility.DisplayDialog("SpecularExV2", "No unused packed masks were found.", "OK");
                return;
            }
            if (!EditorUtility.DisplayDialog("SpecularExV2",
                    $"Delete {unused.Count} packed mask file(s) not referenced by any saved material?\n" +
                    "Unsaved materials that still use them will regenerate them when edited or built.",
                    "Delete", "Cancel"))
                return;

            var failed = new List<string>();
            AssetDatabase.DeleteAssets(unused.ToArray(), failed);
            foreach (var f in failed) Debug.LogWarning($"[SpecularExV2] Could not delete '{f}'.");
        }
    }
}
#endif
