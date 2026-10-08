Shader "OpenOita/V2/WorldFire"
{
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent+1" "RenderPipeline"="UniversalPipeline" }
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
                uint hasBurning;
            };

            struct BodyPose
            {
                float4 positionRotation;
            };

            Texture2DArray<uint> _PagePixels;
            StructuredBuffer<TileInstance> _TileInstances;
            StructuredBuffer<BodyPose> _BodyPoses;
            float4 _WorldRect;
            float _EnableWorldCull;

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                nointerpolation uint layer : TEXCOORD1;
                nointerpolation uint hasBurning : TEXCOORD2;
            };

            float2 QuadUv(uint corner)
            {
                if (corner == 0) return float2(0, 0);
                if (corner == 1) return float2(1, 0);
                if (corner == 2) return float2(1, 1);
                if (corner == 3) return float2(0, 0);
                if (corner == 4) return float2(1, 1);
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
                float height = tile.positionSize.z * (130.0 / 128.0);
                float2 p2 = BodyWorld(body, tile.positionSize.xy + float2(0, height));
                float2 p3 = BodyWorld(body, tile.positionSize.xy + float2(tile.positionSize.z, height));
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
                uv.y *= 130.0 / 128.0;
                float2 local = tile.positionSize.xy + uv * tile.positionSize.z;
                float2 world = BodyWorld(body, local);
                bool visible = tile.active != 0 && tile.hasBurning != 0 &&
                    (_EnableWorldCull == 0 || TileVisible(tile, body));
                output.positionCS = !visible ? float4(0, 0, 0, 0) :
                    TransformWorldToHClip(float3(world, -0.01));
                output.uv = uv;
                output.layer = tile.layer;
                output.hasBurning = tile.hasBurning;
                return output;
            }

            uint BurningAt(uint2 cell, uint layer)
            {
                if (cell.x >= 128 || cell.y >= 128) return 0;
                uint packed = _PagePixels.Load(int4(cell, layer, 0));
                return (packed & (1u << 16)) != 0 ? 1 : 0;
            }

            half4 frag(Varyings input) : SV_Target
            {
                if (input.hasBurning == 0) return half4(0, 0, 0, 0);
                uint2 cell = (uint2)(input.uv * 128.0);
                uint current = BurningAt(cell, input.layer);
                uint below = cell.y > 0 ? BurningAt(uint2(cell.x, cell.y - 1), input.layer) : 0;
                uint lower = cell.y > 1 ? BurningAt(uint2(cell.x, cell.y - 2), input.layer) : 0;
                if (current == 0 && below == 0 && lower == 0) return half4(0, 0, 0, 0);

                float2 cellUv = frac(input.uv * 128.0);
                float support = current + below * 0.25 + lower * 0.12;
                float wave = 0.06 * sin(_Time.y * 8 + cellUv.y * 12 + cell.x * 0.17);
                float edge = abs(cellUv.x - 0.5 + wave) * 2;
                float alpha = saturate((1 - cellUv.y - edge) * (4 + support * 2)) *
                    (0.72 + saturate(support) * 0.18);
                return half4(1, lerp(0.85, 0.25, cellUv.y), 0.05, alpha);
            }
            ENDHLSL
        }
    }
}
