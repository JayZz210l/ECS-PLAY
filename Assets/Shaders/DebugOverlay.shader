Shader "CitizenSim/DebugOverlay"
{
    Properties
    {
        _Color("Overlay Color", Color) = (0, 1, 0, 0.4)
    }

    SubShader
    {
        // 透明叠加:贴地调试网格/障碍物覆盖。ZTest LEqual(被实体遮挡部分隐藏,可见部分半透明)。
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "ForwardOverlay"
            Tags { "LightMode" = "UniversalForward" }

            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 2.0

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _Color;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float4 color : COLOR;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float4 color : TEXCOORD0;
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                OUT.positionCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.color = IN.color;
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                // 顶点色 * 材质色(顶点色为 source of truth,材质色作全局乘数)。
                return IN.color * _Color;
            }
            ENDHLSL
        }
    }
    FallBack Off
}
