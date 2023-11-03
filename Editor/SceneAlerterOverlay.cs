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
using System.IO;
using System.Reflection;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

using Debug = System.Diagnostics.Debug;
using Random = UnityEngine.Random;

namespace ZeroSouth.SceneAlerter
{
    [Overlay(typeof(SceneView), "SceneAlerter", true)]
    public class SceneAlerterOverlay : Overlay, ICreateHorizontalToolbar
    {
        static readonly string NickNamePrefsKey = "SceneAlerter_NickName";

        private SceneAlerterSettings settings;
        public static SceneAlerterOverlay Instance;

        private SocketIOUnity socket;

        private bool isInitialize = false;

        private bool isConnected = false;
        public bool IsConnected => isConnected;

        private bool isWaitForConnect = false;
        public bool IsWaitForConnect => isWaitForConnect;

        private string nickName = "User";
        private bool isEditNickNameText = false;
        private int currentSceneUserCount = 0;
        private bool showUserList = true;
        private bool isPlayMode = false;

        private List<string> currentRoomUserList = new List<string>();

        private Queue<UnityAction> actionQueue = new Queue<UnityAction>();

        private EditorSceneManager.SceneOpenedCallback opendedCallback;
        private EditorSceneManager.SceneClosedCallback closedCallback;

        private TextField nickNameTextField;
        private Button userCountButton;
        private Label userNickNameLabel;

        private VisualElement panelElement;

        public SceneAlerterOverlay()
        {
            Instance = this;
            nickName = EditorPrefs.GetString(NickNamePrefsKey);

            userCountButton = new Button();
            nickNameTextField = new TextField();
            nickNameTextField.RegisterCallback<KeyDownEvent>(e =>
            {
                if (e.keyCode == KeyCode.Return)
                {
                    nickName = nickNameTextField.value;
                    RefreshNickname();
                }
            });
            userNickNameLabel = new Label();
        }

        /// <summary>
        /// 이름이 비어있을 때 사용자 .gitconfig에서 닉네임을 가져와 설정합니다. 실패 시 기본 User_000 이름으로 설정합니다.
        /// </summary>
        private void RefreshNickname()
        {
            // 정상적인 이름(User_ 제외)이 있으면 그대로 저장
            if (!string.IsNullOrWhiteSpace(nickName) && !nickName.StartsWith("User_")
                || TryReadUnityAccountName(out nickName) // 없으면 유니티 계정 이름 가져오기 try
                || TryReadGitName(out nickName) // 없으면 .gitconfig 이름 가져오기 try
            )
            {
                EditorPrefs.SetString(NickNamePrefsKey, nickName);
                actionQueue.Enqueue(ChangeNickName);
                return;
            }

            // 모든 fallback 실패 시 랜덤 이름
            nickName = $"User_{Random.Range(0, 1000):000}";
        }

        /// <summary>
        /// 사용자의 현재 유니티 계정을 참조해 이름을 가져옵니다.
        /// </summary>
        /// <param name="accountName">유니티 계정에서 가져온 이름입니다.</param>
        /// <returns>성공 시 true를 반환합니다.</returns>
        private bool TryReadUnityAccountName(out string accountName)
        {
            try
            {
                Assembly assembly = Assembly.GetAssembly(typeof(UnityEditor.EditorWindow));
                object uc = assembly.CreateInstance("UnityEditor.Connect.UnityConnect", false, BindingFlags.NonPublic | BindingFlags.Instance, null, null, null, null);

                // Cache type of UnityConnect.
                Type t = uc.GetType();

                // Get user info object from UnityConnect.
                var userInfo = t.GetProperty("userInfo").GetValue(uc, null);

                // Retrieve user id from user info.
                Type userInfoType = userInfo.GetType();
                accountName = userInfoType.GetProperty("displayName").GetValue(userInfo, null) as string;
                return accountName != "anonymous"; // 익명 판정이 아니면 성공
            }
            catch (Exception e)
            {
                Debug.WriteLine(e);
            }

            accountName = string.Empty;
            return false;
        }

        /// <summary>
        /// 사용자의 %userprofile%/.gitconfig 를 참조해 이름을 가져옵니다.
        /// </summary>
        /// <param name="gitName">.gitconfig에서 읽은 name 설정값입니다.</param>
        /// <returns>성공 시 true를 반환합니다.</returns>
        private bool TryReadGitName(out string gitName)
        {
            // %userprofile%\.gitconfig 파일 가져오기
            var userProfilePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var gitConfigPath = Path.Combine(userProfilePath, ".gitconfig");

            try
            {
                // 파일 읽기
                using var file = File.OpenText(gitConfigPath);
                var raw = file.ReadToEnd();
                var lines = raw.Split("\n");
                foreach (var line in lines)
                {
                    var trimmedLine = line.Trim();
                    // name 라인 읽어오기
                    if (trimmedLine.StartsWith("name"))
                    {
                        gitName = trimmedLine.Substring(7);
                        return true;
                    }
                }
            }
            catch (Exception e)
            {
                Debug.WriteLine(e);
            }

            gitName = string.Empty;
            return false;
        }

        private void Initialize()
        {
            isInitialize = true;

            settings = SceneAlerterSettings.GetOrCreateSettings();

            opendedCallback = new EditorSceneManager.SceneOpenedCallback(Enter);
            closedCallback = new EditorSceneManager.SceneClosedCallback(Leave);

            if (settings.AutoConnected)
            {
                StartConnect();
            }
        }

        public void InitializeAlerter(SceneView sceneView)
        {
            Initialize();
            SceneView.beforeSceneGui -= InitializeAlerter;
        }

        private void Update(SceneView sceneView)
        {
            userCountButton.text = $"{currentSceneUserCount}명 사용중";

            userNickNameLabel.text = "";

            for (var i = 0; i < currentRoomUserList.Count; ++i)
            {
                if (i > 0)
                    userNickNameLabel.text += "\n";

                userNickNameLabel.text += currentRoomUserList[i];
            }

            while (actionQueue.Count > 0)
            {
                var action = actionQueue.Dequeue();
                action.Invoke();
            }

            EditorApplication.QueuePlayerLoopUpdate();
        }

        private void StartConnect()
        {
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
                actionQueue.Enqueue(() =>
                {
                    RefreshNickname();
                    nickNameTextField.value = nickName;
                });
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
            if (socket == null)
                return;

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

        public string GetSceneName(string assetPath)
        {
            string assetName = assetPath.Substring(assetPath.LastIndexOf("/") + 1);
            assetName = assetName.Replace(".unity", "");
            return assetName;
        }

        private void ToggleFoldOut()
        {
            showUserList = !showUserList;
            if (!showUserList && panelElement.Contains(userNickNameLabel))
            {
                panelElement.Remove(userNickNameLabel);
            }
            else
            {
                panelElement.Add(userNickNameLabel);
            }
        }

        private void TogglePopup()
        {
            var activatorRect = GUIUtility.GUIToScreenRect(userCountButton.worldBound);
            var sceneAlerterPopup = EditorWindow.CreateInstance<SceneAlerterPopup>();
            sceneAlerterPopup.currentRoomUserList = currentRoomUserList;
            sceneAlerterPopup.ShowAsDropDown(activatorRect, new Vector2(100, 100));
        }

        public override void OnCreated()
        {
            base.OnCreated();
            SceneView.beforeSceneGui += InitializeAlerter;
        }

        public override void OnWillBeDestroyed()
        {
            base.OnWillBeDestroyed();

            SceneView.beforeSceneGui -= InitializeAlerter;
            EditorSceneManager.sceneClosed -= closedCallback;
            EditorSceneManager.sceneOpened -= opendedCallback;

            Leave(EditorSceneManager.GetActiveScene());
        }

        public override VisualElement CreatePanelContent()
        {
            panelElement = new VisualElement() { name = "Scene Alerter" };
            userCountButton.clicked -= TogglePopup;
            userCountButton.clicked += ToggleFoldOut;

            nickNameTextField.value = nickName;
            panelElement.Add(nickNameTextField);

            userCountButton.name = $"{currentSceneUserCount} 명 사용중";
            panelElement.Add(userCountButton);

            if (showUserList)
            {
                panelElement.Add(userNickNameLabel);
            }

            return panelElement;
        }

        public OverlayToolbar CreateHorizontalToolbarContent()
        {
            var horizontalOverlayToolbar = new OverlayToolbar();
            userCountButton.clicked -= ToggleFoldOut;
            userCountButton.clicked += TogglePopup;
            horizontalOverlayToolbar.Add(userCountButton);

            return horizontalOverlayToolbar;
        }

    }

    public class SceneAlerterPopup : EditorWindow
    {
        Vector2 scrollPos;
        public List<string> currentRoomUserList = new List<string>();

        public void OnGUI()
        {
            GUILayout.BeginHorizontal();
            GUILayout.BeginScrollView(scrollPos);
            foreach (var nickName in currentRoomUserList)
            {
                GUILayout.Label(nickName);
            }
            GUILayout.EndScrollView();
            GUILayout.EndHorizontal();
        }
    }
}