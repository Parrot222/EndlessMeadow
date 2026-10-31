Shader "Endless Meadow/Far Grass"
{
    // Lightweight ray-marched far grass. A camera-centred ring mesh is laid on the canopy top (terrain
    // height from the GPU heightmap + the average blade height). Each pixel marches a few steps from
    // that top surface down along the view ray through a slab of grass "fog": blades are upright, so
    // a ray across the slab meets their sides and one looking down only their tops. The march stops
    // at the ground; whatever is left shows the terrain. Colour and occlusion follow the height inside
    // the canopy with the blades' own colour law, so grazing views see bright tips and steep views
    // the darker lower blades and the ground between them. Fades in exactly where the blades fade out.
    // Drawn by MeadowGrass; every parameter comes from its MaterialPropertyBlock.
    Properties {}
    SubShader
    {
        Tags {"RenderPipeline"="UniversalPipeline" "Queue"="Transparent-100" "RenderType"="Transparent"}
        Pass
        {
            Name "ForwardLit"
            Tags {"LightMode"="UniversalForward"}
            Blend One OneMinusSrcAlpha
            ZWrite Off Cull Off
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fog
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "MeadowGrassCommon.hlsl"

            TEXTURE2D(_Heights); SAMPLER(sampler_Heights);
            TEXTURE2D(_Control0); TEXTURE2D(_Control1); SAMPLER(sampler_Control0);
            float4 _TerrainOrigin,_TerrainSize;   // xyz
            float4 _HeightsInfo;                  // xy heightmap size, z height scale, w lift (m)
            float4 _ControlInfo;                  // xy control size, z use layers, w max slope (tangent)
            float4 _LayerGrass0,_LayerGrass1;
            float4 _CameraPositionWS;
            float4 _Fade;                         // x fade start, y fade end (blades out / far grass in), z far end
            float4 _Canopy;                       // x canopy height (m), y side extinction, z top extinction (1/m), w steps
            float4 _Motion;                       // x root occlusion, z normal-up blend, w wave brightness
            float4 _Wind,_WindDirection,_WindNoise;
            float4 _RootColor,_TipColor,_PatchColor;
            float4 _Look;                         // x patch frequency, y patch strength

            float Height(float2 uv)
            {
                float2 texel=(uv*(_HeightsInfo.xy-1)+.5)/_HeightsInfo.xy;
                return SAMPLE_TEXTURE2D_LOD(_Heights,sampler_Heights,texel,0).r*_HeightsInfo.z;
            }

            #define MIN_PIXELS 3      // the canopy keeps at least this many pixels of height on screen
            #define TUFT_CELL .8      // smallest tuft size (m)
            #define TUFT_PIXELS 4     // a tuft stays about this many pixels wide
            #define TUFT_MAX 1.3      // tallest tuft relative to the canopy height

            // Metres one screen pixel covers at a distance.
            float PixelSize(float distance)
            {
                return distance*2/(abs(UNITY_MATRIX_P._m11)*_ScreenParams.y);
            }

            // The real average blade height where the blades hand over; further out the canopy keeps a few
            // pixels of height on screen, so the field still reads as a layer with depth instead of paint.
            float CanopyHeight(float distance)
            {
                float visible=max(_Canopy.x,MIN_PIXELS*PixelSize(distance));
                return lerp(_Canopy.x,visible,smoothstep(_Fade.x,_Fade.y*2,distance));
            }

            // Tufts: the canopy top rises and falls clump by clump. The tuft size grows in octaves with
            // distance (cross-faded between two) so a tuft stays a few pixels wide without shimmering.
            float TuftLevel(float distance)
            {
                return max(0,log2(TUFT_PIXELS*PixelSize(distance)/TUFT_CELL));
            }
            float TuftTop(float2 xz,float level)
            {
                float octave=floor(level);
                float a=SGNoise(xz/(TUFT_CELL*exp2(octave))+5.1);
                float b=SGNoise(xz/(TUFT_CELL*exp2(octave+1))+5.1);
                return lerp(.35,TUFT_MAX,smoothstep(.15,.85,lerp(a,b,level-octave)));
            }

            // Ground under a world position. The lift grows with distance: the terrain renders simplified
            // LODs far away and must not poke through.
            float Ground(float2 xz)
            {
                float2 uv=saturate((xz-_TerrainOrigin.xz)/_TerrainSize.xz);
                return _TerrainOrigin.y+Height(uv)+_HeightsInfo.w+length(xz-_CameraPositionWS.xz)*.002;
            }

            // Same placement rule as the blades: the terrain-layer grass amounts.
            float LayerAmount(float2 xz)
            {
                if(_ControlInfo.z<.5) return 1;
                float2 uv=saturate((xz-_TerrainOrigin.xz)/_TerrainSize.xz);
                float2 cuv=(uv*(_ControlInfo.xy-1)+.5)/_ControlInfo.xy;
                return saturate(dot(SAMPLE_TEXTURE2D_LOD(_Control0,sampler_Control0,cuv,0),_LayerGrass0)
                               +dot(SAMPLE_TEXTURE2D_LOD(_Control1,sampler_Control0,cuv,0),_LayerGrass1));
            }

            struct Attributes {float3 positionOS:POSITION;};
            struct Varyings
            {
                float4 positionCS:SV_POSITION;float3 world:TEXCOORD0;float3 normal:TEXCOORD1;
                float fog:TEXCOORD2;float inside:TEXCOORD3;
            };

            Varyings Vert(Attributes i)
            {
                float3 world=TransformObjectToWorld(i.positionOS);
                float2 uv=(world.xz-_TerrainOrigin.xz)/_TerrainSize.xz;
                float inside=all(uv>=0) && all(uv<=1)?1:0;
                uv=saturate(uv);
                // Normal from the heightmap (central differences, one texel).
                float2 texel=1/max(_HeightsInfo.xy-1,1);
                float2 metres=_TerrainSize.xz*texel;
                float dx=(Height(uv+float2(texel.x,0))-Height(uv-float2(texel.x,0)))/(2*metres.x);
                float dz=(Height(uv+float2(0,texel.y))-Height(uv-float2(0,texel.y)))/(2*metres.y);
                world.y=Ground(world.xz)+CanopyHeight(length(world.xz-_CameraPositionWS.xz))*TUFT_MAX;
                Varyings o;
                o.positionCS=TransformWorldToHClip(world);
                o.world=world;o.normal=normalize(float3(-dx,1,-dz));o.inside=inside;
                o.fog=ComputeFogFactor(o.positionCS.z);
                return o;
            }

            half4 Frag(Varyings i):SV_Target
            {
                float distance=length(i.world.xz-_CameraPositionWS.xz);
                // Complementary to the blades' dithered fade-out; thins out again at the far end.
                float fade=smoothstep(_Fade.x,_Fade.y,distance)*(1-smoothstep(_Fade.z*.85,_Fade.z,distance))*step(.5,i.inside);
                float slope=sqrt(max(0,1/(i.normal.y*i.normal.y)-1));
                fade*=1-smoothstep(_ControlInfo.w*.9,_ControlInfo.w*1.1,slope);
                clip(fade-.01);

                float3 rayDir=normalize(i.world-_CameraPositionWS.xyz);
                float down=max(-rayDir.y,.002);
                float across=sqrt(saturate(1-rayDir.y*rayDir.y));
                float2 p=i.world.xz;

                // Wave crests press the grass over: lower canopy, more blade area seen from above,
                // and more of every blade's bright upper part on show.
                float wave=SGWave(p,_WindDirection.xy,_Wind,_WindNoise.xyz)*_Motion.w;
                float canopy=CanopyHeight(distance)*(1-.25*wave);
                float tuftLevel=TuftLevel(distance);
                // A thickened canopy keeps the optical depth of the real one (same coverage, more depth).
                float extinction=(_Canopy.y*across+_Canopy.z*(1+2*wave)*down)*_Canopy.x/canopy;

                // March from the top of the tallest tufts until the ground, or until practically opaque.
                // The start is jittered per pixel so the steps do not show as layers.
                int steps=(int)_Canopy.w;
                float marchLength=min(canopy*TUFT_MAX/down,8/max(extinction,1e-3));
                float stepLength=marchLength/steps;
                float jitter=frac(52.9829189*frac(dot(i.positionCS.xy,float2(.06711056,.00583715))));

                // Per-pixel parts of the blades' colour law and lighting.
                float3 tip=SGAlbedo(p,1,.5,_RootColor.rgb,_TipColor.rgb,_PatchColor.rgb,_Look.xy,0);
                float grain=lerp(.9,1.08,SGNoise(p*1.7))*lerp(.94,1.04,SGNoise(p*.23+9.1))*(1+wave*.5);
                float3 n=normalize(lerp(i.normal,float3(0,1,0),_Motion.z));
                Light light=GetMainLight(TransformWorldToShadowCoord(i.world));
                float3 ambient=SampleSH(n);
                float3 sun=light.color*(.35+.65*saturate(dot(n,light.direction)))*light.shadowAttenuation;

                float transmittance=1;
                float3 color=0;
                [loop] for(int s=0;s<steps;s++)
                {
                    float3 position=i.world+rayDir*(stepLength*(s+jitter));
                    float above=position.y-Ground(position.xz);
                    if(above<0) break;
                    float top=canopy*TuftTop(position.xz,tuftLevel);
                    if(above>=top) continue;
                    // Inside a tuft: blades are widest low down and taper to the tips (profile averages 1;
                    // 1.25 makes up for the air between the tufts).
                    float t=above/top;
                    float density=extinction*1.7*1.25*pow(1-t,.7)*LayerAmount(position.xz);
                    float alpha=1-exp(-density*stepLength);
                    // Colour and occlusion by height above the ground: tall tufts catch bright tips,
                    // the gaps between them stay dark.
                    float h=saturate(lerp(.25,.85,above/canopy)+wave*.3);
                    float occlusion=1-_Motion.x*pow(1-h,1.6);
                    float3 albedo=lerp(_RootColor.rgb,tip,pow(h,.75))*grain;
                    color+=transmittance*alpha*albedo*(ambient*occlusion+sun*lerp(1,occlusion,.55));
                    transmittance*=1-alpha;
                    if(transmittance<.02) break;
                }

                float coverage=1-transmittance;
                if(coverage<.001) discard;
                // Fog the average colour, then premultiply by the faded coverage.
                float3 average=MixFog(color/coverage,i.fog);
                float alphaOut=coverage*fade;
                return half4(average*alphaOut,alphaOut);
            }
            ENDHLSL
        }
    }
}
