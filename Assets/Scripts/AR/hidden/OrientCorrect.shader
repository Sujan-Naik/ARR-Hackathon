Shader "Hidden/OrientCorrect"
{
    Properties
    {
        _MainTex ("Source", 2D) = "white" {}
        _Angle ("Angle", Float) = 0
        _FlipY ("Flip Y", Float) = 0
    }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always

        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float _Angle;
            float _FlipY;

            fixed4 frag(v2f_img i) : SV_Target
            {
                float2 uv = i.uv;
                if (_FlipY > 0.5) uv.y = 1.0 - uv.y;

                int a = (int)round(_Angle) % 360;
                float2 src = uv;
                if (a == 90)       src = float2(1.0 - uv.y, uv.x);
                else if (a == 180) src = float2(1.0 - uv.x, 1.0 - uv.y);
                else if (a == 270) src = float2(uv.y, 1.0 - uv.x);

                return tex2D(_MainTex, src);
            }
            ENDCG
        }
    }
}