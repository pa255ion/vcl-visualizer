// Lit solids, surfaces and models: Standard lighting, vertex colour, an
// optional texture (glTF base colour), fade by alpha.
Shader "Vcl/Lit"
{
    Properties
    {
        _Color ("Color", Color) = (1,1,1,1)
        _MainTex ("Albedo", 2D) = "white" {}
        _Glossiness ("Smoothness", Range(0,1)) = 0.5
        _Metallic ("Metallic", Range(0,1)) = 0.05
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 2
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" }
        Cull [_Cull]
        ZWrite On
        CGPROGRAM
        #pragma surface surf Standard alpha:fade vertex:vert
        #pragma target 3.0

        fixed4 _Color;
        sampler2D _MainTex;
        half _Glossiness;
        half _Metallic;

        struct Input { float4 vcolor; float2 uv_MainTex; };

        void vert (inout appdata_full v, out Input o)
        {
            UNITY_INITIALIZE_OUTPUT(Input, o);
            o.vcolor = v.color;
        }

        void surf (Input IN, inout SurfaceOutputStandard o)
        {
            fixed4 c = _Color * IN.vcolor * tex2D(_MainTex, IN.uv_MainTex);
            o.Albedo = c.rgb;
            o.Metallic = _Metallic;
            o.Smoothness = _Glossiness;
            o.Alpha = c.a;
        }
        ENDCG
    }
    FallBack "Diffuse"
}
