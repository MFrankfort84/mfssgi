

extern sampler2D _CameraNormalsTexture;
extern float4 _CameraNormalsTexture_ST;

extern sampler2D _MF_SSGI_ScreenCapture;
extern float4 _MF_SSGI_ScreenCapture_ST;

extern sampler2D _MF_SSGI_ThicknessMask;
extern float4 _MF_SSGI_ThicknessMask_ST;

extern sampler2D _GBuffer0;
extern float4 _GBuffer0_ST;

extern sampler2D _GBuffer1;
extern float4 _GBuffer1_ST;

extern sampler2D _MF_SSGI_Normals_HQ;
extern float4 _MF_SSGI_Normals_HQ_ST;

extern sampler2D _MF_SSGI_Normals_LQ;
extern float4 _MF_SSGI_Normals_LQ_ST;

extern int _use_deferred;

extern float _forward_albedo_contrast;
extern float _forward_albedo_subtract_fog;
extern float _forward_albedo_subtract_sky;

extern float3 _deferred_specular_tint;

extern float _albedo_min_whiteness;
extern float _albedo_boost;
extern float _albedo_boost_miplevel;

extern int _ssgi_refprobe_count;
extern float _ssgi_refprobe_mip;
extern float _ssgi_refprobe_falloff;
extern float _ssgi_refprobe_realtime_intensity;
extern float _ssgi_refprobe_realtime_saturation;
extern float _ssgi_refprobe_realtime_power;
extern float _ssgi_refprobe_raymarch_samples;

//Shared raymarch values
extern float _raymarch_cubic_distance_falloff = 0.5;
extern float _raymarch_shorten = 0.9;
extern int _raymarch_min_hit_count = 2;
extern float _raymarch_surface_depth_bias_min = 0.05;
extern float _raymarch_surface_depth_bias_max = 0.25;


//Params: x = intensity, y = gamma, z = baked yes/no
extern samplerCUBE _ssgi_refprobe_texture_0;   extern float4 _ssgi_refprobe_texture_0_ST;   extern float3 _ssgi_refprobe_center_0; extern float3 _ssgi_refprobe_source_0;   extern float3 _ssgi_refprobe_extents_0;  extern float4 _ssgi_refprobe_params_0; extern float4 _ssgi_refprobe_rayinfo_0;
extern samplerCUBE _ssgi_refprobe_texture_1;   extern float4 _ssgi_refprobe_texture_1_ST;   extern float3 _ssgi_refprobe_center_1; extern float3 _ssgi_refprobe_source_1;   extern float3 _ssgi_refprobe_extents_1;  extern float4 _ssgi_refprobe_params_1; extern float4 _ssgi_refprobe_rayinfo_1;
extern samplerCUBE _ssgi_refprobe_texture_2;   extern float4 _ssgi_refprobe_texture_2_ST;   extern float3 _ssgi_refprobe_center_2; extern float3 _ssgi_refprobe_source_2;   extern float3 _ssgi_refprobe_extents_2;  extern float4 _ssgi_refprobe_params_2; extern float4 _ssgi_refprobe_rayinfo_2;
extern samplerCUBE _ssgi_refprobe_texture_3;   extern float4 _ssgi_refprobe_texture_3_ST;   extern float3 _ssgi_refprobe_center_3; extern float3 _ssgi_refprobe_source_3;   extern float3 _ssgi_refprobe_extents_3;  extern float4 _ssgi_refprobe_params_3; extern float4 _ssgi_refprobe_rayinfo_3;
extern samplerCUBE _ssgi_refprobe_texture_4;   extern float4 _ssgi_refprobe_texture_4_ST;   extern float3 _ssgi_refprobe_center_4; extern float3 _ssgi_refprobe_source_4;   extern float3 _ssgi_refprobe_extents_4;  extern float4 _ssgi_refprobe_params_4; extern float4 _ssgi_refprobe_rayinfo_4;
extern samplerCUBE _ssgi_refprobe_texture_5;   extern float4 _ssgi_refprobe_texture_5_ST;   extern float3 _ssgi_refprobe_center_5; extern float3 _ssgi_refprobe_source_5;   extern float3 _ssgi_refprobe_extents_5;  extern float4 _ssgi_refprobe_params_5; extern float4 _ssgi_refprobe_rayinfo_5;
extern samplerCUBE _ssgi_refprobe_texture_6;   extern float4 _ssgi_refprobe_texture_6_ST;   extern float3 _ssgi_refprobe_center_6; extern float3 _ssgi_refprobe_source_6;   extern float3 _ssgi_refprobe_extents_6;  extern float4 _ssgi_refprobe_params_6; extern float4 _ssgi_refprobe_rayinfo_6;
extern samplerCUBE _ssgi_refprobe_texture_7;   extern float4 _ssgi_refprobe_texture_7_ST;   extern float3 _ssgi_refprobe_center_7; extern float3 _ssgi_refprobe_source_7;   extern float3 _ssgi_refprobe_extents_7;  extern float4 _ssgi_refprobe_params_7; extern float4 _ssgi_refprobe_rayinfo_7;


//Sign has the issue that it returns 0 if the value is zero. Use this to flip values, not intrinsic sign!
float SignBinary(float value) {
    return value >= 0.0 ? 1.0 : -1.0;
}

//Ripped from: Packages/com.unity.render-pipelines.core/ShaderLibrary/Packing.hlsl
float2 Unpack888ToFloat2(float3 x) {
    uint3 i = (uint3)(x * 255.5);
    uint hi = i.z >> 4;
    uint lo = i.z & 15;
    uint2 cb = i.xy | uint2(lo << 8, hi << 8);
    return cb / 4095.0;
}

//Ripped from: Packages/com.unity.render-pipelines.core/ShaderLibrary/Packing.hlsl
float3 UnpackNormalOctQuadEncode(float2 f) {
    float3 n = float3(f.x, f.y, 1.0 - abs(f.x) - abs(f.y));
    float t = max(-n.z, 0.0);
    n.xy += n.xy >= 0.0 ? -t.xx : t.xx;
    return normalize(n);
}

half3 SampleCameraNormalsTexture(float2 uv) {
    float4 normals = tex2D(_CameraNormalsTexture, uv);

#if defined(_GBUFFER_NORMALS_OCT)
    half2 remappedOctNormalWS = Unpack888ToFloat2(normals.xyz); // values between [ 0,  1]
    half2 octNormalWS = remappedOctNormalWS.xy * 2.0h - 1.0h;    // values between [-1, +1]
    //return normalize(half3(UnpackNormalOctQuadEncode(octNormalWS)));
    return half3(UnpackNormalOctQuadEncode(octNormalWS));
#else
    return normals;
#endif
}




float3 RGBToHSV(float3 c) {
    float4 K = float4(0.0, -1.0 / 3.0, 2.0 / 3.0, -1.0);
    float4 p = lerp(float4(c.bg, K.wz), float4(c.gb, K.xy), step(c.b, c.g));
    float4 q = lerp(float4(p.xyw, c.r), float4(c.r, p.yzx), step(p.x, c.r));
    float d = q.x - min(q.w, q.y);
    float e = 1.0e-10;
    return float3(abs(q.z + (q.w - q.y) / (6.0 * d + e)), d / (q.x + e), q.x);
}

float3 HSVToRGB(float3 c) {
    float4 K = float4(1.0, 2.0 / 3.0, 1.0 / 3.0, 3.0);
    float3 p = abs(frac(c.xxx + K.xyz) * 6.0 - K.www);
    return c.z * lerp(K.xxx, saturate(p - K.xxx), c.y);
}

float EncodeNormalToFloat(float3 normal) {
    if (!any(normal)) {
        return -1.0;
    }

    float x01 = (normal.x + 1.0) / 2.0;
    float y01 = (normal.y + 1.0) / 2.0;
    //2046: 2048-1 = 2047 to cover 0-to-2047 x-component --  2046 to cover 0-1 y-component
    float stacked = floor(x01 * 2046.0) + y01 + 1.0;
    return stacked * SignBinary(normal.z);
}

float3 DecodeFloatToNormal(float value) {
    if (value == -1.0) {
        return float3(0.0, 0.0, 0.0);
    }

    float absAlpha = abs(value);
    float x01 = floor(absAlpha - 1.0) / 2046.0;
    float y01 = frac(absAlpha);

    float x = (x01 * 2.0) - 1.0;
    float y = (y01 * 2.0) - 1.0;

    float3 normal = normalize(float3(x, y, sqrt(abs(1.0 - ((x * x) + (y * y)))) * SignBinary(value)));
    if (!isnan(normal.x) && !isnan(normal.y) && !isnan(normal.z)) {
        return normal;
    }
    return 0.0;
}

float2 EncodeColorToFloat2(float3 value) {
    float lum = length(value);
    if (lum == 0.0){//} || isnan(lum)){//} || isinf(lum)) {
        return 0.0;
    }

    value /= lum; //normalized

    //PURE GREEN WORKAROUND: Pure Green (no R&G) results in the color flipping to blue
    if (value.x == 0.0 && value.z == 0.0) {
        lum = -lum;
    }
    return float2(floor(value.x * 2046.0) + value.y + 1.0, lum);
}

float3 DecodeFloat2ToColor(float2 value) {
    if (!any(value)) {
        return 0.0;
    }

    //PURE GREEN WORKAROUND: Pure Green (no R&G) results in the color flipping to blue
    if (value.y < 0.0) {
        return float3(0.0, -value.y, 0.0);
    }

    float x = floor(value.x - 1.0) / 2046.0;
    float y = frac(value.x);
    return normalize(float3(x, y, sqrt(abs(1.0 - ((x * x) + (y * y)))))) * value.y;
}


void SampleReflectionProbe(samplerCUBE refSampler, float3 refCenter, float3 refSource, float3 refExtents, float4 refParams, float4 refRayInfo, float2 uv, float3 worldPos, float3 worldNormal, inout float3 resultColor, inout float3 resultLightDir, inout float contribution) {
    float3 diffVecAbs = abs(worldPos - refCenter);
    if (diffVecAbs.x > refExtents.x || diffVecAbs.y > refExtents.y || diffVecAbs.z > refExtents.z) {
        return;
    }
    contribution++;

    float maxDistance = min(min(refExtents.x * _ssgi_refprobe_falloff, refExtents.y * _ssgi_refprobe_falloff), refExtents.z * _ssgi_refprobe_falloff);
    float edgeDistance = min(min(refExtents.x - diffVecAbs.x, refExtents.y - diffVecAbs.y), refExtents.z - diffVecAbs.z);
    float fade = saturate(edgeDistance / maxDistance);

    //Raymarch block?
    if (_ssgi_refprobe_raymarch_samples > 0) {
        float depthLerpInc = 1.0 / (float)_ssgi_refprobe_raymarch_samples;
        float depthLerp = depthLerpInc;
        float depthBias = (_raymarch_surface_depth_bias_min + _raymarch_surface_depth_bias_max) / 2.0;

        //Normals and Depth
        float4 normalsDepth = tex2D(_MF_SSGI_Normals_LQ, uv);
        int blockedCount = 0;

        [loop]
        for (int r = 1; r < _ssgi_refprobe_raymarch_samples; r++) {
            float lerpExp = lerp(depthLerp, depthLerp * depthLerp * depthLerp, _raymarch_cubic_distance_falloff);
            lerpExp *= _raymarch_shorten;

            float depthToTest = lerp(normalsDepth.a, refRayInfo.z, lerpExp);
            float2 depthTestUV = lerp(uv, refRayInfo.xy, lerpExp);
            float4 depthTestUV4 = float4(depthTestUV, 0.0, 0.0);

            //Thickness map
            float4 thicknessMask = tex2Dlod(_MF_SSGI_ThicknessMask, depthTestUV4);
            if (thicknessMask.g != 0.0) {
                
                //Depth test
                float4 raymarchNormalsDepth = tex2Dlod(_MF_SSGI_Normals_LQ, depthTestUV4);
                if (raymarchNormalsDepth.a < depthToTest && raymarchNormalsDepth.a > depthToTest - thicknessMask.r) {
                    //if (abs(dot(raymarchWorldPos.xyz - localWorldPos, localWorldNormal)) > depthBias) {

                        //More the 2 hits in a row?
                        blockedCount++;
                        if (blockedCount >= _raymarch_min_hit_count) { //Requires atleast 2 hits - reduces flickering significantly 
                            return;
                        }
                    //}
                }
            }
            depthLerp += depthLerpInc;
        }
    }

    //Apply intensities
    float3 color = texCUBElod(refSampler, float4(worldNormal, _ssgi_refprobe_mip * fade)).rgb;

    //Compensation for realtime light
    if (refParams.z == 0.0) {
        color = pow(color, _ssgi_refprobe_realtime_power);
        color *= _ssgi_refprobe_realtime_intensity;

        float lum = (color.r + color.g + color.b) / 3.0;
        color = lerp(lum, color, _ssgi_refprobe_realtime_saturation);
    }

    resultColor += color * fade * refParams.x;
    resultLightDir += normalize(worldPos - refSource);
}


void SampleReflectionProbes(out float3 resultColor, out float3 resultLightDir, float3 worldPos, float3 worldNormal, float2 uv, float intensity, float saturation, float exp) {
    resultColor = 0.0;
    resultLightDir = 0.0;
    float contribution = 0.0;

    if (_ssgi_refprobe_count > 0) {
        SampleReflectionProbe(_ssgi_refprobe_texture_0, _ssgi_refprobe_center_0, _ssgi_refprobe_source_0, _ssgi_refprobe_extents_0, _ssgi_refprobe_params_0, _ssgi_refprobe_rayinfo_0, uv, worldPos, worldNormal, resultColor, resultLightDir, contribution);
    }
    if (_ssgi_refprobe_count > 1) {
        SampleReflectionProbe(_ssgi_refprobe_texture_1, _ssgi_refprobe_center_1, _ssgi_refprobe_source_1, _ssgi_refprobe_extents_1, _ssgi_refprobe_params_1, _ssgi_refprobe_rayinfo_1, uv, worldPos, worldNormal, resultColor, resultLightDir, contribution);
    }
    if (_ssgi_refprobe_count > 2) {
        SampleReflectionProbe(_ssgi_refprobe_texture_2, _ssgi_refprobe_center_2, _ssgi_refprobe_source_2, _ssgi_refprobe_extents_2, _ssgi_refprobe_params_2, _ssgi_refprobe_rayinfo_2, uv, worldPos, worldNormal, resultColor, resultLightDir, contribution);
    }
    if (_ssgi_refprobe_count > 3) {
        SampleReflectionProbe(_ssgi_refprobe_texture_3, _ssgi_refprobe_center_3, _ssgi_refprobe_source_3, _ssgi_refprobe_extents_3, _ssgi_refprobe_params_3, _ssgi_refprobe_rayinfo_3, uv, worldPos, worldNormal, resultColor, resultLightDir, contribution);
    }
    if (_ssgi_refprobe_count > 4) {
        SampleReflectionProbe(_ssgi_refprobe_texture_4, _ssgi_refprobe_center_4, _ssgi_refprobe_source_4, _ssgi_refprobe_extents_4, _ssgi_refprobe_params_4, _ssgi_refprobe_rayinfo_4, uv, worldPos, worldNormal, resultColor, resultLightDir, contribution);
    }
    if (_ssgi_refprobe_count > 5) {
        SampleReflectionProbe(_ssgi_refprobe_texture_5, _ssgi_refprobe_center_5, _ssgi_refprobe_source_5, _ssgi_refprobe_extents_5, _ssgi_refprobe_params_5, _ssgi_refprobe_rayinfo_5, uv, worldPos, worldNormal, resultColor, resultLightDir, contribution);
    }
    if (_ssgi_refprobe_count > 6) {
        SampleReflectionProbe(_ssgi_refprobe_texture_6, _ssgi_refprobe_center_6, _ssgi_refprobe_source_6, _ssgi_refprobe_extents_6, _ssgi_refprobe_params_6, _ssgi_refprobe_rayinfo_6, uv, worldPos, worldNormal, resultColor, resultLightDir, contribution);
    }
    if (_ssgi_refprobe_count > 7) {
        SampleReflectionProbe(_ssgi_refprobe_texture_7, _ssgi_refprobe_center_7, _ssgi_refprobe_source_7, _ssgi_refprobe_extents_7, _ssgi_refprobe_params_7, _ssgi_refprobe_rayinfo_7, uv, worldPos, worldNormal, resultColor, resultLightDir, contribution);
    }

    //Divide by contribution
    resultColor /= contribution;
    
    //Apply color grading
    float lum = (resultColor.r + resultColor.g + resultColor.b) / 3.0;
    resultColor = pow(lerp(lum, resultColor, saturation), exp);
    resultColor = max(float3(0.0, 0.0, 0.0), resultColor * intensity);
    resultLightDir = normalize(resultLightDir) * intensity;
}


float3 GetAlbedo(float2 uv, float3 screenNormal, float3 screenColor, float aaMask, out float occlusion) {
    float3 albedo = 0.0;

    if (_use_deferred) {
        albedo = tex2D(_GBuffer0, uv).rgb;

        float lum = albedo.r + albedo.g + albedo.b;
        float4 g1 = tex2D(_GBuffer1, uv);
        occlusion = g1.a;
        albedo = lerp(albedo, (albedo / lum) * _deferred_specular_tint, g1.r);
        
    } else {
        occlusion = 1.0;

        //Fake albedo in Forward-mode
        float screenLum = max(screenColor.r, max(screenColor.g, screenColor.b));
        screenLum = saturate((screenColor.r + screenColor.g + screenColor.b) / 3.0);
        albedo = lerp(saturate(screenColor), normalize(screenColor), screenLum * screenLum);
        
        float invScreenLum = 1.0 - screenLum;
        invScreenLum *= invScreenLum;
        invScreenLum *= invScreenLum;
        albedo = lerp(albedo, albedo * (1.0 - invScreenLum), _forward_albedo_contrast);

        //Subtract fog color
        if (_forward_albedo_subtract_fog > 0.0) {
            albedo = max(0.0, albedo - (unity_FogColor.rgb * _forward_albedo_subtract_fog * screenLum));
        }

        //Subtract sky color
        if(_forward_albedo_subtract_sky){
            albedo = max(0.0, albedo - (unity_AmbientSky.rgb * _forward_albedo_subtract_sky * screenLum));
        }
        
        //Boost albedo
        if(_albedo_boost > 0.0){
            float4 mippedScreenColor = tex2Dlod(_MF_SSGI_ScreenCapture, float4(uv, 0.0, _albedo_boost_miplevel));
            albedo = lerp(albedo, albedo / (max(mippedScreenColor.r, max(mippedScreenColor.g, mippedScreenColor.b)) * 3.0), _albedo_boost * aaMask);
        }
    }

    if (_albedo_min_whiteness > 0.0) {
        albedo = lerp(albedo, 1.0, _albedo_min_whiteness);
    }

    return albedo;
}