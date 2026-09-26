#if UNITY_EDITOR && SPECULAREXV2_HAS_NDMF
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using UnityEngine;

[assembly: ExportsPlugin(typeof(Dennokoworks.SpecularExV2.SpecularExV2NDMFPlugin))]

namespace Dennokoworks.SpecularExV2
{
    // Removes textures the uploaded avatar never samples. Only compiled when NDMF is installed; without it
    // the build keeps the original materials (SpecularExPackedMaskBuildHook still packs them).
    //
    // Unity ships every texture a material references, whether the shader samples it or not. The eight
    // *_MaskTex slots only exist so the editor can (re)bake the packed RGBA masks, so without this pass
    // an upload contains both the packed masks and every source image.
    //
    // For each SpecularExV2 material on the avatar (renderers and animation-swapped materials) a clone is
    // made, packed, and then:
    //   * the source mask slots are cleared (their tiling/offset is kept; the shader uses it),
    //   * a packed mask whose channels no feature can read is dropped,
    //   * the normal map 3rd/4th and matcap textures of disabled features are dropped.
    // A feature whose enable flag (or noise strength) is animated anywhere counts as possibly on.
    // The project's .mat assets are never modified.
    internal class SpecularExV2NDMFPlugin : Plugin<SpecularExV2NDMFPlugin>
    {
        public override string QualifiedName => "dennokoworks.specularex.v2";
        public override string DisplayName => "SpecularExV2";

        // Feature texture -> the effective enable flag that gates its only sample.
        static readonly (string texture, string enabled)[] FeatureTextures =
        {
            ("_CustomNormal3rdTex",   "_CustomNormal3rdEnabled"),
            ("_CustomNormal4thTex",   "_CustomNormal4thEnabled"),
            ("_CustomMatcapFrontTex", "_CustomMatcapEnabled"),
        };

        protected override void Configure()
        {
            // Late, after the tools that create or merge materials, so the final materials are processed.
            InPhase(BuildPhase.Optimizing)
                .AfterPlugin("nadena.dev.modular-avatar")
                .AfterPlugin("net.rs64.tex-trans-tool")
                .WithRequiredExtension(typeof(AnimatorServicesContext), seq =>
                    seq.Run("Strip SpecularExV2 source masks", StripSourceMasks));
        }

        static void StripSourceMasks(BuildContext context)
        {
            var animation = context.Extension<AnimatorServicesContext>();
            var animated = CollectAnimatedMaterialProperties(animation);
            bool IsAnimated(string prop) => animated.Contains(prop);

            var renderers = context.AvatarRootObject.GetComponentsInChildren<Renderer>(true);
            var originals = new HashSet<Material>();
            foreach (var r in renderers)
                foreach (var m in r.sharedMaterials)
                    if (m != null) originals.Add(m);
            foreach (var o in animation.AnimationIndex.GetPPtrReferencedObjects)
                if (o is Material m) originals.Add(m);

            var clones = new Dictionary<Material, Material>();
            foreach (var original in originals)
            {
                if (!SpecularExMaskPacker.HasPackedSlot(original) || !CanStrip(original, IsAnimated)) continue;
                var clone = new Material(original) { name = original.name };
                ObjectRegistry.RegisterReplacedObject(original, clone);
                context.AssetSaver.SaveAsset(clone);
                clones[original] = clone;
            }
            if (clones.Count == 0) return;

            // Pack first: stripping removes the inputs. A clone that cannot be packed keeps its slots, and
            // the VRChat build hook reports it and stops the upload.
            SpecularExPackedMaskStore.EnsureAll(clones.Values, persist: false);
            foreach (var clone in clones.Values)
            {
                if (!SpecularExPackedMaskStore.ArePacksAssigned(clone)) continue;
                SpecularExPackedMaskStore.StripSources(clone);
                for (int p = 0; p < SpecularExMaskPacker.Packs.Length; p++)
                {
                    string prop = SpecularExMaskPacker.Packs[p].prop;
                    if (clone.HasProperty(prop) && !SpecularExMaskPacker.IsPackUsed(clone, p, IsAnimated))
                        clone.SetTexture(prop, null);
                }
                foreach (var (texture, enabled) in FeatureTextures)
                    if (clone.HasProperty(texture) && !IsOn(clone, enabled, IsAnimated))
                        clone.SetTexture(texture, null);
            }

            foreach (var r in renderers)
            {
                var materials = r.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < materials.Length; i++)
                {
                    if (materials[i] == null || !clones.TryGetValue(materials[i], out var clone)) continue;
                    materials[i] = clone;
                    changed = true;
                }
                if (changed) r.sharedMaterials = materials;
            }

            // Materials swapped in by animation are referenced by the clips rather than by a renderer.
            animation.AnimationIndex.RewriteObjectCurves(o =>
                o is Material m && clones.TryGetValue(m, out var clone) ? clone : o);
        }

        // Something would be removed from the clone.
        static bool CanStrip(Material m, System.Func<string, bool> isAnimated)
        {
            if (SpecularExMaskPacker.NeedsPacking(m)) return true;
            for (int p = 0; p < SpecularExMaskPacker.Packs.Length; p++)
            {
                string prop = SpecularExMaskPacker.Packs[p].prop;
                if (m.HasProperty(prop) && m.GetTexture(prop) != null
                    && !SpecularExMaskPacker.IsPackUsed(m, p, isAnimated)) return true;
            }
            foreach (var (texture, enabled) in FeatureTextures)
                if (m.HasProperty(texture) && m.GetTexture(texture) != null && !IsOn(m, enabled, isAnimated))
                    return true;
            return false;
        }

        static bool IsOn(Material m, string prop, System.Func<string, bool> isAnimated)
            => !m.HasProperty(prop) || m.GetFloat(prop) > 0.5f || isAnimated(prop);

        // Names of material properties driven by any animation ("material._Foo" / "material._Foo.x" -> "_Foo").
        static HashSet<string> CollectAnimatedMaterialProperties(AnimatorServicesContext animation)
        {
            var result = new HashSet<string>();
            var clips = animation.ControllerContext.GetAllControllers()
                .SelectMany(c => c.AllReachableNodes())
                .OfType<VirtualClip>()
                .Distinct();
            foreach (var clip in clips)
            {
                foreach (var binding in clip.GetFloatCurveBindings())
                {
                    string name = binding.propertyName;
                    if (!name.StartsWith("material.")) continue;
                    name = name.Substring("material.".Length);
                    int dot = name.IndexOf('.');
                    result.Add(dot < 0 ? name : name.Substring(0, dot));
                }
            }
            return result;
        }
    }
}
#endif
