using System.Collections.Generic;

namespace PaviseApp
{
    internal static class ThreadBoostSelection
    {
        internal const string Key = "GmThreadBoostStrategyV1";

        internal const string Off = "off";

        internal const string On = "on";

        internal const string Candidate = "candidate";

        internal const string Frame = "frame";

        internal static readonly string[] Choices = new string[2] { "off", "on" };

        internal static string RawGlobal
        {
            get
            {
                string selected;
                if (!TryReadGlobal(out selected))
                {
                    return "off";
                }
                return selected;
            }
        }

        internal static bool IsLegacy(string key)
        {
            if (key != "GmRenderLane")
            {
                return key == "FrameCoordination";
            }
            return true;
        }

        internal static bool Valid(string value)
        {
            switch (value)
            {
            default:
                return value == "frame";
            case "off":
            case "on":
            case "candidate":
                return true;
            }
        }

        internal static string Normalize(string value)
        {
            switch (value)
            {
            default:
                return "off";
            case "on":
            case "candidate":
            case "frame":
                return "on";
            }
        }

        internal static bool IsOn(string value)
        {
            return Normalize(value) == "on";
        }

        internal static bool TryReadGlobal(out string selected)
        {
            selected = "off";
            string text;
            if (!Settings.TryLoadStr("GmThreadBoostStrategyV1", out text))
            {
                return false;
            }
            if (!string.IsNullOrEmpty(text))
            {
                if (!Valid(text))
                {
                    return false;
                }
                selected = Normalize(text);
                return true;
            }
            bool renderLaneEnabled;
            bool frameCoordinationEnabled;
            if (!TryLegacyFlag("GmRenderLane", true, out renderLaneEnabled) || !TryLegacyFlag("FrameCoordination", false, out frameCoordinationEnabled))
            {
                return false;
            }
            selected = LegacyValue(renderLaneEnabled, frameCoordinationEnabled);
            return true;
        }

        private static bool TryLegacyFlag(string key, bool fallback, out bool value)
        {
            return Settings.TryLoadBool(key, fallback, out value);
        }

        internal static string LegacyValue(bool candidate, bool frame)
        {
            if (!candidate && !frame)
            {
                return "off";
            }
            return "on";
        }

        internal static bool Save(string value)
        {
            if (Valid(value))
            {
                return Settings.SaveStr("GmThreadBoostStrategyV1", Normalize(value));
            }
            return false;
        }

        internal static bool MigrateOverrides(IDictionary<string, string> values)
        {
            string renderLaneOverride;
            bool hasRenderLaneOverride = values.TryGetValue("GmRenderLane", out renderLaneOverride);
            string frameCoordinationOverride;
            bool hasFrameCoordinationOverride = values.TryGetValue("FrameCoordination", out frameCoordinationOverride);
            if (!hasRenderLaneOverride && !hasFrameCoordinationOverride)
            {
                return true;
            }
            string currentStrategy;
            if (!values.TryGetValue("GmThreadBoostStrategyV1", out currentStrategy))
            {
                bool renderLaneEnabled = renderLaneOverride == "1";
                bool frameCoordinationEnabled = frameCoordinationOverride == "1";
                if (!hasRenderLaneOverride && !TryLegacyFlag("GmRenderLane", true, out renderLaneEnabled))
                {
                    return false;
                }
                if (!hasFrameCoordinationOverride && !TryLegacyFlag("FrameCoordination", false, out frameCoordinationEnabled))
                {
                    return false;
                }
                values["GmThreadBoostStrategyV1"] = LegacyValue(renderLaneEnabled, frameCoordinationEnabled);
            }
            values.Remove("GmRenderLane");
            values.Remove("FrameCoordination");
            return true;
        }

        internal static string Title(int index)
        {
            if (index != 0)
            {
                return PerformancePolicy.Text("开启", "On");
            }
            return PerformancePolicy.Text("关闭", "Off");
        }
    }
}
