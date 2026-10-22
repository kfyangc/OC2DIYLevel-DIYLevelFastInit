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

        internal static IEnumerator FinishSaveSelection(Func<bool> current, string context)
        {
            T17FrontendFlow flow = T17FrontendFlow.Instance;
            if (flow == null || !current()) yield break;
            GameStateMessage.ClientSavePayload payload = ClientSavePayload.GetValue(flow) as GameStateMessage.ClientSavePayload;
            if (payload != null && payload.DLCID == 15) ClientSavePayload.SetValue(flow, null);
            ClientCountdown.SetValue(flow, false);
            flow.HideWaitingForPlayers();
            HashSet<SelectSaveDialog> dialogs = new HashSet<SelectSaveDialog> { _visibleSaveDialog,
                OnlineSaveDialog.GetValue(flow) as SelectSaveDialog, LocalSaveDialog.GetValue(flow) as SelectSaveDialog };
            foreach (SelectSaveDialog dialog in dialogs)
            {
                if (!current()) yield break;
                if (dialog == null || dialog.DLC != 15) continue;
                yield return SaveSelectionState.CloseWhenReady(current,
                    () => dialog == null || !dialog.gameObject.activeSelf || dialog.Hide(false));
            }
            if (current()) FastInitPlugin.Log.LogInfo("DIY " + context + " save UI closed before resource preparation");
        }

        internal static void ResetSaveSelection() { SaveSelection.Reset(); }

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

        private static void SaveDialogHidePrefix(SelectSaveDialog __instance, ref bool restoreInvokerState, out bool __state)
        {
            __state = __instance.DLC == 15 && __instance.gameObject.activeSelf;
            if (__instance.DLC == 15 && IsSaveEntryActive) restoreInvokerState = false;
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

        private static void DontSavePostfix(SelectSaveDialog __instance)
        {
            if (__instance.DLC == 15) ConfirmSaveSelection();
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
            _visibleSaveDialog = null;
            ClientLoadGate.Cancel("official save selection requested");
            CancelServerPreparation("official save selection requested");
            return false;
        }

        private static bool SaveReadyPrefix(SaveSlotElement __instance, out bool __state)
        {
            __state = false;
            if (__instance.DLC != 15) return true;
            if (__instance.SaveGameReady || SaveSelection.IsConfirmed(T17FrontendFlow.Instance) || IsSaveEntryActive
                || SaveReadyCallbacks.Count != 0 || !SaveReadyCallbacks.Add(__instance))
            {
                FastInitPlugin.Log.LogInfo("duplicate DIY save confirmation ignored");
                return false;
            }
            __state = true;
            return true;
        }

        private static Exception SaveReadyFinalizer(SaveSlotElement __instance, bool __state, Exception __exception)
        {
            if (__state)
            {
                SaveReadyCallbacks.Remove(__instance);
                if (__exception == null && __instance.SaveGameReady) ConfirmSaveSelection();
            }
            return __exception;
        }
    }
}
