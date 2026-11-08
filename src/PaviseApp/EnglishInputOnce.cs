using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace PaviseApp
{
	internal sealed class EnglishInputOnce
	{
		private sealed class Session
		{
			internal string ProfileId;

			internal long Deadline;

			internal bool Consumed;

			internal bool Revoked;

			internal bool HistoryFull;

			internal Func<bool> OriginalAdmission;

			internal readonly List<GameInputProcess> Processes = new List<GameInputProcess>();
		}

		internal const int ForegroundWaitMs = 15000;

		private const int MaximumHistory = 128;

		private readonly object stateGate = new object();

		private readonly object operationGate = new object();

		private readonly IGameInputLanguage input;

		private readonly Func<long> clock;

		private readonly List<Session> history = new List<Session>();

		private Session current;

		private int cancellationVersion;

		private bool historyExhausted;

		internal EnglishInputOnce(IGameInputLanguage value)
			: this(value, MonotonicMilliseconds)
		{
		}

		internal EnglishInputOnce(IGameInputLanguage value, Func<long> milliseconds)
		{
			if (value == null || milliseconds == null)
			{
				throw new ArgumentNullException();
			}
			input = value;
			clock = milliseconds;
		}

		private static long MonotonicMilliseconds()
		{
			return (long)((double)Stopwatch.GetTimestamp() * (1000.0 / (double)Stopwatch.Frequency));
		}

		private static bool SameProfile(string first, string second)
		{
			if (!string.IsNullOrEmpty(first))
			{
				return string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
			}
			return false;
		}

		private static void Remember(Session session, GameInputProcess process)
		{
			if (process.IsValid && !session.Processes.Contains(process))
			{
				session.Processes.Add(process);
			}
		}

		private static bool Evaluate(Func<bool> admission)
		{
			try
			{
				return admission != null && admission();
			}
			catch
			{
				return false;
			}
		}

		internal void Begin(string profileId, GameInputProcess process, bool enabledAtEntry, Func<bool> admission)
		{
			long deadline = clock() + 15000;
			lock (operationGate)
			{
				int cancellationVersionSnapshot;
				Session[] historyItems;
				lock (stateGate)
				{
					cancellationVersionSnapshot = cancellationVersion;
					if (current != null && SameProfile(current.ProfileId, profileId))
					{
						Remember(current, process);
						return;
					}
					if (current != null)
					{
						current.Consumed = true;
						current.Revoked = true;
					}
					current = null;
					historyItems = history.ToArray();
				}
				Session session = null;
				List<Session> list = new List<Session>();
				Session[] sessionItems = historyItems;
				foreach (Session session2 in sessionItems)
				{
					if (session != null)
					{
						foreach (GameInputProcess process3 in session2.Processes)
						{
							if (process3.Equals(process))
							{
								session = session2;
								break;
							}
						}
						continue;
					}
					bool beginReady = false;
					foreach (GameInputProcess process4 in session2.Processes)
					{
						if (process4.Equals(process))
						{
							session = session2;
							beginReady = true;
							break;
						}
						if (!beginReady)
						{
							GameInputProcessState gameInputProcessState;
							try
							{
								gameInputProcessState = input.Probe(process4);
							}
							catch
							{
								gameInputProcessState = GameInputProcessState.Unknown;
							}
							if (gameInputProcessState != GameInputProcessState.Gone)
							{
								beginReady = true;
							}
						}
					}
					if (!beginReady)
					{
						list.Add(session2);
					}
					else if (session == null && SameProfile(session2.ProfileId, profileId))
					{
						session = session2;
					}
				}
				bool matchesEnabledAtEntry = enabledAtEntry && Evaluate(admission);
				lock (stateGate)
				{
					matchesEnabledAtEntry = matchesEnabledAtEntry && cancellationVersionSnapshot == cancellationVersion;
					foreach (Session item in list)
					{
						history.Remove(item);
					}
					if (session != null)
					{
						session.ProfileId = profileId;
						session.Consumed = true;
						session.Revoked = true;
						Remember(session, process);
						current = session;
						return;
					}
					if (history.Count >= 128)
					{
						historyExhausted = true;
					}
					Session session3 = new Session();
					session3.ProfileId = profileId;
					session3.Deadline = deadline;
					session3.Consumed = !matchesEnabledAtEntry;
					session3.Revoked = !matchesEnabledAtEntry;
					session3.OriginalAdmission = admission;
					session3.HistoryFull = historyExhausted;
					Session session4 = session3;
					Remember(session4, process);
					if (!session4.HistoryFull)
					{
						history.Add(session4);
					}
					current = session4;
				}
			}
		}

		private bool Allowed(Session session, Func<bool> admission)
		{
			bool matchesReferenceEquals;
			lock (stateGate)
			{
				matchesReferenceEquals = object.ReferenceEquals(current, session) && !session.Revoked;
			}
			if (matchesReferenceEquals && clock() <= session.Deadline && Evaluate(session.OriginalAdmission))
			{
				return Evaluate(admission);
			}
			return false;
		}

		internal EnglishInputOutcome Step(string profileId, GameInputProcess process, Func<bool> admission)
		{
			if (!Monitor.TryEnter(operationGate))
			{
				return null;
			}
			try
			{
				Session session;
				lock (stateGate)
				{
					session = current;
					if (session == null || !SameProfile(session.ProfileId, profileId))
					{
						return null;
					}
					Remember(session, process);
					if (session.Consumed)
					{
						return null;
					}
				}
				EnglishInputOutcome englishInputOutcome;
				if (session.HistoryFull)
				{
					englishInputOutcome = new EnglishInputOutcome(EnglishInputResult.HistoryFull);
				}
				else if (clock() > session.Deadline)
				{
					englishInputOutcome = new EnglishInputOutcome(EnglishInputResult.Expired);
				}
				else
				{
					if (!process.IsValid)
					{
						return null;
					}
					if (!Allowed(session, admission))
					{
						englishInputOutcome = new EnglishInputOutcome(EnglishInputResult.Canceled);
					}
					else
					{
						Func<bool> allowed = () => Allowed(session, admission);
						Func<bool> claim = delegate
						{
							if (allowed())
							{
								lock (stateGate)
								{
									if (!object.ReferenceEquals(current, session) || session.Revoked || session.Consumed)
									{
										return false;
									}
									session.Consumed = true;
									return true;
								}
							}
							return false;
						};
						try
						{
							englishInputOutcome = input.TryOnce(process, claim, allowed);
						}
						catch
						{
							englishInputOutcome = new EnglishInputOutcome(EnglishInputResult.Unavailable);
						}
						if (englishInputOutcome == null)
						{
							englishInputOutcome = new EnglishInputOutcome(EnglishInputResult.Unavailable);
						}
					}
				}
				if (englishInputOutcome.Result == EnglishInputResult.WaitingForGameWindow)
				{
					if (Allowed(session, admission))
					{
						return null;
					}
					englishInputOutcome = new EnglishInputOutcome((clock() > session.Deadline) ? EnglishInputResult.Expired : EnglishInputResult.Canceled);
				}
				lock (stateGate)
				{
					session.Consumed = true;
				}
				return englishInputOutcome;
			}
			finally
			{
				Monitor.Exit(operationGate);
			}
		}

		internal void Cancel()
		{
			lock (stateGate)
			{
				cancellationVersion++;
				if (current != null)
				{
					current.Revoked = true;
					current.Consumed = true;
				}
			}
		}

		internal void End()
		{
			lock (stateGate)
			{
				cancellationVersion++;
				if (current != null)
				{
					current.Revoked = true;
					current.Consumed = true;
				}
				current = null;
			}
		}

		internal bool Drain(int timeoutMs)
		{
			if (Monitor.IsEntered(operationGate))
			{
				return false;
			}
			if (!Monitor.TryEnter(operationGate, Math.Max(0, timeoutMs)))
			{
				return false;
			}
			Monitor.Exit(operationGate);
			return true;
		}
	}
}
