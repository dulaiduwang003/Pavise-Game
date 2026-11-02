using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal static class AntiCheatTopologyChecks
    {
        private static int checks;

        private static void Check(bool condition, string name)
        {
            checks++;
            if (!condition) throw new Exception(name);
        }

        private static AntiCheatAffinityCore Core(int group, ulong mask, int efficiency, int cache)
        {
            return new AntiCheatAffinityCore { Group = (ushort)group, Mask = mask,
                Efficiency = (byte)efficiency, L3Id = cache, L3Bytes = 32 * 1024 * 1024 };
        }

        private static AntiCheatAffinitySnapshot Shape(params AntiCheatAffinityCore[] cores)
        {
            return new AntiCheatAffinitySnapshot { Valid = true, Reason = "ok", CpuSetMetadata = true, Cores = cores };
        }

        private static AntiCheatAffinitySnapshot Homogeneous(int count, int split)
        {
            AntiCheatAffinityCore[] cores = new AntiCheatAffinityCore[count];
            for (int i = 0; i < count; i++) cores[i] = Core(0, 3UL << (i * 2), 0, split == 0 ? 0 : i / split);
            return Shape(cores);
        }

        private static ulong All(AntiCheatAffinitySnapshot snapshot, ushort group)
        {
            ulong all = 0;
            foreach (AntiCheatAffinityCore core in snapshot.Cores) if (core.Group == group) all |= core.Mask;
            return all;
        }

        private static AntiCheatAffinityChoice Select(AntiCheatAffinitySnapshot shape, ulong protectedMask, ulong domainMask)
        {
            return AntiCheatAffinityTopology.Select(shape, 0, All(shape, 0), protectedMask, domainMask);
        }

        private static void Invariants(AntiCheatAffinitySnapshot shape, ushort group,
            ulong allowed, ulong protectedMask, AntiCheatAffinityChoice choice)
        {
            if (!choice.Applicable) { Check(choice.Mask == 0, "rejected choice has no mask"); return; }
            Check((choice.Mask & ~allowed) == 0, "never expand caller affinity");
            Check((choice.Mask & protectedMask) == 0, "never use protected siblings");
            int count = 0;
            foreach (AntiCheatAffinityCore core in shape.Cores)
            {
                if (core.Group != group || (choice.Mask & core.Mask) == 0) continue;
                Check((choice.Mask & core.Mask) == core.Mask, "complete physical core");
                Check(!core.Parked && !core.Allocated && !core.Favored, "read-only reservations respected");
                count++;
            }
            Check(count == choice.PhysicalCount && count >= 2, "at least two complete cores");
        }

        private static void ProcessorShapes()
        {
            AntiCheatAffinitySnapshot laptop = Homogeneous(6, 0);
            AntiCheatAffinityChoice chosen = Select(laptop, 0, 0);
            Check(chosen.Applicable && chosen.Kind == "auxiliary" && chosen.PhysicalCount == 2, "9750H two auxiliaries");
            Check(chosen.Mask == 0xF00, "9750H four logical siblings kept together");
            Invariants(laptop, 0, 0xFFF, 0, chosen);
            Check(!Select(Homogeneous(2, 0), 0, 0).Applicable, "dual core retained");
            Check(!Select(Homogeneous(4, 0), 0, 0).Applicable, "four core gameplay capacity retained");
            Check(Select(Homogeneous(8, 0), 0, 0).PhysicalCount == 2, "single CCD Ryzen auxiliaries");
            AntiCheatAffinitySnapshot x3dSingle = Homogeneous(8, 0);
            foreach (AntiCheatAffinityCore core in x3dSingle.Cores) core.L3Bytes = 96 * 1024 * 1024;
            Check(Select(x3dSingle, 0, 0).PhysicalCount == 2, "single X3D no invented extra domain");

            AntiCheatAffinitySnapshot dual = Homogeneous(16, 8);
            chosen = Select(dual, 0xFFFF, 0xFFFF);
            Check(chosen.Mask == 0xFFFF0000 && chosen.PhysicalCount == 8
                && chosen.Kind == "alternate-cache-domain", "dual cache domain preserved");
            foreach (AntiCheatAffinityCore core in dual.Cores)
                if (core.L3Id == 1) core.L3Bytes = 96 * 1024 * 1024;
            chosen = Select(dual, 0xFFFF0000, 0xFFFF0000);
            Check(chosen.Mask == 0xFFFF, "X3D uses explicit selected domain rather than cache size");
            chosen = Select(dual, 0, 0);
            Check(chosen.Kind == "auxiliary" && chosen.PhysicalCount == 2, "unrestricted game not evidence for alternate domain");
            chosen = Select(dual, 0, 0xFFFFFFFF);
            Check(chosen.Kind == "auxiliary", "cross domain game mask is not one selected domain");
            foreach (AntiCheatAffinityCore core in dual.Cores) if (core.L3Id == 0) core.Parked = true;
            chosen = Select(dual, 0, 0xFFFF0000);
            Check(!chosen.Applicable && chosen.Reason == "alternate-cache-domain-unavailable", "parked alternate X3D domain not forced awake");

            List<AntiCheatAffinityCore> hybridCores = new List<AntiCheatAffinityCore>();
            for (int i = 0; i < 8; i++) hybridCores.Add(Core(0, 3UL << (i * 2), 1, 0));
            for (int i = 0; i < 8; i++) hybridCores.Add(Core(0, 1UL << (16 + i), 0, 1));
            AntiCheatAffinitySnapshot hybrid = Shape(hybridCores.ToArray());
            chosen = Select(hybrid, 0, 0);
            Check(chosen.Kind == "efficiency" && chosen.Mask == 0xFF0000 && chosen.PhysicalCount == 8, "desktop E pool");
            Invariants(hybrid, 0, All(hybrid, 0), 0, chosen);
            hybrid.Cores[8].Parked = true; hybrid.Cores[9].Allocated = true;
            chosen = Select(hybrid, 1UL << 18, 0);
            Check(chosen.Mask == 0xF80000, "parked allocated and reserved E cores excluded");
            Check((chosen.Mask & 0xFFFF) == 0, "no P core fallback");
            for (int i = 8; i < 15; i++) hybrid.Cores[i].Parked = true;
            Check(!Select(hybrid, 0, 0).Applicable, "single E insufficient no SMT deception");

            for (int i = 0; i < 8; i++) hybrid.Cores[i].Parked = true;
            for (int i = 8; i < 16; i++) { hybrid.Cores[i].Parked = false; hybrid.Cores[i].Allocated = false; }
            chosen = Select(hybrid, 0, 0);
            Check(chosen.Mask == 0xFF0000, "idle parked P cores do not block active E pool");
            AntiCheatAffinitySnapshot isolated = Homogeneous(6, 0);
            for (int i = 0; i < 4; i++) isolated.Cores[i].Allocated = true;
            chosen = Select(isolated, 0xFF, 0);
            Check(chosen.Mask == 0xF00, "four exclusively reserved game cores count as game capacity");

            AntiCheatAffinitySnapshot ultra = Shape(Core(0, 3, 2, 0), Core(0, 12, 2, 0),
                Core(0, 16, 1, 1), Core(0, 32, 1, 1), Core(0, 64, 1, 1), Core(0, 128, 1, 1),
                Core(0, 256, 0, 2), Core(0, 512, 0, 2));
            chosen = Select(ultra, 0, 0);
            Check(chosen.Mask == 240 && chosen.PhysicalCount == 4, "three tiers prefer regular E rather than LP E");
            chosen = Select(ultra, 240, 0);
            Check(chosen.Mask == 768 && chosen.PhysicalCount == 2, "complete LP tier allowed only as fallback");
            ultra.Cores[0].Parked = true;
            Check(Select(ultra, 0, 0).Mask == 240, "parked game cores remain physically available capacity");
            ultra.Cores[1].Efficiency = 1;
            Check(!Select(ultra, 0, 0).Applicable, "hybrid still leaves two physically present performance cores");

            AntiCheatAffinitySnapshot ultraScheduling = Shape(Core(0, 3, 1, 0), Core(0, 12, 1, 0),
                Core(0, 16, 0, 1), Core(0, 32, 0, 1), Core(0, 64, 0, 1), Core(0, 128, 0, 1),
                Core(0, 256, 0, 2), Core(0, 512, 0, 2));
            for (int i = 2; i < 6; i++) ultraScheduling.Cores[i].Scheduling = 1;
            chosen = Select(ultraScheduling, 0, 0);
            Check(chosen.Mask == 240, "two efficiency classes with three scheduling classes keep LP separate");
            chosen = Select(ultraScheduling, 240, 0);
            Check(chosen.Mask == 768, "secondary scheduling class fallback complete physical cores");

            AntiCheatAffinitySnapshot mobileAmd = Shape(Core(0, 3, 3, 0), Core(0, 12, 3, 0),
                Core(0, 48, 1, 0), Core(0, 192, 1, 0), Core(0, 768, 1, 0), Core(0, 3072, 1, 0));
            Check(Select(mobileAmd, 0, 0).Mask == 4080, "AMD heterogeneous tiers use reported capacity");

            AntiCheatAffinitySnapshot preferred = Homogeneous(8, 0);
            for (int i = 0; i < preferred.Cores.Length; i++) preferred.Cores[i].Scheduling = (byte)(20 + i);
            chosen = Select(preferred, 0, 0);
            Check(chosen.Mask == 15, "core rank preserves faster preferred cores");
            preferred.Cores[0].Favored = preferred.Cores[1].Favored = true;
            chosen = Select(preferred, 0, 0);
            Check(chosen.Mask == 240, "explicit favored cores excluded");

            AntiCheatAffinitySnapshot multi = Homogeneous(32, 8);
            List<AntiCheatAffinityCore> groups = new List<AntiCheatAffinityCore>(multi.Cores);
            for (int i = 0; i < 32; i++) groups.Add(Core(1, 3UL << (i * 2), 0, 4 + i / 8));
            multi = Shape(groups.ToArray());
            chosen = AntiCheatAffinityTopology.Select(multi, 1, ulong.MaxValue, 0, 0xFFFF);
            Check(chosen.Applicable && chosen.PhysicalCount == 8 && chosen.Mask == 0xFFFF0000, "128 logical processors group one independent");
            Invariants(multi, 1, ulong.MaxValue, 0, chosen);
            chosen = AntiCheatAffinityTopology.Select(multi, 2, ulong.MaxValue, 0, 0);
            Check(!chosen.Applicable, "unknown group rejected");

            AntiCheatAffinitySnapshot sparse = Shape(Core(7, 3, 0, 1), Core(7, 12, 0, 1),
                Core(7, 3UL << 20, 0, 1), Core(7, 3UL << 30, 0, 1),
                Core(7, 3UL << 40, 0, 1), Core(7, 3UL << 62, 0, 1));
            chosen = AntiCheatAffinityTopology.Select(sparse, 7, All(sparse, 7), 1UL << 63, 0);
            Invariants(sparse, 7, All(sparse, 7), 1UL << 63, chosen);
            Check(chosen.Applicable && (chosen.Mask & (3UL << 62)) == 0, "SMT high bit protection expands physical core");
        }

        private static void SelectionBoundaries()
        {
            AntiCheatAffinitySnapshot laptop = Homogeneous(6, 0);
            for (ulong allowed = 1; allowed < 4096; allowed++)
            {
                AntiCheatAffinityChoice choice = AntiCheatAffinityTopology.Select(laptop, 0, allowed, 0, 0);
                Invariants(laptop, 0, allowed, 0, choice);
            }
            for (ulong reserved = 0; reserved < 4096; reserved++)
            {
                AntiCheatAffinityChoice choice = AntiCheatAffinityTopology.Select(laptop, 0, 4095, reserved, 0);
                Invariants(laptop, 0, 4095, reserved, choice);
            }
            Check(!AntiCheatAffinityTopology.Select(laptop, 0, 0x1000, 0, 0).Applicable, "allowed outside group rejected");
            Check(!AntiCheatAffinityTopology.Select(laptop, 0, 0xFFF, 0x1000, 0).Applicable, "foreign reservation rejected");
            Check(!AntiCheatAffinityTopology.Select(laptop, 0, 0xFFF, 0, 0x1000).Applicable, "foreign domain rejected");
            Check(!AntiCheatAffinityTopology.Select(laptop, 0, 0, 0, 0).Applicable, "zero affinity rejected");
            Check(!AntiCheatAffinityTopology.Select(null, 0, 1, 0, 0).Applicable, "missing topology rejected");
            laptop.CpuSetMetadata = false;
            Check(!Select(laptop, 0, 0).Applicable, "no allocation parking metadata rejected");
            Check(!Select(Shape(Core(0, 3, 0, 0), Core(0, 2, 0, 0)), 0, 0).Applicable, "overlap rejected");
        }

        private static byte[] Join(List<byte[]> items)
        {
            List<byte> result = new List<byte>();
            foreach (byte[] item in items) result.AddRange(item);
            return result.ToArray();
        }

        private static void U16(byte[] bytes, int offset, int value)
        { Array.Copy(BitConverter.GetBytes((ushort)value), 0, bytes, offset, 2); }
        private static void U32(byte[] bytes, int offset, uint value)
        { Array.Copy(BitConverter.GetBytes(value), 0, bytes, offset, 4); }
        private static void U64(byte[] bytes, int offset, ulong value)
        { Array.Copy(BitConverter.GetBytes(value), 0, bytes, offset, 8); }

        private static byte[] RawTopology(int groups, bool modernCache)
        {
            List<byte[]> records = new List<byte[]>();
            byte[] group = new byte[32 + groups * 48];
            U32(group, 0, 4); U32(group, 4, (uint)group.Length); U16(group, 8, groups); U16(group, 10, groups);
            for (int g = 0; g < groups; g++)
            {
                group[32 + g * 48] = 64; group[33 + g * 48] = 12;
                U64(group, 72 + g * 48, 4095);
            }
            records.Add(group);
            for (int g = 0; g < groups; g++)
            {
                for (int i = 0; i < 6; i++)
                {
                    byte[] core = new byte[48];
                    U32(core, 0, 0); U32(core, 4, 48); core[8] = 1; U16(core, 30, 1);
                    U64(core, 32, 3UL << (2 * i)); U16(core, 40, g);
                    records.Add(core);
                }
                byte[] cache = new byte[56];
                U32(cache, 0, 2); U32(cache, 4, 56); cache[8] = 3;
                U32(cache, 12, 32 * 1024 * 1024); U32(cache, 16, 0);
                U16(cache, 38, modernCache ? 1 : 0); U64(cache, 40, 4095); U16(cache, 48, g);
                records.Add(cache);
            }
            return Join(records);
        }

        private static byte[] RawStates(int groups)
        {
            byte[] states = new byte[groups * 12 * 32];
            for (int g = 0; g < groups; g++)
                for (int logical = 0; logical < 12; logical++)
                {
                    int at = (g * 12 + logical) * 32;
                    U32(states, at, 32); U32(states, at + 8, (uint)(g * 12 + logical));
                    U16(states, at + 12, g); states[at + 14] = (byte)logical;
                    states[at + 15] = (byte)(logical / 2);
                    states[at + 20] = (byte)(logical % 2 == 0 ? logical / 2 + 1 : 0);
                }
            return states;
        }

        private static byte[] Changed(byte[] original, int offset, byte value)
        { byte[] bytes = (byte[])original.Clone(); bytes[offset] = value; return bytes; }

        private static void ParserBoundaries()
        {
            byte[] topology = RawTopology(1, false), states = RawStates(1);
            AntiCheatAffinitySnapshot decoded = AntiCheatAffinityTopology.Decode(topology, states);
            Check(decoded.Valid && decoded.Cores.Length == 6 && decoded.Cores[0].L3Bytes == 33554432, "legacy cache records decoded");
            Check(decoded.Cores[0].Scheduling == 1 && decoded.Cores[5].Scheduling == 6, "SMT primary rank normalized per physical core");
            Check(Select(decoded, 0, 0).Mask == 15, "native metadata rank drives safe auxiliary choice");
            decoded = AntiCheatAffinityTopology.Decode(RawTopology(2, true), RawStates(2));
            Check(decoded.Valid && decoded.Cores.Length == 12 && decoded.Cores[6].Group == 1, "modern two processor groups decoded");
            Check(decoded.Cores[0].L3Id != decoded.Cores[6].L3Id, "group cache domains independent");
            decoded = AntiCheatAffinityTopology.Decode(topology, Changed(states, 19, 1));
            Check(decoded.Valid && decoded.Cores[0].Parked, "one parked SMT sibling excludes whole core");
            decoded = AntiCheatAffinityTopology.Decode(topology, Changed(states, 19, 2));
            Check(decoded.Valid && decoded.Cores[0].Allocated, "allocation excludes whole core");
            decoded = AntiCheatAffinityTopology.Decode(topology, Changed(states, 19, 8));
            Check(decoded.Valid && decoded.Cores[0].Allocated, "real time allocation excluded");
            Check(!AntiCheatAffinityTopology.Decode(null, states).Valid, "null topology rejected");
            Check(!AntiCheatAffinityTopology.Decode(topology, null).Valid, "missing state rejected");
            Check(!AntiCheatAffinityTopology.Decode(Changed(topology, 4, 0), states).Valid, "zero native record length rejected");
            Check(!AntiCheatAffinityTopology.Decode(Changed(topology, 10, 2), states).Valid, "group count contradiction rejected");
            Check(!AntiCheatAffinityTopology.Decode(Changed(topology, 33, 11), states).Valid, "active mask count contradiction rejected");
            Check(!AntiCheatAffinityTopology.Decode(Changed(topology, 80 + 30, 2), states).Valid, "cross group physical core rejected");
            Check(!AntiCheatAffinityTopology.Decode(Changed(topology, 80 + 48 + 32, 3), states).Valid, "overlapping native cores rejected");
            Check(!AntiCheatAffinityTopology.Decode(topology, Changed(states, 32 + 8, 0)).Valid, "duplicate CPU IDs rejected");
            Check(!AntiCheatAffinityTopology.Decode(topology, Changed(states, 14, 64)).Valid, "logical index overflow rejected");
            Check(!AntiCheatAffinityTopology.Decode(topology, Changed(states, 18, 1)).Valid, "efficiency mismatch rejected");
            Check(!AntiCheatAffinityTopology.Decode(topology, Changed(states, 32 + 15, 4)).Valid, "SMT core metadata mismatch rejected");
            Check(!AntiCheatAffinityTopology.Decode(topology, Changed(states, 2 * 32 + 15, 0)).Valid, "separate cores cannot share native core ID");
            byte[] truncated = new byte[states.Length - 1]; Array.Copy(states, truncated, truncated.Length);
            Check(!AntiCheatAffinityTopology.Decode(topology, truncated).Valid, "truncated state rejected");
            byte[] missing = new byte[states.Length - 32]; Array.Copy(states, missing, missing.Length);
            Check(!AntiCheatAffinityTopology.Decode(topology, missing).Valid, "missing logical CPU rejected");
            byte[] missingCore = new byte[topology.Length - 48];
            Array.Copy(topology, 0, missingCore, 0, 80); Array.Copy(topology, 128, missingCore, 80, topology.Length - 128);
            Check(!AntiCheatAffinityTopology.Decode(missingCore, states).Valid, "missing physical core rejected");
            List<byte[]> reversed = new List<byte[]>();
            reversed.Add(new byte[56]); Array.Copy(topology, topology.Length - 56, reversed[0], 0, 56);
            for (int i = 5; i >= 0; i--) { byte[] item = new byte[48]; Array.Copy(topology, 80 + i * 48, item, 0, 48); reversed.Add(item); }
            byte[] head = new byte[80]; Array.Copy(topology, head, 80); reversed.Add(head);
            decoded = AntiCheatAffinityTopology.Decode(Join(reversed), states);
            Check(decoded.Valid && decoded.Cores[0].Mask == 3 && decoded.Cores[5].Mask == 3072, "native record order canonicalized");
        }

        public static int Main(string[] args)
        {
            try
            {
                ProcessorShapes(); SelectionBoundaries(); ParserBoundaries();
                if (args.Length > 0 && args[0] == "--capture")
                {
                    AntiCheatAffinitySnapshot snapshot = AntiCheatAffinityTopology.Capture();
                    Console.WriteLine("READ_ONLY_NATIVE valid=" + snapshot.Valid + " reason=" + snapshot.Reason + " physical=" + snapshot.Cores.Length);
                    if (snapshot.Valid)
                    {
                        SortedDictionary<string, int> classes = new SortedDictionary<string, int>(StringComparer.Ordinal);
                        foreach (AntiCheatAffinityCore core in snapshot.Cores)
                        {
                            string key = "group=" + core.Group + " efficiency=" + core.Efficiency
                                + " parked=" + core.Parked + " allocated=" + core.Allocated;
                            int count; classes.TryGetValue(key, out count); classes[key] = count + 1;
                        }
                        foreach (KeyValuePair<string, int> pair in classes)
                            Console.WriteLine("READ_ONLY_CLASS " + pair.Key + " physical=" + pair.Value);
                        AntiCheatAffinityChoice choice = AntiCheatAffinityTopology.Select(snapshot, 0, All(snapshot, 0), 0, 0);
                        Console.WriteLine("READ_ONLY_SELECTION kind=" + choice.Kind + " mask=" + choice.Mask.ToString("X")
                            + " physical=" + choice.PhysicalCount + " reason=" + choice.Reason);
                    }
                }
                Console.WriteLine("PASS anti-cheat topology assertions=" + checks + " hardware_writes=0");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }
    }
}
