#pragma warning disable CS8618 // 생성자를 종료할 때 null이 아닌 값을 포함해야 함 (유니티 컴포넌트 특성 반영)
#pragma warning disable CS8601 // 가능한 null 참조 할당
#pragma warning disable CS8603 // 가능한 null 참조 반환
#pragma warning disable CS8604 // 가능한 null 참조 인자
#pragma warning disable CS8625 // Null 리터럴을 null을 허용하지 않는 참조 형식으로 변환할 수 없음

using FMODUnity;
using Mirror;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SephiriaTrial
{
    public class TrialMonsterTag : MonoBehaviour { }
    public class MonsterMonitorTag : MonoBehaviour { }

    public class CoroutineRunner : MonoBehaviour { }

    public static class CoroutineManager
    {
        private static CoroutineRunner? _runner;
        public static CoroutineRunner Instance
        {
            get
            {
                if (_runner == null)
                {
                    GameObject obj = new GameObject("CoroutineManager");
                    UnityEngine.Object.DontDestroyOnLoad(obj);
                    _runner = obj.AddComponent<CoroutineRunner>();
                }
                return _runner;
            }
        }
    }

    public class EndlessMod : HorayModBase
    {
        public static EndlessMod Instance;
        public static Vector3 BossSpawnPosition = Vector3.zero;
        public static GameObject? TrialTablet;
        private static Dictionary<string, AvatarSpawnEntity>? dbCache;
        public static readonly List<GameObject> SpawnedRewards = new List<GameObject>();
        public static readonly List<GameObject> ActiveDummies = new List<GameObject>();
        private static bool _cachedGameOver = false;
        private static float _lastGameOverCheck = 0f;

        private GameObject? _trialCanvas;
        private TextMeshProUGUI? _trialText;
        private static bool isPopupOpen = false;
        public static List<GameObject> ActiveTrialMerchants = new List<GameObject>();
        private enum GameOverState { None, Shown }
        private static GameOverState _currentTrialGameOverState = GameOverState.None;

        private static readonly List<string> BlackList = new List<string> { "VampireBat", "GoatSkeleton_Prologue", "SkeletonArcher", "Skeleton", "Doppelganger_GreatSword", "FanaticTongueDemon(S)", "FanaticTentacle", "FanaticTentacle(S)", "FanaticSuicider", "FanaticSuicider(S)", "FanaticSamurai", "FanaticSamurai(S)", "DamageDummy", "FanaticCapybara", "FanaticCapybara(S)", "FanaticCapyBara_Event", "BabaMerchant", "RabbittownTeleporter", "RabbittownSoldier", "GrassLandtownCrowSoldier", "GrassLandtownDuckCarpenter", "GrassLandtownDuckSmith", "GrassLandtownDuckVillager", "KnightDemon", "FanaticTongueDemon", "OinkArcher", "OinkArcher(S)", "MoleElite" };
        private static readonly List<string> Pool_Tier1 = new List<string> { "Hedgehog_Mercenary", "Hedgehog_Mercenary(S)", "MoleDoctor", "MoleDoctor(S)", "MoleGrenader_Dynamite", "MoleGrenader_Dynamite(S)", "MoleGrenader_Stone", "MoleGrenader_Stone(S)", "MoleGuard", "MoleGuard(S)", "MoleHandBomber", "MoleHandBomber(S)", "MoleMini", "MoleMini(S)", "MoleSoldier", "MoleSoldier(S)", "SlimeBlue", "SlimeBlue_Big", "MoleDoctor_Demon", "MoleGrenader_Dynamite_Demon", "MoleGrenader_Stone_Demon", "MoleGuard_Demon", "MoleHandBomber_Demon", "MoleMini_Demon", "MoleSoldier_Demon" };
        private static readonly List<string> Pool_Tier21 = new List<string> { "CatAssassin", "CatAssassin(S)", "CatAssassin_Ground", "CatAssassin_Ground(S)", "CatThief", "CatThief(S)", "CatThief_Ground", "CatThief_Ground(S)", "OinkChief", "OinkChief(S)", "OinkMini", "OinkMini(S)", "OinkShaman", "OinkShaman(S)", "OinkTotem", "OinkWarrior", "OinkWarrior(S)", "FloatingEye", "MageDemon" };
        private static readonly List<string> Pool_Tier41 = new List<string> { "LibraryDrone", "LibraryDrone(S)", "LibraryGargoyle", "LibraryGargoyle(S)", "LibraryGargoyle_Winged", "LibraryGargoyle_Winged(S)", "LibraryGhost_Laser", "LibraryGhost_Laser(S)", "LibraryGhost_Melee", "LibraryGhost_Melee(S)", "LibraryGolemBall", "LibraryGolemBall(S)", "LibraryGolemCow", "LibraryGolemCow(S)", "LibraryGuardianStatue", "LibraryGuardianStatue(S)", "LibraryLivingStatue", "LibraryLivingStatue(S)", "LibraryMage", "LibraryMage(S)", "LibraryMage_WaterBolt", "LibraryMage_WaterBolt(S)", "SlimeOrange", "SlimeOrange_Big" };
        private static readonly List<string> Pool_Tier51 = new List<string> { "BoneDemon", "CannonDemon", "CannonDemon(S)", "SkeletonSoldier", "SkeletonSoldier(S)", "FanaticBuffer", "FanaticDemonSoldier", "FanaticDemonSoldier(S)", "FanaticMouse", "FanaticMouse(S)", "FanaticRabbit", "FanaticRabbit(S)" };
        private static readonly List<string> Pool_Tier101 = new List<string> { "BombDemon", "BombDemon(S)", "ChakramThrower", "ChakramThrower(S)", "CrystalDemon", "CrystalDemon(S)", "LanternDemon", "LanternDemon(S)", "SkeletonMouseMage", "EyeOfDeath", "EyeOfDeath(S)", "FanaticCat", "FanaticCat(S)", "FloatingEye_DeepCave", "FloatingEye_DeepCave(S)", "SlimeMagma", "SpikeEye(S)", "FlowerSkeleton", "FlowerSkeleton(S)", "GoatSkeletonDeepCave", "HugeSkeleton", "LizardSkeleton", "MushroomHowitzer", "MushroomHowitzer(S)", "PoisonDemon", "RootMage", "RootMage(S)" };

        private static FieldInfo? cachedFNetworkMaxHp;
        private static FieldInfo? cachedFMaxHp;
        private static FieldInfo? cachedFNetworkHp;
        private static FieldInfo? cachedFHp;
        private static bool isHpFieldsCached = false;

        private static FieldInfo? cachedFNetworkIsInBattle;
        private static FieldInfo? cachedFIsInBattle;
        private static bool isBattleFieldsCached = false;

        // [하모니 제거 및 최적화용 필드]
        private static GameObject? _lastTrackedTablet = null;
        private static float _pollTimer = 0f;

        private class EndlessUpdateBridge : MonoBehaviour
        {
            public Action? UpdateAction;
            private void Update() => UpdateAction?.Invoke();
        }

        protected override void OnModLoaded()
        {
            Instance = this;
            LoadDatabase();

            GameObject? pf = Resources.Load<GameObject>("Sephirite/Sephirite_Tablet");
            if (pf != null && !NetworkClient.prefabs.ContainsKey(pf.GetComponent<NetworkIdentity>().assetId))
                NetworkClient.RegisterPrefab(pf);

            HorayModAPI.OnLocalizationReady += (ctx) =>
            {
                var textData = new Dictionary<string, Dictionary<string, string>>
                {
                    { "trial.popup.challenge", new Dictionary<string, string> { { "ko-KR", "<color=red>{0}단계</color> 시련에 도전하겠습니까?" }, { "en-US", "Would you like to challenge Phase <color=red>{0}</color>?" }, { "ja-JP", "<color=red>{0}段階</color>の試練に挑戦しますか？" }, { "zh-CN", "你要挑战 <color=red>第 {0} 阶段</color> 的试炼吗？" } } },
                    { "trial.tablet.interact", new Dictionary<string, string> { { "ko-KR", "시련 시작하기" }, { "en-US", "Start Trial" }, { "ja-JP", "試練段階を開始" }, { "zh-CN", "开始试炼" } } },
                    { "trial.msg.start", new Dictionary<string, string> { { "ko-KR", "<color=yellow>{0}단계 시련을 시작합니다...</color>" }, { "en-US", "<color=yellow>Starting Phase {0} Trial...</color>" }, { "ja-JP", "<color=yellow>{0}段階の試練を開始します...</color>" }, { "zh-CN", "<color=yellow>开始第 {0} 阶段试炼...</color>" } } },
                    { "trial.msg.clear", new Dictionary<string, string> { { "ko-KR", "시련 <color=red>{0}단계</color> 완료" }, { "en-US", "Phase <color=red>{0}</color> Cleared" }, { "ja-JP", "試練 <color=red>{0}段階</color> 完了" }, { "zh-CN", "试炼 <color=red>第 {0} 阶段</color> 完成" } } },
                    { "trial.ui.text", new Dictionary<string, string> { { "ko-KR", "시련 <color=red>{0}단계</color>" }, { "en-US", "Phase <color=red>{0}</color>" }, { "ja-JP", "試練 <color=red>{0}段階</color>" }, { "zh-CN", "试炼 <color=red>第 {0} 阶段</color>" } } },
                    { "trial.ui.waiting", new Dictionary<string, string> { { "ko-KR", "시련 <color=red>대기</color>" }, { "en-US", "<color=red>Waiting</color> Trial" }, { "ja-JP", "試練 <color=red>待機</color>" }, { "zh-CN", "试炼 <color=red>等待</color>" } } },
                    { "trial.merchant.name", new Dictionary<string, string> { { "ko-KR", "타미" }, { "en-US", "Tami" }, { "ja-JP", "Tami" }, { "zh-CN", "Tami" } } }
                };

                foreach (var kp in textData)
                {
                    ctx.AddText(kp.Key, kp.Value);
                }
            };

            GameObject bridgeObj = new GameObject("EndlessMod_UpdateBridge");
            UnityEngine.Object.DontDestroyOnLoad(bridgeObj);
            var bridge = bridgeObj.AddComponent<EndlessUpdateBridge>();
            bridge.UpdateAction = OnModUpdate;

            Debug.Log($"[{metadata.modName}] Loaded successfully.");
        }

        public static string GetSafeText(string key, string fallback)
        {
            try
            {
                string localized = Loc._(key);
                if (!string.IsNullOrEmpty(localized) && localized != key)
                {
                    return localized;
                }
            }
            catch (Exception) { }
            return fallback;
        }

        private void OnModUpdate()
        {
            UpdateTrialGameOverState();
            
            // [렉 제어 혁신] 매 프레임 무거운 오브젝트 서칭을 하지 않고 0.2초마다 스캔하도록 제약하여 프레임 드랍 완벽 제거
            _pollTimer += Time.deltaTime;
            if (_pollTimer >= 0.2f)
            {
                _pollTimer = 0f;
                MonitorGameWorldObjects();
            }
        }

        private static void MonitorGameWorldObjects()
        {
            // [A] 스폰 몬스터 추적 및 컴포넌트 실시간 이식
            if (!NetworkServer.active) return;

            // 1. 전체를 뒤지지 말고, 최근 생성되었는데 아직 감시 루프에 안 들어간 몬스터만 필터링하거나
            // 2. 혹은 이미 감시 중인 몬스터를 제외한 새로운 녀석만 타겟팅합니다.
            var allAvatars = UnityEngine.Object.FindObjectsByType<UnitAvatar>(FindObjectsSortMode.None);

            foreach (var av in allAvatars)
            {
                // 이미 관리 중인 몬스터는 건너뜀 (예: 태그나 커스텀 필드 활용)
                if (av == null || av.GetComponent<MonsterMonitorTag>() != null) continue;

                // 시련 몬스터이거나 보스라면 감시 시작
                if (av is Unit_LizardDemon || av.GetComponent<TrialMonsterTag>() != null)
                {
                    // 감시를 시작했다는 표시를 남김 (메모리상에만 존재하는 마커)
                    av.gameObject.AddComponent<MonsterMonitorTag>();

                    // 코루틴 시작: 이 몬스터가 죽을 때까지 감시
                    CoroutineManager.Instance.StartCoroutine(MonitorMonsterDeath(av.gameObject, av));
                }
            }

            // [B] 보스 출현 위치 단 1회 캡처 (SpawnPatch 대체)
            var lizard = UnityEngine.Object.FindAnyObjectByType<Unit_LizardDemon>();
            if (lizard != null && BossSpawnPosition == Vector3.zero)
            {
                BossSpawnPosition = lizard.transform.position;
                Debug.Log($"[EndlessMod] 보스 스폰 좌표 갱신: {BossSpawnPosition}");
            }

            // [C] 석판 클라이언트 가시성 세팅 및 동기화 캐싱 방어 조건 고도화
            if (TrialTablet != null && _lastTrackedTablet != TrialTablet)
            {
                var sephirite = TrialTablet.GetComponent<Sephirite>();
                if (sephirite != null)
                {
                    _lastTrackedTablet = TrialTablet; // 캐싱 시점을 먼저 기록하여 중복 호출 차단
                    ApplySephiriteClientVisibilityManual(sephirite);
                }
            }
        }

        // Tracker 가 소멸/사망 트리거를 발동할 때 안전하게 호출해 줄 징검다리 메서드
        public static void TriggerLizardDemonDeathLogicDirect()
        {
            var lizard = UnityEngine.Object.FindAnyObjectByType<Unit_LizardDemon>();
            if (lizard != null)
            {
                TriggerLizardDemonDeathLogic(lizard);
            }
            else
            {
                TriggerLizardDemonDeathLogic(null!);
            }
        }

        private static void TriggerLizardDemonDeathLogic(Unit_LizardDemon bossInstance)
        {
            if (!NetworkServer.active) return;
            
            if (TrialController.Instance == null)
            {
                GameObject ctrlObj = new GameObject("TrialController");
                ctrlObj.AddComponent<NetworkIdentity>();
                ctrlObj.AddComponent<TrialController>();
                NetworkServer.Spawn(ctrlObj);
            }
            else TrialController.Instance.ResetTrial(false);

            GameObject? pf = Resources.Load<GameObject>("Sephirite/Sephirite_Tablet");
            if (pf == null) return;
            if (TrialTablet != null) NetworkServer.Destroy(TrialTablet);

            // 보스 사망 위치 혹은 백업 위치에 안전하게 인스턴스 생성
            Vector3 finalSpawnPos = (BossSpawnPosition != Vector3.zero) ? BossSpawnPosition : new Vector3(0f, 3005f, 0f);
            TrialTablet = UnityEngine.Object.Instantiate(pf, finalSpawnPos, Quaternion.identity);
            TrialTablet.name = "Trial_Sephirite_Tablet";
            NetworkServer.Spawn(TrialTablet);

            Sephirite? sephirite = TrialTablet.GetComponent<Sephirite>();
            if (sephirite != null)
            {
                sephirite.enabled = true;
                Type sepType = sephirite.GetType();

                PropertyInfo? pNetworkGen = sepType.GetProperty("NetworkisGenerated", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                FieldInfo? fIsGen = sepType.GetField("isGenerated", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                pNetworkGen?.SetValue(sephirite, true);
                fIsGen?.SetValue(sephirite, true);
            }
            foreach (var comp in TrialTablet.GetComponents<IRandomID>()) comp.SetRandomID(UnityEngine.Random.Range(0, int.MaxValue));
            TrialTablet.layer = LayerMask.NameToLayer("Blockable");

            var renderingData = TrialTablet.GetComponent<TopdownActorRenderingMetadata>();
            if (renderingData != null)
            {
                renderingData.enabled = true;
                if (renderingData.bodyRenderer != null) renderingData.bodyRenderer.enabled = true;
            }

            Interactable? it = TrialTablet.GetComponent<Interactable>();
            if (it != null)
            {
                it.enabled = true;
                it.OnInteraction += (a) => ShowTrialPopup();
                string interactStr = GetSafeText("trial.tablet.interact", "시련 시작하기");

                FieldInfo? fDesc = it.GetType().GetField("interactionDescription", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                fDesc?.SetValue(it, new LocalizedString(interactStr));
            }

            FloorGenerator? fg = UnityEngine.Object.FindFirstObjectByType<FloorGenerator>();
            if (fg != null) fg.floorRelatedNetworkObjects.Add(TrialTablet);
            if (TrialController.Instance != null) Instance.UpdateTrialText(0);
        }

        private static void ApplySephiriteClientVisibilityManual(Sephirite sephiriteInstance)
        {
            var rd = sephiriteInstance.GetComponent<TopdownActorRenderingMetadata>();
            if (rd != null) { rd.enabled = true; if (rd.bodyRenderer != null) rd.bodyRenderer.enabled = true; }
            var it = sephiriteInstance.GetComponent<Interactable>();
            if (it != null)
            {
                it.enabled = true;
                var locString = new LocalizedString("trial.tablet.interact");
                var field = typeof(Sephirite).GetField("interactString", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (field != null)
                {
                    field.SetValue(sephiriteInstance, locString);
                }
                Debug.Log("[EndlessMod] 하모니 없이 석판 가시성 세팅 및 상호작용 문자열 주입 완료.");
            }
        }

        private static void LoadDatabase()
        {
            try
            {
                var field = typeof(AvatarSpawnDatabase).GetField("spawnEntities", BindingFlags.NonPublic | BindingFlags.Static);
                if (field != null)
                {
                    dbCache = field.GetValue(null) as Dictionary<string, AvatarSpawnEntity>;
                    if (dbCache != null && dbCache.Count > 0)
                    {
                        Debug.Log($"[Sephiria Endless Trial] Database loaded successfully. ({dbCache.Count} entities)");
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[Sephiria Endless Trial] Error loading database: {e.Message}");
            }
        }

        public static void ShowTrialPopup()
        {
            if (isPopupOpen) return;

            UI_MessageBoxHolder? holder = Resources.FindObjectsOfTypeAll<UI_MessageBoxHolder>()
                                         .FirstOrDefault(h => h != null && !h.name.Contains("(Clone)"));
            if (holder == null || UIManager.Instance == null) return;

            isPopupOpen = true;

            int currentPhase = TrialController.Instance?.CurrentPhase ?? 1;
            string rawMsg = GetSafeText("trial.popup.challenge", "<color=red>{0}단계</color> 시련에 도전하겠습니까?");
            string localizedBody = string.Format(rawMsg, currentPhase);

            UI_MessageBox msgBox = holder.OpenYesNo(
                localizedBody,
                () => { TrialController.Instance?.CmdStartTrial(); PlayBattleStartSound(); CloseTrialPopup(); },
                () => { CloseTrialPopup(); },
                false
            );

            if (msgBox != null)
            {
                var tr = msgBox.GetComponent<RectTransform>();
                if (tr != null) tr.anchoredPosition = new Vector2(0, 0f);
            }
        }

        public static void CloseTrialPopup()
        {
            isPopupOpen = false;
            var msgBox = Resources.FindObjectsOfTypeAll<UI_MessageBox>()
                        .FirstOrDefault(b => b != null && b.gameObject.name == "TrialMessageBox_Unique" && b.gameObject.activeInHierarchy);

            if (msgBox != null && UIManager.Instance != null)
            {
                InvokePrivateMethod(UIManager.Instance, "OnControlRemoved", new object[] { msgBox });
                if (UIManager.Instance.doingUIThingValue > 0) UIManager.Instance.doingUIThingValue--;
                msgBox.Close();
                InvokePrivateMethod(UIManager.Instance, "ControlUpdate", Array.Empty<object>());
            }
        }

        private static void InvokePrivateMethod(object instance, string methodName, object[]? args)
        {
            try
            {
                MethodInfo? method = instance.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
                if (method != null) method.Invoke(instance, args);
            }
            catch (Exception e)
            {
                Debug.LogError($"[Trial] Reflection Error ({methodName}): {e.Message}");
            }
        }

        public void ShowTrialTablet() { if (TrialTablet != null) TrialTablet.SetActive(true); }
        public void HideTrialTablet() { if (TrialTablet != null) TrialTablet.SetActive(false); }

        public static void SpawnMonster(float statMult)
        {
            if (!NetworkServer.active) return;
            if (dbCache == null || dbCache.Count == 0) LoadDatabase();
            if (dbCache == null) return;

            int phase = TrialController.Instance?.CurrentPhase ?? 1;
            var pool = GetCurrentPool(phase).Where(m => !BlackList.Contains(m)).ToList();
            if (pool.Count > 0)
            {
                string key = pool[UnityEngine.Random.Range(0, pool.Count)];
                if (dbCache.TryGetValue(key, out var ent))
                {
                    GameObject? prefab = ent.Select();
                    if (prefab == null) return;

                    Vector3 spawnPos = new Vector3(UnityEngine.Random.Range(-12f, 12f), UnityEngine.Random.Range(3003f, 3012f), 0f);
                    GameObject obj = UnityEngine.Object.Instantiate(prefab, spawnPos, Quaternion.identity);
                    
                    // 태그 부착 시 다음 감시 사이클에서 자동으로 Tracker가 할당됩니다.
                    obj.AddComponent<TrialMonsterTag>();
                    NetworkServer.Spawn(obj);

                    if (TrialController.Instance != null)
                    {
                        TrialController.Instance.StartCoroutine(ApplyMonsterStatNextFrame(obj, statMult, phase));
                    }
                }
            }
        }

        public static void CheckAndSpawnBoss(int phase, float statMult)
        {
            if (phase % 5 == 0)
            {
                int fullStacks = phase / 60;
                int remaining = phase % 60;
                for (int i = 0; i < fullStacks; i++) HandlePhaseBossLogic(60, statMult);
                if (remaining > 0) HandlePhaseBossLogic(remaining, statMult);
            }
        }

        private static void HandlePhaseBossLogic(int phase, float statMult)
        {
            switch (phase)
            {
                case 5: SpawnSpecificBosses("GoatSkeleton", 1, statMult); break;
                case 10: SpawnSpecificBosses("GoatSkeleton", 2, statMult); break;
                case 15: SpawnSpecificBosses("GoatSkeleton", 3, statMult); break;
                case 20: SpawnSpecificBosses("GoatSkeleton", 2, statMult); SpawnSpecificBosses("PantherRogue", 1, statMult); break;
                case 25: SpawnSpecificBosses("GoatSkeleton", 1, statMult); SpawnSpecificBosses("PantherRogue", 2, statMult); break;
                case 30: SpawnSpecificBosses("PantherRogue", 3, statMult); break;
                case 35: SpawnSpecificBosses("PantherRogue", 2, statMult); SpawnSpecificBosses("LibraryDemonBook", 1, statMult); break;
                case 40: SpawnSpecificBosses("PantherRogue", 1, statMult); SpawnSpecificBosses("LibraryDemonBook", 2, statMult); break;
                case 45: SpawnSpecificBosses("LibraryDemonBook", 3, statMult); break;
                case 50: SpawnSpecificBosses("LibraryDemonBook", 2, statMult); SpawnSpecificBosses("SamuraiDemon", 1, statMult); break;
                case 55: SpawnSpecificBosses("LibraryDemonBook", 1, statMult); SpawnSpecificBosses("SamuraiDemon", 2, statMult); break;
                case 60: SpawnSpecificBosses("SamuraiDemon", 3, statMult); break;
            }
        }

        private static void SpawnSpecificBosses(string key, int count, float statMult)
        {
            if (dbCache == null) return;
            if (dbCache.TryGetValue(key, out var ent))
            {
                for (int i = 0; i < count; i++)
                {
                    GameObject? prefab = ent.Select();
                    if (prefab == null) continue;
                    GameObject obj = UnityEngine.Object.Instantiate(prefab, GetRandomSpawnPos(), Quaternion.identity);
                    obj.AddComponent<TrialMonsterTag>();
                    NetworkServer.Spawn(obj);

                    if (TrialController.Instance != null)
                    {
                        TrialController.Instance.AddAliveCount();
                        int currentPhase = TrialController.Instance.CurrentPhase;
                        TrialController.Instance.StartCoroutine(ApplyMonsterStatNextFrame(obj, statMult * 1.5f, currentPhase));
                    }
                }
            }
        }

        public static void SpawnTrialReward(int phase, Vector3 basePos)
        {
            if (!NetworkServer.active) return;

            List<int> rewardboxPool = new List<int> { 28, 29, 30, 31, 32, 33, 38, 39 };
            if (phase % 5 == 0)
            {
                Vector3 orbPos = BossSpawnPosition + new Vector3(0f, 2.5f, 0f);
                SpawnFromDatabase("InventoryOrb", orbPos);

                CreateCustomRewardBox("RewardBox_MP", BossSpawnPosition + new Vector3(-2f, 2.5f, 0f), rewardboxPool);
                CreateCustomRewardBox("RewardBox_MP", BossSpawnPosition + new Vector3(-2f, 3.5f, 0f), rewardboxPool);
                CreateCustomRewardBox("RewardBox_MP", BossSpawnPosition + new Vector3(2f, 2.5f, 0f), rewardboxPool);
                CreateCustomRewardBox("RewardBox_MP", BossSpawnPosition + new Vector3(2f, 3.5f, 0f), rewardboxPool);
            }

            int rewardCount = Mathf.Clamp(phase / 20 + 1, 1, 5);
            List<string> rewardPool = new List<string> { "MysticPot", "AltarOfEnchant_Dual", "AltarOfEnchant_Tri", "SephiriteSpawner-StoneTablet", "SephiriteSpawner-Charm", "MaxHPDispenser", "MiracleSelector", "Obelisk" };

            Vector3 centerPos = BossSpawnPosition + new Vector3(0f, -3f, 0f);
            float fixedDistance = 3f;

            for (int i = 0; i < rewardCount; i++)
            {
                string selectedID = rewardPool[UnityEngine.Random.Range(0, rewardPool.Count)];
                float sectorSize = 180f / rewardCount;
                float baseAngle = 180f + (i * sectorSize) + (sectorSize / 2f);

                float offset = UnityEngine.Random.Range(-sectorSize * 0.4f, sectorSize * 0.4f);
                float finalAngleRad = (baseAngle + offset) * Mathf.Deg2Rad;

                float spawnX = Mathf.Cos(finalAngleRad) * fixedDistance;
                float spawnY = Mathf.Sin(finalAngleRad) * fixedDistance;

                Vector3 finalPos = centerPos + new Vector3(spawnX, spawnY, 0f);
                SpawnFromDatabase(selectedID, finalPos);
            }
            PlayRewardSound(basePos);
        }

        private static void CreateCustomRewardBox(string propId, Vector3 position, List<int> itemIDPool)
        {
            PropEntity? entity = PropDatabase.FindPropById(propId);
            if (entity == null || entity.propPrefab == null) return;

            GameObject boxObj = UnityEngine.Object.Instantiate(entity.propPrefab, position, Quaternion.identity);
            BreakableProp? prop = boxObj.GetComponent<BreakableProp>();
            if (prop != null)
            {
                List<BreakableProp.DropItemData> customDrops = new List<BreakableProp.DropItemData>();
                foreach (int id in itemIDPool)
                {
                    ItemEntity? item = ItemDatabase.FindItemById(id);
                    if (item != null) customDrops.Add(new BreakableProp.DropItemData { entity = item, quantity = 1 });
                }
                prop.droppableItems = customDrops.ToArray();
                prop.SetRandomID(UnityEngine.Random.Range(int.MinValue, int.MaxValue));
            }

            NetworkServer.Spawn(boxObj);
            FloorGenerator? fg = UnityEngine.Object.FindFirstObjectByType<FloorGenerator>();
            if (fg != null) fg.floorRelatedNetworkObjects.Add(boxObj);
            SpawnedRewards.Add(boxObj);
        }

        private static void SpawnFromDatabase(string propId, Vector3 pos)
        {
            PropEntity? entity = PropDatabase.FindPropById(propId);
            if (entity?.propPrefab == null) return;
            GameObject obj = UnityEngine.Object.Instantiate(entity.propPrefab, pos, Quaternion.identity);
            foreach (var r in obj.GetComponents<IRandomID>()) r.SetRandomID(UnityEngine.Random.Range(int.MinValue, int.MaxValue));
            NetworkServer.Spawn(obj);
            FloorGenerator? fg = UnityEngine.Object.FindFirstObjectByType<FloorGenerator>();
            if (fg != null) fg.floorRelatedNetworkObjects.Add(obj);
            SpawnedRewards.Add(obj);
        }

        public void UpdateTrialText(int phase)
        {
            int currentDisplayPhase = (TrialController.Instance != null) ? TrialController.Instance.DisplayPhase : phase;
            string textResult = "";

            if (currentDisplayPhase <= 0)
            {
                textResult = GetSafeText("trial.ui.waiting", "시련 <color=red>대기</color>");
            }
            else
            {
                string rawFormat = GetSafeText("trial.ui.text", "시련 <color=red>{0}단계</color>");
                textResult = string.Format(rawFormat, currentDisplayPhase);
            }

            if (_trialCanvas == null || _trialText == null) CreateTrialUI();
            if (_trialText != null) _trialText.text = textResult;
        }

        private void CreateTrialUI()
        {
            _trialCanvas = new GameObject("TrialCanvas");
            UnityEngine.Object.DontDestroyOnLoad(_trialCanvas);
            var canvas = _trialCanvas.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay; canvas.sortingOrder = 1000;

            GameObject bgObj = new GameObject("TrialStageBackground");
            bgObj.transform.SetParent(_trialCanvas.transform, false);
            var bgImage = bgObj.AddComponent<Image>();
            
            string imgPath = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "", "Endless_Trial", "trial_bg.png");
            Sprite? loadedSprite = LoadSprite(imgPath);
            if (loadedSprite != null) bgImage.sprite = loadedSprite;
            bgImage.color = new Color(1f, 1f, 1f, 0.5f);

            var bgRect = bgObj.GetComponent<RectTransform>();
            bgRect.anchorMin = bgRect.anchorMax = new Vector2(0.5f, 1f);
            bgRect.pivot = new Vector2(0.5f, 1f); bgRect.anchoredPosition = new Vector2(0, -30);
            bgRect.sizeDelta = new Vector2(300, 60);

            GameObject textObj = new GameObject("TrialText");
            textObj.transform.SetParent(bgObj.transform, false);
            _trialText = textObj.AddComponent<TextMeshProUGUI>();
            _trialText.font = Resources.FindObjectsOfTypeAll<TMP_FontAsset>().FirstOrDefault(f => f != null && (f.name.Contains("Main") || f.name.Contains("Title"))) ?? TMP_Settings.defaultFontAsset;
            _trialText.fontSize = 30; _trialText.alignment = TextAlignmentOptions.Center;
            _trialText.textWrappingMode = TextWrappingModes.NoWrap; 
        }

        private Sprite? LoadSprite(string path)
        {
            if (!File.Exists(path)) return null;
            byte[] data = File.ReadAllBytes(path);
            Texture2D tex = new Texture2D(2, 2, TextureFormat.RGBA32, false); tex.LoadImage(data); tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f));
        }

        public static List<string> GetCurrentPool(int phase)
        {
            List<string> pool = new List<string>(Pool_Tier1);
            if (phase >= 21) pool.AddRange(Pool_Tier21);
            if (phase >= 41) pool.AddRange(Pool_Tier41);
            if (phase >= 51) pool.AddRange(Pool_Tier51);
            if (phase >= 101) pool.AddRange(Pool_Tier101);
            return pool;
        }

        public static Vector3 GetRandomSpawnPos() => new Vector3(UnityEngine.Random.Range(-12f, 12f), UnityEngine.Random.Range(3001f, 3014f), 0f);

        private static IEnumerator ApplyMonsterStatNextFrame(GameObject monster, float mult, int phase)
        {
            yield return null;
            if (monster == null) yield break;

            UnitAvatar? av = monster.GetComponent<UnitAvatar>();
            if (av == null) yield break;

            av.ChangeFaction("Demon");
            av.CancelCurrentAction();

            if (TrialController.Instance != null)
                TrialController.Instance.StartCoroutine(MonitorMonsterDeath(monster, av));

            Type avType = av.GetType();
            FieldInfo? fNetworkMaxHp = avType.GetField("NetworkmaxHp", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            FieldInfo? fMaxHp = avType.GetField("maxHp", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            FieldInfo? fNetworkHp = avType.GetField("Networkhp", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            FieldInfo? fHp = avType.GetField("hp", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            float currentHp = fHp != null ? (float)fHp.GetValue(av) : 1f;
            FieldInfo? fIsDead = avType.GetField("isDead", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            bool isDead = fIsDead != null && (bool)fIsDead.GetValue(av);

            if (currentHp <= 0f || isDead) { yield break; }

            float baseHp = 0f;
            if (fNetworkMaxHp != null) baseHp = (float)fNetworkMaxHp.GetValue(av);
            if (baseHp <= 0f && fMaxHp != null) baseHp = (float)fMaxHp.GetValue(av);

            float finalHp = baseHp * mult;

            fNetworkMaxHp?.SetValue(av, finalHp);
            fMaxHp?.SetValue(av, finalHp);
            fNetworkHp?.SetValue(av, finalHp);
            fHp?.SetValue(av, finalHp);

            av.AddCustomStat(ECustomStat.TrueDamage, phase * 5);
            av.AddCustomStat(ECustomStat.AttackSpeed, phase * 10);
            av.AddCustomStat(ECustomStat.DamageReduction, phase * 3);
        }

        private static IEnumerator MonitorMonsterDeath(GameObject monster, UnitAvatar av)
        {
            Type avType = av.GetType();
            FieldInfo? fIsDead = avType.GetField("isDead", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            FieldInfo? fHp = avType.GetField("hp", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            while (monster != null)
            {
                bool isDead = false;
                float currentHp = 0f;

                if (fIsDead != null) isDead = (bool)fIsDead.GetValue(av);
                if (fHp != null) currentHp = (float)fHp.GetValue(av);
                if (isDead || currentHp <= 0f)
                {
                    if (av is Unit_LizardDemon) TriggerLizardDemonDeathLogicDirect();
                    else TrialController.Instance?.OnMonsterDied(av);
                    yield break;
                }
                yield return new UnityEngine.WaitForSeconds(0.5f);
            }
        }

        public static void ShowSystemMessage(string msg) { UI_SystemMessage? ui = Resources.FindObjectsOfTypeAll<UI_SystemMessage>().FirstOrDefault(); ui?.Open(msg, 3f, false); }
        private static void PlayRewardSound(Vector3 pos) { try { var guid = new FMOD.GUID { Data1 = -1064292075, Data2 = 1082856418, Data3 = -164297295, Data4 = -85952462 }; var inst = RuntimeManager.CreateInstance(new EventReference { Guid = guid }); inst.set3DAttributes(RuntimeUtils.To3DAttributes(pos)); inst.start(); inst.release(); } catch { } }
        private static void PlayBattleStartSound() { try { var guid = new FMOD.GUID { Data1 = -1393806677, Data2 = 1320069483, Data3 = -1389724540, Data4 = -2128244874 }; var inst = RuntimeManager.CreateInstance(new EventReference { Guid = guid }); inst.set3DAttributes(RuntimeUtils.To3DAttributes(BossSpawnPosition)); inst.start(); inst.release(); } catch { } }

        public static void CleanupTrialUI()
        {
            CloseTrialPopup();
            if (Instance != null && Instance._trialCanvas != null)
            {
                UnityEngine.Object.Destroy(Instance._trialCanvas);
                Instance._trialCanvas = null;
                Instance._trialText = null;
            }

            GameObject? extraCanvas = GameObject.Find("TrialCanvas");
            if (extraCanvas != null) UnityEngine.Object.Destroy(extraCanvas);

            if (TrialTablet != null)
            {
                if (NetworkServer.active) NetworkServer.Destroy(TrialTablet);
                else TrialTablet.SetActive(false);
                TrialTablet = null;
            }
            isPopupOpen = false;
        }

        public static bool IsGameOver()
        {
            if (Time.unscaledTime - _lastGameOverCheck > 0.2f)
            {
                var label = GameObject.Find("GameOverLabel");
                _cachedGameOver = (label != null && label.activeInHierarchy);
                _lastGameOverCheck = Time.unscaledTime;
            }
            return _cachedGameOver;
        }

        public static void CleanupTrialRewards()
        {
            if (!NetworkServer.active) return;
            for (int i = SpawnedRewards.Count - 1; i >= 0; i--)
            {
                GameObject obj = SpawnedRewards[i];
                if (obj == null || obj == TrialTablet) continue;
                NetworkServer.Destroy(obj);
            }
            SpawnedRewards.Clear();

            foreach (GameObject go in UnityEngine.Object.FindObjectsByType<GameObject>(FindObjectsSortMode.None))
            {
                if (go == null || go == TrialTablet) continue;
                if (go.name.Contains("AltarOfTablet") || go.name.Contains("AltarOfEnchant") || go.name.Contains("SephiriteSpawner") || go.name.Contains("TabletMix") || go.name.Contains("HPDispenser"))
                    NetworkServer.Destroy(go);
            }
        }

        public static void SpawnTrialDummies()
        {
            if (!NetworkServer.active) return;
            ClearTrialDummies();

            Vector3[] positions = new Vector3[] { new Vector3(-10f, 3003f, 0f), new Vector3(10f, 3003f, 0f) };
            var field = typeof(AvatarSpawnDatabase).GetField("spawnEntities", BindingFlags.NonPublic | BindingFlags.Static);
            var db = field?.GetValue(null) as Dictionary<string, AvatarSpawnEntity>;

            if (db != null && db.TryGetValue("DamageDummy", out var ent))
            {
                foreach (var pos in positions)
                {
                    GameObject? prefab = ent.Select();
                    if (prefab == null) continue;

                    GameObject dummy = UnityEngine.Object.Instantiate(prefab, pos, Quaternion.identity);
                    UnitAvatar? av = dummy.GetComponent<UnitAvatar>();

                    if (av != null)
                    {
                        if (!isHpFieldsCached)
                        {
                            Type avType = av.GetType();
                            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                            cachedFNetworkMaxHp = avType.GetField("NetworkmaxHp", flags);
                            cachedFMaxHp = avType.GetField("maxHp", flags);
                            cachedFNetworkHp = avType.GetField("Networkhp", flags);
                            cachedFHp = avType.GetField("hp", flags);
                            isHpFieldsCached = true;
                        }

                        float infinityHp = 99999999f;

                        cachedFNetworkMaxHp?.SetValue(av, infinityHp);
                        cachedFMaxHp?.SetValue(av, infinityHp);
                        cachedFNetworkHp?.SetValue(av, infinityHp);
                        cachedFHp?.SetValue(av, infinityHp);

                        Debug.Log("[EndlessMod] 허수아비 체력을 리플렉션을 통해 1억으로 설정했습니다.");
                    }

                    NetworkServer.Spawn(dummy);
                    ActiveDummies.Add(dummy);
                }
            }
        }

        public static void ClearTrialDummies()
        {
            if (!NetworkServer.active) return;
            foreach (var d in ActiveDummies) if (d != null) NetworkServer.Destroy(d);
            ActiveDummies.Clear();
        }

        public static void SpawnTrialMerchantByClone(Vector3 position, int phase)
        {
            if (!NetworkServer.active) return;

            GameObject? merchantPrefab = null;

            if (dbCache != null && dbCache.TryGetValue("SquirrelTraveler", out var ent))
            {
                merchantPrefab = ent.Select();
            }

            if (merchantPrefab == null)
            {
                merchantPrefab = NetworkClient.prefabs.Values
                    .FirstOrDefault(p => p != null && (p.name == "SquirrelTraveler" || p.name == "SquirrelTraveler(Clone)"));
            }

            if (merchantPrefab == null)
            {
                Debug.LogError("[시련] 오류: 게임 내부에서 'SquirrelTraveler' 프리팹을 전혀 찾을 수 없습니다. (DB 및 NetworkPrefabs 확인 필요)");
                return;
            }

            GameObject clone = UnityEngine.Object.Instantiate(merchantPrefab, position, Quaternion.identity);
            clone.name = "TrialMerchant_Phase_" + phase;

            UnitAvatar? avatar = clone.GetComponent<UnitAvatar>();
            if (avatar != null)
                avatar.SetRandomID(UnityEngine.Random.Range(0, int.MaxValue));

            NetworkServer.Spawn(clone);
            clone.SetActive(true);

            UnitAI_NewBasic? ai = clone.GetComponent<UnitAI_NewBasic>();
            if (ai != null)
            {
                string uniqueSocialID = $"TrialMerchant_{phase}_{UnityEngine.Random.Range(0, 10000)}";
                string merchantName = GetSafeText("trial.merchant.name", "타미");

                ai.SetSocialID(uniqueSocialID, merchantName, EPersonality.Rational, EFactionAlignment.Good, "Merchant", EProceduralMerchantType.Vendor, 1000 + (phase * 100), null);

                if (ai.NetworkMySafe != null)
                    ActiveTrialMerchants.Add(ai.NetworkMySafe.gameObject);
            }
            ActiveTrialMerchants.Add(clone);
        }

        public static GameObject? GetInactiveMerchantPrefab()
        {
            GameObject[] allObjects = UnityEngine.Object.FindObjectsByType<GameObject>(FindObjectsSortMode.None);
            foreach (GameObject obj in allObjects) if (obj != null && (obj.name == "SquirrelTraveler" || obj.name == "SquirrelTraveler(Clone)")) return obj;
            return null;
        }

        public static void CleanupTrialMerchants()
        {
            if (!NetworkServer.active) return;
            foreach (var obj in ActiveTrialMerchants) if (obj != null) NetworkServer.Destroy(obj);
            ActiveTrialMerchants.Clear();
        }

        private void UpdateTrialGameOverState()
        {
            bool isGameOverNow = IsGameOver();
            switch (_currentTrialGameOverState)
            {
                case GameOverState.None:
                    if (isGameOverNow)
                    {
                        _currentTrialGameOverState = GameOverState.Shown;
                        CleanupTrialUI();
                        CleanupTrialRewards();
                        CleanupTrialMerchants();
                        ClearTrialDummies();

                        if (TrialController.Instance != null)
                        {
                            if (NetworkServer.active) TrialController.Instance.ResetTrial(true);
                            else TrialController.Instance.CmdNotifyGameOver();
                        }
                    }
                    break;
                case GameOverState.Shown:
                    if (!isGameOverNow) _currentTrialGameOverState = GameOverState.None;
                    break;
            }
        }

        public static void SetAllPlayersBattleState(bool isInBattle)
        {
            if (!NetworkServer.active) return;
            sbyte stateValue = (sbyte)(isInBattle ? 1 : 0);

            foreach (PlayerSpawner playerSpawner in PlayerSpawner.MultiplayerList)
            {
                if (playerSpawner == null || playerSpawner.PlayerAvatar == null) continue;

                var player = playerSpawner.PlayerAvatar;
                if (player == null) continue;

                if (!isBattleFieldsCached)
                {
                    Type playerType = player.GetType();
                    var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

                    cachedFNetworkIsInBattle = playerType.GetField("NetworkisInBattle", flags);
                    cachedFIsInBattle = playerType.GetField("isInBattle", flags);
                    isBattleFieldsCached = true;
                }

                try
                {
                    cachedFNetworkIsInBattle?.SetValue(player, stateValue);
                    cachedFIsInBattle?.SetValue(player, stateValue);
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[EndlessMod] 플레이어 전투 상태 리플렉션 주입 실패: {ex.Message}");
                }
            }
        }
    }
}