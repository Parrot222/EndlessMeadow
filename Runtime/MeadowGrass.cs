using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace EndlessMeadow
{
    /// <summary>
    /// GPU stylized grass for a Unity Terrain (URP). Roots are generated on the GPU once; every
    /// camera (Game and Scene views) culls the grid cells around itself and draws five distance
    /// bands, each with a blade-clump mesh that only holds the blades that band can still show.
    /// </summary>
    [ExecuteAlways]
    [AddComponentMenu("Endless Meadow/Meadow Grass")]
    public sealed class MeadowGrass : MonoBehaviour
    {
        [Tooltip("Terrain the grass grows on")] public Terrain terrain;
        public ComputeShader compute;
        public Shader shader;
        [Tooltip("Shader of the far grass layer (optional)")]
        public Shader farShader;

        [Header("Placement (regenerates)")]
        [Tooltip("Grass clumps per square metre")]
        [Range(.1f,4)] public float density=1.4f;
        [Tooltip("No grass on slopes steeper than this")]
        [Range(0,89)] public float maxSlope=38;
        public int seed=1234;
        [Tooltip("Grass amount per terrain layer (0..1), in the order of the terrain's layers. Edited in the Inspector list.")]
        [HideInInspector] public float[] layerGrass=new float[0];

        [Header("Shape")]
        [Tooltip("Base grass height in metres")]
        [Range(.1f,3)] public float height=.8f;
        [Tooltip("Random height of each clump (x base height)")]
        public Vector2 heightRange=new Vector2(.6f,1.45f);
        [Tooltip("Random width of each clump")]
        public Vector2 widthRange=new Vector2(.8f,1.25f);
        [Tooltip("Random height of every single blade (stable per blade)")]
        [Range(0,.8f)] public float bladeHeightVariation=.4f;
        [Range(4,40)] public int bladesPerClump=24;
        [Tooltip("Clump radius, relative to the grass height")]
        [Range(.1f,1.2f)] public float clumpRadius=.5f;
        [Tooltip("Blade width multiplier")]
        [Range(.3f,3)] public float bladeWidth=1;
        [Tooltip("Blade width at the root, relative to its widest point")]
        [Range(.05f,1)] public float rootWidth=.35f;
        [Tooltip("Height of the widest point along a blade (0 root .. 1 tip)")]
        [Range(.05f,.8f)] public float widestAt=.25f;
        [Tooltip("Shortest blade in a clump, relative to the tallest")]
        [Range(.1f,1)] public float shortestBlade=.35f;

        [Header("Colour")]
        public static readonly Color DefaultRootColor=new Color(.24f,.51f,.29f),DefaultTipColor=new Color(.66f,.78f,.29f);
        public Color rootColor=DefaultRootColor;
        public Color tipColor=DefaultTipColor;
        [Tooltip("Tip colour of the yellower patches")]
        public Color patchColor=new Color(.76f,.82f,.33f);
        [Range(2,200)] public float patchSize=28;
        [Range(0,1)] public float patchStrength=.7f;
        [Range(0,.3f)] public float bladeBrightnessVariation=.08f;
        [Tooltip("Glow of thin blades when the sun is behind them")]
        [Range(0,2)] public float translucency=.6f;
        [Tooltip("Blend blade normals toward up: softer, unified canopy lighting")]
        [Range(0,1)] public float normalsUp=.6f;
        [Tooltip("Darkening toward the roots")]
        [Range(0,1)] public float rootOcclusion=.6f;

        [Header("Wind")]
        public bool wind=true;
        [Range(0,.6f)] public float windStrength=.22f;
        [Range(0,4)] public float windSpeed=1.4f;
        public Vector2 windDirection=new Vector2(1,.35f);
        [Tooltip("Spacing of the wind waves (higher = closer waves)")]
        [Range(.005f,.3f)] public float windFrequency=.065f;
        [Tooltip("How far passing gust crests push the grass over")]
        [Range(0,1.5f)] public float gustBend=.7f;
        [Tooltip("Brightening of grass at gust crests")]
        [Range(0,1.5f)] public float waveBrightness=.6f;
        [Tooltip("Breaks up the wave fronts: 0 = straight regular bands")]
        [Range(0,1)] public float windNoise=.6f;
        [Range(5,200)] public float windNoiseSize=40;
        [Tooltip("How differently each blade moves")]
        [Range(0,1)] public float bladeSwayVariation=.6f;

        [Header("Distance and performance")]
        [Tooltip("Grass is fully gone beyond this distance (m)")]
        [Range(20,600)] public float drawDistance=220;
        [Tooltip("Dithered fade length before the draw distance (m)")]
        [Range(1,200)] public float fadeLength=80;
        [Tooltip("Blades start dropping out from this distance (m)")]
        [Range(0,300)] public float lodStart=12;
        [Tooltip("Fewest blades from this distance (m)")]
        [Range(1,600)] public float lodEnd=120;
        [Tooltip("Share of blades kept at and beyond the LOD end")]
        [Range(.1f,1)] public float lodKeep=.25f;
        [Tooltip("Safety cap on generated roots (16 bytes each)")]
        public int maxRoots=8000000;
        [Tooltip("Draw the grass in the Scene view (turn off to see and edit the bare terrain)")]
        public bool showInSceneView=true;

        [Header("Far grass")]
        [Tooltip("Continue the meadow beyond the draw distance with a light ground-hugging grass layer (same colours, wind waves and grass layers)")]
        public bool farGrass=true;
        [Tooltip("How far the far grass reaches (m)")]
        [Range(250,5000)] public float farDistance=1500;
        [Tooltip("Ray-march steps through the far grass canopy per pixel (more = smoother, slower)")]
        [Range(4,24)] public int farSteps=12;

        public int RootCount=>total;
        public long CulledCells {get;private set;}
        public bool Ready=>roots!=null;

        static readonly float[] LodShare={1,.7f,.5f,.35f,0};
        GraphicsBuffer roots;
        readonly GraphicsBuffer[] visible=new GraphicsBuffer[5],arguments=new GraphicsBuffer[5];
        readonly Mesh[] lods=new Mesh[5];
        readonly MaterialPropertyBlock[] blocks=new MaterialPropertyBlock[5];
        readonly float[] lodStarts=new float[4];
        readonly Plane[] planes=new Plane[6];
        readonly Vector4[] planeVectors=new Vector4[6];
        Material material;
        int total,columns,rows,generateKernel,cullKernel;
        string placementKey,fillKey,shapeKey;
        bool terrainEdited;float lastFill=-1;
        // Far grass: ring mesh around the camera, plus the placement data it shares with Generate.
        Mesh farMesh;Material farMaterial;MaterialPropertyBlock farBlock;Vector2 builtFar;
        Texture control0,control1;Vector4 layerGrass0,layerGrass1;bool useLayers;
        float timeOffset;

        /// <summary>Grass amount of terrain layer i (1 when not set).</summary>
        public float LayerGrass(int i)=>layerGrass!=null && i<layerGrass.Length?Mathf.Clamp01(layerGrass[i]):1;
        // Sculpting or painting this terrain: refill the roots (GPU only, so it stays interactive).
        void OnHeightmapChanged(Terrain t,RectInt region,bool synched) {if(t==terrain) terrainEdited=true;}
        void OnTextureChanged(Terrain t,string name,RectInt region,bool synched) {if(t==terrain && name==TerrainData.AlphamapTextureName) terrainEdited=true;}

        void OnEnable()
        {
            RenderPipelineManager.beginCameraRendering+=OnBeginCamera;
            TerrainCallbacks.heightmapChanged+=OnHeightmapChanged;TerrainCallbacks.textureChanged+=OnTextureChanged;
            timeOffset=Random.value*100;
        }
        void OnDisable()
        {
            RenderPipelineManager.beginCameraRendering-=OnBeginCamera;
            TerrainCallbacks.heightmapChanged-=OnHeightmapChanged;TerrainCallbacks.textureChanged-=OnTextureChanged;
            Release();
        }
#if UNITY_EDITOR
        // Adding the component by hand: pick up the package shaders and the active terrain.
        void Reset()
        {
            if(!terrain) terrain=Terrain.activeTerrain;
            foreach(string guid in UnityEditor.AssetDatabase.FindAssets("Meadow"))
            {
                string path=UnityEditor.AssetDatabase.GUIDToAssetPath(guid);
                if(path.EndsWith("/Shaders/MeadowGrass.compute")) compute=UnityEditor.AssetDatabase.LoadAssetAtPath<ComputeShader>(path);
                if(path.EndsWith("/Shaders/MeadowGrassBlades.shader")) shader=UnityEditor.AssetDatabase.LoadAssetAtPath<Shader>(path);
                if(path.EndsWith("/Shaders/MeadowFarGrass.shader")) farShader=UnityEditor.AssetDatabase.LoadAssetAtPath<Shader>(path);
            }
        }
#endif
        void OnValidate()
        {
            heightRange.y=Mathf.Max(heightRange.x,heightRange.y);widthRange.y=Mathf.Max(widthRange.x,widthRange.y);
            lodEnd=Mathf.Max(lodStart+1,lodEnd);
        }

        /// <summary>Rebuild everything (terrain edits are picked up automatically).</summary>
        [ContextMenu("Regenerate")]
        public void Regenerate() {Release();}

        // Changing these reallocates the root buffer; everything else only refills it.
        string PlacementKey()=>terrain?$"{terrain.GetInstanceID()}|{terrain.terrainData.size}|{density}|{maxRoots}":"";
        string FillKey()
        {
            var sb=new System.Text.StringBuilder($"{terrain.GetPosition()}|{maxSlope}|{seed}|{terrain.terrainData.terrainLayers.Length}");
            for(int i=0;i<Mathf.Min(8,terrain.terrainData.terrainLayers.Length);i++) sb.Append('|').Append(LayerGrass(i).ToString("F3"));
            return sb.ToString();
        }
        string ShapeKey()=>$"{bladesPerClump}|{clumpRadius}|{rootWidth}|{widestAt}|{shortestBlade}|{lodKeep}|{seed}";

        bool EnsureReady()
        {
            if(!terrain || !terrain.terrainData || !compute || !shader || !SystemInfo.supportsComputeShaders) return false;
            if(roots!=null && placementKey!=PlacementKey()) Release();
            if(roots==null) Allocate();
            // Refill after terrain edits (throttled while a brush is held) or setting changes.
            float now=Time.realtimeSinceStartup;
            if(fillKey!=FillKey() || (terrainEdited && now-lastFill>.08f)) {Fill();terrainEdited=false;lastFill=now;}
            if(shapeKey!=ShapeKey()) BuildLods();
            return roots!=null;
        }

        void Allocate()
        {
            var data=terrain.terrainData;Vector3 size=data.size;
            long wanted=(long)Mathf.Ceil(size.x*size.z*density);
            total=(int)Mathf.Clamp(wanted,1,maxRoots);
            if(wanted>maxRoots) Debug.LogWarning($"Meadow Grass: {wanted:N0} roots wanted, capped at {maxRoots:N0}. Lower the density or raise Max Roots.",this);
            columns=Mathf.Max(1,Mathf.RoundToInt(Mathf.Sqrt(total*size.x/Mathf.Max(1,size.z))));
            rows=Mathf.CeilToInt((float)total/columns);
            roots=new GraphicsBuffer(GraphicsBuffer.Target.Structured,total,16);
            generateKernel=compute.FindKernel("Generate");cullKernel=compute.FindKernel("Cull");

            material=new Material(shader) {name="Meadow Grass (runtime)",hideFlags=HideFlags.HideAndDontSave};
            for(int i=0;i<5;i++)
            {
                // 4 bytes per root per band: safe for any LOD / draw-distance settings.
                visible[i]=new GraphicsBuffer(GraphicsBuffer.Target.Append,total,4);
                arguments[i]=new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments,1,GraphicsBuffer.IndirectDrawIndexedArgs.size);
                blocks[i]=new MaterialPropertyBlock();
                blocks[i].SetBuffer("_Roots",roots);blocks[i].SetBuffer("_Visible",visible[i]);
            }
            placementKey=PlacementKey();fillKey=shapeKey=null;
        }

        // Place the roots from the terrain's live GPU data: heightmap and painted layer weights.
        void Fill()
        {
            var data=terrain.terrainData;Vector3 size=data.size;
            var heightmap=data.heightmapTexture;
            compute.SetInt("_Total",total);compute.SetInt("_Columns",columns);compute.SetInt("_Rows",rows);compute.SetInt("_Seed",seed);
            compute.SetVector("_TerrainOrigin",terrain.GetPosition());compute.SetVector("_TerrainSize",size);
            compute.SetVector("_HeightsSize",new Vector4(heightmap.width,heightmap.height,0,0));
            // 16-bit heightmap: 0.5 (32766/65535) means the full terrain height.
            compute.SetFloat("_HeightScale",size.y*65535f/32766f);
            compute.SetFloat("_MaxSlope",Mathf.Tan(Mathf.Min(maxSlope,89)*Mathf.Deg2Rad));
            compute.SetTexture(generateKernel,"_Heights",heightmap);
            int layers=Mathf.Min(8,data.terrainLayers.Length);
            useLayers=layers>0 && data.alphamapTextureCount>0;
            var grass=new float[8];for(int i=0;i<layers;i++) grass[i]=LayerGrass(i);
            control0=useLayers?data.GetAlphamapTexture(0):Texture2D.blackTexture;
            control1=useLayers && data.alphamapTextureCount>1?data.GetAlphamapTexture(1):Texture2D.blackTexture;
            layerGrass0=new Vector4(grass[0],grass[1],grass[2],grass[3]);layerGrass1=new Vector4(grass[4],grass[5],grass[6],grass[7]);
            compute.SetTexture(generateKernel,"_Control0",control0);compute.SetTexture(generateKernel,"_Control1",control1);
            compute.SetVector("_ControlSize",new Vector4(control0.width,control0.height,0,0));
            compute.SetVector("_LayerGrass0",layerGrass0);
            compute.SetVector("_LayerGrass1",layerGrass1);
            compute.SetFloat("_UseLayers",useLayers?1:0);
            compute.SetBuffer(generateKernel,"_Roots",roots);
            compute.Dispatch(generateKernel,Mathf.CeilToInt(total/256f),1,1);
            fillKey=FillKey();
        }

        void BuildLods()
        {
            for(int k=0;k<5;k++)
            {
                if(lods[k]) DestroyImmediate(lods[k]);
                int blades=Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(LodShare[k],lodKeep)*bladesPerClump),1,bladesPerClump);
                lods[k]=BladeClump.Build(blades,bladesPerClump,seed,clumpRadius,shortestBlade,rootWidth,widestAt);
                arguments[k].SetData(new[]{new GraphicsBuffer.IndirectDrawIndexedArgs {
                    indexCountPerInstance=lods[k].GetIndexCount(0),startIndex=lods[k].GetIndexStart(0),baseVertexIndex=lods[k].GetBaseVertex(0)}});
            }
            shapeKey=ShapeKey();
        }

        void Release()
        {
            roots?.Dispose();roots=null;
            for(int i=0;i<5;i++)
            {
                visible[i]?.Dispose();visible[i]=null;arguments[i]?.Dispose();arguments[i]=null;
                if(lods[i]) DestroyImmediate(lods[i]);lods[i]=null;
            }
            if(material) DestroyImmediate(material);material=null;
            if(farMesh) DestroyImmediate(farMesh);farMesh=null;
            if(farMaterial) DestroyImmediate(farMaterial);farMaterial=null;
            placementKey=fillKey=shapeKey=null;
        }

        // Cull and draw for each camera right before it renders (works in Edit mode and Scene view).
        void OnBeginCamera(ScriptableRenderContext context,Camera camera)
        {
            if(!isActiveAndEnabled || camera.cameraType==CameraType.Preview || camera.cameraType==CameraType.Reflection) return;
            if(camera.cameraType==CameraType.SceneView && !showInSceneView) return;
            if(!EnsureReady()) return;
            float fadeEnd=drawDistance,fadeStart=Mathf.Max(0,drawDistance-fadeLength);
            float radius=height*heightRange.y*(1.5f+clumpRadius+windStrength+gustBend);
            Vector3 cam=camera.transform.position;

            GeometryUtility.CalculateFrustumPlanes(camera,planes);
            for(int i=0;i<6;i++) planeVectors[i]=new Vector4(planes[i].normal.x,planes[i].normal.y,planes[i].normal.z,planes[i].distance);
            float end=Mathf.Max(lodStart+1,lodEnd);
            for(int k=1;k<5;k++)
                lodStarts[k-1]=lodStart+(end-lodStart)*Mathf.Clamp01((1-Mathf.Max(LodShare[k],lodKeep))/Mathf.Max(.001f,1-lodKeep));

            // Only the rectangle of grid cells within reach of this camera.
            Vector3 origin=terrain.GetPosition(),size=terrain.terrainData.size;
            float reach=fadeEnd+radius;
            int c0=Mathf.Clamp(Mathf.FloorToInt((cam.x-reach-origin.x)/size.x*columns)-1,0,columns-1);
            int c1=Mathf.Clamp(Mathf.CeilToInt((cam.x+reach-origin.x)/size.x*columns)+1,0,columns-1);
            int r0=Mathf.Clamp(Mathf.FloorToInt((cam.z-reach-origin.z)/size.z*rows)-1,0,rows-1);
            int r1=Mathf.Clamp(Mathf.CeilToInt((cam.z+reach-origin.z)/size.z*rows)+1,0,rows-1);
            int width=c1-c0+1,depth=r1-r0+1;
            CulledCells=(long)width*depth;

            compute.SetInt("_Total",total);compute.SetInt("_Columns",columns);
            compute.SetInts("_CellRect",c0,r0,width,depth);
            compute.SetVectorArray("_Planes",planeVectors);compute.SetVector("_CameraPosition",cam);
            compute.SetFloat("_CullDistance",fadeEnd);compute.SetFloat("_CullRadius",radius);
            compute.SetVector("_LodBands",new Vector4(lodStarts[0],lodStarts[1],lodStarts[2],lodStarts[3]));
            compute.SetBuffer(cullKernel,"_Roots",roots);
            for(int i=0;i<5;i++) {visible[i].SetCounterValue(0);compute.SetBuffer(cullKernel,"_Visible"+i,visible[i]);}
            Vector2 dir=windDirection.sqrMagnitude>1e-4f?windDirection.normalized:Vector2.right;
            float time=(Application.isPlaying?Time.time:(float)Time.realtimeSinceStartupAsDouble)+timeOffset;
            Color root=rootColor.linear,tip=tipColor.linear,patch=patchColor.linear;
            if(farGrass && farShader) DrawFar(camera,cam,fadeStart,fadeEnd,dir,time,root,tip,patch);
            if(cam.x+reach<origin.x || cam.z+reach<origin.z || cam.x-reach>origin.x+size.x || cam.z-reach>origin.z+size.z) return;
            compute.Dispatch(cullKernel,Mathf.CeilToInt(width*(float)depth/256),1,1);
            var bounds=new Bounds(origin+size*.5f,size+Vector3.one*(radius*2+10));
            for(int i=0;i<5;i++)
            {
                // The indexed instanceCount field is at byte offset four.
                GraphicsBuffer.CopyCount(visible[i],arguments[i],4);
                var b=blocks[i];
                b.SetVector("_CameraPositionWS",cam);
                b.SetVector("_Fade",new Vector4(fadeStart,fadeEnd,0,0));
                b.SetVector("_Size",new Vector4(height,heightRange.x,heightRange.y,0));
                b.SetVector("_WidthRange",new Vector4(widthRange.x,widthRange.y,0,0));
                b.SetVector("_Blade",new Vector4(bladeWidth,lodKeep,lodStart,end));
                b.SetVector("_Motion",new Vector4(bladeSwayVariation,bladeHeightVariation,normalsUp,waveBrightness));
                b.SetVector("_Wind",new Vector4(wind?windStrength:0,windSpeed,windFrequency,time));
                b.SetVector("_WindDirection",new Vector4(dir.x,dir.y,wind?gustBend:0,0));
                b.SetVector("_WindNoise",new Vector4(windNoise,1/Mathf.Max(1,windNoiseSize),4,0));
                b.SetVector("_RootColor",root);b.SetVector("_TipColor",tip);b.SetVector("_PatchColor",patch);
                b.SetVector("_Look",new Vector4(1/Mathf.Max(1,patchSize),patchStrength,bladeBrightnessVariation,translucency));
                b.SetVector("_Shading",new Vector4(rootOcclusion,0,0,0));
                var render=new RenderParams(material) {worldBounds=bounds,camera=camera,matProps=b,
                    shadowCastingMode=ShadowCastingMode.Off,receiveShadows=true,layer=gameObject.layer};
                Graphics.RenderMeshIndirect(render,lods[i],arguments[i]);
            }
        }

        // Far grass: a camera-centred ring mesh from just inside the blade fade to the far distance,
        // laid on the terrain in the shader. It fades in where the blades fade out.
        void DrawFar(Camera camera,Vector3 cam,float fadeStart,float fadeEnd,Vector2 dir,float time,Color root,Color tip,Color patch)
        {
            float inner=Mathf.Max(1,fadeStart*.9f),outer=Mathf.Max(inner+10,farDistance);
            if(!farMesh || builtFar!=new Vector2(inner,outer)) {if(farMesh) DestroyImmediate(farMesh);farMesh=BuildRing(inner,outer);builtFar=new Vector2(inner,outer);}
            if(!farMaterial) farMaterial=new Material(farShader) {name="Meadow Far Grass (runtime)",hideFlags=HideFlags.HideAndDontSave};
            farBlock??=new MaterialPropertyBlock();
            var data=terrain.terrainData;Vector3 size=data.size,origin=terrain.GetPosition();
            var heightmap=data.heightmapTexture;
            var b=farBlock;
            b.SetTexture("_Heights",heightmap);b.SetTexture("_Control0",control0?control0:Texture2D.blackTexture);b.SetTexture("_Control1",control1?control1:Texture2D.blackTexture);
            b.SetVector("_TerrainOrigin",origin);b.SetVector("_TerrainSize",size);
            b.SetVector("_HeightsInfo",new Vector4(heightmap.width,heightmap.height,size.y*65535f/32766f,.15f));
            b.SetVector("_ControlInfo",new Vector4(control0?control0.width:1,control0?control0.height:1,useLayers?1:0,Mathf.Tan(Mathf.Min(maxSlope,89)*Mathf.Deg2Rad)));
            b.SetVector("_LayerGrass0",layerGrass0);b.SetVector("_LayerGrass1",layerGrass1);
            b.SetVector("_CameraPositionWS",cam);
            b.SetVector("_Fade",new Vector4(fadeStart,fadeEnd,outer,0));
            // The far field is a canopy slab as tall as the average blade. Its extinction scales with how much
            // blade area the thinned far bands hold (1 with the defaults): looking across the slab meets the
            // blades' sides, looking down only their small top-down footprint.
            float cover=lodKeep*bladesPerClump*density*bladeWidth/8.4f;
            float canopy=height*(heightRange.x+heightRange.y)*.5f*.8f;
            b.SetVector("_Canopy",new Vector4(canopy,.3f*cover/canopy,.1f*cover/canopy,farSteps));
            b.SetVector("_Motion",new Vector4(rootOcclusion,0,normalsUp,waveBrightness));
            b.SetVector("_Wind",new Vector4(wind?windStrength:0,windSpeed,windFrequency,time));
            b.SetVector("_WindDirection",new Vector4(dir.x,dir.y,0,0));
            b.SetVector("_WindNoise",new Vector4(windNoise,1/Mathf.Max(1,windNoiseSize),4,0));
            b.SetVector("_RootColor",root);b.SetVector("_TipColor",tip);b.SetVector("_PatchColor",patch);
            b.SetVector("_Look",new Vector4(1/Mathf.Max(1,patchSize),patchStrength,0,0));
            // Snap the centre so the ring's vertices do not slide over the terrain with every small move.
            const float snap=2;
            var matrix=Matrix4x4.Translate(new Vector3(Mathf.Round(cam.x/snap)*snap,0,Mathf.Round(cam.z/snap)*snap));
            var render=new RenderParams(farMaterial) {worldBounds=new Bounds(origin+size*.5f,size+Vector3.one*(outer*2)),camera=camera,matProps=b,
                shadowCastingMode=ShadowCastingMode.Off,receiveShadows=true,layer=gameObject.layer};
            Graphics.RenderMesh(render,farMesh,0,matrix);
        }

        // Rings spaced geometrically (dense near the blades, sparse far away).
        static Mesh BuildRing(float inner,float outer)
        {
            const int rings=96,segments=192;
            var vertices=new Vector3[(rings+1)*segments];var triangles=new int[rings*segments*6];
            for(int r=0;r<=rings;r++)
            {
                float radius=inner*Mathf.Pow(outer/inner,(float)r/rings);
                for(int s=0;s<segments;s++)
                {
                    float a=s*Mathf.PI*2/segments;
                    vertices[r*segments+s]=new Vector3(Mathf.Cos(a)*radius,0,Mathf.Sin(a)*radius);
                }
            }
            int t=0;
            for(int r=0;r<rings;r++) for(int s=0;s<segments;s++)
            {
                int a=r*segments+s,b=r*segments+(s+1)%segments,c=a+segments,d=b+segments;
                triangles[t++]=a;triangles[t++]=c;triangles[t++]=b;triangles[t++]=b;triangles[t++]=c;triangles[t++]=d;
            }
            var mesh=new Mesh {name="Far grass ring",hideFlags=HideFlags.HideAndDontSave};
            mesh.SetVertices(vertices);mesh.SetTriangles(triangles,0);
            mesh.bounds=new Bounds(Vector3.zero,new Vector3(outer*2,10000,outer*2));
            return mesh;
        }
    }

    /// <summary>Procedural clump of tapered, curved blades (height 1, rooted at y = 0).</summary>
    public static class BladeClump
    {
        /// <param name="blades">How many blades of the sequence to build (a prefix of the full clump).</param>
        /// <param name="total">Full clump size; the LOD order is relative to it, so every LOD is a prefix.</param>
        public static Mesh Build(int blades,int total,int seed,float clumpRadius,float shortest,float rootWidth,float widestAt)
        {
            total=Mathf.Max(total,blades);
            var random=new System.Random(seed*7919+1);
            float R()=>(float)random.NextDouble();
            const int segments=5;
            // Leaf profile: narrow root, widest a little above it, tapering to the tip.
            float Profile(float t)
            {
                if(t<widestAt) {float u=t/Mathf.Max(.001f,widestAt);return Mathf.Lerp(rootWidth,1,u*u*(3-2*u));}
                return Mathf.Pow(Mathf.Clamp01((1-t)/Mathf.Max(.001f,1-widestAt)),.85f);
            }
            var positions=new List<Vector3>();var normals=new List<Vector3>();var tangents=new List<Vector4>();
            var uv0=new List<Vector2>();var uv1=new List<Vector2>();var uv2=new List<Vector2>();var triangles=new List<int>();
            for(int b=0;b<blades;b++)
            {
                float radius=clumpRadius*Mathf.Sqrt(R()),angle=R()*Mathf.PI*2;
                var root=new Vector3(Mathf.Cos(angle)*radius,0,Mathf.Sin(angle)*radius);
                float bladeHeight=b==0?1:Mathf.Lerp(shortest,1,R()),halfWidth=Mathf.Lerp(.035f,.06f,R());
                float facing=R()*Mathf.PI*2;
                var right=new Vector3(Mathf.Cos(facing),0,Mathf.Sin(facing));var forward=new Vector3(-right.z,0,right.x);
                // Lean outward from the clump centre plus a random curl.
                Vector3 lean=(root.sqrMagnitude>1e-6f?root.normalized:forward)*Mathf.Lerp(.05f,.22f,R())+forward*Mathf.Lerp(-.12f,.12f,R());
                Vector3 normal=(Vector3.up+forward*.35f).normalized;
                float order=(b+.5f)/total,jitter=R();
                int start=positions.Count;
                for(int k=0;k<=segments;k++)
                {
                    float t=(float)k/segments;
                    Vector3 centre=root+Vector3.up*bladeHeight*t+lean*bladeHeight*t*t;
                    if(k<segments)
                    {
                        float w=halfWidth*Profile(t);
                        for(int side=-1;side<=1;side+=2)
                        {
                            positions.Add(centre+right*w*side);normals.Add(normal);tangents.Add(new Vector4(right.x*w,0,right.z*w,1));
                            uv0.Add(new Vector2(side,t));uv1.Add(new Vector2(order,jitter));uv2.Add(new Vector2(root.x,root.z));
                        }
                    }
                    else
                    {
                        positions.Add(centre);normals.Add(normal);tangents.Add(Vector4.zero);
                        uv0.Add(new Vector2(0,1));uv1.Add(new Vector2(order,jitter));uv2.Add(new Vector2(root.x,root.z));
                    }
                }
                for(int k=0;k<segments-1;k++) {int a=start+k*2;triangles.AddRange(new[]{a,a+2,a+1,a+1,a+2,a+3});}
                int last=start+(segments-1)*2;triangles.AddRange(new[]{last,last+2,last+1});
            }
            var mesh=new Mesh {name=$"Grass clump {blades}/{total}",hideFlags=HideFlags.HideAndDontSave};
            mesh.SetVertices(positions);mesh.SetNormals(normals);mesh.SetTangents(tangents);
            mesh.SetUVs(0,uv0);mesh.SetUVs(1,uv1);mesh.SetUVs(2,uv2);mesh.SetTriangles(triangles,0);
            float extent=2*clumpRadius+.6f;
            mesh.bounds=new Bounds(new Vector3(0,.5f,0),new Vector3(extent,1,extent));
            return mesh;
        }
    }
}
