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

// Normal Map 3rd composites in tangent space, so the TBN must reach the fragment shader.
#define LIL_V2F_FORCE_TANGENT
#define LIL_V2F_FORCE_BITANGENT

// Pass flags usable from the hook macros (macros are expanded after this file, so they resolve here).
// Branching on these constants is folded away by the compiler.
#if defined(LIL_PASS_FORWARDADD)
    #define DNKW_PASS_FORWARDADD 1
#else
    #define DNKW_PASS_FORWARDADD 0
#endif
#if defined(LIL_PASS_META)
    #define DNKW_PASS_META 1
#else
    #define DNKW_PASS_META 0
#endif

//----------------------------------------------------------------------------------------------------------------------
// Specular 2nd
//----------------------------------------------------------------------------------------------------------------------

// lilToon's reflection is gated by _ApplySpecularFA in the additive pass; Specular 2nd mirrors that.
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

//----------------------------------------------------------------------------------------------------------------------
// World-Oriented Dual-Hemisphere MatCap
//----------------------------------------------------------------------------------------------------------------------

// Half width (in R.z) of the crossfade between the front and back hemispheres.
#define DNKW_MATCAP_SEAM_WIDTH 0.15

// Samples the world matcap for a WORLD-space reflection vector R.
// Each hemisphere uses the matcap (orthographic hemisphere) projection uv = dir.xy * 0.5 + 0.5, so any
// ordinary matcap image can be used as a hemisphere:
//   Front (+Z): looking toward +Z  -> uv = ( R.x, R.y)
//   Back  (-Z): looking toward -Z  -> uv = (-R.x, R.y)   (+X is on the viewer's left there)
// With these conventions a direction on the seam (R.z = 0) lands on the matching edge of both images,
// and the smoothstep crossfade hides residual differences.
// Without a Back texture the Front image is mirrored across the Z plane (uv = (R.x, R.y) on both sides),
// which is continuous by construction and needs a single sample.
// Explicit-LOD sampling: implicit derivatives would jump at the hemisphere switch and at the uv
// discontinuity, producing a 1-pixel mip seam.
float4 DNKW_SampleWorldMatcap(float3 R, float useBack, float lod)
{
    float2 uvFront = R.xy * 0.5 + 0.5;
    float4 front = LIL_SAMPLE_2D_LOD(_CustomMatcapFrontTex, lil_sampler_linear_clamp, uvFront, lod);
    if (useBack < 0.5) return front;

    float2 uvBack = float2(-R.x, R.y) * 0.5 + 0.5;
    float4 back = LIL_SAMPLE_2D_LOD(_CustomMatcapBackTex, lil_sampler_linear_clamp, uvBack, lod);
    return lerp(back, front, smoothstep(-DNKW_MATCAP_SEAM_WIDTH, DNKW_MATCAP_SEAM_WIDTH, R.z));
}

// Ordinary view-space matcap (world fixing OFF): the Front texture only.
float4 DNKW_SampleViewMatcap(float2 uv, float lod)
{
    return LIL_SAMPLE_2D_LOD(_CustomMatcapFrontTex, lil_sampler_linear_clamp, uv, lod);
}

// MatCap lighting, mirroring lilToon's lilGetMatCap (_MatCapEnableLighting).
//   ForwardBase: lerp toward the lit color by enableLighting.
//   ForwardAdd : the pass output is ADDED per light, so the matcap is scaled by the additional light
//                color (Normal/Add/Screen); Multiply (mode 3) is left as-is, same as lilToon.
float3 DNKW_MatcapLighting(float3 mc, float3 lightColor, float enableLighting, float blendMode)
{
    #if !defined(LIL_PASS_FORWARDADD)
        return lerp(mc, mc * lightColor, enableLighting);
    #else
        return (blendMode < 2.5) ? mc * lightColor * enableLighting : mc;
    #endif
}

#endif // DNKW_SPEX_CUSTOM_INSERT_INCLUDED
