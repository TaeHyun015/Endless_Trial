#pragma warning disable CS8618 // 생성자를 종료할 때 null이 아닌 값을 포함해야 함 (유니티 컴포넌트 특성 반영)
#pragma warning disable CS8601 // 가능한 null 참조 할당
#pragma warning disable CS8603 // 가능한 null 참조 반환
#pragma warning disable CS8604 // 가능한 null 참조 인자
#pragma warning disable CS8625 // Null 리터럴을 null을 허용하지 않는 참조 형식으로 변환할 수 없음

using FMODUnity;
using HeathenEngineering.SteamworksIntegration;
using HeathenEngineering.SteamworksIntegration.API;
using Mirror;
using Newtonsoft.Json;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Tilemaps;
using UnityEngine.UI;

namespace SephiriaTrial
{
    public class TrialMonsterTag : MonoBehaviour { }

    // Server-only marker for the 60th-phase mini-boss relay.  It deliberately
    // lives on the spawned avatar so existing monster counting remains authoritative.
    public class TrialBossSequenceTag : MonoBehaviour
    {
        public string nextBossKey = string.Empty;
    }

    // Native PreventPosition handles normal movement.  This records only how
    // long a teleporting monster has remained outside before a hard recovery.
    public class TrialMonsterBoundaryRecoveryTag : MonoBehaviour
    {
        public float outsideSince = -1f;
    }

    // The existing return-portal NetworkIdentity is reused for all trial portal
    // variants. The mod assembly is not processed by Mirror's Weaver, so this
    // route needs explicit serialization to reach remote clients.
    public class TrialPortalRoute : NetworkBehaviour
    {
        // 0 = save and return to town, 1 = battle to shared reward room,
        // 2 = reward room back to battle.
        public byte route;

        public override void OnSerialize(NetworkWriter writer, bool initialState)
        {
            writer.WriteByte(route);
        }

        public override void OnDeserialize(NetworkReader reader, bool initialState)
        {
            route = reader.ReadByte();
            if (route == 1) gameObject.name = "Trial_BattleToReward_Portal";
            else if (route == 2) gameObject.name = "Trial_RewardToBattle_Portal";
            else gameObject.name = "Trial_SaveReturn_Portal";
            Interactable? interactable = GetComponent<Interactable>();
            if (interactable != null)
                EndlessMod.ConfigureTrialPortalInteraction(interactable, route);
        }

        [Server]
        public void SetRoute(byte value)
        {
            if (route == value) return;
            route = value;
            SetDirty();
        }
    }

    // The dedicated entrance is built by the mod, not cloned from the town
    // portal.  Each client copies only the town portal's Sprite assets locally
    // so the networked object never inherits any teleport/return behaviour.
    public class TrialEntrancePortalVisual : MonoBehaviour
    {
        private IEnumerator Start()
        {
            if (gameObject.name == "TrialEntrancePortal_RuntimePrefab") yield break;
            WaitForSeconds portalRetryDelay = new WaitForSeconds(0.25f);
            // Mirror can spawn this object on a remote peer before that peer has
            // finished spawning the MultiZone return portal.  Wait for it rather
            // than giving up after one frame; otherwise a client receives the
            // interaction object but sees no portal art.
            for (int attempt = 0; attempt < 40; attempt++)
            {
                if (GetComponentInChildren<SpriteRenderer>() != null)
                {
                    Interactable? configuredInteractable = GetComponent<Interactable>();
                    if (configuredInteractable != null)
                        EndlessMod.ConfigureTrialEntranceInteraction(configuredInteractable);
                    yield break;
                }

                Interactable? townPortal = EndlessMod.FindTownReturnPortal();
                if (townPortal != null)
                {
                    EndlessMod.CopyTownPortalPresentation(townPortal, gameObject);
                    Interactable? interactable = GetComponent<Interactable>();
                    if (interactable != null)
                        EndlessMod.ConfigureTrialEntranceInteraction(interactable);
                    yield break;
                }

                yield return portalRetryDelay;
            }

            Debug.LogWarning("[Sephiria Endless Trial] 클라이언트에서 마을 귀환 포탈을 찾지 못해 시련 포탈 외형을 적용하지 못했습니다.");
        }
    }

    // The return portal uses the same presentation as the town portal, but it
    // intentionally owns a different interaction flow: save this trial run and
    // reset back to the lobby rather than invoking the town portal's behaviour.
    public class TrialReturnPortalVisual : MonoBehaviour
    {
        private IEnumerator Start()
        {
            if (gameObject.name == "TrialReturnPortal_RuntimePrefab") yield break;
            WaitForSeconds portalRetryDelay = new WaitForSeconds(0.25f);
            // Let Mirror deserialize TrialPortalRoute before selecting the
            // client-side interaction callback.
            yield return null;

            for (int attempt = 0; attempt < 40; attempt++)
            {
                if (GetComponentInChildren<SpriteRenderer>() != null)
                {
                    Interactable? configuredInteractable = GetComponent<Interactable>();
                    if (configuredInteractable != null)
                        EndlessMod.ConfigureTrialPortalInteraction(configuredInteractable, GetRoute());
                    yield break;
                }

                if (EndlessMod.CopyCachedTownPortalPresentation(gameObject))
                {
                    Interactable? interactable = GetComponent<Interactable>();
                    if (interactable != null)
                        EndlessMod.ConfigureTrialPortalInteraction(interactable, GetRoute());
                    yield break;
                }

                Interactable? townPortal = EndlessMod.FindTownReturnPortal();
                if (townPortal != null)
                {
                    EndlessMod.CopyTownPortalPresentation(townPortal, gameObject);
                    Interactable? interactable = GetComponent<Interactable>();
                    if (interactable != null)
                        EndlessMod.ConfigureTrialPortalInteraction(interactable, GetRoute());
                    yield break;
                }

                yield return portalRetryDelay;
            }

            Debug.LogWarning("[Sephiria Endless Trial] 저장 귀환 포탈의 외형을 준비하지 못했습니다.");
        }

        private byte GetRoute()
        {
            TrialPortalRoute? route = GetComponent<TrialPortalRoute>();
            return route != null ? route.route : (byte)0;
        }
    }

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

        public static void Clear()
        {
            if (_runner != null)
            {
                _runner.StopAllCoroutines();
                UnityEngine.Object.Destroy(_runner.gameObject);
                _runner = null;
            }
        }
    }

    public partial class EndlessMod : HorayModBase
    {
        public const string TrialFloorGuid = "endless_trial_floor";
        public const string TrialBattle21FloorGuid = "endless_trial_battle_21";
        public const string TrialBattle41FloorGuid = "endless_trial_battle_41";
        public const string TrialBattle51FloorGuid = "endless_trial_battle_51";
        public const string TrialBattle101FloorGuid = "endless_trial_battle_101";
        public const string TrialReward01FloorGuid = "endless_trial_reward_01";
        public const string TrialReward21FloorGuid = "endless_trial_reward_21";
        public const string TrialReward41FloorGuid = "endless_trial_reward_41";
        public const string TrialReward51FloorGuid = "endless_trial_reward_51";
        public const string TrialReward101FloorGuid = "endless_trial_reward_101";
        private const string TrialFloorName = "Endless Trial";
        // DLL과 같은 폴더에 두는 Windows AssetBundle. 모든 멀티플레이 참가자가
        // 동일한 파일을 가져야 Mirror가 같은 NetworkIdentity.assetId를 해석할 수 있다.
        private const string TrialFloorBundleFileName = "endless_trial_floor";
        private const string TrialFloorPrefabName = "EndlessTrialFloor";
        private const string TrialSavePanelPrefabName = "TrialSaveProfilePanel";
        private const uint TrialEntrancePortalAssetId = 0xE71D0F02u;
        private const uint TrialControllerAssetId = 0xE71D0F03u;
        private const uint TrialReturnPortalAssetId = 0xE71D0F04u;
        private const string TrialSnapshotPrefix = "EndlessTrialSnapshot_";
        private const string TrialSlotPrefix = "EndlessTrialSlot_";
        private const string TrialActiveSlotKey = "EndlessTrialActiveSlot";
        private const string TrialRewardIssuedPhaseKey = "EndlessTrialRewardIssuedPhase";
        private const string TrialMerchantRoomStateKey = "EndlessTrialMerchantRoomState";
        private const string TrialIndividualRewardRoomStateKey = "EndlessTrialIndividualRewardRoomState";
        private const string TrialWitchHatPendingKey = "EndlessTrialWitchHatPending";
        private const string TrialWitchHatLastRollPhaseKey = "EndlessTrialWitchHatLastRollPhase";
        private const float TrialPortalGatherRadius = 2.5f;
        public const int TrialSlotCount = 3;
        private const int TrialMaxLevel = 10000;
        private static FieldInfo? _levelExpTableField;
        private static int[]? _nativeLevelExpTable;
        private static int[]? _trialLevelExpTable;
        private static bool _trialLevelCapActive;
        private static bool _trialLevelCapUnavailable;
        private static bool _trialLevelCapSuppressed;
        private static float _trialLevelCapPendingFloorUntil;
        private static float _nextLevelCapRestoreAttemptTime;
        private static int _activeTrialSlot;
        private static readonly Dictionary<int, SaveData> TrialSlotFiles = new Dictionary<int, SaveData>();
        private static string _trialSlotProfile = string.Empty;
        private static Task _trialSlotWriteTask = Task.CompletedTask;
        private static bool _keepRestoredTrialLobbyOpen;
        private static float _nextSavedLobbyRefreshTime;
        private static readonly List<string> ActiveTrialPartyGuids = new List<string>();
        private static readonly List<string> ActiveTrialPartyNames = new List<string>();
        private static Vector3 TrialPlayerSpawnPosition = new Vector3(0f, -5f, 0f);
        public static EndlessMod Instance;
        private static EndlessUpdateBridge? _updateBridge;
        private static Action<HorayModLocalizationContext>? _localizationReadyHandler;
        private static LocalizationManager? _subscribedLanguageManager;
        // 시련 전용 공간을 붙이기 전까지는 시작 지점 근처를 시련의 기준점으로 사용한다.
        // 기존에는 크라즈의 사망 위치가 이 값을 채웠다.
        public static Vector3 TrialAnchorPosition = Vector3.zero;
        // Keep random spawns and portal placement near the room centre. Monster
        // movement uses the actual collision-tile bounds below instead.
        private static Vector2 TrialCombatHalfExtents = new Vector2(10.5f, 5f);
        private static Vector2 TrialMonsterBoundsMin = new Vector2(-10.5f, -5f);
        private static Vector2 TrialMonsterBoundsMax = new Vector2(10.5f, 5f);
        // TopdownRigidbody exposes five independent movement-prevention slots.
        // Reserve the last one for the trial arena so enemy prefabs can keep
        // any restrictions they already define for their own abilities.
        private const int TrialMonsterBoundaryLayer = 4;
        // The wall TilemapCollider2D already blocks movement at the physical
        // edge. Do not add a second arbitrary inset that stops monsters before
        // players can reach the same collision boundary.
        private const float TrialMonsterBoundaryInset = 0f;
        private const float TrialMonsterHardRecoveryDistance = 2f;
        private const float TrialMonsterHardRecoveryDelay = 1f;
        private const float TrialMonsterSpawnSuperArmorChance = 0.30f;
        // The original dungeon's region-weighted random-room selection averages
        // roughly 0.8-0.9% for WitchHat. Roll once after each cleared phase and
        // reserve a single visit for the next reward room.
        private const float TrialWitchHatSpawnChance = 0.0088f;
        public static GameObject? TrialEntrance;
        public static GameObject? TrialTablet;
        public static GameObject? TrialReturnPortal;
        private static string? _trialReturnPortalFloorGuid;
        private static string? _trialEntranceFloorGuid;
        private static readonly List<GameObject> ActiveTrialTransferPortals = new List<GameObject>();
        private static readonly Dictionary<GameObject, string> ActiveTrialTransferPortalFloors = new Dictionary<GameObject, string>();
        private static readonly Dictionary<string, int> PreparedRewardRoomPhases = new Dictionary<string, int>();
        private static readonly Dictionary<string, int> PreparedRewardRoomFloorIds = new Dictionary<string, int>();
        private sealed class TrialMerchantItemState
        {
            public TrialMerchantItemState() { }
            public int instanceId;
            public int entityId;
            public sbyte quantity;
            public sbyte x;
            public sbyte y;
        }

        private sealed class TrialMerchantState
        {
            public TrialMerchantState() { }
            public int randomId;
            public int money;
            public List<TrialMerchantItemState> items = new List<TrialMerchantItemState>();
            public List<TrialMerchantReplenishmentState> replenishments = new List<TrialMerchantReplenishmentState>();
            public int replenishmentTryCount;
        }

        private sealed class TrialMerchantReplenishmentState
        {
            public TrialMerchantReplenishmentState() { }
            public int entityId;
            public bool purchased;
        }

        private sealed class TrialMerchantRoomState
        {
            public TrialMerchantRoomState() { }
            public int phase;
            public TrialMerchantState? regular;
            public TrialMerchantState? witch;
        }
        private static Dictionary<string, AvatarSpawnEntity>? dbCache;
        public static readonly List<GameObject> SpawnedRewards = new List<GameObject>();
        public static readonly List<GameObject> ActiveDummies = new List<GameObject>();
        private static bool _cachedGameOver = false;
        private static float _lastGameOverCheck = 0f;
        private static UI_GameOverLabel? _cachedGameOverLabel;
        private static float _nextGameOverLabelSearchTime;
        private static float _lastTrialFloorSeenAt = float.NegativeInfinity;

        private GameObject? _trialCanvas;
        private TextMeshProUGUI? _trialText;
        private static bool isPopupOpen = false;
        public static List<GameObject> ActiveTrialMerchants = new List<GameObject>();
        // The native SocialAvatarSpawer itself is not networked; it only creates
        // the networked WitchHat avatar.  Track these helper objects separately
        // so cleanup never passes a non-networked object to NetworkServer.Destroy.
        private static readonly List<GameObject> ActiveTrialSocialSpawners = new List<GameObject>();
        private enum GameOverState { None, Shown }
        private static GameOverState _currentTrialGameOverState = GameOverState.None;
        private static bool _trialGameOverPlaceActive;
        private static UI_HUDMultiplayerRoomViewer? _trialRoomInfoViewer;
        private static bool _trialRoomInfoHidden;
        private static float _nextRoomInfoViewerSearchTime;
        private static bool _restoreCheckpointPhaseOnFloorAllocation;
        private static string? _startupCheckpointFloorGuid;
        private static UI_MessageBox_YesNo? _activeTrialPopup;
        private static string? _activeTrialPopupKey;
        private static object[] _activeTrialPopupFormatArgs = Array.Empty<object>();
        private static string? _activeSystemMessageKey;
        private static int _activeSystemMessagePhase;
        private static string? _activeSystemMessageRenderedText;

        private static readonly List<string> BlackList = new List<string> { "VampireBat", "GoatSkeleton_Prologue", "SkeletonArcher", "Skeleton", "Doppelganger_GreatSword", "FanaticTongueDemon(S)", "FanaticTentacle", "FanaticTentacle(S)", "FanaticSuicider", "FanaticSuicider(S)", "FanaticSamurai", "FanaticSamurai(S)", "DamageDummy", "FanaticCapybara", "FanaticCapybara(S)", "FanaticCapyBara_Event", "BabaMerchant", "RabbittownTeleporter", "RabbittownSoldier", "GrassLandtownCrowSoldier", "GrassLandtownDuckCarpenter", "GrassLandtownDuckSmith", "GrassLandtownDuckVillager", "KnightDemon", "FanaticTongueDemon", "OinkArcher", "OinkArcher(S)", "MoleElite" };
        private static readonly List<string> Pool_Tier1 = new List<string> { "Hedgehog_Mercenary", "Hedgehog_Mercenary(S)", "MoleDoctor", "MoleDoctor(S)", "MoleGrenader_Dynamite", "MoleGrenader_Dynamite(S)", "MoleGrenader_Stone", "MoleGrenader_Stone(S)", "MoleGuard", "MoleGuard(S)", "MoleHandBomber", "MoleHandBomber(S)", "MoleMini", "MoleMini(S)", "MoleSoldier", "MoleSoldier(S)", "SlimeBlue", "SlimeBlue_Big", "MoleDoctor_Demon", "MoleGrenader_Dynamite_Demon", "MoleGrenader_Stone_Demon", "MoleGuard_Demon", "MoleHandBomber_Demon", "MoleMini_Demon", "MoleSoldier_Demon" };
        private static readonly List<string> Pool_Tier21 = new List<string> { "CatAssassin", "CatAssassin(S)", "CatAssassin_Ground", "CatAssassin_Ground(S)", "CatThief", "CatThief(S)", "CatThief_Ground", "CatThief_Ground(S)", "OinkChief", "OinkChief(S)", "OinkMini", "OinkMini(S)", "OinkShaman", "OinkShaman(S)", "OinkTotem", "OinkWarrior", "OinkWarrior(S)", "FloatingEye", "MageDemon" };
        private static readonly List<string> Pool_Tier41 = new List<string> { "LibraryDrone", "LibraryDrone(S)", "LibraryGargoyle", "LibraryGargoyle(S)", "LibraryGargoyle_Winged", "LibraryGargoyle_Winged(S)", "LibraryGhost_Laser", "LibraryGhost_Laser(S)", "LibraryGhost_Melee", "LibraryGhost_Melee(S)", "LibraryGolemBall", "LibraryGolemBall(S)", "LibraryGolemCow", "LibraryGolemCow(S)", "LibraryGuardianStatue", "LibraryGuardianStatue(S)", "LibraryLivingStatue", "LibraryLivingStatue(S)", "LibraryMage", "LibraryMage(S)", "LibraryMage_WaterBolt", "LibraryMage_WaterBolt(S)", "SlimeOrange", "SlimeOrange_Big" };
        private static readonly List<string> Pool_Tier51 = new List<string> { "BoneDemon", "CannonDemon", "CannonDemon(S)", "SkeletonSoldier", "SkeletonSoldier(S)", "FanaticBuffer", "FanaticDemonSoldier", "FanaticDemonSoldier(S)", "FanaticMouse", "FanaticMouse(S)", "FanaticRabbit", "FanaticRabbit(S)" };
        private static readonly List<string> Pool_Tier101 = new List<string> { "BombDemon", "BombDemon(S)", "ChakramThrower", "ChakramThrower(S)", "CrystalDemon", "CrystalDemon(S)", "LanternDemon", "LanternDemon(S)", "SkeletonMouseMage", "EyeOfDeath", "EyeOfDeath(S)", "FanaticCat", "FanaticCat(S)", "FloatingEye_DeepCave", "FloatingEye_DeepCave(S)", "SlimeMagma", "SpikeEye(S)", "FlowerSkeleton", "FlowerSkeleton(S)", "GoatSkeletonDeepCave", "HugeSkeleton", "LizardSkeleton", "MushroomHowitzer", "MushroomHowitzer(S)", "PoisonDemon", "RootMage", "RootMage(S)" };
        private static readonly Dictionary<int, List<string>> FilteredTrialMonsterPools =
            new Dictionary<int, List<string>>();
        // Keep the trial's contribution to UnitAvatar.isInBattle separate from
        // the native PlayerBattleChecker and other room spawners. Each source
        // must release only the StartBattle call it made itself.
        private static readonly HashSet<PlayerAvatar> TrialBattleParticipants = new HashSet<PlayerAvatar>();
        private static readonly HashSet<PlayerAvatar> TrialBattleEligiblePlayers = new HashSet<PlayerAvatar>();
        private static readonly List<PlayerAvatar> TrialBattleParticipantsToRelease = new List<PlayerAvatar>();

        // [하모니 제거 및 최적화용 필드]
        private static GameObject? _lastTrackedTablet = null;
        private static float _nextTrialFastTickTime;
        private static float _nextTrialTabletSearchTime;
        private static FloorGenerator? _trialTabletSearchFloor;
        private static Vector3 _trialTabletSearchCenter;
        private static readonly List<UnitAvatar> ActiveTrialMonsters = new List<UnitAvatar>();
        private sealed class TrialMonsterRuntimeCache
        {
            public readonly Transform transform;
            public TopdownRigidbody? rigidbody;
            public TrialMonsterBoundaryRecoveryTag? recovery;
            public Vector3 appliedLowerLeft;
            public Vector3 appliedUpperRight;
            public bool boundaryApplied;

            public TrialMonsterRuntimeCache(UnitAvatar avatar)
            {
                transform = avatar.transform;
                rigidbody = avatar.TopdownRigidbody;
                recovery = avatar.GetComponent<TrialMonsterBoundaryRecoveryTag>();
            }
        }
        private static readonly Dictionary<UnitAvatar, TrialMonsterRuntimeCache> TrialMonsterRuntimeCaches =
            new Dictionary<UnitAvatar, TrialMonsterRuntimeCache>();
        private static PlayerAvatar? _cachedLocalPlayer;
        private static float _nextLocalPlayerSearchTime;
        private static bool _trialFloorRegistered;
        private static DungeonManager? _trialRegisteredDungeon;
        private static bool _trialEntranceSpawnPending;
        private static FieldInfo? _floorDataField;
        private static MethodInfo? _addToGeneratedFloorMethod;
        private static AssetBundle? _trialFloorBundle;
        private static GameObject? _trialFloorPrefab;
        private static GameObject? _trialSavePanelPrefab;
        private static readonly Dictionary<string, GameObject> _trialFloorPrefabs = new Dictionary<string, GameObject>();
        private static readonly Dictionary<string, uint> _trialFloorAssetIds = new Dictionary<string, uint>();
        private static readonly Dictionary<string, string> _trialFloorLayoutXml = new Dictionary<string, string>();
        private static GameObject? _trialEntrancePortalPrefab;
        private static GameObject? _trialReturnPortalPrefab;
        private static GameObject? _trialControllerPrefab;
        private static uint _trialFloorAssetId;
        private static uint _trialEntrancePortalAssetId;
        private static uint _trialReturnPortalAssetId;
        private static uint _trialControllerAssetId;
        private static FieldInfo? _networkIdentityAssetIdField;
        private static bool _trialFloorBundleLoadAttempted;
        private static bool _trialFloorBundleAvailable;
        private static string? _lastObservedLocalFloorGuid;
        private static GameObject? _townPortalPresentationTemplate;
        private static Vector2 _townPortalColliderOffset;
        private static Vector2 _townPortalColliderSize;
        private static bool _hasCachedTownPortalPresentation;
        private static Interactable? _cachedTownReturnPortal;
        private static float _nextTownReturnPortalSearchTime;
        private static MethodInfo? _playerSpawnerInitializeMethod;
        private static MethodInfo? _dungeonSaveCurrentSessionMethod;
        private static MethodInfo? _dungeonRunStartRpcMethod;

        private static readonly Dictionary<string, string> TrialFloorPrefabNames = new Dictionary<string, string>
        {
            { TrialFloorGuid, "EndlessTrialFloor" },
            { TrialBattle21FloorGuid, "EndlessTrialBattle21" },
            { TrialBattle41FloorGuid, "EndlessTrialBattle41" },
            { TrialBattle51FloorGuid, "EndlessTrialBattle51" },
            { TrialBattle101FloorGuid, "EndlessTrialBattle101" },
            { TrialReward01FloorGuid, "EndlessTrialReward01" },
            { TrialReward21FloorGuid, "EndlessTrialReward21" },
            { TrialReward41FloorGuid, "EndlessTrialReward41" },
            { TrialReward51FloorGuid, "EndlessTrialReward51" },
            { TrialReward101FloorGuid, "EndlessTrialReward101" }
        };

        // DungeonManager allocates a floor at (globalX * 500, globalY * 500).
        // Keep every battle and reward floor in its own world cell, including
        // reward floors retained while players return to the battle floor.
        private static readonly string[] TrialFloorWorldOrder =
        {
            TrialFloorGuid, TrialReward01FloorGuid,
            TrialBattle21FloorGuid, TrialReward21FloorGuid,
            TrialBattle41FloorGuid, TrialReward41FloorGuid,
            TrialBattle51FloorGuid, TrialReward51FloorGuid,
            TrialBattle101FloorGuid, TrialReward101FloorGuid
        };

        private static readonly Dictionary<string, string> TrialFloorLayoutAssetPaths = new Dictionary<string, string>
        {
            { TrialFloorGuid, "Assets/TrialMod/TrialLayout.xml" },
            { TrialBattle21FloorGuid, "Assets/TrialMod/Layouts/Battle_21.xml" },
            { TrialBattle41FloorGuid, "Assets/TrialMod/Layouts/Battle_41.xml" },
            { TrialBattle51FloorGuid, "Assets/TrialMod/Layouts/Battle_51.xml" },
            { TrialBattle101FloorGuid, "Assets/TrialMod/Layouts/Battle_101.xml" },
            { TrialReward01FloorGuid, "Assets/TrialMod/Layouts/Reward_01.xml" },
            { TrialReward21FloorGuid, "Assets/TrialMod/Layouts/Reward_21.xml" },
            { TrialReward41FloorGuid, "Assets/TrialMod/Layouts/Reward_41.xml" },
            { TrialReward51FloorGuid, "Assets/TrialMod/Layouts/Reward_51.xml" },
            { TrialReward101FloorGuid, "Assets/TrialMod/Layouts/Reward_101.xml" }
        };

        private class EndlessUpdateBridge : MonoBehaviour
        {
            public Action? UpdateAction;
            private void Update() => UpdateAction?.Invoke();
            private void OnDestroy()
            {
                if (!ReferenceEquals(_updateBridge, this)) return;
                _updateBridge = null;
                _nextLevelCapRestoreAttemptTime = 0f;
                RestoreNativeLevelCap();
            }
        }

        protected override void OnModLoaded()
        {
            // AddOnLoader.LoadAll clears its list without calling UnloadMod when
            // the title scene starts a new GameDataLoader. Remove the previous
            // instance's static subscriptions and DontDestroyOnLoad update loop.
            if (Instance != null) Instance.OnModUnloaded();
            Instance = this;
            // GameDataLoader initializes AvatarSpawnDatabase after AddOnLoader.
            // Read it only when a Trial spawn actually needs it.
            dbCache = null;
            FilteredTrialMonsterPools.Clear();
            // 씬/데이터베이스 초기화와 무관하게 각 클라이언트에서 먼저 번들을 읽고
            // Mirror 프리팹 표에 등록한다. 그래야 서버가 시련 층을 Spawn할 때
            // 호스트뿐 아니라 접속 중인 클라이언트도 같은 프리팹을 만들 수 있다.
            TryLoadTrialFloorBundle();
            EnsureRuntimeTrialEntrancePortalPrefab();
            EnsureRuntimeTrialReturnPortalPrefab();
            EnsureRuntimeTrialControllerPrefab();
            RegisterCachedTrialNetworkPrefabs();
            TrialNetworkBridge.EnsureRegistered();
            SceneManager.activeSceneChanged += OnActiveSceneChanged;

            GameObject? pf = Resources.Load<GameObject>("Sephirite/Sephirite_Tablet");
            if (pf != null && !NetworkClient.prefabs.ContainsKey(pf.GetComponent<NetworkIdentity>().assetId))
                NetworkClient.RegisterPrefab(pf);

            _localizationReadyHandler ??= (ctx) =>
            {
                var textData = new Dictionary<string, Dictionary<string, string>>
                {
                    { "trial.popup.challenge", new Dictionary<string, string> { { "ko-KR", "<color=red>{0}단계</color> 시련에 도전하겠습니까?" }, { "en-US", "Would you like to challenge Phase <color=red>{0}</color>?" }, { "ja-JP", "<color=red>{0}段階</color>の試練に挑戦しますか？" }, { "zh-CN", "你要挑战 <color=red>第 {0} 阶段</color> 的试炼吗？" } } },
                    { "trial.popup.enter", new Dictionary<string, string> { { "ko-KR", "시련의 공간으로 이동하시겠습니까?\n모든 플레이어가 함께 이동합니다." }, { "en-US", "Enter the Trial space?\nAll players will move together." }, { "ja-JP", "試練の空間へ移動しますか？\n全プレイヤーが一緒に移動します。" }, { "zh-CN", "要进入试炼空间吗？\n所有玩家将一起移动。" } } },
                    { "trial.entrance.interact", new Dictionary<string, string> { { "ko-KR", "시련의 공간으로 이동" }, { "en-US", "Enter Trial Space" }, { "ja-JP", "試練の空間へ移動" }, { "zh-CN", "进入试炼空间" } } },
                    { "trial.return.interact", new Dictionary<string, string> { { "ko-KR", "저장 후 돌아가기" }, { "en-US", "Save and Return" }, { "ja-JP", "保存して戻る" }, { "zh-CN", "保存后返回" } } },
                    { "trial.reward.interact", new Dictionary<string, string> { { "ko-KR", "보상 공간으로 이동" }, { "en-US", "Go to Reward Area" }, { "ja-JP", "報酬エリアへ移動" }, { "zh-CN", "前往奖励区域" } } },
                    { "trial.battle.interact", new Dictionary<string, string> { { "ko-KR", "시련 공간으로 돌아가기" }, { "en-US", "Return to Trial Area" }, { "ja-JP", "試練エリアへ戻る" }, { "zh-CN", "返回试炼区域" } } },
                    { "trial.place.name", new Dictionary<string, string> { { "ko-KR", "시련의 탑" }, { "en-US", "Tower of Trials" }, { "ja-JP", "試練の塔" }, { "zh-CN", "试炼之塔" } } },
                    { "trial.return.confirm", new Dictionary<string, string> { { "ko-KR", "현재 시련 진행 상황을 저장하고 돌아가시겠습니까?\n저장한 시련은 다음 입장 시 이어서 진행할 수 있습니다." }, { "en-US", "Save this Trial run and return?\nYou can continue it the next time you enter." }, { "ja-JP", "現在の試練の進行を保存して戻りますか？\n次回入場時に続きから再開できます。" }, { "zh-CN", "要保存当前试炼进度并返回吗？\n下次进入时可以继续挑战。" } } },
                    { "trial.return.saved", new Dictionary<string, string> { { "ko-KR", "시련 진행 상황을 저장했습니다." }, { "en-US", "Trial progress saved." }, { "ja-JP", "試練の進行を保存しました。" }, { "zh-CN", "已保存试炼进度。" } } },
                    { "trial.return.resumed", new Dictionary<string, string> { { "ko-KR", "저장된 시련을 이어서 시작합니다." }, { "en-US", "Resuming the saved Trial." }, { "ja-JP", "保存された試練を再開します。" }, { "zh-CN", "继续已保存的试炼。" } } },
                    { "trial.tablet.interact", new Dictionary<string, string> { { "ko-KR", "시련 시작하기" }, { "en-US", "Start Trial" }, { "ja-JP", "試練段階を開始" }, { "zh-CN", "开始试炼" } } },
                    { "trial.msg.start", new Dictionary<string, string> { { "ko-KR", "<color=yellow>{0}단계 시련을 시작합니다...</color>" }, { "en-US", "<color=yellow>Starting Phase {0} Trial...</color>" }, { "ja-JP", "<color=yellow>{0}段階の試練を開始します...</color>" }, { "zh-CN", "<color=yellow>开始第 {0} 阶段试炼...</color>" } } },
                    { "trial.msg.clear", new Dictionary<string, string> { { "ko-KR", "시련 <color=red>{0}단계</color> 완료" }, { "en-US", "Phase <color=red>{0}</color> Cleared" }, { "ja-JP", "試練 <color=red>{0}段階</color> 完了" }, { "zh-CN", "试炼 <color=red>第 {0} 阶段</color> 完成" } } },
                    { "trial.ui.text", new Dictionary<string, string> { { "ko-KR", "시련 <color=red>{0}단계</color>" }, { "en-US", "Phase <color=red>{0}</color>" }, { "ja-JP", "試練 <color=red>{0}段階</color>" }, { "zh-CN", "试炼 <color=red>第 {0} 阶段</color>" } } },
                    { "trial.ui.waiting", new Dictionary<string, string> { { "ko-KR", "시련 <color=red>대기</color>" }, { "en-US", "<color=red>Waiting</color> Trial" }, { "ja-JP", "試練 <color=red>待機</color>" }, { "zh-CN", "试炼 <color=red>等待</color>" } } },
                    { "trial.merchant.name", new Dictionary<string, string> { { "ko-KR", "타미" }, { "en-US", "Tami" }, { "ja-JP", "Tami" }, { "zh-CN", "Tami" } } },
                    { "trial.msg.host_only", new Dictionary<string, string> { { "ko-KR", "호스트만 단계를 시작할 수 있습니다." }, { "en-US", "Only the host can start a Trial phase." }, { "ja-JP", "試練の段階はホストのみ開始できます。" }, { "zh-CN", "只有房主可以开始试炼阶段。" } } },
                    { "trial.msg.all_players", new Dictionary<string, string> { { "ko-KR", "모든 플레이어가 시련 공간으로 돌아와야 합니다." }, { "en-US", "All players must return to the Trial area." }, { "ja-JP", "全プレイヤーが試練エリアへ戻る必要があります。" }, { "zh-CN", "所有玩家都必须返回试炼区域。" } } },
                    { "trial.msg.restore_failed", new Dictionary<string, string> { { "ko-KR", "저장된 시련을 복원하지 못했습니다. 저장 슬롯을 확인하세요." }, { "en-US", "Could not restore the saved Trial. Check the save slot." }, { "ja-JP", "保存された試練を復元できませんでした。保存スロットを確認してください。" }, { "zh-CN", "无法恢复已保存的试炼。请检查存档栏位。" } } },
                    { "trial.msg.save_unavailable", new Dictionary<string, string> { { "ko-KR", "시련 저장을 준비하지 못했습니다." }, { "en-US", "Could not prepare the Trial save." }, { "ja-JP", "試練の保存を準備できませんでした。" }, { "zh-CN", "无法准备保存试炼。" } } },
                    { "trial.msg.start_unavailable", new Dictionary<string, string> { { "ko-KR", "시련 준비가 완료되지 않았습니다. 잠시 후 다시 시도하세요." }, { "en-US", "The Trial is not ready. Please try again shortly." }, { "ja-JP", "試練の準備が完了していません。しばらくしてから再試行してください。" }, { "zh-CN", "试炼尚未准备就绪。请稍后重试。" } } },
                    { "trial.msg.entrance_unavailable", new Dictionary<string, string> { { "ko-KR", "시련 입구를 준비하지 못했습니다. 잠시 후 다시 시도하세요." }, { "en-US", "The Trial entrance is not ready. Please try again shortly." }, { "ja-JP", "試練の入口を準備できませんでした。しばらくしてから再試行してください。" }, { "zh-CN", "试炼入口尚未准备就绪。请稍后重试。" } } },
                    { "trial.msg.host_only_enter", new Dictionary<string, string> { { "ko-KR", "호스트만 시련 저장 슬롯을 선택하고 입장할 수 있습니다." }, { "en-US", "Only the host can choose a Trial slot and enter." }, { "ja-JP", "ホストだけが試練スロットを選んで入場できます。" }, { "zh-CN", "只有房主可以选择试炼存档并进入。" } } },
                    { "trial.msg.host_only_return", new Dictionary<string, string> { { "ko-KR", "호스트만 이용할 수 있습니다." }, { "en-US", "Only the host can save and return." }, { "ja-JP", "保存して戻れるのはホストだけです。" }, { "zh-CN", "只有房主可以保存并返回。" } } },
                    { "trial.msg.gather_entrance", new Dictionary<string, string> { { "ko-KR", "모든 플레이어가 시련 입구 포탈 근처에 모여야 합니다." }, { "en-US", "All players must gather near the Trial entrance portal." }, { "ja-JP", "全プレイヤーが試練入口のポータル付近に集まる必要があります。" }, { "zh-CN", "所有玩家必须聚集在试炼入口传送门附近。" } } },
                    { "trial.msg.gather_return", new Dictionary<string, string> { { "ko-KR", "모든 플레이어가 모여야 합니다." }, { "en-US", "All players must gather near the save and return portal." }, { "ja-JP", "全プレイヤーが保存・帰還ポータル付近に集まる必要があります。" }, { "zh-CN", "所有玩家必须聚集在保存返回传送门附近。" } } },
                    { "trial.msg.party_mismatch", new Dictionary<string, string> { { "ko-KR", "저장된 참가자 전원이 모여야 합니다. 새 참가자는 이 시련에 합류할 수 없습니다." }, { "en-US", "All saved players must be present. New players cannot join this Trial." }, { "ja-JP", "保存された参加者全員が必要です。新しい参加者は合流できません。" }, { "zh-CN", "已保存的参与者必须全部到齐。新玩家无法加入此试炼。" } } },
                    { "trial.msg.slot_unavailable", new Dictionary<string, string> { { "ko-KR", "시련 저장 슬롯을 사용할 수 없습니다." }, { "en-US", "This Trial save slot is unavailable." }, { "ja-JP", "この試練保存スロットは利用できません。" }, { "zh-CN", "此试炼存档栏位不可用。" } } },
                    { "trial.msg.slot_deleted", new Dictionary<string, string> { { "ko-KR", "시련 저장 슬롯을 삭제했습니다." }, { "en-US", "Trial save slot deleted." }, { "ja-JP", "試練保存スロットを削除しました。" }, { "zh-CN", "已删除试炼存档栏位。" } } },
                    { "trial.slot.title", new Dictionary<string, string> { { "ko-KR", "시련 저장 슬롯" }, { "en-US", "Trial Save Slots" }, { "ja-JP", "試練保存スロット" }, { "zh-CN", "试炼存档栏位" } } },
                    { "trial.slot.empty", new Dictionary<string, string> { { "ko-KR", "슬롯 {0}: 새 시련" }, { "en-US", "Slot {0}: New Trial" }, { "ja-JP", "スロット {0}: 新しい試練" }, { "zh-CN", "栏位 {0}：新试炼" } } },
                    { "trial.slot.corrupt", new Dictionary<string, string> { { "ko-KR", "슬롯 {0}: 불러올 수 없음" }, { "en-US", "Slot {0}: Cannot load" }, { "ja-JP", "スロット {0}: 読み込めません" }, { "zh-CN", "栏位 {0}：无法读取" } } },
                    { "trial.slot.saved", new Dictionary<string, string> { { "ko-KR", "슬롯 {0}: {1}단계 · {2}" }, { "en-US", "Slot {0}: Phase {1} · {2}" }, { "ja-JP", "スロット {0}: {1}段階 · {2}" }, { "zh-CN", "栏位 {0}：第 {1} 阶段 · {2}" } } },
                    { "trial.slot.select", new Dictionary<string, string> { { "ko-KR", "세이브 선택" }, { "en-US", "Select Save" }, { "ja-JP", "セーブを選択" }, { "zh-CN", "选择存档" } } },
                    { "trial.slot.delete", new Dictionary<string, string> { { "ko-KR", "삭제" }, { "en-US", "Delete" }, { "ja-JP", "削除" }, { "zh-CN", "删除" } } },
                    { "trial.slot.close", new Dictionary<string, string> { { "ko-KR", "닫기" }, { "en-US", "Close" }, { "ja-JP", "閉じる" }, { "zh-CN", "关闭" } } },
                    { "trial.slot.delete_confirm", new Dictionary<string, string> { { "ko-KR", "시련 슬롯 {0}을 삭제하시겠습니까?" }, { "en-US", "Delete Trial slot {0}?" }, { "ja-JP", "試練スロット {0} を削除しますか？" }, { "zh-CN", "删除试炼栏位 {0}？" } } },
                    { "trial.update.prompt", new Dictionary<string, string> { { "ko-KR", "Endless Trial {1} 업데이트가 있습니다. (현재 {0})\n다운로드 후 게임을 종료하고 업데이트를 설치할까요?" }, { "en-US", "Endless Trial {1} is available (installed: {0}).\nDownload it, close the game, and install the update?" }, { "ja-JP", "Endless Trial {1} の更新があります（現在 {0}）。\nダウンロード後にゲームを終了して更新しますか？" }, { "zh-CN", "Endless Trial {1} 有可用更新（当前 {0}）。\n下载后关闭游戏并安装更新吗？" } } },
                    { "trial.update.failed", new Dictionary<string, string> { { "ko-KR", "시련 모드 업데이트 준비에 실패했습니다. 현재 게임을 계속할 수 있습니다." }, { "en-US", "Could not prepare the Trial update. You can keep playing." }, { "ja-JP", "試練モードの更新準備に失敗しました。ゲームは続行できます。" }, { "zh-CN", "试炼模组更新准备失败。你可以继续游戏。" } } }
                };

                foreach (var kp in textData)
                {
                    ctx.AddText(kp.Key, kp.Value);
                }
            };
            HorayModAPI.OnLocalizationReady += _localizationReadyHandler;

            _subscribedLanguageManager = LocalizationManager.Instance;
            if (_subscribedLanguageManager != null)
                _subscribedLanguageManager.OnLanguageChanged += OnTrialLanguageChanged;

            HorayModAPI.OnFloorAllocatedServerside += OnFloorAllocatedServerside;
            HorayModAPI.OnFloorAllocatedClientside += OnFloorAllocatedClientside;
            HorayModAPI.OnStartGameServerside += RestoreTrialCheckpointBeforeNativeLoad;
            HorayModAPI.OnStartSessionServerside += RegisterRestoredTrialFloorPrefabs;

            GameObject bridgeObj = new GameObject("EndlessMod_UpdateBridge");
            UnityEngine.Object.DontDestroyOnLoad(bridgeObj);
            var bridge = bridgeObj.AddComponent<EndlessUpdateBridge>();
            bridge.UpdateAction = OnModUpdate;
            _updateBridge = bridge;

            TrialAutoUpdater.EnsureStarted(metadata.modVersion);

            Debug.Log($"[{metadata.modName}] Loaded successfully.");
        }

        protected override void OnModUnloaded()
        {
            if (!ReferenceEquals(Instance, this)) return;
            TrialAutoUpdater.SuspendUntilNextLoad();
            SetAllPlayersBattleState(false);
            TrialNetworkBridge.Shutdown();
            if (_localizationReadyHandler != null)
                HorayModAPI.OnLocalizationReady -= _localizationReadyHandler;
            HorayModAPI.OnFloorAllocatedServerside -= OnFloorAllocatedServerside;
            HorayModAPI.OnFloorAllocatedClientside -= OnFloorAllocatedClientside;
            HorayModAPI.OnStartGameServerside -= RestoreTrialCheckpointBeforeNativeLoad;
            HorayModAPI.OnStartSessionServerside -= RegisterRestoredTrialFloorPrefabs;
            SceneManager.activeSceneChanged -= OnActiveSceneChanged;
            if (_subscribedLanguageManager != null)
                _subscribedLanguageManager.OnLanguageChanged -= OnTrialLanguageChanged;
            _subscribedLanguageManager = null;

            if (_updateBridge != null)
            {
                _updateBridge.UpdateAction = null;
                UnityEngine.Object.Destroy(_updateBridge.gameObject);
                _updateBridge = null;
            }
            _nextLevelCapRestoreAttemptTime = 0f;
            RestoreNativeLevelCap();
            _trialLevelCapPendingFloorUntil = 0f;
            _trialLevelCapSuppressed = false;
            ResetTrialMusic();
            CleanupTrialUI();
            TrialSaveSlotMenu.CloseOpen();
            ClearTrialLocalRewardClaims();
            CoroutineManager.Clear();
            if (_townPortalPresentationTemplate != null)
                UnityEngine.Object.Destroy(_townPortalPresentationTemplate);
            _townPortalPresentationTemplate = null;
            _hasCachedTownPortalPresentation = false;
            _cachedTownReturnPortal = null;
            _nextTownReturnPortalSearchTime = 0f;
            ActiveTrialMonsters.Clear();
            TrialMonsterRuntimeCaches.Clear();
            SpawnedRewards.Clear();
            ActiveTrialMerchants.Clear();
            ActiveTrialSocialSpawners.Clear();
            ActiveTrialTransferPortals.Clear();
            ActiveTrialTransferPortalFloors.Clear();
            PreparedRewardRoomPhases.Clear();
            PreparedRewardRoomFloorIds.Clear();
            TrialSlotFiles.Clear();
            ActiveTrialPartyGuids.Clear();
            ActiveTrialPartyNames.Clear();
            dbCache = null;
            FilteredTrialMonsterPools.Clear();
            _activeTrialSlot = 0;
            _trialSlotProfile = string.Empty;
            _keepRestoredTrialLobbyOpen = false;
            _nextSavedLobbyRefreshTime = 0f;
            _trialFloorRegistered = false;
            _trialRegisteredDungeon = null;
            _trialEntranceSpawnPending = false;
            _restoreCheckpointPhaseOnFloorAllocation = false;
            _startupCheckpointFloorGuid = null;
            _lastTrackedTablet = null;
            _nextTrialFastTickTime = 0f;
            _nextTrialTabletSearchTime = 0f;
            _trialTabletSearchFloor = null;
            if (_trialRoomInfoHidden && _trialRoomInfoViewer != null &&
                _trialRoomInfoViewer.canvasGroup != null)
                _trialRoomInfoViewer.canvasGroup.alpha = _cachedLocalPlayer != null &&
                    _cachedLocalPlayer.isInDungeon > 0 ? 0f : 1f;
            _cachedLocalPlayer = null;
            _nextLocalPlayerSearchTime = 0f;
            _lastObservedLocalFloorGuid = null;
            _cachedGameOver = false;
            _lastGameOverCheck = 0f;
            _cachedGameOverLabel = null;
            _nextGameOverLabelSearchTime = 0f;
            _lastTrialFloorSeenAt = float.NegativeInfinity;
            _currentTrialGameOverState = GameOverState.None;
            _trialGameOverPlaceActive = false;
            isPopupOpen = false;
            _activeTrialPopup = null;
            _activeTrialPopupKey = null;
            _activeTrialPopupFormatArgs = Array.Empty<object>();
            _activeSystemMessageKey = null;
            _activeSystemMessageRenderedText = null;
            _activeSystemMessagePhase = 0;
            _trialRoomInfoViewer = null;
            _trialRoomInfoHidden = false;
            _nextRoomInfoViewerSearchTime = 0f;
            TrialEntrance = null;
            _trialEntranceFloorGuid = null;
            TrialReturnPortal = null;
            _trialReturnPortalFloorGuid = null;
            TrialTablet = null;
            Instance = null!;
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
            // None of these state checks needs a per-frame update. Keep UI,
            // rewards, music and server boundary maintenance on one 5 Hz tick.
            float now = Time.unscaledTime;
            if (now < _nextTrialFastTickTime) return;
            _nextTrialFastTickTime = now + 0.2f;
            TrialNetworkBridge.EnsureRegistered();

            if (_trialLevelCapActive && !NetworkClient.active && !NetworkServer.active)
                RestoreNativeLevelCap();
            UpdateTrialUIForFloorChange();

            bool trialRelevant = (_cachedLocalPlayer != null &&
                                  IsTrialFloorGuid(_cachedLocalPlayer.currentFloorGuid)) ||
                                 (_lastObservedLocalFloorGuid != null &&
                                  IsTrialFloorGuid(_lastObservedLocalFloorGuid)) ||
                                 now - _lastTrialFloorSeenAt < 3f ||
                                 _trialGameOverPlaceActive ||
                                 _currentTrialGameOverState != GameOverState.None;
            if (trialRelevant)
                UpdateTrialGameOverState();
            if (_trialGameOverPlaceActive)
                UpdateTrialGameOverPlaceName();

            UpdateTrialLocalRewardClaims();
            UpdateTrialMusic();
            MonitorGameWorldObjects();
        }

        private static void MonitorGameWorldObjects()
        {
            EnsureLocalTrialTabletInteraction();
            TrialController.Instance?.RefreshLocalTrialState();
            // Trial monsters are registered at the point of spawning.
            if (!NetworkServer.active) return;
            ReconcileTrialPlayerBattleState();
            KeepSavedTrialLobbyOpenForRejoin();
            RefreshTrialSephiriteRewardObservers();

            for (int i = ActiveTrialMonsters.Count - 1; i >= 0; i--)
            {
                UnitAvatar av = ActiveTrialMonsters[i];
                if (av == null)
                {
                    if (!ReferenceEquals(av, null)) TrialMonsterRuntimeCaches.Remove(av);
                    ActiveTrialMonsters.RemoveAt(i);
                    continue;
                }
                ConstrainTrialMonsterToArena(av);
            }

            // [B] 석판 클라이언트 가시성 세팅 및 동기화 캐싱 방어 조건 고도화
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

        public static void ClearTrackedTrialMonsters()
        {
            ActiveTrialMonsters.Clear();
            TrialMonsterRuntimeCaches.Clear();
        }

        private static void RequestTrialEntranceSpawn()
        {
            if (!NetworkServer.active || TrialEntrance != null || _trialEntranceSpawnPending) return;
            _trialEntranceSpawnPending = true;
            Debug.Log("[시련] MultiZone 입구 포탈 생성 대기 시작");
            CoroutineManager.Instance.StartCoroutine(SpawnTrialEntranceAfterMultiZoneAllocated());
        }

        private static IEnumerator SpawnTrialEntranceAfterMultiZoneAllocated()
        {
            // MultiZone의 귀환 포탈이 생성된 뒤에만 그 옆의 배치 위치를 확인한다.
            // 시련 포탈 자체는 번들 안의 완전히 독립된 프리팹으로 생성한다.
            const int maxAttempts = 40;
            WaitForSeconds portalRetryDelay = new WaitForSeconds(0.25f);
            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                if (TrialEntrance != null)
                {
                    _trialEntranceSpawnPending = false;
                    yield break;
                }

                Interactable? townPortal = FindTownReturnPortal();
                if (townPortal != null)
                {
                    EnsureTrialEntrance(townPortal);
                    if (TrialEntrance != null)
                    {
                        _trialEntranceSpawnPending = false;
                        yield break;
                    }
                }

                yield return portalRetryDelay;
            }

            Debug.LogError("[Sephiria Endless Trial] MultiZone의 마을 귀환 포탈을 찾지 못해 시련 포탈을 생성하지 못했습니다.");
            _trialEntranceSpawnPending = false;
        }

        internal static Interactable? FindTownReturnPortal()
        {
            // The MultiZone portal is SteamPortal/NetConnectedPortal. Find it
            // on the MultiZone floor before considering older portal variants.
            FloorData? multiZone = DungeonManager.Instance?.FindFloorByName("MultiZone");
            if (_cachedTownReturnPortal != null && _cachedTownReturnPortal.enabled &&
                _cachedTownReturnPortal.gameObject.activeInHierarchy &&
                IsInteractableOnMultiZone(_cachedTownReturnPortal, multiZone))
                return _cachedTownReturnPortal;
            if (Time.unscaledTime < _nextTownReturnPortalSearchTime) return null;
            _nextTownReturnPortalSearchTime = Time.unscaledTime + 1f;

            foreach (NetConnectedPortal portal in UnityEngine.Object.FindObjectsByType<NetConnectedPortal>(FindObjectsSortMode.None))
            {
                if (portal == null || !portal.gameObject.activeInHierarchy ||
                    portal.direction != NetConnectedPortal.EDirection.GoToMyTown ||
                    !(portal.GetComponentInParent<FloorGenerator>() is FloorGenerator floor) ||
                    (multiZone == null ? !floor.name.StartsWith("MultiZone", StringComparison.OrdinalIgnoreCase)
                                       : floor.guid != multiZone.guid))
                    continue;
                if (portal.TryGetComponent(out Interactable nativeInteraction))
                {
                    _cachedTownReturnPortal = nativeInteraction;
                    return nativeInteraction;
                }
            }

            // Preserve compatibility with other versions, without selecting a
            // portal on an unrelated floor that happens to remain loaded.
            Interactable[] interactables = UnityEngine.Object.FindObjectsByType<Interactable>(FindObjectsSortMode.None);
            foreach (Interactable interactable in interactables)
            {
                // Visual-only child copies retain the hierarchy but are disabled.
                if (interactable == null || !interactable.enabled || !interactable.gameObject.activeInHierarchy ||
                    !IsInteractableOnMultiZone(interactable, multiZone))
                    continue;
                string text = interactable.GetInteractionString() ?? string.Empty;
                if (text.IndexOf("마을로 돌아가기", StringComparison.OrdinalIgnoreCase) < 0 &&
                    text.IndexOf("Return to Town", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                _cachedTownReturnPortal = interactable;
                return interactable;
            }
            foreach (Interactable interactable in interactables)
            {
                if (interactable == null || !interactable.enabled || !interactable.gameObject.activeInHierarchy ||
                    !IsInteractableOnMultiZone(interactable, multiZone))
                    continue;
                PortalToAnotherFloor? portal = interactable.GetComponent<PortalToAnotherFloor>();
                if (portal != null &&
                    (portal.floorName.IndexOf("town", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     portal.floorName.IndexOf("Rabbittown", StringComparison.OrdinalIgnoreCase) >= 0) &&
                    interactable.GetComponentInChildren<SpriteRenderer>() != null)
                {
                    _cachedTownReturnPortal = interactable;
                    return interactable;
                }
            }
            return null;
        }

        private static bool IsInteractableOnMultiZone(Interactable interactable, FloorData? multiZone)
        {
            FloorGenerator? floor = interactable.GetComponentInParent<FloorGenerator>();
            return floor != null && (multiZone == null
                ? floor.name.StartsWith("MultiZone", StringComparison.OrdinalIgnoreCase)
                : floor.guid == multiZone.guid);
        }

        private static TrialController? EnsureTrialController()
        {
            if (TrialController.Instance != null)
                return TrialController.Instance;
            if (!NetworkServer.active)
                return null;

            // DungeonManager can destroy all network objects after GameOver while
            // a C# static field still points to the now-destroyed controller.
            // Always recreate it for the new server session before a portal or
            // tablet attempts to call a Command on it.
            if (!EnsureRuntimeTrialControllerPrefab() || _trialControllerPrefab == null)
            {
                Debug.LogError("[Sephiria Endless Trial] 네트워크 시련 컨트롤러 프리팹을 준비하지 못했습니다.");
                return null;
            }

            GameObject ctrlObj = UnityEngine.Object.Instantiate(_trialControllerPrefab);
            ctrlObj.name = "TrialController";
            ctrlObj.SetActive(true);
            TrialController controller = ctrlObj.GetComponent<TrialController>();
            NetworkServer.Spawn(ctrlObj);
            Debug.Log("[Sephiria Endless Trial] 새 시련 컨트롤러를 생성했습니다.");
            return controller;
        }

        private static void EnsureTrialEntrance(Interactable townPortal)
        {
            if (!NetworkServer.active || TrialEntrance != null) return;
            if (EnsureTrialController() == null) return;
            if (!TryGetTrialEntrancePortalPrefab(out GameObject portalPrefab)) return;

            // The town portal is used only as a convenient, reliable position
            // marker.  No town-portal GameObject, component, event, or listener
            // is copied, so its "return to town" confirmation can never appear.
            Vector3 finalSpawnPos = townPortal.transform.position + Vector3.left * 6f;
            TrialEntrance = UnityEngine.Object.Instantiate(portalPrefab, finalSpawnPos, Quaternion.identity);
            TrialEntrance.name = "Trial_Entrance_Portal";
            // The copied SpriteRenderer transforms below already use the town
            // portal's world scale.  Scaling this root again made the two
            // portals different sizes, so keep it at one.
            TrialEntrance.transform.localScale = Vector3.one;
            // The registered template is intentionally inactive so its
            // NetworkIdentity has never been initialized.  Activate only the
            // live server copy immediately before it is spawned.
            TrialEntrance.SetActive(true);

            Interactable? it = TrialEntrance.GetComponent<Interactable>();
            if (it == null)
            {
                Debug.LogError("[Sephiria Endless Trial] 전용 시련 포탈 프리팹에 Interactable이 없습니다.");
                UnityEngine.Object.Destroy(TrialEntrance);
                TrialEntrance = null;
                return;
            }

            CopyTownPortalPresentation(townPortal, TrialEntrance);
            ConfigureTrialEntranceInteraction(it);
            NetworkServer.Spawn(TrialEntrance);

            FloorGenerator? fg = townPortal.GetComponentInParent<FloorGenerator>() ?? UnityEngine.Object.FindFirstObjectByType<FloorGenerator>();
            if (fg != null)
            {
                _trialEntranceFloorGuid = fg.guid;
                fg.floorRelatedNetworkObjects.Add(TrialEntrance);
            }
            Debug.Log($"[시련] MultiZone 입구 포탈 생성: floor={_trialEntranceFloorGuid}, position={finalSpawnPos}");
        }

        internal static void ConfigureTrialEntranceInteraction(Interactable interactable)
        {
            FieldInfo? interactionHandler = typeof(Interactable).GetField("OnInteraction", BindingFlags.NonPublic | BindingFlags.Instance);
            interactionHandler?.SetValue(interactable, null);
            interactable.DoInteraction?.RemoveAllListeners();
            interactable.enabled = true;
            interactable.OnInteraction += (actor) => ShowTrialEntrancePopup();

            interactable.interactionDescription = new LocalizedString("trial.entrance.interact");
        }

        internal static void ConfigureTrialReturnInteraction(Interactable interactable)
        {
            ConfigureTrialPortalInteraction(interactable, 0);
        }

        internal static void ConfigureTrialPortalInteraction(Interactable interactable, byte route)
        {
            FieldInfo? interactionHandler = typeof(Interactable).GetField("OnInteraction", BindingFlags.NonPublic | BindingFlags.Instance);
            interactionHandler?.SetValue(interactable, null);
            interactable.DoInteraction?.RemoveAllListeners();
            interactable.enabled = true;
            if (route == 1)
            {
                interactable.OnInteraction += (actor) => RequestMoveToRewardFloor(actor);
                interactable.interactionDescription = new LocalizedString("trial.reward.interact");
            }
            else if (route == 2)
            {
                interactable.OnInteraction += (actor) => RequestMoveToBattleFloor(actor);
                interactable.interactionDescription = new LocalizedString("trial.battle.interact");
            }
            else
            {
                interactable.OnInteraction += (actor) => ShowTrialSaveReturnPopup();
                interactable.interactionDescription = new LocalizedString("trial.return.interact");
            }
            Debug.Log($"[시련] 포탈 상호작용 연결: route={route}, object={interactable.gameObject.name}, server={NetworkServer.active}");
        }

        // Copy the complete visual hierarchy from the return portal.  The stock
        // PortalToAnotherFloor prefab is not merely a SpriteRenderer: its root
        // Animator drives the 9-sliced portal art and a child Light 2D supplies
        // the large glow.  Copying only the SpriteRenderer made the trial portal
        // look like a much smaller, unlit square.
        //
        // The copied hierarchy is visual-only.  Its Interactable, colliders and
        // portal scripts are disabled, so only the independently-created parent
        // can receive input and open the trial confirmation.
        internal static void CopyTownPortalPresentation(Interactable townPortal, GameObject trialPortal)
        {
            if (townPortal == null || trialPortal == null) return;

            CacheTownPortalPresentation(townPortal);

            Transform? oldVisual = trialPortal.transform.Find("TownPortalVisual");
            if (oldVisual != null)
            {
                UnityEngine.Object.Destroy(oldVisual.gameObject);
            }

            GameObject visualRoot = UnityEngine.Object.Instantiate(townPortal.gameObject);
            visualRoot.name = "TownPortalVisual";
            visualRoot.SetActive(false);
            visualRoot.transform.SetParent(trialPortal.transform, false);
            visualRoot.transform.localPosition = Vector3.zero;
            visualRoot.transform.localRotation = townPortal.transform.rotation;
            // The networked trial root intentionally remains at (1,1,1).  Use
            // the original portal's final scale here so nested art, animation and
            // Light 2D retain precisely the same world-space proportions.
            visualRoot.transform.localScale = townPortal.transform.lossyScale;

            DisableCopiedPortalBehaviour(visualRoot);
            ConfigureTrialPortalVisualSorting(visualRoot);

            visualRoot.SetActive(true);

            Interactable? targetInteractable = trialPortal.GetComponent<Interactable>();
            if (targetInteractable != null)
            {
                targetInteractable.hideGuide = townPortal.hideGuide;
                targetInteractable.interactionType = townPortal.interactionType;
                targetInteractable.delayTime = townPortal.delayTime;
                targetInteractable.interactOrder = townPortal.interactOrder;
                // Height controls the interaction prompt's world position.
                targetInteractable.applyCustomHeight = true;
                targetInteractable.customHeight = townPortal.Height;
            }

            BoxCollider2D? targetCollider = trialPortal.GetComponent<BoxCollider2D>();
            Collider2D? sourceCollider = townPortal.GetComponentsInChildren<Collider2D>(true)
                .FirstOrDefault(collider => collider != null && collider.enabled);
            if (targetCollider != null && sourceCollider != null)
            {
                // The runtime portal root deliberately has scale (1,1,1), so a
                // world-space bounds copy gives it the exact same trigger area.
                Bounds bounds = sourceCollider.bounds;
                targetCollider.offset = bounds.center - townPortal.transform.position;
                targetCollider.size = bounds.size;
                targetCollider.isTrigger = true;
            }
        }

        private static void CacheTownPortalPresentation(Interactable townPortal)
        {
            if (_hasCachedTownPortalPresentation || townPortal == null) return;

            Collider2D? sourceCollider = townPortal.GetComponentsInChildren<Collider2D>(true)
                .FirstOrDefault(collider => collider != null && collider.enabled);
            if (sourceCollider != null)
            {
                Bounds bounds = sourceCollider.bounds;
                _townPortalColliderOffset = bounds.center - townPortal.transform.position;
                _townPortalColliderSize = bounds.size;
            }

            GameObject template = UnityEngine.Object.Instantiate(townPortal.gameObject);
            template.name = "Trial_TownPortalVisual_Template";
            template.SetActive(false);
            template.transform.position = new Vector3(100000f, 100000f, 0f);
            template.transform.rotation = townPortal.transform.rotation;
            template.transform.localScale = townPortal.transform.lossyScale;
            DisableCopiedPortalBehaviour(template);
            UnityEngine.Object.DontDestroyOnLoad(template);
            _townPortalPresentationTemplate = template;
            _hasCachedTownPortalPresentation = true;
        }

        internal static bool CopyCachedTownPortalPresentation(GameObject trialPortal)
        {
            if (!_hasCachedTownPortalPresentation || _townPortalPresentationTemplate == null || trialPortal == null)
                return false;

            Transform? oldVisual = trialPortal.transform.Find("TownPortalVisual");
            if (oldVisual != null) UnityEngine.Object.Destroy(oldVisual.gameObject);

            GameObject visualRoot = UnityEngine.Object.Instantiate(_townPortalPresentationTemplate);
            visualRoot.name = "TownPortalVisual";
            visualRoot.SetActive(false);
            visualRoot.transform.SetParent(trialPortal.transform, false);
            visualRoot.transform.localPosition = Vector3.zero;
            visualRoot.transform.localRotation = Quaternion.identity;
            DisableCopiedPortalBehaviour(visualRoot);
            visualRoot.SetActive(true);
            ConfigureTrialPortalVisualSorting(visualRoot);

            BoxCollider2D? collider = trialPortal.GetComponent<BoxCollider2D>();
            if (collider != null && _townPortalColliderSize != Vector2.zero)
            {
                collider.offset = _townPortalColliderOffset;
                collider.size = _townPortalColliderSize;
                collider.isTrigger = true;
            }
            return true;
        }

        private static void ConfigureTrialPortalVisualSorting(GameObject visualRoot)
        {
            int defaultLayerId = SortingLayer.NameToID("Default");
            int defaultLayerValue = SortingLayer.GetLayerValueFromID(defaultLayerId);
            const int portalSortingOrder = 4;

            foreach (UnityEngine.Rendering.SortingGroup group in
                     visualRoot.GetComponentsInChildren<UnityEngine.Rendering.SortingGroup>(true))
            {
                if (SortingLayer.GetLayerValueFromID(group.sortingLayerID) < defaultLayerValue)
                    group.sortingLayerID = defaultLayerId;
                if (group.sortingOrder < portalSortingOrder)
                    group.sortingOrder = portalSortingOrder;
            }

            foreach (Renderer renderer in visualRoot.GetComponentsInChildren<Renderer>(true))
            {
                if (SortingLayer.GetLayerValueFromID(renderer.sortingLayerID) < defaultLayerValue)
                    renderer.sortingLayerID = defaultLayerId;
                if (renderer.sortingOrder < portalSortingOrder)
                    renderer.sortingOrder = portalSortingOrder;
            }
        }

        private static void DisableCopiedPortalBehaviour(GameObject visualRoot)
        {
            foreach (Collider2D collider in visualRoot.GetComponentsInChildren<Collider2D>(true))
                collider.enabled = false;

            foreach (MonoBehaviour behaviour in visualRoot.GetComponentsInChildren<MonoBehaviour>(true))
            {
                string typeName = behaviour.GetType().Name;
                if (behaviour is Interactable || behaviour is NetworkBehaviour ||
                    typeName.Equals("PortalToAnotherFloor", StringComparison.Ordinal) ||
                    typeName.Equals("PortalToGrasstown", StringComparison.Ordinal) ||
                    typeName.IndexOf("StudioEventEmitter", StringComparison.Ordinal) >= 0)
                {
                    behaviour.enabled = false;
                }
            }
        }

        public static bool MoveAllPlayersToTrialFloor()
        {
            if (!NetworkServer.active || DungeonManager.Instance == null || !EnsureTrialFloorRegistered())
            {
                Debug.LogError("[시련] 새 시련 층을 서버 던전 생성기에 등록하지 못했습니다.");
                return false;
            }

            string targetFloorGuid = GetBattleFloorGuidForPhase(TrialController.Instance?.CurrentPhase ?? 1);
            // The destination floor has not been allocated yet on first entry,
            // so TrialPlayerSpawnPosition still contains the old (0, -5)
            // fallback. Read the spawn marker from the registered bundle
            // prefab and add the destination FloorData's world offset now.
            TrialPlayerSpawnPosition = GetFloorMarkerPosition(
                targetFloorGuid, "PlayerSpawn", new Vector3(float.NaN, float.NaN, 0f));
            if (float.IsNaN(TrialPlayerSpawnPosition.x) || float.IsNaN(TrialPlayerSpawnPosition.y))
            {
                Debug.LogError("[Sephiria Endless Trial] 목적 층의 PlayerSpawn 표식을 찾지 못해 잘못된 좌표로 이동하지 않도록 입장을 중단했습니다: " + targetFloorGuid);
                return false;
            }

            // Remove an old stage label before every peer begins its loading
            // transition.  The target floor recreates it from DisplayPhase.
            CleanupTrialUI();

            SaveManager.CurrentRun?.SetBool(TrialWitchHatPendingKey, value: false);
            SaveManager.CurrentRun?.SetInt(TrialWitchHatLastRollPhaseKey, 0);

            // Use the same run state transition as DungeonManager.LoadStageAndMove.
            StartNativeTrialRun(resumingSavedRun: false);

            foreach (NetworkConnectionToClient connection in NetworkServer.connections.Values)
            {
                if (connection == null || connection.identity == null) continue;

                PlayerAvatar? avatar = connection.identity.GetComponent<PlayerAvatar>();
                if (avatar == null) continue;

                DungeonManager.Instance.MoveFloor(avatar, targetFloorGuid, "endless_trial_spawn", 0,
                    recordHistory: true, allowSave: true, keepPrevFloor: false,
                    randomPosition: false, leaveTrainingSchool: false,
                    overridePosition: TrialPlayerSpawnPosition);
            }
            Debug.Log($"[시련] 새 시련 층 이동 요청 완료: floor={targetFloorGuid}, phase={TrialController.Instance?.CurrentPhase ?? 1}");
            return true;
        }

        private static void StartNativeTrialRun(bool resumingSavedRun)
        {
            DungeonManager? dungeon = DungeonManager.Instance;
            if (!NetworkServer.active || dungeon == null) return;

            _trialLevelCapSuppressed = false;
            if (EnableTrialLevelCap())
                _trialLevelCapPendingFloorUntil = Time.unscaledTime + 30f;
            bool wasRunStarted = dungeon.isRunStarted || resumingSavedRun;
            dungeon.dungeonEnvironment["IsInDungeon"] = 1;
            foreach (NetworkConnectionToClient connection in NetworkServer.connections.Values)
            {
                PlayerAvatar? avatar = connection?.identity != null
                    ? connection.identity.GetComponent<PlayerAvatar>() : null;
                if (avatar == null) continue;
                if (avatar.spawner != null)
                    avatar.spawner.gotoMySessionOnGameOverServerside = 0;
                avatar.NetworkisInDungeon = 1;
            }

            _dungeonRunStartRpcMethod ??= typeof(DungeonManager).GetMethod(
                "RpcRunStart", BindingFlags.NonPublic | BindingFlags.Instance);
            if (_dungeonRunStartRpcMethod != null)
                _dungeonRunStartRpcMethod.Invoke(dungeon, new object[] { wasRunStarted });
            else
                Debug.LogError("[Sephiria Endless Trial] DungeonManager.RpcRunStart를 찾지 못했습니다.");

            SaveManager.CurrentRun?.SetBool("RunStarted", value: true);
            dungeon.NetworkisRunStarted = true;

            _keepRestoredTrialLobbyOpen = false;
            SyncTrialRejoinWhitelist();
            LockTrialLobbyLikeNativeRun();
        }

        private static void SyncTrialRejoinWhitelist()
        {
            if (!(NetworkManager.singleton is HorayNetworkManager manager)) return;
            manager.ClearRejoinWhitelist();
            foreach (string guid in ActiveTrialPartyGuids)
                manager.AddToRejoinWhitelist(guid);
        }

        private static void KeepSavedTrialLobbyOpenForRejoin()
        {
            if (!_keepRestoredTrialLobbyOpen) return;
            if (Time.unscaledTime < _nextSavedLobbyRefreshTime) return;
            _nextSavedLobbyRefreshTime = Time.unscaledTime + 1f;
            if (DungeonManager.Instance != null)
                DungeonManager.Instance.dungeonEnvironment["IsInDungeon"] = 1;
            GameObject steamManager = SingletonObject.Find("SteamManager");
            if (steamManager == null || !App.Initialized ||
                !steamManager.TryGetComponent<LobbyManager>(out var lobbyManager) ||
                !lobbyManager.HasLobby || !lobbyManager.Lobby.IsOwner) return;
            LobbyData lobby = lobbyManager.Lobby;
            if (lobby["pw"] != SteamInvitation.LobbyPasswordOpenValue)
                lobby["pw"] = SteamInvitation.LobbyPasswordOpenValue;
        }

        private static void LockTrialLobbyLikeNativeRun()
        {
            // Match DungeonManager.LoadStageAndMove: a run starts by locking
            // the Steam lobby and closing the lobby creation phase.
            GameObject steamManager = SingletonObject.Find("SteamManager");
            if (steamManager != null && App.Initialized &&
                steamManager.TryGetComponent<LobbyManager>(out var lobbyManager) && lobbyManager.HasLobby)
            {
                LobbyData lobby = lobbyManager.Lobby;
                string password = lobby["pw"] ?? string.Empty;
                if (!SteamInvitation.IsPasswordLocked(password))
                    lobby["pw"] = UnityEngine.Random.Range(100000, 1000000).ToString();
            }
            if (DungeonManager.Instance != null)
                DungeonManager.Instance.NetworklobbyCreatedPhase = 0;
        }

        private static void UnlockTrialLobbyAfterSavedReturn()
        {
            _keepRestoredTrialLobbyOpen = false;
            // HorayNetworkManager.RestartGameCoroutine reopens the lobby after
            // an ordinary run. The save-return path restarts players directly.
            GameObject steamManager = SingletonObject.Find("SteamManager");
            if (steamManager != null && App.Initialized &&
                steamManager.TryGetComponent<LobbyManager>(out var lobbyManager) && lobbyManager.HasLobby &&
                lobbyManager.Lobby.IsOwner)
            {
                LobbyData lobby = lobbyManager.Lobby;
                lobby["pw"] = SteamInvitation.LobbyPasswordOpenValue;
            }
        }

        private static bool EnsureTrialFloorRegistered()
        {
            DungeonManager? dungeon = DungeonManager.Instance;
            if (dungeon == null) return false;
            if (_trialFloorRegistered && ReferenceEquals(_trialRegisteredDungeon, dungeon) &&
                TrialFloorWorldOrder.All(guid => dungeon.generatedFloors.ContainsKey(guid))) return true;
            FloorData? source = GetFloorData(UnityEngine.Object.FindFirstObjectByType<FloorGenerator>());
            if (source == null)
            {
                Debug.LogError("[Sephiria Endless Trial] 현재 플로어의 기본 데이터를 찾지 못했습니다.");
                return false;
            }
            _addToGeneratedFloorMethod ??= typeof(DungeonManager).GetMethod("AddToGeneratedFloor", BindingFlags.NonPublic | BindingFlags.Instance);
            if (_addToGeneratedFloorMethod == null)
            {
                Debug.LogError("[Sephiria Endless Trial] DungeonManager.AddToGeneratedFloor을 찾지 못했습니다.");
                return false;
            }

            int trialWorldRow = GetTrialFloorWorldRow(dungeon.generatedFloors.Values);
            if (SaveManager.CurrentRun == null ||
                !NormalizeTrialFloorCoordinatesInSave(SaveManager.CurrentRun, trialWorldRow)) return false;

            for (int i = 0; i < TrialFloorWorldOrder.Length; i++)
            {
                string guid = TrialFloorWorldOrder[i];
                int worldX = i + 1;
                if (dungeon.generatedFloors.TryGetValue(guid, out FloorData existing))
                {
                    existing.globalX = worldX;
                    existing.globalY = trialWorldRow;
                    continue;
                }
                if (!TryRegisterBundledTrialFloorPrefab(guid, out uint prefabAssetId)) return false;

                FloorData data = new FloorData
                {
                    guid = guid,
                    name = guid == TrialFloorGuid ? TrialFloorName : "Endless Trial " + guid,
                    stageName = source.stageName,
                    prefabAssetId = prefabAssetId,
                    difficulty = source.difficulty,
                    seed = UnityEngine.Random.Range(int.MinValue, int.MaxValue),
                    globalX = worldX,
                    globalY = trialWorldRow,
                    connectionToOtherFloors = Array.Empty<string>(),
                    pocketDimension = true,
                    randomRoomCount = 1
                };
                _addToGeneratedFloorMethod.Invoke(dungeon, new object[] { data });
            }
            _trialFloorRegistered = dungeon.generatedFloors.ContainsKey(TrialFloorGuid);
            _trialRegisteredDungeon = _trialFloorRegistered ? dungeon : null;
            return _trialFloorRegistered;
        }

        private static int GetTrialFloorWorldRow(IEnumerable<FloorData> floors)
        {
            int lowestNativeRow = 0;
            foreach (FloorData floor in floors)
            {
                if (floor != null && !IsTrialFloorGuid(floor.guid))
                    lowestNativeRow = Math.Min(lowestNativeRow, floor.globalY);
            }
            return lowestNativeRow - 2;
        }

        private static bool NormalizeTrialFloorCoordinatesInSave(SaveData run, int? worldRow = null)
        {
            try
            {
                int floorCount = run.GetInt("FloorCount", 0);
                var savedFloors = new List<KeyValuePair<int, FloorData>>();
                for (int i = 0; i < floorCount; i++)
                {
                    string json = run.GetString($"Floor{i}", string.Empty);
                    if (string.IsNullOrWhiteSpace(json)) continue;
                    FloorData? floor = JsonConvert.DeserializeObject<FloorData>(json);
                    if (floor != null) savedFloors.Add(new KeyValuePair<int, FloorData>(i, floor));
                }

                int row = worldRow ?? GetTrialFloorWorldRow(savedFloors.Select(entry => entry.Value));
                foreach (KeyValuePair<int, FloorData> entry in savedFloors)
                {
                    int index = Array.IndexOf(TrialFloorWorldOrder, entry.Value.guid);
                    if (index < 0 || (entry.Value.globalX == index + 1 && entry.Value.globalY == row)) continue;
                    entry.Value.globalX = index + 1;
                    entry.Value.globalY = row;
                    run.SetString($"Floor{entry.Key}", JsonConvert.SerializeObject(entry.Value));
                    Debug.Log($"[시련] 저장된 층 좌표 분리: {entry.Value.guid} -> ({entry.Value.globalX}, {row})");
                }
                return true;
            }
            catch (Exception exception)
            {
                Debug.LogError("[시련] 저장된 층 좌표를 분리하지 못했습니다: " + exception);
                return false;
            }
        }

        public static string GetBattleFloorGuidForPhase(int phase)
        {
            if (phase >= 101) return TrialBattle101FloorGuid;
            if (phase >= 51) return TrialBattle51FloorGuid;
            if (phase >= 41) return TrialBattle41FloorGuid;
            if (phase >= 21) return TrialBattle21FloorGuid;
            return TrialFloorGuid;
        }

        public static string GetRewardFloorGuidForPhase(int phase)
        {
            if (phase >= 101) return TrialReward101FloorGuid;
            if (phase >= 51) return TrialReward51FloorGuid;
            if (phase >= 41) return TrialReward41FloorGuid;
            if (phase >= 21) return TrialReward21FloorGuid;
            return TrialReward01FloorGuid;
        }

        public static bool IsTrialBattleFloorGuid(string guid)
        {
            return guid == TrialFloorGuid || guid == TrialBattle21FloorGuid || guid == TrialBattle41FloorGuid ||
                guid == TrialBattle51FloorGuid || guid == TrialBattle101FloorGuid;
        }

        public static bool IsTrialRewardFloorGuid(string guid)
        {
            return guid == TrialReward01FloorGuid || guid == TrialReward21FloorGuid || guid == TrialReward41FloorGuid ||
                guid == TrialReward51FloorGuid || guid == TrialReward101FloorGuid;
        }

        public static bool IsTrialFloorGuid(string guid) => IsTrialBattleFloorGuid(guid) || IsTrialRewardFloorGuid(guid);

        // LevelController's native level cap is the length of this one shared
        // experience table. Its first 30 entries stay exactly as shipped by the
        // game; only Trial play receives additional thresholds.
        private static bool EnableTrialLevelCap()
        {
            if (_trialLevelCapActive) return true;
            if (_trialLevelCapUnavailable) return false;
            try
            {
                _levelExpTableField ??= typeof(LevelController).GetField(
                    nameof(LevelController.ExpTableByLevel), BindingFlags.Public | BindingFlags.Static);
                if (_levelExpTableField == null)
                    throw new MissingFieldException(nameof(LevelController), nameof(LevelController.ExpTableByLevel));

                _nativeLevelExpTable ??= LevelController.ExpTableByLevel;
                if (_nativeLevelExpTable.Length < 2)
                    throw new InvalidOperationException("원본 경험치 표가 비어 있습니다.");

                if (_trialLevelExpTable == null)
                {
                    int last = _nativeLevelExpTable.Length - 1;
                    long increment = (long)_nativeLevelExpTable[last] - _nativeLevelExpTable[last - 1];
                    if (increment <= 0)
                        throw new InvalidOperationException("원본 경험치 표의 마지막 증가량이 올바르지 않습니다.");

                    long safeLevelCount = _nativeLevelExpTable.Length +
                        (int.MaxValue - (long)_nativeLevelExpTable[last]) / increment;
                    int levelCount = (int)Math.Min(TrialMaxLevel, safeLevelCount);
                    if (levelCount <= _nativeLevelExpTable.Length)
                        throw new InvalidOperationException("확장할 수 있는 경험치 범위가 없습니다.");

                    _trialLevelExpTable = new int[levelCount];
                    Array.Copy(_nativeLevelExpTable, _trialLevelExpTable, _nativeLevelExpTable.Length);
                    for (int level = _nativeLevelExpTable.Length; level < levelCount; level++)
                        _trialLevelExpTable[level] = (int)(_trialLevelExpTable[level - 1] + increment);
                }

                _levelExpTableField.SetValue(null, _trialLevelExpTable);
                if (!ReferenceEquals(LevelController.ExpTableByLevel, _trialLevelExpTable))
                    throw new InvalidOperationException("확장한 경험치 표가 적용되지 않았습니다.");
                _trialLevelCapActive = true;
                Debug.Log($"[시련] 시련 전용 최대 레벨 적용: {_trialLevelExpTable.Length}");
                return true;
            }
            catch (Exception exception)
            {
                _trialLevelCapUnavailable = true;
                Debug.LogError("[시련] 시련 전용 경험치 표를 적용할 수 없습니다: " + exception);
                return false;
            }
        }

        private static void RestoreNativeLevelCap()
        {
            if (!_trialLevelCapActive || _levelExpTableField == null || _nativeLevelExpTable == null) return;
            if (Time.unscaledTime < _nextLevelCapRestoreAttemptTime) return;
            try
            {
                _levelExpTableField.SetValue(null, _nativeLevelExpTable);
                if (!ReferenceEquals(LevelController.ExpTableByLevel, _nativeLevelExpTable))
                    throw new InvalidOperationException("원본 경험치 표가 복원되지 않았습니다.");
                _trialLevelCapActive = false;
                _nextLevelCapRestoreAttemptTime = 0f;
                Debug.Log($"[시련] 원본 최대 레벨 복원: {_nativeLevelExpTable.Length}");
            }
            catch (Exception exception)
            {
                _nextLevelCapRestoreAttemptTime = Time.unscaledTime + 5f;
                Debug.LogError("[시련] 원본 경험치 표 복원 실패: " + exception);
            }
        }

        private static void UpdateTrialLevelCapForLocalFloor(string currentGuid)
        {
            if (IsTrialFloorGuid(currentGuid))
            {
                _trialLevelCapPendingFloorUntil = 0f;
                if (!_trialLevelCapSuppressed) EnableTrialLevelCap();
            }
            else
            {
                // The native load can briefly report the previous floor while a
                // saved Trial character is being initialized on the server.
                if (NetworkServer.active && Time.unscaledTime < _trialLevelCapPendingFloorUntil)
                    return;
                _trialLevelCapPendingFloorUntil = 0f;
                _trialLevelCapSuppressed = false;
                RestoreNativeLevelCap();
            }
        }

        private static string TrialFloorBundlePath
        {
            get
            {
                string? assemblyDirectory = Path.GetDirectoryName(typeof(EndlessMod).Assembly.Location);
                return Path.Combine(assemblyDirectory ?? string.Empty, TrialFloorBundleFileName);
            }
        }

        private static bool TryLoadTrialFloorBundle()
        {
            if (_trialFloorBundleLoadAttempted) return _trialFloorBundleAvailable;
            _trialFloorBundleLoadAttempted = true;

            string path = TrialFloorBundlePath;
            if (!File.Exists(path))
            {
                Debug.LogWarning("[Sephiria Endless Trial] 시련 층 AssetBundle을 찾지 못했습니다: " + path +
                    "\nUnity에서 Trial Mod > Build > Build Endless Trial AssetBundle을 실행한 뒤, 'endless_trial_floor' 파일을 이 DLL 옆에 복사하세요.");
                return false;
            }

            try
            {
                _trialFloorBundle = AssetBundle.LoadFromFile(path);
                if (_trialFloorBundle == null)
                {
                    Debug.LogError("[Sephiria Endless Trial] AssetBundle을 열지 못했습니다: " + path);
                    return false;
                }

                _trialFloorPrefab = _trialFloorBundle.LoadAsset<GameObject>(TrialFloorPrefabName);
                if (_trialFloorPrefab == null)
                {
                    Debug.LogError("[Sephiria Endless Trial] 번들 안에서 프리팹 '" + TrialFloorPrefabName + "'을 찾지 못했습니다.");
                    return false;
                }

                if (_trialFloorPrefab.GetComponent<TileFloorGenerator>() == null)
                {
                    Debug.LogError("[Sephiria Endless Trial] 번들 층은 원본 TileFloorGenerator를 사용해야 합니다.");
                    return false;
                }

                NetworkIdentity? identity = _trialFloorPrefab.GetComponent<NetworkIdentity>();
                if (identity == null || identity.assetId == 0)
                {
                    Debug.LogError("[Sephiria Endless Trial] 번들 프리팹에 유효한 NetworkIdentity.assetId가 없습니다.");
                    return false;
                }

                _trialFloorAssetId = identity.assetId;
                if (!NetworkClient.prefabs.ContainsKey(_trialFloorAssetId))
                    NetworkClient.RegisterPrefab(_trialFloorPrefab);

                _trialFloorPrefabs.Clear();
                _trialFloorAssetIds.Clear();
                _trialFloorLayoutXml.Clear();
                foreach (KeyValuePair<string, string> definition in TrialFloorPrefabNames)
                {
                    GameObject? prefab = _trialFloorBundle.LoadAsset<GameObject>(definition.Value);
                    // Older bundles remain usable while the additional layouts
                    // are being authored: they temporarily fall back to Battle_01.
                    if (prefab == null) prefab = _trialFloorPrefab;
                    NetworkIdentity? floorIdentity = prefab.GetComponent<NetworkIdentity>();
                    if (floorIdentity == null || floorIdentity.assetId == 0) return false;
                    _trialFloorPrefabs[definition.Key] = prefab;
                    _trialFloorAssetIds[definition.Key] = floorIdentity.assetId;
                    if (!NetworkClient.prefabs.ContainsKey(floorIdentity.assetId))
                        NetworkClient.RegisterPrefab(prefab);
                }

                foreach (KeyValuePair<string, string> definition in TrialFloorLayoutAssetPaths)
                {
                    TextAsset? xml = _trialFloorBundle.LoadAsset<TextAsset>(definition.Value);
                    if (xml == null)
                    {
                        Debug.LogError("[Sephiria Endless Trial] 번들에서 층 XML을 찾지 못했습니다: " + definition.Value);
                        return false;
                    }
                    _trialFloorLayoutXml[definition.Key] = xml.text;
                }

                _trialSavePanelPrefab = _trialFloorBundle.LoadAsset<GameObject>(TrialSavePanelPrefabName);
                if (_trialSavePanelPrefab == null)
                    Debug.LogWarning("[시련] 원본 저장 선택 UI 프리팹이 번들에 없습니다. Unity에서 번들을 다시 빌드해 설치하세요.");

                _trialFloorBundleAvailable = true;
                Debug.Log("[Sephiria Endless Trial] 전용 시련 층 번들을 준비했습니다: " +
                    _trialFloorPrefab.name + " (assetId=" + _trialFloorAssetId + ")");
                return true;
            }
            catch (Exception exception)
            {
                Debug.LogError("[Sephiria Endless Trial] 시련 층 AssetBundle 로드 실패: " + exception);
                return false;
            }
        }

        private static void RegisterCachedTrialNetworkPrefabs()
        {
            // Mirror can clear its client prefab registry between sessions even
            // while the cached AssetBundle and runtime templates remain loaded.
            foreach (KeyValuePair<string, GameObject> entry in _trialFloorPrefabs)
            {
                if (entry.Value != null && _trialFloorAssetIds.TryGetValue(entry.Key, out uint assetId) &&
                    assetId != 0 && !NetworkClient.prefabs.ContainsKey(assetId))
                    NetworkClient.RegisterPrefab(entry.Value);
            }
            if (_trialEntrancePortalPrefab != null && _trialEntrancePortalAssetId != 0 &&
                !NetworkClient.prefabs.ContainsKey(_trialEntrancePortalAssetId))
                NetworkClient.RegisterPrefab(_trialEntrancePortalPrefab, _trialEntrancePortalAssetId,
                    SpawnRuntimeTrialEntrancePortalOnClient, UnspawnRuntimeTrialEntrancePortalOnClient);
            if (_trialReturnPortalPrefab != null && _trialReturnPortalAssetId != 0 &&
                !NetworkClient.prefabs.ContainsKey(_trialReturnPortalAssetId))
                NetworkClient.RegisterPrefab(_trialReturnPortalPrefab, _trialReturnPortalAssetId,
                    SpawnRuntimeTrialReturnPortalOnClient, UnspawnRuntimeTrialReturnPortalOnClient);
            if (_trialControllerPrefab != null && _trialControllerAssetId != 0 &&
                !NetworkClient.prefabs.ContainsKey(_trialControllerAssetId))
                NetworkClient.RegisterPrefab(_trialControllerPrefab, _trialControllerAssetId,
                    SpawnRuntimeTrialControllerOnClient, UnspawnRuntimeTrialControllerOnClient);
        }

        internal static bool TryGetTrialSavePanelPrefab(out GameObject prefab)
        {
            prefab = null!;
            if (!TryLoadTrialFloorBundle() || _trialSavePanelPrefab == null) return false;
            prefab = _trialSavePanelPrefab;
            return true;
        }

        private static bool TryGetTrialEntrancePortalPrefab(out GameObject prefab)
        {
            prefab = null!;
            if (!EnsureRuntimeTrialEntrancePortalPrefab() || _trialEntrancePortalPrefab == null || _trialEntrancePortalAssetId == 0)
            {
                Debug.LogError("[Sephiria Endless Trial] 전용 시련 포탈 프리팹을 준비하지 못했습니다.");
                return false;
            }

            prefab = _trialEntrancePortalPrefab;
            return true;
        }

        private static bool EnsureRuntimeTrialEntrancePortalPrefab()
        {
            if (_trialEntrancePortalPrefab != null && _trialEntrancePortalAssetId == TrialEntrancePortalAssetId)
                return true;

            try
            {
                // A local template is registered on every peer before the server
                // spawns it.  The live copy is activated explicitly below; the
                // template itself stays far outside any playable floor.
                GameObject prefab = new GameObject("TrialEntrancePortal_RuntimePrefab");
                prefab.transform.position = new Vector3(100000f, 100000f, 0f);
                // NetworkIdentity copies its private initialization state when
                // Unity instantiates an already-active runtime object.  Keeping
                // the template inactive until a live copy is made prevents that
                // state from being copied and avoids Mirror's scene-object error.
                prefab.SetActive(false);
                NetworkIdentity identity = prefab.AddComponent<NetworkIdentity>();
                if (!SetNetworkIdentityAssetId(identity, TrialEntrancePortalAssetId))
                {
                    UnityEngine.Object.Destroy(prefab);
                    return false;
                }

                BoxCollider2D trigger = prefab.AddComponent<BoxCollider2D>();
                trigger.isTrigger = true;
                trigger.offset = new Vector2(0f, -0.15f);
                trigger.size = new Vector2(1.25f, 1.25f);

                Interactable interactable = prefab.AddComponent<Interactable>();
                interactable.interactionDescription = new LocalizedString("trial.entrance.interact");
                interactable.delayTime = 0.1f;
                prefab.AddComponent<TrialEntrancePortalVisual>();
                UnityEngine.Object.DontDestroyOnLoad(prefab);

                _trialEntrancePortalPrefab = prefab;
                _trialEntrancePortalAssetId = TrialEntrancePortalAssetId;
                if (!NetworkClient.prefabs.ContainsKey(_trialEntrancePortalAssetId))
                {
                    NetworkClient.RegisterPrefab(
                        _trialEntrancePortalPrefab,
                        _trialEntrancePortalAssetId,
                        SpawnRuntimeTrialEntrancePortalOnClient,
                        UnspawnRuntimeTrialEntrancePortalOnClient);
                }

                Debug.Log("[Sephiria Endless Trial] 독립 시련 입구 포탈 프리팹을 등록했습니다: " +
                    _trialEntrancePortalAssetId);
                return true;
            }
            catch (Exception exception)
            {
                Debug.LogError("[Sephiria Endless Trial] 독립 시련 입구 포탈 프리팹 생성 실패: " + exception);
                _trialEntrancePortalPrefab = null;
                _trialEntrancePortalAssetId = 0;
                return false;
            }
        }

        private static bool EnsureRuntimeTrialControllerPrefab()
        {
            if (_trialControllerPrefab != null && _trialControllerAssetId == TrialControllerAssetId)
                return true;

            try
            {
                GameObject prefab = new GameObject("TrialController_RuntimePrefab");
                prefab.transform.position = new Vector3(100000f, 100000f, 0f);
                prefab.SetActive(false);

                NetworkIdentity identity = prefab.AddComponent<NetworkIdentity>();
                if (!SetNetworkIdentityAssetId(identity, TrialControllerAssetId))
                {
                    UnityEngine.Object.Destroy(prefab);
                    return false;
                }

                prefab.AddComponent<TrialController>();
                UnityEngine.Object.DontDestroyOnLoad(prefab);
                _trialControllerPrefab = prefab;
                _trialControllerAssetId = TrialControllerAssetId;

                if (!NetworkClient.prefabs.ContainsKey(_trialControllerAssetId))
                {
                    NetworkClient.RegisterPrefab(
                        _trialControllerPrefab,
                        _trialControllerAssetId,
                        SpawnRuntimeTrialControllerOnClient,
                        UnspawnRuntimeTrialControllerOnClient);
                }

                Debug.Log("[Sephiria Endless Trial] 네트워크 시련 컨트롤러 프리팹을 등록했습니다: " +
                    _trialControllerAssetId);
                return true;
            }
            catch (Exception exception)
            {
                Debug.LogError("[Sephiria Endless Trial] 네트워크 시련 컨트롤러 프리팹 생성 실패: " + exception);
                _trialControllerPrefab = null;
                _trialControllerAssetId = 0;
                return false;
            }
        }

        private static bool SetNetworkIdentityAssetId(NetworkIdentity identity, uint assetId)
        {
            _networkIdentityAssetIdField ??= typeof(NetworkIdentity).GetField(
                "_assetId", BindingFlags.NonPublic | BindingFlags.Instance);
            if (_networkIdentityAssetIdField == null)
            {
                Debug.LogError("[Sephiria Endless Trial] Mirror NetworkIdentity._assetId 필드를 찾지 못했습니다.");
                return false;
            }

            _networkIdentityAssetIdField.SetValue(identity, assetId);
            return identity.assetId == assetId;
        }

        private static GameObject SpawnRuntimeTrialEntrancePortalOnClient(Vector3 position, uint assetId)
        {
            if (_trialEntrancePortalPrefab == null)
            {
                Debug.LogError("[Sephiria Endless Trial] 클라이언트가 시련 입구 포탈 템플릿을 찾지 못했습니다.");
                return null!;
            }

            GameObject portal = UnityEngine.Object.Instantiate(_trialEntrancePortalPrefab, position, Quaternion.identity);
            portal.name = "Trial_Entrance_Portal";
            portal.SetActive(true);
            return portal;
        }

        private static void UnspawnRuntimeTrialEntrancePortalOnClient(GameObject portal)
        {
            if (portal != null)
                UnityEngine.Object.Destroy(portal);
        }

        private static bool TryGetTrialReturnPortalPrefab(out GameObject prefab)
        {
            prefab = null!;
            if (!EnsureRuntimeTrialReturnPortalPrefab() || _trialReturnPortalPrefab == null || _trialReturnPortalAssetId == 0)
            {
                Debug.LogError("[시련] 저장 귀환 포탈 프리팹을 준비하지 못했습니다.");
                return false;
            }

            prefab = _trialReturnPortalPrefab;
            return true;
        }

        private static bool EnsureRuntimeTrialReturnPortalPrefab()
        {
            if (_trialReturnPortalPrefab != null && _trialReturnPortalAssetId == TrialReturnPortalAssetId)
                return true;

            try
            {
                GameObject prefab = new GameObject("TrialReturnPortal_RuntimePrefab");
                prefab.transform.position = new Vector3(100000f, 100000f, 0f);
                prefab.SetActive(false);

                NetworkIdentity identity = prefab.AddComponent<NetworkIdentity>();
                if (!SetNetworkIdentityAssetId(identity, TrialReturnPortalAssetId))
                {
                    UnityEngine.Object.Destroy(prefab);
                    return false;
                }

                BoxCollider2D trigger = prefab.AddComponent<BoxCollider2D>();
                trigger.isTrigger = true;
                trigger.offset = new Vector2(0f, -0.15f);
                trigger.size = new Vector2(1.25f, 1.25f);

                Interactable interactable = prefab.AddComponent<Interactable>();
                interactable.interactionDescription = new LocalizedString("trial.return.interact");
                interactable.delayTime = 0.1f;
                prefab.AddComponent<TrialPortalRoute>();
                prefab.AddComponent<TrialReturnPortalVisual>();
                UnityEngine.Object.DontDestroyOnLoad(prefab);

                _trialReturnPortalPrefab = prefab;
                _trialReturnPortalAssetId = TrialReturnPortalAssetId;
                if (!NetworkClient.prefabs.ContainsKey(_trialReturnPortalAssetId))
                {
                    NetworkClient.RegisterPrefab(
                        _trialReturnPortalPrefab,
                        _trialReturnPortalAssetId,
                        SpawnRuntimeTrialReturnPortalOnClient,
                        UnspawnRuntimeTrialReturnPortalOnClient);
                }

                Debug.Log("[시련] 저장 귀환 포탈 프리팹을 등록했습니다: " + _trialReturnPortalAssetId);
                return true;
            }
            catch (Exception exception)
            {
                Debug.LogError("[시련] 저장 귀환 포탈 프리팹 생성 실패: " + exception);
                _trialReturnPortalPrefab = null;
                _trialReturnPortalAssetId = 0;
                return false;
            }
        }

        private static GameObject SpawnRuntimeTrialReturnPortalOnClient(Vector3 position, uint assetId)
        {
            if (_trialReturnPortalPrefab == null)
            {
                Debug.LogError("[시련] 클라이언트가 저장 귀환 포탈 템플릿을 찾지 못했습니다.");
                return null!;
            }

            GameObject portal = UnityEngine.Object.Instantiate(_trialReturnPortalPrefab, position, Quaternion.identity);
            portal.name = "Trial_SaveReturn_Portal";
            portal.SetActive(true);
            return portal;
        }

        private static void UnspawnRuntimeTrialReturnPortalOnClient(GameObject portal)
        {
            if (portal != null)
                UnityEngine.Object.Destroy(portal);
        }

        private static GameObject SpawnRuntimeTrialControllerOnClient(Vector3 position, uint assetId)
        {
            if (_trialControllerPrefab == null)
            {
                Debug.LogError("[Sephiria Endless Trial] 클라이언트가 시련 컨트롤러 템플릿을 찾지 못했습니다.");
                return null!;
            }

            GameObject controller = UnityEngine.Object.Instantiate(_trialControllerPrefab, position, Quaternion.identity);
            controller.name = "TrialController";
            controller.SetActive(true);
            return controller;
        }

        private static void UnspawnRuntimeTrialControllerOnClient(GameObject controller)
        {
            if (controller != null)
                UnityEngine.Object.Destroy(controller);
        }

        private static bool TryRegisterBundledTrialFloorPrefab(string floorGuid, out uint assetId)
        {
            assetId = 0;
            if (!TryLoadTrialFloorBundle() || !_trialFloorPrefabs.TryGetValue(floorGuid, out GameObject? floorPrefab) ||
                !_trialFloorAssetIds.TryGetValue(floorGuid, out uint floorAssetId))
                return false;

            Dictionary<uint, GameObject>? prefabs = RaceDatabase.floorGeneratorDictionary;
            if (prefabs == null)
            {
                Debug.LogError("[Sephiria Endless Trial] FloorGenerator 프리팹 데이터베이스가 아직 준비되지 않았습니다.");
                return false;
            }

            if (prefabs.TryGetValue(floorAssetId, out GameObject? registered) && registered != floorPrefab)
            {
                Debug.LogError("[Sephiria Endless Trial] 시련 층 NetworkIdentity.assetId가 게임 프리팹과 충돌합니다: " + floorAssetId);
                return false;
            }

            // DungeonManager.FloorAlloc은 FloorData.prefabAssetId로 이 딕셔너리를 찾아
            // 번들 안의 원본 TileFloorGenerator 프리팹을 Instantiate한다.
            prefabs[floorAssetId] = floorPrefab;
            assetId = floorAssetId;
            Debug.Log("[Sephiria Endless Trial] 전용 시련 층 번들을 FloorGenerator 데이터베이스에 등록했습니다: " + assetId);
            return true;
        }

        private static FloorData? GetFloorData(FloorGenerator? generator)
        {
            if (generator == null) return null;
            _floorDataField ??= typeof(FloorGenerator).GetField("data", BindingFlags.NonPublic | BindingFlags.Instance);
            return _floorDataField?.GetValue(generator) as FloorData;
        }

        private static void OnFloorAllocatedServerside(string guid, string floorName, FloorGenerator generator)
        {
            if (!NetworkServer.active) return;
            if (!IsTrialFloorGuid(guid))
            {
                // 시련 입구는 일반 시작 플로어가 아니라 MultiZone의 귀환 포탈 옆에만 배치한다.
                FloorData? multiZone = DungeonManager.Instance?.FindFloorByName("MultiZone");
                if (generator != null &&
                    (generator.gameObject.name.StartsWith("MultiZone", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(floorName, "MultiZone", StringComparison.OrdinalIgnoreCase) ||
                     (multiZone != null && guid == multiZone.guid)))
                    RequestTrialEntranceSpawn();
                return;
            }

            ConfigureTrialFloorMusic(generator, guid);

            // PlayerSpawner.Initialize restores a saved run using the native
            // FLOORSTARTING id. Register the XML PlayerSpawn marker before the
            // floor begins generating, so DungeonManager can resolve it when
            // the pending travel transaction completes.
            RegisterTrialStartingPoint(generator);

            TrialController? controller = EnsureTrialController();
            if (controller == null)
            {
                Debug.LogError("[Sephiria Endless Trial] 시련 층에 사용할 TrialController를 만들지 못했습니다.");
                return;
            }
            if (_restoreCheckpointPhaseOnFloorAllocation && guid == _startupCheckpointFloorGuid &&
                GetTrialSlotReadData(_activeTrialSlot) is SaveData slotData)
            {
                int phase = Mathf.Max(1, slotData.GetInt(ActiveSnapshotPrefix + "Phase", 1));
                int displayPhase = slotData.GetInt(ActiveSnapshotPrefix + "DisplayPhase", phase);
                controller.RestoreSavedProgress(phase, displayPhase);
                controller.RestoreTrialPlaytime(SaveManager.CurrentRun?.GetFloat("PlayTime", 0f) ?? 0f);
                _restoreCheckpointPhaseOnFloorAllocation = false;
                _startupCheckpointFloorGuid = null;
                Debug.Log($"[시련] 게임 재시작 체크포인트 복원: {phase}단계, floor={guid}");
            }
            Debug.Log($"[시련] 층 할당 확인: guid={guid}, name={floorName}, generator={generator.name}");
            if (IsTrialBattleFloorGuid(guid))
                CoroutineManager.Instance.StartCoroutine(PrepareBundledTrialFloor(generator));
            else
                CoroutineManager.Instance.StartCoroutine(PrepareRewardFloor(generator));
        }

        private static IEnumerator PrepareBundledTrialFloor(FloorGenerator generator)
        {
            float timeout = Time.realtimeSinceStartup + 15f;
            while (generator != null && !generator.GenerateSuccess && Time.realtimeSinceStartup < timeout)
                yield return null;

            if (generator == null) yield break;
            if (!generator.GenerateSuccess)
            {
                Debug.LogError("[Sephiria Endless Trial] 전용 시련 층 생성 시간이 초과되었습니다.");
                yield break;
            }

            // The native XML replacement clears FloorGenerator.spawnPoints.
            // Keep the player marker registered on the generated instance.
            RegisterTrialStartingPoint(generator);

            // The bundle may have been baked from any valid TrialLayout.xml
            // size.  Its markers are therefore authoritative instead of the
            // old hard-coded 31 x 19 coordinates.
            Vector2 localCenter = generator.Center;
            Transform? centerMarker = FindBundledTrialMarker(generator, "ArenaCenter");
            Transform? playerSpawnMarker = FindBundledTrialMarker(generator, "PlayerSpawn");
            TrialAnchorPosition = centerMarker != null
                ? centerMarker.position
                : generator.transform.position + new Vector3(localCenter.x, localCenter.y, 0f);

            Vector2 floorSize = generator.Size;
            TrialCombatHalfExtents = new Vector2(
                Mathf.Max(1.5f, floorSize.x * 0.5f - 4.5f),
                Mathf.Max(1.5f, floorSize.y * 0.5f - 4.5f));
            UpdateTrialMonsterBounds(generator);
            TrialPlayerSpawnPosition = playerSpawnMarker != null
                ? playerSpawnMarker.position
                : TrialAnchorPosition + new Vector3(0f, -Mathf.Min(5f, TrialCombatHalfExtents.y), 0f);
            SpawnTrialStartTablet(generator, TrialAnchorPosition);
            RestoreBattleTransitionPortals(generator);
        }

        private static void OnFloorAllocatedClientside(string guid, string floorName, FloorGenerator generator)
        {
            if (generator == null || !IsTrialFloorGuid(guid)) return;
            if (!_trialFloorLayoutXml.TryGetValue(guid, out string? xml) || string.IsNullOrWhiteSpace(xml))
            {
                Debug.LogError("[Sephiria Endless Trial] 해당 시련 층의 XML 레이아웃을 번들에서 찾지 못했습니다: " + guid);
                return;
            }

            if (!TrialLayoutRuntime.ApplyTo(generator, xml))
                Debug.LogError($"[Sephiria Endless Trial] 원본 타일 생성 층에 XML 적용 실패: guid={guid}, name={floorName}");
            else if (NetworkServer.active)
                // TrialLayoutRuntime.ClearGeneratedProps also clears the native
                // spawnPoints list. Restore the XML marker before travel resolves.
                RegisterTrialStartingPoint(generator);
            ConfigureTrialFloorMusic(generator, guid);
        }

        private static void RestoreBattleTransitionPortals(FloorGenerator generator)
        {
            TrialController? controller = TrialController.Instance;
            bool clearedRewardPhase = controller != null && controller.DisplayPhase > 0 &&
                controller.DisplayPhase % 5 == 0 && controller.CurrentPhase == controller.DisplayPhase + 1;
            string expectedGuid = GetBattleFloorGuidForPhase(controller?.CurrentPhase ?? 1);
            string previousGuid = GetBattleFloorGuidForPhase(controller?.DisplayPhase ?? 1);
            if (!NetworkServer.active || generator == null || controller == null ||
                controller.IsTrialRunning ||
                (generator.guid != expectedGuid && (!clearedRewardPhase || generator.guid != previousGuid)))
                return;

            Transform? centerMarker = FindBundledTrialMarker(generator, "ArenaCenter");
            TrialAnchorPosition = centerMarker != null
                ? centerMarker.position
                : generator.transform.position + (Vector3)generator.Center;
            TrialCombatHalfExtents = new Vector2(
                Mathf.Max(1.5f, generator.Size.x * 0.5f - 4.5f),
                Mathf.Max(1.5f, generator.Size.y * 0.5f - 4.5f));
            UpdateTrialMonsterBounds(generator);

            SpawnTrialSaveReturnPortal(generator.guid);
            if (clearedRewardPhase)
                SpawnBattleToRewardPortal(generator.guid);
            Debug.Log($"[시련] 전투 층 포탈 복구 완료: floor={generator.guid}, phase={controller.CurrentPhase}");
        }

        private static IEnumerator RestoreBattleTransitionPortalsAfterMove(PlayerAvatar avatar, string battleGuid)
        {
            float timeout = Time.realtimeSinceStartup + 15f;
            FloorGenerator? floor = null;
            while (Time.realtimeSinceStartup < timeout)
            {
                if (avatar != null && avatar.currentFloorGuid == battleGuid)
                {
                    floor = FindLoadedFloor(battleGuid);
                    if (floor != null && floor.GenerateSuccess) break;
                }
                yield return null;
            }

            if (floor == null || avatar == null || avatar.currentFloorGuid != battleGuid)
            {
                Debug.LogWarning($"[시련] 전투 층 도착 후 포탈 복구를 완료하지 못했습니다: floor={battleGuid}");
                yield break;
            }
            RestoreBattleTransitionPortals(floor);
        }

        private static Transform? FindBundledTrialMarker(FloorGenerator generator, string markerName)
        {
            foreach (Transform transform in generator.GetComponentsInChildren<Transform>(true))
            {
                if (transform.name == markerName)
                    return transform;
            }
            return null;
        }

        private static void RegisterTrialStartingPoint(FloorGenerator generator)
        {
            if (generator == null) return;
            Transform? marker = FindBundledTrialMarker(generator, "PlayerSpawn");
            if (marker == null)
            {
                Debug.LogError($"[Sephiria Endless Trial] 시련 층의 PlayerSpawn 표식이 없습니다: {generator.guid}");
                return;
            }

            DungeonCustomSpawnPoint? spawnPoint = marker.GetComponent<DungeonCustomSpawnPoint>();
            if (spawnPoint == null)
                spawnPoint = marker.gameObject.AddComponent<DungeonCustomSpawnPoint>();
            spawnPoint.spawnPointId = "FLOORSTARTING";
            spawnPoint.customSpawnOffset = Vector2.zero;

            // The cloned native floor may still contain a source-floor starting
            // point. Make the XML marker authoritative for this trial instance.
            for (int i = generator.spawnPoints.Count - 1; i >= 0; i--)
            {
                AreaSpawnPointProp? existing = generator.spawnPoints[i];
                if (existing != null && existing != spawnPoint && existing.SpawnPointId == "FLOORSTARTING")
                    generator.spawnPoints.RemoveAt(i);
            }
            if (!generator.spawnPoints.Contains(spawnPoint))
                generator.spawnPoints.Insert(0, spawnPoint);

            Debug.Log($"[시련] 원본 FLOORSTARTING 스폰 지점 등록: floor={generator.guid}, position={spawnPoint.SpawnPoint}");
        }

        private static void UpdateTrialMonsterBounds(FloorGenerator generator)
        {
            // Fallback for older bundles without a serialized wall Tilemap.
            Vector2 floorSize = generator.Size;
            Vector2 floorCenter = (Vector2)generator.transform.position + generator.Center;
            TrialMonsterBoundsMin = floorCenter - floorSize * 0.5f + Vector2.one;
            TrialMonsterBoundsMax = floorCenter + floorSize * 0.5f - Vector2.one;

            Tilemap? wall = generator.GetComponentsInChildren<Tilemap>(true)
                .FirstOrDefault(map => map.name == "Wall");
            if (wall == null)
            {
                TrialCombatHalfExtents = Vector2.Max(Vector2.one * 1.5f,
                    (TrialMonsterBoundsMax - TrialMonsterBoundsMin) * 0.5f);
                return;
            }
            Tilemap? wallCollider = generator.GetComponentsInChildren<Tilemap>(true)
                .FirstOrDefault(map => map.name == "WallCollider");
            BoundsInt cells = wall.cellBounds;
            Vector3Int centerCell = wall.WorldToCell(TrialAnchorPosition);
            int left = int.MinValue, right = int.MaxValue;
            int bottom = int.MinValue, top = int.MaxValue;
            for (int x = centerCell.x; x >= cells.xMin; x--)
                if (IsTrialWallCell(wall, wallCollider, new Vector3Int(x, centerCell.y, 0))) { left = x; break; }
            for (int x = centerCell.x; x < cells.xMax; x++)
                if (IsTrialWallCell(wall, wallCollider, new Vector3Int(x, centerCell.y, 0))) { right = x; break; }
            for (int y = centerCell.y; y >= cells.yMin; y--)
                if (IsTrialWallCell(wall, wallCollider, new Vector3Int(centerCell.x, y, 0))) { bottom = y; break; }
            for (int y = centerCell.y; y < cells.yMax; y++)
                if (IsTrialWallCell(wall, wallCollider, new Vector3Int(centerCell.x, y, 0))) { top = y; break; }

            if (left == int.MinValue || right == int.MaxValue || bottom == int.MinValue || top == int.MaxValue)
                return;
            Vector3 min = wall.CellToWorld(new Vector3Int(left + 1, bottom + 1, 0));
            Vector3 max = wall.CellToWorld(new Vector3Int(right, top, 0));
            if (max.x - min.x <= 2f || max.y - min.y <= 2f) return;
            TrialMonsterBoundsMin = new Vector2(min.x, min.y);
            TrialMonsterBoundsMax = new Vector2(max.x, max.y);
            TrialCombatHalfExtents = Vector2.Max(Vector2.one * 1.5f,
                (TrialMonsterBoundsMax - TrialMonsterBoundsMin) * 0.5f);
            Debug.Log($"[Sephiria Endless Trial] 몬스터 경계: {TrialMonsterBoundsMin} ~ {TrialMonsterBoundsMax}");
        }

        private static bool IsTrialWallCell(Tilemap wall, Tilemap? wallCollider, Vector3Int cell)
        {
            return wall.GetColliderType(cell) != Tile.ColliderType.None ||
                   (wallCollider != null && wallCollider.GetColliderType(cell) != Tile.ColliderType.None);
        }

        private static void SpawnTrialStartTablet(FloorGenerator floor, Vector3 position)
        {
            if (TrialTablet != null) NetworkServer.Destroy(TrialTablet);
            GameObject? prefab = Resources.Load<GameObject>("Sephirite/Sephirite_Tablet");
            if (prefab == null) return;

            TrialTablet = UnityEngine.Object.Instantiate(prefab, position, Quaternion.identity);
            TrialTablet.name = "Trial_Start_Tablet";
            NetworkServer.Spawn(TrialTablet);
            floor.floorRelatedNetworkObjects.Add(TrialTablet);

            Interactable? interactable = TrialTablet.GetComponent<Interactable>();
            if (interactable != null)
                ConfigureTrialTabletInteraction(interactable);
            if (TrialController.Instance != null)
                Instance.UpdateTrialText(TrialController.Instance.DisplayPhase);
        }

        private static void ConfigureTrialTabletInteraction(Interactable interactable)
        {
            FieldInfo? interactionHandler = typeof(Interactable).GetField("OnInteraction", BindingFlags.NonPublic | BindingFlags.Instance);
            interactionHandler?.SetValue(interactable, null);
            interactable.DoInteraction?.RemoveAllListeners();
            interactable.enabled = true;
            interactable.OnInteraction += actor => ShowTrialPopup();
            interactable.interactionDescription = new LocalizedString("trial.tablet.interact");
        }

        private static void EnsureLocalTrialTabletInteraction()
        {
            string guid = _cachedLocalPlayer?.currentFloorGuid ?? string.Empty;
            if (!IsTrialBattleFloorGuid(guid)) return;
            if (Time.unscaledTime < _nextTrialTabletSearchTime) return;
            _nextTrialTabletSearchTime = Time.unscaledTime + 1f;
            FloorGenerator? floor = GameCamera.Instance?.CurrentSeeingFloor;
            if (floor == null || floor.guid != guid) floor = FindLoadedFloor(guid);
            if (floor == null || !floor.GenerateSuccess) return;
            if (_trialTabletSearchFloor != floor)
            {
                Transform? marker = FindBundledTrialMarker(floor, "ArenaCenter");
                _trialTabletSearchCenter = marker != null
                    ? marker.position : floor.transform.position + (Vector3)floor.Center;
                _trialTabletSearchFloor = floor;
            }
            Vector3 center = _trialTabletSearchCenter;
            if (TrialTablet != null)
            {
                if ((TrialTablet.transform.position - center).sqrMagnitude < 2.25f)
                    return;
                // A previous battle floor may remain loaded while the next
                // floor's tablet is spawned at a different world position.
                TrialTablet = null;
            }
            Sephirite? tablet = null;
            foreach (Sephirite candidate in UnityEngine.Object.FindObjectsByType<Sephirite>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (candidate == null) continue;
                string name = candidate.gameObject.name;
                if ((name == "Trial_Start_Tablet" ||
                     name.StartsWith("Sephirite_Tablet", StringComparison.Ordinal)) &&
                    (candidate.transform.position - center).sqrMagnitude < 2.25f)
                {
                    tablet = candidate;
                    break;
                }
            }
            if (tablet == null) return;
            TrialTablet = tablet.gameObject;
            ApplySephiriteClientVisibilityManual(tablet);
            Interactable? interactable = tablet.GetComponent<Interactable>();
            if (interactable != null) ConfigureTrialTabletInteraction(interactable);
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
                Debug.Log("[EndlessMod] 시련 입구 석판의 가시성 및 상호작용 문자열을 설정했습니다.");
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

        public static void SpawnTrialSaveReturnPortal(string? battleGuidOverride = null)
        {
            if (!NetworkServer.active) return;

            string battleGuid = battleGuidOverride ?? GetBattleFloorGuidForPhase(TrialController.Instance?.CurrentPhase ?? 1);
            if (TrialReturnPortal != null && _trialReturnPortalFloorGuid != battleGuid)
                ClearTrialSaveReturnPortal();
            if (TrialReturnPortal != null) return;

            FloorGenerator? floor = FindLoadedFloor(battleGuid);
            if (floor == null)
            {
                Debug.LogError($"[시련] 저장 귀환 포탈을 배치할 전투 층을 찾지 못했습니다: {battleGuid}");
                return;
            }
            if (!TryGetTrialReturnPortalPrefab(out GameObject prefab)) return;

            Transform? marker = FindBundledTrialMarker(floor, "SaveReturnPortal") ??
                                FindBundledTrialMarker(floor, "SavePortal");
            float inwardOffset = Mathf.Max(1.5f, TrialCombatHalfExtents.y - 1.25f);
            Vector3 floorCenter = floor.transform.position + (Vector3)floor.Center;
            Vector3 position = marker != null
                ? marker.position
                : floorCenter + new Vector3(0f, -inwardOffset, 0f);
            GameObject portal = UnityEngine.Object.Instantiate(prefab, position, Quaternion.identity);
            portal.name = "Trial_SaveReturn_Portal";
            portal.transform.localScale = Vector3.one;
            portal.SetActive(true);

            Interactable? interactable = portal.GetComponent<Interactable>();
            if (interactable == null)
            {
                UnityEngine.Object.Destroy(portal);
                return;
            }

            if (!CopyCachedTownPortalPresentation(portal))
            {
                Debug.LogWarning("[시련] 저장 귀환 포탈은 외형 캐시를 기다리고 있습니다.");
            }
            ConfigureTrialReturnInteraction(interactable);
            NetworkServer.Spawn(portal);
            TrialReturnPortal = portal;
            _trialReturnPortalFloorGuid = battleGuid;
            RegisterTrialFloorObject(floor, portal);
            Debug.Log($"[시련] 저장 귀환 포탈 생성 완료: floor={battleGuid}, position={position}, visual={portal.GetComponentInChildren<SpriteRenderer>() != null}");
        }

        public static void SpawnBattleToRewardPortal(string? battleGuidOverride = null)
        {
            if (!NetworkServer.active) return;
            string battleGuid = battleGuidOverride ?? GetBattleFloorGuidForPhase(TrialController.Instance?.CurrentPhase ?? 1);
            FloorGenerator? floor = FindLoadedFloor(battleGuid);
            if (floor == null)
            {
                Debug.LogError($"[시련] 보상 이동 포탈을 만들 전투 층을 찾지 못했습니다: {battleGuid}");
                return;
            }
            SpawnTrialTransferPortal(floor, "BattleToRewardPortal", 1, "Trial_BattleToReward_Portal");
        }

        private static void SpawnRewardToBattlePortal(FloorGenerator floor)
        {
            SpawnTrialTransferPortal(floor, "RewardToBattlePortal", 2, "Trial_RewardToBattle_Portal");
        }

        private static void SpawnTrialTransferPortal(FloorGenerator floor, string markerName, byte route, string objectName)
        {
            if (!NetworkServer.active || floor == null || ActiveTrialTransferPortals.Any(portal =>
                portal != null && portal.name == objectName && floor.floorRelatedNetworkObjects.Contains(portal))) return;
            if (!TryGetTrialReturnPortalPrefab(out GameObject prefab)) return;

            Transform? marker = FindBundledTrialMarker(floor, markerName);
            Vector3 position = marker != null
                ? marker.position
                : floor.transform.position + new Vector3(floor.Center.x, floor.Center.y - Mathf.Max(2f, floor.Size.y * 0.35f), 0f);
            GameObject portal = UnityEngine.Object.Instantiate(prefab, position, Quaternion.identity);
            portal.name = objectName;
            TrialPortalRoute? portalRoute = portal.GetComponent<TrialPortalRoute>();
            if (portalRoute == null)
            {
                Debug.LogError($"[시련] 이동 포탈 프리팹에 TrialPortalRoute가 없습니다: {objectName}");
                UnityEngine.Object.Destroy(portal);
                return;
            }
            portalRoute.SetRoute(route);
            portal.SetActive(true);
            CopyCachedTownPortalPresentation(portal);
            Interactable? interactable = portal.GetComponent<Interactable>();
            if (interactable == null)
            {
                Debug.LogError($"[시련] 이동 포탈 프리팹에 Interactable이 없습니다: {objectName}");
                UnityEngine.Object.Destroy(portal);
                return;
            }
            ConfigureTrialPortalInteraction(interactable, route);
            NetworkServer.Spawn(portal);
            floor.floorRelatedNetworkObjects.Add(portal);
            ActiveTrialTransferPortals.Add(portal);
            ActiveTrialTransferPortalFloors[portal] = floor.guid;
            Debug.Log($"[시련] 이동 포탈 생성 완료: {objectName}, route={route}, floor={floor.guid}, position={position}");
        }

        internal static void RequestMoveToRewardFloor(GameObject actor)
        {
            if (TryMoveLocalHostActor(actor, toRewardFloor: true)) return;
            TrialController? controller = TrialController.Instance;
            if (controller == null)
            {
                Debug.LogError("[시련] 보상 이동 입력을 받았지만 TrialController가 없습니다.");
                return;
            }
            Debug.Log($"[시련] 클라이언트에서 보상 이동 명령 전송: actor={actor?.name ?? "null"}");
            TrialNetworkBridge.SendAction(TrialNetworkBridge.MoveToReward);
        }

        internal static void RequestMoveToBattleFloor(GameObject actor)
        {
            if (TryMoveLocalHostActor(actor, toRewardFloor: false)) return;
            TrialController? controller = TrialController.Instance;
            if (controller == null)
            {
                Debug.LogError("[시련] 전투 이동 입력을 받았지만 TrialController가 없습니다.");
                return;
            }
            Debug.Log($"[시련] 클라이언트에서 전투 이동 명령 전송: actor={actor?.name ?? "null"}");
            TrialNetworkBridge.SendAction(TrialNetworkBridge.MoveToBattle);
        }

        private static bool TryMoveLocalHostActor(GameObject actor, bool toRewardFloor)
        {
            if (!NetworkServer.active || actor == null) return false;

            NetworkIdentity? actorIdentity = actor.GetComponent<NetworkIdentity>();
            NetworkConnectionToClient? localConnection = NetworkServer.localConnection;
            if (actorIdentity == null || localConnection == null || localConnection.identity != actorIdentity)
                return false;

            PlayerAvatar? avatar = actorIdentity.GetComponent<PlayerAvatar>();
            if (avatar == null)
            {
                Debug.LogError($"[시련] 호스트 포탈 입력 오브젝트에 PlayerAvatar가 없습니다: {actor.name}");
                return true;
            }
            if (!IsActorNearTrialTransferPortal(avatar, toRewardFloor ? (byte)1 : (byte)2))
                return true;

            Debug.Log($"[시련] 호스트 포탈 입력 직접 처리: target={(toRewardFloor ? "reward" : "battle")}, actor={actor.name}");
            if (toRewardFloor) MovePlayerToRewardFloor(avatar);
            else MovePlayerToBattleFloor(avatar);
            return true;
        }

        public static void MovePlayerToRewardFloor(PlayerAvatar avatar)
        {
            if (avatar == null || TrialController.Instance == null || TrialController.Instance.IsTrialRunning)
            {
                Debug.LogWarning("[시련] 보상 층 이동이 거부됐습니다: 플레이어/컨트롤러가 없거나 시련 진행 중입니다.");
                return;
            }
            string rewardGuid = GetRewardFloorGuidForPhase(Mathf.Max(1, TrialController.Instance.CurrentPhase - 1));
            if (!EnsureTrialFloorRegistered())
            {
                Debug.LogError($"[시련] 보상 층 이동 실패: 번들 층 등록 실패 ({rewardGuid})");
                return;
            }
            FloorGenerator? existingRewardFloor = FindLoadedFloor(rewardGuid);
            if (existingRewardFloor != null)
                CoroutineManager.Instance.StartCoroutine(PrepareRewardFloor(existingRewardFloor));
            Debug.Log($"[시련] 보상 층 이동 실행: {avatar.name}, {avatar.currentFloorGuid} -> {rewardGuid}");
            DungeonManager.Instance.MoveFloor(avatar, rewardGuid, "endless_trial_reward", 0,
                recordHistory: true, allowSave: true, keepPrevFloor: false, randomPosition: false,
                leaveTrainingSchool: false, overridePosition: GetFloorMarkerPosition(rewardGuid, "PlayerSpawn", Vector3.zero));
        }

        public static void MovePlayerToBattleFloor(PlayerAvatar avatar)
        {
            if (avatar == null || TrialController.Instance == null || TrialController.Instance.IsTrialRunning)
            {
                Debug.LogWarning("[시련] 전투 층 이동이 거부됐습니다: 플레이어/컨트롤러가 없거나 시련 진행 중입니다.");
                return;
            }
            string battleGuid = GetBattleFloorGuidForPhase(TrialController.Instance.CurrentPhase);
            if (!EnsureTrialFloorRegistered())
            {
                Debug.LogError($"[시련] 전투 층 이동 실패: 번들 층 등록 실패 ({battleGuid})");
                return;
            }
            CaptureTrialMerchantRoomState();
            CaptureTrialIndividualRewardRoomState();
            Debug.Log($"[시련] 전투 층 이동 실행: {avatar.name}, {avatar.currentFloorGuid} -> {battleGuid}");
            DungeonManager.Instance.MoveFloor(avatar, battleGuid, "endless_trial_battle", 0,
                recordHistory: true, allowSave: true, keepPrevFloor: true, randomPosition: false,
                leaveTrainingSchool: false, overridePosition: GetFloorMarkerPosition(battleGuid, "PlayerSpawn", TrialPlayerSpawnPosition));
            CoroutineManager.Instance.StartCoroutine(RestoreBattleTransitionPortalsAfterMove(avatar, battleGuid));
        }

        public static bool AreAllPlayersInCurrentBattleFloor()
        {
            string expectedGuid = GetBattleFloorGuidForPhase(TrialController.Instance?.CurrentPhase ?? 1);
            foreach (NetworkConnectionToClient connection in NetworkServer.connections.Values)
            {
                PlayerAvatar? avatar = connection?.identity != null ? connection.identity.GetComponent<PlayerAvatar>() : null;
                if (avatar == null || avatar.currentFloorGuid != expectedGuid) return false;
            }
            return true;
        }

        private static FloorGenerator? FindLoadedFloor(string guid)
        {
            return UnityEngine.Object.FindObjectsByType<FloorGenerator>(FindObjectsSortMode.None)
                .FirstOrDefault(generator => generator != null && generator.guid == guid);
        }

        private static void RegisterTrialFloorObject(FloorGenerator? floor, GameObject? obj)
        {
            if (floor == null || obj == null) return;
            if (!floor.floorRelatedNetworkObjects.Contains(obj))
                floor.floorRelatedNetworkObjects.Add(obj);
        }

        private static void UnregisterTrialFloorObject(GameObject? obj)
        {
            if (obj == null) return;
            foreach (FloorGenerator floor in UnityEngine.Object.FindObjectsByType<FloorGenerator>(FindObjectsSortMode.None))
            {
                if (floor != null)
                    floor.floorRelatedNetworkObjects.Remove(obj);
            }
        }

        private static Vector3 GetFloorMarkerPosition(string guid, string markerName, Vector3 fallback)
        {
            FloorGenerator? floor = FindLoadedFloor(guid);
            Transform? marker = floor != null ? FindBundledTrialMarker(floor, markerName) : null;
            if (marker != null) return marker.position;

            // FloorAlloc places each generated floor at FloorData.globalX/Y *
            // 500. When a player is moved before that floor exists, its prefab
            // still has the serialized marker and FloorData is already
            // registered, so resolve the same world position without waiting
            // for Generate() to finish.
            if (_trialFloorPrefabs.TryGetValue(guid, out GameObject? prefab) && prefab != null)
            {
                FloorGenerator? prefabGenerator = prefab.GetComponent<FloorGenerator>();
                Transform? prefabMarker = prefabGenerator != null
                    ? FindBundledTrialMarker(prefabGenerator, markerName)
                    : null;
                if (prefabMarker != null)
                {
                    Vector3 floorOrigin = prefab.transform.position;
                    DungeonManager? dungeon = DungeonManager.Instance;
                    if (dungeon != null && dungeon.generatedFloors.TryGetValue(guid, out FloorData floorData))
                        floorOrigin = new Vector3(floorData.globalX * 500f, floorData.globalY * 500f, 0f);

                    return floorOrigin + prefab.transform.TransformVector(prefabMarker.localPosition);
                }
            }

            return fallback;
        }

        private static void OnTrialLanguageChanged(string language)
        {
            TrialSaveSlotMenu.RefreshOpen();
            if (Instance != null && Instance._trialCanvas != null)
                Instance.UpdateTrialText(TrialController.Instance?.DisplayPhase ?? 0);

            if (_activeTrialPopup != null && _activeTrialPopup.IsOpened &&
                _activeTrialPopup.text != null && !string.IsNullOrEmpty(_activeTrialPopupKey))
            {
                string body = GetSafeText(_activeTrialPopupKey, _activeTrialPopupKey);
                if (_activeTrialPopupFormatArgs.Length > 0)
                    body = string.Format(body, _activeTrialPopupFormatArgs);
                _activeTrialPopup.text.text = KeywordDatabase.Convert(body);
            }

            UI_SystemMessage? systemMessage = Resources.FindObjectsOfTypeAll<UI_SystemMessage>()
                .FirstOrDefault(candidate => candidate != null && candidate.IsOpened);
            if (systemMessage?.messageText != null && !string.IsNullOrEmpty(_activeSystemMessageKey) &&
                systemMessage.messageText.text == _activeSystemMessageRenderedText)
            {
                string body = GetSafeText(_activeSystemMessageKey, _activeSystemMessageKey);
                if (_activeSystemMessagePhase > 0)
                    body = string.Format(body, _activeSystemMessagePhase);
                systemMessage.messageText.text = KeywordDatabase.Convert(body, useColor: false, useSprite: false);
                _activeSystemMessageRenderedText = systemMessage.messageText.text;
            }

            UpdateTrialGameOverPlaceName();
        }

        internal static void TrackTrialPopup(UI_MessageBox? messageBox, string key,
            params object[] formatArgs)
        {
            _activeTrialPopup = messageBox as UI_MessageBox_YesNo;
            _activeTrialPopupKey = key;
            _activeTrialPopupFormatArgs = formatArgs ?? Array.Empty<object>();
        }

        public static void RollTrialWitchHatReservation(int clearedPhase)
        {
            SaveData? run = SaveManager.CurrentRun;
            if (!NetworkServer.active || run == null || clearedPhase <= 0 ||
                run.GetInt(TrialWitchHatLastRollPhaseKey, 0) >= clearedPhase) return;

            run.SetInt(TrialWitchHatLastRollPhaseKey, clearedPhase);
            float roll = UnityEngine.Random.value;
            if (roll < TrialWitchHatSpawnChance)
                run.SetBool(TrialWitchHatPendingKey, value: true);
            Debug.Log($"[시련] {clearedPhase}단계 마녀 상인 판정: roll={roll:P4}, " +
                $"chance={TrialWitchHatSpawnChance:P4}, pending={run.GetBool(TrialWitchHatPendingKey, fallback: false)}");
        }

        private static bool TryFulfillTrialWitchHatReservation(Vector3 position, int phase, FloorGenerator floor)
        {
            SaveData? run = SaveManager.CurrentRun;
            // Reward floor generators may be allocated before the next five-phase
            // reward is available. Keep the reservation until that milestone.
            if (!NetworkServer.active || run == null || phase <= 0 || phase % 5 != 0 ||
                floor == null || floor.guid != GetRewardFloorGuidForPhase(phase) ||
                !run.GetBool(TrialWitchHatPendingKey, fallback: false)) return false;

            bool alreadySpawned = ActiveTrialMerchants.Any(obj => obj != null &&
                obj.name == "TrialWitchHat_Phase_" + phase);
            if (!alreadySpawned && !TrySpawnTrialWitchHat(position, phase, floor))
            {
                Debug.LogWarning($"[시련] {phase}단계 마녀 상인 생성 실패: 예약 상태를 유지합니다.");
                return false;
            }

            run.SetBool(TrialWitchHatPendingKey, value: false);
            Debug.Log($"[시련] {phase}단계 보상 공간에 마녀 상인을 배치하고 예약을 해제했습니다.");
            return true;
        }

        private static IEnumerator PrepareRewardFloor(FloorGenerator generator)
        {
            Debug.Log($"[시련] 보상 층 준비 시작: {generator.guid}, generateSuccess={generator.GenerateSuccess}");
            float timeout = Time.realtimeSinceStartup + 15f;
            while (generator != null && !generator.GenerateSuccess && Time.realtimeSinceStartup < timeout)
                yield return null;
            if (generator == null)
            {
                Debug.LogError("[시련] 보상 층 준비 중 FloorGenerator가 파괴됐습니다.");
                yield break;
            }
            if (!generator.GenerateSuccess)
            {
                Debug.LogError($"[시련] 보상 층 생성 대기 시간이 초과됐습니다: {generator.guid}");
                yield break;
            }
            if (TrialController.Instance == null)
            {
                Debug.LogError("[시련] 보상 층 생성은 완료됐지만 TrialController가 없습니다.");
                yield break;
            }

            int rewardPhase = Mathf.Max(1, TrialController.Instance.CurrentPhase - 1);
            if (PreparedRewardRoomPhases.TryGetValue(generator.guid, out int preparedPhase) &&
                preparedPhase == rewardPhase &&
                PreparedRewardRoomFloorIds.TryGetValue(generator.guid, out int preparedFloorId) &&
                preparedFloorId == generator.GetInstanceID())
            {
                // Keep the consumed props and merchant stock on this live floor.
                // Recreate its return portal if a transition removed it.
                SpawnRewardToBattlePortal(generator);
                Vector3 existingCenter = GetFloorMarkerPosition(generator.guid, "ArenaCenter", generator.transform.position + (Vector3)generator.Center);
                Vector3 existingWitch = GetFloorMarkerPosition(generator.guid, "WitchMerchant", existingCenter + new Vector3(-5f, -2f, 0f));
                if (TryFulfillTrialWitchHatReservation(existingWitch, rewardPhase, generator))
                    CaptureTrialMerchantRoomState();
                BroadcastTrialIndividualRewardClaims(rewardPhase);
                Debug.Log($"[시련] 보상 층은 이미 준비돼 있습니다: {generator.guid}, phase={rewardPhase}");
                yield break;
            }
            CaptureTrialMerchantRoomState();
            CaptureTrialIndividualRewardRoomState();
            PreparedRewardRoomPhases[generator.guid] = rewardPhase;
            PreparedRewardRoomFloorIds[generator.guid] = generator.GetInstanceID();
            CleanupTrialMerchants();
            CleanupTrialRewards();
            SpawnRewardToBattlePortal(generator);

            // A restored checkpoint already contains items obtained here. A
            // newly allocated reward floor must not issue them a second time.
            if (SaveManager.CurrentRun?.GetInt(TrialRewardIssuedPhaseKey, 0) == rewardPhase)
            {
                TrialMerchantRoomState? savedMerchants = ReadTrialMerchantRoomState(rewardPhase);
                if (savedMerchants != null)
                {
                    Vector3 savedCenter = GetFloorMarkerPosition(generator.guid, "ArenaCenter", generator.transform.position + (Vector3)generator.Center);
                    Vector3 savedMerchant = GetFloorMarkerPosition(generator.guid, "Merchant", savedCenter + new Vector3(-5f, 2f, 0f));
                    Vector3 savedWitch = GetFloorMarkerPosition(generator.guid, "WitchMerchant", savedCenter + new Vector3(-5f, -2f, 0f));
                    if (savedMerchants.regular != null)
                    {
                        SpawnTrialMerchantByClone(savedMerchant, rewardPhase, generator, savedMerchants.regular.randomId);
                        RestoreTrialMerchantState("TrialMerchant_Phase_" + rewardPhase, savedMerchants.regular);
                    }
                    if (savedMerchants.witch != null)
                    {
                        if (TrySpawnTrialWitchHat(savedWitch, rewardPhase, generator,
                            savedRandomId: savedMerchants.witch.randomId))
                        {
                            RestoreTrialMerchantState("TrialWitchHat_Phase_" + rewardPhase, savedMerchants.witch);
                        }
                    }
                    Debug.Log($"[시련] {rewardPhase}단계 상인 상태를 저장 기록에서 복원했습니다.");
                }
                if (savedMerchants?.witch == null)
                {
                    Vector3 retryCenter = GetFloorMarkerPosition(generator.guid, "ArenaCenter", generator.transform.position + (Vector3)generator.Center);
                    Vector3 retryWitch = GetFloorMarkerPosition(generator.guid, "WitchMerchant", retryCenter + new Vector3(-5f, -2f, 0f));
                    if (TryFulfillTrialWitchHatReservation(retryWitch, rewardPhase, generator))
                        CaptureTrialMerchantRoomState();
                }
                RestoreTrialIndividualRewardRoomState(rewardPhase, generator);
                BroadcastTrialIndividualRewardClaims(rewardPhase);
                Debug.Log($"[시련] 저장된 {rewardPhase}단계 보상은 재지급하지 않습니다.");
                yield break;
            }
            SaveManager.CurrentRun?.SetInt(TrialRewardIssuedPhaseKey, rewardPhase);
            BeginTrialIndividualRewardRoomState(rewardPhase);

            Vector3 center = GetFloorMarkerPosition(generator.guid, "ArenaCenter", generator.transform.position + (Vector3)generator.Center);
            Vector3 rewardFallback = center + new Vector3(0f, 2.5f, 0f);
            Vector3 merchant = GetFloorMarkerPosition(generator.guid, "Merchant", center + new Vector3(-5f, 2f, 0f));
            Vector3 witch = GetFloorMarkerPosition(generator.guid, "WitchMerchant", center + new Vector3(-5f, -2f, 0f));
            SpawnTrialReward(rewardPhase, generator, rewardFallback);
            SpawnTrialMerchantByClone(merchant, rewardPhase, generator);
            SpawnTrialSupplyTerminal(merchant + new Vector3(3f, 0f, 0f), rewardPhase, generator);
            TryFulfillTrialWitchHatReservation(witch, rewardPhase, generator);
            CaptureTrialMerchantRoomState();
            CaptureTrialIndividualRewardRoomState();
            BroadcastTrialIndividualRewardClaims(rewardPhase);
            Debug.Log($"[시련] 보상 층 준비 완료: {generator.guid}, phase={rewardPhase}");
        }

        public static void ClearTrialSaveReturnPortal()
        {
            if (TrialReturnPortal != null)
            {
                UnregisterTrialFloorObject(TrialReturnPortal);
                if (NetworkServer.active) NetworkServer.Destroy(TrialReturnPortal);
                else UnityEngine.Object.Destroy(TrialReturnPortal);
            }
            TrialReturnPortal = null;
            _trialReturnPortalFloorGuid = null;
        }

        public static void ClearTrialTransferPortals()
        {
            if (!NetworkServer.active) return;
            for (int i = ActiveTrialTransferPortals.Count - 1; i >= 0; i--)
            {
                GameObject portal = ActiveTrialTransferPortals[i];
                if (portal != null)
                {
                    UnregisterTrialFloorObject(portal);
                    NetworkServer.Destroy(portal);
                }
            }
            ActiveTrialTransferPortals.Clear();
            ActiveTrialTransferPortalFloors.Clear();
            PreparedRewardRoomPhases.Clear();
            PreparedRewardRoomFloorIds.Clear();
        }

        public static bool HasSavedTrialSnapshot()
        {
            return IsTrialSlotSaved(_activeTrialSlot);
        }

        private static string GetTrialSlotPrefix(int slot) => TrialSlotPrefix + slot + "_";

        private static string GetTrialSlotFileName(int slot)
        {
            string profile = string.IsNullOrWhiteSpace(SaveManager.Binded)
                ? SaveManager.defaultSlotName : SaveManager.Binded;
            return "EndlessTrial_" + profile + "_" + slot;
        }

        private static SaveData? GetTrialSlotFile(int slot, bool create)
        {
            if (slot < 1 || slot > TrialSlotCount) return null;
            string profile = SaveManager.Binded ?? string.Empty;
            if (_trialSlotProfile != profile)
            {
                TrialSlotFiles.Clear();
                _trialSlotProfile = profile;
            }
            if (TrialSlotFiles.TryGetValue(slot, out SaveData cached)) return cached;
            string name = GetTrialSlotFileName(slot);
            SaveData data = new SaveData(useEncryption: true);
            if (SaveData.Exists(name))
            {
                if (!data.Load(name)) return null;
            }
            else if (create)
                data.CreateNew(name);
            else
                return null;
            TrialSlotFiles[slot] = data;
            return data;
        }

        private static SaveData? GetTrialSlotReadData(int slot)
        {
            SaveData? file = GetTrialSlotFile(slot, create: false);
            if (file != null)
                return file;
            // Only an absent slot file may fall back to the old single-slot
            // checkpoint. An invalidated or damaged file must never revive it.
            if (slot == 1 && !SaveData.Exists(GetTrialSlotFileName(slot)) &&
                SaveManager.Current?.GetBool(TrialSnapshotPrefix + "Valid", fallback: false) == true)
                return SaveManager.Current;
            return null;
        }

        public static bool IsTrialSlotCorrupt(int slot) =>
            slot >= 1 && slot <= TrialSlotCount && SaveData.Exists(GetTrialSlotFileName(slot)) &&
            GetTrialSlotFile(slot, create: false) == null;

        public static bool CanDeleteTrialSlot(int slot) =>
            slot >= 1 && slot <= TrialSlotCount &&
            (SaveData.Exists(GetTrialSlotFileName(slot)) || IsTrialSlotSaved(slot));

        private static string GetTrialSlotReadPrefix(int slot)
        {
            string prefix = GetTrialSlotPrefix(slot);
            if (slot == 1 && ReferenceEquals(GetTrialSlotReadData(slot), SaveManager.Current))
                return TrialSnapshotPrefix;
            return prefix;
        }

        private static string ActiveSnapshotPrefix => GetTrialSlotReadPrefix(_activeTrialSlot);

        public static bool IsTrialSlotSaved(int slot)
        {
            SaveData? data = GetTrialSlotReadData(slot);
            return data != null && data.GetBool(GetTrialSlotReadPrefix(slot) + "Valid", fallback: false);
        }

        private static List<KeyValuePair<string, string>> GetTrialSlotParty(int slot)
        {
            var party = new List<KeyValuePair<string, string>>();
            SaveData? data = GetTrialSlotReadData(slot);
            if (!IsTrialSlotSaved(slot) || data == null) return party;
            string prefix = GetTrialSlotReadPrefix(slot);
            int count = data.GetInt(prefix + "PartyCount", 0);
            for (int i = 0; i < count; i++)
            {
                string guid = data.GetString(prefix + "PartyGuid" + i, string.Empty);
                string name = data.GetString(prefix + "PartyName" + i, guid);
                if (!string.IsNullOrWhiteSpace(guid)) party.Add(new KeyValuePair<string, string>(guid, name));
            }
            if (party.Count > 0) return party;

            // Older single-slot checkpoints already contain the native player
            // GUID/name fields. Use them without allowing a new roster in.
            int fieldCount = data.GetInt(prefix + "FieldCount", 0);
            var savedFields = new Dictionary<string, string>();
            for (int i = 0; i < fieldCount; i++)
            {
                string key = data.GetString(prefix + "Key" + i, string.Empty);
                if (key.StartsWith("Player", StringComparison.Ordinal) &&
                    (key.EndsWith("Guid", StringComparison.Ordinal) || key.EndsWith("Name", StringComparison.Ordinal)) &&
                    data.HasKey(prefix + "Value" + i))
                    savedFields[key] = data[prefix + "Value" + i]?.ToString() ?? string.Empty;
            }
            for (int i = 0; i < 16; i++)
            {
                if (savedFields.TryGetValue("Player" + i + "Guid", out string guid) && !string.IsNullOrWhiteSpace(guid))
                    party.Add(new KeyValuePair<string, string>(guid,
                        savedFields.TryGetValue("Player" + i + "Name", out string name) ? name : guid));
            }
            return party;
        }

        private static List<KeyValuePair<string, string>> GetConnectedTrialParty()
        {
            var party = new List<KeyValuePair<string, string>>();
            foreach (NetworkConnectionToClient connection in NetworkServer.connections.Values)
            {
                if (connection?.identity == null) return new List<KeyValuePair<string, string>>();
                PlayerSpawner? player = connection.identity.GetComponent<PlayerSpawner>();
                if (player?.PlayerAvatar == null || string.IsNullOrWhiteSpace(player.playerGuid))
                    return new List<KeyValuePair<string, string>>();
                party.Add(new KeyValuePair<string, string>(player.playerGuid, player.PlayerAvatar.Name));
            }
            return party;
        }

        private static void LoadActiveTrialParty(int slot)
        {
            ActiveTrialPartyGuids.Clear();
            ActiveTrialPartyNames.Clear();
            foreach (KeyValuePair<string, string> player in GetTrialSlotParty(slot))
            {
                ActiveTrialPartyGuids.Add(player.Key);
                ActiveTrialPartyNames.Add(player.Value);
            }
        }

        public static bool IsTrialHost(NetworkConnectionToClient? sender)
        {
            return NetworkServer.active && sender != null && sender.identity != null &&
                sender.identity.GetComponent<PlayerSpawner>()?.isHost == true;
        }

        public static bool IsLocalTrialHost()
        {
            return NetworkServer.active && NetworkServer.localConnection?.identity != null &&
                NetworkServer.localConnection.identity.GetComponent<PlayerSpawner>()?.isHost == true;
        }

        public static bool AreAllConnectedPlayersNearTrialPortal(GameObject? portal, string? floorGuid)
        {
            if (!NetworkServer.active || portal == null || string.IsNullOrEmpty(floorGuid)) return false;
            int count = 0;
            foreach (NetworkConnectionToClient connection in NetworkServer.connections.Values)
            {
                PlayerAvatar? avatar = connection?.identity != null
                    ? connection.identity.GetComponent<PlayerAvatar>() : null;
                if (avatar == null || avatar.currentFloorGuid != floorGuid ||
                    Vector2.Distance(avatar.transform.position, portal.transform.position) > TrialPortalGatherRadius)
                    return false;
                count++;
            }
            return count > 0;
        }

        public static bool AreAllPlayersNearTrialEntrance() =>
            AreAllConnectedPlayersNearTrialPortal(TrialEntrance, _trialEntranceFloorGuid);

        public static bool AreAllPlayersNearTrialReturnPortal() =>
            AreAllConnectedPlayersNearTrialPortal(TrialReturnPortal, _trialReturnPortalFloorGuid);

        public static bool IsActiveTrialPartyComplete()
        {
            List<KeyValuePair<string, string>> connected = GetConnectedTrialParty();
            return ActiveTrialPartyGuids.Count > 0 && connected.Count == ActiveTrialPartyGuids.Count &&
                connected.Select(player => player.Key).Distinct(StringComparer.Ordinal).Count() == connected.Count &&
                connected.All(player => ActiveTrialPartyGuids.Contains(player.Key));
        }

        public static bool TrySelectTrialSlot(int slot, out string errorKey)
        {
            errorKey = "trial.msg.slot_unavailable";
            if (!NetworkServer.active || slot < 1 || slot > TrialSlotCount || SaveManager.Current == null)
                return false;
            if (IsTrialSlotCorrupt(slot)) return false;
            List<KeyValuePair<string, string>> connected = GetConnectedTrialParty();
            if (connected.Count == 0) return false;
            if (IsTrialSlotSaved(slot))
            {
                List<KeyValuePair<string, string>> saved = GetTrialSlotParty(slot);
                if (saved.Count == 0 || saved.Count != connected.Count ||
                    !connected.All(player => saved.Any(member => member.Key == player.Key)))
                {
                    errorKey = "trial.msg.party_mismatch";
                    return false;
                }
                LoadActiveTrialParty(slot);
            }
            else
            {
                ActiveTrialPartyGuids.Clear();
                ActiveTrialPartyNames.Clear();
                foreach (KeyValuePair<string, string> player in connected)
                {
                    ActiveTrialPartyGuids.Add(player.Key);
                    ActiveTrialPartyNames.Add(player.Value);
                }
            }
            _activeTrialSlot = slot;
            SaveManager.Current.SetInt(TrialActiveSlotKey, slot);
            SaveManager.Save(saveCurrent: true, saveCurrentRun: false);
            return true;
        }

        public static bool IsActorNearTrialTransferPortal(PlayerAvatar? avatar, byte route)
        {
            if (avatar == null || avatar.spawner == null ||
                !ActiveTrialPartyGuids.Contains(avatar.spawner.playerGuid)) return false;
            return ActiveTrialTransferPortals.Any(portal => portal != null &&
                portal.GetComponent<TrialPortalRoute>()?.route == route &&
                ActiveTrialTransferPortalFloors.TryGetValue(portal, out string floorGuid) &&
                floorGuid == avatar.currentFloorGuid &&
                Vector2.Distance(avatar.transform.position, portal.transform.position) <= TrialPortalGatherRadius);
        }

        public static string GetTrialSlotSummary(int slot)
        {
            if (IsTrialSlotCorrupt(slot))
                return string.Format(GetSafeText("trial.slot.corrupt", "슬롯 {0}: 불러올 수 없음"), slot);
            if (!IsTrialSlotSaved(slot))
                return string.Format(GetSafeText("trial.slot.empty", "슬롯 {0}: 새 시련"), slot);
            SaveData? data = GetTrialSlotReadData(slot);
            if (data == null) return string.Format(GetSafeText("trial.slot.empty", "슬롯 {0}: 새 시련"), slot);
            string prefix = GetTrialSlotReadPrefix(slot);
            int phase = data.GetInt(prefix + "Phase", 1);
            List<KeyValuePair<string, string>> party = GetTrialSlotParty(slot);
            var connected = GetConnectedTrialParty().Select(player => player.Key).ToHashSet(StringComparer.Ordinal);
            string names = string.Join(", ", party.Select(player => player.Value +
                (connected.Contains(player.Key) ? " ✓" : " ○")));
            return string.Format(GetSafeText("trial.slot.saved", "슬롯 {0}: {1}단계 · {2}"), slot, phase, names);
        }

        public static bool DeleteTrialSlot(int slot)
        {
            if (!NetworkServer.active || slot < 1 || slot > TrialSlotCount || SaveManager.Current == null ||
                DungeonManager.Instance?.isRunStarted == true ||
                !CanDeleteTrialSlot(slot))
            {
                Debug.LogWarning($"[시련] 슬롯 삭제 불가: slot={slot}, server={NetworkServer.active}, current={SaveManager.Current != null}, runStarted={DungeonManager.Instance?.isRunStarted}, exists={CanDeleteTrialSlot(slot)}");
                return false;
            }
            try
            {
                _trialSlotWriteTask.Wait();
                string fileName = GetTrialSlotFileName(slot);
                SaveManager.DeleteSavedFile(fileName, includeBackup: true);
                if (SaveData.Exists(fileName))
                    throw new IOException("시련 슬롯 파일이 삭제 후에도 남아 있습니다: " + fileName);
                TrialSlotFiles.Remove(slot);
                if (slot == 1 && SaveManager.Current.GetBool(TrialSnapshotPrefix + "Valid", fallback: false))
                {
                    RemoveTrialSnapshotFields(SaveManager.Current, TrialSnapshotPrefix);
                    SaveManager.Current.SetBool(TrialSnapshotPrefix + "Valid", value: false);
                }
            }
            catch (Exception exception)
            {
                Debug.LogError("[시련] 저장 슬롯 삭제 실패: " + exception);
                return false;
            }
            if (_activeTrialSlot == slot)
            {
                _activeTrialSlot = 0;
                SaveManager.Current.SetInt(TrialActiveSlotKey, 0);
                ActiveTrialPartyGuids.Clear();
                ActiveTrialPartyNames.Clear();
            }
            SaveManager.Save(saveCurrent: true, saveCurrentRun: false);
            return true;
        }

        private static void RemoveTrialSnapshotFields(SaveData data, string prefix)
        {
            if (!(data.BakedData is Dictionary<string, object> fields)) return;
            string[] keys = fields.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
            foreach (string key in keys) fields.Remove(key);
        }

        private static void QueueTrialSlotFileSave(SaveData data)
        {
            SaveData snapshot = data.Copy();
            _trialSlotWriteTask = _trialSlotWriteTask.ContinueWith(previous =>
            {
                if (previous.IsFaulted)
                    Debug.LogError("[시련] 이전 슬롯 저장 실패: " + previous.Exception);
                snapshot.Save();
            }, TaskScheduler.Default);
        }

        private static bool VerifyTrialSlotFile(int slot, int phase)
        {
            SaveData file = new SaveData(useEncryption: true);
            if (!SaveData.Exists(GetTrialSlotFileName(slot)) ||
                !file.Load(GetTrialSlotFileName(slot))) return false;
            string prefix = GetTrialSlotPrefix(slot);
            return file.GetBool(prefix + "Valid", fallback: false) &&
                file.GetInt(prefix + "Phase", -1) == phase &&
                file.GetInt(prefix + "PartyCount", 0) == ActiveTrialPartyGuids.Count &&
                file.GetInt(prefix + "FieldCount", 0) > 0;
        }

        private static string GetSavedTrialBattleFloorGuid()
        {
            string guid = GetTrialSlotReadData(_activeTrialSlot)?.GetString(ActiveSnapshotPrefix + "FloorGuid", TrialFloorGuid) ?? TrialFloorGuid;
            return IsTrialBattleFloorGuid(guid) ? guid : TrialFloorGuid;
        }

        private static bool CopySavedTrialSnapshotRunFields(bool clearCurrentRun)
        {
            SaveData? data = GetTrialSlotReadData(_activeTrialSlot);
            if (!HasSavedTrialSnapshot() || data == null || SaveManager.CurrentRun == null)
                return false;

            string prefix = ActiveSnapshotPrefix;
            int fieldCount = data.GetInt(prefix + "FieldCount", 0);
            if (fieldCount <= 0) return false;
            var fields = new List<KeyValuePair<string, object>>(fieldCount);
            for (int i = 0; i < fieldCount; i++)
            {
                string key = data.GetString(prefix + "Key" + i, string.Empty);
                string valueKey = prefix + "Value" + i;
                if (!string.IsNullOrEmpty(key) && data.HasKey(valueKey))
                    fields.Add(new KeyValuePair<string, object>(key, data[valueKey]));
            }
            if (fields.Count == 0) return false;

            if (clearCurrentRun)
            {
                FieldInfo? bakedDataField = typeof(SaveData).GetField("bakedData", BindingFlags.NonPublic | BindingFlags.Instance);
                if (!(bakedDataField?.GetValue(SaveManager.CurrentRun) is Dictionary<string, object> bakedData))
                {
                    Debug.LogError("[시련] 현재 런 저장 필드를 초기화할 수 없어 체크포인트 복원을 중단합니다.");
                    return false;
                }
                bakedData.Clear();
            }
            foreach (KeyValuePair<string, object> field in fields)
                SaveManager.CurrentRun[field.Key] = field.Value;
            // Older slots recorded every trial floor at world cell (0, 0).
            // Move their serialized FloorData before native LoadDungeon allocates
            // either floor; changing only the live dictionary would be too late.
            if (!NormalizeTrialFloorCoordinatesInSave(SaveManager.CurrentRun)) return false;
            SaveManager.CurrentRun.SetString("LastFloorGuid", GetSavedTrialBattleFloorGuid());
            return true;
        }

        private static void RestoreTrialCheckpointBeforeNativeLoad()
        {
            _keepRestoredTrialLobbyOpen = false;
            _restoreCheckpointPhaseOnFloorAllocation = false;
            _startupCheckpointFloorGuid = null;
            _activeTrialSlot = SaveManager.Current?.GetInt(TrialActiveSlotKey, 0) ?? 0;
            if (_activeTrialSlot == 0 && SaveManager.CurrentRun?.GetBool("RunStarted", fallback: false) == true &&
                IsTrialFloorGuid(SaveManager.CurrentRun.GetString("LastFloorGuid", string.Empty)) &&
                IsTrialSlotSaved(1))
                _activeTrialSlot = 1;
            if (!NetworkServer.active || !HasSavedTrialSnapshot() || SaveManager.CurrentRun == null ||
                !SaveManager.CurrentRun.GetBool("RunStarted", fallback: false) ||
                !IsTrialFloorGuid(SaveManager.CurrentRun.GetString("LastFloorGuid", string.Empty)))
                return;

            try
            {
                LoadActiveTrialParty(_activeTrialSlot);
                if (ActiveTrialPartyGuids.Count == 0) return;
                if (!CopySavedTrialSnapshotRunFields(clearCurrentRun: true)) return;
                if (EnableTrialLevelCap())
                    _trialLevelCapPendingFloorUntil = Time.unscaledTime + 30f;
                _startupCheckpointFloorGuid = GetSavedTrialBattleFloorGuid();
                _restoreCheckpointPhaseOnFloorAllocation = true;
                foreach (string guid in TrialFloorPrefabNames.Keys)
                    TryRegisterBundledTrialFloorPrefab(guid, out _);
                Debug.Log($"[시련] 원본 던전 로드 전에 마지막 클리어 체크포인트를 적용했습니다: floor={_startupCheckpointFloorGuid}");
            }
            catch (Exception exception)
            {
                _restoreCheckpointPhaseOnFloorAllocation = false;
                _startupCheckpointFloorGuid = null;
                Debug.LogError("[시련] 게임 시작 체크포인트 복원 실패: " + exception);
            }
        }

        private static void RegisterRestoredTrialFloorPrefabs(bool isSavedSession)
        {
            if (!_restoreCheckpointPhaseOnFloorAllocation) return;
            foreach (string guid in TrialFloorPrefabNames.Keys)
                TryRegisterBundledTrialFloorPrefab(guid, out _);
            if (DungeonManager.Instance != null)
                DungeonManager.Instance.dungeonEnvironment["IsInDungeon"] = 1;
            SyncTrialRejoinWhitelist();
            // The host is already inside a saved run. Leave the Steam lobby
            // discoverable while the native authenticator admits only GUIDs
            // from this checkpoint's roster.
            _keepRestoredTrialLobbyOpen = ActiveTrialPartyGuids.Count > 1;
            _nextSavedLobbyRefreshTime = 0f;
            KeepSavedTrialLobbyOpenForRejoin();
        }

        public static void ClearTrialCheckpointOnGameOver()
        {
            SaveData? slotData = GetTrialSlotReadData(_activeTrialSlot);
            if (!NetworkServer.active || !HasSavedTrialSnapshot() || slotData == null || SaveManager.Current == null ||
                !PlayerSpawner.MultiplayerList.Any(player => player != null && player.PlayerAvatar != null &&
                    IsTrialFloorGuid(player.PlayerAvatar.currentFloorGuid)))
                return;
            slotData.SetBool(ActiveSnapshotPrefix + "Valid", value: false);
            if (!ReferenceEquals(slotData, SaveManager.Current))
                QueueTrialSlotFileSave(slotData);
            if (_activeTrialSlot == 1)
                SaveManager.Current.SetBool(TrialSnapshotPrefix + "Valid", value: false);
            SaveManager.Current.SetInt(TrialActiveSlotKey, 0);
            _activeTrialSlot = 0;
            _keepRestoredTrialLobbyOpen = false;
            ActiveTrialPartyGuids.Clear();
            ActiveTrialPartyNames.Clear();
            SaveManager.Save(saveCurrent: true, saveCurrentRun: false);
            Debug.Log("[시련] 게임 오버로 마지막 클리어 체크포인트를 해제했습니다.");
        }

        public static void ShowTrialSaveReturnPopup()
        {
            if (!IsLocalTrialHost())
            {
                ShowLocalizedSystemMessage("trial.msg.host_only_return");
                return;
            }
            if (!IsActiveTrialPartyComplete() || !AreAllPlayersNearTrialReturnPortal())
            {
                ShowLocalizedSystemMessage("trial.msg.gather_return");
                return;
            }
            if (isPopupOpen) return;

            UI_MessageBoxHolder? holder = Resources.FindObjectsOfTypeAll<UI_MessageBoxHolder>()
                .FirstOrDefault(h => h != null && !h.name.Contains("(Clone)"));
            if (holder == null || UIManager.Instance == null) return;

            isPopupOpen = true;
            UI_MessageBox messageBox = holder.OpenYesNo(
                GetSafeText("trial.return.confirm", "현재 시련 진행 상황을 저장하고 돌아가시겠습니까?"),
                SaveAndReturnFromPopup,
                CloseTrialPopup,
                false);
            TrackTrialPopup(messageBox, "trial.return.confirm");

            if (messageBox != null)
            {
                RectTransform? rect = messageBox.GetComponent<RectTransform>();
                if (rect != null) rect.anchoredPosition = Vector2.zero;
            }
        }

        private static void SaveAndReturnFromPopup()
        {
            TrialController? controller = EnsureTrialController();
            if (controller == null)
            {
                ShowLocalizedSystemMessage("trial.msg.save_unavailable");
                CloseTrialPopup();
                return;
            }

            Debug.Log("[시련] 저장 귀환 팝업 확인: 호스트 서버 처리 요청");
            controller.SaveAndReturnToLobbyFromHost();
            CloseTrialPopup();
        }

        // Capture the exact fields the base game writes for a mid-run save, then
        // keep a private copy in the permanent save.  The temporary CurrentRun
        // file may be deleted by Game Over, so it cannot be our checkpoint store.
        private static bool SaveTrialSnapshot(int phase, int displayPhase, bool saveCurrentRun = false)
        {
            if (!NetworkServer.active || _activeTrialSlot < 1 || _activeTrialSlot > TrialSlotCount ||
                !IsActiveTrialPartyComplete() || SaveManager.Current == null ||
                SaveManager.CurrentRun == null || DungeonManager.Instance == null)
            {
                Debug.LogError($"[시련] 저장 준비 조건 불충족: server={NetworkServer.active}, slot={_activeTrialSlot}, party={IsActiveTrialPartyComplete()}, current={SaveManager.Current != null}, run={SaveManager.CurrentRun != null}, dungeon={DungeonManager.Instance != null}");
                return false;
            }

            try
            {
                CaptureTrialMerchantRoomState();
                CaptureTrialIndividualRewardRoomState();
                string battleGuid = PlayerSpawner.MultiplayerList
                    .Where(player => player != null && player.PlayerAvatar != null)
                    .Select(player => player.PlayerAvatar.currentFloorGuid)
                    .FirstOrDefault(IsTrialBattleFloorGuid) ?? GetBattleFloorGuidForPhase(Mathf.Max(1, displayPhase));
                _dungeonSaveCurrentSessionMethod ??= typeof(DungeonManager).GetMethod(
                    "SaveCurrentSessionData", BindingFlags.NonPublic | BindingFlags.Instance);
                _dungeonSaveCurrentSessionMethod?.Invoke(DungeonManager.Instance, new object[] { battleGuid });
                foreach (PlayerSpawner player in PlayerSpawner.MultiplayerList.Where(player => player != null))
                    player.SaveCurrentSessionData();

                SaveManager.CurrentRun.SetString("LastFloorGuid", battleGuid);
                KeyValuePair<string, object>[] runFields = SaveManager.CurrentRun.BakedData
                    .Where(field => field.Value != null)
                    .ToArray();

                SaveData? slotData = GetTrialSlotFile(_activeTrialSlot, create: true);
                if (slotData == null) return false;
                string prefix = GetTrialSlotPrefix(_activeTrialSlot);
                RemoveTrialSnapshotFields(slotData, prefix);
                slotData.SetInt(prefix + "FieldCount", runFields.Length);
                for (int i = 0; i < runFields.Length; i++)
                {
                    slotData.SetString(prefix + "Key" + i, runFields[i].Key);
                    slotData[prefix + "Value" + i] = runFields[i].Value;
                }
                slotData.SetInt(prefix + "Phase", phase);
                slotData.SetInt(prefix + "DisplayPhase", displayPhase);
                slotData.SetString(prefix + "FloorGuid", battleGuid);
                slotData.SetInt(prefix + "PartyCount", ActiveTrialPartyGuids.Count);
                for (int i = 0; i < ActiveTrialPartyGuids.Count; i++)
                {
                    slotData.SetString(prefix + "PartyGuid" + i, ActiveTrialPartyGuids[i]);
                    slotData.SetString(prefix + "PartyName" + i, ActiveTrialPartyNames[i]);
                }
                slotData.SetBool(prefix + "Valid", value: true);
                SaveManager.Current.SetInt(TrialActiveSlotKey, _activeTrialSlot);
                QueueTrialSlotFileSave(slotData);
                if (saveCurrentRun)
                    SaveManager.Save(saveCurrent: false, saveCurrentRun: true);
                Debug.Log($"[시련] 호스트 슬롯 {_activeTrialSlot} 진행 상황 저장: {phase}단계, floor={battleGuid}, 필드 {runFields.Length}개");
                return true;
            }
            catch (Exception exception)
            {
                Debug.LogError("[시련] 진행 상황 저장 실패: " + exception);
                return false;
            }
        }

        public static bool SaveTrialAndReturnToLobby(TrialController controller)
        {
            if (!NetworkServer.active || controller == null || !IsActiveTrialPartyComplete() ||
                !SaveTrialSnapshot(controller.CurrentPhase, controller.DisplayPhase))
                return false;

            // The explicit return must not erase the active native run before
            // the host's independent trial slot has finished writing.
            try
            {
                _trialSlotWriteTask.Wait();
                if (!VerifyTrialSlotFile(_activeTrialSlot, controller.CurrentPhase))
                {
                    Debug.LogError("[시련] 저장 슬롯 파일 검증에 실패하여 귀환을 중단했습니다.");
                    return false;
                }
            }
            catch (Exception exception)
            {
                Debug.LogError("[시련] 저장 슬롯 기록 완료 전 귀환 중단: " + exception);
                return false;
            }

            // A checkpoint is not a second active run.  Replace the temporary run
            // with a fresh one and use the game's own RestartNewGame flow so that
            // inventory, levels, money and orphaned session stats are reset just
            // like a normal Game Over return.
            foreach (PlayerSpawner player in PlayerSpawner.MultiplayerList.Where(player => player != null))
            {
                PlayerAvatar? avatar = player.PlayerAvatar;
                if (avatar == null) continue;
                avatar.Inventory.ForceRemoveAll();
                avatar.ClearOrphanedStatusInstance();
                if (avatar.Money > 0) avatar.SubMoney(avatar.Money);
            }

            SaveManager.CreateNewTMP(string.IsNullOrEmpty(SaveManager.Binded) ? SaveManager.defaultSlotName : SaveManager.Binded);
            DungeonManager.Instance.dungeonEnvironment["IsInDungeon"] = 0;
            DungeonManager.Instance.NetworkisRunStarted = false;
            SaveManager.CurrentRun?.SetBool("RunStarted", value: false);
            UnlockTrialLobbyAfterSavedReturn();
            _activeTrialSlot = 0;
            ActiveTrialPartyGuids.Clear();
            ActiveTrialPartyNames.Clear();
            SaveManager.Current.SetInt(TrialActiveSlotKey, 0);
            SaveManager.Save(saveCurrent: true, saveCurrentRun: false);
            foreach (PlayerSpawner player in PlayerSpawner.MultiplayerList.Where(player => player != null))
                if (player.PlayerAvatar != null) player.PlayerAvatar.NetworkisInDungeon = 0;
            _trialLevelCapPendingFloorUntil = 0f;
            _trialLevelCapSuppressed = true;
            RestoreNativeLevelCap();
            controller.StopTrialRunTimer();
            controller.ResetAfterSavedReturn();
            foreach (PlayerSpawner player in PlayerSpawner.MultiplayerList.Where(player => player != null))
                player.RestartNewGame(0);
            return true;
        }

        public static bool RestoreSavedTrialSnapshot(TrialController controller)
        {
            if (!NetworkServer.active || controller == null || !HasSavedTrialSnapshot() ||
                SaveManager.Current == null || SaveManager.CurrentRun == null || DungeonManager.Instance == null)
                return false;
            if (!EnsureTrialFloorRegistered()) return false;

            try
            {
                if (!CopySavedTrialSnapshotRunFields(clearCurrentRun: true)) return false;
                if (EnableTrialLevelCap())
                    _trialLevelCapPendingFloorUntil = Time.unscaledTime + 30f;

                // Initialize reads saved weapons and the saved floor only when the
                // live dungeon reports an in-dungeon run.  The checkpoint always
                // resumes inside our registered Trial floor.
                DungeonManager.Instance.dungeonEnvironment["IsInDungeon"] = 1;
                _playerSpawnerInitializeMethod ??= typeof(PlayerSpawner).GetMethod(
                    "Initialize", BindingFlags.NonPublic | BindingFlags.Instance);
                if (_playerSpawnerInitializeMethod == null) return false;

                foreach (PlayerSpawner player in PlayerSpawner.MultiplayerList.Where(player => player != null))
                {
                    PlayerAvatar? avatar = player.PlayerAvatar;
                    if (avatar == null) continue;
                    avatar.Inventory.ForceRemoveAll();
                    avatar.ClearOrphanedStatusInstance();
                    if (avatar.Money > 0) avatar.SubMoney(avatar.Money);

                    _playerSpawnerInitializeMethod.Invoke(player, new object[]
                    {
                        player.LocalDataStorage.defaultWeapon,
                        player.LocalDataStorage.defaultCostume,
                        player.LocalDataStorage.defaultCostumeSkin,
                        0
                    });
                }

                StartNativeTrialRun(resumingSavedRun: true);
                controller.RestoreTrialPlaytime(SaveManager.CurrentRun.GetFloat("PlayTime", 0f));

                SaveData? slotData = GetTrialSlotReadData(_activeTrialSlot);
                if (slotData == null) return false;
                int phase = Mathf.Max(1, slotData.GetInt(ActiveSnapshotPrefix + "Phase", 1));
                // Snapshots saved before this field existed fall back to the next phase,
                // rather than presenting a restored run as "waiting".
                int displayPhase = slotData.GetInt(ActiveSnapshotPrefix + "DisplayPhase", phase);
                controller.RestoreSavedProgress(phase, displayPhase);
                Debug.Log("[시련] 저장 진행 상황 복원 완료: " + phase + "단계");
                return true;
            }
            catch (Exception exception)
            {
                Debug.LogError("[시련] 저장 진행 상황 복원 실패: " + exception);
                return false;
            }
        }

        public static void ShowTrialPopup()
        {
            if (!IsLocalTrialHost())
            {
                ShowLocalizedSystemMessage("trial.msg.host_only");
                return;
            }
            if (isPopupOpen) return;

            UI_MessageBoxHolder? holder = Resources.FindObjectsOfTypeAll<UI_MessageBoxHolder>()
                                         .FirstOrDefault(h => h != null && !h.name.Contains("(Clone)"));
            if (holder == null || UIManager.Instance == null) return;

            isPopupOpen = true;

            int currentPhase = TrialController.Instance != null ? TrialController.Instance.CurrentPhase : 1;
            string rawMsg = GetSafeText("trial.popup.challenge", "<color=red>{0}단계</color> 시련에 도전하겠습니까?");
            string localizedBody = string.Format(rawMsg, currentPhase);

            UI_MessageBox msgBox = holder.OpenYesNo(
                localizedBody,
                StartTrialFromPopup,
                () => { CloseTrialPopup(); },
                false
            );
            TrackTrialPopup(msgBox, "trial.popup.challenge", currentPhase);

            if (msgBox != null)
            {
                var tr = msgBox.GetComponent<RectTransform>();
                if (tr != null) tr.anchoredPosition = new Vector2(0, 0f);
            }
        }

        public static void ShowTrialEntrancePopup()
        {
            Debug.Log($"[시련] 입구 상호작용: host={IsLocalTrialHost()}, portal={TrialEntrance != null}, floor={_trialEntranceFloorGuid}");
            if (!IsLocalTrialHost())
            {
                ShowLocalizedSystemMessage("trial.msg.host_only_enter");
                return;
            }
            if (!AreAllPlayersNearTrialEntrance())
            {
                ShowLocalizedSystemMessage("trial.msg.gather_entrance");
                return;
            }
            if (!TrialSaveSlotMenu.Open(EnterTrialSpaceFromSlot))
                ShowLocalizedSystemMessage("trial.msg.slot_unavailable");
        }

        private static void StartTrialFromPopup()
        {
            TrialController? controller = EnsureTrialController();
            if (controller == null)
            {
                Debug.LogError("[Sephiria Endless Trial] 시련 시작 요청을 처리할 컨트롤러가 없습니다.");
                ShowLocalizedSystemMessage("trial.msg.start_unavailable");
                CloseTrialPopup();
                return;
            }

            if (controller.StartTrialFromHost())
                PlayBattleStartSound();
            CloseTrialPopup();
        }

        private static void EnterTrialSpaceFromSlot(int slot)
        {
            TrialController? controller = EnsureTrialController();
            if (controller == null)
            {
                Debug.LogError("[Sephiria Endless Trial] 시련 입장 요청을 처리할 컨트롤러가 없습니다.");
                ShowLocalizedSystemMessage("trial.msg.entrance_unavailable");
                CloseTrialPopup();
                return;
            }

            Debug.Log($"[시련] 선택한 슬롯의 서버 입장 요청: {slot}");
            controller.EnterTrialSpaceFromHost(slot);
        }

        public static void CloseTrialPopup()
        {
            isPopupOpen = false;
            _activeTrialPopup = null;
            _activeTrialPopupKey = null;
            _activeTrialPopupFormatArgs = Array.Empty<object>();
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

        public void ShowTrialTablet()
        {
            if (TrialTablet != null && !TrialTablet.activeSelf) TrialTablet.SetActive(true);
        }

        public void HideTrialTablet()
        {
            if (TrialTablet != null && TrialTablet.activeSelf) TrialTablet.SetActive(false);
        }

        public static bool SpawnMonster(float statMult)
        {
            if (!NetworkServer.active) return false;
            if (dbCache == null || dbCache.Count == 0) LoadDatabase();
            if (dbCache == null) return false;

            int phase = TrialController.Instance?.CurrentPhase ?? 1;
            List<string> pool = GetFilteredTrialMonsterPool(phase);
            if (pool.Count > 0)
            {
                string key = pool[UnityEngine.Random.Range(0, pool.Count)];
                if (dbCache.TryGetValue(key, out var ent))
                {
                    GameObject? prefab = ent.Select();
                    if (prefab == null) return false;

                    Vector3 spawnPos = GetRandomSpawnPos();
                    return SpawnMonsterWithAppearance(ent, prefab, spawnPos, statMult, phase);
                }
            }
            return false;
        }

        private static bool SpawnMonsterWithAppearance(AvatarSpawnEntity entity, GameObject prefab, Vector3 spawnPosition, float statMult, int phase, bool isMiniBoss = false, Action<GameObject>? configureBeforeSpawn = null)
        {
            TrialController? controller = TrialController.Instance;
            if (controller == null || !NetworkServer.active) return false;

            GameObject monster = UnityEngine.Object.Instantiate(prefab, spawnPosition, Quaternion.identity);
#if DEBUG
            Debug.Log($"[시련] 몬스터 스폰 위치: {monster.name}, phase={phase}, pos={spawnPosition}, arena={TrialMonsterBoundsMin}~{TrialMonsterBoundsMax}");
#endif
            monster.AddComponent<TrialMonsterTag>();
            UnitAvatar? trialAvatar = monster.GetComponent<UnitAvatar>();
            if (trialAvatar != null) ActiveTrialMonsters.Add(trialAvatar);
            configureBeforeSpawn?.Invoke(monster);
            ConstrainTrialMonsterToArena(trialAvatar);
            TryPlayNativeSpawnAppearance(entity, monster, controller);
            NetworkServer.Spawn(monster);
            ConstrainTrialMonsterToArena(trialAvatar);
            controller.StartCoroutine(ApplyMonsterStatNextFrame(monster, statMult, phase, isMiniBoss));
            return true;
        }

        // Some of the game's spawn entries contain their own appearance effect.
        // Reuse it where present, just as EnemySpawner does; the telegraph above
        // remains the common fallback for entries with no configured appearance.
        private static void TryPlayNativeSpawnAppearance(AvatarSpawnEntity entity, GameObject monster, TrialController controller)
        {
            try
            {
                UnitAI_NewBasic? ai = monster.GetComponent<UnitAI_NewBasic>();
                if (ai == null || entity.apperance == null || DungeonManager.Instance == null) return;

                AvatarSpawn_Apperance? appearance = DungeonManager.Instance.FindApperanceData(entity.apperance.name);
                if (appearance == null) return;

                appearance.ApperanceInitialize(ai);
                controller.StartCoroutine(appearance.Appear(ai));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[시련] 기본 몬스터 출현 이펙트를 적용하지 못했습니다: {e.Message}");
            }
        }

        public static IEnumerator CheckAndSpawnBoss(int phase, float statMult, int maxConcurrentMonsters)
        {
            if (!NetworkServer.active || phase % 5 != 0) yield break;

            int fullStacks = phase / 60;
            int remaining = phase % 60;
            for (int i = 0; i < fullStacks; i++)
            {
                while (TrialController.Instance != null &&
                    TrialController.Instance.AliveMonsterCount >= maxConcurrentMonsters)
                    yield return null;
                if (TrialController.Instance == null) yield break;
                HandlePhaseBossLogic(60, statMult);
            }

            if (remaining == 0) yield break;

            // Reserve enough capacity for every boss in the milestone group.
            int groupSize = remaining == 5 ? 1 : remaining == 10 ? 2 : 3;
            while (TrialController.Instance != null &&
                TrialController.Instance.AliveMonsterCount > maxConcurrentMonsters - groupSize)
                yield return null;
            if (TrialController.Instance != null) HandlePhaseBossLogic(remaining, statMult);
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
                // The actual UnitAvatar database entries for the QTemple trio.
                // Spawn only L here; its server-side death advances the relay.
                case 60: SpawnSixtiethPhaseBossSequence(statMult); break;
            }
        }

        private static void SpawnSixtiethPhaseBossSequence(float statMult)
        {
            SpawnSequencedMiniBoss("QTemple_MBTrio_L", "QTemple_MBTrio_M", statMult);
        }

        private static bool SpawnSequencedMiniBoss(string key, string nextBossKey, float statMult)
        {
            if (dbCache == null || dbCache.Count == 0) LoadDatabase();
            if (dbCache == null || !dbCache.TryGetValue(key, out AvatarSpawnEntity? entity))
            {
                Debug.LogError($"[시련] 60단계 미니보스 원본 스폰 엔트리를 찾지 못했습니다: {key}");
                return false;
            }

            GameObject? prefab = entity.Select();
            TrialController? controller = TrialController.Instance;
            if (prefab == null || controller == null) return false;

            bool spawned = SpawnMonsterWithAppearance(
                entity, prefab, GetRandomSpawnPos(), statMult * 1.5f, controller.CurrentPhase, isMiniBoss: true,
                configureBeforeSpawn: monster => monster.AddComponent<TrialBossSequenceTag>().nextBossKey = nextBossKey);

            if (spawned) controller.AddAliveCount();
            return spawned;
        }

        // Called by TrialController before it finalizes a tagged monster death.
        // F has an empty next key, making it the relay's count-releasing kill.
        internal static bool TrySpawnNextSixtiethPhaseBoss(UnitAvatar defeatedBoss)
        {
            TrialBossSequenceTag? sequence = defeatedBoss.GetComponent<TrialBossSequenceTag>();
            if (sequence == null || string.IsNullOrEmpty(sequence.nextBossKey)) return false;

            int phase = TrialController.Instance?.CurrentPhase ?? 60;
            float statMult = 1f + (phase - 1) * 2f;
            string nextKey = sequence.nextBossKey;
            string followingKey = string.Equals(nextKey, "QTemple_MBTrio_M", StringComparison.Ordinal)
                ? "QTemple_MBTrio_F"
                : string.Empty;
            return SpawnSequencedMiniBoss(nextKey, followingKey, statMult);
        }

        private static void SpawnSpecificBosses(string key, int count, float statMult)
        {
            if (dbCache == null || dbCache.Count == 0) LoadDatabase();
            if (dbCache == null) return;
            if (dbCache.TryGetValue(key, out var ent))
            {
                for (int i = 0; i < count; i++)
                {
                    GameObject? prefab = ent.Select();
                    if (prefab == null) continue;
                    TrialController? controller = TrialController.Instance;
                    int currentPhase = controller?.CurrentPhase ?? 1;
                    // Boss phases still use the same enemy pool, but this flag gives
                    // these units their own survivability and anti-evasion profile.
                    if (SpawnMonsterWithAppearance(ent, prefab, GetRandomSpawnPos(), statMult * 1.5f, currentPhase, isMiniBoss: true) && controller != null)
                    {
                        controller.AddAliveCount();
                    }
                }
            }
        }

        private static Vector3 GetNextRewardPosition(FloorGenerator floor, ref int slot, string dedicatedMarker, Vector3 fallback)
        {
            slot++;
            if (!string.IsNullOrEmpty(dedicatedMarker))
            {
                Transform? dedicated = FindBundledTrialMarker(floor, dedicatedMarker);
                if (dedicated != null) return dedicated.position;
            }
            Transform? marker = FindBundledTrialMarker(floor, "Reward_" + slot.ToString(CultureInfo.InvariantCulture));
            return marker != null ? marker.position : fallback;
        }

        private static Vector3[] GetTrialPotionBoxOffsets(int playerCount)
        {
            // The XML marker is numpad 5. Each offset is one tile in world space.
            switch (playerCount)
            {
                case 2:
                    return new[] { new Vector3(-1f, 0f, 0f), new Vector3(1f, 0f, 0f) }; // 4, 6
                case 3:
                    return new[] { new Vector3(-1f, -1f, 0f), new Vector3(0f, 1f, 0f),
                        new Vector3(1f, -1f, 0f) }; // 1, 8, 3
                case 4:
                    return new[] { new Vector3(-1f, -1f, 0f), new Vector3(1f, -1f, 0f),
                        new Vector3(-1f, 1f, 0f), new Vector3(1f, 1f, 0f) }; // 1, 3, 7, 9
                default:
                    return new[] { Vector3.zero }; // 5
            }
        }

        public static void SpawnTrialReward(int phase, FloorGenerator floor, Vector3 fallbackBasePos)
        {
            if (!NetworkServer.active) return;

            int rewardSlot = 0;
            Vector3 boxPos = GetNextRewardPosition(floor, ref rewardSlot, "RewardPotionBoxPosition", fallbackBasePos);
            List<int> rewardboxPool = new List<int> { 28, 29, 30, 31, 32, 33, 34, 38, 39, 40, 41, 42, 43, 44, 45, 46, 47, 48, 49, 50, 51 };
            // Keep the selected save slot's party size across reconnects.
            foreach (Vector3 offset in GetTrialPotionBoxOffsets(ActiveTrialPartyGuids.Count))
                CreateCustomRewardBox("RewardBox_MP", boxPos + offset, rewardboxPool, floor);

            // Keep the existing special-reward list, but make the larger utility drop
            // a milestone reward rather than a repeated reward after every boss.
            if (phase % 10 == 0)
            {
                SpawnFromDatabase("InventoryOrb", GetNextRewardPosition(floor, ref rewardSlot, "RewardInventoryOrbPosition",
                    boxPos + new Vector3(-2.5f, 0f, 0f)), floor, phase);
            }

            // Use the game's FloorGenerator.CreateProp route, not a copied
            // prefab. The native Anvil component keeps its per-player enhanced
            // hash list and disables interaction for a player already at max.
            if (phase % 10 == 0)
                SpawnNativeTrialAnvil(GetNextRewardPosition(floor, ref rewardSlot, "RewardAnvilPosition",
                    boxPos + new Vector3(2.5f, 0f, 0f)), floor, phase);

            List<string> rewardPool = new List<string> { 
                "MysticPot", "AltarOfEnchant_Dual", "AltarOfEnchant_Tri", 
                "SephiriteSpawner-StoneTablet", "SephiriteSpawner-Charm", 
                "MaxHPDispenser", "MiracleSelector", "Obelisk" 
            };
            if (phase % 10 == 0)
            {
                string selectedReward = rewardPool[UnityEngine.Random.Range(0, rewardPool.Count)];
                SpawnFromDatabase(selectedReward, GetNextRewardPosition(floor, ref rewardSlot, string.Empty,
                    boxPos + new Vector3(0f, -3f, 0f)), floor, phase);
            }
            PlayRewardSound(boxPos);
        }

        private static void CreateCustomRewardBox(string propId, Vector3 position, List<int> itemIDPool, FloorGenerator floor)
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
            RegisterTrialFloorObject(floor, boxObj);
            SpawnedRewards.Add(boxObj);
        }

        public static bool SaveTrialAutoCheckpoint(int phase, int displayPhase)
        {
            return NetworkServer.active && SaveTrialSnapshot(phase, displayPhase, saveCurrentRun: true);
        }

        private static GameObject? SpawnFromDatabase(string propId, Vector3 pos, FloorGenerator floor,
            int phase, TrialIndividualRewardSourceState? restored = null)
        {
            PropEntity? entity = PropDatabase.FindPropById(propId);
            if (entity?.propPrefab == null) return null;
            GameObject obj = UnityEngine.Object.Instantiate(entity.propPrefab, pos, Quaternion.identity);
            int randomId = restored?.randomId ?? UnityEngine.Random.Range(int.MinValue, int.MaxValue);
            foreach (IRandomID randomizable in obj.GetComponents<IRandomID>())
                randomizable.SetRandomID(randomId);
            RegisterTrialIndividualRewardSource(obj, phase, propId, randomId);
            NetworkServer.Spawn(obj);
            if (restored != null)
            {
                ApplyTrialIndividualRewardSourceState(obj, restored);
                RestoreTrialTabletClaimState(obj, restored);
            }
            RegisterTrialFloorObject(floor, obj);
            SpawnedRewards.Add(obj);
            if (obj.GetComponent<SephiriteSpawner>() != null)
                ObserveTrialSephiriteRewards(obj, phase, propId);
            return obj;
        }

        private static void SpawnNativeTrialAnvil(Vector3 position, FloorGenerator floor, int phase,
            TrialIndividualRewardSourceState? restored = null)
        {
            if (!NetworkServer.active) return;

            PropEntity? anvil = PropDatabase.FindPropById("Anvil");
            if (floor == null || anvil == null)
            {
                Debug.LogError("[시련] 원본 모루 생성에 필요한 Trial Floor 또는 PropEntity(Anvil)를 찾지 못했습니다.");
                return;
            }

            // FloorGenerator performs the original prefab selection, IRandomID
            // initialization, NetworkServer spawn and floor ownership tracking.
            int randomId = restored?.randomId ?? UnityEngine.Random.Range(int.MinValue, int.MaxValue);
            GameObject? spawned = floor.CreateProp(
                randomId, anvil, position,
                Vector3.one, null!, null!);
            if (spawned != null)
            {
                RegisterTrialIndividualRewardSource(spawned, phase, "Anvil", randomId);
                if (restored != null) ApplyTrialIndividualRewardSourceState(spawned, restored);
                SpawnedRewards.Add(spawned);
                Debug.Log("[시련] 원본 FloorGenerator 경로로 10단계 모루를 생성했습니다.");
            }
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

        private static List<string> GetFilteredTrialMonsterPool(int phase)
        {
            int tier = phase >= 101 ? 101 : phase >= 51 ? 51 :
                phase >= 41 ? 41 : phase >= 21 ? 21 : 1;
            if (FilteredTrialMonsterPools.TryGetValue(tier, out List<string>? pool))
                return pool;

            pool = GetCurrentPool(phase);
            for (int i = pool.Count - 1; i >= 0; i--)
                if (BlackList.Contains(pool[i])) pool.RemoveAt(i);
            FilteredTrialMonsterPools[tier] = pool;
            return pool;
        }

        public static Vector3 GetRandomSpawnPos()
        {
            // Sample from the collision-derived arena bounds, not the floor's
            // camera size. The original generator can include camera padding or
            // template leftovers in Size, which shifts apparent spawns outward.
            Vector2 boundsCenter = (TrialMonsterBoundsMin + TrialMonsterBoundsMax) * 0.5f;
            Vector2 boundsHalfExtents = (TrialMonsterBoundsMax - TrialMonsterBoundsMin) * 0.5f;
            Vector2 offset = UnityEngine.Random.insideUnitCircle * 0.55f;
            return new Vector3(
                boundsCenter.x + offset.x * boundsHalfExtents.x,
                boundsCenter.y + offset.y * boundsHalfExtents.y,
                TrialAnchorPosition.z);
        }

        private static void ConstrainTrialMonsterToArena(UnitAvatar? monster)
        {
            if (!NetworkServer.active || monster == null) return;

            if (!TrialMonsterRuntimeCaches.TryGetValue(monster, out TrialMonsterRuntimeCache? cache))
            {
                cache = new TrialMonsterRuntimeCache(monster);
                TrialMonsterRuntimeCaches.Add(monster, cache);
            }
            Vector3 lowerLeft = new Vector3(
                TrialMonsterBoundsMin.x + TrialMonsterBoundaryInset,
                TrialMonsterBoundsMin.y + TrialMonsterBoundaryInset, TrialAnchorPosition.z);
            Vector3 upperRight = new Vector3(
                TrialMonsterBoundsMax.x - TrialMonsterBoundaryInset,
                TrialMonsterBoundsMax.y - TrialMonsterBoundaryInset, TrialAnchorPosition.z);

            // Applying the same prevention data every poll repeats native and
            // Mirror work for every enemy. Reapply only on spawn or bounds change.
            cache.rigidbody ??= monster.TopdownRigidbody;
            if (cache.rigidbody != null && cache.rigidbody.isServer &&
                (!cache.boundaryApplied || cache.appliedLowerLeft != lowerLeft ||
                 cache.appliedUpperRight != upperRight))
            {
                cache.rigidbody.PreventPosition(TrialMonsterBoundaryLayer, lowerLeft, upperRight);
                cache.appliedLowerLeft = lowerLeft;
                cache.appliedUpperRight = upperRight;
                cache.boundaryApplied = true;
            }

            Vector3 current = cache.transform.position;
            bool outside = current.x < lowerLeft.x || current.x > upperRight.x ||
                           current.y < lowerLeft.y || current.y > upperRight.y;
            TrialMonsterBoundaryRecoveryTag? recovery = cache.recovery;
            if (!outside)
            {
                if (recovery != null) recovery.outsideSince = -1f;
                return;
            }

            recovery ??= monster.gameObject.AddComponent<TrialMonsterBoundaryRecoveryTag>();
            cache.recovery = recovery;
            if (recovery.outsideSince < 0f) recovery.outsideSince = Time.time;

            // Do not cancel an action merely because an enemy crossed the edge
            // by a fraction of a cell. The native boundary gets a short chance
            // to resolve ordinary movement; only a clear teleport escape or a
            // persistent out-of-bounds state receives an authoritative snap.
            bool farOutside = current.x < lowerLeft.x - TrialMonsterHardRecoveryDistance ||
                              current.x > upperRight.x + TrialMonsterHardRecoveryDistance ||
                              current.y < lowerLeft.y - TrialMonsterHardRecoveryDistance ||
                              current.y > upperRight.y + TrialMonsterHardRecoveryDistance;
            if (!farOutside && Time.time - recovery.outsideSince < TrialMonsterHardRecoveryDelay) return;

            // Teleport skills assign a transform position directly and can
            // therefore skip PreventPosition.  Clamp the server's authoritative
            // position back into the playable rectangle and cancel the stale
            // teleport action so the enemy does not immediately continue toward
            // its old out-of-bounds destination.  NetworkTransform then sends
            // this authoritative correction to every client.
            Vector3 returnPosition = new Vector3(
                Mathf.Clamp(current.x, lowerLeft.x, upperRight.x),
                Mathf.Clamp(current.y, lowerLeft.y, upperRight.y),
                TrialAnchorPosition.z);
            monster.CancelCurrentAction();
            cache.transform.position = returnPosition;
            recovery.outsideSince = -1f;
        }

        private static IEnumerator ApplyMonsterStatNextFrame(GameObject monster, float mult, int phase, bool isMiniBoss)
        {
            yield return null;
            if (monster == null) yield break;

            UnitAvatar? av = monster.GetComponent<UnitAvatar>();
            if (av == null) yield break;

            av.ChangeFaction("Demon");

            av.CancelCurrentAction();

            if (TrialController.Instance != null)
                TrialController.Instance.StartCoroutine(MonitorMonsterDeath(monster, av));

            if (av.hp <= 0f || av.IsDead) yield break;

            float baseHp = av.NetworkmaxHp;

            float finalHp = baseHp + (baseHp * mult / 10f);
            if (isMiniBoss)
            {
                // A modest extra health layer keeps bosses distinct from the normal
                // pack without replacing the existing phase scaling curve.
                finalHp *= 1.25f;
            }

            // These native UnitAvatar setters mark Mirror's SyncVars dirty.
            // Writing the backing fields leaves clients on the prefab HP.
            av.NetworkmaxHp = finalHp;
            av.Networkhp = finalHp;

            // Regular enemies keep the existing spawn chance. Mini bosses only
            // receive this trial-granted super armor from phase 25 onward.
            // Armor activated by a monster's native combat pattern is separate.
            if ((!isMiniBoss || phase >= 25) && UnityEngine.Random.value < TrialMonsterSpawnSuperArmorChance)
                av.TurnOnSuperArmor(Mathf.Max(1f, finalHp * 0.25f));

            av.AddCustomStat(ECustomStat.AllDamageBonus, phase * 15);
            av.AddCustomStat(ECustomStat.AttackSpeed, phase * 5);
            av.AddCustomStat(ECustomStat.DamageReduction, phase * 1);

            if (isMiniBoss)
            {
                av.AddCustomStat(ECustomStat.AllDamageBonus, phase * 5);
                av.AddCustomStat(ECustomStat.AttackSpeed, phase * 3);
                av.AddCustomStat(ECustomStat.DamageReduction, 15);
                // These are recognized by UnitAvatar's native damage calculation:
                // 35% of a target's evasion is ignored and critical damage is cut by 30%.
                av.AddCustomStat("IGNOREEVASION", 35);
                av.AddCustomStat("CRITICALRESIST", 30);
            }
        }

        private static IEnumerator MonitorMonsterDeath(GameObject monster, UnitAvatar av)
        {
            Type avType = av.GetType();
            FieldInfo? fIsDead = avType.GetField("isDead", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            FieldInfo? fHp = avType.GetField("hp", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            WaitForSeconds deathPollDelay = new WaitForSeconds(0.5f);
            while (monster != null)
            {
                bool isDead = false;
                float currentHp = 0f;

                if (fIsDead != null) isDead = (bool)fIsDead.GetValue(av);
                if (fHp != null) currentHp = (float)fHp.GetValue(av);
                if (isDead || currentHp <= 0f)
                {
                    if (av.GetComponent<TrialMonsterTag>() != null)
                        TrialController.Instance?.OnMonsterDied(av);
                    yield break;
                }
                yield return deathPollDelay;
            }
        }

        public static void ShowLocalizedSystemMessage(string key, int phase = 0, float duration = 3f)
        {
            UI_SystemMessage? ui = Resources.FindObjectsOfTypeAll<UI_SystemMessage>().FirstOrDefault();
            if (ui == null) return;
            string message = GetSafeText(key, key);
            if (phase > 0) message = string.Format(message, phase);
            ui.Open(message, duration, false);
            _activeSystemMessageKey = key;
            _activeSystemMessagePhase = phase;
            _activeSystemMessageRenderedText = ui.messageText != null ? ui.messageText.text : null;
        }

        public static void ShowSystemMessage(string msg)
        {
            _activeSystemMessageKey = null;
            UI_SystemMessage? ui = Resources.FindObjectsOfTypeAll<UI_SystemMessage>().FirstOrDefault();
            ui?.Open(msg, 3f, false);
        }
        private static void PlayRewardSound(Vector3 pos) { try { var guid = new FMOD.GUID { Data1 = -1064292075, Data2 = 1082856418, Data3 = -164297295, Data4 = -85952462 }; var inst = RuntimeManager.CreateInstance(new EventReference { Guid = guid }); inst.set3DAttributes(RuntimeUtils.To3DAttributes(pos)); inst.start(); inst.release(); } catch { } }
        private static void PlayBattleStartSound() { try { var guid = new FMOD.GUID { Data1 = -1393806677, Data2 = 1320069483, Data3 = -1389724540, Data4 = -2128244874 }; var inst = RuntimeManager.CreateInstance(new EventReference { Guid = guid }); inst.set3DAttributes(RuntimeUtils.To3DAttributes(TrialAnchorPosition)); inst.start(); inst.release(); } catch { } }

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

            // 입구 석판은 게임오버/시련 정리 대상이 아니다. 다음 시련을 바로 시작할 수 있어야 한다.
            isPopupOpen = false;
        }

        private static void OnActiveSceneChanged(Scene previousScene, Scene nextScene)
        {
            _nextTrialFastTickTime = 0f;
            _cachedTownReturnPortal = null;
            _nextTownReturnPortalSearchTime = 0f;
            _nextTrialTabletSearchTime = 0f;
            _trialTabletSearchFloor = null;
            _trialRoomInfoViewer = null;
            _nextRoomInfoViewerSearchTime = 0f;
            _cachedGameOverLabel = null;
            _nextGameOverLabelSearchTime = 0f;
            _lastGameOverCheck = 0f;
            // Floor changes and scene changes are not always delivered in the
            // same frame.  Defer one frame so the local avatar's synced floor
            // GUID has a chance to update before deciding whether this UI is
            // still appropriate for the newly active scene.
            CoroutineManager.Instance.StartCoroutine(CleanupTrialUIAfterSceneChange());
        }

        private static IEnumerator CleanupTrialUIAfterSceneChange()
        {
            yield return null;
            if (!IsLocalPlayerInTrialFloor())
                CleanupTrialUI();
        }

        private static void UpdateTrialUIForFloorChange()
        {
            PlayerAvatar? localPlayer = _cachedLocalPlayer;
            if (localPlayer == null || !localPlayer.isLocalPlayer)
            {
                if (!NetworkClient.active)
                {
                    _cachedLocalPlayer = null;
                    if (_trialLevelCapActive && Time.unscaledTime >= _trialLevelCapPendingFloorUntil)
                        RestoreNativeLevelCap();
                    return;
                }
                localPlayer = TryResolveLocalPlayer();
            }
            if (localPlayer == null)
            {
                if (_trialLevelCapActive && Time.unscaledTime >= _trialLevelCapPendingFloorUntil)
                    RestoreNativeLevelCap();
                return;
            }

            string currentGuid = localPlayer.currentFloorGuid ?? string.Empty;
            if (IsTrialFloorGuid(currentGuid))
                _lastTrialFloorSeenAt = Time.unscaledTime;
            UpdateTrialLevelCapForLocalFloor(currentGuid);
            UpdateTrialRoomInfoVisibility(localPlayer, currentGuid);
            if (string.Equals(_lastObservedLocalFloorGuid, currentGuid, StringComparison.Ordinal)) return;

            _lastObservedLocalFloorGuid = currentGuid;
            _nextTrialTabletSearchTime = 0f;
            if (IsTrialBattleFloorGuid(currentGuid))
            {
                TrialController.Instance?.UpdateLocalUIFromFloorTransition();
            }
            else
            {
                CleanupTrialUI();
            }
        }

        internal static bool IsLocalPlayerInTrialFloor()
        {
            if (_cachedLocalPlayer != null && _cachedLocalPlayer.isLocalPlayer)
                return IsTrialFloorGuid(_cachedLocalPlayer.currentFloorGuid);
            if (!NetworkClient.active) return false;
            return IsTrialFloorGuid(TryResolveLocalPlayer()?.currentFloorGuid ?? string.Empty);
        }

        private static PlayerAvatar? TryResolveLocalPlayer()
        {
            if (_cachedLocalPlayer != null && _cachedLocalPlayer.isLocalPlayer)
                return _cachedLocalPlayer;

            NetworkIdentity? identity = NetworkClient.localPlayer;
            if (identity != null)
            {
                PlayerAvatar? direct = identity.GetComponent<PlayerSpawner>()?.PlayerAvatar ??
                    identity.GetComponent<PlayerAvatar>();
                if (direct != null && direct.isLocalPlayer)
                {
                    _cachedLocalPlayer = direct;
                    return direct;
                }
            }

            if (Time.unscaledTime < _nextLocalPlayerSearchTime) return null;
            _nextLocalPlayerSearchTime = Time.unscaledTime + 1f;
            foreach (PlayerAvatar player in UnityEngine.Object.FindObjectsByType<PlayerAvatar>(FindObjectsSortMode.None))
                if (player != null && player.isLocalPlayer)
                {
                    _cachedLocalPlayer = player;
                    return player;
                }
            return null;
        }

        public static bool IsGameOver()
        {
            if (Time.unscaledTime - _lastGameOverCheck >= 0.2f)
            {
                UI_GameOverLabel? label = FindTrialGameOverLabel();
                _cachedGameOver = label != null && label.gameObject.activeInHierarchy;
                _lastGameOverCheck = Time.unscaledTime;
            }
            return _cachedGameOver;
        }

        private static UI_GameOverLabel? FindTrialGameOverLabel()
        {
            if (_cachedGameOverLabel != null) return _cachedGameOverLabel;
            if (Time.unscaledTime < _nextGameOverLabelSearchTime) return null;
            _nextGameOverLabelSearchTime = Time.unscaledTime + 1f;

            UIManager? manager = UIManager.Instance;
            if (manager != null)
                _cachedGameOverLabel = manager.GetElement<UI_GameOverLabel>();
            if (_cachedGameOverLabel == null)
            {
                GameObject? labelObject = GameObject.Find("GameOverLabel");
                if (labelObject != null)
                    _cachedGameOverLabel = labelObject.GetComponent<UI_GameOverLabel>() ??
                        labelObject.GetComponentInParent<UI_GameOverLabel>();
            }
            return _cachedGameOverLabel;
        }

        public static void CleanupTrialRewards()
        {
            if (!NetworkServer.active) return;
            for (int i = SpawnedRewards.Count - 1; i >= 0; i--)
            {
                GameObject obj = SpawnedRewards[i];
                if (obj == null || obj == TrialTablet) continue;
                UnregisterTrialFloorObject(obj);
                NetworkServer.Destroy(obj);
            }
            SpawnedRewards.Clear();

            foreach (GameObject go in UnityEngine.Object.FindObjectsByType<GameObject>(FindObjectsSortMode.None))
            {
                if (go == null || go == TrialTablet) continue;
                if (go.name.Contains("AltarOfTablet") || go.name.Contains("AltarOfEnchant") || go.name.Contains("SephiriteSpawner") || go.name.Contains("TabletMix") || go.name.Contains("HPDispenser"))
                {
                    UnregisterTrialFloorObject(go);
                    NetworkServer.Destroy(go);
                }
            }
        }

        public static void SpawnTrialDummies()
        {
            if (!NetworkServer.active) return;
            ClearTrialDummies();

            // TrialRoutine increments CurrentPhase before it places the dummies.
            // They are still in the just-cleared battle floor until the next stage
            // begins, so bind them to the previous phase's floor at milestone edges.
            FloorGenerator? floor = FindLoadedFloor(GetBattleFloorGuidForPhase(
                Mathf.Max(1, (TrialController.Instance?.CurrentPhase ?? 1) - 1)));
            if (floor == null)
            {
                Debug.LogWarning("[시련] 허수아비를 등록할 현재 전투 층을 찾지 못했습니다.");
                return;
            }

            Vector3[] positions = new Vector3[] { TrialAnchorPosition + new Vector3(-11f, -6f, 0f), TrialAnchorPosition + new Vector3(10f, -6f, 0f) };
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
                        float infinityHp = 99999999f;
                        av.NetworkmaxHp = infinityHp;
                        av.Networkhp = infinityHp;
                        Debug.Log("[EndlessMod] 허수아비 체력을 1억으로 설정했습니다.");
                    }

                    NetworkServer.Spawn(dummy);
                    RegisterTrialFloorObject(floor, dummy);
                    ActiveDummies.Add(dummy);
                }
            }
        }

        public static void ClearTrialDummies()
        {
            if (!NetworkServer.active) return;
            foreach (var d in ActiveDummies)
            {
                if (d == null) continue;
                UnregisterTrialFloorObject(d);
                NetworkServer.Destroy(d);
            }
            ActiveDummies.Clear();
        }

        private static TrialMerchantRoomState? ReadTrialMerchantRoomState(int phase)
        {
            string json = SaveManager.CurrentRun?.GetString(TrialMerchantRoomStateKey, string.Empty) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                TrialMerchantRoomState? state = JsonConvert.DeserializeObject<TrialMerchantRoomState>(json);
                return state?.phase == phase ? state : null;
            }
            catch (Exception exception)
            {
                Debug.LogError("[시련] 상인 저장 정보 읽기 실패: " + exception);
                return null;
            }
        }

        private static TrialMerchantState? CaptureTrialMerchantState(GameObject? merchant)
        {
            UnitAI_NewBasic? ai = merchant != null ? merchant.GetComponent<UnitAI_NewBasic>() : null;
            UnitAvatar? avatar = ai?.Avatar;
            GridInventory? inventory = ai?.NetworkMySafe?.Inventory ?? avatar?.Inventory;
            if (ai == null || avatar == null || inventory == null) return null;

            var state = new TrialMerchantState
            {
                randomId = avatar.RandomID,
                money = avatar.Money,
                replenishmentTryCount = ai.replenishmentTryCount
            };
            foreach (NewItemOwnInstance item in inventory.inventoryMatrix.Values)
            {
                if (item == null) continue;
                state.items.Add(new TrialMerchantItemState
                {
                    instanceId = item.InstanceID,
                    entityId = item.EntityID,
                    quantity = item.Quantity,
                    x = item.XIdx,
                    y = item.YIdx
                });
            }
            foreach (UnitAI_NewBasic.ReplenishmentItem item in ai.replenishments)
                state.replenishments.Add(new TrialMerchantReplenishmentState
                    { entityId = item.entityID, purchased = item.purchased });
            return state;
        }

        private static void CaptureTrialMerchantRoomState()
        {
            if (!NetworkServer.active || SaveManager.CurrentRun == null) return;
            int phase = SaveManager.CurrentRun.GetInt(TrialRewardIssuedPhaseKey, 0);
            if (phase <= 0) return;
            GameObject? regular = ActiveTrialMerchants.FirstOrDefault(obj => obj != null &&
                obj.name == "TrialMerchant_Phase_" + phase);
            GameObject? witch = ActiveTrialMerchants.FirstOrDefault(obj => obj != null &&
                obj.name == "TrialWitchHat_Phase_" + phase);
            if (regular == null && witch == null)
            {
                FloorGenerator? floor = FindLoadedFloor(GetRewardFloorGuidForPhase(phase));
                if (floor == null ||
                    !PreparedRewardRoomPhases.TryGetValue(floor.guid, out int preparedPhase) ||
                    preparedPhase != phase ||
                    !PreparedRewardRoomFloorIds.TryGetValue(floor.guid, out int preparedFloorId) ||
                    preparedFloorId != floor.GetInstanceID()) return;
            }

            TrialMerchantState? regularState = CaptureTrialMerchantState(regular);
            TrialMerchantState? witchState = CaptureTrialMerchantState(witch);
            if ((regular != null && regularState == null) || (witch != null && witchState == null))
            {
                Debug.LogWarning($"[시련] {phase}단계 상인 상태를 아직 읽을 수 없어 기존 기록을 보존합니다.");
                return;
            }
            var state = new TrialMerchantRoomState
                { phase = phase, regular = regularState, witch = witchState };
            SaveManager.CurrentRun.SetString(TrialMerchantRoomStateKey, JsonConvert.SerializeObject(state));
        }

        private static void RestoreTrialMerchantState(string objectName, TrialMerchantState state)
        {
            GameObject? merchant = ActiveTrialMerchants.FirstOrDefault(obj => obj != null && obj.name == objectName);
            UnitAI_NewBasic? ai = merchant != null ? merchant.GetComponent<UnitAI_NewBasic>() : null;
            UnitAvatar? avatar = ai?.Avatar;
            GridInventory? inventory = ai?.NetworkMySafe?.Inventory ?? avatar?.Inventory;
            if (ai == null || avatar == null || inventory == null)
            {
                Debug.LogError($"[시련] 상인 복원 대상의 인벤토리를 찾지 못했습니다: {objectName}");
                return;
            }

            avatar.SetMoney(state.money);
            inventory.ForceRemoveAll();
            foreach (TrialMerchantItemState item in state.items ?? new List<TrialMerchantItemState>())
            {
                if (item.quantity <= 0 || ItemDatabase.FindItemById(item.entityId) == null) continue;
                inventory.AddItemAtPosition(new ItemMetadata(item.instanceId, item.entityId, item.quantity),
                    new ItemPosition(item.x, item.y), notification: false);
            }
            ai.replenishments.Clear();
            foreach (TrialMerchantReplenishmentState item in state.replenishments ?? new List<TrialMerchantReplenishmentState>())
                ai.replenishments.Add(new UnitAI_NewBasic.ReplenishmentItem(item.entityId, item.purchased));
            ai.replenishmentTryCount = state.replenishmentTryCount;
        }

        public static void SpawnTrialMerchantByClone(Vector3 position, int phase, FloorGenerator? floor = null,
            int? savedRandomId = null)
        {
            if (!NetworkServer.active) return;
            if (dbCache == null || dbCache.Count == 0) LoadDatabase();
            floor ??= FindLoadedFloor(GetRewardFloorGuidForPhase(phase));
            if (floor == null)
            {
                Debug.LogError($"[시련] 상인을 등록할 보상 층을 찾지 못했습니다: phase={phase}");
                return;
            }

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
                avatar.SetRandomID(savedRandomId ?? UnityEngine.Random.Range(0, int.MaxValue));

            NetworkServer.Spawn(clone);
            clone.SetActive(true);
            RegisterTrialFloorObject(floor, clone);

            UnitAI_NewBasic? ai = clone.GetComponent<UnitAI_NewBasic>();
            if (ai != null)
            {
                string uniqueSocialID = $"TrialMerchant_{phase}_{UnityEngine.Random.Range(0, 10000)}";
                // SetSocialID stores this in Avatar.defaultNameKey. The game's
                // Avatar.Name resolves the key in each client's current language.
                ai.SetSocialID(uniqueSocialID, "trial.merchant.name", EPersonality.Rational, EFactionAlignment.Good, "Merchant", EProceduralMerchantType.Vendor, 1000 + (phase * 100), null);

                if (ai.NetworkMySafe != null)
                {
                    RegisterTrialFloorObject(floor, ai.NetworkMySafe.gameObject);
                    ActiveTrialMerchants.Add(ai.NetworkMySafe.gameObject);
                }
            }
            ActiveTrialMerchants.Add(clone);
        }

        // Uses the exact SocialID and spawn path used by the game's random
        // WitchHat rooms.  In particular, UnitAI_WitchHat creates its native safe
        // and fills it with the real WitchHat item selection after SetSocialID.
        internal static bool TrySpawnTrialWitchHat(Vector3 spawnPosition, int phase, FloorGenerator? floor = null,
            int? savedRandomId = null)
        {
            if (!NetworkServer.active)
            {
                Debug.LogWarning("[시련] WitchHat 생성 요청이 서버가 아닌 곳에서 호출되었습니다.");
                return false;
            }
            floor ??= FindLoadedFloor(GetRewardFloorGuidForPhase(phase));
            if (floor == null)
            {
                Debug.LogWarning($"[시련] WitchHat을 등록할 보상 층을 찾지 못했습니다: phase={phase}");
                return false;
            }

            try
            {
                // 1. 원본 SocialID 데이터 가져오기
                SocialIDEntity socialID = SocialIDDatabase.FindByName("WitchHat");

                if (socialID == null)
                {
                    Debug.LogError(
                        "[시련] SocialIDDatabase에서 'WitchHat'을 찾지 못했습니다."
                    );
                    return false;
                }

                if (socialID.avatarPrefab == null)
                {
                    Debug.LogError(
                        "[시련] WitchHat SocialIDEntity의 avatarPrefab이 null입니다."
                    );
                    return false;
                }

                spawnPosition.z = 0f;

                Debug.Log(
                    $"[시련] WitchHat 생성 시작: " +
                    $"ID={socialID.name}, " +
                    $"Prefab={socialID.avatarPrefab.name}, " +
                    $"MerchantType={socialID.proceduralMerchantType}"
                );

                // 2. 게임 원본과 동일하게 avatarPrefab 생성
                GameObject gameObject =
                    UnityEngine.Object.Instantiate<GameObject>(
                        socialID.avatarPrefab,
                        spawnPosition,
                        Quaternion.identity
                    );

                if (gameObject == null)
                {
                    Debug.LogError("[시련] WitchHat avatarPrefab Instantiate 실패.");
                    return false;
                }
        

                UnitAvatar avatar = gameObject.GetComponent<UnitAvatar>();

                if (avatar == null)
                {
                   Debug.LogError(
                        $"[시련] WitchHat prefab에 UnitAvatar가 없습니다. " +
                        $"Prefab={socialID.avatarPrefab.name}"
                    );

                    UnityEngine.Object.Destroy(gameObject);
                    return false;
                }

                UnitAI_NewBasic ai =
                    gameObject.GetComponent<UnitAI_NewBasic>();

                if (ai == null)
                {
                    Debug.LogError(
                        $"[시련] WitchHat prefab에 UnitAI_NewBasic이 없습니다. " +
                        $"Prefab={socialID.avatarPrefab.name}"
                    );

                    UnityEngine.Object.Destroy(gameObject);
                    return false;
                }
                NetworkServer.Spawn(gameObject);
                RegisterTrialFloorObject(floor, gameObject);

                avatar.SetRandomID(savedRandomId ?? UnityEngine.Random.Range(int.MinValue, int.MaxValue));

                if (socialID.startingFaction != null)
                {
                    avatar.ChangeFaction(socialID.startingFaction.name);
                }
                else
                {
                    Debug.LogWarning(
                        "[시련] WitchHat startingFaction이 null입니다."
                    );
                }

                string roleName =
                    socialID.npcRole != null
                        ? socialID.npcRole.name
                        : "";

                ai.SetSocialID(
                    socialID.name,
                    socialID.aName.key,
                    socialID.personality,
                    socialID.alignment,
                    roleName,
                    socialID.proceduralMerchantType,
                    socialID.startingMoney,
                    socialID.startingItems
                );

                // 원본 NPC 생성 코드와 동일
                if (socialID.overridePeacrfulAIType)
                {
                    ai.peacefulAIType = socialID.peacefulAIType;
                }

                if (ai.NetworkMySafe == null)
                {
                    Debug.LogError("[시련] WitchHat의 거래 금고가 생성되지 않아 예약을 유지합니다.");
                    UnregisterTrialFloorObject(gameObject);
                    NetworkServer.Destroy(gameObject);
                    return false;
                }

                ActiveTrialMerchants.Add(gameObject);
                RegisterTrialFloorObject(floor, ai.NetworkMySafe.gameObject);
                ActiveTrialMerchants.Add(ai.NetworkMySafe.gameObject);

                gameObject.name =
                    $"TrialWitchHat_Phase_{phase}";


                Debug.Log(
                    $"[시련] WitchHat 생성 성공: " +
                    $"Phase={phase}, " +
                    $"SocialID={socialID.name}, " +
                    $"MerchantType={socialID.proceduralMerchantType}, " +
                    $"Position={spawnPosition}"
                );
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError(
                    $"[시련] WitchHat 마녀 상인 생성 중 오류:\n{ex}"
                );
                return false;
            }
        }

        // Reuse the game's real inventory-upgrade object instead of inventing a
        // parallel currency system.  InventoryShop already charges the interacting
        // player, grants the permanent INVENTORY_SLOT status, and replicates through
        // Mirror; the trial only chooses its location and price.
        public static void SpawnTrialSupplyTerminal(Vector3 position, int phase, FloorGenerator? floor = null)
        {
            SpawnTrialSupplyTerminalRestored(position, phase, floor, null);
        }

        private static void SpawnTrialSupplyTerminalRestored(Vector3 position, int phase,
            FloorGenerator? floor, TrialIndividualRewardSourceState? restored)
        {
            if (!NetworkServer.active) return;
            floor ??= FindLoadedFloor(GetRewardFloorGuidForPhase(phase));
            if (floor == null)
            {
                Debug.LogError($"[시련] 보급 단말을 등록할 보상 층을 찾지 못했습니다: phase={phase}");
                return;
            }

            PropEntity? entity = PropDatabase.FindPropById("InventoryShop");
            if (entity?.propPrefab == null)
            {
                Debug.LogWarning("[시련] 보급 단말(InventoryShop) 프리팹을 찾지 못했습니다.");
                return;
            }

            GameObject terminal = UnityEngine.Object.Instantiate(entity.propPrefab, position, Quaternion.identity);
            terminal.name = "TrialSupplyTerminal_Phase_" + phase;
            int randomId = restored?.randomId ?? UnityEngine.Random.Range(int.MinValue, int.MaxValue);
            foreach (IRandomID randomizable in terminal.GetComponents<IRandomID>())
                randomizable.SetRandomID(randomId);
            RegisterTrialIndividualRewardSource(terminal, phase, "InventoryShop", randomId);

            InventoryShop? shop = terminal.GetComponent<InventoryShop>();
            if (shop != null)
            {
                // The native shop normally appears only in later chapters and has a
                // random appearance rate.  The trial terminal is deliberately always
                // available, while its cost rises slowly with each boss milestone.
                shop.appearRate = 1f;
                shop.storage = 1;
                shop.price = Mathf.Min(500 + ((phase / 5 - 1) * 500), 50000);
            }

            NetworkServer.Spawn(terminal);
            if (restored != null) ApplyTrialIndividualRewardSourceState(terminal, restored);
            terminal.SetActive(true);
            RegisterTrialFloorObject(floor, terminal);

            if (shop != null)
            {
                // OnStartServer may have disabled it because this is not chapter 6.
                // Restore the synced flag after spawning so every client sees and can
                // use the exact same native inventory purchase flow.
                shop.Networkactivated = true;
                if (shop.shopObject != null) shop.shopObject.SetActive(true);
                if (shop.orbImage != null) shop.orbImage.SetActive(true);
                if (shop.interactable != null) shop.interactable.enabled = true;
            }

            ActiveTrialMerchants.Add(terminal);
            Debug.Log($"[시련] 보급 단말 생성: {shop?.price ?? 0} 골드, 단계 {phase}");
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
            foreach (var obj in ActiveTrialMerchants)
            {
                if (obj == null) continue;
                UnregisterTrialFloorObject(obj);
                NetworkServer.Destroy(obj);
            }
            ActiveTrialMerchants.Clear();
            foreach (var spawner in ActiveTrialSocialSpawners)
                if (spawner != null) UnityEngine.Object.Destroy(spawner);
            ActiveTrialSocialSpawners.Clear();
        }

        private void UpdateTrialGameOverState()
        {
            bool isGameOverNow = IsGameOver();
            switch (_currentTrialGameOverState)
            {
                case GameOverState.None:
                    if (isGameOverNow)
                    {
                        _trialGameOverPlaceActive = IsLocalPlayerInTrialFloor() ||
                            (_lastObservedLocalFloorGuid != null && IsTrialFloorGuid(_lastObservedLocalFloorGuid));
                        if (_trialGameOverPlaceActive)
                        {
                            _trialLevelCapPendingFloorUntil = 0f;
                            _trialLevelCapSuppressed = true;
                            RestoreNativeLevelCap();
                        }
                        _currentTrialGameOverState = GameOverState.Shown;
                        CleanupTrialUI();
                        CleanupTrialRewards();
                        CleanupTrialMerchants();
                        ClearTrialDummies();

                        if (TrialController.Instance != null)
                        {
                            if (NetworkServer.active) TrialController.Instance.ResetTrial(true);
                            else TrialNetworkBridge.SendAction(TrialNetworkBridge.NotifyGameOver);
                        }
                    }
                    break;
                case GameOverState.Shown:
                    if (!isGameOverNow)
                    {
                        _currentTrialGameOverState = GameOverState.None;
                        _trialGameOverPlaceActive = false;
                    }
                    break;
            }
        }

        private static void UpdateTrialRoomInfoVisibility(PlayerAvatar localPlayer, string currentGuid)
        {
            bool inTrial = IsTrialFloorGuid(currentGuid);
            if (!inTrial && !_trialRoomInfoHidden) return;
            if (_trialRoomInfoViewer == null)
            {
                if (Time.unscaledTime < _nextRoomInfoViewerSearchTime) return;
                _nextRoomInfoViewerSearchTime = Time.unscaledTime + 1f;
                _trialRoomInfoViewer = UIManager.Instance != null
                    ? UIManager.Instance.GetElement<UI_HUDMultiplayerRoomViewer>() : null;
                if (_trialRoomInfoViewer == null)
                    _trialRoomInfoViewer = UnityEngine.Object.FindFirstObjectByType<UI_HUDMultiplayerRoomViewer>();
            }
            CanvasGroup? canvasGroup = _trialRoomInfoViewer?.canvasGroup;
            if (canvasGroup == null) return;

            if (inTrial)
            {
                // Native UI_HUDMultiplayerRoomViewer.HandleIsInDungeonChanged
                // uses the same CanvasGroup when entering a normal dungeon.
                if (!_trialRoomInfoHidden || canvasGroup.alpha != 0f)
                    canvasGroup.alpha = 0f;
                _trialRoomInfoHidden = true;
            }
            else if (_trialRoomInfoHidden)
            {
                canvasGroup.alpha = localPlayer.isInDungeon > 0 ? 0f : 1f;
                _trialRoomInfoHidden = false;
            }
        }

        private static void UpdateTrialGameOverPlaceName()
        {
            if (!_trialGameOverPlaceActive) return;
            UI_GameOverLabel? label = FindTrialGameOverLabel();
            if (label?.placeNameText != null)
            {
                string placeName = GetSafeText("trial.place.name", "시련의 탑");
                if (label.placeNameText.text != placeName)
                    label.placeNameText.text = placeName;
            }
        }

        public static void SetAllPlayersBattleState(bool isInBattle)
        {
            if (!NetworkServer.active)
            {
                TrialBattleParticipants.Clear();
                TrialBattleEligiblePlayers.Clear();
                TrialBattleParticipantsToRelease.Clear();
                return;
            }

            if (isInBattle)
                ReconcileTrialPlayerBattleState();
            else
                ReleaseTrialPlayerBattleState();
        }

        private static void ReconcileTrialPlayerBattleState()
        {
            TrialController? controller = TrialController.Instance;
            if (controller == null || !controller.IsTrialRunning)
            {
                ReleaseTrialPlayerBattleState();
                return;
            }

            string battleGuid = GetBattleFloorGuidForPhase(controller.CurrentPhase);
            TrialBattleEligiblePlayers.Clear();
            foreach (NetworkConnectionToClient connection in NetworkServer.connections.Values)
            {
                PlayerAvatar? player = connection?.identity != null
                    ? connection.identity.GetComponent<PlayerAvatar>() : null;
                if (player != null && player.isServer && !player.IsDead &&
                    player.currentFloorGuid == battleGuid)
                    TrialBattleEligiblePlayers.Add(player);
            }

            TrialBattleParticipantsToRelease.Clear();
            foreach (PlayerAvatar participant in TrialBattleParticipants)
                if (participant == null || !TrialBattleEligiblePlayers.Contains(participant))
                    TrialBattleParticipantsToRelease.Add(participant);
            foreach (PlayerAvatar participant in TrialBattleParticipantsToRelease)
                ReleaseTrialPlayerBattleState(participant);
            TrialBattleParticipantsToRelease.Clear();

            foreach (PlayerAvatar player in TrialBattleEligiblePlayers)
            {
                if (TrialBattleParticipants.Contains(player)) continue;
                try
                {
                    player.StartBattle();
                    TrialBattleParticipants.Add(player);
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[EndlessMod] 시련 전투 상태 시작 실패: {ex.Message}");
                }
            }
            TrialBattleEligiblePlayers.Clear();
        }

        private static void ReleaseTrialPlayerBattleState()
        {
            TrialBattleParticipantsToRelease.Clear();
            TrialBattleParticipantsToRelease.AddRange(TrialBattleParticipants);
            foreach (PlayerAvatar participant in TrialBattleParticipantsToRelease)
                ReleaseTrialPlayerBattleState(participant);
            TrialBattleParticipantsToRelease.Clear();
            TrialBattleEligiblePlayers.Clear();
        }

        private static void ReleaseTrialPlayerBattleState(PlayerAvatar participant)
        {
            if (participant == null || !NetworkServer.active)
            {
                TrialBattleParticipants.Remove(participant);
                return;
            }
            try
            {
                if (participant.isInBattle > 0)
                    participant.StopBattle();
                TrialBattleParticipants.Remove(participant);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[EndlessMod] 시련 전투 상태 해제 실패: {ex.Message}");
            }
        }
    }
}
