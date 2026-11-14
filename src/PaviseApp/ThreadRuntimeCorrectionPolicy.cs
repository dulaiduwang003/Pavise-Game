using System;
using System.Collections.Generic;

namespace PaviseApp
{
    // 仅调度已接管属性的运行期维护，不授予首次写入、换角色或恢复的重试权限。
    internal sealed class ThreadRuntimeCorrectionPolicy
    {
        internal const long NormalInterval = 2L * TimeSpan.TicksPerSecond;
        internal const long MaximumDelay = 30L * TimeSpan.TicksPerSecond;
        private const int MaximumProperties = 512;
        private sealed class State
        {
            internal bool Running;
            internal int Failures;
            internal long Next;
        }
        private readonly object gate = new object();
        private readonly Dictionary<string, State> states = new Dictionary<string, State>(StringComparer.Ordinal);

        internal bool TryBegin(string property, long now)
        {
            if (string.IsNullOrEmpty(property) || now <= 0) return false;
            lock (gate)
            {
                State state;
                if (!states.TryGetValue(property, out state))
                {
                    if (states.Count >= MaximumProperties) return false;
                    states.Add(property, state = new State());
                }
                if (state.Running || now < state.Next) return false;
                state.Running = true;
                return true;
            }
        }

        internal void Complete(string property, long now, bool verified)
        {
            lock (gate)
            {
                State state;
                if (!states.TryGetValue(property, out state) || !state.Running) return;
                state.Running = false;
                if (verified) state.Failures = 0;
                else if (state.Failures < 5) state.Failures++;
                long delay = verified ? NormalInterval : Math.Min(MaximumDelay, NormalInterval << Math.Max(0, state.Failures - 1));
                state.Next = now > long.MaxValue - delay ? long.MaxValue : now + delay;
            }
        }

        internal long NextDue(string property)
        {
            lock (gate)
            {
                State state;
                return states.TryGetValue(property, out state) ? state.Next : 0;
            }
        }

        // 只供已证明退出且归属已结清的身份使用；删除存活属性会重填退避并掩盖原维护状态。
        internal bool ForgetExited(string property)
        {
            lock (gate)
            {
                State state;
                if (!states.TryGetValue(property, out state)) return true;
                if (state.Running) return false;
                return states.Remove(property);
            }
        }
    }
}
