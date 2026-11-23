// Lake water (world space, metres): small waves, the sky's colours by the
// Fresnel term, and the sun's glitter path. The twin of waterMaterial in
// viewer/js/scenery.js. Fogged.
Shader "Vcl/Water"
{
    Properties
    {
        _Deep ("Deep", Color) = (0.11, 0.26, 0.35, 1)
        _Horizon ("Horizon", Color) = (1, 0.79, 0.58, 1)
        _Top ("Top", Color) = (0.29, 0.47, 0.71, 1)
        _Sun ("Sun", Color) = (1, 0.94, 0.78, 1)
        _SunDir ("Sun direction", Vector) = (-0.33, 0.07, 0.94, 0)
    }
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
            fixed4 _Deep, _Horizon, _Top, _Sun;
            float4 _SunDir;
            struct appdata { float4 vertex : POSITION; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct v2f { float4 pos : SV_POSITION; float3 w : TEXCOORD0; UNITY_FOG_COORDS(1) UNITY_VERTEX_OUTPUT_STEREO };
            v2f vert (appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.pos = UnityObjectToClipPos(v.vertex);
                o.w = mul(unity_ObjectToWorld, v.vertex).xyz;
                UNITY_TRANSFER_FOG(o, o.pos);
                return o;
            }
            void wave(float2 p, float2 k, float a, float s, inout float2 g) { g += a * k * cos(dot(k, p) + _Time.y * s); }
            fixed4 frag (v2f i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                // The browser's z is this app's -z: mirror the wave directions so both lakes move alike.
                float2 p = float2(i.w.x, -i.w.z), g = 0;
                float dist = length(_WorldSpaceCameraPos - i.w);
                wave(p, float2(0.9, 0.35), 0.018, 1.1, g);
                wave(p, float2(-0.5, 1.2), 0.014, 1.4, g);
                wave(p, float2(2.1, -1.3), 0.008, 2.3, g);
                wave(p, float2(-3.3, -2.2), 0.005, 3.1, g);
                wave(p, float2(4.7, 3.1), 0.003, 4.2, g);
                g *= 1 / (1 + dist * 0.12);
                float3 n = normalize(float3(-g.x, 1, g.y));
                float3 v = normalize(_WorldSpaceCameraPos - i.w);
                float fres = 0.08 + 0.9 * pow(1 - max(dot(n, v), 0), 4);
                float3 r = reflect(-v, n);
                float3 sky = lerp(_Horizon.rgb, _Top.rgb, saturate(r.y * 2.5));
                float3 c = lerp(_Deep.rgb, sky, fres);
                float sd = max(dot(r, normalize(_SunDir.xyz)), 0);
                c += _Sun.rgb * (pow(sd, 400) * 4 + pow(sd, 40) * 0.35);
                fixed4 col = fixed4(c, 1);
                UNITY_APPLY_FOG(i.fogCoord, col);
                return col;
            }
            ENDCG
        }
    }
}
