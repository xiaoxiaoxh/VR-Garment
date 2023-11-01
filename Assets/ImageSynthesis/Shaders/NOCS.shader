Shader "Hidden/NOCS"
{
    Properties
    {
    }
    SubShader
    {
        Cull Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 nocs1 : TEXCOORD1;
                float2 nocs2 : TEXCOORD2;
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                float2 nocs1 : TEXCOORD1;
                float2 nocs2 : TEXCOORD2;
            };


            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.nocs1 = v.nocs1;
                o.nocs2 = v.nocs2;
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                return float4(i.nocs1.x, i.nocs1.y, i.nocs2.x, 1);
            }
            ENDCG
        }
    }
}
