using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace ZeroSouth.SceneAlerter
{
    internal class SceneAlerterPostProccessor : AssetPostprocessor
    {
        private static void OnPostprocessAllAssets(string[] importedAssets, string[] deletedAssets, string[] movedAssets, string[] movedFromAssetPaths)
        {
            var settings = (SceneAlerterSettings)AssetDatabase.LoadAssetAtPath(settingsPath, typeof(SceneAlerterSettings));

            if (settings == null)
            {
                settings = ScriptableObject.CreateInstance<SceneAlerterSettings>();
                settings.serverURL = "https://port-0-unity-scenealerter-euegqv2llo151tb1.sel5.cloudtype.app/";
                settings.autoConnected = true;
                AssetDatabase.CreateAsset(settings, settingsPath);
                AssetDatabase.SaveAssets();
            }
        }
    }


    class SceneAlerterSettings : ScriptableObject
    {
        public const string settingsPath = "Packages/com.kyn320.unityscenealerter/SceneAlerterSettings.asset";

        [SerializeField]
        private string serverURL = "https://port-0-unity-scenealerter-euegqv2llo151tb1.sel5.cloudtype.app/";

        public string ServerURL => serverURL;

        [SerializeField]
        private bool autoConnected = true;
        public bool AutoConnected => autoConnected;

        internal static SceneAlerterSettings GetOrCreateSettings()
        {
            var settings = (SceneAlerterSettings)AssetDatabase.LoadAssetAtPath(settingsPath, typeof(SceneAlerterSettings));

            if (settings == null)
            {
                settings = ScriptableObject.CreateInstance<SceneAlerterSettings>();
                settings.serverURL = "https://port-0-unity-scenealerter-euegqv2llo151tb1.sel5.cloudtype.app/";
                settings.autoConnected = true;
                AssetDatabase.CreateAsset(settings, settingsPath);
                AssetDatabase.SaveAssets();
            }
            return settings;
        }

        internal static SerializedObject GetSerializedSettings()
        {
            return new SerializedObject(GetOrCreateSettings());
        }
    }

    static class SceneAlerterSettingsIMGUIRegister
    {
        [SettingsProvider]
        public static SettingsProvider CreateMyCustomSettingsProvider()
        {
            // First parameter is the path in the Settings window.
            // Second parameter is the scope of this setting: it only appears in the Project Settings window.
            var provider = new SettingsProvider("Project/SceneAlerterSettings", SettingsScope.Project)
            {
                // By default the last token of the path is used as display name if no label is provided.
                label = "Scene Alerter",
                // Create the SettingsProvider and initialize its drawing (IMGUI) function in place:
                guiHandler = (searchContext) =>
                {
                    var settings = SceneAlerterSettings.GetSerializedSettings();
                    EditorGUILayout.PropertyField(settings.FindProperty("serverURL"), new GUIContent("Server URL"));
                    EditorGUILayout.PropertyField(settings.FindProperty("autoConnected"), new GUIContent("Auto Connected"));

                    settings.ApplyModifiedProperties();
                },

                // Populate the search keywords to enable smart search filtering and label highlighting:
                keywords = new HashSet<string>(new[] { "Server URL", "Auto Connected" })
            };

            return provider;
        }
    }
}