using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using EOSNative;
using EOSNative.Lobbies;
using EOSNative.Voice;
using FishNet.Transport.EOSNative.Demo;
using FishNet.Transport.EOSNative.Migration;

namespace FishNet.Transport.EOSNative.Editor
{
    /// <summary>
    /// Tools menu for FishNet EOS Transport setup and utilities.
    /// </summary>
    public static class EOSNativeMenu
    {
        private const string MenuRoot = "Tools/FishNet EOS Transport/";

        /// <summary>
        /// Sets up the scene with all required EOS + FishNet components.
        /// Creates EOSNativeTransport which triggers full auto-setup via Reset().
        /// </summary>
        [MenuItem(MenuRoot + "Setup Scene", priority = 0)]
        public static void SetupScene()
        {
            // Check if transport already exists
            var existingTransport = UnityEngine.Object.FindAnyObjectByType<EOSNativeTransport>();
            if (existingTransport != null)
            {
                Debug.Log("[EOSNativeMenu] EOSNativeTransport already exists. Re-running AutoSetup...");
                existingTransport.SendMessage("Reset", SendMessageOptions.DontRequireReceiver);
                Selection.activeGameObject = existingTransport.gameObject;
                EditorGUIUtility.PingObject(existingTransport.gameObject);
                return;
            }

            // Create new GameObject with transport — AutoSetup handles everything else
            var go = new GameObject("NetworkManager");
            Undo.RegisterCreatedObjectUndo(go, "Setup FishNet EOS Transport");

            go.AddComponent<EOSNativeTransport>();

            Selection.activeGameObject = go;
            EditorGUIUtility.PingObject(go);

            Debug.Log("[EOSNativeMenu] Scene setup complete! NetworkManager created with EOS transport and all subsystems.");
        }

        [MenuItem(MenuRoot + "Setup Scene", true)]
        public static bool SetupSceneValidate() => true;

        /// <summary>
        /// Selects the EOSConfig asset in the Project window.
        /// </summary>
        [MenuItem(MenuRoot + "Select Config", priority = 1)]
        public static void SelectConfig()
        {
            // Try SampleEOSConfig first, then any EOSConfig
            var guids = AssetDatabase.FindAssets("SampleEOSConfig t:EOSConfig");
            if (guids.Length == 0)
                guids = AssetDatabase.FindAssets("EOSConfig t:EOSConfig");
            if (guids.Length == 0)
                guids = AssetDatabase.FindAssets("t:EOSConfig");

            if (guids.Length > 0)
            {
                var path = AssetDatabase.GUIDToAssetPath(guids[0]);
                var asset = AssetDatabase.LoadAssetAtPath<EOSConfig>(path);
                if (asset != null)
                {
                    Selection.activeObject = asset;
                    EditorGUIUtility.PingObject(asset);
                    Debug.Log($"[EOSNativeMenu] Selected config: {path}");
                    return;
                }
            }

            // No config found — offer to create one
            if (EditorUtility.DisplayDialog(
                "EOSConfig Not Found",
                "No EOSConfig asset found in the project.\n\nWould you like to create one?",
                "Create Config",
                "Cancel"))
            {
                CreateEOSConfig();
            }
        }

        /// <summary>
        /// Creates a new EOSConfig ScriptableObject asset.
        /// </summary>
        [MenuItem(MenuRoot + "Create New Config", priority = 2)]
        public static void CreateEOSConfig()
        {
            var config = ScriptableObject.CreateInstance<EOSConfig>();

            // Save next to the scene or in a sensible default location
            var directory = "Assets";
            var path = AssetDatabase.GenerateUniqueAssetPath($"{directory}/EOSConfig.asset");
            AssetDatabase.CreateAsset(config, path);
            AssetDatabase.SaveAssets();

            Selection.activeObject = config;
            EditorGUIUtility.PingObject(config);

            Debug.Log($"[EOSNativeMenu] Created new EOSConfig at {path}. Configure your EOS credentials in the Inspector.");
        }

        [MenuItem(MenuRoot + "Create New Config", true)]
        public static bool CreateEOSConfigValidate() => true;

        /// <summary>
        /// Logs platform info to console (useful for crossplay debugging).
        /// </summary>
        [MenuItem(MenuRoot + "Log Platform Info", priority = 51)]
        public static void LogPlatformInfo()
        {
            EOSPlatformHelper.LogPlatformInfo();
        }

        #region Validation

        /// <summary>
        /// Validates the current scene has all required components configured.
        /// </summary>
        [MenuItem(MenuRoot + "Validate Setup", priority = 50)]
        public static void ValidateSetup()
        {
            var issues = new System.Collections.Generic.List<string>();
            var warnings = new System.Collections.Generic.List<string>();

            // Check for EOSNativeTransport
            var transport = UnityEngine.Object.FindAnyObjectByType<EOSNativeTransport>();
            if (transport == null)
            {
                issues.Add("EOSNativeTransport not found in scene");
            }
            else
            {
                // Check for config assignment
                var configField = transport.GetType().GetField("_eosConfig",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (configField != null)
                {
                    var config = configField.GetValue(transport) as EOSConfig;
                    if (config == null)
                        issues.Add("EOSConfig not assigned on EOSNativeTransport");
                }
            }

            // Check for NetworkManager
            var networkManager = UnityEngine.Object.FindAnyObjectByType<FishNet.Managing.NetworkManager>();
            if (networkManager == null)
                issues.Add("NetworkManager not found in scene");

            // Check for EOSManager
            var eosManager = UnityEngine.Object.FindAnyObjectByType<EOSManager>();
            if (eosManager == null)
                issues.Add("EOSManager not found in scene");

            // Optional subsystems
            var lobbyManager = UnityEngine.Object.FindAnyObjectByType<EOSLobbyManager>();
            if (lobbyManager == null)
                warnings.Add("EOSLobbyManager not found (required for lobby features)");

            var voiceManager = UnityEngine.Object.FindAnyObjectByType<EOSVoiceManager>();
            if (voiceManager == null)
                warnings.Add("EOSVoiceManager not found (required for voice features)");

            var playerSpawner = UnityEngine.Object.FindAnyObjectByType<HostMigrationPlayerSpawner>();
            if (playerSpawner == null)
                warnings.Add("HostMigrationPlayerSpawner not found (required for player spawning)");

            // Report results
            if (issues.Count == 0 && warnings.Count == 0)
            {
                EditorUtility.DisplayDialog("Validation Passed",
                    "All required components are properly configured!", "OK");
                Debug.Log("[EOSNativeMenu] Validation passed - all components configured correctly.");
            }
            else
            {
                var message = "";
                if (issues.Count > 0)
                {
                    message += "ERRORS:\n";
                    foreach (var issue in issues)
                    {
                        message += $"  - {issue}\n";
                        Debug.LogError($"[EOSNativeMenu] {issue}");
                    }
                }
                if (warnings.Count > 0)
                {
                    if (issues.Count > 0) message += "\n";
                    message += "WARNINGS:\n";
                    foreach (var warning in warnings)
                    {
                        message += $"  - {warning}\n";
                        Debug.LogWarning($"[EOSNativeMenu] {warning}");
                    }
                }

                message += "\nUse 'Tools > FishNet EOS Transport > Setup Scene' to fix.";
                EditorUtility.DisplayDialog("Validation Issues Found", message, "OK");
            }
        }

        #endregion

        #region Demo Builder

        /// <summary>
        /// Creates demo prefabs (PlayerBall, Crate) in Assets/EOSDemo/Prefabs.
        /// </summary>
        [MenuItem(MenuRoot + "Build Demo Prefabs", priority = 20)]
        public static void BuildDemoPrefabs()
        {
            // Create folder structure
            EnsureFolder("Assets", "EOSDemo");
            EnsureFolder("Assets/EOSDemo", "Prefabs");

            string prefabDir = "Assets/EOSDemo/Prefabs";

            // --- Player Ball Prefab ---
            var playerGo = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            playerGo.name = "PlayerBallPrefab";
            playerGo.transform.position = Vector3.zero;

            // Physics
            var rb = playerGo.GetComponent<Rigidbody>();
            if (rb == null) rb = playerGo.AddComponent<Rigidbody>();
            rb.mass = 1f;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode.Continuous;
            rb.constraints = RigidbodyConstraints.FreezeRotation;

            // FishNet
            var nob = playerGo.AddComponent<NetworkObject>();

            // Physics sync
            var pnt = playerGo.AddComponent<PhysicsNetworkTransform>();
            pnt.rigidbodyToTrack = rb;
            pnt.claimOwnershipOnCollision = false; // player already owns it

            // Game logic
            playerGo.AddComponent<PlayerBall>();
            playerGo.AddComponent<EOSNetworkPlayer>();
            playerGo.AddComponent<HostMigratable>();

            // Save prefab
            string playerPath = $"{prefabDir}/PlayerBallPrefab.prefab";
            var playerPrefab = PrefabUtility.SaveAsPrefabAsset(playerGo, playerPath);
            UnityEngine.Object.DestroyImmediate(playerGo);
            Debug.Log($"[EOSNativeMenu] Created player prefab: {playerPath}");

            // --- Crate Prefab (physics object) ---
            var crateGo = GameObject.CreatePrimitive(PrimitiveType.Cube);
            crateGo.name = "CratePrefab";
            crateGo.transform.localScale = new Vector3(0.8f, 0.8f, 0.8f);

            var crateRb = crateGo.GetComponent<Rigidbody>();
            if (crateRb == null) crateRb = crateGo.AddComponent<Rigidbody>();
            crateRb.mass = 2f;

            crateGo.AddComponent<NetworkObject>();
            var cratePnt = crateGo.AddComponent<PhysicsNetworkTransform>();
            cratePnt.rigidbodyToTrack = crateRb;
            crateGo.AddComponent<NetworkPhysicsObject>();
            crateGo.AddComponent<HostMigratable>();

            string cratePath = $"{prefabDir}/CratePrefab.prefab";
            PrefabUtility.SaveAsPrefabAsset(crateGo, cratePath);
            UnityEngine.Object.DestroyImmediate(crateGo);
            Debug.Log($"[EOSNativeMenu] Created crate prefab: {cratePath}");

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            // Wire player prefab to HostMigrationPlayerSpawner if it exists
            var spawner = UnityEngine.Object.FindAnyObjectByType<HostMigrationPlayerSpawner>();
            if (spawner != null)
            {
                var playerNob = AssetDatabase.LoadAssetAtPath<NetworkObject>(playerPath);
                if (playerNob != null)
                {
                    var so = new SerializedObject(spawner);
                    so.FindProperty("_playerPrefab").objectReferenceValue = playerNob;
                    so.ApplyModifiedProperties();
                    EditorUtility.SetDirty(spawner);
                    Debug.Log("[EOSNativeMenu] Assigned PlayerBallPrefab to HostMigrationPlayerSpawner.");
                }
            }

            // FishNet's auto-generator detects NetworkObject prefabs automatically —
            // no need to manually add to DefaultPrefabObjects.

            EditorUtility.DisplayDialog("Demo Prefabs Created",
                $"Created:\n  - {playerPath}\n  - {cratePath}\n\nPlayerBall assigned to player spawner.\nFishNet auto-registers spawnable prefabs.",
                "OK");
        }

        /// <summary>
        /// Builds a complete demo scene with ground, camera, NetworkManager, and crates.
        /// </summary>
        [MenuItem(MenuRoot + "Build Demo Scene", priority = 21)]
        public static void BuildDemoScene()
        {
            // Ensure prefabs exist first
            if (!AssetDatabase.LoadAssetAtPath<NetworkObject>("Assets/EOSDemo/Prefabs/PlayerBallPrefab.prefab"))
            {
                if (EditorUtility.DisplayDialog("Prefabs Not Found",
                    "Demo prefabs don't exist yet. Create them first?", "Create Prefabs", "Cancel"))
                {
                    BuildDemoPrefabs();
                }
                else return;
            }

            // Create new scene
            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

            // Ground plane (scaled up)
            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Ground";
            ground.transform.localScale = new Vector3(5f, 1f, 5f);
            var groundMat = new Material(Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard"));
            groundMat.color = new Color(0.3f, 0.35f, 0.3f);
            ground.GetComponent<Renderer>().material = groundMat;

            // Walls to keep balls in
            CreateWall("WallN", new Vector3(0f, 1f, 25f), new Vector3(50f, 2f, 0.5f));
            CreateWall("WallS", new Vector3(0f, 1f, -25f), new Vector3(50f, 2f, 0.5f));
            CreateWall("WallE", new Vector3(25f, 1f, 0f), new Vector3(0.5f, 2f, 50f));
            CreateWall("WallW", new Vector3(-25f, 1f, 0f), new Vector3(0.5f, 2f, 50f));

            // Camera — add SimpleCamera for player following
            var cam = UnityEngine.Object.FindAnyObjectByType<Camera>();
            if (cam != null)
            {
                cam.transform.position = new Vector3(0f, 15f, -10f);
                cam.transform.rotation = Quaternion.Euler(55f, 0f, 0f);
                cam.gameObject.AddComponent<SimpleCamera>();
            }

            // Setup NetworkManager via our existing SetupScene
            SetupScene();

            // Spawn some crates in the scene
            var cratePrefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/EOSDemo/Prefabs/CratePrefab.prefab");
            if (cratePrefab != null)
            {
                for (int i = 0; i < 5; i++)
                {
                    float x = Random.Range(-8f, 8f);
                    float z = Random.Range(-8f, 8f);
                    var crate = (GameObject)PrefabUtility.InstantiatePrefab(cratePrefab);
                    crate.transform.position = new Vector3(x, 1f, z);
                    crate.name = $"Crate_{i}";
                }
            }

            // Save scene
            EnsureFolder("Assets", "EOSDemo");
            EnsureFolder("Assets/EOSDemo", "Scenes");
            string scenePath = "Assets/EOSDemo/Scenes/EOSDemoScene.unity";
            EditorSceneManager.SaveScene(scene, scenePath);

            Debug.Log($"[EOSNativeMenu] Demo scene created at {scenePath}");
            EditorUtility.DisplayDialog("Demo Scene Created",
                $"Scene saved to: {scenePath}\n\nIncludes:\n  - NetworkManager + EOS transport\n  - Ground + walls\n  - SimpleCamera (follows player)\n  - 5 physics crates\n\nPress Play, then click Host in the Inspector!",
                "OK");
        }

        private static void CreateWall(string name, Vector3 position, Vector3 scale)
        {
            var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
            wall.name = name;
            wall.transform.position = position;
            wall.transform.localScale = scale;
            wall.isStatic = true;

            var wallMat = new Material(Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard"));
            wallMat.color = new Color(0.5f, 0.45f, 0.4f);
            wall.GetComponent<Renderer>().material = wallMat;
        }

        private static void EnsureFolder(string parent, string name)
        {
            string path = $"{parent}/{name}";
            if (!AssetDatabase.IsValidFolder(path))
                AssetDatabase.CreateFolder(parent, name);
        }

        #endregion
    }
}

