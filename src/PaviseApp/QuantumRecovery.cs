using System;
using System.Collections.Generic;
using System.Globalization;

namespace PaviseApp
{
    internal static class QuantumRecovery
    {
        private sealed class Layer
        {
            internal string Slot;

            internal string Stored;

            internal string Original;

            internal string Applied;

            internal PerformanceActionStore.Receipt Receipt;
        }

        internal const string RepairSlot = "PrevPrioritySep";

        internal const string BoostSlot = "PrevWin32PriSep";

        internal const string RepairedFlag = "PrioritySepRepaired";

        internal const string RetiredModeKey = "QuantumMode";

        internal static readonly string Target = PerformanceActionBackend.RegistryTarget("SYSTEM\\CurrentControlSet\\Control\\PriorityControl", "Win32PrioritySeparation");

        private static readonly object gate = new object();

        [ThreadStatic]
        private static string lastFailure;

        [ThreadStatic]
        private static bool lastWrote;

        internal static string LastFailure
        {
            get
            {
                return lastFailure;
            }
        }

        internal static bool LastWrote
        {
            get
            {
                return lastWrote;
            }
        }

        internal static bool HasLegacyRecords
        {
            get
            {
                if (Stored("PrevPrioritySep").Length <= 0)
                {
                    return Stored("PrevWin32PriSep").Length > 0;
                }
                return true;
            }
        }

        internal static bool LegacyRecordsForDisplay
        {
            get
            {
                try
                {
                    return HasLegacyRecords;
                }
                catch
                {
                    return true;
                }
            }
        }

        internal static bool HasRecords()
        {
            if (HasLegacyRecords)
            {
                return true;
            }
            foreach (PerformanceActionStore.Receipt item in PerformanceActionStore.All())
            {
                if (item.Id == "EnvQuantumProfileV1")
                {
                    return true;
                }
            }
            return false;
        }

        internal static bool Chained(PerformanceActionStore.Receipt receipt)
        {
            if (receipt != null && receipt.Id == "EnvQuantumProfileV1" && Canonical(receipt))
            {
                return true;
            }
            return false;
        }

        internal static bool RestoreAll()
        {
            return Unwind(null);
        }

        internal static bool RestoreThrough(PerformanceActionStore.Receipt receipt)
        {
            return Unwind(receipt);
        }

        private static bool Unwind(PerformanceActionStore.Receipt through)
        {
            lock (gate)
            {
                QuantumLiveUpdate.Begin();
                bool restored = UnwindCore(through);
                if (restored && lastWrote)
                    PerformanceTuning.SetState(PerformanceActionCatalog.Quantum, QuantumLiveUpdate.Complete(false, "restored"));
                return restored;
            }
        }

        private static bool UnwindCore(PerformanceActionStore.Receipt through)
        {
            lock (gate)
            {
                lastFailure = null;
                lastWrote = false;
                List<Layer> layers = new List<Layer>();
                bool result = true;
                bool wholeLedger = through == null;
                try
                {
                    foreach (PerformanceActionStore.Receipt all in PerformanceActionStore.All())
                    {
                        if (all.Id != "EnvQuantumProfileV1")
                        {
                            continue;
                        }
                        bool requestedSlot = through != null && SameSlot(all, through);
                        if (requestedSlot && PerformanceActionStore.Pack(all) != PerformanceActionStore.Pack(through))
                        {
                            return Fail("Time-slice recovery record changed; newer record retained");
                        }
                        if (!Canonical(all))
                        {
                            if ((through == null || requestedSlot) && !RestoreProfile(all))
                            {
                                result = false;
                            }
                            continue;
                        }
                        string normalizeResult = Normalize(all.Original);
                        string normalizeResult2 = Normalize(all.Expected);
                        if (normalizeResult == null || normalizeResult2 == null)
                        {
                            return Fail("Time-slice profile record has an unknown value; records retained");
                        }
                        layers.Add(new Layer
                        {
                            Receipt = all,
                            Original = normalizeResult,
                            Applied = normalizeResult2
                        });
                        wholeLedger = wholeLedger || requestedSlot;
                    }
                    foreach (string slot in new string[2] { "PrevPrioritySep", "PrevWin32PriSep" })
                    {
                        string storedResult = Stored(slot);
                        if (storedResult.Length != 0)
                        {
                            Layer layer = LegacyLayer(slot, storedResult);
                            if (layer == null)
                            {
                                return Fail("Legacy time-slice record is unreadable; records retained");
                            }
                            layers.Add(layer);
                        }
                    }
                }
                catch (Exception ex)
                {
                    return Fail("Time-slice recovery records could not be read | " + ex.Message);
                }
                if (layers.Count == 0 || !wholeLedger)
                {
                    return result;
                }
                string text;
                if (!ReadCurrent(out text))
                {
                    return Fail("Current time-slice value could not be read; records retained");
                }
                while (layers.Count > 0)
                {
                    Layer currentLayer = null;
                    foreach (Layer candidateLayer in layers)
                    {
                        if (candidateLayer.Applied != null && candidateLayer.Applied == candidateLayer.Original && candidateLayer.Applied == text)
                        {
                            currentLayer = candidateLayer;
                            break;
                        }
                    }
                    if (currentLayer != null)
                    {
                        if (!Release(currentLayer))
                        {
                            return false;
                        }
                        layers.Remove(currentLayer);
                        if (IsThrough(currentLayer, through))
                        {
                            return result;
                        }
                        continue;
                    }
                    Layer resolvedLayer = null;
                    int processedCount = 0;
                    foreach (Layer selectedLayer in layers)
                    {
                        if (selectedLayer.Applied != null && selectedLayer.Applied == text)
                        {
                            resolvedLayer = selectedLayer;
                            processedCount++;
                        }
                    }
                    if (processedCount > 1)
                    {
                        return Fail("Several time-slice records claim the current value; records retained");
                    }
                    if (resolvedLayer == null)
                    {
                        foreach (Layer workingLayer in new List<Layer>(layers))
                        {
                            if (workingLayer.Applied == null && workingLayer.Original == text)
                            {
                                if (!ClearLegacy(workingLayer))
                                {
                                    return false;
                                }
                                layers.Remove(workingLayer);
                            }
                        }
                        foreach (Layer pendingLayer in layers)
                        {
                            if (pendingLayer.Applied == null)
                            {
                                return Fail("Legacy time-slice record has no recorded value to compare; records retained");
                            }
                        }
                        foreach (Layer sourceLayer in new List<Layer>(layers))
                        {
                            if (through == null || IsThrough(sourceLayer, through))
                            {
                                if (!Release(sourceLayer))
                                {
                                    return false;
                                }
                                layers.Remove(sourceLayer);
                            }
                        }
                        return result;
                    }
                    if (!RestoreLayer(resolvedLayer, text))
                    {
                        return false;
                    }
                    layers.Remove(resolvedLayer);
                    if (IsThrough(resolvedLayer, through))
                    {
                        return result;
                    }
                    if (layers.Count == 0)
                    {
                        return result;
                    }
                    if (!ReadCurrent(out text))
                    {
                        return Fail("Current time-slice value could not be read; records retained");
                    }
                }
                return result;
            }
        }

        private static bool RestoreLayer(Layer layer, string current)
        {
            if (layer.Receipt != null)
            {
                return RestoreProfile(layer.Receipt);
            }
            if (current != layer.Original)
            {
                string text;
                if (!ReadCurrent(out text) || text != current)
                {
                    return Fail("Time-slice value changed before restore; records retained");
                }
                try
                {
                    if (Stored(layer.Slot) != layer.Stored)
                    {
                        return Fail("Legacy time-slice record changed before restore; record retained");
                    }
                }
                catch (Exception ex)
                {
                    return Fail("Legacy time-slice record could not be re-read | " + ex.Message);
                }
                lastWrote = true;
                bool writeSucceeded;
                try
                {
                    writeSucceeded = PerformanceActionBackend.Write("EnvQuantumProfileV1", Target, layer.Original);
                }
                catch
                {
                    writeSucceeded = false;
                }
                string currentText;
                if (!writeSucceeded || !ReadCurrent(out currentText) || currentText != layer.Original)
                {
                    return Fail("Legacy time-slice value could not be restored; record retained");
                }
            }
            return ClearLegacy(layer);
        }

        private static bool Release(Layer layer)
        {
            if (layer.Receipt == null)
            {
                return ClearLegacy(layer);
            }
            return RestoreProfile(layer.Receipt);
        }

        private static bool RestoreProfile(PerformanceActionStore.Receipt r)
        {
            bool restoreSucceeded;
            try
            {
                restoreSucceeded = PerformanceActionStore.Restore(r, () => PerformanceActionBackend.Read(r.Id, r.Target), delegate(string value)
                {
                    lastWrote = true;
                    return PerformanceActionBackend.Write(r.Id, r.Target, value);
                }, (string value) => value == r.Expected);
            }
            catch (Exception ex)
            {
                return Fail("Time-slice profile restore failed | " + ex.Message);
            }
            if (!restoreSucceeded)
            {
                return Fail("Time-slice profile restore incomplete; record retained");
            }
            return true;
        }

        private static bool ClearLegacy(Layer layer)
        {
            try
            {
                string storedResult = Stored(layer.Slot);
                if (storedResult.Length == 0)
                {
                    return true;
                }
                if (storedResult != layer.Stored)
                {
                    return Fail("Legacy time-slice record changed during recovery; record retained");
                }
                if (!Settings.SaveStr(layer.Slot, "") || Stored(layer.Slot).Length != 0)
                {
                    return Fail("Legacy time-slice record could not be cleared");
                }
            }
            catch (Exception ex)
            {
                return Fail("Legacy time-slice record could not be cleared | " + ex.Message);
            }
            if (layer.Slot == "PrevPrioritySep")
            {
                Settings.Save("PrioritySepRepaired", false);
                Settings.Remove("QuantumMode");
            }
            return true;
        }

        private static bool ReadCurrent(out string value)
        {
            try
            {
                value = Normalize(PerformanceActionBackend.Read("EnvQuantumProfileV1", Target));
            }
            catch
            {
                value = null;
            }
            return value != null;
        }

        internal static string Normalize(string value)
        {
            if (value == "absent")
            {
                return value;
            }
            int result;
            if (value == null || !value.StartsWith("d:", StringComparison.Ordinal) || !int.TryParse(value.Substring(2), NumberStyles.Integer, CultureInfo.InvariantCulture, out result))
            {
                return null;
            }
            return "d:" + result.ToString(CultureInfo.InvariantCulture);
        }

        private static Layer LegacyLayer(string slot, string stored)
        {
            int matchIndex = stored.LastIndexOf('\u001f');
            string originalValue = LegacyValue((matchIndex < 0) ? stored : stored.Substring(0, matchIndex));
            if (originalValue == null)
            {
                return null;
            }
            string applied = null;
            if (matchIndex >= 0 && (applied = LegacyValue(stored.Substring(matchIndex + 1))) == null)
            {
                return null;
            }
            Layer layer = new Layer();
            layer.Slot = slot;
            layer.Stored = stored;
            layer.Original = originalValue;
            layer.Applied = applied;
            return layer;
        }

        private static string LegacyValue(string repr)
        {
            if (repr == "__pavise_absent__")
            {
                return "absent";
            }
            if (string.IsNullOrEmpty(repr))
            {
                return null;
            }
            long result;
            if (!long.TryParse((repr[0] == '=') ? repr.Substring(1) : repr, NumberStyles.Integer, CultureInfo.InvariantCulture, out result))
            {
                return null;
            }
            return "d:" + ((int)result).ToString(CultureInfo.InvariantCulture);
        }

        private static string Stored(string slot)
        {
            string value;
            if (!Settings.TryLoadStr(slot, out value))
            {
                throw new InvalidOperationException("Unreadable legacy time-slice record");
            }
            return value ?? "";
        }

        private static bool Canonical(PerformanceActionStore.Receipt receipt)
        {
            return string.Equals(receipt.Target, Target, StringComparison.OrdinalIgnoreCase);
        }

        private static bool SameSlot(PerformanceActionStore.Receipt a, PerformanceActionStore.Receipt b)
        {
            if (a.Id == b.Id)
            {
                return string.Equals(a.Target, b.Target, StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }

        private static bool IsThrough(Layer layer, PerformanceActionStore.Receipt through)
        {
            if (through != null && layer.Receipt != null)
            {
                return SameSlot(layer.Receipt, through);
            }
            return false;
        }

        private static bool Fail(string reason)
        {
            lastFailure = reason;
            Logger.Warn("时间片恢复 | " + reason);
            return false;
        }
    }
}
