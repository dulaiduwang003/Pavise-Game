using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal sealed class LogEventEntry
    {
        public string Raw = "";

        public string Date = "-- --";

        public string Time = "--:--:--";

        public string Module = "SYSTEM";

        public string Message = "";

        public LogEventSeverity Severity;

        public DateTime Timestamp;

        public string SessionToken = "";

        public string SessionName = "";

        public string Phase = "";

        public string StableKey = "";

        public string FirstTime = "";

        public string LastTime = "";

        public int RepeatCount = 1;

        public bool IsSummary;

        public List<LogEventEntry> Details = new List<LogEventEntry>();
    }
}
