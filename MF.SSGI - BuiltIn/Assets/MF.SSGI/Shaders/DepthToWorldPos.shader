Shader "MF_SSGI/DepthToWorldPos"
{
    //Reads _CameraDepthTexture and outputs (worldPos.xyz, dot(worldPos - cam_world_pos, cam_world_forward)).
    //The .a channel is the linear "world-distance along camera forward" — used by the rest of MF.SSGI
    //as the cached depth for software depth tests (see SSGIObjects + thickness mask shaders).
    Properties { }
    SubShader
    {
        ZTest Always ZWrite Off Cull Off
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);
            float4 _CameraDepthTexture_TexelSize;

            float3 _cam_world_position;
            float3 _cam_world_forward;

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f    { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

            v2f vert(appdata v) {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            float4 frag(v2f i) : SV_Target {
                float rawDepth = SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, i.uv);
                #if UNITY_REVERSED_Z
                    float depth01 = 1.0 - rawDepth;
                #else
                    float depth01 = rawDepth;
                #endif

                //Reconstruct view-space position from NDC + inverse projection
                float3 ndc = float3(i.uv * 2.0 - 1.0, depth01 * 2.0 - 1.0);
                float4 viewPos = mul(unity_CameraInvProjection, float4(ndc, 1.0));
                viewPos.xyz /= viewPos.w;

                //Unity's view-space convention has -Z forward; unity_CameraToWorld expects the
                //"flipped" convention. Negate Z to match.
                viewPos.z = -viewPos.z;
                float3 worldPos = mul(unity_CameraToWorld, float4(viewPos.xyz, 1.0)).xyz;

                float forwardDepth = dot(worldPos - _cam_world_position, _cam_world_forward);
                return float4(worldPos, forwardDepth);
            }
            ENDCG
        }
    }
}
