// SprayMod screen-space (deferred-style) decal for URP.
// Draws a unit cube; for each pixel it reads the camera depth texture, reconstructs the world
// position of whatever surface was rendered behind the cube, transforms it into the cube's local
// space, and (if inside the cube) stamps the spray texture onto it. This conforms the spray to the
// ACTUAL rendered geometry without needing readable meshes, colliders, or the URP Decal feature.
//
// Build this into an AssetBundle with Unity 6000.3.14f1 + URP (see decal/README.md).
Shader "SprayMod/ScreenSpaceDecal"
{
    Properties
    {
        [MainTexture] _BaseMap ("Spray Texture", 2D) = "white" {}
        [MainColor]   _Color   ("Tint (a = opacity)", Color) = (1,1,1,1)
    }

    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent+100" "RenderPipeline"="UniversalPipeline" "IgnoreProjector"="True" }

        Pass
        {
            Name "SprayScreenDecal"
            // Draw the box back faces, ignore depth, alpha blend onto the scene. This lets the cube
            // render whether the camera is inside or outside it (classic screen-space decal).
            Cull Front
            ZTest Always
            ZWrite Off
            Blend SrcAlpha OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                float4 _Color;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float4 screenPos  : TEXCOORD0;
            };

            Varyings vert (Attributes IN)
            {
                Varyings OUT;
                VertexPositionInputs vp = GetVertexPositionInputs(IN.positionOS.xyz);
                OUT.positionCS = vp.positionCS;
                OUT.screenPos  = vp.positionNDC;
                return OUT;
            }

            half4 frag (Varyings IN) : SV_Target
            {
                float2 screenUV = IN.screenPos.xy / IN.screenPos.w;

                // World position of the rendered surface behind this fragment, from the depth buffer.
                float rawDepth = SampleSceneDepth(screenUV);
                float3 worldPos = ComputeWorldSpacePosition(screenUV, rawDepth, UNITY_MATRIX_I_VP);

                // Into the decal cube's local space; reject anything outside the unit cube.
                float3 objPos = TransformWorldToObject(worldPos);
                clip(0.5 - abs(objPos));

                // Reject surfaces that don't lie roughly parallel to the decal plane. Keeps the spray
                // on the wall and OFF sticks/pucks/bodies (and the blade at glass), whose normals don't
                // match. Surface normal is reconstructed from depth via screen-space derivatives.
                float3 surfN = normalize(cross(ddy(worldPos), ddx(worldPos)));
                float3 decalN = normalize(mul((float3x3)UNITY_MATRIX_M, float3(0.0, 0.0, 1.0)));
                clip(abs(dot(surfN, decalN)) - 0.5); // 0.5 ~= within ~60 degrees of the wall

                // Cube local XY -> texture UV (projection is along local Z = surface normal).
                float2 uv = objPos.xy + 0.5;
                uv = uv * _BaseMap_ST.xy + _BaseMap_ST.zw;

                half4 col = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, uv) * _Color;
                return col;
            }
            ENDHLSL
        }
    }
    Fallback Off
}
