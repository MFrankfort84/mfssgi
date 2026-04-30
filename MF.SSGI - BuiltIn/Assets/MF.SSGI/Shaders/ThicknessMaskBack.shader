Shader "MF_SSGI/ThicknessMaskBack"
{
    //Renders BACK faces (Cull Front) of thickness-mask geometry. Output is the world-distance
    //along camera-forward of each back-face fragment, written to an RHalf RT.
    //Used by ThicknessMaskFront to compute object thickness (front->back) for SSGI raymarched shadows.
    Properties { }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        Cull Front
        ZWrite On
        ZTest LEqual

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            //Globals (set by SSGIPass.WriteThicknessMask)
            float _object_thickness_pivot_vs_normal;
            float _thickness_mask_expand;
            float3 _cam_world_position;
            float3 _cam_world_forward;

            struct appdata {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
            };
            struct v2f {
                float4 pos : SV_POSITION;
                float3 worldPos : TEXCOORD0;
            };

            v2f vert(appdata v) {
                v2f o;
                //Expand vertex outward — direction is a lerp of "from-object-pivot" and "world-normal".
                //Rasterization uses the EXPANDED silhouette, but the depth written must be the depth
                //of the ORIGINAL un-expanded surface so it can be matched against _MF_SSGI_Normals_*.a.
                float3 dirPivot = normalize(mul(unity_ObjectToWorld, float4(v.vertex.xyz, 0)).xyz);
                float3 dirNormal = normalize(UnityObjectToWorldNormal(v.normal));
                float3 dirWS = lerp(dirPivot, dirNormal, _object_thickness_pivot_vs_normal);
                float3 dirOS = mul(unity_WorldToObject, float4(dirWS * _thickness_mask_expand, 0)).xyz;

                o.worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;                            //un-expanded
                o.pos = UnityObjectToClipPos(float4(v.vertex.xyz + dirOS, 1.0));                //expanded
                return o;
            }

            float frag(v2f i) : SV_Target {
                //Linear "depth along camera forward" of this back-face fragment
                return dot(i.worldPos - _cam_world_position, _cam_world_forward);
            }
            ENDCG
        }
    }
}
