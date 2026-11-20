// Soap bubble: bright rim, a drifting rainbow sheen and a highlight; additive.
Shader "Vcl/Bubble"
{
    Properties { }
    SubShader
    {
        Tags { "Queue"="Transparent-50" "RenderType"="Transparent" }
        Blend One One
        ZWrite Off
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"
            struct appdata { float4 vertex : POSITION; float3 normal : NORMAL; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct v2f { float4 pos : SV_POSITION; float3 n : TEXCOORD0; float3 v : TEXCOORD1; UNITY_VERTEX_OUTPUT_STEREO };
            v2f vert (appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.pos = UnityObjectToClipPos(v.vertex);
                o.n = UnityObjectToWorldNormal(v.normal);
                o.v = normalize(WorldSpaceViewDir(v.vertex));
                return o;
            }
            fixed4 frag (v2f i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float3 n = normalize(i.n), v = normalize(i.v);
                float f = 1 - abs(dot(n, v));
                float3 sheen = 0.5 + 0.5 * cos(6.2831 * (f * 1.3 + float3(0, 0.33, 0.67) + _Time.y * 0.05));
                float spec = pow(max(dot(reflect(-v, n), normalize(float3(-0.4, 0.8, -0.5))), 0), 40);
                float3 c = lerp(float3(0.75, 0.9, 1), sheen, 0.45) * pow(f, 2.5) * 0.9 + spec * 0.9;
                return fixed4(c, 1);
            }
            ENDCG
        }
    }
}
