//----------------------------------------------------------------------------------------------------------------------
// SpecularExV2 custom.hlsl - lilToon 2.x extension
// Features: Specular 2nd / Specular 3rd / World-Oriented Dual-Hemisphere MatCap / Normal Map 3rd / Rim Light 2nd
//
// Hook order in lilToon's forward pass (lil_pass_forward_normal.hlsl):
//   [Normal 1st/2nd] -> BEFORE_AUDIOLINK    : (1) Normal Map 3rd
//   [Main / Shadow]  -> BEFORE_REFLECTION   : (2) Specular 2nd        (ForwardBase + ForwardAdd)
//   [Reflection/MatCap] -> BEFORE_RIMLIGHT  : (3) World MatCap        (ForwardBase + ForwardAdd)
//   [Rim light]      -> BEFORE_EMISSION_1ST : (4) Rim Light 2nd       (ForwardBase only; lilToon does not
//                                                                      expand this hook in ForwardAdd)
// ShadowCaster / DepthOnly passes never expand these hooks; the Meta pass (which expands
// BEFORE_EMISSION_1ST) is excluded explicitly via DNKW_PASS_META.
//
// Pass-dependent helpers (DNKW_*) and texture sampling helpers live in custom_insert.hlsl — see there.
//----------------------------------------------------------------------------------------------------------------------

//----------------------------------------------------------------------------------------------------------------------
// Macro

// Custom variables
#define LIL_CUSTOM_PROPERTIES \
    float4 _CustomRefl2ndColor; \
    float  _CustomRefl2ndStrength; \
    float  _CustomRefl2ndMode; \
    float  _CustomRefl2ndSmoothness; \
    float  _CustomRefl2ndMetallic; \
    float  _CustomRefl2ndReflectance; \
    float  _CustomRefl2ndNormalStrength; \
    float  _CustomRefl2ndShadowAttenuation; \
    float  _CustomRefl2ndMainColorStrength; \
    float  _CustomRefl2ndApplyFA; \
    float  _CustomRefl2ndFakeLightBlend; \
    float4 _CustomRefl2ndFakeLightDir; \
    float  _CustomRefl2ndEnableLighting; \
    float  _CustomRefl2ndLightLimit; \
    float  _CustomRefl2ndClearCoat; \
    float  _CustomRefl2ndFresnelStrength; \
    float  _CustomRefl2ndFresnelPower; \
    float  _CustomRefl2ndEnabled; \
    float4 _CustomRefl2ndMaskTex_ST; \
    float4 _CustomRefl3rdColor; \
    float  _CustomRefl3rdStrength; \
    float  _CustomRefl3rdMode; \
    float  _CustomRefl3rdSmoothness; \
    float  _CustomRefl3rdMetallic; \
    float  _CustomRefl3rdReflectance; \
    float  _CustomRefl3rdNormalStrength; \
    float  _CustomRefl3rdShadowAttenuation; \
    float  _CustomRefl3rdMainColorStrength; \
    float  _CustomRefl3rdApplyFA; \
    float  _CustomRefl3rdFakeLightBlend; \
    float4 _CustomRefl3rdFakeLightDir; \
    float  _CustomRefl3rdEnableLighting; \
    float  _CustomRefl3rdLightLimit; \
    float  _CustomRefl3rdClearCoat; \
    float  _CustomRefl3rdFresnelStrength; \
    float  _CustomRefl3rdFresnelPower; \
    float  _CustomRefl3rdEnabled; \
    float4 _CustomRefl3rdMaskTex_ST; \
    float4 _CustomMatcapColor; \
    float  _CustomMatcapAlpha; \
    float  _CustomMatcapBlendMode; \
    float  _CustomMatcapBlur; \
    float  _CustomMatcapWorldFixed; \
    float  _CustomMatcapWorldRotation; \
    float  _CustomMatcapNormalStrength; \
    float  _CustomMatcapEnableLighting; \
    float  _CustomMatcapShadowStrength; \
    float  _CustomMatcapDisableBackface; \
    float  _CustomMatcapFresnelStrength; \
    float  _CustomMatcapFresnelPower; \
    float4 _CustomMatcapHSVG; \
    float  _CustomMatcapMainColorStrength; \
    float  _CustomMatcapBackEnabled; \
    float  _CustomMatcapEnabled; \
    float4 _CustomMatcapMaskTex_ST; \
    float4 _CustomNormal3rdTex_ST; \
    float  _CustomNormal3rdStrength; \
    float  _CustomNormal3rdTex_UVMode; \
    float4 _CustomNormal3rdTex_ScrollRotate; \
    float4 _CustomNormal3rdDistanceFade; \
    float  _CustomNormal3rdEnabled; \
    float4 _CustomNormal3rdMaskTex_ST; \
    float4 _CustomRim2ndColor; \
    float  _CustomRim2ndStrength; \
    float  _CustomRim2ndPower; \
    float  _CustomRim2ndBorder; \
    float  _CustomRim2ndBlur; \
    float  _CustomRim2ndVerticalBias; \
    float  _CustomRim2ndBacklight; \
    float  _CustomRim2ndEnableLighting; \
    float  _CustomRim2ndBlendMode; \
    float  _CustomRim2ndNormalStrength; \
    float  _CustomRim2ndShadowAttenuation; \
    float  _CustomRim2ndMainColorStrength; \
    float  _CustomRim2ndEnabled; \
    float4 _CustomRim2ndMaskTex_ST;

// Custom textures
// Two separate limits apply:
//   * 16 SAMPLERS (ps_4_0): every texture below uses lilToon's shared samplers (sampler_linear_repeat /
//     lil_sampler_linear_clamp), so no SamplerState is declared here.
//   * 64 TEXTURE PARAMETERS per shader: the single-channel masks are packed into RGBA textures:
//       Packed 1: R = Specular 2nd   G = Rim Light 2nd   B = Normal Map 3rd   A = World MatCap
//       Packed 2: R = Specular 3rd
//     Each channel is still sampled with its own mask slot's tiling/offset, so nothing is lost.
#define LIL_CUSTOM_TEXTURES \
    TEXTURE2D(_CustomMaskPacked); \
    TEXTURE2D(_CustomMaskPacked2); \
    TEXTURE2D(_CustomNormal3rdTex); \
    TEXTURE2D(_CustomMatcapFrontTex); \
    TEXTURE2D(_CustomMatcapBackTex);

// Add vertex copy
#define LIL_CUSTOM_VERT_COPY

//----------------------------------------------------------------------------------------------------------------------
// Helpers (pass independent)

#define DNKW_DEG2RAD 0.01745329252

// Samples one channel-set of the packed mask with a mask slot's tiling/offset.
#define DNKW_SAMPLE_MASK(st)  LIL_SAMPLE_2D(_CustomMaskPacked,  sampler_linear_repeat, fd.uv0 * (st).xy + (st).zw)
#define DNKW_SAMPLE_MASK2(st) LIL_SAMPLE_2D(_CustomMaskPacked2, sampler_linear_repeat, fd.uv0 * (st).xy + (st).zw)

// Rotates a world-space direction around the world Y axis (yaw), in degrees.
float3 DNKW_RotateYaw(float3 v, float degrees)
{
    float s, c;
    sincos(degrees * DNKW_DEG2RAD, s, c);
    return float3(v.x * c + v.z * s, v.y, -v.x * s + v.z * c);
}

// Refresh everything lilToon derives from fd.N. lilToon computes these once, BEFORE the BEFORE_AUDIOLINK
// hook (lil_pass_forward_normal.hlsl, "Normal" block), so a hook that changes fd.N must recompute them or
// the change is silently ignored by later stages:
//   reflectionN / matcapN / matcap2ndN / uvMat -> reflection & matcaps (incl. World MatCap)
//   ln                                         -> toon shadow ramp (OVERRIDE_SHADOW) and ForwardAdd shading
//   nv / nvabs / uvRim                         -> lilToon's rim light and other view-dependent terms
#define DNKW_REFRESH_NORMAL_DERIVED \
    fd.reflectionN  = fd.N; \
    fd.matcapN      = fd.N; \
    fd.matcap2ndN   = fd.N; \
    fd.uvMat        = mul(fd.cameraMatrix, fd.N).xy * 0.5 + 0.5; \
    fd.ln           = dot(fd.L, fd.N); \
    fd.nv           = saturate(dot(fd.N, fd.V)); \
    fd.nvabs        = abs(dot(fd.N, fd.V)); \
    fd.uvRim        = float2(fd.nvabs, fd.nvabs);

//----------------------------------------------------------------------------------------------------------------------
// (1) BEFORE_AUDIOLINK - Normal Map 3rd
//
// Layered on top of lilToon's Normal 1st/2nd result (which is left untouched): the current normal is
// brought to tangent space and Whiteout-blended with the 3rd map via lilBlendNormal, exactly like lilToon
// blends 1st and 2nd. lilUnpackNormalScale scales tangent xy without clamping, so strength is -2..2
// (negative flips the relief). The mask (packed .b) scales the strength.
// The map's UV scrolls/rotates via lilCalcUV (lilToon's ScrollRotate layout); the mask does not move.
// Distance fade lowers the strength with the head distance fd.depth (set before this hook) to calm
// moire/shimmering of fine detail far away. The texture is always sampled (no dynamic branch around an
// implicit-derivative sample).
#define BEFORE_AUDIOLINK \
    if (_CustomNormal3rdEnabled > 0.5) { \
        float2 _n3UV = fd.uv0; \
        if (_CustomNormal3rdTex_UVMode == 1) _n3UV = fd.uv1; \
        if (_CustomNormal3rdTex_UVMode == 2) _n3UV = fd.uv2; \
        if (_CustomNormal3rdTex_UVMode == 3) _n3UV = fd.uv3; \
        _n3UV = lilCalcUV(_n3UV, _CustomNormal3rdTex_ST, _CustomNormal3rdTex_ScrollRotate); \
        float  _n3Mask  = DNKW_SAMPLE_MASK(_CustomNormal3rdMaskTex_ST).b; \
        float4 _n3Fade  = _CustomNormal3rdDistanceFade; \
        _n3Mask *= lerp(1.0, 1.0 - saturate((fd.depth - _n3Fade.x) / max(_n3Fade.y - _n3Fade.x, 1e-4)), _n3Fade.z); \
        float4 _n3Raw   = LIL_SAMPLE_2D(_CustomNormal3rdTex, sampler_linear_repeat, _n3UV); \
        float3 _n3NTS   = lilUnpackNormalScale(_n3Raw, _CustomNormal3rdStrength * _n3Mask); \
        float3 _n3CurTS = mul(fd.TBN, fd.N); \
        float3 _n3Blend = lilBlendNormal(_n3CurTS, _n3NTS); \
        fd.N = normalize(mul(_n3Blend, fd.TBN)); \
        DNKW_REFRESH_NORMAL_DERIVED \
    }

//----------------------------------------------------------------------------------------------------------------------
// (2) BEFORE_REFLECTION - Specular 2nd
//
// lilToon "reflection (real mode)" specular: H = normalize(V + L), alpha = (1 - smoothness)^2, GGX (or
// Blinn-Phong) lobe x N.L x Fresnel(F0), F0 = lerp(reflectance, albedo, metallic).
// Expanded in ForwardBase (main directional light, fd.lightColor incl. vertex lights) AND ForwardAdd
// (one additional point/spot light per pass: fd.L / fd.lightColor of that light, x fd.attenuation) —
// ForwardAdd is gated by _CustomRefl2ndApplyFA like lilToon's _ApplySpecularFA.
// The base color is not darkened by metallic: this is an additive highlight layer on top of lilToon.
// Extensions (all neutral at their defaults; see DNKW_ApplySpecularLayer in custom_insert.hlsl):
//   fake light      - ForwardBase light DIRECTION blended toward a camera-relative one (brightness untouched)
//   enable lighting - how much fd.lightColor tints/limits the highlight (1 = original behavior)
//   light limit     - luminance cap relative to the light, so highlights do not float in dark worlds
//   clear coat      - F0 fixed at 0.04 and the base below darkened by the coat's Fresnel
//   fresnel         - weights the highlight toward grazing angles
#define BEFORE_REFLECTION \
    if (_CustomRefl2ndEnabled > 0.5 && DNKW_Refl2ndPassEnabled(_CustomRefl2ndApplyFA)) { \
        DNKW_ApplySpecularLayer(fd, DNKW_SAMPLE_MASK(_CustomRefl2ndMaskTex_ST).r, \
            _CustomRefl2ndColor.rgb, _CustomRefl2ndStrength, _CustomRefl2ndMode, _CustomRefl2ndSmoothness, \
            _CustomRefl2ndMetallic, _CustomRefl2ndReflectance, _CustomRefl2ndNormalStrength, \
            _CustomRefl2ndShadowAttenuation, _CustomRefl2ndMainColorStrength, \
            _CustomRefl2ndFakeLightBlend, _CustomRefl2ndFakeLightDir.xyz, _CustomRefl2ndEnableLighting, \
            _CustomRefl2ndLightLimit, _CustomRefl2ndClearCoat, _CustomRefl2ndFresnelStrength, _CustomRefl2ndFresnelPower); \
    } \
    if (_CustomRefl3rdEnabled > 0.5 && DNKW_Refl2ndPassEnabled(_CustomRefl3rdApplyFA)) { \
        DNKW_ApplySpecularLayer(fd, DNKW_SAMPLE_MASK2(_CustomRefl3rdMaskTex_ST).r, \
            _CustomRefl3rdColor.rgb, _CustomRefl3rdStrength, _CustomRefl3rdMode, _CustomRefl3rdSmoothness, \
            _CustomRefl3rdMetallic, _CustomRefl3rdReflectance, _CustomRefl3rdNormalStrength, \
            _CustomRefl3rdShadowAttenuation, _CustomRefl3rdMainColorStrength, \
            _CustomRefl3rdFakeLightBlend, _CustomRefl3rdFakeLightDir.xyz, _CustomRefl3rdEnableLighting, \
            _CustomRefl3rdLightLimit, _CustomRefl3rdClearCoat, _CustomRefl3rdFresnelStrength, _CustomRefl3rdFresnelPower); \
    }

//----------------------------------------------------------------------------------------------------------------------
// (3) BEFORE_RIMLIGHT - MatCap 2nd (optionally World-Oriented Dual-Hemisphere)
//
// _CustomMatcapWorldFixed = 0 (default): an ordinary view-space matcap, sampled from the Front texture
//   with lilToon's head-centered camera matrix (same UV as fd.uvMat, VR-stereo safe).
// _CustomMatcapWorldFixed = 1: the texture is looked up with the WORLD-space reflection vector
//   R = reflect(-V, N), optionally rotated around world Y (yaw). The highlight therefore stays fixed to the
//   world while the camera or the avatar turns — a lightweight pseudo cubemap made of two hemispheres
//   (front = +Z, back = -Z, crossfaded around R.z = 0; see DNKW_SampleWorldMatcap).
// _CustomMatcapWorldFixed = 2 (Object): same dual-hemisphere lookup, but R is taken to OBJECT space, so the
//   reflection turns with the avatar and stays still when only the camera moves. Yaw is then around the
//   object's local Y.
// Blend modes use lilBlendColor: 0 = Normal, 1 = Add, 2 = Screen, 3 = Multiply.
// The texture color goes through lilToneCorrection (HSVG, skipped at the neutral value) and can be
// multiplied by the main color; the opacity can be weighted toward grazing angles (fresnel).
#define BEFORE_RIMLIGHT \
    if (_CustomMatcapEnabled > 0.5) { \
        float3 _wmN   = normalize(lerp(fd.origN, fd.matcapN, _CustomMatcapNormalStrength)); \
        float4 _wmTex; \
        if (_CustomMatcapWorldFixed > 0.5) { \
            float3 _wmR = reflect(-fd.V, _wmN); \
            if (_CustomMatcapWorldFixed > 1.5) _wmR = lilTransformDirWStoOS(_wmR, true); \
            _wmR = DNKW_RotateYaw(_wmR, _CustomMatcapWorldRotation); \
            _wmTex = DNKW_SampleWorldMatcap(_wmR, _CustomMatcapBackEnabled, _CustomMatcapBlur); \
        } else { \
            _wmTex = DNKW_SampleViewMatcap(mul(fd.cameraMatrix, _wmN).xy * 0.5 + 0.5, _CustomMatcapBlur); \
        } \
        float3 _wmRGB = DNKW_ToneCorrection(_wmTex.rgb, _CustomMatcapHSVG); \
        _wmRGB *= _CustomMatcapColor.rgb * lerp(float3(1.0, 1.0, 1.0), fd.albedo, _CustomMatcapMainColorStrength); \
        float3 _wmCol = DNKW_MatcapLighting(_wmRGB, fd.lightColor, _CustomMatcapEnableLighting, _CustomMatcapBlendMode); \
        float  _wmA   = _wmTex.a * _CustomMatcapColor.a * _CustomMatcapAlpha * DNKW_SAMPLE_MASK(_CustomMatcapMaskTex_ST).a; \
        _wmA *= DNKW_FresnelWeight(saturate(dot(_wmN, fd.V)), _CustomMatcapFresnelStrength, _CustomMatcapFresnelPower); \
        _wmA *= lerp(1.0, fd.shadowmix, _CustomMatcapShadowStrength); \
        _wmA  = (_CustomMatcapDisableBackface > 0.5 && fd.facing < 0.0) ? 0.0 : _wmA; \
        fd.col.rgb = lilBlendColor(fd.col.rgb, _wmCol, _wmA, _CustomMatcapBlendMode); \
    }

//----------------------------------------------------------------------------------------------------------------------
// (4) BEFORE_EMISSION_1ST - Rim Light 2nd
//
// Light-direction independent by design (lilToon's _RimLightDirection fixed at 0): a pure view Fresnel
// term pow(1 - N.V, power). Blur = 1 keeps the soft gradient, 0 turns it into an anti-aliased hard edge at
// Border (default 0.5; lilTooning-style border/blur). Blend modes use lilBlendColor: 0 = Normal (lerp),
// 1 = Add, 2 = Screen, 3 = Multiply (rim shade).
// Direction without light dependence: VerticalBias restricts the rim to up- (+) or down-facing (-)
// surfaces against WORLD up. Backlight boosts the rim when the light is behind the surface (V.L -> -1);
// in worlds without a directional light fd.L is lilToon's SH direction.
// Enable lighting tints the rim by fd.lightColor like lilToon's _RimEnableLighting (default 1, so the rim
// darkens with the world instead of glowing); Multiply (rim shade) is left untouched.
// lilToon's Meta pass also expands this hook (twice); DNKW_PASS_META keeps the rim out of lightmap baking.
#define BEFORE_EMISSION_1ST \
    if (DNKW_PASS_META == 0 && _CustomRim2ndEnabled > 0.5) { \
        float3 _r2N     = normalize(lerp(fd.origN, fd.N, _CustomRim2ndNormalStrength)); \
        float  _r2Val   = pow(saturate(1.0 - saturate(dot(_r2N, fd.V))), _CustomRim2ndPower); \
        float  _r2Half  = _CustomRim2ndBlur * 0.5; \
        _r2Val = saturate((_r2Val - (_CustomRim2ndBorder - _r2Half)) / max(_r2Half * 2.0, fwidth(_r2Val) + 1e-4)); \
        float  _r2Amt   = _r2Val * _CustomRim2ndStrength * _CustomRim2ndColor.a * DNKW_SAMPLE_MASK(_CustomRim2ndMaskTex_ST).g; \
        _r2Amt *= lerp(1.0, fd.shadowmix, _CustomRim2ndShadowAttenuation); \
        float  _r2Up    = (_CustomRim2ndVerticalBias >= 0.0 ? _r2N.y : -_r2N.y) * 0.5 + 0.5; \
        _r2Amt *= lerp(1.0, _r2Up, abs(_CustomRim2ndVerticalBias)); \
        float  _r2Back  = saturate(-fd.vl); \
        _r2Amt  = saturate(_r2Amt * (1.0 + _CustomRim2ndBacklight * _r2Back * _r2Back)); \
        float3 _r2Color = _CustomRim2ndColor.rgb * lerp(float3(1.0, 1.0, 1.0), fd.albedo, _CustomRim2ndMainColorStrength); \
        if (_CustomRim2ndBlendMode < 2.5) _r2Color = lerp(_r2Color, _r2Color * fd.lightColor, _CustomRim2ndEnableLighting); \
        fd.col.rgb = lilBlendColor(fd.col.rgb, _r2Color, _r2Amt, _CustomRim2ndBlendMode); \
    }

//----------------------------------------------------------------------------------------------------------------------
// Information about variables
//----------------------------------------------------------------------------------------------------------------------

//----------------------------------------------------------------------------------------------------------------------
// Vertex shader inputs (appdata structure)
//
// Type     Name                    Description
// -------- ----------------------- --------------------------------------------------------------------
// float4   input.positionOS        POSITION
// float2   input.uv0               TEXCOORD0
// float2   input.uv1               TEXCOORD1
// float2   input.uv2               TEXCOORD2
// float2   input.uv3               TEXCOORD3
// float2   input.uv4               TEXCOORD4
// float2   input.uv5               TEXCOORD5
// float2   input.uv6               TEXCOORD6
// float2   input.uv7               TEXCOORD7
// float4   input.color             COLOR
// float3   input.normalOS          NORMAL
// float4   input.tangentOS         TANGENT
// uint     vertexID                SV_VertexID

//----------------------------------------------------------------------------------------------------------------------
// Vertex shader outputs or pixel shader inputs (v2f structure)
//
// The structure depends on the pass.
// Please check lil_pass_xx.hlsl for details.
//
// Type     Name                    Description
// -------- ----------------------- --------------------------------------------------------------------
// float4   output.positionCS       SV_POSITION
// float2   output.uv01             TEXCOORD0 TEXCOORD1
// float2   output.uv23             TEXCOORD2 TEXCOORD3
// float3   output.positionOS       object space position
// float3   output.positionWS       world space position
// float3   output.normalWS         world space normal
// float4   output.tangentWS        world space tangent

//----------------------------------------------------------------------------------------------------------------------
// Variables commonly used in the forward pass
//
// These are members of `lilFragData fd`
//
// Type     Name                    Description
// -------- ----------------------- --------------------------------------------------------------------
// float4   col                     lit color
// float3   albedo                  unlit color
// float3   emissionColor           color of emission
// -------- ----------------------- --------------------------------------------------------------------
// float3   lightColor              color of light
// float3   indLightColor           color of indirectional light
// float3   addLightColor           color of additional light
// float    attenuation             attenuation of light
// float3   invLighting             saturate((1.0 - lightColor) * sqrt(lightColor));
// -------- ----------------------- --------------------------------------------------------------------
// float2   uv0                     TEXCOORD0
// float2   uv1                     TEXCOORD1
// float2   uv2                     TEXCOORD2
// float2   uv3                     TEXCOORD3
// float2   uvMain                  Main UV
// float2   uvMat                   MatCap UV
// float2   uvRim                   Rim Light UV
// float2   uvPanorama              Panorama UV
// float2   uvScn                   Screen UV
// bool     isRightHand             input.tangentWS.w > 0.0;
// -------- ----------------------- --------------------------------------------------------------------
// float3   positionOS              object space position
// float3   positionWS              world space position
// float4   positionCS              clip space position
// float4   positionSS              screen space position
// float    depth                   distance from camera
// -------- ----------------------- --------------------------------------------------------------------
// float3x3 TBN                     tangent / bitangent / normal matrix
// float3   T                       tangent direction
// float3   B                       bitangent direction
// float3   N                       normal direction
// float3   V                       view direction
// float3   L                       light direction
// float3   origN                   normal direction without normal map
// float3   origL                   light direction without sh light
// float3   headV                   middle view direction of 2 cameras
// float3   reflectionN             normal direction for reflection
// float3   matcapN                 normal direction for reflection for MatCap
// float3   matcap2ndN              normal direction for reflection for MatCap 2nd
// float    facing                  VFACE
// -------- ----------------------- --------------------------------------------------------------------
// float    vl                      dot(viewDirection, lightDirection);
// float    hl                      dot(headDirection, lightDirection);
// float    ln                      dot(lightDirection, normalDirection);
// float    nv                      saturate(dot(normalDirection, viewDirection));
// float    nvabs                   abs(dot(normalDirection, viewDirection));
// -------- ----------------------- --------------------------------------------------------------------
// float4   triMask                 TriMask (for lite version)
// float3   parallaxViewDirection   mul(tbnWS, viewDirection);
// float2   parallaxOffset          parallaxViewDirection.xy / (parallaxViewDirection.z+0.5);
// float    anisotropy              strength of anisotropy
// float    smoothness              smoothness
// float    roughness               roughness
// float    perceptualRoughness     perceptual roughness
// float    shadowmix               this variable is 0 in the shadow area
// float    audioLinkValue          volume acquired by AudioLink
// -------- ----------------------- --------------------------------------------------------------------
// uint     renderingLayers         light layer of object (for URP / HDRP)
// uint     featureFlags            feature flags (for HDRP)
// uint2    tileIndex               tile index (for HDRP)