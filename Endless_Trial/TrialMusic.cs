using FMODUnity;
using UnityEngine;

namespace SephiriaTrial
{
    public partial class EndlessMod
    {
        // These GUIDs come from the original game's FMOD BGM bank. The credits
        // prefab plays placeTownVillage_Win through SoundManager.PlayBGM.
        private static readonly EventReference TrialBattleMusic = new EventReference
        {
            Guid = new FMOD.GUID
            {
                Data1 = -686542012, Data2 = 1304356135,
                Data3 = 2087711128, Data4 = 275695250
            }
        };

        // The native town theme gives the shared merchant/reward area a rest.
        private static readonly EventReference TrialRewardMusic = new EventReference
        {
            Guid = new FMOD.GUID
            {
                Data1 = -1012032887, Data2 = 1192934651,
                Data3 = -493542487, Data4 = -273576196
            }
        };

        private static float _nextTrialMusicCheckTime;
        private static bool _trialMusicOwned;
        private static EventReference _lastTrialMusicEvent;

        private static void ConfigureTrialFloorMusic(FloorGenerator floor, string guid)
        {
            // GameCamera plays this event when it first starts seeing the floor.
            // Battle room music stays the same while waiting and during a phase.
            // Each player hears the BGM for the room their camera is seeing.
            floor.bgmSoundEvent = IsTrialRewardFloorGuid(guid)
                ? TrialRewardMusic : TrialBattleMusic;
        }

        private static void UpdateTrialMusic()
        {
            if (Time.unscaledTime < _nextTrialMusicCheckTime) return;
            _nextTrialMusicCheckTime = Time.unscaledTime + 0.2f;

            SoundManager? sound = SoundManager.Instance;
            if (sound == null) return;
            FloorGenerator? cameraFloor = GameCamera.Instance?.CurrentSeeingFloor;
            PlayerAvatar? player = _cachedLocalPlayer;
            if (player == null || !player.isLocalPlayer)
            {
                if (_trialMusicOwned && (cameraFloor == null || !IsTrialFloorGuid(cameraFloor.guid)))
                    ReleaseTrialMusic(sound, cameraFloor);
                return;
            }

            string guid = player.currentFloorGuid ?? string.Empty;
            if (!IsTrialFloorGuid(guid))
            {
                // Wait for GameCamera to finish its native floor transition.
                if (_trialMusicOwned && (cameraFloor == null || cameraFloor.guid == guid))
                    ReleaseTrialMusic(sound, cameraFloor);
                return;
            }
            if (cameraFloor == null || cameraFloor.guid != guid) return;

            if (IsGameOver())
            {
                if (_trialMusicOwned) ReleaseTrialMusic(sound, null);
                return;
            }

            EventReference target = IsTrialRewardFloorGuid(guid)
                ? TrialRewardMusic : TrialBattleMusic;

            if (!sound.currentPlayingBGMEvent.Guid.Equals(target.Guid))
            {
                sound.PlayBGM(target);
            }
            else
            {
                // The credits theme is a one-shot event. SoundManager compares
                // only GUIDs, so it will not restart a finished event by itself.
                bool finished = !sound.CurrentPlayingBGM.isValid();
                if (!finished &&
                    sound.CurrentPlayingBGM.getPlaybackState(out FMOD.Studio.PLAYBACK_STATE state) == FMOD.RESULT.OK)
                    finished = state == FMOD.Studio.PLAYBACK_STATE.STOPPED;
                if (finished)
                {
                    sound.StopBGM();
                    sound.PlayBGM(target);
                }
            }

            _lastTrialMusicEvent = target;
            _trialMusicOwned = true;
        }

        private static void ReleaseTrialMusic(SoundManager sound, FloorGenerator? nativeFloor)
        {
            if (_trialMusicOwned &&
                sound.currentPlayingBGMEvent.Guid.Equals(_lastTrialMusicEvent.Guid))
            {
                if (nativeFloor != null && !IsTrialFloorGuid(nativeFloor.guid))
                    sound.PlayBGM(nativeFloor.bgmSoundEvent);
                else
                    sound.StopBGM();
            }
            _trialMusicOwned = false;
            _lastTrialMusicEvent = default;
        }

        private static void ResetTrialMusic()
        {
            SoundManager? sound = SoundManager.Instance;
            FloorGenerator? cameraFloor = GameCamera.Instance?.CurrentSeeingFloor;
            if (sound != null && _trialMusicOwned)
                ReleaseTrialMusic(sound, cameraFloor);
            _trialMusicOwned = false;
            _lastTrialMusicEvent = default;
            _nextTrialMusicCheckTime = 0f;
        }
    }
}
