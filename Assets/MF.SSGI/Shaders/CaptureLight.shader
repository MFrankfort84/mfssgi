Shader "MF_SSGI/CaptureLight"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
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

            sampler2D _WorldPositions;
            float4 _WorldPositions_ST;

            extern sampler2D _MF_SSGI_SSGIObjects; 
            extern float4 _MF_SSGI_SSGIObjects_ST;

            extern samplerCUBE _reflection_probe;
            extern float4 _reflection_probe_ST;

            //Fallback
            extern int _use_ssgi_objects = 0;
            extern float _ssgi_fallback_indirect_intensity = 1.0;
            extern float _ssgi_fallback_indirect_saturation = 1.0;
            extern float _ssgi_fallback_indirect_power = 1.0;


            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                return o;
            }

            float4 frag(v2f i) : SV_Target{
                float4 resultColor = max(0.0, tex2D(_MainTex, i.uv));
                
                //Add fallback Reflection-probe light
                if (_ssgi_fallback_indirect_intensity > 0.0) {
                    float3 refColor;
                    float3 refLightDir;
                    float occlusion;
                    SampleReflectionProbes(refColor, refLightDir, tex2D(_WorldPositions, i.uv), tex2D(_MF_SSGI_Normals_LQ, i.uv), i.uv, _ssgi_fallback_indirect_intensity, _ssgi_fallback_indirect_saturation, _ssgi_fallback_indirect_power);
                    
                    float3 albedo = GetAlbedo(i.uv, tex2D(_MF_SSGI_Normals_LQ, i.uv).rgb, resultColor, 1.0, occlusion);
                    resultColor.rgb = max(resultColor, refColor.rgb * albedo * occlusion);
                }

                //Apply SSGIObjects Emmit intenity
                if (_use_ssgi_objects == 1.0) {
                    resultColor.rgb *= tex2D(_MF_SSGI_SSGIObjects, i.uv).r;
                }
                return resultColor;
            }
            ENDCG
        }
    }
}
