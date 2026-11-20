// Distant towers with lit window bands (object space, metres). Vertex colour
// red: a per-tower seed. Fogged.
Shader "Vcl/Towers"
{
    Properties { }
    SubShader
    {
        Tags { "Queue"="Geometry" "RenderType"="Opaque" }
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma multi_compile_fog
            #include "UnityCG.cginc"
            struct appdata { float4 vertex : POSITION; float3 normal : NORMAL; fixed4 color : COLOR; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct v2f { float4 pos : SV_POSITION; float3 p : TEXCOORD0; float3 n : TEXCOORD1; float seed : TEXCOORD2; UNITY_FOG_COORDS(3) UNITY_VERTEX_OUTPUT_STEREO };
            v2f vert (appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.pos = UnityObjectToClipPos(v.vertex);
                o.p = v.vertex.xyz; o.n = v.normal; o.seed = v.color.r * 100;
                UNITY_TRANSFER_FOG(o, o.pos);
                return o;
            }
            float hash(float2 p) { return frac(sin(dot(p, float2(127.1, 311.7))) * 43758.5453); }
            fixed4 frag (v2f i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float3 c = float3(0.02, 0.05, 0.09);
                if (abs(i.n.y) < 0.5)
                {
                    float2 q = float2(i.p.x + i.p.z, i.p.y) * float2(1.6, 1.2);
                    float2 cell = floor(q);
                    float on = step(0.62, hash(cell + i.seed + floor(_Time.y * 0.2 + hash(cell) * 5) * 0.01));
                    float2 f = frac(q);
                    float win = step(0.2, f.x) * step(f.x, 0.8) * step(0.3, f.y) * step(f.y, 0.75);
                    c += win * on * lerp(float3(0.1, 0.75, 1), float3(0.6, 0.4, 1), hash(cell * 1.3)) * 0.9;
                }
                else c += float3(0.1, 0.6, 0.9) * 0.15;
                fixed4 col = fixed4(c, 1);
                UNITY_APPLY_FOG(i.fogCoord, col);
                return col;
            }
            ENDCG
        }
    }
}
