using HeathenEngineering.SteamworksIntegration;
using Mirror;
using Newtonsoft.Json;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEngine;

namespace SephiriaTrial
{
    public partial class EndlessMod
    {
        internal sealed class TrialBuildInfo
        {
            public TrialBuildInfo() { }

            public string version = string.Empty;
            public string dllHash = string.Empty;
            public string bundleHash = string.Empty;
            public string playerGuid = string.Empty;
        }

        private enum TrialBuildCheck { Ready, Pending, Missing, Mismatch }

        private static readonly Dictionary<int, TrialBuildInfo> TrialPeerBuilds = new Dictionary<int, TrialBuildInfo>();
        private static readonly Dictionary<int, float> TrialPeerFirstSeen = new Dictionary<int, float>();
        private static TrialBuildInfo _trialLocalBuild = new TrialBuildInfo();
        private static float _nextTrialBuildReport;
        private static float _trialPartyMissingSince = -1f;
        private static bool _trialPartyWasCompleteInRun;
        private static bool _trialDisconnectPaused;
        private static bool _trialDisconnectPopupReady;
        private static bool _trialDisconnectCheckpointVerified;
        private static string _trialDisconnectedPlayerName = string.Empty;
        private static UI_MessageBox? _trialDisconnectPopup;

        private static void InitializeTrialMultiplayerSafety(string version)
        {
            _trialLocalBuild = new TrialBuildInfo
            {
                version = version,
                dllHash = HashTrialFile(typeof(EndlessMod).Assembly.Location),
                bundleHash = HashTrialFile(TrialFloorBundlePath)
            };
            TrialPeerBuilds.Clear();
            TrialPeerFirstSeen.Clear();
            _nextTrialBuildReport = 0f;
            _trialPartyWasCompleteInRun = false;
            _trialPartyMissingSince = -1f;
            _trialDisconnectPaused = false;
            _trialDisconnectPopupReady = false;
            _trialDisconnectCheckpointVerified = false;
            _trialDisconnectedPlayerName = string.Empty;
            _trialDisconnectPopup = null;
        }

        private static string HashTrialFile(string path)
        {
            try
            {
                using FileStream stream = File.OpenRead(path);
                using SHA256 sha = SHA256.Create();
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty);
            }
            catch (Exception exception)
            {
                Debug.LogWarning("[시련] 멀티플레이 빌드 확인 파일을 읽지 못했습니다: " + exception.Message);
                return string.Empty;
            }
        }

        private static void ShutdownTrialMultiplayerSafety()
        {
            if (_trialDisconnectPaused) Time.timeScale = 1f;
            TrialPeerBuilds.Clear();
            TrialPeerFirstSeen.Clear();
            _trialPartyWasCompleteInRun = false;
            _trialPartyMissingSince = -1f;
            _trialDisconnectPaused = false;
            _trialDisconnectPopupReady = false;
            _trialDisconnectCheckpointVerified = false;
            _trialDisconnectPopup = null;
        }

        private static void MarkTrialPartyStarted()
        {
            _trialPartyWasCompleteInRun = ActiveTrialPartyGuids.Count > 1 && IsActiveTrialPartyComplete();
            _trialPartyMissingSince = -1f;
        }

        private static void UpdateTrialMultiplayerSafety()
        {
            if (_trialDisconnectPaused)
            {
                Time.timeScale = 0f;
                OpenTrialDisconnectPopup();
                return;
            }

            if (NetworkClient.active && !NetworkServer.active &&
                Time.unscaledTime >= _nextTrialBuildReport && NetworkClient.connection != null)
            {
                _nextTrialBuildReport = Time.unscaledTime + 2f;
                _trialLocalBuild.playerGuid = HorayNetworkAuthenticator.GetMyPlayerGuid();
                TrialNetworkBridge.SendAction(TrialNetworkBridge.ReportBuild,
                    text: JsonConvert.SerializeObject(_trialLocalBuild));
            }

            if (!NetworkServer.active) return;
            var connectedIds = new HashSet<int>();
            foreach (NetworkConnectionToClient connection in NetworkServer.connections.Values)
            {
                if (connection == null || connection == NetworkServer.localConnection || connection.identity == null)
                    continue;
                connectedIds.Add(connection.connectionId);
                if (!TrialPeerFirstSeen.ContainsKey(connection.connectionId))
                    TrialPeerFirstSeen[connection.connectionId] = Time.unscaledTime;
            }
            foreach (int id in TrialPeerFirstSeen.Keys.Where(id => !connectedIds.Contains(id)).ToArray())
            {
                TrialPeerFirstSeen.Remove(id);
                TrialPeerBuilds.Remove(id);
            }

            if (_activeTrialSlot == 0 || ActiveTrialPartyGuids.Count < 2 ||
                SaveManager.CurrentRun?.GetBool("RunStarted", fallback: false) != true)
            {
                _trialPartyWasCompleteInRun = false;
                _trialPartyMissingSince = -1f;
                return;
            }
            if (IsActiveTrialPartyComplete())
            {
                _trialPartyWasCompleteInRun = true;
                _trialPartyMissingSince = -1f;
                return;
            }
            if (!_trialPartyWasCompleteInRun) return; // A restored lobby is still gathering its saved party.
            if (_trialPartyMissingSince < 0f) _trialPartyMissingSince = Time.unscaledTime;
            if (Time.unscaledTime - _trialPartyMissingSince < 0.5f) return;
            string playerName = GetDisconnectedTrialPlayerName();
            _trialDisconnectPaused = true;
            _trialDisconnectPopupReady = false;
            _trialDisconnectCheckpointVerified = false;
            _trialDisconnectedPlayerName = playerName;
            Time.timeScale = 0f;
            TrialController.Instance?.PauseForPartyDisconnect();
            TrialNetworkBridge.BroadcastNotice(TrialNetworkBridge.PartyDisconnected, text: playerName);
            CoroutineManager.Instance.StartCoroutine(ShowTrialDisconnectAfterCheckpointWrites());
        }

        private static string GetDisconnectedTrialPlayerName()
        {
            var connected = new HashSet<string>(StringComparer.Ordinal);
            foreach (NetworkConnectionToClient connection in NetworkServer.connections.Values)
            {
                PlayerSpawner? player = connection?.identity?.GetComponent<PlayerSpawner>();
                if (!string.IsNullOrWhiteSpace(player?.playerGuid))
                    connected.Add(player.playerGuid);
            }
            for (int i = 0; i < ActiveTrialPartyGuids.Count; i++)
                if (!connected.Contains(ActiveTrialPartyGuids[i]))
                    return SafeTrialPlayerName(i < ActiveTrialPartyNames.Count
                        ? ActiveTrialPartyNames[i] : ActiveTrialPartyGuids[i]);
            return "Player";
        }

        private static string SafeTrialPlayerName(string name) =>
            (string.IsNullOrWhiteSpace(name) ? "Player" : name).Replace("<", "").Replace(">", "");

        private static IEnumerator ShowTrialDisconnectAfterCheckpointWrites()
        {
            float deadline = Time.unscaledTime + 10f;
            while (!_trialSlotWriteTask.IsCompleted && Time.unscaledTime < deadline) yield return null;
            if (!_trialSlotWriteTask.IsCompleted)
                Debug.LogError("[시련] 체크포인트 기록 대기 시간이 초과되었습니다. 마지막으로 기록된 슬롯을 확인합니다.");
            if (_trialSlotWriteTask.IsFaulted)
                Debug.LogError("[시련] 마지막 체크포인트 기록 실패: " + _trialSlotWriteTask.Exception);
            string fileName = GetTrialSlotFileName(_activeTrialSlot);
            var persisted = new SaveData(useEncryption: true);
            if (SaveData.Exists(fileName) && persisted.Load(fileName))
            {
                int savedPhase = persisted.GetInt(GetTrialSlotPrefix(_activeTrialSlot) + "Phase", 0);
                _trialDisconnectCheckpointVerified = savedPhase > 0 &&
                    VerifyTrialSlotFile(_activeTrialSlot, savedPhase);
            }
            _trialDisconnectPopupReady = true;
            OpenTrialDisconnectPopup();
        }

        internal static void ShowTrialPartyDisconnectPopup(string playerName)
        {
            _trialDisconnectPaused = true;
            _trialDisconnectPopupReady = true;
            _trialDisconnectCheckpointVerified = false; // Only the host can inspect the slot file.
            _trialDisconnectedPlayerName = SafeTrialPlayerName(playerName);
            Time.timeScale = 0f;
            if (NetworkManager.singleton is HorayNetworkManager manager && !NetworkServer.active)
                manager.requestSelfLeave = true; // Native disconnect must not open a second popup.
            OpenTrialDisconnectPopup();
        }

        private static void OpenTrialDisconnectPopup()
        {
            if (!_trialDisconnectPaused || !_trialDisconnectPopupReady ||
                _trialDisconnectPopup != null || UIManager.Instance == null) return;
            UI_MessageBoxHolder? holder = UIManager.Instance.GetElement<UI_MessageBoxHolder>();
            if (holder == null) return;
            string key = _trialDisconnectCheckpointVerified
                ? "trial.disconnect.confirm" : "trial.disconnect.save_warning";
            string template = GetSafeText(key,
                "{0} disconnected. The Trial stopped. Confirm to return to title.");
            _trialDisconnectPopup = holder.OpenYes(string.Format(template, _trialDisconnectedPlayerName),
                LeaveTrialAfterPartyDisconnect);
        }

        private static void LeaveTrialAfterPartyDisconnect()
        {
            _trialDisconnectPaused = false;
            _trialDisconnectPopupReady = false;
            _trialDisconnectCheckpointVerified = false;
            _trialDisconnectPopup = null;
            Time.timeScale = 1f;
            if (!(NetworkManager.singleton is HorayNetworkManager manager)) return;
            manager.requestSelfLeave = true;
            manager.MarkAsShuttingDown();
            if (NetworkServer.active) manager.StopHost();
            else if (NetworkClient.active) manager.StopClient();
            GameObject steamManager = SingletonObject.Find("SteamManager");
            if (steamManager != null && steamManager.TryGetComponent<LobbyManager>(out var lobby) && lobby.HasLobby)
                lobby.Leave();
            manager.GoToTitleScene();
        }

        internal static bool IsTrialDisconnectPaused => _trialDisconnectPaused;

        internal static void RecordTrialClientBuild(NetworkConnectionToClient? sender, string json)
        {
            if (!NetworkServer.active || sender?.identity == null || json.Length > 512) return;
            try
            {
                TrialBuildInfo? info = JsonConvert.DeserializeObject<TrialBuildInfo>(json);
                PlayerSpawner? player = sender.identity.GetComponent<PlayerSpawner>();
                if (info == null || player == null || string.IsNullOrWhiteSpace(info.version) ||
                    info.playerGuid != player.playerGuid) return;
                TrialPeerBuilds[sender.connectionId] = info;
            }
            catch (Exception exception)
            {
                Debug.LogWarning("[시련] 클라이언트 빌드 정보 읽기 실패: " + exception.Message);
            }
        }

        private static TrialBuildCheck CheckTrialPartyBuilds(out string names)
        {
            names = string.Empty;
            if (!NetworkServer.active) return TrialBuildCheck.Pending;
            var missing = new List<string>();
            var members = new List<KeyValuePair<string, TrialBuildInfo>>();
            foreach (NetworkConnectionToClient connection in NetworkServer.connections.Values)
            {
                if (connection?.identity == null) return TrialBuildCheck.Pending;
                PlayerAvatar? avatar = connection.identity.GetComponent<PlayerAvatar>();
                string name = SafeTrialPlayerName(avatar?.Name ?? "Player");
                if (connection == NetworkServer.localConnection)
                {
                    members.Add(new KeyValuePair<string, TrialBuildInfo>(name, _trialLocalBuild));
                    continue;
                }
                if (!TrialPeerBuilds.TryGetValue(connection.connectionId, out TrialBuildInfo? info) ||
                    info.playerGuid != connection.identity.GetComponent<PlayerSpawner>()?.playerGuid)
                {
                    if (!TrialPeerFirstSeen.TryGetValue(connection.connectionId, out float firstSeen) ||
                        Time.unscaledTime - firstSeen < 3f) return TrialBuildCheck.Pending;
                    missing.Add(name);
                    continue;
                }
                members.Add(new KeyValuePair<string, TrialBuildInfo>(name, info));
            }
            if (missing.Count > 0)
            {
                names = string.Join(", ", missing);
                return TrialBuildCheck.Missing;
            }
            if (members.Count == 0) return TrialBuildCheck.Pending;
            Version newest = new Version(0, 0);
            foreach (KeyValuePair<string, TrialBuildInfo> member in members)
                if (Version.TryParse(member.Value.version.TrimStart('v', 'V'), out Version? parsed) &&
                    parsed != null && parsed.CompareTo(newest) > 0) newest = parsed;
            var outdated = members.Where(member =>
                !Version.TryParse(member.Value.version.TrimStart('v', 'V'), out Version? parsed) ||
                parsed == null || parsed.CompareTo(newest) < 0)
                .Select(member => member.Key + " (" + member.Value.version + ")").ToList();
            if (outdated.Count == 0)
                outdated.AddRange(members.Where(member =>
                    member.Value.dllHash != _trialLocalBuild.dllHash ||
                    member.Value.bundleHash != _trialLocalBuild.bundleHash ||
                    string.IsNullOrEmpty(member.Value.dllHash) ||
                    string.IsNullOrEmpty(member.Value.bundleHash))
                    .Select(member => member.Key + " (" + member.Value.version + ")"));
            if (outdated.Count == 0) return TrialBuildCheck.Ready;
            names = string.Join(", ", outdated);
            return TrialBuildCheck.Mismatch;
        }

        internal static bool RequireCompatibleTrialParty()
        {
            TrialBuildCheck check = CheckTrialPartyBuilds(out string names);
            if (check == TrialBuildCheck.Ready) return true;
            if (check == TrialBuildCheck.Pending)
                ShowLocalizedSystemMessage("trial.msg.build_check_pending");
            else if (check == TrialBuildCheck.Missing)
            {
                ShowTrialMissingModPopup(names);
                // Older mod builds understand the existing SystemMessage
                // operation even though they cannot answer the new handshake.
                foreach (NetworkConnectionToClient connection in NetworkServer.connections.Values)
                    if (connection != null && connection != NetworkServer.localConnection &&
                        !TrialPeerBuilds.ContainsKey(connection.connectionId))
                        TrialNetworkBridge.SendNoticeTo(connection, TrialNetworkBridge.SystemMessage,
                            text: "Trial mod missing or outdated. Update Endless Trial before entering.");
            }
            else
            {
                ShowTrialVersionMismatchPopup(names);
                TrialNetworkBridge.BroadcastNotice(TrialNetworkBridge.VersionMismatch, text: names);
            }
            return false;
        }

        private static void ShowTrialMissingModPopup(string names)
        {
            if (UIManager.Instance == null) return;
            string text = GetSafeText("trial.msg.mod_missing",
                "Trial mod not installed or not responding: {0}. Cannot enter.");
            UIManager.Instance.GetElement<UI_MessageBoxHolder>()?.OpenYes(string.Format(text, names), null!);
        }

        internal static void ShowTrialVersionMismatchPopup(string names)
        {
            if (UIManager.Instance == null) return;
            string text = GetSafeText("trial.msg.version_mismatch",
                "Trial mod version differs for: {0}. Everyone must use the same build.");
            UIManager.Instance.GetElement<UI_MessageBoxHolder>()?.OpenYes(string.Format(text, names), null!);
        }

        private static IEnumerator SaveInitialTrialCheckpointAfterArrival()
        {
            float deadline = Time.unscaledTime + 15f;
            while (Time.unscaledTime < deadline && !AreAllPlayersInCurrentBattleFloor())
                yield return null;
            TrialController? controller = TrialController.Instance;
            if (!_trialDisconnectPaused && controller != null && !controller.IsTrialRunning &&
                controller.CurrentPhase == 1 && controller.DisplayPhase == 0 &&
                AreAllPlayersInCurrentBattleFloor())
            {
                if (!SaveTrialAutoCheckpoint(1, 0))
                    Debug.LogError("[시련] 첫 입장 체크포인트 저장을 시작하지 못했습니다.");
            }
            else if (!_trialDisconnectPaused && controller != null &&
                controller.CurrentPhase == 1 && controller.DisplayPhase == 0)
                Debug.LogError("[시련] 첫 입장 체크포인트 저장 대기 시간 초과: 플레이어가 전투 층에 모두 도착하지 않았습니다.");
        }
    }
}
