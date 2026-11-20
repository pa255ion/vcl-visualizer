// Sky dome for the scenes: a vertical gradient with a sun disc, optionally
// striped (synthwave). Drawn first, behind everything; no fog.
Shader "Vcl/Sky"
{
    Properties
    {
        _Top ("Top", Color) = (0,0,0,1)
        _Horizon ("Horizon", Color) = (0,0,0,1)
        _Bottom ("Bottom", Color) = (0,0,0,1)
        _Sun ("Sun", Color) = (1,1,1,1)
        _Sun2 ("Sun lower", Color) = (1,1,1,1)
        _SunDir ("Sun direction", Vector) = (0,0.3,1,0)
        _SunSize ("Sun size (radians)", Float) = 0
        _Glow ("Glow", Float) = 0
        _Stripes ("Stripes", Float) = 0
        _Band ("Band", Float) = 0.25
    }
    SubShader
    {
        Tags { "Queue"="Background" "RenderType"="Background" "PreviewType"="Skybox" }
        Cull Front ZWrite Off
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"
            fixed4 _Top, _Horizon, _Bottom, _Sun, _Sun2;
            float4 _SunDir;
            float _SunSize, _Glow, _Stripes, _Band;
            struct appdata { float4 vertex : POSITION; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct v2f { float4 pos : SV_POSITION; float3 dir : TEXCOORD0; UNITY_VERTEX_OUTPUT_STEREO };
            v2f vert (appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.pos = UnityObjectToClipPos(v.vertex);
                o.dir = v.vertex.xyz;
                return o;
            }
            fixed4 frag (v2f i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float3 d = normalize(i.dir);
                float h = d.y;
                float3 c = h > 0 ? lerp(_Horizon.rgb, _Top.rgb, pow(smoothstep(0, 1, h / _Band * 0.25 + h), 0.7))
                                 : lerp(_Horizon.rgb, _Bottom.rgb, smoothstep(0, 0.15, -h));
                float3 sd = normalize(_SunDir.xyz);
                float a = acos(clamp(dot(d, sd), -1, 1));
                c += _Sun.rgb * _Glow * exp(-a * 6) * 0.6;
                if (_SunSize > 0 && a < _SunSize)
                {
                    float v = (d.y - (sd.y - _SunSize)) / (2 * _SunSize);
                    float3 s = lerp(_Sun2.rgb, _Sun.rgb, saturate(v));
                    float edge = smoothstep(_SunSize, _SunSize * 0.97, a);
                    if (_Stripes > 0.5 && v < 0.55)
                    {
                        float band = frac(v * 14 - _Time.y * 0.15);
                        edge *= step(lerp(0.08, 0.55, 1 - v / 0.55), band);
                    }
                    c = lerp(c, s, edge);
                }
                return fixed4(c, 1);
            }
            ENDCG
        }
    }
}
