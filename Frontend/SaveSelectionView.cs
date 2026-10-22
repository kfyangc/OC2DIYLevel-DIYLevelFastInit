using System.Collections.Generic;
using UnityEngine;

namespace DIYLevelFastInit
{
    internal static partial class OnDemandResources
    {
        /// <summary>Retains a visible save dialog and owns input suppression until entry ends.</summary>
        internal sealed class SaveSelectionView
        {
            internal readonly T17FrontendFlow Flow;
            private readonly HashSet<SelectSaveDialog> _dialogs = new HashSet<SelectSaveDialog>();
            private readonly HashSet<SelectSaveDialog> _dontSaveScopes = new HashSet<SelectSaveDialog>();
            private readonly Dictionary<T17EventSystem, Suppressor> _suppressors = new Dictionary<T17EventSystem, Suppressor>();
            private bool _released, _restoring;

            internal SaveSelectionView(T17FrontendFlow flow) { Flow = flow; }
            internal bool Released { get { return _released; } }
            internal bool HasDontSaveScope { get { return _dontSaveScopes.Count != 0; } }
            internal bool Holds(SelectSaveDialog dialog) { return !_released && _dialogs.Contains(dialog); }
            internal bool IsRestoring(SelectSaveDialog dialog) { return _restoring && _dialogs.Contains(dialog); }
            internal bool IsDontSaveScope(SelectSaveDialog dialog) { return _dontSaveScopes.Contains(dialog); }
            internal void BeginDontSave(SelectSaveDialog dialog) { _dontSaveScopes.Add(dialog); }
            internal void EndDontSave(SelectSaveDialog dialog) { _dontSaveScopes.Remove(dialog); }

            internal void Hold(SelectSaveDialog dialog)
            {
                if (_released || dialog == null || !dialog.gameObject.activeInHierarchy) return;
                _dialogs.Add(dialog);
                foreach (T17EventSystem system in Object.FindObjectsOfType<T17EventSystem>())
                    if (system != null && !_suppressors.ContainsKey(system))
                        _suppressors.Add(system, system.Disable(dialog));
                T17EventSystem cached = dialog.CachedEventSystem;
                if (cached != null && !_suppressors.ContainsKey(cached))
                    _suppressors.Add(cached, cached.Disable(dialog));
            }

            internal void Release(bool restore)
            {
                if (_released) return;
                _released = true;
                _restoring = restore;
                try
                {
                    foreach (KeyValuePair<T17EventSystem, Suppressor> item in _suppressors)
                        if (item.Value != null) item.Value.Release();
                    foreach (KeyValuePair<T17EventSystem, Suppressor> item in _suppressors)
                        if (item.Key != null && item.Value != null) item.Key.ReleaseSuppressor(item.Value);
                    if (restore)
                        foreach (SelectSaveDialog dialog in _dialogs)
                            if (dialog != null && dialog.gameObject.activeSelf && dialog.CanHide()) dialog.Hide(true);
                }
                finally
                {
                    _restoring = false;
                    _suppressors.Clear();
                    _dialogs.Clear();
                    _dontSaveScopes.Clear();
                }
            }
        }

        internal sealed class SaveSelectionCallback
        {
            internal readonly T17FrontendFlow Flow;
            internal readonly SaveSelectionView View;
            internal readonly long Revision;

            internal SaveSelectionCallback(T17FrontendFlow flow, SaveSelectionView view, long revision)
            {
                Flow = flow;
                View = view;
                Revision = revision;
            }
        }
    }
}
