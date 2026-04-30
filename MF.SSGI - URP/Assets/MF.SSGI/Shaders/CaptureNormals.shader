Shader "MF_SSGI/CaptureNormals"
{
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
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT

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

            extern sampler2D _CameraNormalsTexture;
            extern float4 _CameraNormalsTexture_ST;

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

            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _CameraNormalsTexture);
                return o;
            }

            float4 frag (v2f i) : SV_Target {
                float4 normals = tex2D(_CameraNormalsTexture, i.uv);
                normals.a = tex2D(_WorldPositions, i.uv).a;

#if defined(_GBUFFER_NORMALS_OCT)
                half2 remappedOctNormalWS = Unpack888ToFloat2(normals.xyz); // values between [ 0,  1]
                half2 octNormalWS = remappedOctNormalWS.xy * 2.0h - 1.0h;    // values between [-1, +1]
                return float4(UnpackNormalOctQuadEncode(octNormalWS), normals.a);
#else
                return normals;
#endif
            }
            ENDCG
        }
    }
}

