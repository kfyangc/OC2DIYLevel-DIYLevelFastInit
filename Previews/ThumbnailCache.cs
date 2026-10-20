using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using BepInEx;
using UnityEngine;
using UnityEngine.UI;

namespace DIYLevelFastInit
{

    internal static class ThumbnailCache
    {
        internal sealed class Entry
        {
            internal string Key, File;
            internal Texture2D Texture;
            internal Sprite Sprite;
            internal long Used;
        }

        private sealed class PendingWrite
        {
            internal string File;
            internal byte[] Bytes;
        }

        private const int Magic = 0x314D4854, Version = 1;
        private const int MaxWidth = 640, MaxHeight = 360, MaxBytes = 2 * 1024 * 1024;
        private const long MaxDiskBytes = 128L * 1024 * 1024;
        private static readonly byte[] PngSignature = { 137, 80, 78, 71, 13, 10, 26, 10 };
        private static readonly Dictionary<string, Entry> Memory = new Dictionary<string, Entry>();
        private static readonly Queue<PendingWrite> Writes = new Queue<PendingWrite>();
        private static readonly object WriteLock = new object();
        private static byte[] _stamp;
        private static long _use;
        private static bool _writerRunning;
        private static Image _image;
        private static string _boundKey;

        internal static void Invalidate() { _stamp = null; }

        internal static bool HasWriteCapacity { get { lock (WriteLock) return Writes.Count < 4; } }
        internal static bool WritesBusy { get { lock (WriteLock) return _writerRunning || Writes.Count != 0; } }

        private static string Root()
        {
            return Path.Combine(Path.Combine(Paths.CachePath, "DIYLevelFastInit"), "thumbnails");
        }

        private static string Key(string path, string scene)
        {
            using (SHA256 hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(
                    Path.GetFullPath(path).ToLowerInvariant() + "\n" + scene.ToLowerInvariant()))).Replace("-", "");
        }

        private static byte[] Stamp()
        {
            if (_stamp != null) return _stamp;
            string plugin = FastInitPlugin.GetDiyPluginDir();
            List<string> paths = new List<string>();
            foreach (FileInfo file in new DirectoryInfo(plugin).GetFiles("common*")) paths.Add(file.FullName);
            foreach (DirectoryInfo dir in new DirectoryInfo(Path.Combine(plugin, "levels")).GetDirectories())
                foreach (FileInfo file in dir.GetFiles("info*")) paths.Add(file.FullName);
            paths.Sort(StringComparer.OrdinalIgnoreCase);
            using (MemoryStream stream = new MemoryStream())
            using (BinaryWriter writer = new BinaryWriter(stream, Encoding.UTF8))
            using (SHA256 hash = SHA256.Create())
            {
                foreach (string path in paths)
                {
                    FileInfo file = new FileInfo(path);
                    writer.Write(path.ToLowerInvariant());
                    writer.Write(file.Length);
                    writer.Write(file.LastWriteTimeUtc.Ticks);
                }
                writer.Flush();
                _stamp = hash.ComputeHash(stream.ToArray());
            }
            return _stamp;
        }

        internal static Entry TryGet(string path, string scene, out bool memoryHit)
        {
            memoryHit = false;
            try
            {
                string key = Key(path, scene), memoryKey = key + BitConverter.ToString(Stamp());
                Entry entry;
                if (Memory.TryGetValue(memoryKey, out entry))
                {
                    entry.Used = ++_use;
                    memoryHit = true;
                    Enqueue(entry.File, null);
                    return entry;
                }
                string file = Path.Combine(Root(), key + ".thumb");
                if (!File.Exists(file)) return null;
                if (new FileInfo(file).Length > MaxBytes + 84) throw new IOException("Thumbnail cache exceeds size limit");
                byte[] png;
                using (FileStream stream = File.OpenRead(file))
                using (BinaryReader reader = new BinaryReader(stream)) png = ReadData(reader, Stamp());
                Texture2D texture = null;
                try
                {
                    if (png.Length != 0)
                    {
                        texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                        if (!ImageConversion.LoadImage(texture, png, true)) throw new IOException("Cannot decode cached thumbnail");
                    }
                    entry = Store(memoryKey, file, texture);
                    texture = null;
                }
                finally { if (texture != null) UnityEngine.Object.DestroyImmediate(texture); }
                Enqueue(file, null);
                return entry;
            }
            catch (Exception e)
            {
                FastInitPlugin.Log.LogDebug("thumbnail cache miss [" + scene + "]: " + e.Message);
                return null;
            }
        }

        internal static bool IsReady(string path, string scene)
        {
            try
            {
                string file = Path.Combine(Root(), Key(path, scene) + ".thumb");
                if (!File.Exists(file)) return false;
                using (FileStream stream = File.OpenRead(file))
                using (BinaryReader reader = new BinaryReader(stream))
                {
                    if (reader.ReadInt32() != Magic || reader.ReadInt32() != Version
                        || !Equal(reader.ReadBytes(32), Stamp())) return false;
                    int width = reader.ReadInt32(), height = reader.ReadInt32(), length = reader.ReadInt32();
                    return length >= 0 && length <= MaxBytes && stream.Length == 84L + length
                        && (length == 0 ? width == 0 && height == 0
                            : width > 0 && width <= MaxWidth && height > 0 && height <= MaxHeight);
                }
            }
            catch (Exception) { return false; }
        }

        internal static Entry Capture(string path, string scene, Sprite source, bool retain = true)
        {
            byte[] stamp = Stamp();
            string key = Key(path, scene), file = Path.Combine(Root(), key + ".thumb");
            Texture2D texture = null;
            try
            {
                byte[] png = new byte[0];
                if (source != null)
                {
                    if (source.packed && source.packingRotation != SpritePackingRotation.None)
                        throw new IOException("Rotated packed screenshot cannot be cached");
                    Rect rect = source.textureRect;
                    if (rect.width <= 0 || rect.height <= 0) throw new IOException("Empty screenshot rectangle");
                    float scale = Mathf.Min(1f, Mathf.Min(MaxWidth / rect.width, MaxHeight / rect.height));
                    int width = Math.Max(1, Mathf.RoundToInt(rect.width * scale));
                    int height = Math.Max(1, Mathf.RoundToInt(rect.height * scale));
                    RenderTexture previous = RenderTexture.active;
                    RenderTexture target = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32);
                    try
                    {
                        Texture2D original = source.texture;
                        Graphics.Blit(original, target, new Vector2(rect.width / original.width, rect.height / original.height),
                            new Vector2(rect.x / original.width, rect.y / original.height));
                        RenderTexture.active = target;
                        texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
                        texture.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
                        texture.Apply(false, false);
                        png = ImageConversion.EncodeToPNG(texture);
                        texture.Apply(false, true);
                    }
                    finally
                    {
                        RenderTexture.active = previous;
                        RenderTexture.ReleaseTemporary(target);
                    }
                }
                byte[] bytes;
                using (MemoryStream stream = new MemoryStream())
                using (BinaryWriter writer = new BinaryWriter(stream))
                {
                    WriteData(writer, stamp, png);
                    writer.Flush();
                    bytes = stream.ToArray();
                }
                Entry entry = null;
                if (retain)
                {
                    entry = Store(key + BitConverter.ToString(stamp), file, texture);
                    texture = null;
                }
                Enqueue(file, bytes);
                return entry;
            }
            finally { if (texture != null) UnityEngine.Object.DestroyImmediate(texture); }
        }

        private static Entry Store(string key, string file, Texture2D texture)
        {
            Sprite sprite = null;
            if (texture != null)
            {
                texture.hideFlags = HideFlags.DontUnloadUnusedAsset;
                sprite = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(0.5f, 0.5f));
                sprite.hideFlags = HideFlags.DontUnloadUnusedAsset;
            }
            Entry entry = new Entry { Key = key, File = file, Texture = texture, Sprite = sprite, Used = ++_use };
            Entry replaced;
            if (Memory.TryGetValue(key, out replaced)) Destroy(replaced);
            Memory[key] = entry;
            TrimMemory();
            return entry;
        }

        internal static void Show(Entry entry, Image image)
        {
            if (_image != null && _image != image) Hide(_image);
            _image = image;
            _boundKey = entry.Key;
            image.sprite = entry.Sprite;
            image.gameObject.SetActive(entry.Sprite != null);
            entry.Used = ++_use;
            TrimMemory();
        }

        internal static void Hide(Image image)
        {
            if (image == null) image = _image;
            if (image != null)
            {
                image.sprite = null;
                image.gameObject.SetActive(false);
            }
            if (image == _image) { _image = null; _boundKey = null; }
        }

        private static void TrimMemory()
        {
            while (Memory.Count > 8)
            {
                Entry oldest = null;
                foreach (Entry entry in Memory.Values)
                    if (entry.Key != _boundKey && (oldest == null || entry.Used < oldest.Used)) oldest = entry;
                if (oldest == null) break;
                Memory.Remove(oldest.Key);
                Destroy(oldest);
            }
        }

        internal static void ClearMemory()
        {
            Hide(null);
            foreach (Entry entry in Memory.Values) Destroy(entry);
            Memory.Clear();
        }

        private static void Destroy(Entry entry)
        {
            if (entry.Sprite != null) UnityEngine.Object.DestroyImmediate(entry.Sprite);
            if (entry.Texture != null) UnityEngine.Object.DestroyImmediate(entry.Texture);
        }

        internal static void WriteData(BinaryWriter writer, byte[] stamp, byte[] png)
        {
            int width, height;
            Dimensions(png, out width, out height);
            if (stamp.Length != 32) throw new IOException("Invalid thumbnail source stamp");
            writer.Write(Magic);
            writer.Write(Version);
            writer.Write(stamp);
            writer.Write(width);
            writer.Write(height);
            writer.Write(png.Length);
            using (SHA256 hash = SHA256.Create()) writer.Write(hash.ComputeHash(png));
            writer.Write(png);
        }

        internal static byte[] ReadData(BinaryReader reader, byte[] stamp)
        {
            if (reader.ReadInt32() != Magic || reader.ReadInt32() != Version) throw new IOException("Invalid thumbnail cache version");
            if (!Equal(reader.ReadBytes(32), stamp)) throw new IOException("Thumbnail source fingerprint changed");
            int width = reader.ReadInt32(), height = reader.ReadInt32(), length = reader.ReadInt32();
            if (length < 0 || length > MaxBytes) throw new IOException("Invalid thumbnail payload size");
            byte[] digest = reader.ReadBytes(32), png = reader.ReadBytes(length);
            if (png.Length != length || reader.BaseStream.Position != reader.BaseStream.Length) throw new IOException("Truncated thumbnail or trailing data");
            using (SHA256 hash = SHA256.Create())
                if (!Equal(hash.ComputeHash(png), digest)) throw new IOException("Thumbnail checksum mismatch");
            int actualWidth, actualHeight;
            Dimensions(png, out actualWidth, out actualHeight);
            if (width != actualWidth || height != actualHeight) throw new IOException("Thumbnail dimensions do not match PNG");
            return png;
        }

        private static void Dimensions(byte[] png, out int width, out int height)
        {
            width = height = 0;
            if (png.Length == 0) return;
            if (png.Length < 33 || png.Length > MaxBytes) throw new IOException("Invalid thumbnail PNG size");
            for (int i = 0; i < 8; i++) if (png[i] != PngSignature[i]) throw new IOException("Invalid thumbnail PNG signature");
            if (png[8] != 0 || png[9] != 0 || png[10] != 0 || png[11] != 13
                || png[12] != 73 || png[13] != 72 || png[14] != 68 || png[15] != 82) throw new IOException("Invalid PNG header");
            width = png[16] << 24 | png[17] << 16 | png[18] << 8 | png[19];
            height = png[20] << 24 | png[21] << 16 | png[22] << 8 | png[23];
            if (width < 1 || width > MaxWidth || height < 1 || height > MaxHeight) throw new IOException("Thumbnail dimensions exceed limit");
        }

        private static bool Equal(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        private static void Enqueue(string file, byte[] bytes)
        {
            lock (WriteLock)
            {
                if (Writes.Count == 4)
                {
                    if (bytes == null) return;
                    Writes.Dequeue();
                }
                Writes.Enqueue(new PendingWrite { File = file, Bytes = bytes });
                if (_writerRunning) return;
                _writerRunning = true;
                System.Threading.ThreadPool.QueueUserWorkItem(WriteLoop);
            }
        }

        private static void WriteLoop(object unused)
        {
            while (true)
            {
                PendingWrite write;
                lock (WriteLock)
                {
                    if (Writes.Count == 0) { _writerRunning = false; return; }
                    write = Writes.Dequeue();
                }
                string temporary = null;
                try
                {
                    if (write.Bytes == null)
                    {
                        if (File.Exists(write.File)) File.SetLastWriteTimeUtc(write.File, DateTime.UtcNow);
                        continue;
                    }
                    string root = Path.GetDirectoryName(write.File);
                    Directory.CreateDirectory(root);
                    temporary = write.File + ".tmp-" + Guid.NewGuid().ToString("N");
                    File.WriteAllBytes(temporary, write.Bytes);
                    if (File.Exists(write.File)) File.Replace(temporary, write.File, null);
                    else File.Move(temporary, write.File);
                    temporary = null;
                    TrimDisk(root);
                    FastInitPlugin.Log.LogDebug("thumbnail cache stored: " + Path.GetFileName(write.File));
                }
                catch (Exception e) { FastInitPlugin.Log.LogDebug("thumbnail cache write skipped: " + e.Message); }
                finally
                {
                    if (temporary != null)
                        try { File.Delete(temporary); } catch (Exception) { }
                }
            }
        }

        private static void TrimDisk(string root)
        {
            List<FileInfo> files = new List<FileInfo>(new DirectoryInfo(root).GetFiles("*.thumb"));
            long size = 0;
            foreach (FileInfo file in files) size += file.Length;
            files.Sort((a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));
            foreach (FileInfo file in files)
            {
                if (size <= MaxDiskBytes) break;
                long length = file.Length;
                file.Delete();
                size -= length;
            }
        }
    }
}
