Shader "MF_SSGI/FinalBlit"
{
    SubShader
    {
        ZWrite Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #pragma multi_compile _ _ENCODE_LIGHTDIR
            #pragma multi_compile _ _USE_DEFERRED
            #pragma multi_compile _ _USE_SSGI_OBJECTS

            #include "UnityCG.cginc"
            #include "SSGI.hlsl"

            extern float _debug_screen_coverage = 1.0;
            extern int _debug_motion_vectors = 0;
            extern int _debug_albedo;

            //_use_ssgi_objects replaced by _USE_SSGI_OBJECTS keyword
            extern float _max_output_energy;
            //_do_encode_lightdir replaced by _ENCODE_LIGHTDIR keyword

            extern float _ssgi_range_min;
            extern float _ssgi_range_max;
            
            extern float3 _composit_color = 1.0; 
            extern float _composit_gi_contrast = 1.0;
            extern float _composit_gi_saturate = 0.0;
            extern float _composit_gi_vibrance = 0.0;

            extern float _composit_final_contrast = 1.0;
            extern float _composit_final_intensity = 1.0;
            extern float _composit_occlusion_intensity = 0.0;
            extern float _composit_lightdir_influence = 0.5;
            extern float _composit_lightdir_normal_boost;
            extern float4 _light_direction_info_a;
            extern float4 _light_direction_info_b;

            extern float2 _ssgi_res;
            extern int _aa_debug_edge_detect = 0;
            extern int _aa_quality_level = 2;
            extern float2 _aa_sample_distance;
            extern float _aa_normal_match_threshold = 0.9;
            extern float _aa_edge_detect_dot_theshold = 0.25;
            extern float _aa_edge_detect_depth_theshold = 0.1;
            
            extern float _denoise_min_dot_match = 0.6;
            extern float _denoise_max_dot_match = 0.8;
            extern float _denoise_max_depth_diff = 0.25;

            //Shadow boosting
            extern float _shadow_intensity = 1;
            extern float3 _shadow_boost_tint;
            extern float _shadow_boost_exp;
            extern float _shadow_lambert_influence = 0.75;

            //Samplers
            extern sampler2D _MF_SSGI_Denoised_Final;
            extern float4 _MF_SSGI_Denoised_Final_ST;

            extern sampler2D _MF_SSGI_SSGIObjects;
            extern float4 _MF_SSGI_SSGIObjects_ST;

            extern sampler2D _CameraDepthTexture;
            extern float4 _CameraDepthTexture_ST;
            
            extern sampler2D _MotionVectorTexture;
            extern float4 _MotionVectorTexture_ST;

            //extern sampler2D _GBuffer1;
            //extern float4 _GBuffer1_ST;

            struct appdata
            {
                float2 uv : TEXCOORD0;
                float4 vertex : POSITION;
            };

            struct v2f
            {
                float2 uv : TEXCOORD0;
                float4 vertex : SV_POSITION;
                float depth : SV_Depth;
            };

            v2f vert (appdata v)
            {
                float2 uv = TRANSFORM_TEX(v.uv, _MF_SSGI_ScreenCapture);
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = uv;
                o.depth = 0.0;
                return o;
            }

            void SampleLQvsHQSSGI(inout float3 color, inout float3 lightDir, inout float shadow, inout float contribution, float3 hqNormal, float2 uv, float falloff) {
                if (dot(hqNormal, tex2D(_MF_SSGI_Normals_LQ, uv).xyz) > _aa_normal_match_threshold) { //High vs Low quality normal-map compare
                    float4 value = tex2D(_MF_SSGI_Denoised_Final, uv);
                    #ifdef _ENCODE_LIGHTDIR
                        color += DecodeFloat2ToColor(value.rg) * falloff;
                        lightDir += DecodeFloatToNormal(value.b) * falloff;
                    #else
                        color += value.rgb * falloff;
                    #endif
                    shadow += value.a * falloff;
                    contribution += falloff;
                }
            }

            void SampleSimpleSSGI(inout float3 color, inout float3 lightDir, inout float shadow, float2 uv) {
                float4 value = tex2D(_MF_SSGI_Denoised_Final, uv);
                #ifdef _ENCODE_LIGHTDIR
                    color = DecodeFloat2ToColor(value.rg);
                    lightDir = DecodeFloatToNormal(value.b);
                #else
                    color = value.rgb;
                #endif
                shadow = value.a;
            }

            //Joint bilateral upscale of low-res SSGI (_MF_SSGI_Denoised_Final at _ssgi_res)
            //to full-res, guided by full-res normal+depth. Each of the 4 surrounding LQ texels
            //is weighted by standard bilinear AND by geometric similarity (normal dot, depth diff).
            //Mismatched-geometry taps are downweighted, preventing low-res bleeding across edges.
            //Cost: 8 tex samples (4 LQ normals + 4 LQ SSGI). Replaces the bilinear-blend bleed.
            void JointBilateralUpscaleSSGI(inout float3 color, inout float3 lightDir, inout float shadow, float2 uv, float3 hqNormal, float hqDepth) {
                //Find the 4 LQ texel centers surrounding uv
                float2 lqCoord = uv * _ssgi_res - 0.5;
                float2 lqFloor = floor(lqCoord);
                float2 lqFrac = lqCoord - lqFloor;

                float2 invLQRes = 1.0 / _ssgi_res;
                float2 tap0 = (lqFloor + float2(0.5, 0.5)) * invLQRes;
                float2 tap1 = (lqFloor + float2(1.5, 0.5)) * invLQRes;
                float2 tap2 = (lqFloor + float2(0.5, 1.5)) * invLQRes;
                float2 tap3 = (lqFloor + float2(1.5, 1.5)) * invLQRes;

                //Standard bilinear weights
                float bw0 = (1.0 - lqFrac.x) * (1.0 - lqFrac.y);
                float bw1 = lqFrac.x * (1.0 - lqFrac.y);
                float bw2 = (1.0 - lqFrac.x) * lqFrac.y;
                float bw3 = lqFrac.x * lqFrac.y;

                //Sharper geometry rejection at deeper pixels (depth tolerance scales with distance)
                float depthDiffScale = 4.0 / max(hqDepth, 1e-3);

                float3 colorAccum = 0.0;
                float3 lightDirAccum = 0.0;
                float shadowAccum = 0.0;
                float wAccum = 0.0;

                //Manually unrolled 4 taps. Each: sample LQ normal+depth -> weight -> sample SSGI -> accumulate
                #define MFSSGI_JBU_TAP(uvT, bwT) { \
                    float4 lqND = tex2D(_MF_SSGI_Normals_LQ, uvT); \
                    float nSim = saturate(dot(lqND.xyz, hqNormal)); \
                    nSim *= nSim; nSim *= nSim; \
                    float dSim = saturate(1.0 - abs(lqND.a - hqDepth) * depthDiffScale); \
                    float w = bwT * nSim * dSim; \
                    float4 v = tex2D(_MF_SSGI_Denoised_Final, uvT); \
                    /* color */ \
                    colorAccum += /*DECODED_COLOR*/ v.rgb * w; \
                    shadowAccum += v.a * w; \
                    wAccum += w; \
                }
                #ifdef _ENCODE_LIGHTDIR
                    //Encoded path - decode each tap before weighting
                    {
                        float4 lqND = tex2D(_MF_SSGI_Normals_LQ, tap0);
                        float nSim = saturate(dot(lqND.xyz, hqNormal)); nSim *= nSim; nSim *= nSim;
                        float dSim = saturate(1.0 - abs(lqND.a - hqDepth) * depthDiffScale);
                        float w = bw0 * nSim * dSim;
                        float4 v = tex2D(_MF_SSGI_Denoised_Final, tap0);
                        colorAccum += DecodeFloat2ToColor(v.rg) * w;
                        lightDirAccum += DecodeFloatToNormal(v.b) * w;
                        shadowAccum += v.a * w; wAccum += w;
                    }
                    {
                        float4 lqND = tex2D(_MF_SSGI_Normals_LQ, tap1);
                        float nSim = saturate(dot(lqND.xyz, hqNormal)); nSim *= nSim; nSim *= nSim;
                        float dSim = saturate(1.0 - abs(lqND.a - hqDepth) * depthDiffScale);
                        float w = bw1 * nSim * dSim;
                        float4 v = tex2D(_MF_SSGI_Denoised_Final, tap1);
                        colorAccum += DecodeFloat2ToColor(v.rg) * w;
                        lightDirAccum += DecodeFloatToNormal(v.b) * w;
                        shadowAccum += v.a * w; wAccum += w;
                    }
                    {
                        float4 lqND = tex2D(_MF_SSGI_Normals_LQ, tap2);
                        float nSim = saturate(dot(lqND.xyz, hqNormal)); nSim *= nSim; nSim *= nSim;
                        float dSim = saturate(1.0 - abs(lqND.a - hqDepth) * depthDiffScale);
                        float w = bw2 * nSim * dSim;
                        float4 v = tex2D(_MF_SSGI_Denoised_Final, tap2);
                        colorAccum += DecodeFloat2ToColor(v.rg) * w;
                        lightDirAccum += DecodeFloatToNormal(v.b) * w;
                        shadowAccum += v.a * w; wAccum += w;
                    }
                    {
                        float4 lqND = tex2D(_MF_SSGI_Normals_LQ, tap3);
                        float nSim = saturate(dot(lqND.xyz, hqNormal)); nSim *= nSim; nSim *= nSim;
                        float dSim = saturate(1.0 - abs(lqND.a - hqDepth) * depthDiffScale);
                        float w = bw3 * nSim * dSim;
                        float4 v = tex2D(_MF_SSGI_Denoised_Final, tap3);
                        colorAccum += DecodeFloat2ToColor(v.rg) * w;
                        lightDirAccum += DecodeFloatToNormal(v.b) * w;
                        shadowAccum += v.a * w; wAccum += w;
                    }
                #else
                    {
                        float4 lqND = tex2D(_MF_SSGI_Normals_LQ, tap0);
                        float nSim = saturate(dot(lqND.xyz, hqNormal)); nSim *= nSim; nSim *= nSim;
                        float dSim = saturate(1.0 - abs(lqND.a - hqDepth) * depthDiffScale);
                        float w = bw0 * nSim * dSim;
                        float4 v = tex2D(_MF_SSGI_Denoised_Final, tap0);
                        colorAccum += v.rgb * w; shadowAccum += v.a * w; wAccum += w;
                    }
                    {
                        float4 lqND = tex2D(_MF_SSGI_Normals_LQ, tap1);
                        float nSim = saturate(dot(lqND.xyz, hqNormal)); nSim *= nSim; nSim *= nSim;
                        float dSim = saturate(1.0 - abs(lqND.a - hqDepth) * depthDiffScale);
                        float w = bw1 * nSim * dSim;
                        float4 v = tex2D(_MF_SSGI_Denoised_Final, tap1);
                        colorAccum += v.rgb * w; shadowAccum += v.a * w; wAccum += w;
                    }
                    {
                        float4 lqND = tex2D(_MF_SSGI_Normals_LQ, tap2);
                        float nSim = saturate(dot(lqND.xyz, hqNormal)); nSim *= nSim; nSim *= nSim;
                        float dSim = saturate(1.0 - abs(lqND.a - hqDepth) * depthDiffScale);
                        float w = bw2 * nSim * dSim;
                        float4 v = tex2D(_MF_SSGI_Denoised_Final, tap2);
                        colorAccum += v.rgb * w; shadowAccum += v.a * w; wAccum += w;
                    }
                    {
                        float4 lqND = tex2D(_MF_SSGI_Normals_LQ, tap3);
                        float nSim = saturate(dot(lqND.xyz, hqNormal)); nSim *= nSim; nSim *= nSim;
                        float dSim = saturate(1.0 - abs(lqND.a - hqDepth) * depthDiffScale);
                        float w = bw3 * nSim * dSim;
                        float4 v = tex2D(_MF_SSGI_Denoised_Final, tap3);
                        colorAccum += v.rgb * w; shadowAccum += v.a * w; wAccum += w;
                    }
                #endif

                //Fallback to plain bilinear if all taps got rejected (e.g. very thin features)
                if (wAccum > 1e-4) {
                    float invW = 1.0 / wAccum;
                    color = colorAccum * invW;
                    lightDir = lightDirAccum * invW;
                    shadow = shadowAccum * invW;
                } else {
                    SampleSimpleSSGI(color, lightDir, shadow, uv);
                }
            }

            //void frag(v2f i, out float4 col:COLOR, out float depth : DEPTH) {
            float4 frag(v2f i) : SV_Target{
                float4 hqNormalDepth = tex2D(_MF_SSGI_Normals_HQ, i.uv);
                float linearDepth = hqNormalDepth.a;

                //Early return debug coverage
                if (_debug_screen_coverage < 1.0) {
                    if (i.uv.x > _debug_screen_coverage) {
                        if (i.uv.x > _debug_screen_coverage + 0.005) {
                            return tex2D(_MF_SSGI_ScreenCapture, i.uv);
                        } else {
                            return float4(0.5, 0.5, 0.5, 1.0);
                        }
                    }
                }

                //Override depth to fix broken depth-buffer and make Transparent-objects work
                float3 screenColor = max(0.0, tex2D(_MF_SSGI_ScreenCapture, i.uv)).rgb;

                //Early return when its the skybox
                float depthThreshold = _ProjectionParams.z * 0.95;
                //float linearDepth = LinearEyeDepth(depth);
                if (linearDepth > depthThreshold) {
                    return float4(screenColor, 1.0);
                }

                float3 resultColor = 0.0;
                float3 resultLightDir = 0.0;
                float resultShadow = 0.0;

                float3 localNormal = hqNormalDepth.xyz;
                
                float aaMask = 1.0;
                [branch] if (_aa_quality_level != 0) {

                    float2 edgeSize = float2(1.0 / _ssgi_res.x, 1.0 / _ssgi_res.y);
                    float4 p0 = tex2D(_MF_SSGI_Normals_LQ, i.uv + float2(edgeSize.x, edgeSize.y));
                    float4 p1 = tex2D(_MF_SSGI_Normals_LQ, i.uv + float2(-edgeSize.x, edgeSize.y));
                    float4 p2 = tex2D(_MF_SSGI_Normals_LQ, i.uv + float2(edgeSize.x, -edgeSize.y));
                    float4 p3 = tex2D(_MF_SSGI_Normals_LQ, i.uv + float2(-edgeSize.x, -edgeSize.y));

                    //Normal-based Edge detect first
                    float dotEdge = 999;
                    if (_aa_edge_detect_dot_theshold != 0.0) {
                        dotEdge = abs(dot(p0.xyz, localNormal));
                        dotEdge = min(dotEdge, abs(dot(p1.xyz, localNormal)));
                        dotEdge = min(dotEdge, abs(dot(p2.xyz, localNormal)));
                        dotEdge = min(dotEdge, abs(dot(p3.xyz, localNormal)));
                    }

                    //Depth-based Edge detect
                    float maxDepthDiff  = abs(p0.a - linearDepth);
                    maxDepthDiff        = max(maxDepthDiff, abs(p1.a - linearDepth));
                    maxDepthDiff        = max(maxDepthDiff, abs(p2.a - linearDepth));
                    maxDepthDiff        = max(maxDepthDiff, abs(p3.a - linearDepth));

                    //If depth is greater or dot-product is to large
                    if (maxDepthDiff > linearDepth * _aa_edge_detect_depth_theshold || dotEdge < _aa_edge_detect_dot_theshold){
                    //if (dotEdge == 0.0){
                        aaMask = 1.0;

                        float3 hqNormal = hqNormalDepth.xyz;
                        float contribution = 0.0;
                        
                        //----- 1X
                        //Cross
                        float falloff = 0.65;
                        SampleLQvsHQSSGI(resultColor, resultLightDir, resultShadow, contribution, hqNormal, i.uv + float2(_aa_sample_distance.x, 0.0), falloff);
                        SampleLQvsHQSSGI(resultColor, resultLightDir, resultShadow, contribution, hqNormal, i.uv + float2(-_aa_sample_distance.x, 0.0), falloff);
                        SampleLQvsHQSSGI(resultColor, resultLightDir, resultShadow, contribution, hqNormal, i.uv + float2(0.0, _aa_sample_distance.y), falloff);
                        SampleLQvsHQSSGI(resultColor, resultLightDir, resultShadow, contribution, hqNormal, i.uv + float2(0.0, -_aa_sample_distance.y), falloff);

                        //Corners
                        falloff = 0.35;
                        SampleLQvsHQSSGI(resultColor, resultLightDir, resultShadow, contribution, hqNormal, i.uv + float2(_aa_sample_distance.x, _aa_sample_distance.y), falloff);
                        SampleLQvsHQSSGI(resultColor, resultLightDir, resultShadow, contribution, hqNormal, i.uv + float2(-_aa_sample_distance.x, _aa_sample_distance.y), falloff);
                        SampleLQvsHQSSGI(resultColor, resultLightDir, resultShadow, contribution, hqNormal, i.uv + float2(_aa_sample_distance.x, -_aa_sample_distance.y), falloff);
                        SampleLQvsHQSSGI(resultColor, resultLightDir, resultShadow, contribution, hqNormal, i.uv + float2(-_aa_sample_distance.x, -_aa_sample_distance.y), falloff);

                        //----- 2X
                        [branch] if (_aa_quality_level > 1) {
                            float2 twoX = _aa_sample_distance * 2.0;
                            //Cross
                            falloff = 0.65;
                            SampleLQvsHQSSGI(resultColor, resultLightDir, resultShadow, contribution, hqNormal, i.uv + float2(twoX.x, 0.0), falloff);
                            SampleLQvsHQSSGI(resultColor, resultLightDir, resultShadow, contribution, hqNormal, i.uv + float2(-twoX.x, 0.0), falloff);
                            SampleLQvsHQSSGI(resultColor, resultLightDir, resultShadow, contribution, hqNormal, i.uv + float2(0.0, twoX.y), falloff);
                            SampleLQvsHQSSGI(resultColor, resultLightDir, resultShadow, contribution, hqNormal, i.uv + float2(0.0, -twoX.y), falloff);

                            //Corners
                            falloff = 0.35;
                            SampleLQvsHQSSGI(resultColor, resultLightDir, resultShadow, contribution, hqNormal, i.uv + float2(twoX.x, twoX.y), falloff);
                            SampleLQvsHQSSGI(resultColor, resultLightDir, resultShadow, contribution, hqNormal, i.uv + float2(-twoX.x, twoX.y), falloff);
                            SampleLQvsHQSSGI(resultColor, resultLightDir, resultShadow, contribution, hqNormal, i.uv + float2(twoX.x, -twoX.y), falloff);
                            SampleLQvsHQSSGI(resultColor, resultLightDir, resultShadow, contribution, hqNormal, i.uv + float2(-twoX.x, -twoX.y), falloff);
                        }

                        //----- 3X
                        [branch] if (_aa_quality_level > 2) {
                            float2 threeX = _aa_sample_distance * 3.0;
                            //Cross
                            falloff = 0.65;
                            SampleLQvsHQSSGI(resultColor, resultLightDir, resultShadow, contribution, hqNormal, i.uv + float2(threeX.x, 0.0), falloff);
                            SampleLQvsHQSSGI(resultColor, resultLightDir, resultShadow, contribution, hqNormal, i.uv + float2(-threeX.x, 0.0), falloff);
                            SampleLQvsHQSSGI(resultColor, resultLightDir, resultShadow, contribution, hqNormal, i.uv + float2(0.0, threeX.y), falloff);
                            SampleLQvsHQSSGI(resultColor, resultLightDir, resultShadow, contribution, hqNormal, i.uv + float2(0.0, -threeX.y), falloff);

                            //Corners
                            falloff = 0.35;
                            SampleLQvsHQSSGI(resultColor, resultLightDir, resultShadow, contribution, hqNormal, i.uv + float2(threeX.x, threeX.y), falloff);
                            SampleLQvsHQSSGI(resultColor, resultLightDir, resultShadow, contribution, hqNormal, i.uv + float2(-threeX.x, threeX.y), falloff);
                            SampleLQvsHQSSGI(resultColor, resultLightDir, resultShadow, contribution, hqNormal, i.uv + float2(threeX.x, -threeX.y), falloff);
                            SampleLQvsHQSSGI(resultColor, resultLightDir, resultShadow, contribution, hqNormal, i.uv + float2(-threeX.x, -threeX.y), falloff);
                        }

                        //Done AA
                        if (contribution > 0.1) {
                            resultColor /= contribution;
                            resultShadow /= contribution;
                            resultLightDir /= contribution;
                        } else {
                            JointBilateralUpscaleSSGI(resultColor, resultLightDir, resultShadow, i.uv, hqNormalDepth.xyz, hqNormalDepth.a);
                        }
                        if (_aa_debug_edge_detect == 1) {
                            return float4(1.0, 0.0, 0.0, 1.0);
                        }
                    } else {
                        JointBilateralUpscaleSSGI(resultColor, resultLightDir, resultShadow, i.uv, hqNormalDepth.xyz, hqNormalDepth.a);
                    }
                } else {
                    JointBilateralUpscaleSSGI(resultColor, resultLightDir, resultShadow, i.uv, hqNormalDepth.xyz, hqNormalDepth.a);
                }

                //Apply light-dir
                float3 ssgiColor = 0.0;
                if (_composit_lightdir_influence > 0.0) {
                    float lightReceiveDot = dot(localNormal, -resultLightDir);

                    float lambertA = saturate((lightReceiveDot - _light_direction_info_a.x) / (_light_direction_info_a.y - _light_direction_info_a.x));
                    float lambertB = saturate((lightReceiveDot - _light_direction_info_b.x) / (_light_direction_info_b.y - _light_direction_info_b.x)) * _light_direction_info_b.z;
                    lambertB *= lambertB;
                    float lambertShading = lerp(lambertA, (lambertA * _light_direction_info_a.z) + lambertB, _composit_lightdir_normal_boost);

                    //Apply Lambert to Color
                    ssgiColor = lerp(resultColor, resultColor * lambertShading, _composit_lightdir_influence);

                    //Apply Lambert to Shadows
                    lambertShading = lerp(1.0, lambertShading, _shadow_lambert_influence);
                    resultShadow = lerp(resultShadow, resultShadow * lambertShading * (1.0 + _composit_lightdir_influence), _composit_lightdir_influence);
                } else {
                    ssgiColor = resultColor;
                }

                //Apply SSGIObjects receive intenity
                float shadowReceiveIntensity = 1.0;
                #ifdef _USE_SSGI_OBJECTS
                    float4 ssgiObjectsColors = tex2D(_MF_SSGI_SSGIObjects, i.uv);
                    ssgiColor *= ssgiObjectsColors.g;
                    shadowReceiveIntensity = ssgiObjectsColors.b;
                #endif

                //Energy: Limit output
                float compMagSqr = dot(ssgiColor, ssgiColor);
                if (compMagSqr > _max_output_energy * _max_output_energy) {
                    ssgiColor = (ssgiColor / sqrt(compMagSqr)) * _max_output_energy;
                }

                //Apply Vibrance
                if (_composit_gi_vibrance > 0.0) {
                    float lumMax = max(ssgiColor.r, max(ssgiColor.g, ssgiColor.b));
                    float lumMin = min(ssgiColor.r, min(ssgiColor.g, ssgiColor.b));
                    float diffLum = saturate((lumMax - lumMin) / lumMax);
                    ssgiColor = lerp(ssgiColor, ssgiColor * diffLum, _composit_gi_vibrance);
                }

                //Safely saturate SSGI
                float ssgiLuminance = (ssgiColor.r + ssgiColor.g + ssgiColor.b) / 3.0;
                ssgiColor = max(ssgiColor * 0.25, lerp(ssgiLuminance, ssgiColor, _composit_gi_saturate));

                //Apply GI tint & contrast
                ssgiColor *= _composit_color;
                ssgiColor = pow(ssgiColor, _composit_gi_contrast);

                //Apply Shadow boost
                float occlusion = 1.0;
                float3 albedo = GetAlbedo(i.uv, hqNormalDepth.rgb, screenColor, aaMask, occlusion);
                float3 occludedScreenColor = screenColor;

                //Apply deferred occlusion
                ssgiColor *= occlusion;

                if (resultShadow > 0.0 && _shadow_intensity > 1.0) {
                    //Intensity the shadows where the SSGI is bright
                    float invShadow = resultShadow * shadowReceiveIntensity;
                    float shadow = 1.0 - saturate(pow(invShadow * (_shadow_intensity - 1.0), _shadow_boost_exp));
                                        
                    //Apply
                    float3 shadowTintMask = lerp(_shadow_boost_tint, 1.0, shadow); 
                    occludedScreenColor *= shadowTintMask;
                    albedo *= shadowTintMask;
                    
                    //Mask out shadow, but make sure that Luminance above 1 is countered as well
                    ssgiColor *= shadow;
                }

                //Apply Final pre-multiply
                if(_composit_occlusion_intensity != 0.0){
                    float minLum = saturate(min(screenColor.r, min(screenColor.g, screenColor.b)));
                    minLum *= minLum;
                    minLum *= minLum;
                    minLum *= minLum;
                    occludedScreenColor = lerp(occludedScreenColor, occludedScreenColor * minLum, _composit_occlusion_intensity) * (1.0 + _composit_occlusion_intensity);
                }
                
                //Done! Final composit & blend
                float3 addativeScreenColor = max(0.0, albedo * ssgiColor);
                float4 col = float4(pow((occludedScreenColor + addativeScreenColor), _composit_final_contrast) * _composit_final_intensity, 1.0);
                float distanceMask = saturate((linearDepth - _ssgi_range_min) / (_ssgi_range_max - _ssgi_range_min));
                col.rgb = lerp(col.rgb, screenColor, distanceMask);

                //Debug Motion-vectors
                if(_debug_motion_vectors == 1){
                    col.rgb = tex2D(_MotionVectorTexture, i.uv).rgb;
                }
                if(_debug_albedo == 1){
                    col.rgb = albedo;
                }

                return col;
            }
            ENDCG 
        }
    }
}
