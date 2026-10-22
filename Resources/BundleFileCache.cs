using System;
using System.Collections.Generic;
using System.IO;
using System.IO.IsolatedStorage;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using DIYLevelFastInit.BundleCodecs;

namespace DIYLevelFastInit
{
    /// <summary>Builds disposable bundle-file copies on one managed worker without delaying native loads.</summary>
    internal static class BundleFileCache
    {
        internal delegate UnityFsRepacker.ConversionResult Converter(string source, string destination, Func<bool> cancelled, Action checkpoint);

        internal sealed class Lease : IDisposable
        {
            internal readonly string Path;
            internal readonly bool IsCached;
            private State _owner;
            private Entry _entry;
            private FileStream _pin;

            internal Lease(string path) { Path = path; }
            internal Lease(State owner, Entry entry, FileStream pin)
            {
                Path = entry.Bundle;
                IsCached = true;
                _owner = owner;
                _entry = entry;
                _pin = pin;
            }

            public void Dispose()
            {
                FileStream pin = Interlocked.Exchange(ref _pin, null);
                if (pin == null) return;
                pin.Dispose();
                lock (_owner.Sync)
                {
                    _entry.Pins--;
                    _owner.Maintenance = true;
                    Monitor.PulseAll(_owner.Sync);
                }
                _owner = null;
                _entry = null;
            }
        }

        internal sealed class Entry
        {
            internal string Source, Key, Bundle, Metadata, LockFile, BadFile;
            internal long SourceLength, SourceTicks, OutputLength, OutputTicks, LastUse;
            internal bool Queued, Ready, Rejected, WriteBad, Touch;
            internal int Pins;
        }

        internal sealed class State
        {
            internal readonly object Sync = new object();
            internal readonly Dictionary<string, Entry> Entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
            internal readonly Queue<Entry> Jobs = new Queue<Entry>();
            internal readonly Queue<string> Logs = new Queue<string>();
            internal string Root;
            internal Action<string> Log;
            internal Converter Convert;
            internal long Budget;
            internal volatile bool Cancelled;
            internal bool Idle, Maintenance, RejectionsPending;
            internal Thread Worker;
        }

        private sealed class Metadata
        {
            internal byte[] SourceHash, DataHash, OutputHash;
            internal long OutputLength, DataLength;
            internal int BlockCount;
        }

        private sealed class DiskEntry
        {
            internal string Key, Bundle, Metadata;
            internal long Bytes, LastUse;
        }

        private const int Magic = 0x31434642;
        private const int Schema = 1;
        internal const int FormatVersion = 2;
        private const int BadMagic = 0x31424342;
        private const long MaxBytes = 512L * 1024 * 1024;
        private static readonly object Configuration = new object();
        private static State _state;

        internal static void Configure(string root, Action<string> log)
        {
            Configure(root, log, UnityFsRepacker.Convert);
        }

        internal static void Configure(string root, Action<string> log, Converter convert)
        {
            Configure(root, log, convert, MaxBytes);
        }

        internal static void Configure(string root, Action<string> log, Converter convert, long budget)
        {
            if (root == null || convert == null) throw new ArgumentNullException();
            State next = new State { Root = System.IO.Path.GetFullPath(root), Log = log, Convert = convert,
                Budget = Math.Min(MaxBytes, Math.Max(1, budget)), Maintenance = true };
            next.Worker = new Thread(() => Work(next)) { IsBackground = true, Name = "DIY bundle file cache", Priority = ThreadPriority.BelowNormal };
            lock (Configuration)
            {
                Stop(_state);
                _state = next;
                next.Worker.Start();
            }
        }

        internal static Lease Acquire(string source)
        {
            State state;
            lock (Configuration) state = _state;
            if (state == null || state.Cancelled || !Allowed(source)) return new Lease(source);
            Entry stamp;
            try { stamp = Describe(state, source); }
            catch (Exception error)
            {
                Log(state, "bundle file cache source unavailable: " + error.Message);
                return new Lease(source);
            }
            lock (state.Sync)
            {
                Entry entry;
                if (!state.Entries.TryGetValue(stamp.Key, out entry))
                {
                    entry = stamp;
                    state.Entries.Add(entry.Key, entry);
                }
                if (entry.Ready && !entry.Rejected)
                {
                    try
                    {
                        FileInfo file = new FileInfo(entry.Bundle);
                        if (file.Exists && file.Length == entry.OutputLength && file.LastWriteTimeUtc.Ticks == entry.OutputTicks)
                        {
                            FileStream pin = new FileStream(entry.Bundle, FileMode.Open, FileAccess.Read, FileShare.Read);
                            entry.Pins++;
                            entry.LastUse = DateTime.UtcNow.Ticks;
                            entry.Touch = state.Maintenance = true;
                            Monitor.PulseAll(state.Sync);
                            return new Lease(state, entry, pin);
                        }
                    }
                    catch (IsolatedStorageException) { }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                    entry.Ready = false;
                }
                if (!entry.Rejected && !entry.Queued && !state.Cancelled)
                {
                    entry.Queued = true;
                    state.Jobs.Enqueue(entry);
                    Monitor.PulseAll(state.Sync);
                }
            }
            return new Lease(source);
        }

        internal static void Queue(string source) { using (Acquire(source)) { } }

        internal static void Reject(string original, string cached)
        {
            State state;
            lock (Configuration) state = _state;
            if (state == null || original == null || cached == null) return;
            string sourcePath, cachedPath;
            try { sourcePath = Normalize(original); cachedPath = System.IO.Path.GetFullPath(cached); }
            catch (Exception) { return; }
            lock (state.Sync)
            {
                foreach (Entry entry in state.Entries.Values)
                {
                    if (!string.Equals(entry.Source, sourcePath, StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(entry.Bundle, cachedPath, StringComparison.OrdinalIgnoreCase)) continue;
                    entry.Ready = false;
                    entry.Rejected = entry.WriteBad = true;
                    state.RejectionsPending = true;
                    state.Logs.Enqueue("bundle file cache rejected; using source until it changes: " + original);
                    Monitor.PulseAll(state.Sync);
                    break;
                }
            }
        }

        internal static void Tick(bool idle)
        {
            State state;
            lock (Configuration) state = _state;
            if (state == null) return;
            List<string> messages = new List<string>();
            lock (state.Sync)
            {
                state.Idle = idle;
                foreach (Entry entry in state.Entries.Values) if (entry.WriteBad) state.RejectionsPending = true;
                while (state.Logs.Count != 0) messages.Add(state.Logs.Dequeue());
                Monitor.PulseAll(state.Sync);
            }
            foreach (string message in messages)
                try { if (state.Log != null) state.Log(message); }
                catch (Exception) { }
        }

        internal static void Shutdown()
        {
            lock (Configuration) { Stop(_state); _state = null; }
        }

        private static void Stop(State state)
        {
            if (state == null) return;
            lock (state.Sync) { state.Cancelled = true; Monitor.PulseAll(state.Sync); }
        }

        private static bool Allowed(string source)
        {
            try { return source != null && string.Equals(System.IO.Path.GetFileName(source), "info_dmm", StringComparison.OrdinalIgnoreCase); }
            catch (ArgumentException) { return false; }
        }

        private static string Normalize(string source) { return System.IO.Path.GetFullPath(source); }

        private static Entry Describe(State state, string source)
        {
            string path = Normalize(source);
            FileInfo file = new FileInfo(path);
            if (!file.Exists) throw new IOException("Source bundle does not exist");
            string identity = "UnityFS-LZ4/" + FormatVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + "\n" + path.ToUpperInvariant() + "\n" + file.Length + "\n" + file.LastWriteTimeUtc.Ticks;
            string key;
            using (SHA256 hash = SHA256.Create()) key = Hex(hash.ComputeHash(Encoding.UTF8.GetBytes(identity)));
            return new Entry { Source = path, Key = key, SourceLength = file.Length, SourceTicks = file.LastWriteTimeUtc.Ticks,
                Bundle = System.IO.Path.Combine(state.Root, key + ".bundle"), Metadata = System.IO.Path.Combine(state.Root, key + ".meta"),
                LockFile = System.IO.Path.Combine(state.Root, key + ".lock"), BadFile = System.IO.Path.Combine(state.Root, key + ".bad") };
        }

        private static void Checkpoint(State state)
        {
            while (true)
            {
                bool flush;
                lock (state.Sync)
                {
                    if (state.Cancelled) throw new OperationCanceledException();
                    if (state.Idle) return;
                    flush = state.RejectionsPending;
                    state.RejectionsPending = false;
                    if (!flush) { Monitor.Wait(state.Sync); continue; }
                }
                FlushRejections(state);
            }
        }

        private static void Work(State state)
        {
            try
            {
                while (true)
                {
                    Entry entry = null;
                    bool idle;
                    lock (state.Sync)
                    {
                        while (!state.Cancelled && !state.RejectionsPending && (!state.Idle || (state.Jobs.Count == 0 && !state.Maintenance))) Monitor.Wait(state.Sync);
                        if (state.Cancelled) return;
                        idle = state.Idle;
                        if (idle && state.Jobs.Count != 0) entry = state.Jobs.Dequeue();
                        if (idle) state.Maintenance = false;
                        state.RejectionsPending = false;
                    }
                    try
                    {
                        Directory.CreateDirectory(state.Root);
                        FlushRejections(state);
                        if (!idle) continue;
                        if (entry != null) Prepare(state, entry);
                        Maintain(state);
                    }
                    catch (OperationCanceledException) { return; }
                    catch (Exception error) { Log(state, "bundle file cache worker: " + error); }
                    finally { if (entry != null) lock (state.Sync) entry.Queued = false; }
                }
            }
            finally
            {
                FlushRejections(state);
                lock (state.Sync) { state.Cancelled = true; Monitor.PulseAll(state.Sync); }
            }
        }

        private static FileStream TryLock(string path)
        {
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IsolatedStorageException) { return null; }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        private static void Prepare(State state, Entry entry)
        {
            Checkpoint(state);
            lock (state.Sync) if (entry.Rejected) return;
            using (FileStream fileLock = TryLock(entry.LockFile))
            {
                if (fileLock == null) return;
                if (!MatchesSource(entry)) return;
                if (IsBad(entry))
                {
                    lock (state.Sync) entry.Rejected = true;
                    Log(state, "bundle file cache disabled after previous Unity load failure: " + entry.Source
                        + " (format " + FormatVersion + "; using source)");
                    return;
                }
                Metadata metadata = ReadMetadata(entry);
                if (metadata != null && Validate(state, entry, metadata))
                {
                    MarkReady(state, entry, metadata.OutputLength);
                    Log(state, "bundle file cache validated: " + entry.Source);
                    return;
                }
                lock (state.Sync) if (entry.Rejected || entry.Pins != 0) return;
                if (!Delete(entry.Metadata) || !Delete(entry.Bundle)) return;
                Checkpoint(state);
                if (!UnityFsRepacker.IsEligible(entry.Source)) return;
                string temporary = System.IO.Path.Combine(state.Root, entry.Key + "." + Guid.NewGuid().ToString("N") + ".tmp");
                string metadataTemporary = temporary + ".meta";
                try
                {
                    System.Diagnostics.Stopwatch timer = System.Diagnostics.Stopwatch.StartNew();
                    UnityFsRepacker.ConversionResult result = state.Convert(entry.Source, temporary, () => state.Cancelled, () => Checkpoint(state));
                    Checkpoint(state);
                    lock (state.Sync) if (entry.Rejected) return;
                    if (!MatchesSource(entry) || !ValidResult(result, entry) || new FileInfo(temporary).Length != result.OutputLength
                        || !Same(HashFile(state, temporary), result.OutputHash) || !Same(HashFile(state, entry.Source), result.SourceHash)
                        || !MatchesSource(entry)) throw new IOException("Converted bundle validation failed");
                    WriteMetadata(metadataTemporary, entry, result);
                    metadata = ReadMetadataFile(metadataTemporary, entry);
                    if (metadata == null) throw new IOException("Converted bundle metadata validation failed");
                    if (!Prune(state, result.OutputLength + new FileInfo(metadataTemporary).Length))
                    {
                        Log(state, "bundle file cache capacity pinned; source retained: " + entry.Source);
                        return;
                    }
                    Checkpoint(state);
                    lock (state.Sync) if (entry.Rejected) return;
                    if (!MatchesSource(entry)) return;
                    File.Move(temporary, entry.Bundle);
                    File.Move(metadataTemporary, entry.Metadata);
                    MarkReady(state, entry, result.OutputLength);
                    Log(state, "bundle file cache built: " + entry.Source + " (" + result.SourceLength + " -> " + result.OutputLength
                        + " bytes, " + timer.ElapsedMilliseconds + " ms)");
                }
                finally { Delete(temporary); Delete(metadataTemporary); }
            }
        }

        private static bool MatchesSource(Entry entry)
        {
            try { FileInfo file = new FileInfo(entry.Source); return file.Exists && file.Length == entry.SourceLength && file.LastWriteTimeUtc.Ticks == entry.SourceTicks; }
            catch (IsolatedStorageException) { return false; }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        private static bool ValidResult(UnityFsRepacker.ConversionResult result, Entry entry)
        {
            return result != null && result.SourceLength == entry.SourceLength && result.OutputLength > 0 && result.OutputLength <= MaxBytes
                && result.DataLength > 0 && result.BlockCount > 0 && result.SourceHash != null && result.SourceHash.Length == 32
                && result.OutputHash != null && result.OutputHash.Length == 32 && result.DataHash != null && result.DataHash.Length == 32;
        }

        private static void WriteMetadata(string path, Entry entry, UnityFsRepacker.ConversionResult result)
        {
            using (FileStream stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (BinaryWriter writer = new BinaryWriter(stream))
            {
                writer.Write(Magic); writer.Write(Schema);
                byte[] name = Encoding.UTF8.GetBytes(entry.Source);
                writer.Write(name.Length); writer.Write(name);
                writer.Write(entry.SourceLength); writer.Write(entry.SourceTicks);
                writer.Write(result.OutputLength); writer.Write(result.DataLength); writer.Write(result.BlockCount);
                writer.Write(result.SourceHash); writer.Write(result.DataHash); writer.Write(result.OutputHash);
                writer.Flush();
            }
        }

        private static Metadata ReadMetadata(Entry entry)
        {
            try { return File.Exists(entry.Metadata) ? ReadMetadataFile(entry.Metadata, entry) : null; }
            catch (IsolatedStorageException) { return null; }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        private static Metadata ReadMetadataFile(string path, Entry entry)
        {
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (BinaryReader reader = new BinaryReader(stream))
            {
                if (stream.Length > 65536 || reader.ReadInt32() != Magic || reader.ReadInt32() != Schema) return null;
                int length = reader.ReadInt32();
                if (length <= 0 || length > 32768) return null;
                byte[] name = reader.ReadBytes(length);
                if (name.Length != length || !string.Equals(Encoding.UTF8.GetString(name), entry.Source, StringComparison.OrdinalIgnoreCase)
                    || reader.ReadInt64() != entry.SourceLength || reader.ReadInt64() != entry.SourceTicks) return null;
                Metadata result = new Metadata { OutputLength = reader.ReadInt64(), DataLength = reader.ReadInt64(), BlockCount = reader.ReadInt32(),
                    SourceHash = reader.ReadBytes(32), DataHash = reader.ReadBytes(32), OutputHash = reader.ReadBytes(32) };
                if (result.OutputLength <= 0 || result.OutputLength > MaxBytes || result.DataLength <= 0 || result.BlockCount <= 0
                    || result.SourceHash.Length != 32 || result.DataHash.Length != 32 || result.OutputHash.Length != 32 || stream.Position != stream.Length) return null;
                return result;
            }
        }

        private static bool Validate(State state, Entry entry, Metadata metadata)
        {
            try
            {
                FileInfo output = new FileInfo(entry.Bundle);
                return output.Exists && output.Length == metadata.OutputLength && Same(HashFile(state, entry.Source), metadata.SourceHash)
                    && Same(HashFile(state, entry.Bundle), metadata.OutputHash) && MatchesSource(entry);
            }
            catch (IsolatedStorageException) { return false; }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        private static byte[] HashFile(State state, string path)
        {
            using (SHA256 hash = SHA256.Create())
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                byte[] buffer = new byte[65536];
                int count;
                while (true)
                {
                    Checkpoint(state);
                    count = stream.Read(buffer, 0, buffer.Length);
                    if (count == 0) break;
                    hash.TransformBlock(buffer, 0, count, buffer, 0);
                }
                hash.TransformFinalBlock(new byte[0], 0, 0);
                return hash.Hash;
            }
        }

        private static void MarkReady(State state, Entry entry, long length)
        {
            FileInfo file = new FileInfo(entry.Bundle);
            lock (state.Sync)
            {
                if (entry.Rejected || state.Cancelled) return;
                entry.OutputLength = length;
                entry.OutputTicks = file.LastWriteTimeUtc.Ticks;
                entry.LastUse = DateTime.UtcNow.Ticks;
                entry.Ready = entry.Touch = true;
                state.Maintenance = true;
            }
        }

        private static bool IsBad(Entry entry)
        {
            try
            {
                if (!File.Exists(entry.BadFile)) return false;
                using (BinaryReader reader = new BinaryReader(File.OpenRead(entry.BadFile)))
                    return reader.BaseStream.Length == 24 && reader.ReadInt32() == BadMagic && reader.ReadInt32() == Schema
                        && reader.ReadInt64() == entry.SourceLength && reader.ReadInt64() == entry.SourceTicks;
            }
            catch (IsolatedStorageException) { return false; }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        private static void Maintain(State state)
        {
            CleanupTemporaryFiles(state);
            List<Entry> entries;
            lock (state.Sync) entries = new List<Entry>(state.Entries.Values);
            foreach (Entry entry in entries)
            {
                Checkpoint(state);
                bool touch;
                lock (state.Sync) { touch = entry.Touch; entry.Touch = false; }
                try
                {
                    if (touch && File.Exists(entry.Metadata)) File.SetLastWriteTimeUtc(entry.Metadata, new DateTime(entry.LastUse, DateTimeKind.Utc));
                }
                catch (IsolatedStorageException) { }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            Prune(state, 0);
        }

        private static void FlushRejections(State state)
        {
            List<Entry> entries;
            lock (state.Sync) entries = new List<Entry>(state.Entries.Values);
            foreach (Entry entry in entries)
            {
                lock (state.Sync) if (!entry.WriteBad) continue;
                try
                {
                    using (FileStream fileLock = TryLock(entry.LockFile))
                    {
                        if (fileLock == null) continue;
                        string temporary = System.IO.Path.Combine(state.Root, entry.Key + "." + Guid.NewGuid().ToString("N") + ".tmp");
                        try
                        {
                            using (BinaryWriter writer = new BinaryWriter(new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)))
                            { writer.Write(BadMagic); writer.Write(Schema); writer.Write(entry.SourceLength); writer.Write(entry.SourceTicks); }
                            if (!Delete(entry.BadFile)) continue;
                            File.Move(temporary, entry.BadFile);
                            lock (state.Sync) entry.WriteBad = false;
                        }
                        finally { Delete(temporary); }
                    }
                }
                catch (IsolatedStorageException) { }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        private static void CleanupTemporaryFiles(State state)
        {
            foreach (string path in Directory.GetFiles(state.Root))
            {
                Checkpoint(state);
                string name = System.IO.Path.GetFileName(path);
                if (name.Length != 101 && name.Length != 106) continue;
                string key = name.Substring(0, 64), id = name.Substring(65, 32);
                if (!IsKey(key) || name[64] != '.' || name.Substring(97) != (name.Length == 101 ? ".tmp" : ".tmp.meta")) continue;
                bool valid = true;
                foreach (char ch in id) if (!((ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f'))) valid = false;
                if (!valid) continue;
                using (FileStream fileLock = TryLock(System.IO.Path.Combine(state.Root, key + ".lock")))
                    if (fileLock != null) Delete(path);
            }
        }

        private static bool Prune(State state, long incoming)
        {
            Checkpoint(state);
            List<DiskEntry> files = new List<DiskEntry>();
            long bytes = incoming;
            foreach (string path in Directory.GetFiles(state.Root, "*.bundle"))
            {
                Checkpoint(state);
                string key = System.IO.Path.GetFileNameWithoutExtension(path);
                if (!IsKey(key)) continue;
                FileInfo file = new FileInfo(path);
                string metadata = System.IO.Path.Combine(state.Root, key + ".meta");
                FileInfo meta = new FileInfo(metadata);
                long size = file.Length + (meta.Exists ? meta.Length : 0);
                long lastUse = meta.Exists ? meta.LastWriteTimeUtc.Ticks : file.LastWriteTimeUtc.Ticks;
                lock (state.Sync)
                {
                    Entry entry;
                    if (state.Entries.TryGetValue(key, out entry)) lastUse = Math.Max(lastUse, entry.LastUse);
                }
                bytes += size;
                files.Add(new DiskEntry { Key = key, Bundle = path, Metadata = metadata, Bytes = size,
                    LastUse = lastUse });
            }
            files.Sort((left, right) => left.LastUse.CompareTo(right.LastUse));
            foreach (DiskEntry file in files)
            {
                if (bytes <= state.Budget) break;
                Checkpoint(state);
                lock (state.Sync)
                {
                    Entry entry;
                    if (state.Entries.TryGetValue(file.Key, out entry) && (entry.Pins != 0 || entry.Queued)) continue;
                    using (FileStream fileLock = TryLock(System.IO.Path.Combine(state.Root, file.Key + ".lock")))
                    {
                        if (fileLock == null || !Delete(file.Bundle)) continue;
                        Delete(file.Metadata);
                        bytes -= file.Bytes;
                        if (entry != null) entry.Ready = false;
                    }
                }
            }
            return bytes <= state.Budget;
        }

        private static bool IsKey(string value)
        {
            if (value.Length != 64) return false;
            foreach (char ch in value) if (!((ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f'))) return false;
            return true;
        }

        private static bool Delete(string path)
        {
            try { File.Delete(path); return !File.Exists(path); }
            catch (IsolatedStorageException) { return false; }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        private static bool Same(byte[] left, byte[] right)
        {
            if (left.Length != right.Length) return false;
            for (int i = 0; i < left.Length; i++) if (left[i] != right[i]) return false;
            return true;
        }

        private static string Hex(byte[] bytes)
        {
            StringBuilder result = new StringBuilder(bytes.Length * 2);
            foreach (byte value in bytes) result.Append(value.ToString("x2"));
            return result.ToString();
        }

        private static void Log(State state, string message)
        {
            lock (state.Sync) state.Logs.Enqueue(message);
        }
    }
}
