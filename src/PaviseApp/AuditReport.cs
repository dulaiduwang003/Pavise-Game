using System.Collections.Generic;

namespace PaviseApp
{
	internal sealed class AuditReport
	{
		public readonly List<AuditRow> Capability = new List<AuditRow>();

		public readonly List<AuditRow> Machine = new List<AuditRow>();

		public readonly List<AuditRow> Persistent = new List<AuditRow>();

		public readonly List<AuditRow> Verdicts = new List<AuditRow>();

		public bool MeasureOk;

		public int MeasureWindowMs;

		public double CpuBusy;

		public bool MemoryCompressionCanEnable;

		public GpuIrqResult GpuIrq;
	}
}
