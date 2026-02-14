#if UNITY_EDITOR
using System;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

namespace FishNet.Transport.EOSNative.Bootstrap
{
    /// <summary>
    /// Auto-detects FishNet and manages the FISHNET_V4 scripting define.
    /// This allows the transport package to be installed before FishNet
    /// without causing compilation errors.
    /// </summary>
    [InitializeOnLoad]
    internal static class FishNetDetector
    {
        private const string Define = "FISHNET_V4";

        static FishNetDetector()
        {
            // Delay to avoid modifying defines during compilation
            EditorApplication.delayCall += DetectFishNet;
        }

        private static void DetectFishNet()
        {
            bool hasFishNet = AppDomain.CurrentDomain.GetAssemblies()
                .Any(a => a.GetName().Name == "FishNet.Runtime");

            var targetGroup = EditorUserBuildSettings.selectedBuildTargetGroup;
            if (targetGroup == BuildTargetGroup.Unknown)
                targetGroup = BuildTargetGroup.Standalone;

            var namedTarget = NamedBuildTarget.FromBuildTargetGroup(targetGroup);
            string defines = PlayerSettings.GetScriptingDefineSymbols(namedTarget);
            var defineList = defines.Split(';')
                .Where(d => !string.IsNullOrWhiteSpace(d))
                .ToList();
            bool hasDefine = defineList.Contains(Define);

            if (hasFishNet && !hasDefine)
            {
                defineList.Add(Define);
                PlayerSettings.SetScriptingDefineSymbols(namedTarget, string.Join(";", defineList));
                Debug.Log($"[FishNet EOS Transport] FishNet detected — added {Define} scripting define. Transport will compile on next refresh.");
            }
            else if (!hasFishNet && hasDefine)
            {
                defineList.Remove(Define);
                PlayerSettings.SetScriptingDefineSymbols(namedTarget, string.Join(";", defineList));
                Debug.Log($"[FishNet EOS Transport] FishNet not found — removed {Define} scripting define.");
            }
        }
    }
}
#endif
