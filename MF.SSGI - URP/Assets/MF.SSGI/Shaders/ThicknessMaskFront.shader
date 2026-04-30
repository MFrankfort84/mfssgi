Shader "MF_SSGI/ThicknessMaskFront"
{
    //Renders FRONT faces of thickness-mask geometry, sampling the back-face depth from
    //_MF_SSGI_ThicknessMask_Prepass to compute object thickness (back - front).
    //
    //Output (RGHalf):
    //  R = thickness = |backDepth - frontDepth| + _object_min_thickness + per-renderer additional
    //  G = _SSGICastShadows (per-renderer flag)
    //  B = unused
    //
    //Software depth test against _MF_SSGI_Normals_LQ.a clips fragments that don't match the cached
    //world-forward depth — keeps the mask aligned with what's actually visible.
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

            //Globals
            float _object_thickness_pivot_vs_normal;
            float _thickness_mask_expand;
            float _object_min_thickness;
            float _default_clip_depth_bias;
            float3 _cam_world_position;
            float3 _cam_world_forward;
            sampler2D _MF_SSGI_ThicknessMask_Prepass;
            sampler2D _MF_SSGI_Normals_LQ;

            //Per-renderer (MaterialPropertyBlock)
            float _ClipDepthBias;
            float _SSGIAdditionalThickness;
            float _SSGICastShadows;

            struct appdata {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
            };
            struct v2f {
                float4 pos : SV_POSITION;
                float4 screenPos : TEXCOORD0;
                float3 worldPos : TEXCOORD1;
            };

            v2f vert(appdata v) {
                v2f o;
                //Rasterize the EXPANDED silhouette so the mask covers a slightly larger footprint,
                //but write the un-expanded surface depth — the software depth-match in frag compares
                //against _MF_SSGI_Normals_LQ.a, which records the actual surface depth.
                float3 dirPivot = normalize(mul(unity_ObjectToWorld, float4(v.vertex.xyz, 0)).xyz);
                float3 dirNormal = normalize(UnityObjectToWorldNormal(v.normal));
                float3 dirWS = lerp(dirPivot, dirNormal, _object_thickness_pivot_vs_normal);
                float3 dirOS = mul(unity_WorldToObject, float4(dirWS * _thickness_mask_expand, 0)).xyz;

                o.worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;                            //un-expanded
                o.pos = UnityObjectToClipPos(float4(v.vertex.xyz + dirOS, 1.0));                //expanded
                o.screenPos = ComputeScreenPos(o.pos);
                return o;
            }

            float4 frag(v2f i) : SV_Target {
                float2 screenUV = i.screenPos.xy / i.screenPos.w;
                float frontDepth = dot(i.worldPos - _cam_world_position, _cam_world_forward);

                //Software depth test — reject fragments that don't match the cached LQ depth
                float bias = ((_ClipDepthBias < 0.0) ? _default_clip_depth_bias : _ClipDepthBias) + 1.0;
                float cachedDepth = tex2D(_MF_SSGI_Normals_LQ, screenUV).a;
                bool depthMatch = (cachedDepth * bias > frontDepth) && (cachedDepth / bias < frontDepth);
                clip(depthMatch ? 1.0 : -1.0);

                //Thickness = |back-face depth - front-face depth| + minimum + per-renderer additional
                float backDepth = tex2D(_MF_SSGI_ThicknessMask_Prepass, screenUV).r;
                float thickness = abs(backDepth - frontDepth) + _object_min_thickness + _SSGIAdditionalThickness;

                return float4(thickness, _SSGICastShadows, 0.0, 1.0);
            }
            ENDCG
        }
    }
}
