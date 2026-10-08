Shader "OpenOita/V2/WorldPage"
{
    Properties
    {
        _Palette ("Material Palette", 2D) = "white" {}
    }

    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" "RenderPipeline"="UniversalPipeline" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Off

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct TileInstance
            {
                float4 positionSize;
                float4 rotation;
                uint layer;
                uint active;
                uint body;
                uint reserved;
            };

            struct BodyPose
            {
                float4 positionRotation;
            };

            Texture2DArray<uint> _PagePixels;
            StructuredBuffer<TileInstance> _TileInstances;
            StructuredBuffer<BodyPose> _BodyPoses;
            Texture2D<float4> _Palette;
            float4 _WorldRect;
            float _EnableWorldCull;

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                nointerpolation uint layer : TEXCOORD1;
            };

            float2 QuadUv(uint vertex)
            {
                if (vertex == 0) return float2(0, 0);
                if (vertex == 1) return float2(1, 0);
                if (vertex == 2) return float2(1, 1);
                if (vertex == 3) return float2(0, 0);
                if (vertex == 4) return float2(1, 1);
                return float2(0, 1);
            }

            float2 BodyWorld(BodyPose body, float2 local)
            {
                return body.positionRotation.xy + float2(
                    local.x * body.positionRotation.w - local.y * body.positionRotation.z,
                    local.x * body.positionRotation.z + local.y * body.positionRotation.w);
            }

            bool TileVisible(TileInstance tile, BodyPose body)
            {
                float2 p0 = BodyWorld(body, tile.positionSize.xy);
                float2 p1 = BodyWorld(body, tile.positionSize.xy + float2(tile.positionSize.z, 0));
                float2 p2 = BodyWorld(body, tile.positionSize.xy + float2(0, tile.positionSize.z));
                float2 p3 = BodyWorld(body, tile.positionSize.xy + tile.positionSize.zz);
                float2 minimum = min(min(p0, p1), min(p2, p3));
                float2 maximum = max(max(p0, p1), max(p2, p3));
                return maximum.x >= _WorldRect.x && minimum.x <= _WorldRect.z &&
                    maximum.y >= _WorldRect.y && minimum.y <= _WorldRect.w;
            }

            Varyings vert(uint vertex : SV_VertexID, uint instance : SV_InstanceID)
            {
                Varyings output;
                TileInstance tile = _TileInstances[instance];
                BodyPose body = _BodyPoses[tile.body];
                float2 uv = QuadUv(vertex);
                float2 local = tile.positionSize.xy + uv * tile.positionSize.z;
                float2 world = BodyWorld(body, local);
                bool visible = tile.active != 0 && (_EnableWorldCull == 0 || TileVisible(tile, body));
                output.positionCS = !visible ? float4(0, 0, 0, 0) :
                    TransformWorldToHClip(float3(world, 0));
                output.uv = uv;
                output.layer = tile.layer;
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                uint2 pixel = min((uint2)(input.uv * 128), uint2(127, 127));
                uint packed = _PagePixels.Load(int4(pixel, input.layer, 0));
                uint materialId = packed & 0xffff;
                if (materialId == 0) return half4(0, 0, 0, 0);
                uint2 palettePixel = uint2(materialId & 255, materialId >> 8);
                half4 color = _Palette.Load(int3(palettePixel, 0));
                uint fuelBin = (packed >> 17) & 0xff;
                half brightness = 0.35h + 0.65h * (fuelBin / 255.0h);
                return half4(color.rgb * brightness, color.a);
            }
            ENDHLSL
        }
    }
}
