using System;

namespace PaviseApp
{
	internal static class HardThreadHistoricalPolicy
	{
		internal static HardThreadJournal MarkGone(HardThreadJournal original, HardThreadGoneReason reason)
		{
			if (original == null || reason == HardThreadGoneReason.None || !Enum.IsDefined(typeof(HardThreadGoneReason), reason))
			{
				throw new ArgumentException("confirmed original target gone fact required");
			}
			HardThreadJournal hardThreadJournal = original.Clone();
			hardThreadJournal.Revision = checked(original.Revision + 1);
			foreach (HardThreadReceipt row in hardThreadJournal.Rows)
			{
				if (!row.Terminal)
				{
					string phaseText = row.Phase.ToString();
					row.Phase = HardThreadPhase.Gone;
					row.Detail = string.Concat(row.Detail ?? "", "; historical original target gone: ", reason, "; prior phase=", phaseText);
				}
			}
			return hardThreadJournal;
		}

		internal static HardThreadGoneReason Gone(HardThreadTarget target, HardThreadProcessFact first, HardThreadProcessFact second)
		{
			if (target == null)
			{
				return HardThreadGoneReason.None;
			}
			HardThreadProcessFact[] hardThreadProcessFactItems = new HardThreadProcessFact[2] { first, second };
			foreach (HardThreadProcessFact hardThreadProcessFact in hardThreadProcessFactItems)
			{
				if (hardThreadProcessFact != null && hardThreadProcessFact.Opened && hardThreadProcessFact.Pid == target.ProcessId && hardThreadProcessFact.TimesKnown && hardThreadProcessFact.Birth > 0 && (hardThreadProcessFact.Wait == 0 || hardThreadProcessFact.Wait == 258))
				{
					if (hardThreadProcessFact.Birth != target.ProcessCreation)
					{
						return HardThreadGoneReason.ProcessIdReused;
					}
					if (hardThreadProcessFact.Wait == 0)
					{
						return HardThreadGoneReason.OriginalExited;
					}
				}
			}
			if (first != null && second != null && first.Pid == target.ProcessId && second.Pid == target.ProcessId && !first.Opened && !second.Opened && first.Error == 87 && second.Error == 87)
			{
				return HardThreadGoneReason.RepeatedMissingProcess;
			}
			return HardThreadGoneReason.None;
		}
	}
}
