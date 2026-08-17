Shader "CitizenSim/PoiPillar"
{
    Properties
    {
        _BaseColor("Glow Color", Color) = (1, 0.3, 0.2, 1)
        _PulseSpeed("Pulse Speed", Float) = 2.0
        _PulseAmplitude("Pulse Amplitude", Float) = 0.25
        _HeightFade("Height Fade", Float) = 1.5
    }

    SubShader
    {
        // ZTest Always + Transparent 队列:在 Opaque(市民 2000)之后绘制且无视深度,
        // 光柱永不被市民遮挡。Additive 叠加发光。顶部按世界高度渐隐。
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "ForwardPillar"
            Tags { "LightMode" = "UniversalForward" }

            Blend One One
            ZWrite Off
            ZTest Always
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 2.0

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseColor;
                float _PulseSpeed;
                float _PulseAmplitude;
                float _HeightFade;
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
                float3 positionWS : TEXCOORD1;
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                VertexPositionInputs pos = GetVertexPositionInputs(IN.positionOS.xyz);
                VertexNormalInputs norm = GetVertexNormalInputs(IN.normalOS);
                OUT.positionCS = pos.positionCS;
                OUT.normalWS = norm.normalWS;
                OUT.positionWS = pos.positionWS;
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                float3 n = normalize(IN.normalWS);
                float3 v = normalize(GetWorldSpaceViewDir(IN.positionWS));
                // rim:柱体边缘亮,中心暗(圆柱侧视效果)
                float rim = pow(saturate(1.0 - abs(dot(n, v))), 3.0);
                float center = saturate(dot(n, v));

                // 顶部高度渐隐:底部亮、顶部消失
                float heightFade = saturate(1.0 - IN.positionWS.y * _HeightFade);

                // 呼吸脉冲
                float pulse = 1.0 + _PulseAmplitude * sin(_Time.y * _PulseSpeed);

                float3 col = _BaseColor.rgb * (rim * 1.2 + center * 0.5) * heightFade * pulse;
                return half4(col, 1.0);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
