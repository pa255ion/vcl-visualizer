// A glowing floor grid that fades with distance (object space, metres).
// _Pulse: a ring of light sweeping outward; _Scroll: the grid slides toward
// the viewer (synthwave).
Shader "Vcl/Grid"
{
    Properties
    {
        _Color ("Lines", Color) = (0,1,1,1)
        _Base ("Base", Color) = (0,0,0,1)
        _Cell ("Cell", Float) = 1
        _Major ("Major every", Float) = 5
        _Fade ("Fade distance", Float) = 30
        _BaseAlpha ("Base alpha", Float) = 1
        _Pulse ("Pulse", Float) = 0
        _Scroll ("Scroll speed", Float) = 0
    }
    SubShader
    {
        Tags { "Queue"="Geometry+10" "RenderType"="Transparent" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma target 3.0
            #include "UnityCG.cginc"
            fixed4 _Color, _Base;
            float _Cell, _Major, _Fade, _BaseAlpha, _Pulse, _Scroll;
            struct appdata { float4 vertex : POSITION; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct v2f { float4 pos : SV_POSITION; float3 p : TEXCOORD0; UNITY_VERTEX_OUTPUT_STEREO };
            v2f vert (appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.pos = UnityObjectToClipPos(v.vertex);
                o.p = v.vertex.xyz;
                return o;
            }
            float lines(float2 p, float w)
            {
                float2 g = abs(frac(p - 0.5) - 0.5) / fwidth(p);
                return 1 - min(min(g.x, g.y) / w, 1);
            }
            fixed4 frag (v2f i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float2 p = i.p.xz / _Cell + float2(0, _Time.y * _Scroll);
                float r = length(i.p.xz);
                float l = max(lines(p, 1.2) * 0.45, lines(p / _Major, 1.6));
                float fade = 1 - smoothstep(_Fade * 0.25, _Fade, r);
                float pulse = 0;
                if (_Pulse > 0.5) { float ring = fmod(_Time.y * 4, 40); pulse = exp(-abs(r - ring) * 1.5) * 0.8 * (1 - ring / 40); }
                float3 c = lerp(_Base.rgb, _Color.rgb, saturate(l + pulse * 0.6)) + _Color.rgb * pulse * 0.3;
                return fixed4(c, max(_BaseAlpha, l) * fade);
            }
            ENDCG
        }
    }
}
