// Runs the pinned official exporter in an owned, non-elevated, windowless process.
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Principal;
using System.Threading;
using System.Xml;

internal static class MeasureHardware
{
    private static string Number(double number) { return number.ToString("F3", CultureInfo.InvariantCulture); }
    private static int Main(string[] args)
    {
        MethodInfo export = null;
        try
        {
            using (var identity = WindowsIdentity.GetCurrent())
                if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                    throw new InvalidOperationException("This research runner requires a non-elevated token; no driver installation permitted.");
            Assembly assembly = Assembly.LoadFrom(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Exporter.dll"));
            export = assembly.GetType("Exporter.ExportClass", true).GetMethod("ExportToXML", BindingFlags.Public | BindingFlags.Static);
            if (export == null) throw new InvalidOperationException("Official exporter method not found");
            Console.WriteLine("PROFILE " + args[0] + "; process_bits=" + (IntPtr.Size * 8) + "; administrator=false");
            int count = 7;
            var durations = new double[count - 1];
            long cpuBefore = Process.GetCurrentProcess().TotalProcessorTime.Ticks;
            double initialization = 0;
            for (int i = 0; i < count; i++)
            {
                long start = Stopwatch.GetTimestamp();
                string xml = (string)export.Invoke(null, new object[] { false });
                double milliseconds = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
                if (i == 0) initialization = milliseconds; else durations[i - 1] = milliseconds;
                var document = new XmlDocument { XmlResolver = null };
                using (var text = new StringReader("<root>" + xml + "</root>"))
                using (var reader = XmlReader.Create(text, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
                    document.Load(reader);
                if (i == 0)
                {
                    var types = new System.Collections.Generic.List<string>();
                    foreach (XmlNode hardware in document.SelectNodes("/root/hardware"))
                        types.Add(hardware.SelectSingleNode("type").InnerText);
                    int thermal = 0;
                    foreach (XmlNode sensor in document.SelectNodes("/root/hardware[type='Cpu']/sensor[type='Temperature']"))
                        if (sensor.SelectSingleNode("value").InnerText.Trim().Length > 0) thermal++;
                    Console.WriteLine("SOURCE hardware=" + document.SelectNodes("/root/hardware").Count
                        + "; sensors=" + document.SelectNodes("/root/hardware/sensor").Count
                        + "; types=" + string.Join("|", types) + "; cpu_valid_temperature_channels=" + thermal
                        + "; xml_chars=" + xml.Length);
                }
                Console.WriteLine("SAMPLE " + i + "; export_ms=" + Number(milliseconds));
                if (i + 1 < count)
                {
                    int remaining = 1000 - (int)((Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency);
                    if (remaining > 0) Thread.Sleep(remaining);
                }
            }
            double cpuMilliseconds = (Process.GetCurrentProcess().TotalProcessorTime.Ticks - cpuBefore) / 10000.0;
            Array.Sort(durations);
            Console.WriteLine("RESULT initialization_ms=" + Number(initialization)
                + "; steady_median_ms=" + Number((durations[2] + durations[3]) / 2)
                + "; steady_max_ms=" + Number(durations[durations.Length - 1])
                + "; process_cpu_ms_including_initialization_and_xml_inspection=" + Number(cpuMilliseconds));
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally
        {
            if (export != null) try { export.Invoke(null, new object[] { true }); } catch { }
        }
    }
}
