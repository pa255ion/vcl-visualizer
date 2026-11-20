// Camera-facing sprites from one mesh: every sprite is four vertices at its
// centre, uv = the corner, uv2 = (width, rise speed), uv3.x = height
// (0: square). Rising sprites wrap
// between _Floor and _Ceil. _Disc: a soft round dot instead of the texture.
Shader "Vcl/Sprites"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Color ("Color", Color) = (1,1,1,1)
        _Disc ("Soft disc", Float) = 1
        _Floor ("Floor", Float) = 0
        _Ceil ("Ceiling", Float) = 12
        [Enum(UnityEngine.Rendering.BlendMode)] _Src ("Src", Float) = 1
        [Enum(UnityEngine.Rendering.BlendMode)] _Dst ("Dst", Float) = 1
    }
    SubShader
    {
        Tags { "Queue"="Transparent-60" "RenderType"="Transparent" }
        Blend [_Src] [_Dst]
        ZWrite Off Cull Off
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            fixed4 _Color;
            float _Disc, _Floor, _Ceil;
            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; float2 uv2 : TEXCOORD1; float2 uv3 : TEXCOORD2; fixed4 color : COLOR; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; fixed4 color : COLOR; UNITY_VERTEX_OUTPUT_STEREO };
            v2f vert (appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                float4 p = v.vertex;
                if (v.uv2.y != 0) p.y = _Floor + fmod(p.y - _Floor + _Time.y * v.uv2.y, _Ceil - _Floor);
                float4 view = mul(UNITY_MATRIX_V, mul(unity_ObjectToWorld, float4(p.xyz, 1)));
                float2 corner = (v.uv - 0.5) * float2(v.uv2.x, v.uv3.x > 0 ? v.uv3.x : v.uv2.x);
                view.xy += corner;
                o.pos = mul(UNITY_MATRIX_P, view);
                o.uv = v.uv;
                o.color = v.color * _Color;
                return o;
            }
            fixed4 frag (v2f i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                if (_Disc > 0.5)
                {
                    float a = smoothstep(0.5, 0.0, length(i.uv - 0.5));
                    return fixed4(i.color.rgb * a * a, a * a * i.color.a);
                }
                return tex2D(_MainTex, i.uv) * i.color;
            }
            ENDCG
        }
    }
}
