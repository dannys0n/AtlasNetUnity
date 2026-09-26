Shader "AtlasNet/Sample Region Fill"
{
    Properties { _Color ("Region Tint", Color) = (0.05, 0.55, 1, 0.28) }
    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" }
        Pass
        {
            Tags { "LightMode" = "SRPDefaultUnlit" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest Always
            Cull Off
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            CBUFFER_START(UnityPerMaterial)
            half4 _Color;
            CBUFFER_END
            struct Input { float3 positionOS : POSITION; };
            struct Output { float4 positionCS : SV_POSITION; };
            Output Vert(Input input)
            {
                Output output;
                output.positionCS = TransformObjectToHClip(input.positionOS);
                return output;
            }
            half4 Frag(Output input) : SV_Target { return _Color; }
            ENDHLSL
        }
    }
    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" }
        Pass
        {
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest Always
            Cull Off
            CGPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "UnityCG.cginc"
            fixed4 _Color;
            struct Input { float4 vertex : POSITION; };
            struct Output { float4 positionCS : SV_POSITION; };
            Output Vert(Input input)
            {
                Output output;
                output.positionCS = UnityObjectToClipPos(input.vertex);
                return output;
            }
            fixed4 Frag(Output input) : SV_Target { return _Color; }
            ENDCG
        }
    }
}
