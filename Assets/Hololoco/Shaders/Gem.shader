// A drop's gem: its rarity colour with a bright rim, and a dissolve that breaks it
// apart along the old Coupemon rarity patterns (_Dissolve from 0 to 1).
Shader "Hololoco/Gem"
{
    Properties
    {
        _Color ("Colour", Color) = (0.55, 0.83, 1, 1)
        _DissolveTex ("Dissolve pattern", 2D) = "white" {}
        _Dissolve ("Dissolve", Range(0, 1)) = 0
        _EdgeColor ("Edge colour", Color) = (1, 1, 1, 1)
    }
    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" }
        Pass
        {
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite On
            Cull Back
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _DissolveTex;
            float4 _DissolveTex_ST;
            fixed4 _Color;
            fixed4 _EdgeColor;
            float _Dissolve;

            struct appdata { float4 vertex : POSITION; float3 normal : NORMAL; float2 uv : TEXCOORD0; };
            struct v2f { float4 pos : SV_POSITION; float3 normal : TEXCOORD0; float3 view : TEXCOORD1; float2 uv : TEXCOORD2; };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.normal = UnityObjectToWorldNormal(v.normal);
                o.view = WorldSpaceViewDir(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _DissolveTex);
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float pattern = tex2D(_DissolveTex, i.uv).r;
                clip(pattern - _Dissolve * 1.02);
                float3 n = normalize(i.normal);
                float facing = saturate(dot(n, normalize(i.view)));
                float rim = pow(1 - facing, 2.2);
                float light = 0.55 + 0.45 * saturate(dot(n, normalize(float3(0.4, 0.9, -0.3))));
                fixed3 colour = _Color.rgb * light + rim * (_Color.rgb * 0.5 + 0.5);
                float edge = _Dissolve > 0.001 ? 1 - smoothstep(0, 0.08, pattern - _Dissolve) : 0;
                colour = lerp(colour, _EdgeColor.rgb, edge);
                return fixed4(colour, saturate(0.8 + rim * 0.4 + edge));
            }
            ENDCG
        }
    }
}
