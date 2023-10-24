using System.Collections;
using System.Linq;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.Overlays;
using SocketIOClient;
using SocketIOClient.Newtonsoft.Json;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using Random = UnityEngine.Random;
using UnityEngine.SceneManagement;
using Debug = System.Diagnostics.Debug;

namespace ZeroSouth.SceneAlerter
{
    [Overlay(typeof(SceneView), "SceneAlerter")]
    public class SceneAlerterOverlay : IMGUIOverlay
    {
        static readonly string NickNamePrefsKey = "SceneAlerter_NickName";

        private SceneAlerterSettings settings;
        public static SceneAlerterOverlay Instance;

        private SocketIOUnity socket;
        private bool isConnected = false;
        private bool isWaitForConnect = false;
        private string nickName = "User";
        private bool isEditNickNameText = false;
        private int currentSceneUserCount = 0;
        private bool showUserList = true;
        private bool isPlayMode = false;

        private List<string> currentRoomUserList = new List<string>();

        private Queue<UnityAction> actionQueue = new Queue<UnityAction>();

        private EditorSceneManager.SceneOpenedCallback opendedCallback;
        private EditorSceneManager.SceneClosedCallback closedCallback;

        public SceneAlerterOverlay()
        {
            Instance = this;
            nickName = EditorPrefs.GetString(NickNamePrefsKey, $"User_{Random.Range(0, 1000)}");
        }

        public override void OnCreated()
        {
            base.OnCreated();
            settings = SceneAlerterSettings.GetOrCreateSettings();

            opendedCallback = new EditorSceneManager.SceneOpenedCallback(Enter);
            closedCallback = new EditorSceneManager.SceneClosedCallback(Leave);

            if (settings != null && settings.AutoConnected)
            {
                StartConnect();
            }
        }

        public override void OnGUI()
        {
            GUILayout.BeginHorizontal();

            if (EditorGUIUtility.editingTextField)
                isEditNickNameText = true;

            nickName = EditorGUILayout.TextArea(nickName);
            if (isEditNickNameText && !EditorGUIUtility.editingTextField)
            {
                isEditNickNameText = false;
                EditorPrefs.SetString(NickNamePrefsKey, nickName);
                actionQueue.Enqueue(() => { ChangeNickName(); });
            }
            GUILayout.EndHorizontal();

            static void HorizontalLine(Color color)
            {
                var horizontalLine = new GUIStyle
                {
                    normal = { background = EditorGUIUtility.whiteTexture },
                    margin = new RectOffset(0, 0, 4, 4),
                    fixedHeight = 1
                };

                var c = GUI.color;
                GUI.color = color;
                GUILayout.Box(GUIContent.none, horizontalLine);
                GUI.color = c;
            }
            HorizontalLine(Color.gray);

            if (isConnected)
            {
                showUserList = EditorGUILayout.Foldout(showUserList, $"{currentSceneUserCount}명 작업 중", true);

                if (showUserList)
                {
                    EditorGUI.indentLevel++;
                    foreach (var nickName in currentRoomUserList)
                    {
                        GUILayout.Label("  " + nickName);
                    }
                    EditorGUI.indentLevel--;
                }
            }
            else
            {
                if (isWaitForConnect)
                {
                    GUILayout.Label("Connecting to Server..");
                }
                else
                {
                    GUILayout.Label("Please Connecting..");
                    if (!isWaitForConnect && GUILayout.Button("Connect"))
                    {
                        StartConnect();
                    }
                }
            }
        }

        private void Update(SceneView sceneView)
        {
            while (actionQueue.Count > 0)
            {
                var action = actionQueue.Dequeue();
                action.Invoke();
            }

            EditorApplication.QueuePlayerLoopUpdate();
        }

        private void StartConnect()
        {
            if(string.IsNullOrEmpty(settings.ServerURL))
                return;

            isWaitForConnect = true;
            Debug.Print("Start Connecting Server");
            CreateSocket();
            SceneView.duringSceneGui += Update;
        }

        private void CreateSocket()
        {
            //TODO: check the Uri if Valid.
            var uri = new Uri(settings.ServerURL);
            socket = new SocketIOUnity(uri, new SocketIOOptions
            {
                Query = new Dictionary<string, string>
                {
                    {"token", "UNITY" }
                }
                ,
                EIO = 4
                ,
                Transport = SocketIOClient.Transport.TransportProtocol.WebSocket
            }
            , SocketIOUnity.UnityThreadScope.EditorUpdate);
            socket.JsonSerializer = new NewtonsoftJsonSerializer();

            ///// reserved socketio events
            socket.OnConnected += (sender, e) =>
            {
                Debug.Print("socket.OnConnected");

                isWaitForConnect = false;
                isConnected = true;
                actionQueue.Enqueue(() => { ChangeNickName(); });
            };

            socket.OnDisconnected += (sender, e) =>
            {
                Debug.Print("socket.Disconnect: " + e);
                isConnected = false;
            };
            socket.OnReconnectAttempt += (sender, e) =>
            {
                Debug.Print($"{DateTime.Now} Reconnecting: attempt = {e}");
                isConnected = false;
            };

            socket.Connect();

            socket.OnUnityThread("enter", (data) =>
            {
                Debug.Print("is entered");
            });

            socket.OnAnyInUnityThread((name, response) =>
            {
                Debug.Print("Receive : " + name);
                var dataTable = JObject.Parse(response.GetValue<object>().ToString());
                ParserEvent(name, dataTable);
            });

            EditorSceneManager.sceneClosed += closedCallback;
            EditorSceneManager.sceneOpened += opendedCallback;
        }

        private void ChangeNickName()
        {
            var json = new JObject();
            json.Add("nickname", nickName);

            Emit("nickname", json.ToString());
        }

        private void Enter(Scene scene, OpenSceneMode sceneMode)
        {
            isPlayMode = EditorApplication.isPlaying;

            currentRoomUserList.Clear();

            string[] guids = AssetDatabase.FindAssets($"t:scene {scene.name}", new[] { "Assets/Scenes" });
            string[] assetPaths = Array.ConvertAll<string, string>(guids, AssetDatabase.GUIDToAssetPath);

            var currentSceneGUID = "";

            for (var i = 0; i < guids.Length; ++i)
            {
                var sceneFileName = GetSceneName(assetPaths[i]);

                if (scene.name.Equals(sceneFileName))
                {
                    currentSceneGUID = guids[i];
                }
            }

            var json = new JObject();
            json.Add("guid", currentSceneGUID);

            Emit("enter", json.ToString());
        }

        private void Leave(Scene scene)
        {
            if (isPlayMode != EditorApplication.isPlaying)
            {
                Debug.Print($"Is Diff Playing Mode : {isPlayMode} / Current : {EditorApplication.isPlaying}");
                isPlayMode = EditorApplication.isPlaying;
                return;
            }

            string[] guids = AssetDatabase.FindAssets($"t:scene {scene.name}", new[] { "Assets/Scenes" });
            string[] assetPaths = Array.ConvertAll<string, string>(guids, AssetDatabase.GUIDToAssetPath);

            var currentSceneGUID = "";

            for (var i = 0; i < guids.Length; ++i)
            {
                var sceneFileName = GetSceneName(assetPaths[i]);

                if (scene.name.Equals(sceneFileName))
                {
                    currentSceneGUID = guids[i];
                }
            }

            var json = new JObject();
            json.Add("guid", currentSceneGUID);

            Emit("leave", json.ToString());
        }

        public void Emit(string eventName, string jsonData)
        {
            if (!IsJSON(jsonData))
            {
                Debug.Print($"Emit : {eventName} | {jsonData}");
                socket.Emit(eventName, jsonData);
            }
            else
            {
                Debug.Print($"Emit : {eventName} | {jsonData}");
                socket.EmitStringAsJSON(eventName, jsonData);
            }
        }

        public static bool IsJSON(string str)
        {
            if (string.IsNullOrWhiteSpace(str)) { return false; }
            str = str.Trim();
            if ((str.StartsWith("{") && str.EndsWith("}")) || //For object
                (str.StartsWith("[") && str.EndsWith("]"))) //For array
            {
                try
                {
                    var obj = JToken.Parse(str);
                    return true;
                }
                catch (Exception ex) //some other exception
                {
                    Console.WriteLine(ex.ToString());
                    return false;
                }
            }
            else
            {
                return false;
            }
        }

        private void ParserEvent(string eventName, JObject jsonData)
        {
            switch (eventName)
            {
                case "connection":
                    actionQueue.Enqueue(() =>
                    {
                        Enter(EditorSceneManager.GetActiveScene(), OpenSceneMode.Single);
                    });
                    break;
                case "nickname":
                    break;
                case "roominfo":
                    currentRoomUserList = jsonData.GetValue("data").Values<string>().ToList();
                    currentSceneUserCount = currentRoomUserList.Count;
                    break;
                default:
                    Debug.Print("Non-Event Handler");
                    break;
            }
        }

        public override void OnWillBeDestroyed()
        {
            base.OnWillBeDestroyed();
            Leave(EditorSceneManager.GetActiveScene());
        }

        public string GetSceneName(string assetPath)
        {
            string assetName = assetPath.Substring(assetPath.LastIndexOf("/") + 1);
            assetName = assetName.Replace(".unity", "");
            return assetName;
        }


    }


}
