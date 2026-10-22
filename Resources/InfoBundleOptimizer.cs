using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using DIYLevelFastInit.BundleCodecs;

namespace DIYLevelFastInit
{
    /// <summary>Replaces one info bundle with a verified independent bundle while retaining its original.</summary>
    internal static class InfoBundleOptimizer
    {
        internal enum Result { Replaced, AlreadyOptimized, Unsupported, Failed, Restored, NoBackup }

        private sealed class Record
        {
            internal string Name, Path, Backup;
            internal long SourceLength, SourceTicks, OutputLength, OutputTicks;
            internal byte[] SourceHash, OutputHash;
        }

        private const int Magic = 0x314F4944, Schema = 1, MaxNameBytes = 4096;
        private const long MaxLength = 2L * 1024 * 1024 * 1024 - 1;

        internal static Result Optimize(string source, Func<bool> cancelled, Action<string> log)
        {
            string converted = null, recordTemporary = null;
            try
            {
                source = SourcePath(source);
                CheckCancelled(cancelled);
                string directory = WorkDirectory(source);
                using (FileStream guard = Lock(directory))
                {
                    if (UnityFsRepacker.IsIndependent(source))
                    {
                        Log(log, "info bundle already optimized: " + System.IO.Path.GetFileName(source));
                        return Result.AlreadyOptimized;
                    }
                    if (!UnityFsRepacker.IsEligible(source))
                    {
                        Log(log, "info bundle source layout unsupported or unavailable; original retained: " + source);
                        return Result.Unsupported;
                    }
                    FileInfo stamp = new FileInfo(source);
                    long length = stamp.Length, ticks = stamp.LastWriteTimeUtc.Ticks;
                    Log(log, "info bundle optimization started: " + source);
                    converted = Unique(directory, "converted-", ".tmp");
                    UnityFsRepacker.ConversionResult result = UnityFsRepacker.Convert(source, converted, cancelled);
                    UnityFsRepacker.ValidateConverted(source, converted, result, cancelled);
                    CheckCancelled(cancelled);
                    if (result == null || result.SourceLength != length || !ValidHash(result.SourceHash) || !ValidHash(result.OutputHash))
                        throw new IOException("Converted source fingerprint does not match the preparation snapshot");
                    VerifyExclusive(source, length, ticks, result.SourceHash, cancelled);
                    string id = Guid.NewGuid().ToString("N");
                    Record record = new Record { Name = System.IO.Path.GetFileName(source), SourceLength = length, SourceTicks = ticks,
                        OutputLength = result.OutputLength, OutputTicks = File.GetLastWriteTimeUtc(converted).Ticks,
                        SourceHash = result.SourceHash, OutputHash = result.OutputHash,
                        Path = System.IO.Path.Combine(directory, "original-" + id + ".record"),
                        Backup = System.IO.Path.Combine(directory, "original-" + id + ".bundle") };
                    if (File.Exists(record.Path) || File.Exists(record.Backup)) throw new IOException("Original backup name already exists");
                    recordTemporary = Unique(directory, "record-", ".tmp");
                    WriteRecord(recordTemporary, record);
                    Record verified = ReadRecord(recordTemporary);
                    if (!SameRecord(record, verified)) throw new IOException("Prepared original record failed readback verification");
                    File.Move(recordTemporary, record.Path);
                    recordTemporary = null;
                    VerifyExclusive(source, length, ticks, result.SourceHash, cancelled);
                    CheckCancelled(cancelled);
                    if (!Commit(source, converted, record.Backup, record.SourceLength, record.SourceHash,
                        record.OutputLength, record.OutputHash, directory, log)) return Result.Failed;
                    converted = null;
                    Log(log, "info bundle optimized in place: " + source + " (original: " + record.Backup + ")");
                    return Result.Replaced;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) { Log(log, "info bundle optimization failed; original/recovery files retained: " + source + "\n" + error); return Result.Failed; }
            finally { DeleteTemporary(converted, log); DeleteTemporary(recordTemporary, log); }
        }

        internal static Result Restore(string source, Func<bool> cancelled, Action<string> log)
        {
            string temporary = null;
            try
            {
                source = SourcePath(source);
                CheckCancelled(cancelled);
                string directory = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(source), ".diylevel-fastinit");
                if (!Directory.Exists(directory)) return Result.NoBackup;
                using (FileStream guard = Lock(directory))
                {
                    FileInfo stamp = new FileInfo(source);
                    long length = stamp.Length, ticks = stamp.LastWriteTimeUtc.Ticks;
                    byte[] currentHash = HashFile(source, cancelled);
                    List<string> paths = new List<string>(Directory.GetFiles(directory, "original-*.record"));
                    paths.Sort((left, right) => File.GetLastWriteTimeUtc(right).CompareTo(File.GetLastWriteTimeUtc(left)));
                    bool found = false;
                    foreach (string path in paths)
                    {
                        CheckCancelled(cancelled);
                        if (!RecordName(path)) continue;
                        Record record;
                        try { record = ReadRecord(path); }
                        catch (Exception error) { Log(log, "invalid original record retained: " + path + "\n" + error); continue; }
                        if (!string.Equals(record.Name, System.IO.Path.GetFileName(source), StringComparison.OrdinalIgnoreCase)) continue;
                        found = true;
                        if (length == record.SourceLength && Same(currentHash, record.SourceHash)) return Result.Restored;
                        if (length != record.OutputLength || !Same(currentHash, record.OutputHash)) continue;
                        if (!Matches(record.Backup, record.SourceLength, record.SourceHash, cancelled))
                        {
                            Log(log, "original backup missing or changed; source retained: " + source + " (" + record.Backup + ")");
                            continue;
                        }
                        temporary = Unique(directory, "restore-", ".tmp");
                        CopyStable(record.Backup, temporary, cancelled);
                        if (!Matches(temporary, record.SourceLength, record.SourceHash, cancelled)) throw new IOException("Original backup copy verification failed");
                        File.SetLastWriteTimeUtc(temporary, new DateTime(record.SourceTicks, DateTimeKind.Utc));
                        VerifyExclusive(source, length, ticks, currentHash, cancelled);
                        CheckCancelled(cancelled);
                        string retained = Unique(directory, "restored-output-", ".bundle");
                        if (!Commit(source, temporary, retained, length, currentHash, record.SourceLength, record.SourceHash, directory, log)) return Result.Failed;
                        temporary = null;
                        Log(log, "original info bundle restored: " + source + " (optimized version retained: " + retained + ")");
                        return Result.Restored;
                    }
                    if (found) Log(log, "original restore skipped: current map or backup differs from its recorded version: " + source);
                    return found ? Result.Failed : Result.NoBackup;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) { Log(log, "original info bundle restore failed; source/recovery files retained: " + source + "\n" + error); return Result.Failed; }
            finally { DeleteTemporary(temporary, log); }
        }

        private static bool Commit(string source, string replacement, string backup, long beforeLength, byte[] beforeHash,
            long afterLength, byte[] afterHash, string directory, Action<string> log)
        {
            try
            {
                if (File.Exists(backup)) throw new IOException("Refusing to overwrite an existing original/recovery backup");
                File.Replace(replacement, source, backup, false);
                if (Matches(backup, beforeLength, beforeHash, null) && Matches(source, afterLength, afterHash, null)) return true;
                Log(log, "in-place replacement verification failed; restoring the exact captured original: " + source);
            }
            catch (Exception error)
            {
                Log(log, "atomic in-place replacement failed: " + source + "\n" + error);
                if (Matches(source, beforeLength, beforeHash, null)) return false;
            }
            try
            {
                if (!File.Exists(backup)) throw new IOException("No captured original is available for automatic rollback");
                string rollback = Unique(directory, "rollback-", ".tmp");
                string retained = Unique(directory, "failed-output-", ".bundle");
                try
                {
                    CopyStable(backup, rollback, null);
                    long originalLength = new FileInfo(backup).Length;
                    byte[] originalHash = HashFile(backup, null);
                    if (!Matches(rollback, originalLength, originalHash, null)) throw new IOException("Rollback copy verification failed");
                    if (File.Exists(source)) File.Replace(rollback, source, retained, false);
                    else
                    {
                        if (File.Exists(replacement)) File.Move(replacement, retained);
                        File.Move(rollback, source);
                    }
                    if (!Matches(source, originalLength, originalHash, null)) throw new IOException("Rollback verification failed");
                    Log(log, "in-place replacement rolled back; both versions retained: " + source + " (original: " + backup + ", replaced version: " + retained + ")");
                }
                finally { DeleteTemporary(rollback, log); }
            }
            catch (Exception error) { Log(log, "automatic rollback failed; preserve all original/recovery files for manual recovery: " + source + "\n" + error); }
            return false;
        }

        private static string SourcePath(string source)
        {
            string path = System.IO.Path.GetFullPath(source);
            if (!System.IO.Path.GetFileName(path).StartsWith("info", StringComparison.OrdinalIgnoreCase)) throw new IOException("Only info bundle paths may be optimized");
            if (!File.Exists(path)) throw new IOException("Source info bundle does not exist");
            return path;
        }

        private static string WorkDirectory(string source)
        {
            string directory = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(source), ".diylevel-fastinit");
            Directory.CreateDirectory(directory);
            return directory;
        }

        private static FileStream Lock(string directory)
        { return new FileStream(System.IO.Path.Combine(directory, "replace.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }

        private static string Unique(string directory, string prefix, string suffix)
        {
            string path;
            do { path = System.IO.Path.Combine(directory, prefix + Guid.NewGuid().ToString("N") + suffix); } while (File.Exists(path));
            return path;
        }

        private static void VerifyExclusive(string source, long length, long ticks, byte[] hash, Func<bool> cancelled)
        {
            using (FileStream stream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                if (stream.Length != length || File.GetLastWriteTimeUtc(source).Ticks != ticks || !Same(HashStream(stream, cancelled), hash))
                    throw new IOException("Source info bundle changed before in-place commit");
            }
        }

        private static void CopyStable(string source, string target, Func<bool> cancelled)
        {
            using (FileStream input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (FileStream output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                byte[] buffer = new byte[65536]; int count;
                while ((count = input.Read(buffer, 0, buffer.Length)) != 0) { CheckCancelled(cancelled); output.Write(buffer, 0, count); }
                output.Flush();
            }
        }

        private static byte[] HashFile(string path, Func<bool> cancelled)
        { using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) return HashStream(stream, cancelled); }

        private static byte[] HashStream(Stream stream, Func<bool> cancelled)
        {
            using (SHA256 hash = SHA256.Create())
            {
                byte[] buffer = new byte[65536]; int count;
                while ((count = stream.Read(buffer, 0, buffer.Length)) != 0) { CheckCancelled(cancelled); hash.TransformBlock(buffer, 0, count, buffer, 0); }
                hash.TransformFinalBlock(new byte[0], 0, 0); return hash.Hash;
            }
        }

        private static bool Matches(string path, long length, byte[] hash, Func<bool> cancelled)
        {
            try { return new FileInfo(path).Length == length && Same(HashFile(path, cancelled), hash); }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { return false; }
        }

        private static bool ValidHash(byte[] hash) { return hash != null && hash.Length == 32; }
        private static bool Same(byte[] left, byte[] right)
        {
            if (!ValidHash(left) || !ValidHash(right)) return false;
            for (int i = 0; i < left.Length; i++) if (left[i] != right[i]) return false;
            return true;
        }

        private static void WriteRecord(string path, Record record)
        {
            using (FileStream stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (BinaryWriter writer = new BinaryWriter(stream))
            {
                byte[] name = Encoding.UTF8.GetBytes(record.Name);
                if (name.Length == 0 || name.Length > MaxNameBytes) throw new IOException("Original info basename is too long");
                writer.Write(Magic); writer.Write(Schema); writer.Write(name.Length); writer.Write(name);
                writer.Write(record.SourceLength); writer.Write(record.SourceTicks); writer.Write(record.OutputLength); writer.Write(record.OutputTicks);
                writer.Write(record.SourceHash); writer.Write(record.OutputHash); writer.Flush(); stream.Flush();
            }
        }

        private static Record ReadRecord(string path)
        {
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (BinaryReader reader = new BinaryReader(stream))
            {
                if (stream.Length > MaxNameBytes + 108 || reader.ReadInt32() != Magic || reader.ReadInt32() != Schema) throw new IOException("Invalid original record header");
                int count = reader.ReadInt32();
                if (count <= 0 || count > MaxNameBytes) throw new IOException("Invalid original basename length");
                byte[] nameBytes = reader.ReadBytes(count);
                if (nameBytes.Length != count) throw new IOException("Truncated original basename");
                string name = Encoding.UTF8.GetString(nameBytes);
                if (System.IO.Path.GetFileName(name) != name || name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0
                    || !name.StartsWith("info", StringComparison.OrdinalIgnoreCase)) throw new IOException("Invalid original basename");
                Record result = new Record { Name = name, Path = path, Backup = System.IO.Path.ChangeExtension(path, ".bundle"),
                    SourceLength = reader.ReadInt64(), SourceTicks = reader.ReadInt64(), OutputLength = reader.ReadInt64(), OutputTicks = reader.ReadInt64(),
                    SourceHash = reader.ReadBytes(32), OutputHash = reader.ReadBytes(32) };
                if (result.SourceLength <= 0 || result.SourceLength > MaxLength || result.OutputLength <= 0 || result.OutputLength > MaxLength
                    || result.SourceTicks < 0 || result.SourceTicks > DateTime.MaxValue.Ticks || result.OutputTicks < 0 || result.OutputTicks > DateTime.MaxValue.Ticks
                    || !ValidHash(result.SourceHash) || !ValidHash(result.OutputHash) || stream.Position != stream.Length) throw new IOException("Invalid original record extent");
                return result;
            }
        }

        private static bool SameRecord(Record left, Record right)
        { return left.Name == right.Name && left.SourceLength == right.SourceLength && left.SourceTicks == right.SourceTicks
            && left.OutputLength == right.OutputLength && left.OutputTicks == right.OutputTicks && Same(left.SourceHash, right.SourceHash) && Same(left.OutputHash, right.OutputHash); }

        private static bool RecordName(string path)
        {
            string name = System.IO.Path.GetFileName(path);
            if (name.Length != 48 || !name.StartsWith("original-", StringComparison.Ordinal) || !name.EndsWith(".record", StringComparison.Ordinal)) return false;
            for (int i = 9; i < 41; i++) if (!((name[i] >= '0' && name[i] <= '9') || (name[i] >= 'a' && name[i] <= 'f'))) return false;
            return true;
        }

        private static void CheckCancelled(Func<bool> cancelled) { if (cancelled != null && cancelled()) throw new OperationCanceledException(); }
        private static void DeleteTemporary(string path, Action<string> log)
        {
            if (path == null) return;
            try { File.Delete(path); }
            catch (Exception error) { Log(log, "owned in-place temporary retained after cleanup failure: " + path + "\n" + error); }
        }
        private static void Log(Action<string> log, string message) { try { if (log != null) log(message); } catch (Exception) { } }
    }
}
