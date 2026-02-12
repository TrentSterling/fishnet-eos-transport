using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using EOSNative;
using EOSNative.Lobbies;
using EOSNative.Voice;
using FishNet.Object;
using FishNet.Transport.EOSNative.Demo;
using FishNet.Transport.EOSNative.Migration;

namespace FishNet.Transport.EOSNative.Editor
{
    /// <summary>
    /// Tools/FishNet EOS menu for FishNet transport setup and utilities.
    /// Parallel structure to Tools/EOS SDK menu but FishNet-specific.
    /// </summary>
    public static class EOSNativeMenu
    {
        private const string MenuRoot = "Tools/FishNet EOS/";

        #region Scene Setup

        /// <summary>
        /// Sets up the scene with NetworkManager + EOSNativeTransport.
        /// The transport's Reset() auto-setup adds EOSManager, lobbies, voice, etc.
        /// </summary>
        [MenuItem(MenuRoot + "Setup Scene", priority = 0)]
        public static void SetupScene()
        {
            var existingTransport = UnityEngine.Object.FindAnyObjectByType<EOSNativeTransport>();
            if (existingTransport != null)
            {
                Debug.Log("[FishNet EOS] EOSNativeTransport already exists. Re-running AutoSetup...");
                existingTransport.SendMessage("Reset", SendMessageOptions.DontRequireReceiver);
                WireSpawnablePrefabs(existingTransport.gameObject);
                Selection.activeGameObject = existingTransport.gameObject;
                EditorGUIUtility.PingObject(existingTransport.gameObject);
                return;
            }

            var go = new GameObject("NetworkManager");
            Undo.RegisterCreatedObjectUndo(go, "Setup FishNet EOS Scene");

            go.AddComponent<EOSNativeTransport>();

            // Ensure FishNet's NetworkManager has SpawnablePrefabs assigned
            WireSpawnablePrefabs(go);

            Selection.activeGameObject = go;
            EditorGUIUtility.PingObject(go);

            Debug.Log("[FishNet EOS] Scene setup complete! NetworkManager created with EOS transport and all subsystems.");
        }

        [MenuItem(MenuRoot + "Setup Scene", true)]
        public static bool SetupSceneValidate() => true;

        #endregion

        #region Validation

        /// <summary>
        /// Validates the current scene has all required FishNet + EOS components.
        /// </summary>
        [MenuItem(MenuRoot + "Validate Setup", priority = 50)]
        public static void ValidateSetup()
        {
            var issues = new System.Collections.Generic.List<string>();
            var warnings = new System.Collections.Generic.List<string>();

            // FishNet core
            var networkManager = UnityEngine.Object.FindAnyObjectByType<FishNet.Managing.NetworkManager>();
            if (networkManager == null)
                issues.Add("NetworkManager not found in scene");

            // Transport
            var transport = UnityEngine.Object.FindAnyObjectByType<EOSNativeTransport>();
            if (transport == null)
            {
                issues.Add("EOSNativeTransport not found in scene");
            }
            else
            {
                var configField = transport.GetType().GetField("_eosConfig",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (configField != null)
                {
                    var config = configField.GetValue(transport) as EOSConfig;
                    if (config == null)
                        issues.Add("EOSConfig not assigned on EOSNativeTransport");
                }
            }

            // SDK
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

            if (issues.Count == 0 && warnings.Count == 0)
            {
                EditorUtility.DisplayDialog("Validation Passed",
                    "All FishNet + EOS components are properly configured!", "OK");
                Debug.Log("[FishNet EOS] Validation passed.");
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
                        Debug.LogError($"[FishNet EOS] {issue}");
                    }
                }
                if (warnings.Count > 0)
                {
                    if (issues.Count > 0) message += "\n";
                    message += "WARNINGS:\n";
                    foreach (var warning in warnings)
                    {
                        message += $"  - {warning}\n";
                        Debug.LogWarning($"[FishNet EOS] {warning}");
                    }
                }

                message += "\nUse 'Tools > FishNet EOS > Setup Scene' to fix.";
                EditorUtility.DisplayDialog("Validation Issues Found", message, "OK");
            }
        }

        #endregion

        #region Demo Builder

        /// <summary>
        /// Creates FishNet demo prefabs (PlayerBall, Crate) in Assets/EOSDemo/Prefabs.
        /// </summary>
        [MenuItem(MenuRoot + "Build Demo Prefabs", priority = 20)]
        public static void BuildDemoPrefabs()
        {
            EnsureFolder("Assets", "EOSDemo");
            EnsureFolder("Assets/EOSDemo", "Prefabs");

            string prefabDir = "Assets/EOSDemo/Prefabs";

            // --- Player Ball Prefab ---
            var playerGo = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            playerGo.name = "PlayerBallPrefab";
            playerGo.transform.position = Vector3.zero;

            var rb = playerGo.GetComponent<Rigidbody>();
            if (rb == null) rb = playerGo.AddComponent<Rigidbody>();
            rb.mass = 1f;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode.Continuous;
            rb.constraints = RigidbodyConstraints.FreezeRotation;

            playerGo.AddComponent<NetworkObject>();
            var pnt = playerGo.AddComponent<PhysicsNetworkTransform>();
            pnt.rigidbodyToTrack = rb;
            pnt.claimOwnershipOnCollision = false;

            playerGo.AddComponent<PlayerBall>();
            playerGo.AddComponent<EOSNetworkPlayer>();
            playerGo.AddComponent<HostMigratable>();

            string playerPath = $"{prefabDir}/PlayerBallPrefab.prefab";
            string cratePath = $"{prefabDir}/CratePrefab.prefab";

            // Batch asset operations to prevent FishNet's PrefabCollectionGenerator
            // from running between saves (causes hash-of-0 collision).
            AssetDatabase.StartAssetEditing();
            try
            {
                PrefabUtility.SaveAsPrefabAsset(playerGo, playerPath);
                UnityEngine.Object.DestroyImmediate(playerGo);

                // --- Crate Prefab ---
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

                PrefabUtility.SaveAsPrefabAsset(crateGo, cratePath);
                UnityEngine.Object.DestroyImmediate(crateGo);
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[FishNet EOS] Created prefabs: {playerPath}, {cratePath}");

            // Wire player prefab to spawner if it exists
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
                    Debug.Log("[FishNet EOS] Assigned PlayerBallPrefab to HostMigrationPlayerSpawner.");
                }
            }

            EditorUtility.DisplayDialog("Demo Prefabs Created",
                $"Created:\n  - {playerPath}\n  - {cratePath}\n\nFishNet auto-registers spawnable prefabs.",
                "OK");
        }

        /// <summary>
        /// Builds a complete FishNet demo scene with ground, camera, NetworkManager, and crates.
        /// </summary>
        [MenuItem(MenuRoot + "Build Demo Scene", priority = 21)]
        public static void BuildDemoScene()
        {
            if (!AssetDatabase.LoadAssetAtPath<NetworkObject>("Assets/EOSDemo/Prefabs/PlayerBallPrefab.prefab"))
            {
                if (EditorUtility.DisplayDialog("Prefabs Not Found",
                    "Demo prefabs don't exist yet. Create them first?", "Create Prefabs", "Cancel"))
                {
                    BuildDemoPrefabs();
                }
                else return;
            }

            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

            // Ground
            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Ground";
            ground.transform.localScale = new Vector3(5f, 1f, 5f);
            var groundMat = new Material(Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard"));
            groundMat.color = new Color(0.3f, 0.35f, 0.3f);
            ground.GetComponent<Renderer>().material = groundMat;

            // Walls
            CreateWall("WallN", new Vector3(0f, 1f, 25f), new Vector3(50f, 2f, 0.5f));
            CreateWall("WallS", new Vector3(0f, 1f, -25f), new Vector3(50f, 2f, 0.5f));
            CreateWall("WallE", new Vector3(25f, 1f, 0f), new Vector3(0.5f, 2f, 50f));
            CreateWall("WallW", new Vector3(-25f, 1f, 0f), new Vector3(0.5f, 2f, 50f));

            // Camera
            var cam = UnityEngine.Object.FindAnyObjectByType<Camera>();
            if (cam != null)
            {
                cam.transform.position = new Vector3(0f, 15f, -10f);
                cam.transform.rotation = Quaternion.Euler(55f, 0f, 0f);
                cam.gameObject.AddComponent<SimpleCamera>();
            }

            // NetworkManager + transport
            SetupScene();

            // Crates
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

            // Save
            EnsureFolder("Assets", "EOSDemo");
            EnsureFolder("Assets/EOSDemo", "Scenes");
            string scenePath = "Assets/EOSDemo/Scenes/EOSDemoScene.unity";
            EditorSceneManager.SaveScene(scene, scenePath);

            Debug.Log($"[FishNet EOS] Demo scene created at {scenePath}");
            EditorUtility.DisplayDialog("Demo Scene Created",
                $"Scene saved to: {scenePath}\n\nIncludes:\n  - NetworkManager + EOS transport\n  - Ground + walls\n  - SimpleCamera (follows player)\n  - 5 physics crates\n\nPress Play, then click Host in the Inspector!",
                "OK");
        }

        /// <summary>
        /// Deletes all generated demo assets (prefabs, scenes, folder).
        /// </summary>
        [MenuItem(MenuRoot + "Delete Demo Assets", priority = 22)]
        public static void DeleteDemoAssets()
        {
            if (!AssetDatabase.IsValidFolder("Assets/EOSDemo"))
            {
                EditorUtility.DisplayDialog("Nothing to Delete", "Assets/EOSDemo folder does not exist.", "OK");
                return;
            }

            var prefabs = AssetDatabase.FindAssets("t:Prefab", new[] { "Assets/EOSDemo" });
            var scenes = AssetDatabase.FindAssets("t:Scene", new[] { "Assets/EOSDemo" });
            var all = AssetDatabase.FindAssets("", new[] { "Assets/EOSDemo" });

            if (!EditorUtility.DisplayDialog("Delete Demo Assets",
                $"This will delete the entire Assets/EOSDemo folder:\n\n" +
                $"  - {prefabs.Length} prefab(s)\n" +
                $"  - {scenes.Length} scene(s)\n" +
                $"  - {all.Length} total asset(s)\n\n" +
                "Are you sure?",
                "Delete", "Cancel"))
            {
                return;
            }

            AssetDatabase.DeleteAsset("Assets/EOSDemo");
            AssetDatabase.Refresh();

            Debug.Log("[FishNet EOS] Deleted Assets/EOSDemo and all contents.");
        }

        [MenuItem(MenuRoot + "Delete Demo Assets", true)]
        public static bool DeleteDemoAssetsValidate()
        {
            return AssetDatabase.IsValidFolder("Assets/EOSDemo");
        }

        #endregion

        #region Helpers

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

        /// <summary>
        /// Ensures FishNet's NetworkManager has DefaultPrefabObjects assigned.
        /// Without this, NetworkManager.Start() NullRefs and nothing works.
        /// </summary>
        private static void WireSpawnablePrefabs(GameObject networkManagerGo)
        {
            var nm = networkManagerGo.GetComponent<FishNet.Managing.NetworkManager>();
            if (nm == null) return;

            // Find existing DefaultPrefabObjects asset
            var guids = AssetDatabase.FindAssets("t:DefaultPrefabObjects");
            if (guids.Length == 0)
            {
                // Trigger FishNet's generator to create it
                Debug.Log("[FishNet EOS] DefaultPrefabObjects not found. FishNet will create it on next import.");
                return;
            }

            var path = AssetDatabase.GUIDToAssetPath(guids[0]);
            var dpo = AssetDatabase.LoadAssetAtPath<ScriptableObject>(path);
            if (dpo == null) return;

            var so = new SerializedObject(nm);
            var prop = so.FindProperty("_spawnablePrefabs");
            if (prop != null && prop.objectReferenceValue == null)
            {
                prop.objectReferenceValue = dpo;
                so.ApplyModifiedProperties();
                EditorUtility.SetDirty(nm);
                Debug.Log($"[FishNet EOS] Assigned DefaultPrefabObjects to NetworkManager.");
            }
        }

        #endregion
    }
}
