// @author bdth 2074055628@qq.com
// 文件用途 隔离 Pavise 自身系统写入与游戏 IRQ 观测 epoch
using System;

namespace PaviseApp
{
    internal static class IrqMutationBoundary
    {
        private static readonly object gate = new object();
        private static Action mutationBegin;
        private static Action mutationEnd;

        public static void Configure(Action begin, Action end)
        {
            lock (gate)
            {
                mutationBegin = begin;
                mutationEnd = end;
            }
        }

        public static void Run(Action action)
        {
            Action begin;
            Action end;
            lock (gate)
            {
                begin = mutationBegin;
                end = mutationEnd;
            }
            if (begin != null) try { begin(); } catch { }
            try { if (action != null) action(); }
            finally { if (end != null) try { end(); } catch { } }
        }

        public static T Run<T>(Func<T> action)
        {
            Action begin;
            Action end;
            lock (gate)
            {
                begin = mutationBegin;
                end = mutationEnd;
            }
            if (begin != null) try { begin(); } catch { }
            try { return action != null ? action() : default(T); }
            finally { if (end != null) try { end(); } catch { } }
        }
    }
}
