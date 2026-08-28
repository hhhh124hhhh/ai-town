Shader "AiTown/UnlitVC"
{
    Properties
    {
        [MainTexture] _BaseMap("Texture", 2D) = "white" {}
        [MainColor] _BaseColor("Color", Color) = (1, 1, 1, 1)
        _Cutoff("AlphaCutout", Range(0.0, 1.0)) = 0.5

        _Surface("__surface", Float) = 0.0
        _Blend("__mode", Float) = 0.0
        _Cull("__cull", Float) = 2.0
        [ToggleUI] _AlphaClip("__clip", Float) = 0.0
        [HideInInspector] _BlendOp("__blendop", Float) = 0.0
        [HideInInspector] _SrcBlend("__src", Float) = 1.0
        [HideInInspector] _DstBlend("__dst", Float) = 0.0
        [HideInInspector] _SrcBlendAlpha("__srcA", Float) = 1.0
        [HideInInspector] _DstBlendAlpha("__dstA", Float) = 0.0
        [HideInInspector] _ZWrite("__zw", Float) = 1.0
        [HideInInspector] _AlphaToMask("__alphaToMask", Float) = 0.0
        _QueueOffset("Queue offset", Float) = 0.0
        [HideInInspector] _MainTex("BaseMap", 2D) = "white" {}
        [HideInInspector] _Color("Base Color", Color) = (1, 1, 1, 1)
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "IgnoreProjector" = "True"
            "UniversalMaterialType" = "Unlit"
            "RenderPipeline" = "UniversalPipeline"
        }
        LOD 100

        Blend [_SrcBlend][_DstBlend], [_SrcBlendAlpha][_DstBlendAlpha]
        ZWrite [_ZWrite]
        Cull [_Cull]

        // ─── GBuffer（Deferred 主路径，顶点色）───
        Pass
        {
            Name "GBuffer"
            Tags { "LightMode" = "UniversalGBuffer" }
            HLSLPROGRAM
            #pragma target 2.5
            #pragma vertex GVert
            #pragma fragment GFrag
            #pragma shader_feature_local_fragment _ALPHATEST_ON
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/UnityGBuffer.hlsl"

            TEXTURE2D(_BaseMap); SAMPLER(sampler_BaseMap);
            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                half4 _BaseColor;
                half _Cutoff;
                half _Surface;
            CBUFFER_END

            struct GAttrs
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                float3 normalOS : NORMAL;
                float4 vcolor : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };
            struct GVars
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float4 vcolor : TEXCOORD2;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            GVars GVert(GAttrs input)
            {
                GVars output = (GVars)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                output.vcolor = input.vcolor;
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                return output;
            }

            FragmentOutput GFrag(GVars input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                half4 texColor = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv);
                half3 albedo = texColor.rgb * _BaseColor.rgb * input.vcolor.rgb;
                half alpha = texColor.a * _BaseColor.a;

                InputData inputData = (InputData)0;
                inputData.normalWS = NormalizeNormalPerPixel(input.normalWS);
                inputData.positionWS = float3(0, 0, 0);
                inputData.viewDirectionWS = half3(0, 0, 1);
                inputData.shadowCoord = 0;
                inputData.fogCoord = 0;
                inputData.vertexLighting = half3(0, 0, 0);
                inputData.bakedGI = half3(0, 0, 0);
                inputData.normalizedScreenSpaceUV = 0;
                inputData.shadowMask = half4(1, 1, 1, 1);

                SurfaceData surfaceData = (SurfaceData)0;
                surfaceData.albedo = albedo;
                surfaceData.alpha = alpha;
                surfaceData.occlusion = 1;
                return SurfaceDataToGbuffer(surfaceData, inputData, float3(0,0,0), kLightingInvalid);
            }
            ENDHLSL
        }

        // ─── Forward（Forward 渲染路径/前向物体，顶点色，简易光照）───
        Pass
        {
            Name "UnlitVCForward"
            HLSLPROGRAM
            #pragma target 2.5
            #pragma vertex FVert
            #pragma fragment FFrag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_BaseMap); SAMPLER(sampler_BaseMap);
            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                half4 _BaseColor;
                half _Cutoff;
                half _Surface;
            CBUFFER_END

            struct FAttrs
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float4 vcolor : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };
            struct FVars
            {
                float4 positionCS : SV_POSITION;
                float3 normalWS : TEXCOORD0;
                float4 vcolor : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            FVars FVert(FAttrs input)
            {
                FVars output = (FVars)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.vcolor = input.vcolor;
                return output;
            }

            half4 FFrag(FVars input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                half3 n = normalize(input.normalWS + half3(0.0001, 0.0001, 0.0001));
                half3 albedo = _BaseColor.rgb * input.vcolor.rgb;
                half4 n4 = half4(n, 1.0);
                half3 ambient = max(half3(dot(unity_SHAr, n4), dot(unity_SHAg, n4), dot(unity_SHAb, n4)), half3(0.25, 0.25, 0.25));
                float3 L = normalize(_MainLightPosition.xyz + half3(0.001, 0.001, 0.001));
                half ndl = saturate(dot(n, L));
                half3 col = albedo * (ambient + _MainLightColor.rgb * ndl * 0.9h);
                return half4(col, 1);
            }
            ENDHLSL
        }

        // ─── ShadowCaster ───
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            ZWrite On ZTest LEqual ColorMask 0
            HLSLPROGRAM
            #pragma target 2.5
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