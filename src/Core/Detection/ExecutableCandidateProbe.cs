// @author bdth 2074055628@qq.com
// 文件用途 有界只读的安装入口推荐 不运行或加载候选文件 不把静态依赖当作渲染观测
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
        // 根目录算第 0 层 第 4 层目录里的文件仍然收录
        internal const int MaxDirectoryDepth = 4;

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
                    // 同一 RVA 映射多个区域的文件不当作可靠静态证据
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

        // 所有名称都来自平台图形 API 的 ABI 而不是任何游戏或客户端名单
        // 导入表只能帮助挑选安装入口 不能证明本次实际提交过游戏画面
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
            catch { return new ExecutableCandidateFacts(); }
        }

        private static bool ReadImports(PeReader reader, uint address, uint size,
            bool delayed, ref bool graphics)
        {
            if (address == 0 && size == 0) return true;
            int stride = delayed ? 32 : 20;
            // Size 可能覆盖整个 .idata 区 包括 thunk 和名字表
            // 不只是 DLL 描述符 校验这块区域但不去读它
            // 实际访问到的描述符要有上界 并且必须遇到终止符
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
            // 导入目录截断或没有终止项 不沿用已经看到的一半结果
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
            // 深度/数量预算截断不抹掉已经找到的唯一图形证据 这仍只是扫描建议
            // 不代表未扫描区域没有其它renderer 更不会产生“已观测渲染”标签
            // 单靠GUI子系统的弱候选则必须扫描完整且只有一个有效EXE
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

        // 手动挑选用的候选清单 图形证据优先 其次 GUI 子系统 其余可执行文件垫底 被选举否决的名字不进
        //   这是给用户看的列表 不是选举 所以不要求唯一 也不因为截断而放弃
        internal static List<ExecutableCandidateFacts> ListCandidates(string root, int max)
        {
            bool complete;
            List<ExecutableCandidateFacts> facts = CollectFacts(root, out complete);
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

        // 一个目录下全部可执行文件的事实 唯一选举与手动挑选共用
        //   目录越界 IO 出错或文件在读取中变化都返回 null 调用方自己决定怎么退
        internal static List<ExecutableCandidateFacts> CollectFacts(string root, out bool complete)
        {
            complete = false;
            if (string.IsNullOrWhiteSpace(root)) return null;
            try
            {
                root = System.IO.Path.GetFullPath(root.Trim().Trim('"'));
                List<string> paths;
                if (!CollectPaths(root,
                    delegate(string directory) { return Directory.EnumerateFiles(directory, "*.exe"); },
                    Directory.EnumerateDirectories, File.GetAttributes, out paths, out complete)) return null;
                var candidates = new List<ExecutableCandidateFacts>();
                foreach (string path in paths)
                {
                    string name = System.IO.Path.GetFileNameWithoutExtension(path);
                    if (GameSessionDetector.ElectionVetoed(name, path)) continue;
                    var before = new FileInfo(path);
                    long length = before.Length;
                    DateTime modified = before.LastWriteTimeUtc;
                    ExecutableCandidateFacts facts;
                    using (var file = new FileStream(path, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete)) facts = ReadFacts(file);
                    var after = new FileInfo(path);
                    if (after.Length != length || after.LastWriteTimeUtc != modified) return null;
                    facts.Path = path;
                    string dir = System.IO.Path.GetDirectoryName(path);
                    // 命名配对属于引擎文件格式 不是按特定游戏名选举 仍只作扫描建议
                    facts.EngineDataPair = facts.Executable
                        && File.Exists(System.IO.Path.Combine(dir, "UnityPlayer.dll"))
                        && Directory.Exists(System.IO.Path.Combine(dir, name + "_Data"));
                    candidates.Add(facts);
                }
                return candidates;
            }
            catch { return null; }
        }

        // 惰性广度优先让浅层的可执行文件先拿到机会 免得一棵深的
        // 资源目录把预算吃光 用委托还能搭出一个有界的虚拟文件系统夹具
        // 不用真去建几百个目录
        // false 表示 IO 或重解析点出错 true 加 complete=false 只是
        // 碰到了深度或数量上限 只有后一种才允许保留强推荐
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
            try
            {
                var queue = new Queue<DirectoryWork>();
                var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                known.Add(root);
                queue.Enqueue(new DirectoryWork { Path = root, Depth = 0 });
                while (queue.Count > 0)
                {
                    DirectoryWork current = queue.Dequeue();
                    if ((attributes(current.Path) & FileAttributes.ReparsePoint) != 0)
                    { complete = false; return false; }
                    foreach (string file in files(current.Path))
                    {
                        if (paths.Count >= MaxExecutables) { complete = false; return true; }
                        if ((attributes(file) & FileAttributes.ReparsePoint) != 0)
                        { complete = false; return false; }
                        paths.Add(file);
                    }
                    foreach (string sub in directories(current.Path))
                    {
                        if (current.Depth >= MaxDirectoryDepth)
                        { complete = false; break; }
                        if (known.Contains(sub)) continue;
                        if (known.Count >= MaxDirectories)
                        { complete = false; break; }
                        known.Add(sub);
                        queue.Enqueue(new DirectoryWork { Path = sub, Depth = current.Depth + 1 });
                    }
                }
                return true;
            }
            catch { complete = false; return false; }
        }
    }
}
