// Map buildings: dark navy walls, lighter roofs, a little shading by direction,
// fading into the scene's fog with distance. Both sides drawn, so wall winding doesn't matter.
Shader "Hololoco/Building"
{
    Properties
    {
        _Color ("Wall colour", Color) = (0.11, 0.15, 0.23, 1)
        _Top ("Roof colour", Color) = (0.2, 0.27, 0.38, 1)
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" }
        Pass
        {
            Cull Off
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fog
            #include "UnityCG.cginc"

            fixed4 _Color;
            fixed4 _Top;

            struct appdata { float4 vertex : POSITION; float3 normal : NORMAL; };
            struct v2f { float4 pos : SV_POSITION; float3 normal : TEXCOORD0; UNITY_FOG_COORDS(1) };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.normal = UnityObjectToWorldNormal(v.normal);
                UNITY_TRANSFER_FOG(o, o.pos);
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float3 n = normalize(i.normal);
                float up = saturate(n.y);
                float side = 0.75 + 0.25 * abs(dot(n, normalize(float3(0.6, 0, 0.8))));
                fixed4 colour = lerp(_Color * side, _Top, up);
                UNITY_APPLY_FOG(i.fogCoord, colour);
                return colour;
            }
            ENDCG
        }
    }
}
