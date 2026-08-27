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

        [SyncVar(hook = nameof(OnDisplayPhaseChanged))]
        public int DisplayPhase = 0;

        void OnDisplayPhaseChanged(int oldVal, int newVal)
        {
            UpdateLocalUI(newVal);
        }

        [SyncVar] public int CurrentPhase = 1;
        [SyncVar] private int aliveMonsterCount;
        private readonly HashSet<int> deadMonsterIds = new HashSet<int>();

        public override void OnStartServer() { Instance = this; }
        void Awake() { if (Instance == null) Instance = this!; }
        public override void OnStartClient()
        {
            Instance = this;
            UpdateLocalUI(DisplayPhase);
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

                // 2. 안전하게 최신 다국어 텍스트 가져오기
                string interactStr = EndlessMod.GetSafeText("trial.tablet.interact", "시련 시작하기");

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
                    cachedFInteractionDescription.SetValue(interactable, new LocalizedString(interactStr));
                    Debug.Log($"[시련] 석판 상호작용 텍스트 새로고침 완료: {interactStr}");
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
            DisplayPhase = phase;
            RpcUpdateUIText(phase);
        }

        [ClientRpc]
        public void RpcUpdateUIText(int phase)
        {
            UpdateLocalUI(phase);
        }

        [Server]
        public void ResetTrial(bool isGameOver)
        {
            Debug.Log($"[시련] 리셋 실행 (게임오버 여부: {isGameOver})");

            if (isGameOver)
            {
                if (EndlessMod.TrialTablet != null) NetworkServer.Destroy(EndlessMod.TrialTablet);
                EndlessMod.CleanupTrialUI();

                EndlessMod.CleanupTrialMerchants();

                CurrentPhase = 1;
                DisplayPhase = 0;
                RpcOnGameOver();
            }

            aliveMonsterCount = 0;
            deadMonsterIds.Clear();
            StopAllCoroutines();
            EndlessMod.CleanupTrialRewards();
            EndlessMod.ClearTrialDummies();
        }

        [Command(requiresAuthority = false)]
        public void CmdStartTrial()
        {
            if (!NetworkServer.active) return;

            EndlessMod.CleanupTrialMerchants();
            EndlessMod.ClearTrialDummies();
            StopAllCoroutines();
            StartCoroutine(TrialRoutine());
        }

        private IEnumerator TrialRoutine()
        {
            SetDisplayPhase(CurrentPhase);
            EndlessMod.CleanupTrialRewards();
            RpcHideTrialTablet();
            EndlessMod.SetAllPlayersBattleState(true);

            // 1. 시련 시작 시스템 메시지 다국어 처리 ---
            string startFormat = EndlessMod.GetSafeText("trial.msg.start", "<color=yellow>{0}단계 시련을 시작합니다...</color>");
            string localizedStartMsg = string.Format(startFormat, CurrentPhase);
            RpcShowSystemMessage(localizedStartMsg);

            deadMonsterIds.Clear();
            aliveMonsterCount = 0;

            int spawnCount = 25 + (CurrentPhase - 1);
            float statMult = 1f + (CurrentPhase - 1) * 2f;

            EndlessMod.CheckAndSpawnBoss(CurrentPhase, statMult);

            for (int i = 0; i < spawnCount; i++)
            {
                EndlessMod.SpawnMonster(statMult);
                aliveMonsterCount++;
                yield return new WaitForSeconds(0.2f);
            }

            while (aliveMonsterCount > 0) yield return new WaitForSeconds(0.5f);

            if (CurrentPhase % 5 == 0)
            {
                Vector3 basePos = EndlessMod.BossSpawnPosition;
                Vector2 randomOffset = UnityEngine.Random.insideUnitCircle * 5f;
                Vector3 finalPos = new Vector3(basePos.x + randomOffset.x, basePos.y + randomOffset.y, basePos.z);

                EndlessMod.SpawnTrialReward(CurrentPhase, finalPos);

                StartCoroutine(DelayedSpawnMerchant(new Vector3(basePos.x, 3017f, 0), CurrentPhase));
            }

            // --- 2. 시련 완료 시스템 메시지 다국어 처리 ---
            string clearFormat = EndlessMod.GetSafeText("trial.msg.clear", "시련 <color=red>{0}단계</color> 완료");
            string localizedClearMsg = string.Format(clearFormat, CurrentPhase);
            RpcShowSystemMessage(localizedClearMsg);


            RefreshTabletInteractText();
            CurrentPhase++;
            RpcShowTrialTablet();
            EndlessMod.SpawnTrialDummies();
            EndlessMod.SetAllPlayersBattleState(false);
        }


        [Server]
        private IEnumerator DelayedSpawnMerchant(Vector3 position, int phase)
        {
            yield return new WaitForSeconds(2.0f);
            EndlessMod.SpawnTrialMerchantByClone(position, phase);
        }

        [ClientRpc] private void RpcHideTrialTablet() => EndlessMod.Instance?.HideTrialTablet();
        [ClientRpc] private void RpcShowTrialTablet() => EndlessMod.Instance?.ShowTrialTablet();


        [ClientRpc]
        private void RpcShowSystemMessage(string msg)
        {
            if (UIManager.Instance != null)
            {
                EndlessMod.ShowSystemMessage(msg);
            }
            Debug.Log($"[Client] 시스템 메시지 수신: {msg}");
        }

        [Server]
        public void OnMonsterDied(UnitAvatar av)
        {
            StartCoroutine(DestroyMonsterAfterDelay(av.gameObject, 2.0f));

            if (av == null || av.gameObject == null) return;

            int id = av.GetInstanceID();
            if (deadMonsterIds.Contains(id)) return;
            bool isTrialMonster = av.GetComponent<TrialMonsterTag>() != null;
            deadMonsterIds.Add(id);
            aliveMonsterCount--;
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

        [Command(requiresAuthority = false)]
        public void CmdNotifyGameOver()
        {
            if (!NetworkServer.active) return;

            ResetTrial(true);
            RpcOnGameOver();
        }

        [ClientRpc]
        private void RpcOnGameOver()
        {
            if (!NetworkServer.active)
            {
                Debug.Log("[시련] 클라이언트 RPC: UI 정리 신호 수신");
                EndlessMod.CleanupTrialUI();
                EndlessMod.SetAllPlayersBattleState(false);
            }
        }

        [Server] public void AddAliveCount() { aliveMonsterCount++; }
    }
}
#pragma warning restore CS8618
#pragma warning restore CS8604