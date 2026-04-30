Shader "MF_SSGI/DebugBlit"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
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
            
            extern int _debug_mode;
            extern float _debug_range;
            extern float3 _debug_color_mask;


            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                return o;
            }

            float4 frag(v2f i) : SV_Target {
                //Color
                if (_debug_mode == 0) {
                    return float4((abs(tex2D(_MainTex, i.uv).rgb) * _debug_color_mask) / _debug_range, 1.0);
                }

                //Alpha
                if (_debug_mode == 1) {
                    float alpha = tex2D(_MainTex, i.uv).a / _debug_range;
                    return float4(alpha, alpha, alpha, 1.0);
                }

                //Decode Color
                if (_debug_mode == 2) {
                    //return tex2D(_MainTex, i.uv);
                    return float4(DecodeFloat2ToColor(tex2D(_MainTex, i.uv).rg) / _debug_range, 1.0);
                }

                //Decode Normal
                if (_debug_mode == 3) {
                    return float4(abs(DecodeFloatToNormal(tex2D(_MainTex, i.uv).b)), 1.0);
                }

                //Fallback
                return 0.0;
            }
            ENDCG
        }
    }
}
