Shader "MF_SSGI/Denoise"
{
    Properties
    {
        _MainTex("Texture", 2D) = "white" {}
    }


    SubShader
    {
        ZTest Always
        ZWrite Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #pragma multi_compile _ _ENCODE_LIGHTDIR

            #include "UnityCG.cginc"
            #include "SSGI.hlsl"

            extern int _do_edge_detect;
            //_do_encode_lightdir replaced by _ENCODE_LIGHTDIR keyword
            extern int _ssgi_samples = 0;
            extern float2 _oneover_denoise_res = 0.1;
            extern float2 _ssgi_res;
            
            extern float _denoise_max_depth_diff = 0.1;
            extern float _denoise_min_dot_match = 0.4;
            extern float _denoise_max_dot_match = 0.8;

            extern float _denoise_energy_compensation = 1.5;
            extern float _denoise_shadow_compensation = 1.5;
            extern int _denoise_min_color_hit_count = 2;
            extern int _denoise_min_shadows_hit_count = 2;
            extern float _denoise_pixel_size = 1.0;

            extern float _denoise_color_normal_contribution = 0.1;
            extern float _denoise_shadow_normal_contribution = 0.35;

            //Shadow bilateral similarity sharpness.
            //Higher = neighbors with shadow values different from center are rejected harder.
            //Range ~3 (soft) to ~12 (very crisp). Default 6.
            extern float _denoise_shadow_similarity = 6.0;


            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float2 uv : TEXCOORD0;
                float4 vertex : SV_POSITION;
            };

            extern sampler2D _MF_SSGI_SSGIObjects;
            extern float4 _MF_SSGI_SSGIObjects_ST;

            extern sampler2D _MainTex;
            extern float4 _MainTex_ST;

            v2f vert(appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                return o;
            }

            //sampleShadow=false skips shadow accumulation. Used for corner taps so the shadow
            //channel uses only a 5-tap cross (sharper) while color uses the full 9-tap kernel.
            //Shadow also uses stricter normal/depth thresholds, a luminance-similarity bilateral
            //weight, and squared falloff to keep shadow silhouettes crisp through the pyramid blur.
            void SampleEnvironment(inout float3 resultColor, inout float3 resultLightDir, inout float resultShadow, float2 uv, float falloff, float localDepth, float depthDiffBias, float3 localNormal, float centerShadow, inout float colorContribution, inout float shadowContribution, inout int shadowPixelsFound, bool sampleShadow) {
                //Skip: Out of screen
                if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0) {
                    return;
                }

                //Skip: Depth difference too great (color)
                float4 envNormalsAndDepth = tex2D(_MF_SSGI_Normals_LQ, uv);
                float depthDiff = abs(envNormalsAndDepth.a - localDepth);
                if (depthDiff > depthDiffBias) {
                    return;
                }

                //Skip & blend: If normals match
                float dotMatch = dot(envNormalsAndDepth.xyz, localNormal);
                float normalMatch = saturate((dotMatch - _denoise_min_dot_match) / (_denoise_max_dot_match - _denoise_min_dot_match));
                colorContribution += falloff * lerp(normalMatch, 1.0, _denoise_color_normal_contribution);

                //Stricter shadow rejection: tighter normal AND tighter depth threshold
                float shadowDotMin = lerp(_denoise_min_dot_match, _denoise_max_dot_match, 0.5);
                bool shadowGeomAccept = sampleShadow && dotMatch > shadowDotMin && depthDiff < depthDiffBias * 0.5;
                float shadowFalloff = falloff * falloff; //Squared: center-weights the shadow

                if (dotMatch > _denoise_min_dot_match) {
                    //See if total masked values exceed minimum value, otherwise don't bother adding
                    float intensity = falloff * normalMatch * _denoise_energy_compensation;
                    float4 value = tex2D(_MainTex, uv);

                    //Color & Normal
                    #ifdef _ENCODE_LIGHTDIR
                        resultColor += DecodeFloat2ToColor(value.rg) * intensity;
                        resultLightDir += DecodeFloatToNormal(value.b) * intensity;
                    #else
                        resultColor += value.rgb * intensity;
                    #endif

                    if (shadowGeomAccept) {
                        //Bilateral similarity: neighbors with shadow values close to centerShadow are
                        //weighted in, divergent neighbors are downweighted. This is what preserves
                        //shadow silhouettes through wide-radius blur passes.
                        float similarity = saturate(1.0 - abs(value.a - centerShadow) * _denoise_shadow_similarity);
                        float w = shadowFalloff * similarity;
                        shadowContribution += w * lerp(normalMatch, 1.0, _denoise_shadow_normal_contribution);
                        resultShadow = min(2.0, resultShadow + (value.a * w * _denoise_shadow_compensation));
                        if (value.a > 0.0 && similarity > 0.5) {
                            shadowPixelsFound++;
                        }
                    }
                }
            }

            //Returns the center shadow value (pre-compensation) so neighbor taps can
            //bilateral-compare against it.
            float SampleEnvironmentSimple(inout float3 resultColor, inout float3 resultLightDir, inout float resultShadow, float2 uv, inout int shadowPixelsFound) {
                //Skip: Out of screen
                if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0) {
                    return 0.0;
                }

                //See if total masked values exceed minimum value, otherwise don't bother adding
                float4 value = tex2D(_MainTex, uv);

                //Color & Normal
                #ifdef _ENCODE_LIGHTDIR
                    resultColor += DecodeFloat2ToColor(value.rg) * _denoise_energy_compensation;
                    resultLightDir += DecodeFloatToNormal(value.b);
                #else
                    resultColor += value.rgb * _denoise_energy_compensation;
                #endif

                //Shadows
                resultShadow = min(2.0, resultShadow + (value.a * _denoise_shadow_compensation));
                if (value.a > 0.0) {
                    shadowPixelsFound++;
                }
                return value.a;
            }

            float4 frag(v2f i) : SV_Target {
                float3 resultColor = 0.0;
                float3 resultLightDir = 0.0;
                float resultShadow = 0.0;

                //Cache values
                float4 normalsAndDepth = tex2D(_MF_SSGI_Normals_LQ, i.uv);
                float3 localNormal = normalsAndDepth.xyz;
                float localDepth = normalsAndDepth.a;

                //Skip: If we are trying to light the skybox
                float depthThreshold = _ProjectionParams.z * 0.95;
                if (localDepth > depthThreshold) {
                    return 0.0;
                }

                //Cache values
                float depthDiffBias = localDepth * _denoise_max_depth_diff;
                float2 offset = float2(_oneover_denoise_res.x * _denoise_pixel_size, _oneover_denoise_res.y * _denoise_pixel_size);

                //Offsets
                float2 offset0 = float2(-offset.x, -offset.y);
                float2 offset1 = float2(-offset.x, offset.y);
                float2 offset2 = float2(offset.x, -offset.y);
                float2 offset3 = float2(offset.x, offset.y);
                
                float2 offset4 = float2(-offset.x, 0.0);
                float2 offset5 = float2(offset.x, 0.0);
                float2 offset6 = float2(0.0, -offset.y);
                float2 offset7 = float2(0.0, offset.y);

                int totalColorPixelsFound = 0;
                int totalShadowPixelsFound = 0;

                //Center — also gives us the shadow value used for bilateral similarity in neighbors
                float colorContribution = 1.0;
                float shadowContribution = 1.0;
                float falloff = 0.35;
                float centerShadow = SampleEnvironmentSimple(resultColor, resultLightDir, resultShadow, i.uv, totalShadowPixelsFound);

                //Corners (color only - shadow skipped for crisper edges)
                falloff = 0.65f;
                SampleEnvironment(resultColor, resultLightDir, resultShadow, i.uv + offset0, falloff, localDepth, depthDiffBias, localNormal, centerShadow, colorContribution, shadowContribution, totalShadowPixelsFound, false);
                SampleEnvironment(resultColor, resultLightDir, resultShadow, i.uv + offset1, falloff, localDepth, depthDiffBias, localNormal, centerShadow, colorContribution, shadowContribution, totalShadowPixelsFound, false);
                SampleEnvironment(resultColor, resultLightDir, resultShadow, i.uv + offset2, falloff, localDepth, depthDiffBias, localNormal, centerShadow, colorContribution, shadowContribution, totalShadowPixelsFound, false);
                SampleEnvironment(resultColor, resultLightDir, resultShadow, i.uv + offset3, falloff, localDepth, depthDiffBias, localNormal, centerShadow, colorContribution, shadowContribution, totalShadowPixelsFound, false);

                //Cross (full 5-tap shadow kernel: center + 4 cross)
                falloff = 1.0;
                SampleEnvironment(resultColor, resultLightDir, resultShadow, i.uv + offset4, falloff, localDepth, depthDiffBias, localNormal, centerShadow, colorContribution, shadowContribution, totalShadowPixelsFound, true);
                SampleEnvironment(resultColor, resultLightDir, resultShadow, i.uv + offset5, falloff, localDepth, depthDiffBias, localNormal, centerShadow, colorContribution, shadowContribution, totalShadowPixelsFound, true);
                SampleEnvironment(resultColor, resultLightDir, resultShadow, i.uv + offset6, falloff, localDepth, depthDiffBias, localNormal, centerShadow, colorContribution, shadowContribution, totalShadowPixelsFound, true);
                SampleEnvironment(resultColor, resultLightDir, resultShadow, i.uv + offset7, falloff, localDepth, depthDiffBias, localNormal, centerShadow, colorContribution, shadowContribution, totalShadowPixelsFound, true);

                //Avarage out
                resultColor /= colorContribution;
                resultShadow /= shadowContribution;

                //Cancel shadows if hitcount isn't high enough
                if (totalShadowPixelsFound < _denoise_min_shadows_hit_count) {
                    resultShadow = 0.0; 
                }

                #ifdef _ENCODE_LIGHTDIR
                    return float4(EncodeColorToFloat2(resultColor), EncodeNormalToFloat(normalize(resultLightDir)), resultShadow);
                #else
                    return float4(resultColor, resultShadow);
                #endif
            }
            ENDCG
        }
    }
}
