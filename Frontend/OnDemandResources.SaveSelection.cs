using System;
using System.Collections;
using System.Collections.Generic;
using OC2DIYLevel.Extension;
using Team17.Online;
using Team17.Online.Multiplayer.Messaging;
using UnityEngine;

namespace DIYLevelFastInit
{
    internal static partial class OnDemandResources
    {

        private static bool IsSaveEntryActive
        {
            get
            {
                bool remoteClient = ConnectionStatusPublicizer.IsInSession() && !ConnectionStatusPublicizer.IsHost();
                return SaveSelectionState.IsEntryActive(remoteClient, IsTransitioning,
                    remoteClient && ClientLoadGate.HasCurrentTransition);
            }
        }

        internal static IEnumerator FinishSaveSelection(Func<bool> current, string context, Action<SaveSelectionView> capture = null)
        {
            T17FrontendFlow flow = T17FrontendFlow.Instance;
            if (flow == null || !current()) yield break;
            SaveSelectionView view = null;
            bool ready = false;
            try
            {
                GameStateMessage.ClientSavePayload payload = ClientSavePayload.GetValue(flow) as GameStateMessage.ClientSavePayload;
                if (payload != null && payload.DLCID == 15) ClientSavePayload.SetValue(flow, null);
                ClientCountdown.SetValue(flow, false);
                view = HoldSaveSelectionView(flow, null);
                if (capture != null) capture(view);
                flow.HideWaitingForPlayers();
                HashSet<SelectSaveDialog> dialogs = new HashSet<SelectSaveDialog> { _visibleSaveDialog,
                    OnlineSaveDialog.GetValue(flow) as SelectSaveDialog, LocalSaveDialog.GetValue(flow) as SelectSaveDialog };
                foreach (SelectSaveDialog dialog in dialogs)
                {
                    if (!current()) yield break;
                    if (dialog == null || dialog.DLC != 15) continue;
                    yield return SaveSelectionState.CloseWhenReady(current,
                        () => dialog == null || !dialog.gameObject.activeSelf || dialog.CanHide());
                }
                ready = current();
                if (ready) FastInitPlugin.Log.LogInfo("DIY " + context + " save UI retained with input locked during resource preparation");
            }
            finally { if (!ready) EndSaveSelectionView(view, true); }
        }

        internal static SaveSelectionView CaptureSaveSelectionView() { return _saveSelectionView; }

        internal static void EndSaveSelectionView(SaveSelectionView expected, bool restore)
        {
            if (expected == null || !ReferenceEquals(expected, _saveSelectionView)) return;
            if (restore) SaveSelection.Reset();
            try { expected.Release(restore); }
            finally { if (ReferenceEquals(expected, _saveSelectionView)) _saveSelectionView = null; }
        }

        internal static void ResetSaveSelection()
        {
            SaveSelection.Reset();
            EndSaveSelectionView(_saveSelectionView, false);
        }

        private static SaveSelectionView HoldSaveSelectionView(T17FrontendFlow flow, SelectSaveDialog selected)
        {
            if (flow == null) return null;
            SaveSelectionView view = _saveSelectionView;
            if (view == null || view.Released || !ReferenceEquals(view.Flow, flow))
            {
                EndSaveSelectionView(view, false);
                view = new SaveSelectionView(flow);
                _saveSelectionView = view;
            }
            HashSet<SelectSaveDialog> dialogs = new HashSet<SelectSaveDialog> { selected, _visibleSaveDialog,
                OnlineSaveDialog.GetValue(flow) as SelectSaveDialog, LocalSaveDialog.GetValue(flow) as SelectSaveDialog };
            try
            {
                foreach (SelectSaveDialog dialog in dialogs)
                    if (dialog != null && dialog.DLC == 15) view.Hold(dialog);
            }
            catch
            {
                EndSaveSelectionView(view, true);
                throw;
            }
            return view;
        }

        private static SaveSelectionCallback BeginSaveSelectionCallback(SelectSaveDialog dialog)
        {
            T17FrontendFlow flow = T17FrontendFlow.Instance;
            if (flow == null) return null;
            SaveSelection.ObserveRequest(flow, 15);
            return new SaveSelectionCallback(flow, HoldSaveSelectionView(flow, dialog), SaveSelection.Revision);
        }

        private static bool IsCurrentSaveSelectionCallback(SaveSelectionCallback callback)
        {
            return callback != null && ReferenceEquals(callback.Flow, T17FrontendFlow.Instance)
                && callback.Revision == SaveSelection.Revision && SaveSelection.IsDiyRequest(callback.Flow)
                && ReferenceEquals(callback.View, _saveSelectionView)
                && (callback.View == null || !callback.View.Released);
        }

        private static bool SelectionPrefix()
        {
            if (FastInitPlugin.Syncing || IsSaveEntryActive || _catalogBundles != null) return false;
            if (_selectionFrame == Time.frameCount || (_visibleSaveDialog != null && _visibleSaveDialog.gameObject.activeInHierarchy))
            {
                FastInitPlugin.Log.LogInfo("duplicate DIY level selection ignored: save dialog already opening/open");
                return false;
            }
            _selectionFrame = Time.frameCount;
            SaveSelection.Reset();
            return true;
        }

        private static bool SaveDialogShowPrefix(SelectSaveDialog __instance, ref bool __result)
        {
            T17FrontendFlow flow = T17FrontendFlow.Instance;
            GameStateMessage.ClientSavePayload payload = ReferenceEquals(flow, null) ? null
                : ClientSavePayload.GetValue(flow) as GameStateMessage.ClientSavePayload;
            if (!ObserveSaveDialogRequest(flow, payload, __instance.DLC)) return true;
            if (__instance.DLC != 15) return true;
            if (!IsSaveEntryActive && !SaveSelection.IsConfirmed(T17FrontendFlow.Instance) && !__instance.gameObject.activeInHierarchy) return true;
            __result = false;
            FastInitPlugin.Log.LogInfo("duplicate DIY save dialog ignored: already open or entering a level");
            return false;
        }

        private static void SaveDialogShowPostfix(SelectSaveDialog __instance, bool __result)
        {
            if (!__result || __instance.DLC != 15) return;
            _visibleSaveDialog = __instance;
            FastInitPlugin.Log.LogInfo("DIY save dialog opened: id=" + __instance.GetInstanceID() + ", frame=" + Time.frameCount);
        }

        private static bool SaveDialogHidePrefix(SelectSaveDialog __instance, ref bool restoreInvokerState, ref bool __result, out bool __state)
        {
            __state = __instance.DLC == 15 && __instance.gameObject.activeSelf;
            SaveSelectionView view = _saveSelectionView;
            if (view != null && view.Holds(__instance))
            {
                __result = false;
                return false;
            }
            if (__instance.DLC == 15 && IsSaveEntryActive && (view == null || !view.IsRestoring(__instance))) restoreInvokerState = false;
            return true;
        }

        private static void SaveDialogHidePostfix(SelectSaveDialog __instance, bool __state, bool __result)
        {
            if (__state && __result) FastInitPlugin.Log.LogInfo("DIY save dialog closed: id=" + __instance.GetInstanceID()
                + ", entering=" + IsSaveEntryActive + ", frame=" + Time.frameCount);
        }

        private static void ConfirmSaveSelection()
        {
            T17FrontendFlow flow = T17FrontendFlow.Instance;
            if (flow == null) return;
            SaveSelection.Confirm(flow);
            ClientCountdown.SetValue(flow, false);
            FastInitPlugin.Log.LogInfo("DIY save choice confirmed; client save countdown stopped");
        }

        private static bool DontSavePrefix(SelectSaveDialog __instance, out SaveSelectionCallback __state)
        {
            __state = null;
            if (__instance.DLC != 15) return true;
            if (SaveSelection.IsConfirmed(T17FrontendFlow.Instance) || IsSaveEntryActive || SaveReadyCallbacks.Count != 0
                || (_saveSelectionView != null && _saveSelectionView.HasDontSaveScope)) return false;
            __state = BeginSaveSelectionCallback(__instance);
            if (__state != null && __state.View != null) __state.View.BeginDontSave(__instance);
            return true;
        }

        private static void DontSavePostfix(SelectSaveDialog __instance, SaveSelectionCallback __state)
        {
            if (__instance.DLC == 15 && IsCurrentSaveSelectionCallback(__state)) ConfirmSaveSelection();
        }

        private static Exception DontSaveFinalizer(SelectSaveDialog __instance, SaveSelectionCallback __state, Exception __exception)
        {
            if (__state != null && __state.View != null)
            {
                __state.View.EndDontSave(__instance);
                if (__exception != null) EndSaveSelectionView(__state.View, true);
            }
            return __exception;
        }

        private static bool SaveDialogForceClosePrefix(SelectSaveDialog __instance)
        {
            SaveSelectionView view = _saveSelectionView;
            if (view == null || !view.Holds(__instance)) return true;
            if (view.IsDontSaveScope(__instance)) return false;
            ResetSaveSelection();
            return true;
        }

        private static void ServerSaveStatePostfix(Serialisable message)
        {
            GameStateMessage state = message as GameStateMessage;
            if (state == null || state.m_State != GameState.LoadedCampaignMapSave || !HasDiySaveSelection) return;
            List<string> users = new List<string>();
            for (int i = 0; i < ServerUserSystem.m_Users.Count; i++)
            {
                User user = ServerUserSystem.m_Users._items[i];
                users.Add(user.Machine + "/" + user.Engagement + "=" + user.GameState);
            }
            FastInitPlugin.Log.LogInfo("DIY server save acknowledgement received: machine=" + state.m_Machine
                + ", users=" + string.Join(", ", users.ToArray()));
        }

        private static bool ClientSaveDialogPrefix(T17FrontendFlow __instance)
        {
            GameStateMessage.ClientSavePayload payload = ClientSavePayload.GetValue(__instance) as GameStateMessage.ClientSavePayload;
            if (!ObserveClientSaveRequest(__instance, payload)) return true;
            if (SaveSelection.IsConfirmed(__instance))
            {
                ClientSavePayload.SetValue(__instance, null);
                ClientCountdown.SetValue(__instance, false);
                SaveSelection.Reuse(__instance, IsSaveEntryActive, ClientLoadGate.RepeatSaveReady);
                FastInitPlugin.Log.LogInfo("duplicate DIY client save request reused the confirmed choice");
                return false;
            }
            if (!IsSaveEntryActive && (_visibleSaveDialog == null || !_visibleSaveDialog.gameObject.activeInHierarchy)) return true;
            ClientSavePayload.SetValue(__instance, null);
            FastInitPlugin.Log.LogInfo("duplicate DIY client save request ignored: already open or entering a level");
            return false;
        }

        private static bool ClientCountdownPrefix(T17FrontendFlow __instance)
        {
            GameStateMessage.ClientSavePayload payload = ClientSavePayload.GetValue(__instance) as GameStateMessage.ClientSavePayload;
            if (payload != null) ObserveClientSaveRequest(__instance, payload);
            if (!SaveSelection.IsDiyRequest(__instance)) return true;
            if (SaveSelection.IsConfirmed(__instance)) return false;
            return !ClientLoadGate.HasCurrentTransition;
        }

        private static bool ObserveClientSaveRequest(T17FrontendFlow flow, GameStateMessage.ClientSavePayload payload)
        {
            if (payload == null) return false;
            return ObserveSaveRequest(flow, payload.DLCID);
        }

        private static bool ObserveSaveDialogRequest(T17FrontendFlow flow, GameStateMessage.ClientSavePayload payload, int dialogDlc)
        {
            int dlc = payload != null && payload.DLCID != 15 ? payload.DLCID : dialogDlc;
            return ObserveSaveRequest(flow, dlc);
        }

        private static bool ObserveSaveRequest(T17FrontendFlow flow, int dlc)
        {
            SaveSelection.ObserveRequest(flow, dlc);
            if (dlc == 15) return true;
            EndSaveSelectionView(_saveSelectionView, true);
            _visibleSaveDialog = null;
            ClientLoadGate.Cancel("official save selection requested");
            CancelServerPreparation("official save selection requested");
            return false;
        }

        private static bool SaveReadyPrefix(SaveSlotElement __instance, out SaveSelectionCallback __state)
        {
            __state = null;
            if (__instance.DLC != 15) return true;
            if (__instance.SaveGameReady || SaveSelection.IsConfirmed(T17FrontendFlow.Instance) || IsSaveEntryActive
                || (_saveSelectionView != null && _saveSelectionView.HasDontSaveScope)
                || SaveReadyCallbacks.Count != 0 || !SaveReadyCallbacks.Add(__instance))
            {
                FastInitPlugin.Log.LogInfo("duplicate DIY save confirmation ignored");
                return false;
            }
            try { __state = BeginSaveSelectionCallback(null); }
            catch
            {
                SaveReadyCallbacks.Remove(__instance);
                throw;
            }
            if (__state == null) SaveReadyCallbacks.Remove(__instance);
            return true;
        }

        private static Exception SaveReadyFinalizer(SaveSlotElement __instance, SaveSelectionCallback __state, Exception __exception)
        {
            if (__state != null)
            {
                SaveReadyCallbacks.Remove(__instance);
                if (__exception == null && __instance.SaveGameReady && IsCurrentSaveSelectionCallback(__state)) ConfirmSaveSelection();
                else if (__exception != null || !__instance.SaveGameReady) EndSaveSelectionView(__state.View, true);
            }
            return __exception;
        }
    }
}
