#pragma warning disable CS8618 
#pragma warning disable CS8604 

using Mirror;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace SephiriaTrial
{
    public class TrialController : NetworkBehaviour
    {
        public static TrialController? Instance;

        private static FieldInfo? cachedFInteractionDescription;
        private static bool isInteractFieldsCached = false;

        public int DisplayPhase = 0;
        public int CurrentPhase = 1;
        private int aliveMonsterCount;
        private bool isTrialRunning;
        private bool localBattleUiClosedForCurrentLife;
        private readonly HashSet<int> deadMonsterIds = new HashSet<int>();
        // RandomEnemyPhaseSpawner.MultiplayerLimit in the original game.
        private static readonly int[] ConcurrentMonsterLimits = { 53, 53, 63, 73, 73, 83, 93, 103, 113 };
        public bool IsTrialRunning => isTrialRunning;
        public int AliveMonsterCount => aliveMonsterCount;

        // The mod DLL is built outside Unity's Mirror Weaver. Serialize the
        // controller state explicitly; [SyncVar] and [ClientRpc] attributes on
        // this type do not generate network code in the shipped DLL.
        public override void OnSerialize(NetworkWriter writer, bool initialState)
        {
            writer.WriteInt(DisplayPhase);
            writer.WriteInt(CurrentPhase);
            writer.WriteBool(isTrialRunning);
        }

        public override void OnDeserialize(NetworkReader reader, bool initialState)
        {
            int displayPhase = reader.ReadInt();
            int currentPhase = reader.ReadInt();
            bool running = reader.ReadBool();
            bool displayChanged = initialState || DisplayPhase != displayPhase;
            bool runningChanged = initialState || isTrialRunning != running;
            DisplayPhase = displayPhase;
            CurrentPhase = currentPhase;
            isTrialRunning = running;
            if (displayChanged && EndlessMod.IsLocalPlayerInTrialFloor())
                UpdateLocalUI(displayPhase);
            if (runningChanged) RefreshLocalTrialState();
        }

        [Server]
        private void SetTrialRunning(bool running)
        {
            if (isTrialRunning == running) return;
            isTrialRunning = running;
            SetDirty();
            RefreshLocalTrialState();
        }

        internal void RefreshLocalTrialState()
        {
            if (!EndlessMod.IsLocalPlayerInTrialFloor())
            {
                localBattleUiClosedForCurrentLife = false;
                return;
            }
            if (isTrialRunning) EndlessMod.Instance?.HideTrialTablet();
            else EndlessMod.Instance?.ShowTrialTablet();

            PlayerAvatar? local = NetworkClient.localPlayer?.GetComponent<PlayerAvatar>();
            if (!isTrialRunning || local == null || local.IsDead ||
                !EndlessMod.IsTrialBattleFloorGuid(local.currentFloorGuid))
            {
                localBattleUiClosedForCurrentLife = false;
                return;
            }

            UIManager? ui = UIManager.Instance;
            PlayerSpawner? spawner = local.GetComponent<PlayerSpawner>();
            if (ui == null || spawner == null) return;
            bool inventoryOrRewardOpen =
                ui.GetElement<UI_CharacterStatusPanel>()?.IsOpened == true ||
                ui.GetElement<UI_SephiriteRewardPanel>()?.IsOpened == true;
            if (!localBattleUiClosedForCurrentLife || inventoryOrRewardOpen)
            {
                // The native travel flow uses this method to close the local
                // control stack. Apply it when trial combat begins, including
                // after a player is revived during an active phase.
                spawner.CloseSomeUI();
                localBattleUiClosedForCurrentLife = true;
            }
        }

        public override void OnStartServer() { Instance = this; }
        void Awake()
        {
            // The inactive runtime template exists on every peer solely so
            // Mirror can construct a real networked controller later.  It must
            // never claim the singleton slot itself.
            if (gameObject.name == "TrialController_RuntimePrefab") return;
            if (Instance == null) Instance = this!;
        }
        public override void OnStartClient()
        {
            Instance = this;
            // During MultiZone the controller is already replicated, but the
            // stage banner belongs only in the Trial floor.
            if (EndlessMod.IsLocalPlayerInTrialFloor())
            {
                UpdateLocalUI(DisplayPhase);
                RefreshLocalTrialState();
            }
        }

        internal void UpdateLocalUIFromFloorTransition()
        {
            UpdateLocalUI(DisplayPhase);
            RefreshLocalTrialState();
        }

        private void OnDestroy()
        {
            // A GameOver/New Game transition destroys the old network object.
            // Clear the static reference only when it still identifies this exact
            // instance, allowing EndlessMod to create a fresh controller later.
            if (ReferenceEquals(Instance, this))
                Instance = null;
        }

        private void UpdateLocalUI(int phase)
        {
            if (EndlessMod.Instance != null)
            {
                EndlessMod.Instance.UpdateTrialText(phase);
            }
        }

        public void RefreshTabletInteractText()
        {
            try
            {
                // 1. 석판 오브젝트 및 컴포넌트가 존재하는지 검사
                if (EndlessMod.TrialTablet == null) return;

                var interactable = EndlessMod.TrialTablet.GetComponent<Interactable>();
                if (interactable == null) return;

                // Keep the localization key: LocalizedString resolves the
                // selected language when the interaction is displayed.
                const string interactKey = "trial.tablet.interact";

                // 3. 최초 1회만 리플렉션 필드 구조를 캐싱합니다.
                if (!isInteractFieldsCached)
                {
                    Type interactType = interactable.GetType();
                    cachedFInteractionDescription = interactType.GetField("interactionDescription", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    isInteractFieldsCached = true;
                }

                // 4. 캐싱된 FieldInfo를 사용하여 순수 리플렉션으로 값을 안전하게 주입합니다.
                if (cachedFInteractionDescription != null)
                {
                    cachedFInteractionDescription.SetValue(interactable, new LocalizedString(interactKey));
                    Debug.Log($"[시련] 석판 상호작용 텍스트 새로고침 완료: {interactKey}");
                }
                else
                {
                    Debug.LogError("[시련] interactionDescription 필드를 리플렉션으로 찾을 수 없습니다.");
                }
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[시련] 석판 텍스트 갱신 중 오류 발생: {e.Message}");
            }
        }

        [Server]
        public void SetDisplayPhase(int phase)
        {
            if (DisplayPhase != phase)
            {
                DisplayPhase = phase;
                SetDirty();
            }
            UpdateLocalUI(phase);
        }

        [Server]
        public void ResetTrial(bool isGameOver)
        {
            Debug.Log($"[시련] 리셋 실행 (게임오버 여부: {isGameOver})");

            if (isGameOver)
            {
                EndlessMod.ClearTrackedTrialMonsters();
                EndlessMod.ClearTrialCheckpointOnGameOver();
                // 호스트는 RpcOnGameOver의 클라이언트 분기를 타지 않으므로 서버에서 직접 전투 상태를 해제한다.
                EndlessMod.SetAllPlayersBattleState(false);
                EndlessMod.CleanupTrialUI();

                EndlessMod.CleanupTrialMerchants();
                EndlessMod.ClearTrialSaveReturnPortal();
                EndlessMod.ClearTrialTransferPortals();

                CurrentPhase = 1;
                DisplayPhase = 0;
                SetDirty();
                SetTrialRunning(false);
                EndlessMod.Instance?.ShowTrialTablet();
                RpcOnGameOver();
            }

            aliveMonsterCount = 0;
            deadMonsterIds.Clear();
            StopAllCoroutines();
            EndlessMod.CleanupTrialRewards();
            EndlessMod.ClearTrialDummies();
            EndlessMod.ClearTrialSaveReturnPortal();
        }

        // These entry points are invoked on the server, directly for the host
        // or by TrialNetworkBridge for remote clients. This DLL has no Weaver.
        [Server]
        public void CmdStartTrial(NetworkConnectionToClient? sender = null)
        {
            StartTrialOnServer(sender);
        }

        [Server]
        public bool StartTrialFromHost()
        {
            return StartTrialOnServer(NetworkServer.localConnection);
        }

        private bool StartTrialOnServer(NetworkConnectionToClient? sender)
        {
            Debug.Log($"[시련] 단계 시작 처리: server={NetworkServer.active}, host={EndlessMod.IsTrialHost(sender)}, running={isTrialRunning}, phase={CurrentPhase}");
            if (!NetworkServer.active) return false;
            if (EndlessMod.IsTrialDisconnectPaused) return false;
            if (!EndlessMod.IsTrialHost(sender))
            {
                if (sender != null) TargetShowSystemMessage(sender, "trial.msg.host_only");
                return false;
            }
            if (isTrialRunning)
            {
                TargetShowSystemMessage(sender, "trial.msg.start_unavailable");
                return false;
            }
            if (!EndlessMod.IsTrialCheckpointReadyForNextPhase(CurrentPhase))
            {
                TargetShowSystemMessage(sender, "trial.msg.start_unavailable");
                return false;
            }
            if (!EndlessMod.IsActiveTrialPartyComplete())
            {
                TargetShowSystemMessage(sender, "trial.msg.party_mismatch");
                return false;
            }
            if (!EndlessMod.AreAllPlayersInCurrentBattleFloor())
            {
                TargetShowSystemMessage(sender, "trial.msg.all_players");
                return false;
            }

            EndlessMod.CleanupTrialMerchants();
            EndlessMod.ClearTrialDummies();
            EndlessMod.ClearTrialSaveReturnPortal();
            EndlessMod.ClearTrialTransferPortals();
            EndlessMod.ClearDeadTrialMonsters();
            StopAllCoroutines();
            SetTrialRunning(true);
            StartCoroutine(TrialRoutine());
            return true;
        }

        [Server]
        public void CmdEnterTrialSpace(int slot, NetworkConnectionToClient? sender = null)
        {
            EnterTrialSpaceOnServer(slot, sender);
        }

        // The entrance UI is opened only by the local host. Call the server
        // path with its actual connection instead of relying on a Command's
        // optional sender being populated by a separately built mod DLL.
        [Server]
        public void EnterTrialSpaceFromHost(int slot)
        {
            EnterTrialSpaceOnServer(slot, NetworkServer.localConnection);
        }

        private void EnterTrialSpaceOnServer(int slot, NetworkConnectionToClient? sender)
        {
            Debug.Log($"[시련] 슬롯 입장 처리: slot={slot}, server={NetworkServer.active}, host={EndlessMod.IsTrialHost(sender)}, running={isTrialRunning}");
            if (!NetworkServer.active) return;
            if (isTrialRunning)
            {
                Debug.LogWarning("[시련] 이미 진행 중인 시련이 있어 슬롯 입장을 중단했습니다.");
                if (sender != null) TargetShowSystemMessage(sender, "trial.msg.slot_unavailable");
                return;
            }
            if (!EndlessMod.IsTrialHost(sender))
            {
                if (sender != null) TargetShowSystemMessage(sender, "trial.msg.host_only_enter");
                return;
            }
            if (!EndlessMod.RequireCompatibleTrialParty()) return;
            if (!EndlessMod.AreAllPlayersNearTrialEntrance())
            {
                Debug.LogWarning("[시련] 슬롯 입장 중단: 참가자가 입구 포탈 범위 밖에 있습니다.");
                TargetShowSystemMessage(sender, "trial.msg.gather_entrance");
                return;
            }
            if (!EndlessMod.TrySelectTrialSlot(slot, out string errorKey))
            {
                Debug.LogWarning($"[시련] 슬롯 입장 중단: slot={slot}, reason={errorKey}");
                TargetShowSystemMessage(sender, errorKey);
                return;
            }
            RpcClearTrialIndividualRewardClaims();
            if (EndlessMod.HasSavedTrialSnapshot())
            {
                Debug.Log($"[시련] 슬롯 {slot}: 저장된 체크포인트 복원 시작");
                if (EndlessMod.RestoreSavedTrialSnapshot(this))
                {
                    RpcShowSystemMessage("trial.return.resumed", 0);
                    return;
                }
                TargetShowSystemMessage(sender, "trial.msg.restore_failed");
                return;
            }
            Debug.Log($"[시련] 슬롯 {slot}: 저장 기록 없음, 대기 단계로 새 시련 시작");
            if (!EndlessMod.MoveAllPlayersToTrialFloor())
                TargetShowSystemMessage(sender, "trial.msg.entrance_unavailable");
        }

        [Server]
        public void CmdDeleteTrialSlot(int slot, NetworkConnectionToClient? sender = null)
        {
            DeleteTrialSlotOnServer(slot, sender);
        }

        [Server]
        public bool DeleteTrialSlotFromHost(int slot)
        {
            return DeleteTrialSlotOnServer(slot, NetworkServer.localConnection);
        }

        private bool DeleteTrialSlotOnServer(int slot, NetworkConnectionToClient? sender)
        {
            Debug.Log($"[시련] 슬롯 삭제 처리: slot={slot}, server={NetworkServer.active}, host={EndlessMod.IsTrialHost(sender)}");
            if (!EndlessMod.IsTrialHost(sender))
            {
                if (sender != null) TargetShowSystemMessage(sender, "trial.msg.host_only_enter");
                return false;
            }
            if (EndlessMod.DeleteTrialSlot(slot))
            {
                TargetShowSystemMessage(sender, "trial.msg.slot_deleted");
                return true;
            }
            Debug.LogWarning($"[시련] 슬롯 {slot} 삭제 조건을 충족하지 못했습니다.");
            TargetShowSystemMessage(sender, "trial.msg.slot_unavailable");
            return false;
        }

        [Server]
        public void CmdMoveLocalPlayerToRewardFloor(NetworkConnectionToClient? sender = null)
        {
            if (EndlessMod.IsTrialDisconnectPaused) return;
            if (!NetworkServer.active || sender == null || sender.identity == null)
            {
                Debug.LogWarning("[시련] 보상 층 이동 명령을 처리할 연결 또는 플레이어가 없습니다.");
                return;
            }
            PlayerAvatar avatar = sender.identity.GetComponent<PlayerAvatar>();
            if (avatar == null || !EndlessMod.IsActorNearTrialTransferPortal(avatar, 1))
            {
                Debug.LogWarning($"[시련] 보상 층 이동 명령 발신자가 포탈 근처에 없습니다: connId={sender.connectionId}");
                return;
            }
            Debug.Log($"[시련] 보상 층 이동 명령 수신: connId={sender.connectionId}, player={avatar.name}");
            EndlessMod.MovePlayerToRewardFloor(avatar);
        }

        [Server]
        public void CmdRecordTrialIndividualRewardClaim(int phase, string propId,
            NetworkConnectionToClient? sender = null)
        {
            EndlessMod.RecordTrialIndividualRewardClaimFromConnection(phase, propId, sender);
        }

        [Server]
        public void CmdRecordTrialMysticPotUses(int phase, int usedCount,
            NetworkConnectionToClient? sender = null)
        {
            EndlessMod.RecordTrialMysticPotUsesFromConnection(phase, usedCount, sender);
        }

        public void RpcSyncTrialIndividualRewardClaims(int phase, string json)
        {
            if (!NetworkServer.active) return;
            EndlessMod.ReceiveTrialIndividualRewardClaims(phase, json);
            TrialNetworkBridge.BroadcastNotice(
                TrialNetworkBridge.RewardClaims, phase, text: json);
        }

        public void RpcClearTrialIndividualRewardClaims()
        {
            if (!NetworkServer.active) return;
            EndlessMod.ClearTrialLocalRewardClaims();
            TrialNetworkBridge.BroadcastNotice(TrialNetworkBridge.ClearRewardClaims);
        }

        [Server]
        public void CmdMoveLocalPlayerToBattleFloor(NetworkConnectionToClient? sender = null)
        {
            if (EndlessMod.IsTrialDisconnectPaused) return;
            if (!NetworkServer.active || sender == null || sender.identity == null)
            {
                Debug.LogWarning("[시련] 전투 층 이동 명령을 처리할 연결 또는 플레이어가 없습니다.");
                return;
            }
            PlayerAvatar avatar = sender.identity.GetComponent<PlayerAvatar>();
            if (avatar == null || !EndlessMod.IsActorNearTrialTransferPortal(avatar, 2))
            {
                Debug.LogWarning($"[시련] 전투 층 이동 명령 발신자가 포탈 근처에 없습니다: connId={sender.connectionId}");
                return;
            }
            Debug.Log($"[시련] 전투 층 이동 명령 수신: connId={sender.connectionId}, player={avatar.name}");
            EndlessMod.MovePlayerToBattleFloor(avatar);
        }

        private IEnumerator TrialRoutine()
        {
            SetDisplayPhase(CurrentPhase);
            EndlessMod.CleanupTrialRewards();
            EndlessMod.SetAllPlayersBattleState(true);

            RpcShowSystemMessage("trial.msg.start", CurrentPhase);
            yield return EndlessMod.ClearDroppedItemsOnBattleFloor(CurrentPhase);

            deadMonsterIds.Clear();
            aliveMonsterCount = 0;

            int spawnCount = 25 + (CurrentPhase - 1);
            float statMult = 1f + (CurrentPhase - 1) * 2f;
            int playerIndex = Mathf.Clamp(PlayerSpawner.MultiplayerList.Count - 1, 0, ConcurrentMonsterLimits.Length - 1);
            int maxConcurrentMonsters = ConcurrentMonsterLimits[playerIndex];
            WaitForSeconds spawnDelay = new WaitForSeconds(0.2f);
            WaitForSeconds deathDelay = new WaitForSeconds(0.5f);

            yield return EndlessMod.CheckAndSpawnBoss(CurrentPhase, statMult, maxConcurrentMonsters);

            for (int i = 0; i < spawnCount; i++)
            {
                // Match the original spawner: wait for a living monster to die
                // before admitting another, rather than reducing the wave size.
                while (aliveMonsterCount >= maxConcurrentMonsters) yield return null;
                if (EndlessMod.SpawnMonster(statMult))
                    aliveMonsterCount++;
                yield return spawnDelay;
            }

            while (aliveMonsterCount > 0) yield return deathDelay;

            EndlessMod.RollTrialWitchHatReservation(CurrentPhase);

            if (CurrentPhase % 5 == 0)
            {
                Debug.Log($"[시련] {CurrentPhase}단계 완료: 전투 층에서 보상 이동 포탈 생성 요청");
                EndlessMod.SpawnBattleToRewardPortal();
            }

            // A clear phase is the only safe checkpoint moment: no enemies are
            // alive and every player can choose to save the run together.
            EndlessMod.SpawnTrialSaveReturnPortal();

            RpcShowSystemMessage("trial.msg.clear", CurrentPhase);


            RefreshTabletInteractText();
            EndlessMod.SetAllPlayersBattleState(false);
            NotifyTrialBattleEnded(CurrentPhase);
            CurrentPhase++;
            SetDirty();
            SetTrialRunning(false);
            EndlessMod.SpawnTrialDummies();
            EndlessMod.SaveTrialAutoCheckpoint(CurrentPhase, DisplayPhase);
        }

        [Server]
        private void NotifyTrialBattleEnded(int clearedPhase)
        {
            string battleGuid = EndlessMod.GetBattleFloorGuidForPhase(clearedPhase);
            foreach (NetworkConnectionToClient connection in NetworkServer.connections.Values)
            {
                PlayerAvatar? player = connection?.identity != null
                    ? connection.identity.GetComponent<PlayerAvatar>() : null;
                if (player == null || !player.isServer || player.currentFloorGuid != battleGuid) continue;
                player.OnEndSpawnerBattle?.Invoke();
            }
        }


        [Server]
        private IEnumerator DelayedSpawnMerchant(Vector3 position, int phase)
        {
            yield return new WaitForSeconds(0.25f);
            EndlessMod.SpawnTrialMerchantByClone(position, phase);
            // The storage terminal is deliberately next to, not on top of, the
            // merchant so both interaction prompts remain reachable.
            EndlessMod.SpawnTrialSupplyTerminal(position + new Vector3(3f, 0f, 0f), phase);
        }

        [Server]
        public void CmdSaveAndReturnToLobby(NetworkConnectionToClient? sender = null)
        {
            SaveAndReturnToLobbyOnServer(sender);
        }

        // The confirmation is opened only for the local host. Supply that
        // connection explicitly when this mod DLL calls the server path.
        [Server]
        public void SaveAndReturnToLobbyFromHost()
        {
            SaveAndReturnToLobbyOnServer(NetworkServer.localConnection);
        }

        private void SaveAndReturnToLobbyOnServer(NetworkConnectionToClient? sender)
        {
            Debug.Log($"[시련] 저장 귀환 처리: server={NetworkServer.active}, host={EndlessMod.IsTrialHost(sender)}, running={isTrialRunning}");
            if (!NetworkServer.active) return;
            if (isTrialRunning)
            {
                Debug.LogWarning("[시련] 전투 진행 중이어서 저장 귀환을 중단했습니다.");
                if (sender != null) TargetShowSystemMessage(sender, "trial.msg.save_unavailable");
                return;
            }
            if (!EndlessMod.IsTrialHost(sender))
            {
                if (sender != null) TargetShowSystemMessage(sender, "trial.msg.host_only_return");
                return;
            }
            if (!EndlessMod.IsActiveTrialPartyComplete() || !EndlessMod.AreAllPlayersNearTrialReturnPortal())
            {
                Debug.LogWarning("[시련] 저장 귀환 중단: 참가자가 저장 포탈 범위 밖에 있습니다.");
                TargetShowSystemMessage(sender, "trial.msg.gather_return");
                return;
            }
            if (EndlessMod.SaveTrialAndReturnToLobby(this))
                RpcShowSystemMessage("trial.return.saved", 0);
            else
            {
                Debug.LogError("[시련] 슬롯 저장 또는 귀환 처리에 실패했습니다.");
                TargetShowSystemMessage(sender, "trial.msg.save_unavailable");
            }
        }

        [Server]
        public void ResetAfterSavedReturn()
        {
            EndlessMod.ClearTrackedTrialMonsters();
            StopAllCoroutines();
            aliveMonsterCount = 0;
            deadMonsterIds.Clear();
            CurrentPhase = 1;
            DisplayPhase = 0;
            SetDirty();
            SetTrialRunning(false);
            EndlessMod.SetAllPlayersBattleState(false);
            EndlessMod.CleanupTrialUI();
            EndlessMod.CleanupTrialRewards();
            EndlessMod.CleanupTrialMerchants();
            EndlessMod.ClearTrialDummies();
            EndlessMod.ClearTrialSaveReturnPortal();
            EndlessMod.ClearTrialTransferPortals();
        }

        [Server]
        public void RestoreSavedProgress(int phase, int displayPhase)
        {
            CurrentPhase = Mathf.Max(1, phase);
            SetDisplayPhase(Mathf.Max(0, displayPhase));
            aliveMonsterCount = 0;
            deadMonsterIds.Clear();
            SetDirty();
            SetTrialRunning(false);
        }

        [Server]
        public void RestoreTrialPlaytime(float seconds)
        {
            seconds = Mathf.Max(0f, seconds);
            ApplyTrialPlaytime(seconds);
            TrialNetworkBridge.BroadcastNotice(
                TrialNetworkBridge.RestorePlaytime, seconds: seconds);
        }

        private static void ApplyTrialPlaytime(float seconds)
        {
            DungeonManager? dungeon = DungeonManager.Instance;
            if (dungeon == null) return;
            dungeon.playedRealtimeClientside = seconds;
            dungeon.countPlayedRealtimeClientside = true;
        }

        [Server]
        public void StopTrialRunTimer()
        {
            ApplyStopTrialRunTimer();
            TrialNetworkBridge.BroadcastNotice(TrialNetworkBridge.StopPlaytime);
        }

        [Server]
        public void PauseForPartyDisconnect()
        {
            if (!NetworkServer.active) return;
            StopAllCoroutines();
        }

        private static void ApplyStopTrialRunTimer()
        {
            DungeonManager? dungeon = DungeonManager.Instance;
            if (dungeon == null) return;
            dungeon.countPlayedRealtimeClientside = false;
            dungeon.playedRealtimeClientside = 0f;
        }

        private void RpcShowSystemMessage(string key, int phase)
        {
            if (UIManager.Instance != null) EndlessMod.ShowLocalizedSystemMessage(key, phase);
            TrialNetworkBridge.BroadcastNotice(
                TrialNetworkBridge.SystemMessage, phase, text: key);
        }

        private void TargetShowSystemMessage(NetworkConnectionToClient target, string key)
        {
            TrialNetworkBridge.SendNoticeTo(
                target, TrialNetworkBridge.SystemMessage, text: key);
        }

        [Server]
        public void OnMonsterDied(UnitAvatar av)
        {
            if (av == null || av.gameObject == null) return;
            if (av.GetComponent<TrialMonsterTag>() == null) return;

            int id = av.GetInstanceID();
            if (deadMonsterIds.Contains(id)) return;
            deadMonsterIds.Add(id);
            aliveMonsterCount--;

            // A 60th-phase relay boss replaces itself before this death can
            // satisfy the wave condition. Only QTemple_MBTrio_F has no successor, so its
            // death is the relay's final, count-releasing event.
            EndlessMod.TrySpawnNextSixtiethPhaseBoss(av);
            // Stage transitions stop this controller's coroutines. Keep corpse
            // removal on the persistent runner, like the native remover's
            // independent delayed NetworkServer.Destroy path.
            CoroutineManager.Instance.StartCoroutine(DestroyMonsterAfterDelay(av.gameObject, 0.1f));
        }

        private IEnumerator DestroyMonsterAfterDelay(GameObject monster, float delay)
        {
            yield return new WaitForSeconds(delay);
            if (monster != null)
            {
                if (NetworkServer.active) NetworkServer.Destroy(monster);
                else UnityEngine.Object.Destroy(monster);
            }
        }

        [Server]
        public void CmdNotifyGameOver()
        {
            if (!NetworkServer.active) return;

            ResetTrial(true);
        }

        private void RpcOnGameOver()
        {
            TrialNetworkBridge.BroadcastNotice(TrialNetworkBridge.GameOver);
        }

        [Server] public void AddAliveCount() { aliveMonsterCount++; }
    }
}
#pragma warning restore CS8618
#pragma warning restore CS8604
