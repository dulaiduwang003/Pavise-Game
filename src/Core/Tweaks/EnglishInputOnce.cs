// 文件用途 每次游戏运行期只给一次机会 不是每次前台事件都给 这个状态机
// 没有定时器也没有工作线程 做出终态判断之后就不再看输入
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace PaviseApp
{
    internal sealed class EnglishInputOnce
    {
        internal const int ForegroundWaitMs = 15000;
        private const int MaximumHistory = 128;
        private readonly object stateGate = new object(), operationGate = new object();
        private readonly IGameInputLanguage input;
        private readonly Func<long> clock;
        private readonly List<Session> history = new List<Session>();
        private Session current;
        private int cancellationVersion;
        private bool historyExhausted;

        private sealed class Session
        {
            internal string ProfileId;
            internal long Deadline;
            internal bool Consumed, Revoked, HistoryFull;
            internal Func<bool> OriginalAdmission;
            internal readonly List<GameInputProcess> Processes = new List<GameInputProcess>();
        }

        internal EnglishInputOnce(IGameInputLanguage value) : this(value, MonotonicMilliseconds) { }
        internal EnglishInputOnce(IGameInputLanguage value, Func<long> milliseconds)
        {
            if (value == null || milliseconds == null) throw new ArgumentNullException();
            input = value; clock = milliseconds;
        }

        private static long MonotonicMilliseconds()
        { return (long)(Stopwatch.GetTimestamp() * (1000.0 / Stopwatch.Frequency)); }
        private static bool SameProfile(string first, string second)
        { return !string.IsNullOrEmpty(first) && string.Equals(first, second, StringComparison.OrdinalIgnoreCase); }
        private static void Remember(Session session, GameInputProcess process)
        { if (process.IsValid && !session.Processes.Contains(process)) session.Processes.Add(process); }
        private static bool Evaluate(Func<bool> admission)
        {
            try { return admission != null && admission(); }
            catch { return false; }
        }

        internal void Begin(string profileId, GameInputProcess process, bool enabledAtEntry, Func<bool> admission)
        {
            long deadline = clock() + ForegroundWaitMs;
            // Begin 和 Step 在 GameMode.sync 之外跑 Cancel 和 End 只拿
            // 那把短状态锁 锁里不做任何原生调用和回调
            lock (operationGate)
            {
                Session[] previous;
                int beginVersion;
                lock (stateGate)
                {
                    beginVersion = cancellationVersion;
                    if (current != null && SameProfile(current.ProfileId, profileId))
                    { Remember(current, process); return; }
                    if (current != null) { current.Consumed = true; current.Revoked = true; }
                    current = null;
                    previous = history.ToArray();
                }
                Session reuse = null;
                var gone = new List<Session>();
                foreach (Session old in previous)
                {
                    // 找到可复用会话后 其余会话的探测只服务于剪枝 推迟到下次
                    //   Begin 再清 仍要按精确身份换选 那是纯比较 不走内核
                    if (reuse != null)
                    {
                        foreach (GameInputProcess identity in old.Processes)
                            if (identity.Equals(process)) { reuse = old; break; }
                        continue;
                    }
                    bool liveOrUnknown = false;
                    foreach (GameInputProcess identity in old.Processes)
                    {
                        if (identity.Equals(process)) { reuse = old; liveOrUnknown = true; break; }
                        // 判活只需要一个非 Gone 结果 之后只找身份 不再探测
                        if (liveOrUnknown) continue;
                        GameInputProcessState state;
                        try { state = input.Probe(identity); }
                        catch { state = GameInputProcessState.Unknown; }
                        if (state != GameInputProcessState.Gone) liveOrUnknown = true;
                    }
                    // 旧渲染进程访问不到 不能证明这是一次全新的运行
                    // 绝不要给启动器或家族进程的 PID 起别名 它们可能比游戏活得久
                    if (!liveOrUnknown) gone.Add(old);
                    else if (reuse == null && SameProfile(old.ProfileId, profileId)) reuse = old;
                }
                bool permitted = enabledAtEntry && Evaluate(admission);
                lock (stateGate)
                {
                    permitted = permitted && beginVersion == cancellationVersion;
                    foreach (Session old in gone) history.Remove(old);
                    if (reuse != null)
                    {
                        reuse.ProfileId = profileId;
                        reuse.Consumed = true; reuse.Revoked = true;
                        Remember(reuse, process); current = reuse;
                        return;
                    }
                    // 一旦某个身份保不住了 后面不要因为另一条历史条目过期
                    // 就把这次跳过忘掉并重新武装
                    if (history.Count >= MaximumHistory) historyExhausted = true;
                    Session fresh = new Session {
                        ProfileId = profileId, Deadline = deadline, Consumed = !permitted,
                        Revoked = !permitted, OriginalAdmission = admission,
                        HistoryFull = historyExhausted
                    };
                    Remember(fresh, process);
                    if (!fresh.HistoryFull) history.Add(fresh);
                    current = fresh;
                }
            }
        }

        private bool Allowed(Session session, Func<bool> admission)
        {
            bool stateAllows;
            lock (stateGate) stateAllows = ReferenceEquals(current, session) && !session.Revoked;
            // 持有 stateGate 时绝不回调 Invalidate 可能持有 sync
            return stateAllows && clock() <= session.Deadline
                && Evaluate(session.OriginalAdmission) && Evaluate(admission);
        }

        internal EnglishInputOutcome Step(string profileId, GameInputProcess process, Func<bool> admission)
        {
            if (!Monitor.TryEnter(operationGate)) return null; // No queued catch-up.
            try
            {
                Session session;
                lock (stateGate)
                {
                    session = current;
                    if (session == null || !SameProfile(session.ProfileId, profileId)) return null;
                    Remember(session, process); // Record renderer handoffs even after the one attempt.
                    if (session.Consumed) return null;
                }
                EnglishInputOutcome result;
                if (session.HistoryFull) result = new EnglishInputOutcome(EnglishInputResult.HistoryFull);
                else if (clock() > session.Deadline) result = new EnglishInputOutcome(EnglishInputResult.Expired);
                else if (!process.IsValid) return null; // Await a verified renderer, with the original deadline.
                else if (!Allowed(session, admission)) result = new EnglishInputOutcome(EnglishInputResult.Canceled);
                else
                {
                    Func<bool> allowed = delegate { return Allowed(session, admission); };
                    Func<bool> claim = delegate
                    {
                        if (!allowed()) return false;
                        lock (stateGate)
                        {
                            if (!ReferenceEquals(current, session) || session.Revoked || session.Consumed) return false;
                            session.Consumed = true;
                            return true;
                        }
                    };
                    try { result = input.TryOnce(process, claim, allowed); }
                    catch { result = new EnglishInputOutcome(EnglishInputResult.Unavailable); }
                    if (result == null) result = new EnglishInputOutcome(EnglishInputResult.Unavailable);
                }
                if (result.Result == EnglishInputResult.WaitingForGameWindow)
                {
                    if (Allowed(session, admission)) return null;
                    result = new EnglishInputOutcome(clock() > session.Deadline
                        ? EnglishInputResult.Expired : EnglishInputResult.Canceled);
                }
                lock (stateGate) session.Consumed = true;
                return result;
            }
            finally { Monitor.Exit(operationGate); }
        }

        internal void Cancel()
        {
            lock (stateGate)
            {
                cancellationVersion++;
                if (current != null) { current.Revoked = true; current.Consumed = true; }
            }
        }

        internal void End()
        {
            lock (stateGate)
            {
                cancellationVersion++;
                if (current != null) { current.Revoked = true; current.Consumed = true; }
                current = null; // Retain deduplication while an earlier runtime may still be alive.
            }
        }

        internal bool Drain(int timeoutMs)
        {
            if (Monitor.IsEntered(operationGate)) return false;
            if (!Monitor.TryEnter(operationGate, Math.Max(0, timeoutMs))) return false;
            Monitor.Exit(operationGate);
            return true;
        }
    }
}
