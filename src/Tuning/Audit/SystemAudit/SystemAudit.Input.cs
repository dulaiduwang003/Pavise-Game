using System.Collections.Generic;

namespace PaviseApp
{
    internal static partial class SystemAudit
    {

        internal static string InputSummary(List<InputDevice> devices, bool mouse)
        {
            if (devices == null)
            {
                return null;
            }
            bool hasModernTransport = false;
            foreach (InputDevice device in devices)
            {
                if (device.IsMouse == mouse && device.Transport != InputTransport.Virtual && device.Transport != InputTransport.Ps2)
                {
                    hasModernTransport = true;
                }
            }
            List<string> list = new List<string>();
            foreach (InputDevice device2 in devices)
            {
                if (device2.IsMouse == mouse && device2.Transport != InputTransport.Virtual && (device2.Transport != InputTransport.Ps2 || !hasModernTransport))
                {
                    string item = InputChainProbe.TransportText(device2.Transport);
                    if (!list.Contains(item))
                    {
                        list.Add(item);
                    }
                }
            }
            if (list.Count != 0)
            {
                return string.Join(Lang.T("t.systemaudit.101"), list.ToArray());
            }
            return null;
        }

        private static void BuildInputChain(AuditReport report, Facts facts)
        {
            bool hasBluetooth = false;
            bool requiresBluetooth = false;
            try
            {
                hasBluetooth = InputChainProbe.AnyBluetooth(facts.Inputs);
                requiresBluetooth = InputChainProbe.BluetoothIsOnlyOption(facts.Inputs, true) || InputChainProbe.BluetoothIsOnlyOption(facts.Inputs, false);
            }
            catch
            {
            }
            string inputSummaryResult = InputSummary(facts.Inputs, true);
            string inputSummaryResult2 = InputSummary(facts.Inputs, false);
            if (inputSummaryResult != null || inputSummaryResult2 != null)
            {
                report.Machine.Add(new AuditRow
                {
                    Name = Lang.T("audit.input.devices"),
                    Value = ((inputSummaryResult == null) ? "" : (Lang.T("t.systemaudit.102") + inputSummaryResult)) + ((inputSummaryResult != null && inputSummaryResult2 != null) ? "   " : "") + ((inputSummaryResult2 == null) ? "" : (Lang.T("t.systemaudit.103") + inputSummaryResult2)),
                    Note = (requiresBluetooth ? Lang.T("t.systemaudit.104") : (hasBluetooth ? Lang.T("t.systemaudit.105") : Lang.T("t.systemaudit.106"))),
                    Evidence = (hasBluetooth ? "台架实测" : "本机实测"),
                    Warn = requiresBluetooth
                });
            }
            if (facts.Access != null && facts.Access.Read)
            {
                bool filterKeysSwallowing = facts.Access.FilterKeysSwallowing;
                bool anyNeedsFix = facts.Access.AnyNeedsFix;
                report.Machine.Add(new AuditRow
                {
                    Name = Lang.T("t.legacypurge.14"),
                    Value = (filterKeysSwallowing ? Lang.T("t.systemaudit.107") : (anyNeedsFix ? Lang.T("t.systemaudit.108") : Lang.T("t.systemaudit.1"))),
                    Note = (filterKeysSwallowing ? (Lang.T("t.systemaudit.109") + facts.Access.DelayBeforeAcceptanceMs + Lang.T("t.systemaudit.110")) : (anyNeedsFix ? Lang.T("t.systemaudit.111") : Lang.T("t.systemaudit.112"))),
                    Evidence = "机制明确",
                    Warn = anyNeedsFix
                });
            }
            if (facts.ThreadDpc.HasValue)
            {
                report.Machine.Add(new AuditRow
                {
                    Name = Lang.T("t.systemaudit.121"),
                    Value = Lang.T("t.systemaudit.120") + facts.ThreadDpc.Value,
                    Note = Lang.T("t.systemaudit.124"),
                    Evidence = "机制明确",
                    Warn = true
                });
            }
        }

    }
}
