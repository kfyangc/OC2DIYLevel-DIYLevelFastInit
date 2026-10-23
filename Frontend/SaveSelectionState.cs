using System;
using System.Collections;

namespace DIYLevelFastInit
{

    /// <summary>Retains a confirmed choice across duplicate frontend requests.</summary>
    internal sealed class SaveSelectionState
    {
        private object _confirmedFlow, _requestFlow;
        private bool _diyRequest;
        private long _revision;
        internal long Revision { get { return _revision; } }
        internal void ObserveRequest(object flow, int dlc)
        {
            if (!ReferenceEquals(flow, _requestFlow) || dlc != 15 || !_diyRequest)
            {
                _confirmedFlow = null;
                _revision++;
            }
            _requestFlow = flow;
            _diyRequest = dlc == 15;
        }
        internal bool IsDiyRequest(object flow) { return flow != null && ReferenceEquals(flow, _requestFlow) && _diyRequest; }
        internal static bool IsEntryActive(bool remoteClient, bool resourcesPending, bool currentTransition)
        {
            return remoteClient ? currentTransition : resourcesPending;
        }
        internal void Confirm(object flow) { ObserveRequest(flow, 15); _confirmedFlow = flow; }
        internal void Reset() { _confirmedFlow = null; _requestFlow = null; _diyRequest = false; _revision++; }
        internal bool IsConfirmed(object flow) { return flow != null && ReferenceEquals(flow, _confirmedFlow); }
        internal bool Reuse(object flow, bool entering, Action reply)
        {
            if (!IsConfirmed(flow)) return false;
            if (!entering) reply();
            return true;
        }
        internal static IEnumerator CloseWhenReady(Func<bool> current, Func<bool> close)
        {
            while (current() && !close()) yield return null;
        }
    }
}
