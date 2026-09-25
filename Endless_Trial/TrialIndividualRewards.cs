using Mirror;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace SephiriaTrial
{
    // Host-only metadata for the native reward prop. The networked component
    // and its original interaction code remain unchanged.
    internal sealed class TrialIndividualRewardSource : MonoBehaviour
    {
        internal int phase;
        internal string propId = string.Empty;
        internal int randomId;
        internal AltarOfTablet? cachedTablet;
    }

    internal sealed class TrialSephiriteClaimObserver : MonoBehaviour
    {
        internal int phase;
        internal string propId = string.Empty;
        internal string playerGuid = string.Empty;

        private void OnDestroy()
        {
            Sephirite? reward = GetComponent<Sephirite>();
            if (NetworkServer.active && reward != null && reward.isAcquired)
                EndlessMod.RecordTrialIndividualRewardClaimOnServer(phase, propId, playerGuid);
        }
    }

    public partial class EndlessMod
    {
        private sealed class TrialIndividualRewardSourceState
        {
            public string propId = string.Empty;
            public float x;
            public float y;
            public float z;
            public int randomId;
            public List<string> acquiredHashes = new List<string>();
            public List<string> enhancedHashes = new List<string>();
            public List<string> usedGuids = new List<string>();
            public Dictionary<string, int> remainingByGuid = new Dictionary<string, int>();
            public Dictionary<string, int> mysticPotUsesByGuid = new Dictionary<string, int>();
            public List<string> claimedGuids = new List<string>();
        }

        private sealed class TrialIndividualRewardRoomState
        {
            public int phase;
            public List<TrialIndividualRewardSourceState> sources = new List<TrialIndividualRewardSourceState>();
        }

        private static readonly FieldInfo? TrialShopAcquiredField = typeof(InventoryShop).GetField("acquired", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo? TrialObeliskUsedField = typeof(Obelisk).GetField("used", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo? TrialMaxHpUsedField = typeof(MaxHPDispenser).GetField("usedGuids", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo? TrialAltarRemainingField = typeof(AltarOfEnchant).GetField("remainingByGuid", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo? TrialTabletUsedField = typeof(AltarOfTablet).GetField("usedKeys", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo? TrialSephiriteServedField = typeof(SephiriteSpawner).GetField("servedPlayerRandomIDs", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo? TrialMysticPotUsedField = typeof(MysticPot).GetField("usedCount", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly Dictionary<int, TrialIndividualRewardRoomState> TrialLocalRewardRooms = new Dictionary<int, TrialIndividualRewardRoomState>();
        private static readonly HashSet<string> TrialLocalReportedClaims = new HashSet<string>();
        private static readonly Dictionary<int, float> TrialLocalMysticPotRetryAt = new Dictionary<int, float>();
        private static InventoryShop? _trialLocalRewardShop;
        private static Obelisk? _trialLocalRewardObelisk;
        private static MysticPot? _trialLocalRewardMysticPot;
        private static int _trialLocalRewardPhase;
        private static float _nextTrialLocalRewardSearch;
        private static float _nextTrialSephiriteRewardRefresh;

        private static bool IsTrialIndividualRewardSource(string propId) =>
            propId == "InventoryShop" || propId == "InventoryOrb" || propId == "Anvil" ||
            propId == "MaxHPDispenser" || propId == "Obelisk" ||
            propId == "MysticPot" || propId == "MiracleSelector" ||
            propId.StartsWith("AltarOfEnchant", StringComparison.Ordinal) ||
            propId.StartsWith("SephiriteSpawner", StringComparison.Ordinal);

        private static void RegisterTrialIndividualRewardSource(GameObject obj, int phase, string propId, int randomId)
        {
            if (obj == null || !IsTrialIndividualRewardSource(propId)) return;
            TrialIndividualRewardSource marker = obj.GetComponent<TrialIndividualRewardSource>() ??
                obj.AddComponent<TrialIndividualRewardSource>();
            marker.phase = phase;
            marker.propId = propId;
            marker.randomId = randomId;
        }

        private static TrialIndividualRewardRoomState? ReadTrialIndividualRewardRoomState(int phase)
        {
            string json = SaveManager.CurrentRun?.GetString(TrialIndividualRewardRoomStateKey, string.Empty) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                TrialIndividualRewardRoomState? state = JsonConvert.DeserializeObject<TrialIndividualRewardRoomState>(json);
                return state?.phase == phase ? state : null;
            }
            catch (Exception exception)
            {
                Debug.LogError("[시련] 개인 보상 저장 정보 읽기 실패: " + exception);
                return null;
            }
        }

        private static void WriteTrialIndividualRewardRoomState(TrialIndividualRewardRoomState state)
        {
            SaveManager.CurrentRun?.SetString(TrialIndividualRewardRoomStateKey, JsonConvert.SerializeObject(state));
        }

        private static void BeginTrialIndividualRewardRoomState(int phase)
        {
            if (NetworkServer.active && SaveManager.CurrentRun != null)
                WriteTrialIndividualRewardRoomState(new TrialIndividualRewardRoomState { phase = phase });
        }

        private static void CaptureTrialIndividualRewardRoomState()
        {
            if (!NetworkServer.active || SaveManager.CurrentRun == null) return;
            int phase = SaveManager.CurrentRun.GetInt(TrialRewardIssuedPhaseKey, 0);
            TrialIndividualRewardRoomState? state = ReadTrialIndividualRewardRoomState(phase);
            if (state == null) return;

            GameObject[] objects = SpawnedRewards.Concat(ActiveTrialMerchants)
                .Where(obj => obj != null && obj.GetComponent<TrialIndividualRewardSource>()?.phase == phase)
                .Distinct().ToArray();
            if (objects.Length == 0) return;

            foreach (GameObject obj in objects)
            {
                TrialIndividualRewardSource marker = obj.GetComponent<TrialIndividualRewardSource>();
                TrialIndividualRewardSourceState? source = state.sources.FirstOrDefault(item => item.propId == marker.propId);
                if (source == null)
                {
                    source = new TrialIndividualRewardSourceState { propId = marker.propId };
                    state.sources.Add(source);
                }
                source.x = obj.transform.position.x;
                source.y = obj.transform.position.y;
                source.z = obj.transform.position.z;
                source.randomId = marker.randomId;

                InventoryOrb? orb = obj.GetComponent<InventoryOrb>();
                if (orb != null) source.acquiredHashes = orb.acquiredHashes.ToList();
                MiracleSelector2? selector = obj.GetComponent<MiracleSelector2>();
                if (selector != null) source.acquiredHashes = selector.acquiredHashes.ToList();
                Anvil? anvil = obj.GetComponent<Anvil>();
                if (anvil != null) source.enhancedHashes = anvil.enhancedGuidHashes.ToList();
                MaxHPDispenser? dispenser = obj.GetComponent<MaxHPDispenser>();
                if (dispenser != null && TrialMaxHpUsedField?.GetValue(dispenser) is HashSet<string> used)
                    source.usedGuids = used.ToList();
                AltarOfEnchant? altar = obj.GetComponent<AltarOfEnchant>();
                if (altar != null && TrialAltarRemainingField?.GetValue(altar) is Dictionary<string, int> remaining)
                    source.remainingByGuid = new Dictionary<string, int>(remaining);
                if (obj.GetComponent<SephiriteSpawner>() != null &&
                    TryFindTrialTabletForSource(obj, marker.randomId) is AltarOfTablet tablet &&
                    TrialTabletUsedField?.GetValue(tablet) is HashSet<string> tabletUsers)
                    source.usedGuids = tabletUsers.ToList();
            }
            WriteTrialIndividualRewardRoomState(state);
        }

        private static void ApplyTrialIndividualRewardSourceState(GameObject obj, TrialIndividualRewardSourceState state)
        {
            InventoryOrb? orb = obj.GetComponent<InventoryOrb>();
            if (orb != null)
                foreach (string hash in state.acquiredHashes ?? new List<string>())
                    if (!orb.acquiredHashes.Contains(hash)) orb.acquiredHashes.Add(hash);

            MiracleSelector2? selector = obj.GetComponent<MiracleSelector2>();
            if (selector != null)
                foreach (string hash in state.acquiredHashes ?? new List<string>())
                    if (!selector.acquiredHashes.Contains(hash)) selector.acquiredHashes.Add(hash);

            Anvil? anvil = obj.GetComponent<Anvil>();
            if (anvil != null)
                foreach (string hash in state.enhancedHashes ?? new List<string>())
                    if (!anvil.enhancedGuidHashes.Contains(hash)) anvil.enhancedGuidHashes.Add(hash);

            MaxHPDispenser? dispenser = obj.GetComponent<MaxHPDispenser>();
            if (dispenser != null && TrialMaxHpUsedField?.GetValue(dispenser) is HashSet<string> used)
                foreach (string guid in state.usedGuids ?? new List<string>()) used.Add(guid);

            AltarOfEnchant? altar = obj.GetComponent<AltarOfEnchant>();
            if (altar != null && TrialAltarRemainingField?.GetValue(altar) is Dictionary<string, int> remaining)
                foreach (KeyValuePair<string, int> entry in state.remainingByGuid ?? new Dictionary<string, int>())
                    remaining[entry.Key] = entry.Value;

            SephiriteSpawner? spawner = obj.GetComponent<SephiriteSpawner>();
            if (spawner != null && TrialSephiriteServedField?.GetValue(spawner) is HashSet<int> served)
                foreach (PlayerSpawner player in PlayerSpawner.MultiplayerList)
                    if (player != null && player.PlayerAvatar != null &&
                        (state.claimedGuids ?? new List<string>()).Contains(player.playerGuid))
                        served.Add(player.PlayerAvatar.RandomID);
        }

        private static AltarOfTablet? TryFindTrialTabletForSource(GameObject source, int randomId)
        {
            TrialIndividualRewardSource? marker = source.GetComponent<TrialIndividualRewardSource>();
            if (marker?.cachedTablet != null && marker.cachedTablet.RandomID == randomId)
                return marker.cachedTablet;
            foreach (AltarOfTablet tablet in UnityEngine.Object.FindObjectsByType<AltarOfTablet>(FindObjectsSortMode.None))
            {
                if (tablet == null || tablet.RandomID != randomId ||
                    (tablet.transform.position - source.transform.position).sqrMagnitude >= 1f)
                    continue;
                if (marker != null) marker.cachedTablet = tablet;
                return tablet;
            }
            return null;
        }

        private static void RestoreTrialTabletClaimState(GameObject source, TrialIndividualRewardSourceState state)
        {
            if (TrialTabletUsedField == null || source.GetComponent<SephiriteSpawner>() == null) return;
            AltarOfTablet? tablet = TryFindTrialTabletForSource(source, state.randomId);
            if (tablet == null || !(TrialTabletUsedField.GetValue(tablet) is HashSet<string> usedKeys)) return;
            foreach (string guid in state.usedGuids ?? new List<string>()) usedKeys.Add(guid);
        }

        private static void RestoreTrialIndividualRewardRoomState(int phase, FloorGenerator floor)
        {
            TrialIndividualRewardRoomState? state = ReadTrialIndividualRewardRoomState(phase);
            if (state == null) return; // A legacy save has no per-player claim history.
            foreach (TrialIndividualRewardSourceState source in state.sources ?? new List<TrialIndividualRewardSourceState>())
            {
                if (!IsTrialIndividualRewardSource(source.propId)) continue;
                Vector3 position = new Vector3(source.x, source.y, source.z);
                if (source.propId == "InventoryShop")
                    SpawnTrialSupplyTerminalRestored(position, phase, floor, source);
                else if (source.propId == "Anvil")
                    SpawnNativeTrialAnvil(position, floor, phase, source);
                else
                    SpawnFromDatabase(source.propId, position, floor, phase, source);
            }
        }

        private static void ObserveTrialSephiriteRewards(GameObject source, int phase, string propId)
        {
            SephiriteSpawner? spawner = source.GetComponent<SephiriteSpawner>();
            if (spawner == null) return;
            TrialIndividualRewardSourceState? savedSource = ReadTrialIndividualRewardRoomState(phase)?.sources?
                .FirstOrDefault(item => item.propId == propId);
            foreach (PlayerSpawner player in PlayerSpawner.MultiplayerList)
            {
                if (player == null || player.PlayerAvatar == null || string.IsNullOrEmpty(player.playerGuid)) continue;
                bool alreadyClaimed = savedSource?.claimedGuids?.Contains(player.playerGuid) == true;
                if (alreadyClaimed && TrialSephiriteServedField?.GetValue(spawner) is HashSet<int> served)
                    served.Add(player.PlayerAvatar.RandomID);
                int seed = unchecked(spawner.RandomID + player.PlayerAvatar.RandomID);
                List<GameObject> duplicateRewards = new List<GameObject>();
                foreach (NetworkIdentity identity in NetworkServer.spawned.Values)
                {
                    if (identity == null || identity.connectionToClient != player.connectionToClient) continue;
                    Sephirite? sephirite = identity.GetComponent<Sephirite>();
                    if (sephirite == null || sephirite.CurrentSeed != seed ||
                        Vector3.Distance(identity.transform.position, source.transform.position) > 5f) continue;
                    if (alreadyClaimed)
                    {
                        duplicateRewards.Add(identity.gameObject);
                        continue;
                    }
                    TrialSephiriteClaimObserver observer = identity.GetComponent<TrialSephiriteClaimObserver>() ??
                        identity.gameObject.AddComponent<TrialSephiriteClaimObserver>();
                    observer.phase = phase;
                    observer.propId = propId;
                    observer.playerGuid = player.playerGuid;
                }
                foreach (GameObject duplicate in duplicateRewards)
                    if (duplicate != null) NetworkServer.Destroy(duplicate);
            }
        }

        private static void RefreshTrialSephiriteRewardObservers()
        {
            if (!NetworkServer.active || Time.unscaledTime < _nextTrialSephiriteRewardRefresh) return;
            _nextTrialSephiriteRewardRefresh = Time.unscaledTime + 1f;
            foreach (GameObject reward in SpawnedRewards)
            {
                TrialIndividualRewardSource? marker = reward != null
                    ? reward.GetComponent<TrialIndividualRewardSource>() : null;
                if (reward != null && marker != null && reward.GetComponent<SephiriteSpawner>() != null)
                {
                    TrialIndividualRewardSourceState? savedSource =
                        ReadTrialIndividualRewardRoomState(marker.phase)?.sources?
                            .FirstOrDefault(source => source.propId == marker.propId);
                    if (savedSource != null)
                        RestoreTrialTabletClaimState(reward, savedSource);
                    ObserveTrialSephiriteRewards(reward, marker.phase, marker.propId);
                }
            }
        }

        internal static void RecordTrialIndividualRewardClaimOnServer(int phase, string propId, string playerGuid)
        {
            if (!NetworkServer.active || string.IsNullOrWhiteSpace(playerGuid) ||
                !ActiveTrialPartyGuids.Contains(playerGuid)) return;
            TrialIndividualRewardRoomState? state = ReadTrialIndividualRewardRoomState(phase);
            TrialIndividualRewardSourceState? source = state?.sources?.FirstOrDefault(item => item.propId == propId);
            if (state == null || source == null || !IsTrialIndividualRewardSource(propId)) return;
            if (source.claimedGuids.Contains(playerGuid)) return;
            source.claimedGuids.Add(playerGuid);
            WriteTrialIndividualRewardRoomState(state);
            BroadcastTrialIndividualRewardClaims(phase);
        }

        internal static void RecordTrialIndividualRewardClaimFromConnection(int phase, string propId,
            NetworkConnectionToClient? sender)
        {
            if (!NetworkServer.active || sender?.identity == null ||
                (propId != "InventoryShop" && propId != "Obelisk")) return;
            PlayerAvatar? avatar = sender.identity.GetComponent<PlayerAvatar>();
            PlayerSpawner? player = sender.identity.GetComponent<PlayerSpawner>();
            TrialIndividualRewardRoomState? state = ReadTrialIndividualRewardRoomState(phase);
            TrialIndividualRewardSourceState? source = state?.sources?.FirstOrDefault(item => item.propId == propId);
            if (avatar == null || player == null || source == null ||
                avatar.currentFloorGuid != GetRewardFloorGuidForPhase(phase) ||
                Vector3.Distance(avatar.transform.position, new Vector3(source.x, source.y, source.z)) > 6f)
                return;
            RecordTrialIndividualRewardClaimOnServer(phase, propId, player.playerGuid);
        }

        internal static void RecordTrialMysticPotUsesFromConnection(int phase, int usedCount,
            NetworkConnectionToClient? sender)
        {
            if (!NetworkServer.active || sender?.identity == null) return;
            PlayerAvatar? avatar = sender.identity.GetComponent<PlayerAvatar>();
            PlayerSpawner? player = sender.identity.GetComponent<PlayerSpawner>();
            TrialIndividualRewardRoomState? state = ReadTrialIndividualRewardRoomState(phase);
            TrialIndividualRewardSourceState? source = state?.sources?.FirstOrDefault(item => item.propId == "MysticPot");
            int maxUses = KeywordDatabase.GetConstValue("mysticPotMaxUseCount");
            if (avatar == null || player == null || source == null ||
                !ActiveTrialPartyGuids.Contains(player.playerGuid) || usedCount < 0 || usedCount > maxUses ||
                avatar.currentFloorGuid != GetRewardFloorGuidForPhase(phase) ||
                Vector3.Distance(avatar.transform.position, new Vector3(source.x, source.y, source.z)) > 40f)
                return;

            source.mysticPotUsesByGuid ??= new Dictionary<string, int>();
            if (source.mysticPotUsesByGuid.TryGetValue(player.playerGuid, out int savedUses) && usedCount <= savedUses)
                return;
            source.mysticPotUsesByGuid[player.playerGuid] = usedCount;
            WriteTrialIndividualRewardRoomState(state!);
            BroadcastTrialIndividualRewardClaims(phase);
        }

        private static void BroadcastTrialIndividualRewardClaims(int phase)
        {
            if (!NetworkServer.active || TrialController.Instance == null) return;
            TrialIndividualRewardRoomState? state = ReadTrialIndividualRewardRoomState(phase);
            if (state != null)
                TrialController.Instance.RpcSyncTrialIndividualRewardClaims(phase, JsonConvert.SerializeObject(state));
        }

        internal static void ReceiveTrialIndividualRewardClaims(int phase, string json)
        {
            try
            {
                TrialIndividualRewardRoomState? state = JsonConvert.DeserializeObject<TrialIndividualRewardRoomState>(json);
                if (state != null && state.phase == phase) TrialLocalRewardRooms[phase] = state;
            }
            catch (Exception exception)
            {
                Debug.LogError("[시련] 개인 보상 동기화 읽기 실패: " + exception);
            }
        }

        internal static void ClearTrialLocalRewardClaims()
        {
            TrialLocalRewardRooms.Clear();
            TrialLocalReportedClaims.Clear();
            TrialLocalMysticPotRetryAt.Clear();
            _trialLocalRewardShop = null;
            _trialLocalRewardObelisk = null;
            _trialLocalRewardMysticPot = null;
            _trialLocalRewardPhase = 0;
            _nextTrialLocalRewardSearch = 0f;
            _nextTrialSephiriteRewardRefresh = 0f;
        }

        private static void UpdateTrialLocalRewardClaims()
        {
            if (!NetworkClient.active) return;
            PlayerAvatar? local = _cachedLocalPlayer;
            if (local == null || !local.isLocalPlayer || !IsTrialRewardFloorGuid(local.currentFloorGuid))
            {
                _trialLocalRewardShop = null;
                _trialLocalRewardObelisk = null;
                _trialLocalRewardMysticPot = null;
                return;
            }
            int phase = Mathf.Max(1, (TrialController.Instance?.CurrentPhase ?? 1) - 1);
            if (_trialLocalRewardPhase != phase)
            {
                _trialLocalRewardPhase = phase;
                _trialLocalRewardShop = null;
                _trialLocalRewardObelisk = null;
                _trialLocalRewardMysticPot = null;
            }
            if (Time.unscaledTime >= _nextTrialLocalRewardSearch)
            {
                _nextTrialLocalRewardSearch = Time.unscaledTime + 1f;
                Vector3 localPosition = local.transform.position;
                if (_trialLocalRewardShop == null)
                    foreach (InventoryShop shop in UnityEngine.Object.FindObjectsByType<InventoryShop>(FindObjectsSortMode.None))
                        if (shop != null && (shop.transform.position - localPosition).sqrMagnitude < 1600f)
                        {
                            _trialLocalRewardShop = shop;
                            break;
                        }
                if (_trialLocalRewardObelisk == null)
                    foreach (Obelisk obelisk in UnityEngine.Object.FindObjectsByType<Obelisk>(FindObjectsSortMode.None))
                        if (obelisk != null && (obelisk.transform.position - localPosition).sqrMagnitude < 1600f)
                        {
                            _trialLocalRewardObelisk = obelisk;
                            break;
                        }
                if (_trialLocalRewardMysticPot == null)
                    foreach (MysticPot pot in UnityEngine.Object.FindObjectsByType<MysticPot>(FindObjectsSortMode.None))
                        if (pot != null && (pot.transform.position - localPosition).sqrMagnitude < 1600f)
                        {
                            _trialLocalRewardMysticPot = pot;
                            break;
                        }
            }
            string guid = HorayNetworkAuthenticator.GetMyPlayerGuid();
            if (string.IsNullOrEmpty(guid)) return;
            ApplyOrReportTrialLocalRewardClaim(phase, "InventoryShop", _trialLocalRewardShop,
                TrialShopAcquiredField, guid);
            ApplyOrReportTrialLocalRewardClaim(phase, "Obelisk", _trialLocalRewardObelisk,
                TrialObeliskUsedField, guid);
            ApplyOrReportTrialLocalMysticPotUses(phase, _trialLocalRewardMysticPot, guid);
        }

        private static void ApplyOrReportTrialLocalMysticPotUses(int phase, MysticPot? pot, string guid)
        {
            if (pot == null || TrialMysticPotUsedField == null) return;
            int usedCount = (int)(TrialMysticPotUsedField.GetValue(pot) ?? 0);
            int savedCount = 0;
            if (TrialLocalRewardRooms.TryGetValue(phase, out TrialIndividualRewardRoomState? room))
            {
                TrialIndividualRewardSourceState? source = null;
                if (room.sources != null)
                    foreach (TrialIndividualRewardSourceState candidate in room.sources)
                        if (candidate.propId == "MysticPot")
                        {
                            source = candidate;
                            break;
                        }
                if (source?.mysticPotUsesByGuid != null)
                    source.mysticPotUsesByGuid.TryGetValue(guid, out savedCount);
            }
            if (savedCount > usedCount)
            {
                TrialMysticPotUsedField.SetValue(pot, savedCount);
                if (savedCount >= KeywordDatabase.GetConstValue("mysticPotMaxUseCount"))
                {
                    if (pot.lightObject != null) pot.lightObject.SetActive(false);
                    if (pot.BrokenObject != null) pot.BrokenObject.SetActive(true);
                }
                return;
            }
            if (usedCount <= savedCount ||
                (TrialLocalMysticPotRetryAt.TryGetValue(phase, out float retryAt) && Time.unscaledTime < retryAt)) return;
            TrialLocalMysticPotRetryAt[phase] = Time.unscaledTime + 0.5f;
            if (NetworkServer.active)
            {
                NetworkConnectionToClient? connection = NetworkServer.localConnection;
                RecordTrialMysticPotUsesFromConnection(phase, usedCount, connection);
            }
            else
                TrialController.Instance?.CmdRecordTrialMysticPotUses(phase, usedCount);
        }

        private static void ApplyOrReportTrialLocalRewardClaim(int phase, string propId, Component? component,
            FieldInfo? usedField, string guid)
        {
            if (component == null || usedField == null) return;
            bool claimed = false;
            if (TrialLocalRewardRooms.TryGetValue(phase, out TrialIndividualRewardRoomState? state) &&
                state.sources != null)
                foreach (TrialIndividualRewardSourceState source in state.sources)
                    if (source.propId == propId && source.claimedGuids?.Contains(guid) == true)
                    {
                        claimed = true;
                        break;
                    }
            if (claimed)
            {
                usedField.SetValue(component, true);
                if (component is InventoryShop shop)
                {
                    if (shop.interactable != null) shop.interactable.enabled = false;
                    if (shop.orbImage != null) shop.orbImage.SetActive(false);
                }
                return;
            }
            if (!(usedField.GetValue(component) is bool used) || !used) return;
            string key = phase + ":" + propId;
            if (!TrialLocalReportedClaims.Add(key)) return;
            if (NetworkServer.active)
                RecordTrialIndividualRewardClaimOnServer(phase, propId, guid);
            else
                TrialController.Instance?.CmdRecordTrialIndividualRewardClaim(phase, propId);
        }
    }
}
