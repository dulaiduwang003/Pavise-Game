using System;
using System.Text;

namespace PaviseApp
{
    internal interface IGraphicsPreferenceStore
    {
        string ReadValue();
        bool WriteValue(string value);
        string LoadReceipt();
        bool SaveReceipt(string value);
    }

    // 持久化的原值与目标值只持有本次写入；null 表示不存在，不表示系统默认值。
    internal sealed class GraphicsPreferenceTransaction
    {
        private readonly IGraphicsPreferenceStore store;
        private readonly object gate = new object();
        internal GraphicsPreferenceTransaction(IGraphicsPreferenceStore store) { this.store = store; }

        internal static string Receipt(string original, string applied, char phase)
        { return "G2|" + Pack(original) + "|" + Pack(applied) + "|" + phase; }

        private static string Transition(string original, string previous, string target)
        { return "G3|" + Pack(original) + "|" + Pack(previous) + "|" + Pack(target) + "|P"; }

        private static string Pack(string value)
        { return value == null ? "-" : Convert.ToBase64String(Encoding.UTF8.GetBytes(value)); }

        private static string Unpack(string value)
        { return value == "-" ? null : Encoding.UTF8.GetString(Convert.FromBase64String(value)); }

        private static bool Parse(string receipt, out string original, out string applied, out string previous, out char phase)
        {
            original = applied = previous = null; phase = '\0';
            try
            {
                string[] parts = receipt.Split('|');
                bool transition = parts.Length == 5 && parts[0] == "G3";
                if (!transition && (parts.Length != 4 || parts[0] != "G2")) return false;
                int phaseIndex = transition ? 4 : 3;
                if (parts[phaseIndex].Length != 1) return false;
                phase = parts[phaseIndex][0];
                if (phase != 'P' && phase != 'A' && phase != 'R' && phase != 'F') return false;
                original = Unpack(parts[1]);
                applied = Unpack(parts[transition ? 3 : 2]);
                previous = transition ? Unpack(parts[2]) : applied;
                return true;
            }
            catch { return false; }
        }

        private bool Save(string value)
        { return store.SaveReceipt(value) && store.LoadReceipt() == value; }

        private bool Finish(string original, string applied)
        {
            // 已经恢复或释放后，不能仅因后续值再次匹配旧目标而重新写入。
            return Save(Receipt(original, applied, 'F')) && Save("");
        }

        internal bool Set(string target)
        {
            lock (gate)
            {
                try
                {
                    string receipt = store.LoadReceipt(), original = null, applied = null, previous = null; char phase = '\0';
                    bool owned = receipt.Length != 0;
                    if (owned && !Parse(receipt, out original, out applied, out previous, out phase)) return false;
                    if (owned && phase == 'F')
                    {
                        if (!Save("")) return false;
                        owned = false;
                    }
                    string current = store.ReadValue();
                    if (owned && current != applied && current != previous && current != original)
                    {
                        // 外部改动后可以接受新的显式请求，但不能用过时基准覆盖外部值。
                        if (!Finish(original, applied)) return false;
                        owned = false;
                    }
                    if (!owned) original = current;
                    if (current == target)
                    {
                        if (!owned) return true;
                        return current == original ? Finish(original, applied) : Save(Receipt(original, target, 'A'));
                    }
                    if (owned && target == original) return Restore();
                    string prepared = owned && current != original ? Transition(original, current, target) : Receipt(original, target, 'P');
                    if (!Save(prepared)) return false;
                    if (store.ReadValue() != current)
                    {
                        // 本次操作尚未尝试写入，保留并发写入方的值。
                        Finish(original, target); return false;
                    }
                    if (!store.WriteValue(target) || store.ReadValue() != target) return false;
                    return Save(Receipt(original, target, 'A'));
                }
                catch { return false; }
            }
        }

        internal bool Restore()
        {
            lock (gate)
            {
                try
                {
                    string receipt = store.LoadReceipt();
                    if (receipt.Length == 0) return true;
                    string original, applied, previous; char phase;
                    if (!Parse(receipt, out original, out applied, out previous, out phase)) return false;
                    if (phase == 'F') return Save("");
                    string current = store.ReadValue();
                    if (current == original || (current != applied && current != previous)) return Finish(original, applied);
                    string restoring = current;
                    if (!Save(Receipt(original, restoring, 'R'))) return false;
                    current = store.ReadValue();
                    if (current != restoring) return Finish(original, restoring);
                    if (!store.WriteValue(original) || store.ReadValue() != original) return false;
                    return Finish(original, restoring);
                }
                catch { return false; }
            }
        }
    }
}
