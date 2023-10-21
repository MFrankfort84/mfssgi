Shader "MF_SSGI/SSGI"
{

    Properties
    {
        _WorldPositions("_WorldPositions", 2D) = "black" {}
        _PrevWorldPositions("_PrevWorldPositions", 2D) = "black" {}
        _PrevSSGI("_PrevSSGI", 2D) = "black" {}
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
            //#define GL_FRAGMENT_PRECISION_HIGH 1

            #include "UnityCG.cginc"
            #include "SSGI.hlsl"

            //Global shader values
            extern int _debug_reprojection = 0;

            extern float _multiframe_cell_size;
            extern float _multiframe_dist;
            extern float _multiframe_energy_falloff;
            extern float _multiframe_shadow_compensate;
            extern int _multiframe_apply_multisample;
            extern float2 _rnd_pixel;
            extern float _multi_sample_normal_distance;

            extern float2 _ssgi_res;
            extern float _ssgi_range_max;

            extern float _scan_base_range;
            extern float _scan_ratio_y;
            extern float _scan_noise;
            extern float _edge_vignette = 0.2;
            extern int _do_encode_lightdir = 0;

            extern float _ssgi_samples_hq = 0;
            extern float _ssgi_samples_backfill = 0;
            extern float _ssgi_samples_reduction = 0.5;
            extern float _scan_depth_threshold_factor = 10;
            extern float _ssgi_intensity;

            extern float _max_light_attenuation = 100.0;
            extern float _max_input_energy = 1.0;
            extern float _frustum_pixel_size = 1.0;
            extern float _light_falloff_distance = 1.0;

            //Addative
            extern float _shadow_intensity = 1.0;

            //Boost
            extern float _contact_shadow_range = 1.0;
            extern float _casted_shadow_range = 1.0;
            extern float _casted_shadow_intensity = 1.0;
            extern float _casted_shadow_omni_dir = 0.5;
            extern float _result_shadows_contrast = 1.0;
            extern float _contact_shadow_soft_knee;
            extern float _casted_shadow_soft_knee;

            //Raymarching
            extern float _raymarch_depth_bias = 0.99;
            extern int _raymarch_normal_depth_miplevel = 2;
            extern float _raymarch_min_distance = 7.5;
            extern float _raymarch_max_distance = 15;

            extern float _raymarch_samples_hq = 45;
            extern float _raymarch_samples_backfill = 25;

            //Denoise shadows
            extern float _raymarch_contact_min_dist = 0.01;
            extern float _raymarch_casted_min_dist = 0.5;

            extern float _skybox_influence = 1.0;
            extern float _light_cast_dot_min = -0.2;
            extern float _light_cast_dot_max = 0.5;
            extern float _light_receive_dot_min = 0.1;
            extern float _light_receive_dot_max = 0.85;
            extern float _light_viewdir_boost = 4.0;
            extern float3 _cam_world_forward;

            extern float _depth_cutoff_near = 1.0;
            extern float _depth_cutoff_far = 1.5;

            extern float _scansize_distance_threshold = 1.0;
            extern float _scansize_distance_base_size = 1.0;
            extern float _scansize_distance_multiplier = 1.0;
            extern float _scansize_ortho = 0.35;

            //Fallback
            extern float _ssgi_fallback_direct_intensity = 1.0;
            extern float _ssgi_fallback_direct_saturation = 1.0;
            extern float _ssgi_fallback_direct_power = 1.0;

            //Samplers
            sampler2D _WorldPositions;
            float4 _WorldPositions_ST;

            sampler2D _PrevWorldPositions;
            float4 _PrevWorldPositions_ST;

            sampler2D _PrevSSGI;
            float4 _PrevSSGI_ST;

            extern sampler2D _MF_SSGI_LightCapture;
            extern float4 _MF_SSGI_LightCapture_ST;

            extern sampler2D _MotionVectorTexture;
            extern float4 _MotionVectorTexture_ST;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float2 uv : TEXCOORD0;
                float4 vertex : SV_POSITION;
                float3 viewDir : TEXCOORD1;
            };

            v2f vert(appdata v) {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MF_SSGI_LightCapture);
                o.viewDir = normalize(WorldSpaceViewDir(o.vertex)).xyz;
                return o;
            }

            bool Raymarch(int samples, float2 uv, float lightDistance, float3 localWorldPos, float3 localWorldNormal, float localDepth, float3 envUVMipped, float envDepth, float depthBias, inout float3 blockedAtWorldPos, inout float blockedAtLerp){//, inout int debugCount) {
                int raymarchSamples = (int)(envUVMipped.z * (float)samples);
                int blockedCount;

                localDepth *= _raymarch_depth_bias;

                //Raymarch: Close-range
                float depthLerpInc = 1.0 / (float)raymarchSamples;
                float depthLerpIncThird = depthLerpInc / 3.0;
                float depthLerp = depthLerpInc;

                float2 currentThicknessMask = 0.0;
                float2 nextThicknessMask = 0.0;
                float2 prevUV = 0.0;

                [loop]
                for (int r = 1; r < raymarchSamples; r++) {
                    float lerpExp = lerp(depthLerp, depthLerp * depthLerp * depthLerp, _raymarch_cubic_distance_falloff);
                    lerpExp *= _raymarch_shorten;

                    float depthToTest = lerp(localDepth, envDepth, lerpExp);
                    float4 depthTestUV4 = float4(lerp(uv, envUVMipped.xy, lerpExp), 0.0, 0.0);
                    depthTestUV4.xy = round(depthTestUV4 * _ssgi_res) / _ssgi_res;
                    if (prevUV.x == depthTestUV4.x && prevUV.y == depthTestUV4.y) {
                        //debugCount++;
                        continue;
                    }
                    prevUV = depthTestUV4;


                    //Thickness map
                    bool wasHit = false;
                    float4 depthTestUV4Next = float4(lerp(uv, envUVMipped.xy, lerpExp + depthLerpIncThird), 0.0, 0.0);
                    currentThicknessMask = nextThicknessMask;
                    nextThicknessMask = tex2Dlod(_MF_SSGI_ThicknessMask, depthTestUV4Next);
                    
                    if (currentThicknessMask.g != 0.0 && nextThicknessMask.g != 0.0) {
                        float thickness = min(currentThicknessMask.r, nextThicknessMask.r);

                        //Depth test
                        float4 raymarchWorldPos = tex2Dlod(_WorldPositions, depthTestUV4);
                        if (raymarchWorldPos.a < depthToTest && (thickness == 0.0 || raymarchWorldPos.a > depthToTest - thickness)) {
                            if (abs(dot(raymarchWorldPos.xyz - localWorldPos, localWorldNormal)) > depthBias) {

                                //More the 2 hits in a row?
                                blockedCount++;
                                wasHit = true;
                                if (blockedCount >= _raymarch_min_hit_count) { //Requires atleast 2 hits - reduces flickering significantly 
                                    blockedAtWorldPos = raymarchWorldPos.xyz;
                                    blockedAtLerp = lerpExp;

                                    return true;
                                }
                            }
                        }
                    }

                    //Subtract blockedCount when the next step didn't hit anything. This makes sure a hits are consecutive
                    if (!wasHit) {
                        blockedCount = 0;
                    }
                    depthLerp += depthLerpInc;
                }

                //Not blocked (enough)
                return false;
            }

            float4 EncodeResult(float3 color, float3 lightDir, float shadow) {
                if (_do_encode_lightdir == 1) {
                    return float4(EncodeColorToFloat2(color), EncodeNormalToFloat(normalize(lightDir)), max(0.0, shadow));
                }
                return float4(color, max(0.0, shadow));
            }

            void SampleReprojectedGI(float2 uv, float3 localWorldPos, float localDepth, inout float3 color, inout float3 lightdir, inout float shadow, inout bool didFindGI, float falloff) {
                if (uv.x > 0.0 && uv.x < 1.0 && uv.y > 0.0 && uv.y < 1.0) {

                    //If within Minimum range, no need to render new GI
                    float3 diffVec = tex2D(_PrevWorldPositions, uv).xyz - localWorldPos;
                    float distSqr = dot(diffVec, diffVec);
                    float distThresholdSqr = _multiframe_dist * localDepth;
                    distThresholdSqr *= distThresholdSqr;

                    if (distSqr < distThresholdSqr) {
                        float4 value = tex2D(_PrevSSGI, uv);
                        didFindGI = true;

                        //Decode
                        float3 decodedColor = 0.0;
                        float3 decodedDir = 0.0;
                        if (_do_encode_lightdir == 1) {
                            decodedColor = DecodeFloat2ToColor(value.rg) * falloff;
                            decodedDir = DecodeFloatToNormal(value.b) * falloff;
                        } else {
                            decodedColor = value.rgb * falloff;
                        }

                        //use color (and light dir) only if Lum is greater
                        if (decodedColor.r + decodedColor.g + decodedColor.b > color.r + color.g + color.b) {
                            color = decodedColor;
                            lightdir = decodedDir;
                        }

                        //use shadow only if higher
                        shadow = max(shadow, value.a * falloff);
                    }
                }
            }


            float4 frag(v2f i) : SV_Target{
                float2 uv = i.uv;

                float4 localNormalAndDepth = tex2Dlod(_MF_SSGI_Normals_LQ, float4(uv, 0.0, _raymarch_normal_depth_miplevel));
                float localDepth = localNormalAndDepth.a;
                
                //Mutli-sample the normal
                float2 pixelSize = 1.0 / _ssgi_res;
                float3 localWorldNormal = localNormalAndDepth.xyz;

                if (_multi_sample_normal_distance > 0.0) {
                    float2 pixelSize2 = pixelSize * _multi_sample_normal_distance;
                    
                    //Corners
                    localWorldNormal += tex2Dlod(_MF_SSGI_Normals_LQ, float4(uv + float2(-pixelSize2.x, pixelSize2.y), 0.0, _raymarch_normal_depth_miplevel)).xyz;
                    localWorldNormal += tex2Dlod(_MF_SSGI_Normals_LQ, float4(uv + float2(pixelSize2.x, pixelSize2.y), 0.0, _raymarch_normal_depth_miplevel)).xyz;
                    localWorldNormal += tex2Dlod(_MF_SSGI_Normals_LQ, float4(uv + float2(-pixelSize2.x, -pixelSize2.y), 0.0, _raymarch_normal_depth_miplevel)).xyz;
                    localWorldNormal += tex2Dlod(_MF_SSGI_Normals_LQ, float4(uv + float2(pixelSize2.x, -pixelSize2.y), 0.0, _raymarch_normal_depth_miplevel)).xyz;
                    
                    //Cross
                    //localWorldNormal += tex2Dlod(_MF_SSGI_Normals_LQ, float4(uv + float2(-pixelSize2.x, 0.0), 0.0, _raymarch_normal_depth_miplevel)).xyz;
                    //localWorldNormal += tex2Dlod(_MF_SSGI_Normals_LQ, float4(uv + float2(pixelSize2.x, 0.0), 0.0, _raymarch_normal_depth_miplevel)).xyz;
                    //localWorldNormal += tex2Dlod(_MF_SSGI_Normals_LQ, float4(uv + float2(0.0, -pixelSize2.y), 0.0, _raymarch_normal_depth_miplevel)).xyz;
                    //localWorldNormal += tex2Dlod(_MF_SSGI_Normals_LQ, float4(uv + float2(0.0, pixelSize2.y), 0.0, _raymarch_normal_depth_miplevel)).xyz;
                    
                    localWorldNormal = normalize(localWorldNormal);
                }
                
                //Skip: If SSGI is not visible at all
                if (localDepth > min(_ProjectionParams.z * 0.95, _ssgi_range_max)) {
                    return 0.0;
                }

                //Get pixel index
                float pixelX = round(uv.x * _ssgi_res.x);
                float pixelY = round(uv.y * _ssgi_res.y);
                float3 localWorldPos = tex2D(_WorldPositions, uv);

                //--------------------- Multi-frame reprojection ---------------------
                bool requiresBackfill = false;
                if (_multiframe_cell_size > 0){
                    bool cycleX = (pixelX + _rnd_pixel.x) % _multiframe_cell_size != 0.0;
                    bool cycleY = (pixelY + _rnd_pixel.y) % _multiframe_cell_size != 0.0;

                    if (cycleX || cycleY) {
                        requiresBackfill = true;

                        float2 motionVectorUV = uv - (tex2D(_MotionVectorTexture, uv).xy);
                        float3 reprojectedColor = 0.0;
                        float3 reprojectedDir = 0.0;
                        float reprojectedShadow = 0.0;
                        bool didFindGI = false;

                        //Multi sample
                        SampleReprojectedGI(motionVectorUV, localWorldPos, localDepth, reprojectedColor, reprojectedDir, reprojectedShadow, didFindGI, 1.0);

                        if (_multiframe_apply_multisample == 1) {
                            //Corners
                            float falloff = 0.4;
                            SampleReprojectedGI(motionVectorUV + float2(pixelSize.x, pixelSize.y), localWorldPos, localDepth, reprojectedColor, reprojectedDir, reprojectedShadow, didFindGI, falloff);
                            SampleReprojectedGI(motionVectorUV + float2(-pixelSize.x, pixelSize.y), localWorldPos, localDepth, reprojectedColor, reprojectedDir, reprojectedShadow, didFindGI, falloff);
                            SampleReprojectedGI(motionVectorUV + float2(pixelSize.x, -pixelSize.y), localWorldPos, localDepth, reprojectedColor, reprojectedDir, reprojectedShadow, didFindGI, falloff);
                            SampleReprojectedGI(motionVectorUV + float2(-pixelSize.x, -pixelSize.y), localWorldPos, localDepth, reprojectedColor, reprojectedDir, reprojectedShadow, didFindGI, falloff);

                            //Cross
                            falloff = 0.6;
                            SampleReprojectedGI(motionVectorUV + float2(-pixelSize.x, 0.0), localWorldPos, localDepth, reprojectedColor, reprojectedDir, reprojectedShadow, didFindGI, falloff);
                            SampleReprojectedGI(motionVectorUV + float2(pixelSize.x, 0.0), localWorldPos, localDepth, reprojectedColor, reprojectedDir, reprojectedShadow, didFindGI, falloff);
                            SampleReprojectedGI(motionVectorUV + float2(0.0, -pixelSize.y), localWorldPos, localDepth, reprojectedColor, reprojectedDir, reprojectedShadow, didFindGI, falloff);
                            SampleReprojectedGI(motionVectorUV + float2(0.0, pixelSize.y), localWorldPos, localDepth, reprojectedColor, reprojectedDir, reprojectedShadow, didFindGI, falloff);
                        }

                        //Result
                        if (didFindGI) {
                            return EncodeResult(
                                reprojectedColor * _multiframe_energy_falloff,
                                reprojectedDir,
                                reprojectedShadow * _multiframe_energy_falloff
                            );
                        }
                    }
                }


                //--------------------- SSGI calculations ---------------------
                //Cache
                float lightDirCastRange = _light_cast_dot_max - _light_cast_dot_min;
                float lightDirReceiveRange = _light_receive_dot_max - _light_receive_dot_min;
                float nearCamRange = _depth_cutoff_far - _depth_cutoff_near;
                float maxInputEnergySqr = _max_input_energy * _max_input_energy;

                //Time to scan
                float3 resultColor = 0.0;
                float3 resultLightDir = 0.0;
                float totalContribution = 0.0;

                //Apply distance based scan-size
                float depthScale = 1.0;
                if (unity_OrthoParams.w == 0.0) {
                    if (localDepth > _scansize_distance_threshold) {
                        depthScale = _scansize_distance_base_size / (localDepth * _scansize_distance_multiplier);
                    }
                } else {
                    depthScale = _scansize_ortho;
                }

                //Subtract the two and we have good shadows
                float lightLuminocityWithShadows = 0.0;
                float lightLuminocityWithoutShadows = 0.0;
                float contactShadows = 0.0;
                //float2 prevUV = 0.0;
                //int debugCount = 0;

                //float3 viewDir = normalize(WorldSpaceViewDir(mul(unity_ObjectToWorld, i.vertex)));
                //float3 viewDir = normalize(WorldSpaceViewDir(i.vertex));
                //return float4(i.viewDir.x, i.viewDir.y, i.viewDir.z, 1.0);

                float samples = _ssgi_samples_hq;
                if (requiresBackfill) {
                    samples = _ssgi_samples_backfill;

                    if(_debug_reprojection == 1){
                        return EncodeResult(float3(1.0, 0.0, 0.0), 0.0, 0.0);
                    }
                }

                [loop]
                for (float y = 0; y < samples; y++) {
                    float gaussY = sin(y / samples * 3.14159);
                    float offsetY = ((y * 2.0) - samples) / samples * _scan_ratio_y * _scan_base_range;

                    [loop]
                    for (float x = 0; x < samples; x++) {
                        float gaussX = sin(x / samples * 3.14159);
                        float uvLerp = 1.0 - min(gaussX, gaussY);

                        float3 envUVMipped = float3(
                            ((x * 2.0) - samples) / samples * _scan_base_range,
                            offsetY,
                            uvLerp
                        );

                        float2 preOffseted = i.uv + envUVMipped;
                        envUVMipped.xy += float2(
                            ((frac(sin(dot(preOffseted, float2(-67.3634, 78.233))) * 4758.7896)) - 0.5) * 2.0,
                            ((frac(sin(dot(preOffseted, float2(12.9898, -78.233))) * 3758.123)) - 0.5) * 2.0
                            ) * _scan_noise;

                        //Scale UV
                        envUVMipped.xy *= depthScale;

                        //Test random threshold based on the distance to center;
                        if(_ssgi_samples_reduction != 1.0){
                            float rndReduction = frac(sin(dot(envUVMipped.xy, float2(-67.3634, 78.233))) * 4758.7896);
                            if(rndReduction * envUVMipped.z > _ssgi_samples_reduction){
                                continue;
                            }
                        }

                        //Offset UV
                        envUVMipped.xy += i.uv;

                        //Skip: Out of screen
                        if (envUVMipped.x < 0.0 || envUVMipped.x > 1.0 || envUVMipped.y < 0.0 || envUVMipped.y > 1.0) {
                            continue;
                        }

                        //Skip: if snapped-to-pixel UV's match the previous sample
                        envUVMipped.xy = round(envUVMipped * _ssgi_res) / _ssgi_res;

                        float4 envUVMipped4 = float4(envUVMipped.xy, 0.0, 0.0);
                        //if (prevUV.x == envUVMipped4.x && prevUV.y == envUVMipped4.y) { //Only a hand full of pixels!
                        //    debugCount++;
                        //    continue;
                        //}
                        //prevUV = envUVMipped4.xy;

                        //Skip: if color is empty
                        float4 screenCaptureColor = tex2Dlod(_MF_SSGI_LightCapture, envUVMipped4);
                        float3 color = screenCaptureColor.rgb;
                        if (!any(color)) {
                            continue;
                        }

                        //Test if omni-directional - Passed on by making the screenCapture negative in value
                        bool omniDir = screenCaptureColor.r < 0.0 || screenCaptureColor.g < 0.0 || screenCaptureColor.b < 0.0;
                        if (omniDir) {
                            color.rgb = -color.rgb;
                        }

                        //Contribution should be as stable as possible
                        float contribution = 1.0 - ((uvLerp * uvLerp * uvLerp) * depthScale);
                        totalContribution += contribution;

                        //Skip: Skybox, as it already was contributed by Uniy's own indirect GI
                        float4 envNormalAndDepth = tex2Dlod(_MF_SSGI_Normals_LQ, envUVMipped4);
                        float3 envWorldNormal = envNormalAndDepth.xyz;
                        float envDepth = envNormalAndDepth.a;

                        //Skip: If Skybox found!
                        if (envDepth > localDepth * _scan_depth_threshold_factor) {
                            resultColor += color * (_skybox_influence / (samples * samples));
                            continue;
                        }

                        //Skip & fade: If the depth is too close to the camera, it's contribution should be removed/fade-in to prevent near-object-scatter
                        float nearCamMask = 1.0;
                        if (envDepth < _depth_cutoff_far) {
                            nearCamMask = saturate((envDepth - _depth_cutoff_near) / nearCamRange);

                            //Subtract float of the contribution: This boosts the intensity where area's weren't lit because of the near-cam masking
                            totalContribution -= contribution * ((1.0 - nearCamMask) * 0.5f);
                            if (nearCamMask == 0.0) {
                                continue;
                            }
                        }

                        //Cache normals needed for light calculations
                        float3 envWorldPos = tex2Dlod(_WorldPositions, envUVMipped4);
                        float3 lightDirVec = localWorldPos - envWorldPos;
                        float lightDistance = length(lightDirVec);
                        if (lightDistance <= 0.01) {
                            continue;
                        }
                        float3 lightDir = lightDirVec / lightDistance; //Normalize, we already have the square-rooted distance

                        //Skip: If both surface normals almost match perfectly, its most likely part of the same wall/surface
                        if (!omniDir && _light_cast_dot_min <= 0.0) {
                            if (dot(envWorldNormal, localWorldNormal) > 0.9) {
                                continue;
                            }
                        }

                        //Receiving: Lambert-like behaviour
                        float lightReceiveDot = -dot(lightDir, localWorldNormal);
                        float lambertSurface = (lightReceiveDot - _light_receive_dot_min) / lightDirReceiveRange;
                        if (lambertSurface > 1.0) {
                            lambertSurface = 1.0;
                        } else if (lambertSurface < 0.0) {
                            continue;
                        }

                        //Casting: Spotlight-like behaviour
                        float lightDirCastDot = dot(lightDir, envWorldNormal);
                        float lightDirCastMask = 1.0;
                        if (!omniDir) {
                            lightDirCastMask = saturate((lightDirCastDot - _light_cast_dot_min) / lightDirCastRange);
                        }

                        //Apply edge vignette
                        float vignette = 1.0;
                        if (_edge_vignette > 0.0) {
                            vignette = min(vignette, envUVMipped.x / _edge_vignette);
                            vignette = min(vignette, envUVMipped.y / _edge_vignette);
                            vignette = min(vignette, (1.0 - envUVMipped.x) / _edge_vignette);
                            vignette = min(vignette, (1.0 - envUVMipped.y) / _edge_vignette);
                        }

                        //Pixel intensity: When the camera moves away, a single pixel represents more surface area, therefore should add more light
                        float pixelIntensity = 1.0;
                        if (unity_OrthoParams.w == 0.0) {
                            pixelIntensity = 1.0 + (envDepth * _frustum_pixel_size);
                        }

                        //RAYMARCH!!! YEAH BABY!
                        bool blocked = false;
                        float blockedAtLerp = 0.0;
                        //bool skipRaymarch = requiresBackfill || (omniDir && lightDirCastDot < 0.1);
                        bool skipRaymarch = omniDir && lightDirCastDot < 0.1;
                         
                        if (!skipRaymarch && _shadow_intensity > 0.0 && localDepth < _raymarch_max_distance) {

                            float3 blockedAtWorldPos = 0.0;
                            float depthBias = lerp(_raymarch_surface_depth_bias_min, _raymarch_surface_depth_bias_max, saturate(lightReceiveDot));

                            int samples = _raymarch_samples_hq;
                            if (requiresBackfill) {
                                samples = _raymarch_samples_backfill;
                            }

                            if (Raymarch(samples, uv, lightDistance, localWorldPos, localWorldNormal, localDepth, envUVMipped, envDepth, depthBias, blockedAtWorldPos, blockedAtLerp)){ //, debugCount)){
                                float rayDistance = length(blockedAtWorldPos - localWorldPos);
                                if (lightDistance > _raymarch_casted_min_dist) {
                                    blocked = true;

                                    //Done raymarching
                                    if (blocked && _contact_shadow_range > 0.0) {
                                        if (rayDistance > _raymarch_contact_min_dist && rayDistance < _contact_shadow_range) {
                                            float localContactShadow = 1.0 - saturate(rayDistance / _contact_shadow_range);
                                            localContactShadow *= localContactShadow;
                                            contactShadows = max(contactShadows, localContactShadow);
                                        }
                                    }
                                }
                            }
                        }

                        //Add result: Sample environment and multiply with all masks & influences
                        float mask = contribution * vignette * nearCamMask * lightDirCastMask;
                        if (mask > 0.001) {
                            //Energy: Limit input color energy
                            float colorMagSqr = dot(color, color);
                            if (colorMagSqr > maxInputEnergySqr) {
                                color = color / sqrt(colorMagSqr) * _max_input_energy; //Devide by zero? WEBGL turns black?
                            }

                            //Light Attenuation: When light is emitted, the intensity drops further away from the source
                            float lightAttenuation = min(_max_light_attenuation, _light_falloff_distance / lightDistance);

                            //Compensate for sourses that are perpendicular to the view-dir as they most likely represent a lot more surface area then we can see
                            //float perpCastViewDirDot = 1.0 - saturate(dot(envWorldNormal, i.viewDir));
                            //float perpViewDirBoost = 1.0 + ((perpCastViewDirDot * perpCastViewDirDot) * _light_viewdir_boost);

                            //Collect casted shadows
                            float lightIntensity = pixelIntensity * mask * lambertSurface * lightAttenuation;// *perpViewDirBoost;
                            float lightIntensityShadows = lightIntensity;
                            lightLuminocityWithoutShadows += lightIntensity;
                            if (blocked) {
                                float lightDistanceFalloff = saturate(lightDistance / _casted_shadow_range);
                                lightDistanceFalloff *= lightDistanceFalloff;
                                lightDistanceFalloff *= lightDistanceFalloff;
                                lightDistanceFalloff *= lightDistanceFalloff;
                                lightDistanceFalloff *= lightDistanceFalloff;
                                lightDistanceFalloff = 1.0 - lightDistanceFalloff;

                                //By multiplying with the luminocity of the pixel, pixels that are black will not cast shadows as they are not lightsources
                                float colorLum = min(1.0, max(color.r, max(color.g, color.b)));
                                colorLum = 1.0 - colorLum;
                                colorLum *= colorLum;
                                colorLum *= colorLum;
                                colorLum *= colorLum;
                                colorLum *= colorLum;
                                colorLum = 1.0 - colorLum;

                                //Blend to 1.0 to make it a more traditional soft AO
                                colorLum = lerp(colorLum, 1.0, _casted_shadow_omni_dir);

                                //Apply
                                lightIntensityShadows -= (1.0 - blockedAtLerp) * saturate(lightIntensity) * colorLum * lightDistanceFalloff;
                                lightIntensity *= (1.0 - min(1.0, _shadow_intensity));
                            }
                            lightLuminocityWithShadows += lightIntensityShadows;

                            //Add final color
                            resultColor += color * lightIntensity;
                            resultLightDir += lightDir;
                        }
                    }
                }

                //DEBUG
                //return EncodeResult(debugCount / 10, 0.0, 0.0);
                
                //Subtract light, leaving the casted lights as result
                float castedShadow = (lightLuminocityWithoutShadows - lightLuminocityWithShadows);

                //Even output based on total contributions
                if (totalContribution > 0.1) {
                    resultColor /= totalContribution;
                    castedShadow /= totalContribution;
                } else {
                    resultColor = float3(0.0, 0.0, 0.0);
                }

                //Subtract result luminance from casted shadows, to prevent shadows occuring on places where light is clearly visible
                castedShadow *= 1.0 - saturate(max(resultColor.r, max(resultColor.g, resultColor.b)) * 5.0);
                castedShadow = saturate(castedShadow * _casted_shadow_intensity);

                //Apply shadow compensation for the lack of samples
                if (requiresBackfill) {
                    contactShadows *= _multiframe_shadow_compensate;
                    castedShadow *= _multiframe_shadow_compensate;
                }

                //Contrast
                contactShadows *= 1.0 + _result_shadows_contrast;
                contactShadows -= _result_shadows_contrast;
                castedShadow *= 1.0 + _result_shadows_contrast;
                castedShadow -= _result_shadows_contrast;

                //Apply soft-knee
                contactShadows = min(contactShadows, 1.0 - _contact_shadow_soft_knee);
                castedShadow = min(castedShadow, 1.0 - _casted_shadow_soft_knee);

                //Mix in the casted shadows
                float resultShadow = min(1.0, max(contactShadows, castedShadow));

                //Apply distance falloff
                resultShadow *= max(0.0, 1.0 - saturate((localDepth - _raymarch_min_distance) / (_raymarch_max_distance - _raymarch_min_distance)));
                float3 screenColor = max(0.0, tex2D(_MF_SSGI_ScreenCapture, i.uv)).rgb;

                //Apply final GI intensity
                resultColor *= _ssgi_intensity;

                //Add fallback Reflection-probe light
                if (_ssgi_fallback_direct_intensity > 0.0) {
                    float3 refColor;
                    float3 refLightDir;
                    SampleReflectionProbes(refColor, refLightDir, localWorldPos, localWorldNormal, i.uv, _ssgi_fallback_direct_intensity, _ssgi_fallback_direct_saturation, _ssgi_fallback_direct_power);
                    refColor *= saturate(1.0 - resultShadow);

                    if(!isnan(refLightDir.x) && !isnan(refLightDir.y) && !isnan(refLightDir.z)){ //If not checked, it might produce black directional lightmaps
                        resultLightDir += refLightDir;
                        resultColor = max(resultColor, refColor);
                        //resultLightDir = refLightDir;
                    }
                }

                //Done!
                return EncodeResult(resultColor, resultLightDir, resultShadow);
                
            }
            ENDCG
        }
    }
}
