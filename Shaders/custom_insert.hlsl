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
// ...and the vertex input must carry the tangent the v2f copies (lil_common_appdata.hlsl is included by
// the pass file, after this one). Without it the lilToonMulti variants without a normal-map keyword fail
// to compile ("invalid subscript 'tangentOS'").
#define LIL_REQUIRE_APP_TANGENT

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
// Common
//----------------------------------------------------------------------------------------------------------------------

// normalize() that cannot return NaN for a (near) zero vector.
float3 DNKW_SafeNormalize(float3 v, float3 fallback)
{
    float len2 = dot(v, v);
    return len2 > 1e-8 ? v * rsqrt(len2) : fallback;
}

// Grazing-angle weight: 1 at strength 0, pow(1 - N.V, power) at strength 1.
float DNKW_FresnelWeight(float nv, float strength, float power)
{
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
// _CustomRefl*LightLimit at (or near) its maximum disables the brightness cap.
#define DNKW_LIGHT_LIMIT_OFF  9.999

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

// Light direction for the highlight. ForwardBase: fd.L blended toward a camera-relative direction
// (x = right, y = up, z = toward the viewer). headV (surface -> middle of both eyes) is the z axis, so
// both VR eyes see the highlight at the same place. ForwardAdd lights are real, so they are never moved.
float3 DNKW_SpecularLightDir(float3 L, float3 cameraRight, float3 cameraUp, float3 headV, float blend, float3 dirCam)
{
    #if defined(LIL_PASS_FORWARDADD)
        return L;
    #else
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

// Caps the highlight's luminance at limit x the light's luminance, preserving its hue. lilToon only holds
// fd.lightColor up by _LightMinLimit in dark worlds, so an HDR / high-strength highlight would otherwise
// still glow there. limit >= DNKW_LIGHT_LIMIT_OFF leaves it untouched.
float3 DNKW_ApplyLightLimit(float3 contrib, float3 lightColor, float attenuation, float limit)
{
    if (limit > DNKW_LIGHT_LIMIT_OFF) return contrib;
    float cap = limit * lilLuminance(lightColor);
    #if defined(LIL_PASS_FORWARDADD)
        cap *= attenuation;
    #endif
    return contrib * min(1.0, cap / max(lilLuminance(contrib), 1e-4));
}

// One specular layer (Specular 2nd / 3rd). Adds the highlight to fd.col; with clear coat on, first darkens
// what is below by the coat's view Fresnel (F0 = 0.04). That darkening happens at BEFORE_REFLECTION, so
// lilToon's own reflection / matcap / rim / emission added later are not covered by the coat.
void DNKW_ApplySpecularLayer(inout lilFragData fd, float mask, float3 color, float strength, float mode,
    float smoothness, float metallic, float reflectance, float normalStrength, float shadowAttenuation,
    float mainColorStrength, float fakeLightBlend, float3 fakeLightDir, float enableLighting,
    float lightLimit, float clearCoat, float fresnelStrength, float fresnelPower)
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
    contrib = DNKW_ApplyLightLimit(contrib, fd.lightColor, fd.attenuation, lightLimit);

    if (coat) fd.col.rgb *= 1.0 - lilFresnelTerm((DNKW_CLEARCOAT_F0).xxx, nv) * saturate(strength * mask);
    fd.col.rgb += contrib;
}

//----------------------------------------------------------------------------------------------------------------------
// World-Oriented Dual-Hemisphere MatCap
//----------------------------------------------------------------------------------------------------------------------

// Half width (in R.z) of the crossfade between the front and back hemispheres.
#define DNKW_MATCAP_SEAM_WIDTH 0.15

// Samples one half of a side-by-side matcap (offset 0 = left/front, 0.5 = right/back) with a 0..1 uv of
// that half. The x coordinate is kept one texel (of the sampled mip) away from the half's edges, so
// bilinear filtering and the mip chain (whose filter spans the middle seam) never pull in the other half.
float4 DNKW_SampleMatcapHalf(float2 uv, float offset, float lod)
{
    float pad = _CustomMatcapFrontTex_TexelSize.x * exp2(lod) * 2.0; // 1 texel of mip `lod`, in half-uv units
    uv.x = clamp(uv.x, pad, 1.0 - pad) * 0.5 + offset;
    return LIL_SAMPLE_2D_LOD(_CustomMatcapFrontTex, lil_sampler_linear_clamp, uv, lod);
}

// Samples the world matcap for a WORLD- (or object-) space reflection vector R.
// Each hemisphere uses the matcap (orthographic hemisphere) projection uv = dir.xy * 0.5 + 0.5, so any
// ordinary matcap image can be used as a hemisphere:
//   Front (+Z): looking toward +Z  -> uv = ( R.x, R.y)
//   Back  (-Z): looking toward -Z  -> uv = (-R.x, R.y)   (+X is on the viewer's left there)
// With these conventions a direction on the seam (R.z = 0) lands on the matching edge of both images,
// and the smoothstep crossfade hides residual differences.
// layout 0: the single image is mirrored across the Z plane (uv = (R.x, R.y) on both sides), which is
//   continuous by construction and needs a single sample.
// layout 1: side by side in the same texture (left = front, right = back), two samples.
// Explicit-LOD sampling: implicit derivatives would jump at the hemisphere switch and at the uv
// discontinuity, producing a 1-pixel mip seam.
float4 DNKW_SampleWorldMatcap(float3 R, float layout, float lod)
{
    float2 uvFront = R.xy * 0.5 + 0.5;
    float4 col;
    if (layout < 0.5)
    {
        col = LIL_SAMPLE_2D_LOD(_CustomMatcapFrontTex, lil_sampler_linear_clamp, uvFront, lod);
    }
    else
    {
        float2 uvBack = float2(-R.x, R.y) * 0.5 + 0.5;
        float4 front = DNKW_SampleMatcapHalf(uvFront, 0.0, lod);
        float4 back  = DNKW_SampleMatcapHalf(uvBack,  0.5, lod);
        col = lerp(back, front, smoothstep(-DNKW_MATCAP_SEAM_WIDTH, DNKW_MATCAP_SEAM_WIDTH, R.z));
    }
    return col;
}

// Ordinary view-space matcap (world fixing OFF): the whole image, or its left (front) half with layout 1.
float4 DNKW_SampleViewMatcap(float2 uv, float layout, float lod)
{
    float4 col;
    if (layout < 0.5) col = LIL_SAMPLE_2D_LOD(_CustomMatcapFrontTex, lil_sampler_linear_clamp, uv, lod);
    else              col = DNKW_SampleMatcapHalf(uv, 0.0, lod);
    return col;
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
