using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using BepInEx;
using LevelEditorStub;

namespace DIYLevelFastInit
{

    internal static class LevelCatalogCache
    {
        private static string CachePath(DirectoryInfo dir)
        {
            using (SHA256 hash = SHA256.Create())
            {
                string key = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(dir.FullName.ToLowerInvariant()))).Replace("-", "");
                return Path.Combine(Path.Combine(Paths.CachePath, "DIYLevelFastInit"), key + ".catalog");
            }
        }

        internal static LevelSetInfoSO TryRead(DirectoryInfo dir, FileInfo file)
        {
            try
            {
                string path = CachePath(dir);
                if (!File.Exists(path) || new FileInfo(path).Length > 8 * 1024 * 1024) return null;
                SetMetadata data;
                using (FileStream stream = File.OpenRead(path))
                using (BinaryReader reader = new BinaryReader(stream, Encoding.UTF8)) data = ReadData(reader);
                if (data == null || !data.IsValid(dir, file)) return null;
                LevelSetInfoSO result = data.Create();
                FastInitPlugin.Log.LogInfo("catalog cache hit: " + dir.Name);
                return result;
            }
            catch (Exception e)
            {
                FastInitPlugin.Log.LogWarning("catalog cache ignored [" + dir.Name + "]: " + e.Message);
                return null;
            }
        }

        internal static LevelSetInfoSO Extract(DirectoryInfo dir, FileInfo file, LevelSetInfoSO full)
        {
            try
            {
                SetMetadata data = new SetMetadata
                {
                    schema = 2, directory = dir.FullName, infoFile = file.Name,
                    length = file.Length, modified = file.LastWriteTimeUtc.Ticks,
                    name = full.name, levelSetName = full.levelSetName, levelSetNameZH = full.levelSetNameZH,
                    author = full.author, uid = full.uid, version = full.version,
                    levels = new LevelMetadata[full.levelInfos.Length]
                };
                for (int i = 0; i < data.levels.Length; i++)
                {
                    LevelInfoSO level = full.levelInfos[i];
                    if (level == null) throw new IOException("Null level in catalog");
                    data.levels[i] = new LevelMetadata
                    {
                        name = level.name, levelName = level.levelName, levelNameZH = level.levelNameZH,
                        sceneName = level.sceneName, dependencies = level.dependencies ?? new string[0],
                        players = new[] { PlayerMetadata.From(level.config_1p), PlayerMetadata.From(level.config_2p),
                            PlayerMetadata.From(level.config_3p), PlayerMetadata.From(level.config_4p) }
                    };
                }
                if (!data.IsValid(dir, file)) throw new IOException("Invalid level catalog");
                LevelSetInfoSO result = data.Create();
                Write(dir, data);
                return result;
            }
            catch (Exception e)
            {
                FastInitPlugin.Log.LogError("catalog extraction failed [" + dir.Name + "]: " + e);
                return null;
            }
        }

        private static void Write(DirectoryInfo dir, SetMetadata data)
        {
            string temporary = null;
            try
            {
                string path = CachePath(dir);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                temporary = path + ".tmp";
                using (FileStream stream = File.Create(temporary))
                using (BinaryWriter writer = new BinaryWriter(stream, Encoding.UTF8)) WriteData(writer, data);
                SetMetadata restored;
                using (FileStream stream = File.OpenRead(temporary))
                using (BinaryReader reader = new BinaryReader(stream, Encoding.UTF8)) restored = ReadData(reader);
                if (!restored.IsValid(dir, new FileInfo(Path.Combine(dir.FullName, data.infoFile))))
                    throw new IOException("Catalog binary did not preserve metadata");
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            catch (Exception e)
            {
                FastInitPlugin.Log.LogWarning("catalog cache write skipped: " + e.Message);
            }
            finally
            {
                if (temporary != null && File.Exists(temporary))
                {
                    try { File.Delete(temporary); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }

        private static void WriteText(BinaryWriter writer, string value)
        {
            if (value == null) { writer.Write(-1); return; }
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            if (bytes.Length > 1024 * 1024) throw new IOException("Catalog string too long");
            writer.Write(bytes.Length);
            writer.Write(bytes);
        }

        private static string ReadText(BinaryReader reader)
        {
            int length = reader.ReadInt32();
            if (length == -1) return null;
            if (length < 0 || length > 1024 * 1024 || length > reader.BaseStream.Length - reader.BaseStream.Position)
                throw new IOException("Invalid catalog string length");
            return Encoding.UTF8.GetString(reader.ReadBytes(length));
        }

        private static int ReadCount(BinaryReader reader)
        {
            int count = reader.ReadInt32();
            if (count < 0 || count > 20000) throw new IOException("Invalid catalog array length");
            return count;
        }

        internal static void WriteData(BinaryWriter writer, SetMetadata data)
        {
            writer.Write(0x32434944);
            writer.Write(data.schema);
            WriteText(writer, data.directory);
            WriteText(writer, data.infoFile);
            writer.Write(data.length);
            writer.Write(data.modified);
            WriteText(writer, data.name);
            WriteText(writer, data.levelSetName);
            WriteText(writer, data.levelSetNameZH);
            WriteText(writer, data.author);
            WriteText(writer, data.uid);
            WriteText(writer, data.version);
            writer.Write(data.levels.Length);
            foreach (LevelMetadata level in data.levels)
            {
                WriteText(writer, level.name);
                WriteText(writer, level.levelName);
                WriteText(writer, level.levelNameZH);
                WriteText(writer, level.sceneName);
                writer.Write(level.dependencies.Length);
                foreach (string dependency in level.dependencies) WriteText(writer, dependency);
                foreach (PlayerMetadata player in level.players)
                {
                    writer.Write(player.orderLifeTime);
                    writer.Write(player.timeBetweenOrders);
                    writer.Write(player.plateReturnTime);
                    writer.Write(player.roundTime);
                    writer.Write(player.survivalTimeMultiplier);
                    writer.Write(player.one);
                    writer.Write(player.two);
                    writer.Write(player.three);
                    writer.Write(player.four);
                }
            }
        }

        internal static SetMetadata ReadData(BinaryReader reader)
        {
            if (reader.ReadInt32() != 0x32434944 || reader.ReadInt32() != 2)
                throw new IOException("Unsupported catalog format");
            SetMetadata data = new SetMetadata();
            data.schema = 2;
            data.directory = ReadText(reader);
            data.infoFile = ReadText(reader);
            data.length = reader.ReadInt64();
            data.modified = reader.ReadInt64();
            data.name = ReadText(reader);
            data.levelSetName = ReadText(reader);
            data.levelSetNameZH = ReadText(reader);
            data.author = ReadText(reader);
            data.uid = ReadText(reader);
            data.version = ReadText(reader);
            data.levels = new LevelMetadata[ReadCount(reader)];
            for (int i = 0; i < data.levels.Length; i++)
            {
                LevelMetadata level = new LevelMetadata();
                data.levels[i] = level;
                level.name = ReadText(reader);
                level.levelName = ReadText(reader);
                level.levelNameZH = ReadText(reader);
                level.sceneName = ReadText(reader);
                level.dependencies = new string[ReadCount(reader)];
                for (int j = 0; j < level.dependencies.Length; j++) level.dependencies[j] = ReadText(reader);
                level.players = new PlayerMetadata[4];
                for (int j = 0; j < 4; j++)
                {
                    level.players[j] = new PlayerMetadata
                    {
                        orderLifeTime = reader.ReadInt32(), timeBetweenOrders = reader.ReadInt32(),
                        plateReturnTime = reader.ReadInt32(), roundTime = reader.ReadInt32(),
                        survivalTimeMultiplier = reader.ReadSingle(),
                        one = reader.ReadInt32(), two = reader.ReadInt32(), three = reader.ReadInt32(), four = reader.ReadInt32()
                    };
                }
            }
            if (reader.BaseStream.Position != reader.BaseStream.Length) throw new IOException("Trailing catalog data");
            return data;
        }

        internal static void DestroyLightweight(LevelSetInfoSO set)
        {
            if (set == null) return;
            if (set.levelInfos != null)
                foreach (LevelInfoSO level in set.levelInfos)
                {
                    if (level == null) continue;
                    UnityEngine.Object.DestroyImmediate(level.config_1p);
                    UnityEngine.Object.DestroyImmediate(level.config_2p);
                    UnityEngine.Object.DestroyImmediate(level.config_3p);
                    UnityEngine.Object.DestroyImmediate(level.config_4p);
                    UnityEngine.Object.DestroyImmediate(level);
                }
            UnityEngine.Object.DestroyImmediate(set);
        }
    }
}
