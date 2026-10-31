using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace EndlessMeadow.Editor
{
    public static class EndlessMeadowMenu
    {
        const string ComputePath = "/Shaders/MeadowGrass.compute";
        const string BladeShaderPath = "/Shaders/MeadowGrassBlades.shader";
        const string FarShaderPath = "/Shaders/MeadowFarGrass.shader";

        const float TerrainLength = 600f;
        const float TerrainHeight = 60f;
        const int HeightmapResolution = 513;
        const int AlphamapResolution = 512;

        static string GetPackageFolder()
        {
            string[] guids = AssetDatabase.FindAssets("MeadowGrass t:ComputeShader");

            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (path.EndsWith(ComputePath))
                {
                    return path.Substring(0, path.Length - ComputePath.Length);
                }
            }

            return "Assets/EndlessMeadow";
        }

        public static void AssignAssets(MeadowGrass grass)
        {
            string folder = GetPackageFolder();

            if (grass.compute == null)
            {
                grass.compute = AssetDatabase.LoadAssetAtPath<ComputeShader>(folder + ComputePath);
            }
            if (grass.shader == null)
            {
                grass.shader = AssetDatabase.LoadAssetAtPath<Shader>(folder + BladeShaderPath);
            }
            if (grass.farShader == null)
            {
                grass.farShader = AssetDatabase.LoadAssetAtPath<Shader>(folder + FarShaderPath);
            }
        }

        [MenuItem("GameObject/Endless Meadow/Add Grass To Selected Terrain", false, 10)]
        static void AddGrassToSelectedTerrain()
        {
            Terrain terrain = null;

            if (Selection.activeGameObject != null)
            {
                terrain = Selection.activeGameObject.GetComponent<Terrain>();
            }
            if (terrain == null)
            {
                terrain = Terrain.activeTerrain;
            }
            if (terrain == null)
            {
                EditorUtility.DisplayDialog("Endless Meadow", "Select a Terrain first.", "OK");
                return;
            }

            GameObject grassObject = new GameObject("Meadow Grass");
            Undo.RegisterCreatedObjectUndo(grassObject, "Add Meadow Grass");

            MeadowGrass grass = grassObject.AddComponent<MeadowGrass>();
            grass.terrain = terrain;
            AssignAssets(grass);

            Selection.activeGameObject = grassObject;
        }

        public static void FrameTerrain(Terrain terrain)
        {
            SceneView view = SceneView.lastActiveSceneView;
            if (view == null || terrain == null)
            {
                return;
            }

            Vector3 size = terrain.terrainData.size;
            Vector3 center = terrain.GetPosition() + new Vector3(size.x / 2f, 0f, size.z / 2f);
            center.y = terrain.SampleHeight(center) + terrain.GetPosition().y;

            SceneView.SceneViewState state = view.sceneViewState;
            state.showFog = false;
            view.sceneViewState = state;

            float distance = Mathf.Max(size.x, size.z) * 0.12f;
            view.LookAt(center, Quaternion.Euler(30f, 30f, 0f), distance);
            view.Repaint();
        }

        [MenuItem("GameObject/Endless Meadow/Create Demo Scene", false, 11)]
        static void CreateDemoScene()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                return;
            }

            string packageFolder = GetPackageFolder();
            string demoFolder = packageFolder + "/Demo";
            if (!AssetDatabase.IsValidFolder(demoFolder))
            {
                AssetDatabase.CreateFolder(packageFolder, "Demo");
            }

            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

            Terrain terrain = CreateDemoTerrain(demoFolder);
            SetUpLightAndCamera(scene, terrain);
            SetUpFog();

            GameObject grassObject = new GameObject("Meadow Grass");
            MeadowGrass grass = grassObject.AddComponent<MeadowGrass>();
            grass.terrain = terrain;
            grass.layerGrass = new float[] { 1f, 0f };
            AssignAssets(grass);

            string scenePath = demoFolder + "/EndlessMeadowDemo.unity";
            EditorSceneManager.SaveScene(scene, scenePath);
            AssetDatabase.SaveAssets();

            Selection.activeGameObject = grassObject;
            FrameTerrain(terrain);
            Debug.Log("Endless Meadow demo created: " + scenePath);
        }

        static Terrain CreateDemoTerrain(string folder)
        {
            TerrainData data = new TerrainData();
            data.name = "Demo Meadow";
            data.heightmapResolution = HeightmapResolution;
            data.size = new Vector3(TerrainLength, TerrainHeight, TerrainLength);
            data.SetHeights(0, 0, CreateHeights());

            AssetDatabase.CreateAsset(data, folder + "/DemoMeadow.asset");

            TerrainLayer grassLayer = CreateGrassGroundLayer(folder, MeadowGrass.DefaultRootColor, MeadowGrass.DefaultTipColor);
            TerrainLayer dirtLayer = CreatePaintedLayer(folder, "DirtPath", "Dirt Path",
                new Color(0.45f, 0.36f, 0.25f),
                new Color(0.50f, 0.40f, 0.28f),
                new Color(0.56f, 0.46f, 0.32f),
                4f);

            data.alphamapResolution = AlphamapResolution;
            data.terrainLayers = new TerrainLayer[] { grassLayer, dirtLayer };
            data.SetAlphamaps(0, 0, CreatePathAlphamaps());

            EditorUtility.SetDirty(data);
            AssetDatabase.SaveAssets();

            GameObject terrainObject = Terrain.CreateTerrainGameObject(data);
            terrainObject.name = "Meadow Terrain";
            terrainObject.transform.position = new Vector3(-TerrainLength / 2f, 0f, -TerrainLength / 2f);

            Terrain terrain = terrainObject.GetComponent<Terrain>();
            terrain.heightmapPixelError = 4;
            terrain.drawInstanced = true;
            return terrain;
        }

        static float[,] CreateHeights()
        {
            float[,] heights = new float[HeightmapResolution, HeightmapResolution];
            float metersPerPixel = TerrainLength / (HeightmapResolution - 1);

            for (int z = 0; z < HeightmapResolution; z++)
            {
                for (int x = 0; x < HeightmapResolution; x++)
                {
                    float worldX = x * metersPerPixel;
                    float worldZ = z * metersPerPixel;

                    float hills = 22f * Mathf.PerlinNoise(worldX / 260f + 3.1f, worldZ / 260f + 7.4f);
                    float bumps = 9f * Mathf.PerlinNoise(worldX / 90f + 11f, worldZ / 90f + 5f);
                    float details = 2.5f * Mathf.PerlinNoise(worldX / 30f + 2f, worldZ / 30f + 9f);
                    float height = hills + bumps + details;

                    Vector2 fromCenter = new Vector2(worldX - TerrainLength / 2f, worldZ - TerrainLength / 2f);
                    float clearing = Mathf.Clamp01((fromCenter.magnitude - 40f) / 120f);
                    clearing = Mathf.SmoothStep(0f, 1f, clearing);

                    heights[z, x] = Mathf.Lerp(14f, height, clearing) / TerrainHeight;
                }
            }

            return heights;
        }

        static float[,,] CreatePathAlphamaps()
        {
            float[,,] maps = new float[AlphamapResolution, AlphamapResolution, 2];
            float metersPerPixel = TerrainLength / (AlphamapResolution - 1);

            for (int z = 0; z < AlphamapResolution; z++)
            {
                for (int x = 0; x < AlphamapResolution; x++)
                {
                    float worldX = x * metersPerPixel;
                    float worldZ = z * metersPerPixel;

                    float pathCenter = TerrainLength / 2f + 70f * Mathf.Sin(worldZ / 95f) + 25f * Mathf.Sin(worldZ / 37f + 1f);
                    float distanceToPath = Mathf.Abs(worldX - pathCenter);
                    float path = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(1.6f, 3.2f, distanceToPath));

                    maps[z, x, 0] = 1f - path;
                    maps[z, x, 1] = path;
                }
            }

            return maps;
        }

        static void SetUpLightAndCamera(Scene scene, Terrain terrain)
        {
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                Light sun = root.GetComponent<Light>();
                if (sun != null)
                {
                    sun.transform.rotation = Quaternion.Euler(38f, 35f, 0f);
                    sun.intensity = 1.25f;
                    sun.color = new Color(1f, 0.96f, 0.88f);
                    sun.shadows = LightShadows.Soft;
                }

                Camera camera = root.GetComponent<Camera>();
                if (camera != null)
                {
                    camera.transform.position = new Vector3(0f, 16f, -40f);
                    camera.transform.rotation = Quaternion.Euler(4f, 0f, 0f);
                    camera.nearClipPlane = 0.1f;
                    camera.farClipPlane = 1500f;

                    DemoFlyCamera flyCamera = camera.gameObject.AddComponent<DemoFlyCamera>();
                    flyCamera.terrain = terrain;
                }
            }
        }

        static void SetUpFog()
        {
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.fogDensity = 0.0042f;
            RenderSettings.fogColor = new Color(0.70f, 0.80f, 0.90f);
        }

        static TerrainLayer CreateGrassGroundLayer(string folder, Color rootColor, Color tipColor)
        {
            Color dark = Color.Lerp(rootColor, tipColor, 0.5f);
            Color middle = Color.Lerp(rootColor, tipColor, 0.6f);
            Color light = Color.Lerp(rootColor, tipColor, 0.7f);
            return CreatePaintedLayer(folder, "GrassGround", "Grass Ground", dark, middle, light, 23f);
        }

        static TerrainLayer CreatePaintedLayer(string folder, string fileName, string layerName, Color dark, Color middle, Color light, float tileSize)
        {
            string texturePath = folder + "/" + fileName + ".png";
            string layerPath = folder + "/" + fileName + ".terrainlayer";

            SaveTexture(texturePath, dark, middle, light);

            TerrainLayer layer = AssetDatabase.LoadAssetAtPath<TerrainLayer>(layerPath);
            if (layer == null)
            {
                layer = new TerrainLayer();
                layer.name = layerName;
                AssetDatabase.CreateAsset(layer, layerPath);
            }

            layer.diffuseTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath);
            layer.tileSize = new Vector2(tileSize, tileSize);
            layer.smoothness = 0f;
            EditorUtility.SetDirty(layer);
            return layer;
        }

        const int TextureSize = 512;
        const int NoiseCells = 16;

        static void SaveTexture(string path, Color dark, Color middle, Color light)
        {
            float[,,] noise = CreateNoiseGrid();
            System.Random grain = new System.Random(11);
            Color32[] pixels = new Color32[TextureSize * TextureSize];

            for (int y = 0; y < TextureSize; y++)
            {
                for (int x = 0; x < TextureSize; x++)
                {
                    float u = (float)x / TextureSize;
                    float v = (float)y / TextureSize;

                    float value = SmoothNoise(noise, 0, u, v) * 0.5f
                                + SmoothNoise(noise, 1, u, v) * 0.3f
                                + SmoothNoise(noise, 2, u, v) * 0.2f;
                    value = Mathf.Clamp01((value - 0.2f) / 0.6f);

                    Color color;
                    if (value < 0.5f)
                    {
                        color = Color.Lerp(dark, middle, value * 2f);
                    }
                    else
                    {
                        color = Color.Lerp(middle, light, value * 2f - 1f);
                    }

                    float brightness = 1f + ((float)grain.NextDouble() - 0.5f) * 0.08f;
                    color *= brightness;
                    color.a = 0f;

                    pixels[y * TextureSize + x] = color;
                }
            }

            Texture2D texture = new Texture2D(TextureSize, TextureSize, TextureFormat.RGBA32, false);
            texture.SetPixels32(pixels);
            texture.Apply();
            File.WriteAllBytes(path, texture.EncodeToPNG());
            Object.DestroyImmediate(texture);

            AssetDatabase.ImportAsset(path);
            TextureImporter importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.wrapMode = TextureWrapMode.Repeat;
            importer.anisoLevel = 8;
            importer.SaveAndReimport();
        }

        static float[,,] CreateNoiseGrid()
        {
            float[,,] grid = new float[3, NoiseCells, NoiseCells];
            System.Random random = new System.Random(7);

            for (int octave = 0; octave < 3; octave++)
            {
                for (int y = 0; y < NoiseCells; y++)
                {
                    for (int x = 0; x < NoiseCells; x++)
                    {
                        grid[octave, y, x] = (float)random.NextDouble();
                    }
                }
            }

            return grid;
        }

        static float SmoothNoise(float[,,] grid, int octave, float u, float v)
        {
            int cells = NoiseCells >> (2 - octave);

            float fx = u * cells;
            float fy = v * cells;
            int x0 = (int)fx;
            int y0 = (int)fy;
            float tx = Mathf.SmoothStep(0f, 1f, fx - x0);
            float ty = Mathf.SmoothStep(0f, 1f, fy - y0);

            float bottomLeft = GridValue(grid, octave, cells, x0, y0);
            float bottomRight = GridValue(grid, octave, cells, x0 + 1, y0);
            float topLeft = GridValue(grid, octave, cells, x0, y0 + 1);
            float topRight = GridValue(grid, octave, cells, x0 + 1, y0 + 1);

            float bottom = Mathf.Lerp(bottomLeft, bottomRight, tx);
            float top = Mathf.Lerp(topLeft, topRight, tx);
            return Mathf.Lerp(bottom, top, ty);
        }

        static float GridValue(float[,,] grid, int octave, int cells, int x, int y)
        {
            int wrappedX = x % cells;
            int wrappedY = y % cells;
            return grid[octave, wrappedY, wrappedX];
        }
    }

    [CustomEditor(typeof(MeadowGrass))]
    public class MeadowGrassEditor : UnityEditor.Editor
    {
        const int MaxLayers = 8;

        static bool showAmounts;

        public override void OnInspectorGUI()
        {
            MeadowGrass grass = (MeadowGrass)target;

            DrawStatus(grass);
            DrawDefaultInspector();
            DrawGrassLayers(grass);
            DrawButtons(grass);
        }

        void DrawStatus(MeadowGrass grass)
        {
            bool shadersMissing = grass.compute == null || grass.shader == null || (grass.farGrass && grass.farShader == null);
            if (shadersMissing)
            {
                EditorGUILayout.HelpBox("Compute shader or grass shader missing.", MessageType.Warning);
                if (GUILayout.Button("Assign package shaders"))
                {
                    Undo.RecordObject(grass, "Assign shaders");
                    EndlessMeadowMenu.AssignAssets(grass);
                }
            }

            if (grass.terrain == null)
            {
                EditorGUILayout.HelpBox("Assign a Terrain.", MessageType.Info);
            }
            else if (grass.Ready)
            {
                string status = grass.RootCount.ToString("N0") + " clumps generated  |  cells culled last camera: " + grass.CulledCells.ToString("N0");
                EditorGUILayout.HelpBox(status, MessageType.None);
            }
        }

        void DrawGrassLayers(MeadowGrass grass)
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Grass on terrain layers", EditorStyles.boldLabel);

            if (grass.terrain == null || grass.terrain.terrainData == null)
            {
                return;
            }

            TerrainLayer[] layers = grass.terrain.terrainData.terrainLayers;
            if (layers.Length == 0)
            {
                return;
            }
            if (layers.Length > MaxLayers)
            {
                EditorGUILayout.HelpBox("Only the first 8 terrain layers are used.", MessageType.Warning);
            }

            int count = Mathf.Min(MaxLayers, layers.Length);

            float[] amounts = new float[layers.Length];
            for (int i = 0; i < layers.Length; i++)
            {
                amounts[i] = grass.LayerGrass(i);
            }

            string[] names = new string[count];
            for (int i = 0; i < count; i++)
            {
                if (layers[i] != null)
                {
                    names[i] = layers[i].name;
                }
                else
                {
                    names[i] = "Layer " + i + " (missing)";
                }
            }

            EditorGUI.BeginChangeCheck();

            int selected = 0;
            for (int i = 0; i < count; i++)
            {
                if (amounts[i] > 0f)
                {
                    selected |= 1 << i;
                }
            }

            int newSelected = EditorGUILayout.MaskField("Grass layers", selected, names);
            if (newSelected != selected)
            {
                for (int i = 0; i < count; i++)
                {
                    bool isTicked = (newSelected & (1 << i)) != 0;
                    if (isTicked && amounts[i] <= 0f)
                    {
                        amounts[i] = 1f;
                    }
                    if (!isTicked)
                    {
                        amounts[i] = 0f;
                    }
                }
            }

            showAmounts = EditorGUILayout.Foldout(showAmounts, "Fine-tune amounts", true);
            if (showAmounts)
            {
                for (int i = 0; i < count; i++)
                {
                    amounts[i] = DrawLayerSlider(layers[i], names[i], amounts[i]);
                }
            }

            bool sizeChanged = grass.layerGrass == null || grass.layerGrass.Length != amounts.Length;
            if (EditorGUI.EndChangeCheck() || sizeChanged)
            {
                Undo.RecordObject(grass, "Grass on terrain layers");
                grass.layerGrass = amounts;
                EditorUtility.SetDirty(grass);
                SceneView.RepaintAll();
            }
        }

        float DrawLayerSlider(TerrainLayer layer, string layerName, float amount)
        {
            Texture thumbnail = null;
            if (layer != null)
            {
                thumbnail = layer.diffuseTexture;
            }

            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(15f);
            GUILayout.Label(thumbnail, GUILayout.Width(28f), GUILayout.Height(28f));

            EditorGUILayout.BeginVertical();
            EditorGUILayout.LabelField(layerName);
            amount = EditorGUILayout.Slider("Grass amount", amount, 0f, 1f);
            EditorGUILayout.EndVertical();

            EditorGUILayout.EndHorizontal();
            return amount;
        }

        void DrawButtons(MeadowGrass grass)
        {
            EditorGUILayout.Space();
            EditorGUILayout.BeginHorizontal();

            if (GUILayout.Button("Frame Terrain in Scene View"))
            {
                EndlessMeadowMenu.FrameTerrain(grass.terrain);
            }
            if (GUILayout.Button("Regenerate"))
            {
                grass.Regenerate();
            }

            EditorGUILayout.EndHorizontal();
        }
    }
}
