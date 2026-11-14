using System;
using System.Collections.Generic;
using System.Text;

namespace PaviseApp
{
    // 由线程控制会话的串行操作调用；聚合只影响诊断，不参与字段资格、写入和恢复判断。
    internal sealed class GameThreadDiagnosticLog
    {
        private sealed class Field
        {
            internal string Text;
            internal long Count;
        }

        private sealed class Category
        {
            internal readonly Dictionary<string, Field> Fields = new Dictionary<string, Field>(StringComparer.Ordinal);
            internal long Next, LastRecord, Count;
            internal bool Published;
        }

        private readonly Action<string> emit;
        private readonly Dictionary<string, Category> categories = new Dictionary<string, Category>(StringComparer.Ordinal);

        internal GameThreadDiagnosticLog(Action<string> emit) { this.emit = emit; }

        internal void Record(string category, string key, string text, long now)
        {
            try
            {
                if (emit == null || string.IsNullOrEmpty(category) || string.IsNullOrEmpty(key)) return;
                Category group;
                if (!categories.TryGetValue(category, out group)) categories.Add(category, group = new Category());
                Field field;
                if (!group.Fields.TryGetValue(key, out field)) group.Fields.Add(key, field = new Field());
                field.Text = text ?? "";
                if (field.Count < long.MaxValue) field.Count++;
                if (group.Count < long.MaxValue) group.Count++;
                group.LastRecord = now;
            }
            catch { }
        }

        internal void Flush(long now, bool final = false)
        {
            if (emit == null) return;
            try
            {
                // 先快照分类，输出回调即使间接记录其他事件，也不破坏本轮枚举。
                foreach (KeyValuePair<string, Category> item in new List<KeyValuePair<string, Category>>(categories))
                {
                    Category group = item.Value;
                    if (group.Count == 0 || !final && group.Published && now < group.Next) continue;
                    var message = new StringBuilder();
                    message.Append("诊断区间汇总：类别=").Append(item.Key).Append("；涉及字段=").Append(group.Fields.Count)
                        .Append("；记录次数=").Append(group.Count).Append("；最近记录距今毫秒=").Append(Math.Max(0, now - group.LastRecord))
                        .Append("；字段最近记录=[");
                    bool separator = false;
                    foreach (KeyValuePair<string, Field> field in group.Fields)
                    {
                        if (separator) message.Append(" | ");
                        separator = true;
                        message.Append("字段记录=").Append(field.Key).Append("；").Append(field.Value.Text)
                            .Append("（本段次数=").Append(field.Value.Count).Append('）');
                    }
                    message.Append(']');
                    // 区间明细清空后不生成空心跳；类别窗口继续保留，状态切换不会重填日志额度。
                    group.Fields.Clear(); group.Count = 0; group.Published = true;
                    group.Next = now > long.MaxValue - 30000 ? long.MaxValue : now + 30000;
                    try { emit(message.ToString()); }
                    catch { }
                }
            }
            catch { }
        }
    }
}
