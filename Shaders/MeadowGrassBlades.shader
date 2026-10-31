Shader "Endless Meadow/Grass Blades"
{
    // Drawn by MeadowGrass with Graphics.RenderMeshIndirect; every parameter comes from its
    // MaterialPropertyBlock. Procedural blade clumps on GPU-generated roots.
    Properties {}
    SubShader
    {
        Tags {"RenderPipeline"="UniversalPipeline" "Queue"="AlphaTest" "RenderType"="Opaque"}
        Pass
        {
            Name "ForwardLit"
            Tags {"LightMode"="UniversalForward"}
            Cull Off ZWrite On
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fog
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #define UNITY_INDIRECT_DRAW_ARGS IndirectDrawIndexedArgs
            #include "UnityIndirect.cginc"
            #include "MeadowGrassCommon.hlsl"

            StructuredBuffer<float4> _Roots;
            StructuredBuffer<uint> _Visible;
            float4 _CameraPositionWS;
            float4 _Fade;          // x fade start, y fade end (m)
            float4 _Size;          // x base height (m), y/z random height range, w/ (unused)
            float4 _WidthRange;    // x/y random clump width range
            float4 _Blade;         // x width scale, y share of blades kept far away, z/w LOD start/end (m)
            float4 _Motion;        // x per-blade sway variation, y per-blade height jitter, z normal-up blend, w wave brightness
            float4 _Wind;          // strength, speed, spatial frequency, time
            float4 _WindDirection; // xy direction, z gust bend
            float4 _WindNoise;     // x amount, y frequency (1/m), z drift (m/s)
            float4 _RootColor,_TipColor,_PatchColor;
            float4 _Look;          // x patch frequency, y patch strength, z brightness jitter, w translucency
            float4 _Shading;       // x root occlusion strength

            float3 Rotate(float3 v,float s,float c) {return float3(v.x*c+v.z*s,v.y,-v.x*s+v.z*c);}

            // tangent.xyz = blade right * half width; uv0 = (side -1..1, height 0..1);
            // uv1 = (blade order for LOD, blade random); uv2 = blade root (clump-local xz).
            struct Attributes
            {
                float3 positionOS:POSITION;float3 normalOS:NORMAL;float4 tangentOS:TANGENT;
                float2 uv:TEXCOORD0;float2 blade:TEXCOORD1;float2 bladeRoot:TEXCOORD2;uint instanceID:SV_InstanceID;
            };
            struct Varyings
            {
                float4 positionCS:SV_POSITION;float3 normal:TEXCOORD0;float3 world:TEXCOORD1;float2 uv:TEXCOORD2;
                nointerpolation float4 data:TEXCOORD3; // fade, wave, blade random, root occlusion jitter
                nointerpolation float2 root:TEXCOORD4;
                nointerpolation uint id:TEXCOORD5;
                float fog:TEXCOORD6;
                float far:TEXCOORD7;    // 0 near .. 1 at the draw distance: blend toward the field's average look
            };

            Varyings Vert(Attributes i)
            {
                InitIndirectDrawArgs(0);
                uint id=_Visible[GetIndirectInstanceID(i.instanceID)];
                float4 root=_Roots[id];
                float s,c;sincos(root.w,s,c);
                float clumpHeight=_Size.x*lerp(_Size.y,_Size.z,SGRandom(id+3297));
                float clumpWidth=lerp(_WidthRange.x,_WidthRange.y,SGRandom(id+7817));
                float3 scale=clumpHeight*float3(clumpWidth,1,clumpWidth);
                // Every blade gets its own stable height, so clumps never repeat.
                float heightJitter=lerp(1-_Motion.y,1+.4*_Motion.y,SGRandom(id*977u+(uint)(i.blade.y*65535)));
                float3 local=i.positionOS+i.tangentOS.xyz*i.uv.x*(_Blade.x-1);
                local.y*=heightJitter;
                float t=i.uv.y,bladeHeight=clumpHeight*heightJitter;

                // Wind sampled at this blade's own root, so a gust crosses a clump blade by blade.
                float2 bladeRoot=root.xz+Rotate(float3(i.bladeRoot.x,0,i.bladeRoot.y)*scale,s,c).xz;
                float2 direction=_WindDirection.xy;
                float2 wind=SGWind(bladeRoot,direction,_Wind,_WindNoise.xyz);
                float wave=SGWave(bladeRoot,direction,_Wind,_WindNoise.xyz);
                // Own stiffness, two-frequency flutter and a slower sideways swing per blade.
                float r1=i.blade.y,r2=frac(r1*7.31+.17),r3=frac(r1*13.7+.53),time=_Wind.w;
                float stiffness=lerp(1,lerp(.6,1.4,r2),_Motion.x);
                float along=sin(time*(2.1+r1*2.4)+r1*40)*.6+sin(time*(5.3+r3*3.1)+r2*25)*.4;
                float across=sin(time*(1.7+r2*2.2)+r3*31);
                float turbulence=(.35+wave*.9)*_Motion.x*saturate(_Wind.x*5)*.35;
                float2 side=float2(-direction.y,direction.x);
                // Gust crests push the grass over; height shrinks so the blade keeps its length.
                float2 lean=(wind+direction*wave*_WindDirection.z)*stiffness+(direction*along+side*across*.7)*turbulence;
                float2 bend=lean*bladeHeight*t*t;
                float3 offset=Rotate(local*scale,s,c);offset.y*=rsqrt(1+dot(lean,lean)*t*t);
                float3 world=root.xyz+offset+float3(bend.x,0,bend.y);

                // Distance LOD: blades leave in a fixed random order; dropped ones collapse to the root.
                float distance=length(root.xz-_CameraPositionWS.xz);
                float keep=lerp(1,_Blade.y,saturate((distance-_Blade.z)/max(1,_Blade.w-_Blade.z)));
                if(i.blade.x>keep) world=root.xyz;

                Varyings o;
                o.positionCS=TransformWorldToHClip(world);
                o.normal=Rotate(i.normalOS,s,c);o.world=world;o.uv=i.uv;o.root=root.xz;o.id=id;
                o.data=float4(1-smoothstep(_Fade.x,_Fade.y,distance),wave,i.blade.y,SGRandom(id+5113u));
                o.fog=ComputeFogFactor(o.positionCS.z);
                // Start well before the fade so the speckled near look turns into an even meadow tone
                // gradually, instead of ending abruptly where the grass fades into the ground.
                o.far=smoothstep(_Fade.x*.3,_Fade.y,distance);
                return o;
            }

            half4 Frag(Varyings i,FRONT_FACE_TYPE facing:FRONT_FACE_SEMANTIC):SV_Target
            {
                // Blade-anchored dither for the distance fade (stable, no temporal noise).
                uint2 cell=(uint2)floor(float2(i.uv.x*.5+.5,i.uv.y)*64);
                clip(i.data.x-SGRandom(i.id+cell.x*92837111u+cell.y*689287499u));
                float t=saturate(i.uv.y);
                float3 albedo=SGAlbedo(i.root,t,i.data.z,_RootColor.rgb,_TipColor.rgb,_PatchColor.rgb,_Look.xy,_Look.z);
                albedo=SGWaveColor(albedo,i.data.y,t,_Motion.w);
                // Distance: root/tip/patch contrast converges to the average colour of the field.
                // Same colour the far grass layer uses, so the hand-over matches.
                float3 average=SGAlbedo(i.root,.6,.5,_RootColor.rgb,_TipColor.rgb,_PatchColor.rgb,_Look.xy,0);
                albedo=lerp(albedo,average,i.far*.75);
                // Foliage normals: blend toward up so the field lights as one soft canopy.
                float3 n=normalize(i.normal)*IS_FRONT_VFACE(facing,1,-1);
                n=normalize(lerp(n,float3(0,1,0),_Motion.z));
                Light light=GetMainLight(TransformWorldToShadowCoord(i.world));
                // Not distanceAttenuation: it comes from per-object data that indirect draws do not get.
                float shadow=light.shadowAttenuation;
                float occlusion=1-_Shading.x*lerp(.75,1.25,i.data.w)*pow(1-lerp(t,.6,i.far),1.6);
                float3 view=normalize(GetCameraPositionWS()-i.world);
                // Thin blades glow when back-lit, strongest at the tips.
                float back=pow(saturate(dot(-view,light.direction)),4)*t*_Look.w*(1-i.far);
                float direct=(.35+.65*saturate(dot(n,light.direction)))*lerp(1,occlusion,.55)+back;
                half3 color=albedo*(SampleSH(n)*occlusion+light.color*direct*shadow);
                return half4(MixFog(color,i.fog),1);
            }
            ENDHLSL
        }
    }
}
