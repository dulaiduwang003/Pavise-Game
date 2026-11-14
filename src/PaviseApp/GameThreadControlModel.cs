using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal struct GameThreadControlProcessor : IEquatable<GameThreadControlProcessor>
    {
        internal ushort Group;
        internal byte Number;
        internal GameThreadControlProcessor(ushort group, byte number) { Group = group; Number = number; }
        public bool Equals(GameThreadControlProcessor other) { return Group == other.Group && Number == other.Number; }
        public override bool Equals(object value) { return value is GameThreadControlProcessor && Equals((GameThreadControlProcessor)value); }
        public override int GetHashCode() { return Group * 257 + Number; }
        public override string ToString() { return Group + ":" + Number; }
    }

    internal sealed class GameThreadControlRole
    {
        internal readonly HardThreadIdentity Thread;
        internal readonly bool Priority;
        internal readonly int PriorityLimit;
        internal readonly GameThreadControlProcessor? Ideal;
        internal readonly string Name;
        internal GameThreadControlRole(HardThreadIdentity thread, bool priority, GameThreadControlProcessor? ideal, string name, int priorityLimit = 2)
        {
            if (thread == null || thread.Tid <= 0 || thread.Creation <= 0 || (!priority && !ideal.HasValue))
                throw new ArgumentException("统一线程角色无有效字段");
            if (!GameThreadControlPolicy.ValidPriorityLimit(priorityLimit)) throw new ArgumentOutOfRangeException("priorityLimit");
            Thread = thread; Priority = priority; PriorityLimit = priorityLimit; Ideal = ideal; Name = name ?? "关键角色";
        }
    }

    internal sealed class GameThreadControlProcess
    {
        internal HardThreadRead Read;
        internal uint PriorityClass;
        internal bool MultipleGroups;
    }

    internal sealed class GameThreadControlObserved
    {
        internal HardThreadRead Read;
        internal bool PriorityKnown, IdealKnown, PlacementKnown;
        internal int Priority;
        internal GameThreadControlProcessor Ideal;
        internal ushort Group;
        internal ulong Allowed;
    }

    internal interface IGameThreadPriorityReader
    {
        GameThreadControlObserved ReadPriority(HardThreadIdentity thread);
    }

    internal interface IGameThreadControlNative : IDisposable
    {
        bool TargetExited { get; }
        GameThreadControlProcess ReadProcess();
        GameThreadControlObserved Read(HardThreadIdentity thread);
        bool SetPriority(HardThreadIdentity thread, int value);
        bool SetIdeal(HardThreadIdentity thread, GameThreadControlProcessor value, out GameThreadControlProcessor previous);
    }

    internal interface IGameThreadControlAuthority
    {
        IDisposable EnterUse();
        void RequireForward();
        void RequireRecovery();
    }

    // 时间关键权限来自原游戏规则，运行期还须证明同一角色仍处于已核验的单核计划。
    internal interface IGameThreadTimeCriticalAuthority
    {
        bool IsPlacementVerified(HardThreadIdentity thread);
    }

    internal enum GameThreadControlPhase { Observing, Intent, Written, Verified, Restoring, Restored, Gone }

    internal sealed class GameThreadControlField
    {
        internal bool Managed, HasWritten, AppliedKnown, PreviousKnown, InitialAdmitted, Terminal;
        internal int Original, Desired, Applied, Observed, Previous, Written;
        internal bool ObservedKnown;
        internal int AdmissionFailures, RestoreAttempts;
        internal GameThreadControlPhase Phase;
        internal int Failures;
        internal long NextAttempt;
        internal int RestoreValue;
        internal bool RestoreValueKnown;
        internal long LastCheckedMilliseconds, LastObservedMilliseconds, LastVerifiedMilliseconds, RuntimeAttempts, Corrections;
        internal uint DesiredProcessClass;
        internal bool PolicyVerified;
        internal string Detail = "";
        internal GameThreadControlField Clone() { return (GameThreadControlField)MemberwiseClone(); }
    }

    internal sealed class GameThreadControlRow
    {
        internal HardThreadIdentity Thread;
        internal string Name;
        internal int PriorityLimit = 2;
        internal GameThreadControlField Priority = new GameThreadControlField();
        internal GameThreadControlField Ideal = new GameThreadControlField();
        internal GameThreadControlRow Clone()
        {
            return new GameThreadControlRow { Thread = Thread, Name = Name, PriorityLimit = PriorityLimit, Priority = Priority.Clone(), Ideal = Ideal.Clone() };
        }
    }

    internal sealed class GameThreadControlJournal
    {
        internal HardThreadTarget Target;
        internal string Nonce, ScopeHash;
        internal string Rule = "";
        internal long Revision;
        internal bool Stopping;
        internal int RecoveryPasses, ExitRecoveryPasses;
        internal uint OriginalProcessClass, ObservedProcessClass;
        internal long ProcessObservedMilliseconds;
        internal bool ProcessObservedKnown;
        internal readonly List<GameThreadControlRow> Rows = new List<GameThreadControlRow>();
        internal GameThreadControlJournal Clone()
        {
            var copy = new GameThreadControlJournal { Target = Target, Nonce = Nonce, ScopeHash = ScopeHash, Revision = Revision, Stopping = Stopping,
                RecoveryPasses = RecoveryPasses, ExitRecoveryPasses = ExitRecoveryPasses, Rule = Rule };
            copy.OriginalProcessClass = OriginalProcessClass; copy.ObservedProcessClass = ObservedProcessClass;
            copy.ProcessObservedMilliseconds = ProcessObservedMilliseconds; copy.ProcessObservedKnown = ProcessObservedKnown;
            foreach (GameThreadControlRow row in Rows) copy.Rows.Add(row.Clone());
            return copy;
        }
    }

    internal interface IGameThreadControlStore : IDisposable
    {
        GameThreadControlJournal Load();
        void Save(long expectedRevision, GameThreadControlJournal journal);
    }

    internal static class GameThreadControlPolicy
    {
        internal static bool ValidPriorityLimit(int value) { return value == 1 || value == 2 || value == 15; }

        internal static bool TimeCriticalRule(string text)
        {
            CriticalThreadRule rule = CriticalThreadRule.ParseForRuntime(text);
            return rule != null && rule.IsTimeCriticalRender && rule.Encode() == text;
        }

        internal static bool PriorityTarget(uint processClass, int original, out int desired)
        { return PriorityTarget(processClass, original, 2, out desired); }

        internal static bool PriorityTarget(uint processClass, int original, int priorityLimit, out int desired)
        {
            desired = original;
            if (original < -2 || original > 2 || !ValidPriorityLimit(priorityLimit)) return false;
            int target;
            switch (processClass)
            {
                case 0x40: case 0x4000: case 0x20: target = 2; break;
                case 0x8000: target = 1; break;
                // High 的普通相对等级仍处于非实时范围；显式15沿用原有独立准入边界。
                case 0x80: if (priorityLimit == 15) return true; target = 2; break;
                // Realtime 不新增相对提升；已有本方增量只按归属撤回。
                case 0x100: return true;
                default: return false;
            }
            desired = priorityLimit == 15 ? 15 : Math.Max(original, Math.Min(target, priorityLimit));
            return true;
        }

        internal static bool InitiallyManaged(uint processClass, int original, int priorityLimit = 2)
        { return processClass != 0x100 && (processClass != 0x80 || priorityLimit != 15)
            && original >= -2 && original <= 2 && ValidPriorityLimit(priorityLimit); }

        internal static int Encode(GameThreadControlProcessor value) { return (value.Group << 8) | value.Number; }
        internal static GameThreadControlProcessor Decode(int value)
        { return new GameThreadControlProcessor((ushort)(value >> 8), (byte)value); }

        internal static int Backoff(int failures)
        { return failures <= 1 ? 2000 : failures == 2 ? 4000 : failures == 3 ? 8000 : failures == 4 ? 16000 : 30000; }

        internal static bool Pending(GameThreadControlJournal journal)
        {
            if (journal == null) return false;
            foreach (GameThreadControlRow row in journal.Rows)
                if (Pending(row.Priority) || Pending(row.Ideal)) return true;
            return false;
        }
        internal static bool Pending(GameThreadControlField field)
        { return field.Managed && !field.Terminal && field.HasWritten; }
    }
}
