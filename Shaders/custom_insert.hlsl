// SpecularExV2 custom_insert.hlsl
//
// Injected at lilToon's *LIL_SUBSHADER_INSERT* point, i.e. INSIDE each pass program:
//   #define LIL_PASS_FORWARDADD / LIL_PASS_META ...   (pass identification)
//   #include lil_pipeline_*.hlsl
//   #include lil_common.hlsl                          (declares the LIL_CUSTOM_TEXTURES of custom.hlsl)
//   >>> this file <<<
//   #include lil_pass_*.hlsl                          (where the BEFORE_* hook macros are expanded)
//
// Two consequences:
//  * The LIL_V2F_FORCE_* defines below still reach the v2f struct built in lil_pass_*.hlsl.
//  * custom.hlsl is included from the shader-level HLSLINCLUDE, BEFORE the pass defines
//    LIL_PASS_FORWARDADD, so `#if defined(LIL_PASS_FORWARDADD)` written there is always false.
//    Everything that must behave differently per pass, or that samples a custom texture (the
//    textures are declared by lil_common.hlsl, after custom.hlsl), therefore lives HERE.

#ifndef DNKW_SPEX_CUSTOM_INSERT_INCLUDED
#define DNKW_SPEX_CUSTOM_INSERT_INCLUDED

#if defined(LIL_PASS_SHADOWCASTER) || defined(LIL_PASS_META)

// ShadowCaster / Meta need none of the extension: no tangent input, no helpers. The shadow caster expands
// none of our hooks; the Meta pass expands BEFORE_EMISSION_1ST (twice), which would add Rim Light 2nd/3rd to
// the lightmap bake and references the helpers below, so it is emptied here (hooks expand after this file).
#undef  BEFORE_EMISSION_1ST
#define BEFORE_EMISSION_1ST

#else

// Normal Map 3rd composites in tangent space, so the TBN must reach the fragment shader.
#define LIL_V2F_FORCE_TANGENT
// ...and the vertex input must carry the tangent the v2f copies (lil_common_appdata.hlsl is included by
// the pass file, after this one). lilToon only adds it when its own features need a tangent
// (LIL_SHOULD_TANGENT), so without this, variants whose shader settings have no normal-map feature fail to
// compile ("invalid subscript 'tangentOS'").
#define LIL_REQUIRE_APP_TANGENT

// The World MatCap is an environment reflection, so it is drawn by ForwardBase only. Emptying its hook in
// ForwardAdd (instead of a uniform branch) keeps it out of every additional-light variant, which the
// compiler would otherwise have to build (5 light types x fog x instancing, per outline pass too).
#if defined(LIL_PASS_FORWARDADD)
    #undef  BEFORE_RIMLIGHT
    #define BEFORE_RIMLIGHT
#endif

//----------------------------------------------------------------------------------------------------------------------
// Common
//----------------------------------------------------------------------------------------------------------------------

// normalize() that cannot return NaN for a (near) zero vector.
float3 DNKW_SafeNormalize(float3 v, float3 fallback)
{
    float len2 = dot(v, v);
    return len2 > 1e-8 ? v * rsqrt(len2) : fallback;
}

// Packed mask 2 holds a layer's mask AND the shared noise, and both usually keep the default tiling/offset
// (1, 1, 0, 0), so within one hook several reads often hit the same uv. A hook keeps the last packed-2
// sample and its tiling/offset in a DNKW_MaskCache and reuses it when the next read has the same _ST.
// Everything involved is a material value, so the branch is uniform; a different _ST just samples again.
struct DNKW_MaskCache
{
    float4 value;
    float4 st;
    bool   valid;
};

float4 DNKW_SampleMask2Cached(inout DNKW_MaskCache cache, float2 uv, float4 st)
{
    if (!cache.valid || any(st != cache.st))
    {
        cache.value = LIL_SAMPLE_2D(_CustomMaskPacked2, sampler_linear_repeat, uv * st.xy + st.zw);
        cache.st    = st;
        cache.valid = true;
    }
    return cache.value;
}

// Grazing-angle weight: 1 at strength 0, pow(1 - N.V, power) at strength 1. The default strength 0 skips
// the pow (uniform branch; the result is identical).
float DNKW_FresnelWeight(float nv, float strength, float power)
{
    if (strength <= 0.0) return 1.0;
    return lerp(1.0, pow(1.0 - nv, power), strength);
}

// lilToneCorrection (gamma + HSV shift), skipped at the neutral value so an unused HSVG costs nothing and
// cannot clamp HDR colors.
float3 DNKW_ToneCorrection(float3 c, float4 hsvg)
{
    if (all(hsvg == float4(0.0, 1.0, 1.0, 1.0))) return c;
    return lilToneCorrection(c, hsvg);
}

//----------------------------------------------------------------------------------------------------------------------
// Specular 2nd
//----------------------------------------------------------------------------------------------------------------------

#define DNKW_CLEARCOAT_F0     0.04

// lilToon's reflection is gated by _ApplySpecularFA in the additive pass; Specular 2nd/3rd mirror that with
// their own *ApplyFA.
bool DNKW_Refl2ndPassEnabled(float applyFA)
{
    #if defined(LIL_PASS_FORWARDADD)
        return applyFA > 0.5;
    #else
        return true;
    #endif
}

// Shadow / distance attenuation of the specular lobe.
//   ForwardBase: shadowmix (toon shadow incl. received shadows) blended in by shadowAttenuation.
//   ForwardAdd : additionally fd.attenuation (point/spot falloff and shadows), exactly as lilToon's
//                lilReflection does with `fd.shadowmix * fd.attenuation`.
float DNKW_Refl2ndAttenuation(float shadowmix, float attenuation, float shadowAttenuation)
{
    float s = lerp(1.0, shadowmix, shadowAttenuation);
    #if defined(LIL_PASS_FORWARDADD)
        s *= attenuation;
    #endif
    return s;
}

// Specular term of lilToon's "reflection (real mode)" (lilCalcSpecular, isotropic path), with an
// optional Blinn-Phong lobe. Returns the lobe incl. N.L and Fresnel, WITHOUT light color.
//   mode 0: GGX with height-correlated Smith visibility (identical to lilToon).
//   mode 1: Blinn-Phong. The exponent is derived from the same roughness (n = 2/a^2 - 2) and the lobe
//           is normalized/visibility-scaled so its peak matches GGX; only the falloff shape differs,
//           so switching modes does not change the overall brightness.
float3 DNKW_Refl2ndSpecular(float3 N, float3 V, float3 L, float smoothness, float3 F0, float mode)
{
    float3 H  = normalize(V + L);
    float  nh = saturate(dot(N, H));
    float  nv = saturate(dot(N, V));
    float  nl = saturate(dot(N, L));
    float  lh = saturate(dot(L, H));

    float perceptualRoughness = 1.0 - smoothness;
    float roughness = max(perceptualRoughness * perceptualRoughness, 0.002); // alpha = roughness^2
    float r2 = roughness * roughness;

    float specularTerm;
    if (mode < 0.5)
    {
        float lambdaV = nl * (nv * (1.0 - roughness) + roughness);
        float lambdaL = nv * (nl * (1.0 - roughness) + roughness);
        #if defined(SHADER_API_MOBILE) || defined(SHADER_API_SWITCH)
            float sjggx = 0.5 / (lambdaV + lambdaL + 1e-4f);
        #else
            float sjggx = 0.5 / (lambdaV + lambdaL + 1e-5f);
        #endif
        float d = (nh * r2 - nh) * nh + 1.0;
        float ggx = r2 / (d * d + 1e-7f);
        specularTerm = sjggx * ggx;
    }
    else
    {
        float n = min(2.0 / r2 - 2.0, 8192.0);
        specularTerm = pow(nh, n) * (n + 2.0) * 0.125;
    }

    #ifdef LIL_COLORSPACE_GAMMA
        specularTerm = sqrt(max(1e-4, specularTerm));
    #endif
    specularTerm *= nl;
    return specularTerm * lilFresnelTerm(F0, lh);
}

// Light direction for the highlight. ForwardBase: fd.L blended toward a camera-relative direction
// (x = right, y = up, z = toward the viewer). headV (surface -> middle of both eyes) is the z axis, so
// both VR eyes see the highlight at the same place. ForwardAdd lights are real, so they are never moved.
// At the default blend 0 the camera-relative direction is not built: the result is normalize(L), which
// only needs the fake direction as the fallback for a zero-length L.
float3 DNKW_SpecularLightDir(float3 L, float3 cameraRight, float3 cameraUp, float3 headV, float blend, float3 dirCam)
{
    #if defined(LIL_PASS_FORWARDADD)
        return L;
    #else
        if (blend <= 0.0)
        {
            float len2 = dot(L, L);
            if (len2 > 1e-8) return L * rsqrt(len2);
        }
        float3 fakeL = DNKW_SafeNormalize(dirCam.x * cameraRight + dirCam.y * cameraUp + dirCam.z * headV, headV);
        return DNKW_SafeNormalize(lerp(L, fakeL, blend), fakeL);
    #endif
}

// Light color applied to the highlight, mirroring DNKW_MatcapLighting:
//   ForwardBase: lerp(1, lightColor, enableLighting)  (1 = original behavior)
//   ForwardAdd : the pass is additive per light, so the light color always scales it.
float3 DNKW_SpecularLighting(float3 lightColor, float enableLighting)
{
    #if !defined(LIL_PASS_FORWARDADD)
        return lerp(float3(1.0, 1.0, 1.0), lightColor, enableLighting);
    #else
        return lightColor * enableLighting;
    #endif
}

// One specular layer (Specular 2nd / 3rd). Adds the highlight to fd.col; with clear coat on, first darkens
// what is below by the coat's view Fresnel (F0 = 0.04). That darkening happens at BEFORE_REFLECTION, so
// lilToon's own reflection / matcap / rim / emission added later are not covered by the coat.
void DNKW_ApplySpecularLayer(inout lilFragData fd, float mask, float3 color, float strength, float mode,
    float smoothness, float metallic, float reflectance, float normalStrength, float shadowAttenuation,
    float mainColorStrength, float fakeLightBlend, float3 fakeLightDir, float enableLighting,
    float clearCoat, float fresnelStrength, float fresnelPower)
{
    bool   coat  = clearCoat > 0.5;
    float3 N     = normalize(lerp(fd.origN, fd.N, normalStrength));
    float3 L     = DNKW_SpecularLightDir(fd.L, fd.cameraRight, fd.cameraUp, fd.headV, fakeLightBlend, fakeLightDir);
    float  nv    = saturate(dot(N, fd.V));
    float3 F0    = coat ? (DNKW_CLEARCOAT_F0).xxx : lerp(reflectance.xxx, fd.albedo, metallic);
    float  atten = DNKW_Refl2ndAttenuation(fd.shadowmix, fd.attenuation, shadowAttenuation);
    float3 spec  = DNKW_Refl2ndSpecular(N, fd.V, L, smoothness, F0, mode);
    float3 tint  = color * lerp(float3(1.0, 1.0, 1.0), fd.albedo, mainColorStrength)
                 * DNKW_SpecularLighting(fd.lightColor, enableLighting);
    float3 contrib = spec * tint * (strength * mask * atten * DNKW_FresnelWeight(nv, fresnelStrength, fresnelPower));

    if (coat) fd.col.rgb *= 1.0 - lilFresnelTerm((DNKW_CLEARCOAT_F0).xxx, nv) * saturate(strength * mask);
    fd.col.rgb += contrib;
}

//----------------------------------------------------------------------------------------------------------------------
// World-Oriented Dual-Hemisphere MatCap
//----------------------------------------------------------------------------------------------------------------------

// Matcap uv for a world-fixing blend t (0 = ordinary view matcap, 1 = world-fixed).
// Instead of crossfading two samples (which shows two ghosted highlights in between), the two LOOKUP
// DIRECTIONS are blended and the texture is sampled once:
//   view : the view-space normal (lilToon's head-centered camera matrix, same uv as fd.uvMat)
//   world: the world-space reflection vector R = reflect(-V, N), yaw-rotated by the caller
// Both use the matcap (orthographic hemisphere) projection uv = d.xy * 0.5 + 0.5. The world side is
// mirrored across the Z plane (z folded to +), so one image covers both hemispheres continuously; the
// view side is folded too so the two directions are never (near) opposite. In between, the highlight
// follows the camera with a lag instead of sticking to the screen, while never leaving the image.
// t = 0 and t = 1 reproduce the pure modes exactly; there the unused direction may be passed as 0 (the
// caller skips building it), since lerp(a, 0, 0) == a and lerp(0, b, 1) == b.
float2 DNKW_MatcapUV(float3 dView, float3 dWorld, float t)
{
    dView.z  = abs(dView.z);
    dWorld.z = abs(dWorld.z);
    float3 d = DNKW_SafeNormalize(lerp(dView, dWorld, t), float3(0.0, 0.0, 1.0));
    return d.xy * 0.5 + 0.5;
}

// MatCap lighting, mirroring lilToon's lilGetMatCap (_MatCapEnableLighting): lerp toward the lit color by
// enableLighting. ForwardBase only (the hook is emptied in ForwardAdd, see the top of this file).
float3 DNKW_MatcapLighting(float3 mc, float3 lightColor, float enableLighting)
{
    return lerp(mc, mc * lightColor, enableLighting);
}

#endif // !LIL_PASS_SHADOWCASTER && !LIL_PASS_META
#endif // DNKW_SPEX_CUSTOM_INSERT_INCLUDED
