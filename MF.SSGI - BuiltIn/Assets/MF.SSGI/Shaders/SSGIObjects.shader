Shader "MF_SSGI/SSGIObjects"
{
    //Writes the per-object SSGI override mask into _MF_SSGI_SSGIObjects.
    //Per-renderer values come from the SSGIObject MonoBehaviour's MaterialPropertyBlock.
    //
    //Output (RGBA halfHDR):
    //  R = max(emitIntensity, 0) * omniDirectional   (omniDir is +1 normal, -1 omnidirectional;
    //                                                 negative R signals "omni emit" to CaptureLight)
    //  G = lightReceiveIntensity                     (multiplied into final SSGI color in FinalBlit)
    //  B = shadowReceiveIntensity                    (used as shadow modulation in FinalBlit)
    //  A = 1 (or clipped via software depth test)
    //
    //Software depth test against _MF_SSGI_Normals_HQ.a clips fragments whose world-forward depth
    //doesn't match the cached scene depth — without this, occluded back-of-mesh fragments would
    //write the override mask through whatever's in front (causing "see-through" artifacts).
    Properties { }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        Cull Back
        ZWrite On
        ZTest LEqual

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            //Per-renderer (set on MaterialPropertyBlock by SSGIObject)
            float _ClipDepthBias;
            float _SSGIEmitIntensity;
            float _SSGIOmniDirectional;
            float _SSGILightReceiveIntensity;
            float _SSGIShadowReceiveIntensity;
            float _AlphaTestCutoff;
            sampler2D _AlphaTestTexture;
            float4 _AlphaTestTexture_ST;

            //Globals (set by SSGIPass)
            sampler2D _MF_SSGI_Normals_HQ;
            float _default_clip_depth_bias;
            float3 _cam_world_position;
            float3 _cam_world_forward;

            struct appdata {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };
            struct v2f {
                float4 pos : SV_POSITION;
                float4 screenPos : TEXCOORD0;
                float3 worldPos : TEXCOORD1;
                float2 uv : TEXCOORD2;
            };

            v2f vert(appdata v) {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.screenPos = ComputeScreenPos(o.pos);
                o.uv = TRANSFORM_TEX(v.uv, _AlphaTestTexture);
                return o;
            }

            float4 frag(v2f i) : SV_Target {
                float2 screenUV = i.screenPos.xy / i.screenPos.w;
                float fragDepth = dot(i.worldPos - _cam_world_position, _cam_world_forward);

                //Software depth test against HQ cached depth
                float bias = ((_ClipDepthBias < 0.0) ? _default_clip_depth_bias : _ClipDepthBias) + 1.0;
                float cachedDepth = tex2D(_MF_SSGI_Normals_HQ, screenUV).a;
                bool depthMatch = (cachedDepth * bias > fragDepth) && (cachedDepth / bias < fragDepth);

                //Optional alpha-cutout (SSGIObject sets _AlphaTestTexture only when applyAlphaClipping is on)
                float texAlpha = tex2D(_AlphaTestTexture, i.uv).a;
                float alpha = (depthMatch ? 1.0 : 0.0) * texAlpha;
                clip(alpha - _AlphaTestCutoff);

                //RGB = (emit*omniDir, lightReceive, shadowReceive). Sign of R encodes omnidirectional emit.
                float r = max(_SSGIEmitIntensity, 0.0) * _SSGIOmniDirectional;
                return float4(r, _SSGILightReceiveIntensity, _SSGIShadowReceiveIntensity, 1.0);
            }
            ENDCG
        }
    }
}
