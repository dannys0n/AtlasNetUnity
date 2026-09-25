Shader "AtlasNet/Sample Cube Tint"
{
    Properties { _Color ("Cube Color", Color) = (1, 1, 1, 1) }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }
        Pass
        {
            Tags { "LightMode" = "SRPDefaultUnlit" }
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
}
