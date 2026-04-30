Shader "MF_SSGI/ExpandVertices"
{
    //Re-renders SSGIObjects with expanded vertices INTO _MF_SSGI_LightCapture, so thin emissive
    //meshes (lightbars, neons, etc.) cover more screen pixels and the SSGI gather can sample them.
    //
    //Vertex: expand outward by _SSGIVertexExpand (per-renderer), modulated by view distance via
    //(_ExpandRangeMin..Max). Direction is a lerp of object-space normal and "from-pivot" radial dir
    //based on _ExpandNormalVsVertexPos.
    //
    //Fragment: software depth test against _MF_SSGI_Normals_LQ.a so we only write where the
    //expanded geometry's expected depth matches the cached scene depth. The output is
    //screen color * SSGIObjects.r (per-object emit*omniDir intensity), which is what the SSGI
    //gather treats as a light source.
    Properties { }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        Cull Back
        ZWrite Off
        ZTest Always

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            //Material-level (set via Material.SetFloat by SSGIPass)
            float _ExpandRangeMin;
            float _ExpandRangeMax;

            //Per-renderer (MaterialPropertyBlock)
            float3 _SSGIVertexExpand;
            float3 _SSGIVertexOffset;
            float _ExpandNormalVsVertexPos;
            float _ClipDepthBias;

            //Globals
            sampler2D _MF_SSGI_ScreenCapture;
            sampler2D _MF_SSGI_SSGIObjects;
            sampler2D _MF_SSGI_Normals_LQ;
            float _default_clip_depth_bias;
            float3 _cam_world_position;
            float3 _cam_world_forward;

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

                //Direction in object space: lerp(normal, normalized vertex-from-pivot)
                float3 dirRadialOS = normalize(v.vertex.xyz);
                float3 dirOS = lerp(v.normal, dirRadialOS, _ExpandNormalVsVertexPos);

                //Distance falloff: 0 inside _ExpandRangeMin, 1 past _ExpandRangeMax
                float3 viewPos = UnityObjectToViewPos(v.vertex.xyz);
                float eyeDepth = -viewPos.z; //Unity view-space has -Z forward
                float falloff = saturate((eyeDepth - _ExpandRangeMin) / max(1e-5, _ExpandRangeMax - _ExpandRangeMin));

                float3 expand = (dirOS * _SSGIVertexExpand * falloff) + _SSGIVertexOffset;

                //Rasterize EXPANDED silhouette so the emit mask covers a fatter footprint, but
                //write the UN-EXPANDED surface depth into worldPos — the software depth-match in
                //frag compares against _MF_SSGI_Normals_LQ.a, which records the actual surface depth.
                o.worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;                            //un-expanded
                o.pos = UnityObjectToClipPos(float4(v.vertex.xyz + expand, 1.0));               //expanded
                o.screenPos = ComputeScreenPos(o.pos);
                return o;
            }

            float4 frag(v2f i) : SV_Target {
                float2 screenUV = i.screenPos.xy / i.screenPos.w;
                float fragDepth = dot(i.worldPos - _cam_world_position, _cam_world_forward);

                //Software depth test against the LQ cached depth
                float bias = ((_ClipDepthBias < 0.0) ? _default_clip_depth_bias : _ClipDepthBias) + 1.0;
                float cachedDepth = tex2D(_MF_SSGI_Normals_LQ, screenUV).a;
                bool depthMatch = (cachedDepth * bias > fragDepth) && (cachedDepth / bias < fragDepth);
                clip(depthMatch ? 1.0 : -1.0);

                //Output: this object's contribution into the SSGI light capture
                float3 screenColor = tex2D(_MF_SSGI_ScreenCapture, screenUV).rgb;
                float emitMask = tex2D(_MF_SSGI_SSGIObjects, screenUV).r;
                return float4(screenColor * emitMask, 1.0);
            }
            ENDCG
        }
    }
}
