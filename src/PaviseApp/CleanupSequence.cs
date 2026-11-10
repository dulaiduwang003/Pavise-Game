using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal static class CleanupSequence
    {
        internal static void Complete(Exception originalFailure, params Action[] obligations)
        {
            if (obligations == null)
            {
                throw new ArgumentNullException("obligations");
            }
            List<Exception> exceptions = new List<Exception>();
            foreach (Action action in obligations)
            {
                try
                {
                    if (action == null)
                    {
                        throw new ArgumentException("Missing cleanup obligation");
                    }
                    action();
                }
                catch (Exception item)
                {
                    exceptions.Add(item);
                }
            }
            if (exceptions.Count == 0)
            {
                return;
            }
            if (originalFailure != null)
            {
                exceptions.Insert(0, originalFailure);
            }
            throw new AggregateException("One or more cleanup obligations remain unconfirmed", exceptions);
        }
    }
}
