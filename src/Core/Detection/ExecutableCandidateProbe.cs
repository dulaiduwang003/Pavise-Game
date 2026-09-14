// @author bdth 2074055628@qq.com
// File purpose Bounded read-only install entry recommendation; never runs or loads candidate files, never treats static dependencies as renderer observation
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PaviseApp
{
    internal sealed class ExecutableCandidateFacts
    {
        internal string Path;
        internal bool Executable;
        internal bool Gui;
        internal bool GraphicsImports;
        internal bool EngineDataPair;
    }

    internal static class ExecutableCandidateProbe
    {
        internal const int MaxDirectories = 512;
        internal const int MaxExecutables = 400;
        internal const int MaxSections = 96;
        internal const int MaxImports = 256;
        internal const int MaxReadBytes = 192 * 1024;
        // The root is depth 0; files in depth-8 directories are still collected
        internal const int MaxDirectoryDepth = 8;

        private sealed class DirectoryWork
        {
            internal string Path;
            internal int Depth;
        }

        private sealed class Section
        {
            internal uint Address;
            internal uint VirtualSize;
            internal uint RawSize;
            internal uint RawPointer;
        }

        private sealed class PeReader
        {
            internal Stream Stream;
            internal long Length;
            internal int Remaining = MaxReadBytes;
            internal uint HeaderSize;
            internal ulong ImageBase;
            internal readonly List<Section> Sections = new List<Section>();

            internal byte[] Read(long offset, int length)
            {
                if (length <= 0 || length > Remaining || offset < 0
                    || offset > Length || length > Length - offset) return null;
                Remaining -= length;
                byte[] result = new byte[length];
                Stream.Position = offset;
                int read = 0;
                while (read < length)
                {
                    int next = Stream.Read(result, read, length - read);
                    if (next <= 0) return null;
                    read += next;
                }
                return result;
            }

            internal long FileOffset(uint rva, long length)
            {
                if (length <= 0) return -1;
                ulong count = (ulong)length;
                ulong end = (ulong)rva + count;
                if (end > (ulong)uint.MaxValue + 1) return -1;
                long result = -1;
                if (rva < HeaderSize && end <= HeaderSize && end <= (ulong)Length)
                    result = rva;
                foreach (Section section in Sections)
                {
                    if (rva < section.Address) continue;
                    ulong delta = (ulong)rva - section.Address;
                    if (delta >= Math.Max(section.RawSize, section.VirtualSize)
                        || delta + count > section.RawSize) continue;
                    ulong offset = section.RawPointer + delta;
                    if (offset + count > (ulong)Length) continue;
                    // A file whose one RVA maps to multiple sections is not treated as reliable static evidence
                    if (result >= 0) return -1;
                    result = (long)offset;
                }
                return result;
            }

            internal string ImportName(uint rva)
            {
                var name = new StringBuilder();
                while (name.Length < 260)
                {
                    ulong next = (ulong)rva + (uint)name.Length;
                    if (next > uint.MaxValue) return null;
                    int count = 260 - name.Length;
                    long offset = FileOffset((uint)next, count);
                    while (offset < 0 && count > 1)
                    {
                        count /= 2;
                        offset = FileOffset((uint)next, count);
                    }
                    byte[] bytes = Read(offset, count);
                    if (bytes == null) return null;
                    foreach (byte value in bytes)
                    {
                        if (value == 0) return name.Length == 0 ? null : name.ToString();
                        if (value < 32 || value > 126) return null;
                        name.Append((char)value);
                    }
                }
                return null;
            }
        }

        // Every name comes from the platform graphics API ABI, not from any game or client list
        // The import table only helps pick an install entry; it cannot prove that game frames were actually presented this time
        private static bool GraphicsDependency(string module)
        {
            if (string.IsNullOrEmpty(module)) return false;
            switch (module.ToLowerInvariant())
            {
                case "d3d8.dll":
                case "d3d9.dll":
                case "d3d10.dll":
                case "d3d10_1.dll":
                case "d3d11.dll":
                case "d3d12.dll":
                case "opengl32.dll":
                case "vulkan-1.dll": return true;
            }
            return false;
        }

        internal static ExecutableCandidateFacts ReadFacts(Stream stream)
        {
            bool readFailed;
            return ReadFacts(stream, out readFailed);
        }

        private static ExecutableCandidateFacts ReadFacts(Stream stream, out bool readFailed)
        {
            readFailed = false;
            var facts = new ExecutableCandidateFacts();
            try
            {
                if (stream == null || !stream.CanRead || !stream.CanSeek) return facts;
                var reader = new PeReader { Stream = stream, Length = stream.Length };
                byte[] dos = reader.Read(0, 64);
                if (dos == null || U16(dos, 0) != 0x5A4D) return facts;
                uint peOffset = U32(dos, 60);
                if (peOffset < 64 || peOffset > 1024 * 1024) return facts;
                byte[] coff = reader.Read(peOffset, 24);
                if (coff == null || U32(coff, 0) != 0x00004550) return facts;
                ushort sections = U16(coff, 6);
                ushort optionalLength = U16(coff, 20);
                ushort flags = U16(coff, 22);
                if (sections == 0 || sections > MaxSections || optionalLength > 4096
                    || (flags & 0x0002) == 0 || (flags & 0x2000) != 0) return facts;
                byte[] optional = reader.Read((long)peOffset + 24, optionalLength);
                if (optional == null || optional.Length < 96) return facts;
                ushort magic = U16(optional, 0);
                int directoryStart;
                if (magic == 0x010B) directoryStart = 96;
                else if (magic == 0x020B) directoryStart = 112;
                else return facts;
                if (optional.Length < directoryStart) return facts;
                uint directoryCount = U32(optional, directoryStart - 4);
                if (directoryCount > (optional.Length - directoryStart) / 8) return facts;
                reader.HeaderSize = U32(optional, 60);
                reader.ImageBase = magic == 0x010B ? U32(optional, 28) : U64(optional, 24);
                long sectionOffset = (long)peOffset + 24 + optionalLength;
                for (int i = 0; i < sections; i++)
                {
                    byte[] section = reader.Read(sectionOffset + i * 40, 40);
                    if (section == null) return facts;
                    reader.Sections.Add(new Section
                    {
                        VirtualSize = U32(section, 8), Address = U32(section, 12),
                        RawSize = U32(section, 16), RawPointer = U32(section, 20)
                    });
                }
                bool graphics = false;
                if (directoryCount > 1 && !ReadImports(reader,
                    U32(optional, directoryStart + 8), U32(optional, directoryStart + 12),
                    false, ref graphics)) return facts;
                if (directoryCount > 13 && !ReadImports(reader,
                    U32(optional, directoryStart + 13 * 8), U32(optional, directoryStart + 13 * 8 + 4),
                    true, ref graphics)) return facts;
                facts.Executable = true;
                facts.Gui = U16(optional, 68) == 2;
                facts.GraphicsImports = graphics;
                return facts;
            }
            catch (IOException) { readFailed = true; return new ExecutableCandidateFacts(); }
            catch (UnauthorizedAccessException) { readFailed = true; return new ExecutableCandidateFacts(); }
            catch { return new ExecutableCandidateFacts(); }
        }

        private static bool ReadImports(PeReader reader, uint address, uint size,
            bool delayed, ref bool graphics)
        {
            if (address == 0 && size == 0) return true;
            int stride = delayed ? 32 : 20;
            // Size may cover the whole .idata section, including thunks and the name table,
            // not just the DLL descriptors; validate the region but do not read it
            // Descriptors actually visited need an upper bound and must hit the terminator
            if (address == 0 || size < stride || reader.FileOffset(address, size) < 0) return false;
            int limit = (int)Math.Min((uint)MaxImports, size / (uint)stride);
            for (int i = 0; i < limit; i++)
            {
                ulong rva = (ulong)address + (uint)(i * stride);
                if (rva > uint.MaxValue) return false;
                byte[] entry = reader.Read(reader.FileOffset((uint)rva, stride), stride);
                if (entry == null) return false;
                bool zero = true;
                foreach (byte b in entry) if (b != 0) { zero = false; break; }
                if (zero) return true;
                uint nameRva = U32(entry, delayed ? 4 : 12);
                if (delayed && (U32(entry, 0) & 1) == 0)
                {
                    if (nameRva < reader.ImageBase
                        || nameRva - reader.ImageBase > uint.MaxValue) return false;
                    nameRva = (uint)(nameRva - reader.ImageBase);
                }
                string name = reader.ImportName(nameRva);
                if (name == null) return false;
                if (GraphicsDependency(name)) graphics = true;
            }
            // Import directory truncated or missing its terminator; do not keep the half result already seen
            return false;
        }

        private static ushort U16(byte[] bytes, int at) { return BitConverter.ToUInt16(bytes, at); }
        private static uint U32(byte[] bytes, int at) { return BitConverter.ToUInt32(bytes, at); }
        private static ulong U64(byte[] bytes, int at) { return BitConverter.ToUInt64(bytes, at); }

        internal static string PickUnique(IList<ExecutableCandidateFacts> candidates, bool complete)
        {
            if (candidates == null || candidates.Count == 0
                || candidates.Count > MaxExecutables) return null;
            ExecutableCandidateFacts best = null;
            int bestRank = 0;
            bool ambiguous = false;
            var seen = new Dictionary<string, ExecutableCandidateFacts>(StringComparer.OrdinalIgnoreCase);
            foreach (ExecutableCandidateFacts candidate in candidates)
            {
                if (candidate == null || !candidate.Executable || string.IsNullOrEmpty(candidate.Path)) continue;
                ExecutableCandidateFacts previous;
                if (seen.TryGetValue(candidate.Path, out previous))
                {
                    if (previous.Gui != candidate.Gui || previous.GraphicsImports != candidate.GraphicsImports
                        || previous.EngineDataPair != candidate.EngineDataPair) return null;
                    continue;
                }
                seen.Add(candidate.Path, candidate);
                int rank = candidate.GraphicsImports || candidate.EngineDataPair ? 2 : candidate.Gui ? 1 : 0;
                if (rank == 0) continue;
                if (rank > bestRank) { best = candidate; bestRank = rank; ambiguous = false; }
                else if (rank == bestRank) ambiguous = true;
            }
            // Depth and count budget cutoffs do not erase the unique graphics evidence already found; this is still only a scan suggestion,
            // it does not claim unscanned areas hold no other renderer, and it never yields an observed-renderer label
            // A weak candidate backed only by the GUI subsystem requires a complete scan and exactly one valid EXE
            return best == null || ambiguous || (bestRank < 2 && (!complete || seen.Count > 1))
                ? null : best.Path;
        }

        internal static string PickMainExecutable(string root)
        {
            bool complete;
            List<ExecutableCandidateFacts> candidates = CollectFacts(root, out complete);
            return candidates == null ? null : PickUnique(candidates, complete);
        }

        internal static int Rank(ExecutableCandidateFacts candidate)
        {
            if (candidate == null || !candidate.Executable) return -1;
            return candidate.GraphicsImports || candidate.EngineDataPair ? 2 : candidate.Gui ? 1 : 0;
        }

        // Candidate list for manual picking: graphics evidence first, then GUI subsystem, other executables last; names vetoed by the election are excluded
        //   This list is for the user, not an election, so uniqueness is not required and truncation does not abort it
        internal static List<ExecutableCandidateFacts> ListCandidates(string root, int max)
        {
            string recommended;
            return ListCandidates(root, max, out recommended);
        }

        internal static List<ExecutableCandidateFacts> ListCandidates(string root, int max,
            out string recommended)
        {
            bool complete, valid;
            List<ExecutableCandidateFacts> facts = CollectFacts(root, true, out complete, out valid);
            // The recommendation rests on all facts read; the list display cap must not turn several candidates into a unique one
            recommended = valid && facts != null ? PickUnique(facts, complete) : null;
            var list = new List<ExecutableCandidateFacts>();
            if (facts == null) return list;
            foreach (ExecutableCandidateFacts f in facts)
                if (f != null && f.Executable && !string.IsNullOrEmpty(f.Path)) list.Add(f);
            list.Sort(delegate(ExecutableCandidateFacts a, ExecutableCandidateFacts b)
            {
                int ra = Rank(a), rb = Rank(b);
                if (ra != rb) return rb - ra;
                return string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase);
            });
            if (max > 0 && list.Count > max) list.RemoveRange(max, list.Count - max);
            return list;
        }

        // Auto recommendation still requires no IO faults; manual candidates keep the remaining readable files
        internal static List<ExecutableCandidateFacts> CollectFacts(string root, out bool complete)
        {
            bool valid;
            return CollectFacts(root, false, out complete, out valid);
        }

        private static List<ExecutableCandidateFacts> CollectFacts(string root, bool keepReadable,
            out bool complete, out bool valid)
        {
            complete = false;
            valid = false;
            if (string.IsNullOrWhiteSpace(root)) return null;
            try
            {
                root = System.IO.Path.GetFullPath(root.Trim().Trim('"'));
                List<string> paths;
                valid = CollectPaths(root,
                    delegate(string directory) { return Directory.EnumerateFiles(directory, "*.exe"); },
                    Directory.EnumerateDirectories, File.GetAttributes, out paths, out complete);
                if (!valid && !keepReadable) return null;
                var candidates = new List<ExecutableCandidateFacts>();
                foreach (string path in paths)
                {
                    try
                    {
                        string name = System.IO.Path.GetFileNameWithoutExtension(path);
                        if (GameSessionDetector.ElectionVetoed(name, path)) continue;
                        var before = new FileInfo(path);
                        long length = before.Length;
                        DateTime modified = before.LastWriteTimeUtc;
                        ExecutableCandidateFacts facts;
                        bool readFailed;
                        using (var file = new FileStream(path, FileMode.Open, FileAccess.Read,
                            FileShare.ReadWrite | FileShare.Delete)) facts = ReadFacts(file, out readFailed);
                        var after = new FileInfo(path);
                        if (readFailed || after.Length != length || after.LastWriteTimeUtc != modified)
                        {
                            valid = false;
                            complete = false;
                            if (!keepReadable) return null;
                            continue;
                        }
                        facts.Path = path;
                        string dir = System.IO.Path.GetDirectoryName(path);
                        // The name pairing is an engine file format, not an election by a specific game name; still only a scan suggestion
                        facts.EngineDataPair = facts.Executable
                            && File.Exists(System.IO.Path.Combine(dir, "UnityPlayer.dll"))
                            && Directory.Exists(System.IO.Path.Combine(dir, name + "_Data"));
                        candidates.Add(facts);
                    }
                    catch
                    {
                        valid = false;
                        complete = false;
                        if (!keepReadable) return null;
                    }
                }
                return candidates;
            }
            catch { complete = false; valid = false; return null; }
        }

        // Lazy breadth-first gives shallow executables the first chance, so one deep
        // asset tree cannot eat the whole budget; delegates also allow a bounded virtual file system fixture
        // without really creating hundreds of directories
        // Local IO faults and reparse points only skip the current item; the remaining readable paths still go to the manual candidate list
        // false means an IO or reparse point error; true with complete=false only means
        // the depth or count cap was hit, and only the latter may keep a strong recommendation
        internal static bool CollectPaths(string root,
            Func<string, IEnumerable<string>> files,
            Func<string, IEnumerable<string>> directories,
            Func<string, FileAttributes> attributes,
            out List<string> paths, out bool complete)
        {
            paths = new List<string>();
            complete = true;
            if (string.IsNullOrEmpty(root) || files == null || directories == null || attributes == null)
            {
                complete = false;
                return false;
            }
            bool valid = true;
            try
            {
                var queue = new Queue<DirectoryWork>();
                var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                int executableCount = 0, directoryCount = 1;
                known.Add(root);
                queue.Enqueue(new DirectoryWork { Path = root, Depth = 0 });
                while (queue.Count > 0)
                {
                    DirectoryWork current = queue.Dequeue();
                    try
                    {
                        if ((attributes(current.Path) & FileAttributes.ReparsePoint) != 0)
                        { complete = false; valid = false; continue; }
                    }
                    catch { complete = false; valid = false; continue; }
                    try
                    {
                        foreach (string file in files(current.Path))
                        {
                            // Failed items also consume budget, so masses of unreadable files cannot cause an unbounded walk
                            if (executableCount >= MaxExecutables) { complete = false; return valid; }
                            executableCount++;
                            try
                            {
                                if ((attributes(file) & FileAttributes.ReparsePoint) != 0)
                                { complete = false; valid = false; continue; }
                                paths.Add(file);
                            }
                            catch { complete = false; valid = false; }
                        }
                    }
                    catch { complete = false; valid = false; }
                    try
                    {
                        foreach (string sub in directories(current.Path))
                        {
                            if (current.Depth >= MaxDirectoryDepth || directoryCount >= MaxDirectories)
                            { complete = false; break; }
                            directoryCount++;
                            if (!known.Add(sub)) continue;
                            queue.Enqueue(new DirectoryWork { Path = sub, Depth = current.Depth + 1 });
                        }
                    }
                    catch { complete = false; valid = false; }
                }
                return valid;
            }
            catch { complete = false; return false; }
        }
    }
}
