#if UNITY_EDITOR
using UnityEngine;

namespace Dennokoworks.SpecularExV2
{
    // Packs SpecularExV2's four single-channel mask textures into one RGBA image so the runtime shader
    // declares one texture instead of four (keeping it under the 64 texture-parameter limit). Each mask
    // keeps its own tiling/offset because the runtime shader samples the packed texture separately per
    // channel with that slot's _ST — packing is done 1:1 with no UV transform here.
    //
    // This class only produces pixels. Persisting them (PNG asset, import settings, assignment to the
    // material) is SpecularExPackedMaskStore's job.
    //
    // Channel layout (must match custom.hlsl / SpecularEx_MaskPacker.shader):
    //   Pack 1: R = _CustomRefl2ndMaskTex    G = _CustomRim2ndMaskTex
    //           B = _CustomNormal3rdMaskTex  A = _CustomMatcapMaskTex
    //   Pack 2: R = _CustomRefl3rdMaskTex    G = _CustomRim3rdMaskTex
    //           B = _CustomNormal4thMaskTex
    public static class SpecularExMaskPacker
    {
        public const string ShaderNameRoot = "dennokoworks/SpecularExV2";
        public const string PackedProp1 = "_CustomMaskPacked";
        public const string PackedProp2 = "_CustomMaskPacked2";
        public const string PackedProp = PackedProp1;

        // Channel order must match custom.hlsl and SpecularEx_MaskPacker.shader.
        public static readonly string[] SourceProps1 =
        {
            "_CustomRefl2ndMaskTex",   // R
            "_CustomRim2ndMaskTex",    // G
            "_CustomNormal3rdMaskTex", // B
            "_CustomMatcapMaskTex",    // A
        };

        public static readonly string[] SourceProps2 =
        {
            "_CustomRefl3rdMaskTex",   // R
            "_CustomRim3rdMaskTex",    // G
            "_CustomNormal4thMaskTex", // B
            null,                      // A
        };

        public struct PackDefinition
        {
            public string prop;
            public string[] slots;
            public PackDefinition(string prop, string[] slots) { this.prop = prop; this.slots = slots; }
        }

        public static readonly PackDefinition[] Packs =
        {
            new PackDefinition(PackedProp1, SourceProps1),
            new PackDefinition(PackedProp2, SourceProps2),
        };

        public static readonly string[] AllSourceProps =
        {
            "_CustomRefl2ndMaskTex",
            "_CustomRim2ndMaskTex",
            "_CustomNormal3rdMaskTex",
            "_CustomMatcapMaskTex",
            "_CustomRefl3rdMaskTex",
            "_CustomRim3rdMaskTex",
            "_CustomNormal4thMaskTex",
        };

        public static readonly string[] SourceProps = AllSourceProps;

        // Bump when the produced pixels change for identical inputs (shader, size rule, encoding),
        // so previously generated files are no longer considered up to date.
        public const int Version = 1;

        const string PackerShader = "Hidden/dennokoworks/SpecularExV2/MaskPacker";
        const int MaxSize = 2048;
        const int MinSize = 4;

        public static bool IsSpecularEx(Material m)
            => m != null && m.shader != null && m.shader.name.Contains(ShaderNameRoot);

        // The material's current shader declares the packed slot. Must be checked before any
        // Get/SetTexture(PackedProp): Unity logs an error per call on shaders without the property.
        public static bool HasPackedSlot(Material m)
            => IsSpecularEx(m) && m.HasProperty(PackedProp1);

        public static Texture GetSource(Material m, int channel)
            => channel >= 0 && channel < AllSourceProps.Length && m.HasProperty(AllSourceProps[channel])
                ? m.GetTexture(AllSourceProps[channel])
                : null;

        public static Texture GetSource(Material m, string prop)
            => !string.IsNullOrEmpty(prop) && m.HasProperty(prop) ? m.GetTexture(prop) : null;

        // True if the material has at least one mask worth packing for the given pack.
        public static bool NeedsPacking(Material m, int packIndex)
        {
            if (m == null || packIndex < 0 || packIndex >= Packs.Length) return false;
            var slots = Packs[packIndex].slots;
            for (int i = 0; i < slots.Length; i++)
            {
                if (slots[i] != null && m.HasProperty(slots[i]) && m.GetTexture(slots[i]) != null)
                    return true;
            }
            return false;
        }

        public static bool NeedsPacking(Material m)
        {
            for (int p = 0; p < Packs.Length; p++)
                if (NeedsPacking(m, p)) return true;
            return false;
        }

        // Bakes mask slots for the specified pack into a linear RGBA32 image and returns it PNG-encoded.
        public static byte[] BakePng(Material m, int packIndex = 0)
        {
            if (!NeedsPacking(m, packIndex)) return null;

            var shader = Shader.Find(PackerShader);
            if (shader == null)
            {
                Debug.LogError($"[SpecularExV2] Mask packer shader '{PackerShader}' not found; cannot pack masks.");
                return null;
            }

            var slots = Packs[packIndex].slots;
            int size = MinSize;
            for (int i = 0; i < slots.Length; i++)
            {
                if (slots[i] == null) continue;
                var t = GetSource(m, slots[i]);
                if (t != null) size = Mathf.Max(size, Mathf.Max(t.width, t.height));
            }
            size = Mathf.Clamp(Mathf.NextPowerOfTwo(size), MinSize, MaxSize);

            var mat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            // Unset -> Unity binds the shader's "white" default, the correct neutral mask value.
            string[] targets = { "_TexR", "_TexG", "_TexB", "_TexA" };
            for (int i = 0; i < targets.Length; i++)
            {
                if (slots[i] != null)
                {
                    var t = GetSource(m, slots[i]);
                    if (t != null) mat.SetTexture(targets[i], t);
                }
            }

            var rt = RenderTexture.GetTemporary(size, size, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            var prevActive = RenderTexture.active;
            Texture2D tex = null;
            try
            {
                Graphics.Blit(null, rt, mat);
                RenderTexture.active = rt;
                tex = new Texture2D(size, size, TextureFormat.RGBA32, /*mipChain*/ false, /*linear*/ true);
                tex.ReadPixels(new Rect(0, 0, size, size), 0, 0, false);
                tex.Apply(false, false);
                return tex.EncodeToPNG();
            }
            finally
            {
                RenderTexture.active = prevActive;
                RenderTexture.ReleaseTemporary(rt);
                if (mat != null) Object.DestroyImmediate(mat);
                if (tex != null) Object.DestroyImmediate(tex);
            }
        }
    }
}
#endif
