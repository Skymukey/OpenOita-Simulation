Shader "OpenOita/CommittedFlame"
{
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent+1" "RenderPipeline"="UniversalPipeline" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Off
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            struct Input { float4 positionOS : POSITION; float2 uv : TEXCOORD0; };
            struct Output { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };
            Output vert(Input v) { Output o; o.positionCS=TransformObjectToHClip(v.positionOS.xyz); o.uv=v.uv; return o; }
            half4 frag(Output i) : SV_Target
            {
                float wave = 0.06*sin(_Time.y*8+i.uv.y*12);
                float edge = abs(i.uv.x-0.5+wave)*2;
                float alpha = saturate((1-i.uv.y-edge)*5)*0.85;
                return half4(1, lerp(0.85,0.25,i.uv.y),0.05,alpha);
            }
            ENDHLSL
        }
    }
}
