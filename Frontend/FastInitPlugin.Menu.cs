using System;
using System.Collections.Generic;
using LevelEditorStub;
using OC2DIYLevel;
using UnityEngine;
using UnityEngine.UI;

namespace DIYLevelFastInit
{
    public partial class FastInitPlugin
    {

        internal static void HealLevelSetButtons()
        {
            try
            {
                List<KeyValuePair<string, LevelSetInfoSO>> infos = DIYLevelAssetBundleManager.levelSetInfos;
                FrontendOptionsMenu menu = TryGetMenu();
                if (infos == null || menu == null || MiAddSetButton == null)
                {
                    return; // menu not created yet; AddUI itself will build it from the current list
                }
                // The frontend rebuilds its menus on every level exit, so a freshly recreated
                // menu can match the cached count while missing the reload button; re-assert
                // it on every heal before the count check can short-circuit.
                EnsureReloadButton(menu);
                if (_builtCount == infos.Count)
                {
                    return;
                }
                OC2DIYLevel.UIUtils.ClearAllMenuContent(menu);
                for (int i = 0; i < infos.Count; i++)
                {
                    MiAddSetButton.Invoke(null, new object[] { infos[i].Value });
                }
                _builtCount = infos.Count;
                Log.LogInfo("level set menu rebuilt: " + infos.Count + " entries");
                UpdateMenuHeaderProgress(menu);
                EnsureReloadButton(menu);
            }
            catch (Exception e)
            {
                Log.LogWarning("HealLevelSetButtons failed: " + e.Message);
            }
        }

        // Incremental counterpart of HealLevelSetButtons: appends only the buttons past
        // the cached count, so the menu fills in live during the initial async load.
        internal static void NotifySetAdded()
        {
            try
            {
                List<KeyValuePair<string, LevelSetInfoSO>> infos = DIYLevelAssetBundleManager.levelSetInfos;
                FrontendOptionsMenu menu = TryGetMenu();
                if (infos == null || menu == null || MiAddSetButton == null)
                {
                    return;
                }
                if (_builtCount < infos.Count)
                {
                    for (int i = _builtCount; i < infos.Count; i++)
                    {
                        MiAddSetButton.Invoke(null, new object[] { infos[i].Value });
                    }
                }
                _builtCount = infos.Count;
                UpdateMenuHeaderProgress(menu);
                EnsureReloadButton(menu);
            }
            catch (Exception e)
            {
                Log.LogDebug("NotifySetAdded: " + e.Message);
            }
        }

        private static FrontendOptionsMenu TryGetMenu()
        {
            return FiSetMenu != null ? FiSetMenu.GetValue(null) as FrontendOptionsMenu : null;
        }

        private static void UpdateMenuHeaderProgress(FrontendOptionsMenu menu)
        {
            try
            {
                Transform header = menu.transform.Find("SettingsBody/HeaderBacker/Header");
                if (header == null)
                {
                    return;
                }
                T17Text text = header.GetComponent<T17Text>();
                if (text == null)
                {
                    return;
                }
                List<KeyValuePair<string, LevelSetInfoSO>> infos = DIYLevelAssetBundleManager.levelSetInfos;
                int done = (infos != null) ? infos.Count : 0;
                if (Loading && TotalSets > 0)
                {
                    OC2DIYLevel.UIUtils.SetText(text, "More Levels (loading " + done + "/" + TotalSets + ")", "更多关卡（加载中 " + done + "/" + TotalSets + "）");
                }
                else
                {
                    OC2DIYLevel.UIUtils.SetText(text, "More Levels", "更多关卡");
                }
            }
            catch (Exception e)
            {
                Log.LogDebug("UpdateMenuHeaderProgress: " + e.Message);
            }
        }

        // The reload button sits FIRST in the level set menu; find-by-name keeps it
        // from ever duplicating across incremental appends and full rebuilds.
        internal static void EnsureReloadButton(FrontendOptionsMenu menu)
        {
            try
            {
                if (menu == null)
                {
                    return;
                }
                Transform content = menu.transform.Find("SettingsBody/ContentPC/Viewport/Content");
                if (content == null)
                {
                    return;
                }
                T17Button btn;
                Transform existing = content.Find("FastInit_Reload");
                if (existing != null)
                {
                    btn = existing.GetComponent<T17Button>();
                }
                else
                {
                    btn = OC2DIYLevel.UIUtils.AddButton(menu, "FastInit_Reload", "Reload level sets", "刷新关卡列表", TrySyncReload);
                }
                if (btn == null)
                {
                    return;
                }
                btn.transform.SetAsFirstSibling();
                ((Button)btn).interactable = !Loading;
            }
            catch (Exception e)
            {
                Log.LogDebug("EnsureReloadButton: " + e.Message);
            }
        }
    }
}
