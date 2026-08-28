Shader "AiTown/VertexColorLit"
{
    Properties
    {
        _BaseColor ("Base Color", Color) = (1,1,1,1)
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" "Queue"="Geometry" }
        Pass
        {
            Name "ForwardLit"
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float4 color      : COLOR;
            };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 normalWS   : TEXCOORD0;
                float4 color      : TEXCOORD1;
            };

            CBUFFER_START(UnityPerMaterial)
            half4 _BaseColor;
            CBUFFER_END

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                OUT.positionCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.normalWS = TransformObjectToWorldNormal(IN.normalOS);
                OUT.color = IN.color;
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                return half4(1, 0, 0, 1); // DEBUG: 常量红
                half3 n = normalize(IN.normalWS + half3(0.0001, 0.0001, 0.0001));
                half3 albedo = _BaseColor.rgb * IN.color.rgb;
                // 环境：SH 余弦带（方向性环境光），下限防黑
                half4 n4 = half4(n, 1.0);
                half3 ambient = max(half3(dot(unity_SHAr, n4), dot(unity_SHAg, n4), dot(unity_SHAb, n4)), half3(0.25, 0.25, 0.25));
                // 主光：方向兜底防 NaN（编辑态全局可能未初始化为0）
                float3 Ldir = _MainLightPosition.xyz + half3(0.001, 0.001, 0.001);
                float3 L = normalize(Ldir);
                half ndl = saturate(dot(n, L));
                half3 col = albedo * (ambient + _MainLightColor.rgb * ndl * 0.9h);
                return half4(col, 1);
            }
            ENDHLSL
        }
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode"="ShadowCaster" }
            ZWrite On ZTest LEqual ColorMask 0
            HLSLPROGRAM
            #pragma vertex ShadowVert
            #pragma fragment ShadowFrag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct ShadowAttrs { float4 positionOS : POSITION; float3 normalOS : NORMAL; };
            struct ShadowVars { float4 positionCS : SV_POSITION; };

            float3 _LightDirection;
            float4 _ShadowBias;

            ShadowVars ShadowVert(ShadowAttrs v)
            {
                ShadowVars o;
                float3 posWS = TransformObjectToWorld(v.positionOS.xyz);
                float invNdotL = 1.0 - saturate(dot(_LightDirection, v.normalOS));
                float scale = invNdotL * _ShadowBias.y;
                posWS = _LightDirection * _ShadowBias.x + posWS;
                posWS = v.normalOS * scale + posWS;
                o.positionCS = TransformWorldToHClip(posWS);
                #if UNITY_REVERSED_Z
                o.positionCS.z = min(o.positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #else
                o.positionCS.z = max(o.positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #endif
                return o;
            }
            half4 ShadowFrag(ShadowVars i) : SV_Target { return 0; }
            ENDHLSL
        }
    }
}