Shader "MF_SSGI/TAA"
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

            sampler2D _MainTex;
            float4 _MainTex_ST;

            sampler2D _TAAHistory;
            float4 _TAAHistory_ST;

            extern sampler2D _MotionVectorTexture;
            extern sampler2D _PrevWorldPositions;
            extern sampler2D _WorldPositions;

            extern float2 _ssgi_res;
            extern float _taa_shadow_blend;     //0..1, weight on history (0.85 = strong smoothing)
            extern float _taa_world_pos_threshold;//world-space distance, reject history past this

            v2f vert(appdata v) {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                return o;
            }

            float4 frag(v2f i) : SV_Target {
                //Current frame's denoised SSGI
                float4 current = tex2D(_MainTex, i.uv);

                //Reproject to last frame via motion vector
                float2 mv = tex2D(_MotionVectorTexture, i.uv).xy;
                float2 prevUV = i.uv - mv;

                //History invalid if reprojected uv is offscreen
                if (prevUV.x < 0.0 || prevUV.x > 1.0 || prevUV.y < 0.0 || prevUV.y > 1.0) {
                    return current;
                }

                //History invalid if the world-pos at the reprojected location doesn't match current
                //(geometry has moved, occluded, disoccluded). Cheap rejection without proper depth test.
                float3 currentWorldPos = tex2D(_WorldPositions, i.uv).xyz;
                float3 prevWorldPos = tex2D(_PrevWorldPositions, prevUV).xyz;
                float3 worldDelta = currentWorldPos - prevWorldPos;
                if (dot(worldDelta, worldDelta) > _taa_world_pos_threshold * _taa_world_pos_threshold) {
                    return current;
                }

                float4 history = tex2D(_TAAHistory, prevUV);

                //Neighborhood clamp on the SHADOW channel only — reject history values that fall
                //outside the 5-tap cross of current frame's shadows. Prevents ghosting on
                //newly-shadowed or newly-lit pixels that motion vectors can't represent.
                float2 px = 1.0 / _ssgi_res;
                float a0 = current.a;
                float a1 = tex2D(_MainTex, i.uv + float2(px.x, 0.0)).a;
                float a2 = tex2D(_MainTex, i.uv - float2(px.x, 0.0)).a;
                float a3 = tex2D(_MainTex, i.uv + float2(0.0, px.y)).a;
                float a4 = tex2D(_MainTex, i.uv - float2(0.0, px.y)).a;
                float aMin = min(min(min(a0, a1), min(a2, a3)), a4);
                float aMax = max(max(max(a0, a1), max(a2, a3)), a4);
                //Slight slack so high-frequency stable noise still gets averaged
                float slack = (aMax - aMin) * 0.25;
                aMin = max(0.0, aMin - slack);
                aMax = min(2.0, aMax + slack);
                float clampedHistShadow = clamp(history.a, aMin, aMax);

                //Blend: shadow only. Color/light-dir pass through as current to avoid mis-blending
                //encoded values.
                float blendedShadow = lerp(current.a, clampedHistShadow, _taa_shadow_blend);

                #ifdef _ENCODE_LIGHTDIR
                    return float4(current.rg, current.b, blendedShadow);
                #else
                    return float4(current.rgb, blendedShadow);
                #endif
            }
            ENDCG
        }
    }
}
