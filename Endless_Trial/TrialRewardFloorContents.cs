using Mirror;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace SephiriaTrial
{
    // Server-side identity for a potion box. BreakableProp destroys its GameObject
    // shortly after opening, so the save also keeps entries for missing boxes.
    internal sealed class TrialPotionBoxMarker : MonoBehaviour
    {
        internal int phase;
        internal int slot;
    }

    public partial class EndlessMod
    {
        private const string TrialRewardFloorContentsKey = "EndlessTrialRewardFloorContents";

        private sealed class TrialPotionBoxState
        {
            public int slot;
            public float x;
            public float y;
            public float z;
            public int randomId;
            public bool broken;
        }

        private sealed class TrialGroundItemState
        {
            public int instanceId;
            public int entityId;
            public sbyte quantity;
            public float x;
            public float y;
            public float z;
            public bool isBound;
            public string ownerGuid = string.Empty;
            public int ownerPlayerIndex = -1;
        }

        private sealed class TrialRewardFloorContentsState
        {
            public int phase;
            public List<TrialPotionBoxState> boxes = new List<TrialPotionBoxState>();
            public List<TrialGroundItemState> items = new List<TrialGroundItemState>();
        }

        private static readonly HashSet<int> PendingTrialRewardItemIds = new HashSet<int>();
        private static int _pendingTrialRewardItemPhase;

        private static void ClearTrialRewardFloorContentsTracking()
        {
            PendingTrialRewardItemIds.Clear();
            _pendingTrialRewardItemPhase = 0;
        }

        private static TrialRewardFloorContentsState? ReadTrialRewardFloorContents(int phase)
        {
            string json = SaveManager.CurrentRun?.GetString(TrialRewardFloorContentsKey, string.Empty) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                TrialRewardFloorContentsState? state = JsonConvert.DeserializeObject<TrialRewardFloorContentsState>(json);
                return state?.phase == phase ? state : null;
            }
            catch (Exception exception)
            {
                Debug.LogError("[시련] 보상 공간 물품 저장 정보 읽기 실패: " + exception);
                return null;
            }
        }

        private static bool IsInsideTrialFloor(FloorGenerator floor, Vector3 position)
        {
            Vector2 center = (Vector2)floor.transform.position + floor.Center;
            Vector2 halfSize = floor.Size * 0.5f;
            return Mathf.Abs(position.x - center.x) <= halfSize.x &&
                   Mathf.Abs(position.y - center.y) <= halfSize.y;
        }

        private static List<NetworkIdentity> FindTrialRewardFloorItems(FloorGenerator floor)
        {
            var items = new List<NetworkIdentity>();
            foreach (NetworkIdentity identity in NetworkServer.spawned.Values)
                if (identity != null && identity.TryGetComponent<LootableItem>(out _) &&
                    IsInsideTrialFloor(floor, identity.transform.position))
                    items.Add(identity);
            return items;
        }

        private static void CaptureTrialRewardFloorContents(bool initializingRoom = false)
        {
            if (!NetworkServer.active || SaveManager.CurrentRun == null) return;
            int phase = SaveManager.CurrentRun.GetInt(TrialRewardIssuedPhaseKey, 0);
            if (phase <= 0) return;
            FloorGenerator? floor = FindLoadedFloor(GetRewardFloorGuidForPhase(phase));
            if (floor == null || !PreparedRewardRoomPhases.TryGetValue(floor.guid, out int preparedPhase) ||
                preparedPhase != phase || !PreparedRewardRoomFloorIds.TryGetValue(floor.guid, out int floorId) ||
                floorId != floor.GetInstanceID()) return;
            // Battle setup destroys reward props after everyone leaves. The last
            // player leaving already captured the room; do not treat that cleanup
            // as boxes being opened when a later checkpoint is written.
            if (!initializingRoom && !PlayerSpawner.MultiplayerList.Any(player =>
                    player?.PlayerAvatar != null && player.PlayerAvatar.currentFloorGuid == floor.guid))
                return;

            TrialRewardFloorContentsState state = ReadTrialRewardFloorContents(phase) ??
                new TrialRewardFloorContentsState { phase = phase };
            state.boxes ??= new List<TrialPotionBoxState>();
            state.items ??= new List<TrialGroundItemState>();
            foreach (TrialPotionBoxState box in state.boxes)
                box.broken = true;
            foreach (GameObject reward in SpawnedRewards)
            {
                if (reward == null) continue;
                TrialPotionBoxMarker? marker = reward.GetComponent<TrialPotionBoxMarker>();
                BreakableProp? prop = reward.GetComponent<BreakableProp>();
                if (marker == null || marker.phase != phase || prop == null) continue;
                TrialPotionBoxState? box = state.boxes.FirstOrDefault(entry => entry.slot == marker.slot);
                if (box == null)
                {
                    box = new TrialPotionBoxState { slot = marker.slot };
                    state.boxes.Add(box);
                }
                box.x = reward.transform.position.x;
                box.y = reward.transform.position.y;
                box.z = reward.transform.position.z;
                box.randomId = prop.RandomID;
                box.broken = prop.IsBroken;
            }

            List<TrialGroundItemState> pendingItems = _pendingTrialRewardItemPhase == phase
                ? state.items.Where(item => PendingTrialRewardItemIds.Contains(item.instanceId)).ToList()
                : new List<TrialGroundItemState>();
            state.items.Clear();
            foreach (NetworkIdentity identity in FindTrialRewardFloorItems(floor))
            {
                Item? item = identity.GetComponent<Item>();
                if (item == null || item.itemEntityID <= 0 || item.itemInstanceID < 0 || item.itemQuantity <= 0)
                    continue;
                PlayerSpawner? owner = identity.connectionToClient?.identity != null
                    ? identity.connectionToClient.identity.GetComponent<PlayerSpawner>() : null;
                int ownerIndex = owner?.currentPlayerIdx ?? -1;
                if (item.isBound && owner == null && DungeonManager.Instance != null &&
                    int.TryParse(DungeonManager.Instance.GetGlobalItemStatValue(item.itemInstanceID, "Bound"),
                        out int savedOwnerIndex))
                {
                    ownerIndex = savedOwnerIndex;
                    owner = PlayerSpawner.MultiplayerList.FirstOrDefault(player =>
                        player != null && player.currentPlayerIdx == savedOwnerIndex);
                }
                Vector3 position = identity.transform.position;
                state.items.Add(new TrialGroundItemState
                {
                    instanceId = item.itemInstanceID,
                    entityId = item.itemEntityID,
                    quantity = item.itemQuantity,
                    x = position.x,
                    y = position.y,
                    z = position.z,
                    isBound = item.isBound,
                    ownerGuid = owner?.playerGuid ?? string.Empty,
                    ownerPlayerIndex = ownerIndex
                });
                RegisterTrialFloorObject(floor, identity.gameObject);
            }
            foreach (TrialGroundItemState pending in pendingItems)
                if (!state.items.Any(item => item.instanceId == pending.instanceId))
                    state.items.Add(pending);
            SaveManager.CurrentRun.SetString(TrialRewardFloorContentsKey, JsonConvert.SerializeObject(state));
        }

        private static void ClearTrialRewardFloorItems(FloorGenerator floor)
        {
            foreach (NetworkIdentity identity in FindTrialRewardFloorItems(floor))
            {
                UnregisterTrialFloorObject(identity.gameObject);
                NetworkServer.Destroy(identity.gameObject);
            }
        }

        private static void RestoreTrialRewardFloorContents(int phase, FloorGenerator floor)
        {
            TrialRewardFloorContentsState? state = ReadTrialRewardFloorContents(phase);
            if (!NetworkServer.active || state == null) return; // Legacy saves did not record box contents.
            if (_pendingTrialRewardItemPhase != phase)
            {
                PendingTrialRewardItemIds.Clear();
                _pendingTrialRewardItemPhase = phase;
            }
            var liveBoxSlots = new HashSet<int>(SpawnedRewards
                .Where(reward => reward != null)
                .Select(reward => reward.GetComponent<TrialPotionBoxMarker>())
                .Where(marker => marker != null && marker.phase == phase)
                .Select(marker => marker!.slot));
            foreach (TrialPotionBoxState box in state.boxes ?? new List<TrialPotionBoxState>())
                if (!box.broken && liveBoxSlots.Add(box.slot))
                    CreateCustomRewardBox("RewardBox_MP", new Vector3(box.x, box.y, box.z),
                        TrialPotionRewardItemIds, floor, phase, box.slot, box.randomId);

            GameObject? prefab = Resources.Load<GameObject>("LootableItem");
            if (prefab == null) return;
            var existingIds = new HashSet<int>(FindTrialRewardFloorItems(floor)
                .Select(identity => identity.GetComponent<Item>())
                .Where(item => item != null).Select(item => item!.itemInstanceID));
            foreach (TrialGroundItemState saved in state.items ?? new List<TrialGroundItemState>())
            {
                if (saved.instanceId < 0 || saved.entityId <= 0 || saved.quantity <= 0 ||
                    !existingIds.Add(saved.instanceId) || ItemDatabase.FindItemById(saved.entityId) == null)
                    continue;
                PlayerSpawner? owner = null;
                if (saved.isBound)
                {
                    owner = PlayerSpawner.MultiplayerList.FirstOrDefault(player => player != null &&
                        (!string.IsNullOrEmpty(saved.ownerGuid) ? player.playerGuid == saved.ownerGuid :
                         saved.ownerPlayerIndex >= 0 && player.currentPlayerIdx == saved.ownerPlayerIndex));
                    if (owner == null)
                    {
                        PendingTrialRewardItemIds.Add(saved.instanceId);
                        Debug.LogWarning($"[시련] 귀속 아이템의 소유자를 찾지 못해 복원을 보류합니다: {saved.instanceId}");
                        continue;
                    }
                }
                GameObject obj = UnityEngine.Object.Instantiate(prefab,
                    new Vector3(saved.x, saved.y, saved.z), Quaternion.identity);
                Item item = obj.GetComponent<Item>();
                item.Initialize(saved.instanceId, saved.entityId, saved.quantity);
                item.NetworkisBound = saved.isBound;
                if (owner != null) NetworkServer.Spawn(obj, owner.gameObject);
                else NetworkServer.Spawn(obj);
                RegisterTrialFloorObject(floor, obj);
                PendingTrialRewardItemIds.Remove(saved.instanceId);
            }
        }
    }
}
