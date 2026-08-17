Shader "CitizenSim/PoiGlow"
{
    Properties
    {
        _BaseColor("Glow Color", Color) = (1, 0.3, 0.2, 1)
        _PulseSpeed("Pulse Speed", Float) = 2.0
        _PulseAmplitude("Pulse Amplitude", Float) = 0.35
        _RimPower("Rim Power", Float) = 2.0
    }

    SubShader
    {
        // ZTest Always + Transparent 队列:在 Opaque(市民 2000)之后绘制且无视深度,
        // POI 永不被市民遮挡,始终可见。Additive 叠加发光。
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "ForwardGlow"
            Tags { "LightMode" = "UniversalForward" }

            Blend One One
            ZWrite Off
            ZTest Always
            Cull Back

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 2.0

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseColor;
                float _PulseSpeed;
                float _PulseAmplitude;
                float _RimPower;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 normalWS : TEXCOORD0;
                float3 viewDirWS : TEXCOORD1;
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                VertexPositionInputs pos = GetVertexPositionInputs(IN.positionOS.xyz);
                VertexNormalInputs norm = GetVertexNormalInputs(IN.normalOS);
                OUT.positionCS = pos.positionCS;
                OUT.normalWS = norm.normalWS;
                OUT.viewDirWS = GetWorldSpaceViewDir(pos.positionWS);
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                float3 n = normalize(IN.normalWS);
                float3 v = normalize(IN.viewDirWS);
                // rim:边缘亮(垂直视角面暗),球体边缘发光感
                float rim = pow(saturate(1.0 - abs(dot(n, v))), _RimPower);
                // 中心也有一点亮度,让球体整体可见
                float center = saturate(dot(n, v));

                // 呼吸脉冲
                float pulse = 1.0 + _PulseAmplitude * sin(_Time.y * _PulseSpeed);

                float3 col = _BaseColor.rgb * (rim * 1.5 + center * 0.35) * pulse;
                return half4(col, 1.0);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
