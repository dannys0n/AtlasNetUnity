Shader "AtlasNet/M2/Video Text Blur"
{
    Properties
    {
        [PerRendererData] _MainTex ("Video", 2D) = "black" {}
        _Tint ("Dark tint", Color) = (0.015,0.025,0.04,0.45)
        _BlurRadius ("Blur radius in source pixels", Float) = 5
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "IgnoreProjector"="True" "RenderType"="Transparent" }
        Cull Off ZWrite Off ZTest [unity_GUIZTestMode]
        Blend SrcAlpha OneMinusSrcAlpha
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; fixed4 color : COLOR; };
            struct v2f { float4 vertex : SV_POSITION; float2 uv : TEXCOORD0; fixed4 color : COLOR; };
            sampler2D _MainTex;
            float4 _MainTex_TexelSize;
            fixed4 _Tint;
            float _BlurRadius;
            v2f vert(appdata v) { v2f o; o.vertex=UnityObjectToClipPos(v.vertex); o.uv=v.uv; o.color=v.color; return o; }
            fixed4 frag(v2f i) : SV_Target
            {
                float2 d=_MainTex_TexelSize.xy*_BlurRadius;
                fixed3 c=tex2D(_MainTex,i.uv).rgb*4;
                c+=(tex2D(_MainTex,i.uv+float2(d.x,0)).rgb+tex2D(_MainTex,i.uv-float2(d.x,0)).rgb+tex2D(_MainTex,i.uv+float2(0,d.y)).rgb+tex2D(_MainTex,i.uv-float2(0,d.y)).rgb)*2;
                c+=tex2D(_MainTex,i.uv+d).rgb+tex2D(_MainTex,i.uv-d).rgb+tex2D(_MainTex,i.uv+float2(d.x,-d.y)).rgb+tex2D(_MainTex,i.uv+float2(-d.x,d.y)).rgb;
                return fixed4(lerp(c/16,_Tint.rgb,_Tint.a)*i.color.rgb,i.color.a);
            }
            ENDCG
        }
    }
}
