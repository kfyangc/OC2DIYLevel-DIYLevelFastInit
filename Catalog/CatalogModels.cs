using System;
using System.IO;
using LevelEditorStub;
using UnityEngine;

namespace DIYLevelFastInit
{

    // Result slots filled by the shared info-set loader.
    internal sealed class InfoLoad
    {
        public LevelSetInfoSO So;
        public FileInfo InfoFile;
    }

    // Per-set disk fingerprint taken at load time; sync diffs against it.
    internal sealed class SetSnapshot
    {
        public string InfoFileName;
        public DateTime InfoMtimeUtc;
        public long InfoLength;
        public DateTime DirMaxMtimeUtc;
    }

    internal sealed class PlayerMetadata
    {
        public int orderLifeTime, timeBetweenOrders, plateReturnTime, roundTime;
        public float survivalTimeMultiplier;
        public int one, two, three, four;

        internal static PlayerMetadata From(LevelConfigSetupPerPlayerCountSO value)
        {
            if (value == null) throw new IOException("Missing player-count settings");
            return new PlayerMetadata
            {
                orderLifeTime = value.orderLifeTime, timeBetweenOrders = value.timeBetweenOrders,
                plateReturnTime = value.plateReturnTime, roundTime = value.roundTime,
                survivalTimeMultiplier = value.survivalTimeMultiplier,
                one = value.m_OneStarScore, two = value.m_TwoStarScore,
                three = value.m_ThreeStarScore, four = value.m_FourStarScore
            };
        }

        internal LevelConfigSetupPerPlayerCountSO Create()
        {
            LevelConfigSetupPerPlayerCountSO value = ScriptableObject.CreateInstance<LevelConfigSetupPerPlayerCountSO>();
            value.hideFlags = HideFlags.DontUnloadUnusedAsset;
            value.orderLifeTime = orderLifeTime;
            value.timeBetweenOrders = timeBetweenOrders;
            value.plateReturnTime = plateReturnTime;
            value.roundTime = roundTime;
            value.survivalTimeMultiplier = survivalTimeMultiplier;
            value.m_OneStarScore = one;
            value.m_TwoStarScore = two;
            value.m_ThreeStarScore = three;
            value.m_FourStarScore = four;
            return value;
        }
    }

    internal sealed class LevelMetadata
    {
        public string name, levelName, levelNameZH, sceneName;
        public string[] dependencies;
        public PlayerMetadata[] players;
    }

    internal sealed class SetMetadata
    {
        public int schema;
        public string directory, infoFile, name, levelSetName, levelSetNameZH, author, uid, version;
        public long length, modified;
        public LevelMetadata[] levels;

        internal bool IsValid(DirectoryInfo dir, FileInfo file)
        {
            if (schema != 2 || !string.Equals(directory, dir.FullName, StringComparison.OrdinalIgnoreCase)
                || infoFile != file.Name || length != file.Length || modified != file.LastWriteTimeUtc.Ticks
                || levels == null || levels.Length > 20000) return false;
            foreach (LevelMetadata level in levels)
            {
                if (level == null || string.IsNullOrEmpty(level.sceneName) || level.dependencies == null
                    || level.players == null || level.players.Length != 4) return false;
                foreach (PlayerMetadata player in level.players) if (player == null) return false;
            }
            return true;
        }

        internal LevelSetInfoSO Create()
        {
            LevelSetInfoSO set = ScriptableObject.CreateInstance<LevelSetInfoSO>();
            try
            {
                set.hideFlags = HideFlags.DontUnloadUnusedAsset;
                set.name = name;
                set.levelSetName = levelSetName;
                set.levelSetNameZH = levelSetNameZH;
                set.author = author;
                set.uid = uid;
                set.version = version;
                set.levelInfos = new LevelInfoSO[levels.Length];
                for (int i = 0; i < levels.Length; i++)
                {
                    LevelMetadata data = levels[i];
                    LevelInfoSO level = ScriptableObject.CreateInstance<LevelInfoSO>();
                    set.levelInfos[i] = level;
                    level.hideFlags = HideFlags.DontUnloadUnusedAsset;
                    level.name = data.name;
                    level.levelName = data.levelName;
                    level.levelNameZH = data.levelNameZH;
                    level.sceneName = data.sceneName;
                    level.dependencies = data.dependencies;
                    level.config_1p = data.players[0].Create();
                    level.config_2p = data.players[1].Create();
                    level.config_3p = data.players[2].Create();
                    level.config_4p = data.players[3].Create();
                }
                return set;
            }
            catch
            {
                LevelCatalogCache.DestroyLightweight(set);
                throw;
            }
        }
    }
}
