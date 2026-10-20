using System;
using System.Collections;

namespace DIYLevelFastInit
{

    /// <summary>Retains a confirmed choice across duplicate frontend requests.</summary>
    internal sealed class SaveSelectionState
    {
        private object _confirmedFlow;
        internal void Confirm(object flow) { _confirmedFlow = flow; }
        internal void Reset() { _confirmedFlow = null; }
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
