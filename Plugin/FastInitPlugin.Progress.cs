using System;
using System.Collections.Generic;
using LevelEditorStub;
using OC2DIYLevel;
using UnityEngine;

namespace DIYLevelFastInit
{
    public partial class FastInitPlugin
    {

        private void OnGUI()
        {
            bool catalogLoading = Loading && TotalSets > 0;
            string infoPreparing = InfoOptimizationProgress;
            string levelLoading = OnDemandResources.LevelLoadProgress;
            bool thumbnails = OnDemandResources.WarmingThumbnails && !OnDemandResources.IsTransitioning && !PseudoPrefabManager.isInCustomLevel;
            if (!catalogLoading && !thumbnails && levelLoading == null && infoPreparing == null)
            {
                return;
            }
            try
            {
                if (_progressStyle == null)
                {
                    _progressStyle = new GUIStyle(GUI.skin.label);
                    _progressStyle.fontSize = 18;
                    _progressStyle.fontStyle = FontStyle.Bold;
                    _progressStyle.alignment = TextAnchor.UpperRight;
                }
                List<KeyValuePair<string, LevelSetInfoSO>> infos = DIYLevelAssetBundleManager.levelSetInfos;
                int done = (infos != null) ? infos.Count : 0;
                string text = infoPreparing != null ? "DIY info " + infoPreparing
                    : levelLoading != null ? "DIY level preparing " + levelLoading
                    : catalogLoading ? "DIY levels loading " + done + "/" + TotalSets
                    : "DIY previews caching " + OnDemandResources.ThumbnailDone + "/" + OnDemandResources.ThumbnailTotal;
                Rect rect = new Rect(Screen.width - 336, 6, 330, 26);
                _progressStyle.normal.textColor = Color.black;
                GUI.Label(new Rect(rect.x + 2f, rect.y + 2f, rect.width, rect.height), text, _progressStyle);
                _progressStyle.normal.textColor = Color.white;
                GUI.Label(rect, text, _progressStyle);
            }
            catch (Exception)
            {
            }
        }
    }
}
