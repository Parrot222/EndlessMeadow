#ifndef STYLIZED_GRASS_LITE_COMMON
#define STYLIZED_GRASS_LITE_COMMON
// Shared by the blade shader. Everything is world-anchored and stable per root/blade:
// no camera- or frame-dependent noise, so the field does not shimmer when the camera moves.

uint SGHash(uint x) {x^=x>>16;x*=0x7feb352du;x^=x>>15;x*=0x846ca68bu;return x^(x>>16);}
float SGRandom(uint x) {return (SGHash(x)&0xffffffu)/16777216.0;}

float SGNoise(float2 p)
{
    float2 i=floor(p),f=frac(p);f=f*f*(3-2*f);
    uint2 c=asuint((int2)i);
    float a=SGRandom(SGHash(c.x)^c.y*0x27d4eb2du);
    float b=SGRandom(SGHash(c.x+1)^c.y*0x27d4eb2du);
    float d=SGRandom(SGHash(c.x)^(c.y+1)*0x27d4eb2du);
    float e=SGRandom(SGHash(c.x+1)^(c.y+1)*0x27d4eb2du);
    return lerp(lerp(a,b,f.x),lerp(d,e,f.x),f.y);
}

// ---------------------------------------------------------------------------------------------
// Wind. wind = (strength, speed, spatial frequency, time); direction = unit xy;
// noise = (amount 0..1, cell frequency 1/m, drift m/s).
// Returns x phase, y across-wind coordinate, z local gust amplitude.
float3 SGWindPhase(float2 p,float2 direction,float4 wind,float3 noise)
{
    float along=dot(p,direction),across=dot(p,float2(-direction.y,direction.x));
    // World-anchored noise drifting slowly downwind bends the wave fronts and varies their strength.
    float2 q=(p-direction*(wind.w*noise.z))*max(noise.y,1e-4);
    float warp=(SGNoise(q)-.5)*2+(SGNoise(q*2.3+17.1)-.5);
    float phase=along*wind.z-wind.w*wind.y+warp*noise.x*3;
    float amplitude=lerp(1,lerp(.2,1.3,SGNoise(q*.7+41.3)),noise.x);
    return float3(phase,across,amplitude);
}
// Steady lean from a broad gust and a shorter sway wave.
float2 SGWind(float2 p,float2 direction,float4 wind,float3 noise)
{
    float3 w=SGWindPhase(p,direction,wind,noise);
    float gust=.65+.35*sin(w.x*.31+w.y*wind.z*.19);
    float sway=.55+.35*sin(w.x)+.1*sin(w.x*2.17+w.y*wind.z*.7);
    return direction*(wind.x*gust*sway*lerp(1,w.z,.5));
}
// 0..1 crest of the same sway wave: where it passes, grass is pushed over and shows its paler side.
float SGWave(float2 p,float2 direction,float4 wind,float3 noise)
{
    float3 w=SGWindPhase(p,direction,wind,noise);
    float gust=sin(w.x*.31+w.y*wind.z*.19)*.5+.5;
    float sway=sin(w.x+.35*sin(w.y*wind.z*.7))*.5+.5;
    float crest=sway*(.35+.65*gust)*w.z;
    return crest*crest*saturate(wind.x*5);
}

// ---------------------------------------------------------------------------------------------
// Colour. rootColor/tipColor/patchColor are linear; patch.x = frequency (1/m), patch.y = strength.
float3 SGAlbedo(float2 root,float t,float bladeRandom,float3 rootColor,float3 tipColor,float3 patchColor,float2 patch,float jitter)
{
    float n=SGNoise(root*patch.x+3.7)*.65+SGNoise(root*patch.x*3.1+7.7)*.35;
    float3 tip=lerp(tipColor,patchColor,smoothstep(.35,.75,n)*patch.y);
    float3 c=lerp(rootColor,tip,pow(saturate(t),.75));
    return c*(1+(bladeRandom-.5)*2*jitter);
}
// Wave crests: brighter and slightly paler toward the tips.
float3 SGWaveColor(float3 albedo,float wave,float t,float strength)
{
    float amount=wave*strength*saturate(t*1.25);
    float luma=dot(albedo,float3(.2126,.7152,.0722));
    return lerp(albedo,luma.xxx,amount*.35)*(1+amount);
}
#endif
