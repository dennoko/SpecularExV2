// Editor-only blit shader used by SpecularExMatcapAtlasBaker (never referenced by materials).
// Places the legacy front / back matcap hemispheres side by side in one texture:
//   left half = _Front (+Z)   right half = _Back (-Z)
// Explicit LOD 0: implicit derivatives would jump at the split and sample a blurry mip on that column.
// Colors are sampled with the sources' own import settings and written to a render target of the
// matching color space by the baker, so the values round-trip unchanged.
Shader "Hidden/dennokoworks/SpecularExV2/MatcapAtlas"
{
    Properties
    {
        _Front ("Front", 2D) = "black" {}
        _Back ("Back", 2D) = "black" {}
    }
    SubShader
    {
        Pass
        {
            ZTest Always Cull Off ZWrite Off
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _Front;
            sampler2D _Back;

            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

            v2f vert(appdata_img v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv  = v.texcoord;
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                return i.uv.x < 0.5
                    ? tex2Dlod(_Front, float4(i.uv.x * 2.0,       i.uv.y, 0, 0))
                    : tex2Dlod(_Back,  float4(i.uv.x * 2.0 - 1.0, i.uv.y, 0, 0));
            }
            ENDCG
        }
    }
    Fallback Off
}
