using System;
using Team17.Online;

namespace DIYLevelFastInit
{

    /// <summary>Owns deferred message copies and permits one delivery of the current request.</summary>
    internal sealed class ClientLoadQueue
    {
        internal sealed class Request
        {
            internal object Mailbox, Peer, Connection;
            internal IOnlineMultiplayerSessionUserId Sender;
            internal LevelLoadByNameMessage Message;
            internal string Path;
            internal uint Sequence;
            internal bool Reliable;
            internal bool StartAnnounced;
        }

        internal Request Pending, Replaying;

        internal bool Enqueue(object mailbox, object peer, object connection, IOnlineMultiplayerSessionUserId sender,
            LevelLoadByNameMessage message, string path, uint sequence, bool reliable)
        {
            Request old = Pending;
            if (old != null && ReferenceEquals(old.Mailbox, mailbox) && ReferenceEquals(old.Peer, peer) && ReferenceEquals(old.Connection, connection)
                && old.Message.m_Scene == message.m_Scene && old.Message.m_StartLoadGameState == message.m_StartLoadGameState
                && old.Message.m_HideLoadingScreenGameState == message.m_HideLoadingScreenGameState
                && old.Message.UseLoadingScreen == message.UseLoadingScreen) return false;
            LevelLoadByNameMessage copy = new LevelLoadByNameMessage();
            copy.Initialise(message.m_StartLoadGameState, message.m_HideLoadingScreenGameState, message.m_Scene, message.UseLoadingScreen);
            Pending = new Request { Mailbox = mailbox, Peer = peer, Connection = connection, Sender = sender,
                Message = copy, Path = path, Sequence = sequence, Reliable = reliable };
            return true;
        }

        internal bool IsCurrent(Request request) { return request != null && ReferenceEquals(Pending, request); }
        internal void Cancel() { Pending = null; }

        internal bool AnnounceStart(Request request, Action<GameState> send)
        {
            if (!IsCurrent(request) || request.StartAnnounced) return false;
            send(request.Message.m_StartLoadGameState);
            request.StartAnnounced = true;
            return true;
        }

        internal bool AllowsState(GameState state)
        {
            if (Pending != null && state == GameState.LoadedCampaignMapSave) return false;
            return Replaying == null || !Replaying.StartAnnounced || state != Replaying.Message.m_StartLoadGameState;
        }

        internal bool Deliver(Request request, Action<Request> dispatch)
        {
            if (!IsCurrent(request) || Replaying != null) return false;
            Replaying = request;
            try { dispatch(request); return true; }
            finally
            {
                Replaying = null;
                if (IsCurrent(request)) Pending = null;
            }
        }
    }
}
