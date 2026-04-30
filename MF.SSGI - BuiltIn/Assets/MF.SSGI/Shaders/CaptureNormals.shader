Shader "MF_SSGI/CaptureNormals"
{
    //Built-in Render Pipeline forward path: read view-space normals from
    //_CameraDepthNormalsTexture (requires camera.depthTextureMode |= DepthTextureMode.DepthNormals,
    //already set by SSGIController), decode, convert to world-space, write into the RT.
    //Alpha gets the linear world-space depth from the WorldPositions buffer (same as URP).
    //
    //Deferred (Phase 7) would instead read _CameraGBufferTexture2 which holds world-space normals
    //directly (no decode needed). Not implemented yet — keep _USE_DEFERRED disabled.
    Properties
    {
        _WorldPositions("_WorldPositions", 2D) = "black" {}
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

            #include "UnityCG.cginc"

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

            sampler2D _WorldPositions;
            float4 _WorldPositions_ST;

            //BIRP forward depth+normals texture. View-space normal in xy (stereographic),
            //depth01 in zw. Decode via UnityCG's DecodeDepthNormal helper.
            sampler2D _CameraDepthNormalsTexture;
            float4 _CameraDepthNormalsTexture_ST;

            v2f vert(appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            float4 frag(v2f i) : SV_Target {
                float4 packed = tex2D(_CameraDepthNormalsTexture, i.uv);

                //Decode view-space normal + linear depth01
                float depth01;
                float3 viewNormal;
                DecodeDepthNormal(packed, depth01, viewNormal);

                //View-space -> world-space.
                //BIRP's unity_CameraToWorld has a Z-flip baked in (Unity's view-space convention has
                //-Z forward, but the matrix expects +Z forward). DecodeDepthNormal gives us a normal
                //in the unflipped convention, so we negate Z before the matrix multiply, otherwise
                //world-normals come out mirrored along view-Z. Symptom: SSGIObject light emission
                //direction is wrong when omniDirectional is off (the dot(lightDir, envNormal) test
                //against _light_cast_dot_min/max fails on actually-front-facing fragments).
                viewNormal.z = -viewNormal.z;
                float3 worldNormal = normalize(mul((float3x3)unity_CameraToWorld, viewNormal));

                //World-space linear distance from our DepthToWorldPos pre-pass
                float worldDepth = tex2D(_WorldPositions, i.uv).a;
                return float4(worldNormal, worldDepth);
            }
            ENDCG
        }
    }
}
