#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Dennokoworks.SpecularExV2
{
    // Migration of the removed MatCap "Back (-Z)" slot.
    //
    // The shader used to declare two matcap textures (front / back hemisphere). It now declares one: the
    // hemispheres sit side by side in _CustomMatcapFrontTex (left = front, right = back) and
    // _CustomMatcapLayout = 1 selects that layout. Materials saved before the change still carry the old
    // back texture in their serialized properties (Unity keeps properties the shader no longer declares),
    // which is how they are detected here.
    //
    // Migrate() bakes the old front + back pair into one PNG next to the packed masks and switches the
    // material to it. Like the packed masks, the files are content-addressed (named by a hash of the
    // inputs), so converting several materials that share the same pair produces one file.
    public static class SpecularExMatcapAtlasBaker
    {
        public const string Folder = "Assets/dennokoworks/SpecularExV2_Generated/MatcapAtlas";
        public const string TextureProp = "_CustomMatcapFrontTex";
        public const string LayoutProp = "_CustomMatcapLayout";
        public const string LegacyBackProp = "_CustomMatcapBackTex";
        public const string LegacyBackEnabledProp = "_CustomMatcapBackEnabled";

        // Bump when the produced pixels change for identical inputs.
        const int Version = 1;
        const string BlitShader = "Hidden/dennokoworks/SpecularExV2/MatcapAtlas";
        const int MaxHalfSize = 4096;

        // The texture left in the material's saved properties by the removed Back slot, or null.
        public static Texture GetLegacyBackTexture(Material m)
        {
            if (m == null || !SpecularExMaskPacker.IsSpecularEx(m)) return null;
            var so = new SerializedObject(m);
            var envs = so.FindProperty("m_SavedProperties.m_TexEnvs");
            if (envs == null) return null;
            for (int i = 0; i < envs.arraySize; i++)
            {
                var e = envs.GetArrayElementAtIndex(i);
                if (e.FindPropertyRelative("first").stringValue == LegacyBackProp)
                    return e.FindPropertyRelative("second.m_Texture").objectReferenceValue as Texture;
            }
            return null;
        }

        public static bool HasLegacyBackTexture(Material m) => GetLegacyBackTexture(m) != null;

        // Bakes the material's front + legacy back texture into a side-by-side atlas, assigns it with
        // layout 1 and removes the legacy entries. Returns false (and logs why) if nothing was changed.
        public static bool Migrate(Material m)
        {
            var back = GetLegacyBackTexture(m);
            if (back == null) return false;
            var front = m.HasProperty(TextureProp) ? m.GetTexture(TextureProp) : null;

            string path = EnsureAtlas(front, back);
            if (path == null)
            {
                Debug.LogError($"[SpecularExV2] Could not combine the MatCap front/back textures of material '{m.name}'.", m);
                return false;
            }
            var atlas = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (atlas == null) return false;

            Undo.RecordObject(m, "Combine SpecularExV2 MatCap Textures");
            m.SetTexture(TextureProp, atlas);
            m.SetFloat(LayoutProp, 1f);
            RemoveLegacyEntries(m);
            EditorUtility.SetDirty(m);
            if (EditorUtility.IsPersistent(m)) AssetDatabase.SaveAssetIfDirty(m);
            Debug.Log($"[SpecularExV2] Combined the MatCap front/back textures of '{m.name}' into '{path}'.", m);
            return true;
        }

        static void RemoveLegacyEntries(Material m)
        {
            var so = new SerializedObject(m);
            RemoveNamed(so.FindProperty("m_SavedProperties.m_TexEnvs"), LegacyBackProp);
            RemoveNamed(so.FindProperty("m_SavedProperties.m_Floats"), LegacyBackEnabledProp);
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        static void RemoveNamed(SerializedProperty array, string name)
        {
            if (array == null) return;
            for (int i = array.arraySize - 1; i >= 0; i--)
                if (array.GetArrayElementAtIndex(i).FindPropertyRelative("first").stringValue == name)
                    array.DeleteArrayElementAtIndex(i);
        }

        // Path of the atlas for this pair, baking it if it does not exist yet. null on failure.
        static string EnsureAtlas(Texture front, Texture back)
        {
            string key = InputKey(front, back);
            if (key == null)
            {
                Debug.LogError("[SpecularExV2] MatCap textures must be saved assets to be combined.");
                return null;
            }
            string path = Folder + "/" + key + ".png";
            if (AssetDatabase.LoadAssetAtPath<Texture2D>(path) != null) return path;

            bool srgb = IsSRGB(front ?? back);
            byte[] png = BakePng(front, back, srgb);
            if (png == null) return null;

            EnsureFolder();
            File.WriteAllBytes(path, png);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            if (AssetImporter.GetAtPath(path) is TextureImporter ti)
            {
                ApplyImportSettings(ti, srgb, MaxDimension(front, back));
                ti.SaveAndReimport();
            }
            return path;
        }

        static string InputKey(Texture front, Texture back)
        {
            var sb = new StringBuilder("v").Append(Version).Append(';');
            foreach (var t in new[] { front, back })
            {
                if (t == null) { sb.Append("_;"); continue; }
                if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(t, out string guid, out long localId)) return null;
                string p = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(p)) return null;
                sb.Append(guid).Append(':').Append(localId).Append(':')
                  .Append(AssetDatabase.GetAssetDependencyHash(p)).Append(';');
            }
            return "m" + Hash128.Compute(sb.ToString());
        }

        static bool IsSRGB(Texture t)
        {
            if (t == null) return true;
            return !(AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(t)) is TextureImporter ti) || ti.sRGBTexture;
        }

        static int MaxDimension(Texture front, Texture back)
        {
            int w = 4, h = 4;
            foreach (var t in new[] { front, back })
            {
                if (t == null) continue;
                w = Mathf.Max(w, t.width);
                h = Mathf.Max(h, t.height);
            }
            return Mathf.Min(Mathf.Max(w, h), MaxHalfSize);
        }

        // Left half = front, right half = back, each (size x size); an empty side stays black.
        static byte[] BakePng(Texture front, Texture back, bool srgb)
        {
            var shader = Shader.Find(BlitShader);
            if (shader == null)
            {
                Debug.LogError($"[SpecularExV2] MatCap atlas shader '{BlitShader}' not found.");
                return null;
            }

            int size = MaxDimension(front, back);
            int width = size * 2, height = size;
            var mat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            if (front != null) mat.SetTexture("_Front", front);
            if (back != null) mat.SetTexture("_Back", back);

            var rw = srgb ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear;
            var rt = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32, rw);
            var prevActive = RenderTexture.active;
            Texture2D tex = null;
            try
            {
                Graphics.Blit(null, rt, mat);
                RenderTexture.active = rt;
                tex = new Texture2D(width, height, TextureFormat.RGBA32, /*mipChain*/ false, /*linear*/ !srgb);
                tex.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
                tex.Apply(false, false);
                return tex.EncodeToPNG();
            }
            finally
            {
                RenderTexture.active = prevActive;
                RenderTexture.ReleaseTemporary(rt);
                Object.DestroyImmediate(mat);
                if (tex != null) Object.DestroyImmediate(tex);
            }
        }

        // A matcap: color (sRGB like its source), alpha kept, clamp wrap, mipmaps for the blur slider.
        static void ApplyImportSettings(TextureImporter ti, bool srgb, int halfSize)
        {
            ti.textureType = TextureImporterType.Default;
            ti.textureShape = TextureImporterShape.Texture2D;
            ti.sRGBTexture = srgb;
            ti.alphaSource = TextureImporterAlphaSource.FromInput;
            ti.alphaIsTransparency = false;
            ti.npotScale = TextureImporterNPOTScale.None;
            ti.mipmapEnabled = true;
            ti.streamingMipmaps = true;
            ti.isReadable = false;
            ti.wrapMode = TextureWrapMode.Clamp;
            ti.filterMode = FilterMode.Bilinear;
            ti.maxTextureSize = Mathf.Clamp(Mathf.NextPowerOfTwo(halfSize * 2), 32, 8192);
            ti.textureCompression = TextureImporterCompression.CompressedHQ;
        }

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

        [MenuItem("Window/SpecularExV2/MatCap/Combine Legacy Front+Back Textures (All Materials)")]
        static void MigrateAll()
        {
            var targets = new List<Material>();
            var paths = SpecularExPackedMaskStore.FindMaterialContainerPaths();
            try
            {
                for (int i = 0; i < paths.Count; i++)
                {
                    if (EditorUtility.DisplayCancelableProgressBar("SpecularExV2", paths[i], (float)i / paths.Count)) return;
                    if (!SpecularExPackedMaskStore.UsesSpecularExShader(paths[i])) continue;
                    foreach (var m in SpecularExPackedMaskStore.LoadMaterialsAtPath(paths[i]))
                        if (HasLegacyBackTexture(m)) targets.Add(m);
                }
            }
            finally { EditorUtility.ClearProgressBar(); }

            int done = 0;
            foreach (var m in targets) if (Migrate(m)) done++;
            Debug.Log($"[SpecularExV2] Combined the MatCap textures of {done}/{targets.Count} material(s).");
        }
    }
}
#endif
