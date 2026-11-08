using System;
using System.Globalization;
using Microsoft.Win32;

namespace PaviseApp
{
	internal sealed class MaintenancePauseEngine
	{
		private const char Separator = '\u001f';

		private readonly IMaintenanceControl control;

		private readonly IOptionalServiceLedger ledger;

		private readonly object gate = new object();

		private bool active;

		internal bool Active
		{
			get
			{
				lock (gate)
				{
					return active;
				}
			}
		}

		internal bool HasResidue
		{
			get
			{
				lock (gate)
				{
					string raw;
					return !ReadLedger(out raw) || raw.Length != 0;
				}
			}
		}

		internal MaintenancePauseEngine(IMaintenanceControl control, IOptionalServiceLedger ledger)
		{
			if (control == null || ledger == null)
			{
				throw new ArgumentNullException();
			}
			this.control = control;
			this.ledger = ledger;
		}

		internal bool Activate()
		{
			lock (gate)
			{
				try
				{
					MaintenanceValue value;
					if (active)
					{
						return control.TryRead(out value) && value != null && value.IsDisabled;
					}
					string raw;
					if (!ReadLedger(out raw))
					{
						return false;
					}
					if (raw.Length != 0 && !Restore())
					{
						return false;
					}
					if (!control.TryRead(out value) || value == null)
					{
						return false;
					}
					if (value.Exists && (value.Kind != RegistryValueKind.DWord || !(value.Value is int)))
					{
						return false;
					}
					if (value.IsDisabled)
					{
						active = true;
						return true;
					}
					MaintenanceValue maintenanceValue = value;
					string toStringOffset = (value.Exists ? ("=" + ((int)value.Value).ToString(CultureInfo.InvariantCulture)) : "__pavise_absent__");
					if (!SaveLedger(toStringOffset + '\u001f' + "=1"))
					{
						return false;
					}
					if (!control.TryRead(out value) || !maintenanceValue.SameAs(value))
					{
						return false;
					}
					if (!control.TryWrite(1, maintenanceValue) || !control.TryRead(out value) || value == null || !value.IsDisabled)
					{
						return false;
					}
					active = true;
					return true;
				}
				catch
				{
					return false;
				}
			}
		}

		internal bool Restore()
		{
			lock (gate)
			{
				active = false;
				string raw;
				if (!ReadLedger(out raw))
				{
					return false;
				}
				if (raw.Length == 0)
				{
					return true;
				}
				bool absent;
				int original;
				if (!Parse(raw, out absent, out original))
				{
					return false;
				}
				try
				{
					MaintenanceValue value;
					if (!control.TryRead(out value) || value == null)
					{
						return false;
					}
					if (!value.IsDisabled)
					{
						return SaveLedger("");
					}
					if (!(absent ? control.TryDelete(value) : control.TryWrite(original, value)) || !control.TryRead(out value) || value == null)
					{
						return false;
					}
					bool matchesValue;
					if (!absent)
					{
						if (!value.Exists || value.Kind != RegistryValueKind.DWord || !(value.Value is int))
						{
							goto RejectUnconfirmedService;
						}
						matchesValue = (int)value.Value == original;
					}
					else
					{
						matchesValue = !value.Exists;
					}
					if (matchesValue)
					{
						return SaveLedger("");
					}
					goto RejectUnconfirmedService;
					RejectUnconfirmedService:
					return false;
				}
				catch
				{
					return false;
				}
			}
		}

		internal MaintenanceRuntimeSnapshot QueryRuntime()
		{
			try
			{
				MaintenanceRuntimeSnapshot maintenanceRuntimeSnapshot = control.QueryRuntime();
				if (maintenanceRuntimeSnapshot == null)
				{
					MaintenanceRuntimeSnapshot maintenanceRuntimeSnapshot2 = new MaintenanceRuntimeSnapshot();
					maintenanceRuntimeSnapshot2.State = MaintenanceRuntimeState.Unknown;
					maintenanceRuntimeSnapshot = maintenanceRuntimeSnapshot2;
				}
				return maintenanceRuntimeSnapshot;
			}
			catch
			{
				MaintenanceRuntimeSnapshot maintenanceRuntimeSnapshot3 = new MaintenanceRuntimeSnapshot();
				maintenanceRuntimeSnapshot3.State = MaintenanceRuntimeState.Unknown;
				maintenanceRuntimeSnapshot3.Detail = "Runtime status could not be read";
				return maintenanceRuntimeSnapshot3;
			}
		}

		private bool ReadLedger(out string raw)
		{
			raw = null;
			try
			{
				return ledger.TryRead(out raw) && raw != null;
			}
			catch
			{
				return false;
			}
		}

		private bool SaveLedger(string raw)
		{
			try
			{
				string raw2;
				return ledger.TryWrite(raw) && ReadLedger(out raw2) && raw == raw2;
			}
			catch
			{
				return false;
			}
		}

		private static bool Parse(string raw, out bool absent, out int original)
		{
			absent = false;
			original = 0;
			if (raw.Length > 128)
			{
				return false;
			}
			int matchIndex = raw.IndexOf('\u001f');
			if (matchIndex >= 0 && (raw.IndexOf('\u001f', matchIndex + 1) >= 0 || raw.Substring(matchIndex + 1) != "=1"))
			{
				return false;
			}
			string rawSnapshot = ((matchIndex < 0) ? raw : raw.Substring(0, matchIndex));
			if (rawSnapshot == "__pavise_absent__")
			{
				absent = true;
				return true;
			}
			if (rawSnapshot.StartsWith("=", StringComparison.Ordinal))
			{
				rawSnapshot = rawSnapshot.Substring(1);
			}
			return int.TryParse(rawSnapshot, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out original);
		}
	}
}
