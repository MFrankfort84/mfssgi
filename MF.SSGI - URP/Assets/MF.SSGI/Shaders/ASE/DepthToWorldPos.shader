// Made with Amplify Shader Editor v1.9.1.3
// Available at the Unity Asset Store - http://u3d.as/y3X 
Shader "MF_SSGI/DepthToWorldPos"
{
	Properties
	{
		_MainTex ("Sprite Texture", 2D) = "white" {}
		_Color ("Tint", Color) = (1,1,1,1)
		
	}
	
	SubShader
	{
		Tags { "RenderType"="Opaque" }
	LOD 100
		Cull Off
		ZTest Always
		ZWrite Off

		
		Pass
		{
			CGPROGRAM
			
			#ifndef UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX
			//only defining to not throw compilation error over Unity 5.5
			#define UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input)
			#endif

			#pragma target 3.0 
			#pragma vertex vert
			#pragma fragment frag
			#include "UnityCG.cginc"
			#include "UnityShaderVariables.cginc"


			struct appdata
			{
				float4 vertex : POSITION;
				float4 texcoord : TEXCOORD0;
				float4 texcoord1 : TEXCOORD1;
				UNITY_VERTEX_INPUT_INSTANCE_ID
				
			};
			
			struct v2f
			{
				float4 vertex : SV_POSITION;
				float4 texcoord : TEXCOORD0;
				UNITY_VERTEX_INPUT_INSTANCE_ID
				UNITY_VERTEX_OUTPUT_STEREO
				
			};

			uniform sampler2D _MainTex;
			uniform fixed4 _Color;
			UNITY_DECLARE_DEPTH_TEXTURE( _CameraDepthTexture );
			uniform float4 _CameraDepthTexture_TexelSize;
			uniform float3 _cam_world_position;
			uniform float3 _cam_world_forward;
			float2 UnStereo( float2 UV )
			{
				#if UNITY_SINGLE_PASS_STEREO
				float4 scaleOffset = unity_StereoScaleOffset[ unity_StereoEyeIndex ];
				UV.xy = (UV.xy - scaleOffset.zw) / scaleOffset.xy;
				#endif
				return UV;
			}
			
			float3 InvertDepthDir72_g6( float3 In )
			{
				float3 result = In;
				#if !defined(ASE_SRP_VERSION) || ASE_SRP_VERSION <= 70301
				result *= float3(1,1,-1);
				#endif
				return result;
			}
			

			
			v2f vert ( appdata v )
			{
				v2f o;
				UNITY_SETUP_INSTANCE_ID(v);
				UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
				UNITY_TRANSFER_INSTANCE_ID(v, o);
				o.texcoord.xy = v.texcoord.xy;
				o.texcoord.zw = v.texcoord1.xy;
				
				// ase common template code
				
				
				v.vertex.xyz +=  float3(0,0,0) ;
				o.vertex = UnityObjectToClipPos(v.vertex);
				return o;
			}
			
			fixed4 frag (v2f i ) : SV_Target
			{
				UNITY_SETUP_INSTANCE_ID(i);
				UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);

				fixed4 myColorVar;
				// ase common template code
				float2 temp_output_76_0_g6 = i.texcoord.xy;
				float2 UV22_g7 = float4( temp_output_76_0_g6, 0.0 , 0.0 ).xy;
				float2 localUnStereo22_g7 = UnStereo( UV22_g7 );
				float2 break64_g6 = localUnStereo22_g7;
				float clampDepth69_g6 = SAMPLE_DEPTH_TEXTURE( _CameraDepthTexture, float4( temp_output_76_0_g6, 0.0 , 0.0 ).xy );
				#ifdef UNITY_REVERSED_Z
				float staticSwitch38_g6 = ( 1.0 - clampDepth69_g6 );
				#else
				float staticSwitch38_g6 = clampDepth69_g6;
				#endif
				float3 appendResult39_g6 = (float3(break64_g6.x , break64_g6.y , staticSwitch38_g6));
				float4 appendResult42_g6 = (float4((appendResult39_g6*2.0 + -1.0) , 1.0));
				float4 temp_output_43_0_g6 = mul( unity_CameraInvProjection, appendResult42_g6 );
				float3 temp_output_46_0_g6 = ( (temp_output_43_0_g6).xyz / (temp_output_43_0_g6).w );
				float3 In72_g6 = temp_output_46_0_g6;
				float3 localInvertDepthDir72_g6 = InvertDepthDir72_g6( In72_g6 );
				float4 appendResult49_g6 = (float4(localInvertDepthDir72_g6 , 1.0));
				float3 temp_output_51_0 = (mul( unity_CameraToWorld, appendResult49_g6 )).xyz;
				float dotResult6_g9 = dot( ( temp_output_51_0 - _cam_world_position ) , _cam_world_forward );
				float4 appendResult52 = (float4(temp_output_51_0 , dotResult6_g9));
				
				
				myColorVar = appendResult52;
				return myColorVar;
			}
			ENDCG
		}
	}
	CustomEditor "ASEMaterialInspector"
	
	Fallback Off
}
/*ASEBEGIN
Version=19103
Node;AmplifyShaderEditor.TexCoordVertexDataNode;4;-1521.196,1.839534;Inherit;False;0;2;0;5;FLOAT2;0;FLOAT;1;FLOAT;2;FLOAT;3;FLOAT;4
Node;AmplifyShaderEditor.FunctionNode;6;-1278.196,2.839534;Inherit;False;Custom Reconstruct World Position From Depth;-1;;6;3a8f4deef2c70b644a669c19d80210af;0;1;76;FLOAT2;0,0;False;1;FLOAT4;0
Node;AmplifyShaderEditor.ComponentMaskNode;51;-849.6954,-2.327154;Inherit;False;True;True;True;False;1;0;FLOAT4;0,0,0,0;False;1;FLOAT3;0
Node;AmplifyShaderEditor.FunctionNode;59;-593.5974,75.24269;Inherit;False;SimpleLinearDepth;-1;;9;d85b034c2916eb140a3ba097d9e42c43;0;1;7;FLOAT3;0,0,0;False;1;FLOAT;0
Node;AmplifyShaderEditor.DynamicAppendNode;52;-281.2158,6.564423;Inherit;True;FLOAT4;4;0;FLOAT3;0,0,0;False;1;FLOAT;0;False;2;FLOAT;0;False;3;FLOAT;0;False;1;FLOAT4;0
Node;AmplifyShaderEditor.TemplateMultiPassMasterNode;50;-14,7;Float;False;True;-1;2;ASEMaterialInspector;100;18;MF_SSGI/DepthToWorldPos;6e114a916ca3e4b4bb51972669d463bf;True;SubShader 0 Pass 0;0;0;SubShader 0 Pass 0;2;False;False;False;False;False;False;False;False;False;False;False;False;False;False;True;2;False;;False;False;False;False;False;False;False;False;False;False;False;True;2;False;;True;7;False;;False;True;1;RenderType=Opaque=RenderType;False;False;0;False;False;False;False;False;False;False;False;False;False;False;False;False;False;False;False;False;False;False;False;False;False;False;False;False;False;False;False;False;False;True;2;False;0;;0;0;Standard;0;0;1;True;False;;False;0
WireConnection;6;76;4;0
WireConnection;51;0;6;0
WireConnection;59;7;51;0
WireConnection;52;0;51;0
WireConnection;52;3;59;0
WireConnection;50;0;52;0
ASEEND*/
//CHKSM=01B1D45E919918C7F419E25A539165CFAD11A2B5