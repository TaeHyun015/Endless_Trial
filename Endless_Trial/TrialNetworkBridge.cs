using Mirror;
using UnityEngine;

namespace SephiriaTrial
{
    // Mod assemblies built by dotnet do not run through Mirror's Weaver.
    // Register serializers and message handlers explicitly for client actions
    // and server notifications that cannot use generated Command/Rpc wrappers.
    internal static class TrialNetworkBridge
    {
        internal const byte MoveToReward = 1;
        internal const byte MoveToBattle = 2;
        internal const byte ClaimReward = 3;
        internal const byte RecordMysticPotUses = 4;
        internal const byte NotifyGameOver = 5;

        internal const byte SystemMessage = 1;
        internal const byte RewardClaims = 2;
        internal const byte ClearRewardClaims = 3;
        internal const byte RestorePlaytime = 4;
        internal const byte StopPlaytime = 5;
        internal const byte GameOver = 6;

        private struct ClientAction : NetworkMessage
        {
            public byte operation;
            public int phase;
            public int value;
            public string? text;
        }

        private struct ServerNotice : NetworkMessage
        {
            public byte operation;
            public int phase;
            public float seconds;
            public string? text;
        }

        private static bool serializersRegistered;
        private static bool serverHandlerRegistered;
        private static bool clientHandlerRegistered;
        private static NetworkConnectionToClient? registeredServerLocalConnection;
        private static NetworkConnectionToServer? registeredClientConnection;

        internal static void EnsureRegistered()
        {
            if (!serializersRegistered)
            {
                Writer<ClientAction>.write = WriteClientAction;
                Reader<ClientAction>.read = ReadClientAction;
                Writer<ServerNotice>.write = WriteServerNotice;
                Reader<ServerNotice>.read = ReadServerNotice;
                serializersRegistered = true;
            }

            if (NetworkServer.active && (!serverHandlerRegistered ||
                !object.ReferenceEquals(registeredServerLocalConnection, NetworkServer.localConnection)))
            {
                if (serverHandlerRegistered)
                    NetworkServer.UnregisterHandler<ClientAction>();
                NetworkServer.RegisterHandler<ClientAction>(OnServerAction);
                serverHandlerRegistered = true;
                registeredServerLocalConnection = NetworkServer.localConnection;
            }
            else if (!NetworkServer.active && serverHandlerRegistered)
            {
                NetworkServer.UnregisterHandler<ClientAction>();
                serverHandlerRegistered = false;
                registeredServerLocalConnection = null;
            }

            if (NetworkClient.active && (!clientHandlerRegistered ||
                !object.ReferenceEquals(registeredClientConnection, NetworkClient.connection)))
            {
                if (clientHandlerRegistered)
                    NetworkClient.UnregisterHandler<ServerNotice>();
                NetworkClient.RegisterHandler<ServerNotice>(OnClientNotice);
                clientHandlerRegistered = true;
                registeredClientConnection = NetworkClient.connection;
            }
            else if (!NetworkClient.active && clientHandlerRegistered)
            {
                NetworkClient.UnregisterHandler<ServerNotice>();
                clientHandlerRegistered = false;
                registeredClientConnection = null;
            }
        }

        internal static void Shutdown()
        {
            if (serverHandlerRegistered)
                NetworkServer.UnregisterHandler<ClientAction>();
            if (clientHandlerRegistered)
                NetworkClient.UnregisterHandler<ServerNotice>();
            serverHandlerRegistered = false;
            clientHandlerRegistered = false;
            registeredServerLocalConnection = null;
            registeredClientConnection = null;
        }

        internal static void SendAction(byte operation, int phase = 0, int value = 0, string text = "")
        {
            if (!NetworkClient.active) return;
            EnsureRegistered();
            NetworkClient.Send(new ClientAction
            {
                operation = operation,
                phase = phase,
                value = value,
                text = text
            });
        }

        internal static void BroadcastNotice(byte operation, int phase = 0, float seconds = 0f, string text = "")
        {
            if (!NetworkServer.active) return;
            EnsureRegistered();
            ServerNotice notice = new ServerNotice
            {
                operation = operation,
                phase = phase,
                seconds = seconds,
                text = text
            };
            foreach (NetworkConnectionToClient connection in NetworkServer.connections.Values)
                if (connection != null && connection != NetworkServer.localConnection)
                    connection.Send(notice);
        }

        internal static void SendNoticeTo(NetworkConnectionToClient? connection, byte operation,
            int phase = 0, float seconds = 0f, string text = "")
        {
            if (!NetworkServer.active || connection == null) return;
            EnsureRegistered();
            ServerNotice notice = new ServerNotice
            {
                operation = operation,
                phase = phase,
                seconds = seconds,
                text = text
            };
            if (connection == NetworkServer.localConnection)
                OnClientNotice(notice);
            else
                connection.Send(notice);
        }

        private static void OnServerAction(NetworkConnectionToClient sender, ClientAction action)
        {
            if (sender?.identity == null || TrialController.Instance == null) return;
            switch (action.operation)
            {
                case MoveToReward:
                    TrialController.Instance.CmdMoveLocalPlayerToRewardFloor(sender);
                    break;
                case MoveToBattle:
                    TrialController.Instance.CmdMoveLocalPlayerToBattleFloor(sender);
                    break;
                case ClaimReward:
                    EndlessMod.RecordTrialIndividualRewardClaimFromConnection(
                        action.phase, action.text ?? string.Empty, sender);
                    break;
                case RecordMysticPotUses:
                    EndlessMod.RecordTrialMysticPotUsesFromConnection(
                        action.phase, action.value, sender);
                    break;
                case NotifyGameOver:
                    PlayerAvatar? senderAvatar = sender.identity.GetComponent<PlayerAvatar>();
                    if (senderAvatar == null ||
                        !EndlessMod.IsTrialFloorGuid(senderAvatar.currentFloorGuid)) return;
                    bool hasTrialPlayer = false;
                    foreach (NetworkConnectionToClient connection in NetworkServer.connections.Values)
                    {
                        PlayerAvatar? avatar = connection?.identity != null
                            ? connection.identity.GetComponent<PlayerAvatar>() : null;
                        if (avatar == null || !EndlessMod.IsTrialFloorGuid(avatar.currentFloorGuid))
                            continue;
                        hasTrialPlayer = true;
                        if (!avatar.IsDead) return;
                    }
                    if (hasTrialPlayer) TrialController.Instance.CmdNotifyGameOver();
                    break;
                default:
                    Debug.LogWarning($"[시련] 알 수 없는 클라이언트 요청: {action.operation}");
                    break;
            }
        }

        private static void OnClientNotice(ServerNotice notice)
        {
            switch (notice.operation)
            {
                case SystemMessage:
                    EndlessMod.ShowLocalizedSystemMessage(notice.text ?? string.Empty, notice.phase);
                    break;
                case RewardClaims:
                    EndlessMod.ReceiveTrialIndividualRewardClaims(notice.phase, notice.text ?? string.Empty);
                    break;
                case ClearRewardClaims:
                    EndlessMod.ClearTrialLocalRewardClaims();
                    break;
                case RestorePlaytime:
                    if (DungeonManager.Instance != null)
                    {
                        DungeonManager.Instance.playedRealtimeClientside = notice.seconds;
                        DungeonManager.Instance.countPlayedRealtimeClientside = true;
                    }
                    break;
                case StopPlaytime:
                    if (DungeonManager.Instance != null)
                    {
                        DungeonManager.Instance.countPlayedRealtimeClientside = false;
                        DungeonManager.Instance.playedRealtimeClientside = 0f;
                    }
                    break;
                case GameOver:
                    EndlessMod.CleanupTrialUI();
                    TrialController.Instance?.RefreshLocalTrialState();
                    break;
                default:
                    Debug.LogWarning($"[시련] 알 수 없는 서버 알림: {notice.operation}");
                    break;
            }
        }

        private static void WriteClientAction(NetworkWriter writer, ClientAction value)
        {
            writer.WriteByte(value.operation);
            writer.WriteInt(value.phase);
            writer.WriteInt(value.value);
            writer.WriteString(value.text ?? string.Empty);
        }

        private static ClientAction ReadClientAction(NetworkReader reader)
        {
            return new ClientAction
            {
                operation = reader.ReadByte(),
                phase = reader.ReadInt(),
                value = reader.ReadInt(),
                text = reader.ReadString()
            };
        }

        private static void WriteServerNotice(NetworkWriter writer, ServerNotice value)
        {
            writer.WriteByte(value.operation);
            writer.WriteInt(value.phase);
            writer.WriteFloat(value.seconds);
            writer.WriteString(value.text ?? string.Empty);
        }

        private static ServerNotice ReadServerNotice(NetworkReader reader)
        {
            return new ServerNotice
            {
                operation = reader.ReadByte(),
                phase = reader.ReadInt(),
                seconds = reader.ReadFloat(),
                text = reader.ReadString()
            };
        }
    }
}
