using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal static class HardThreadPinClosure
    {
        internal static void Close(Action closeImage, ref IntPtr handle, ref bool uncertain, Func<IntPtr, bool> closeRaw)
        {
            if (closeImage == null)
            {
                throw new ArgumentNullException("closeImage");
            }
            List<Exception> exceptions = new List<Exception>();
            try
            {
                closeImage();
            }
            catch (Exception inner)
            {
                exceptions.Add(new HardThreadClosureFailure("Original image source closure is unconfirmed", inner));
            }
            try
            {
                HardThreadRawHandleClosure.Close(ref handle, ref uncertain, closeRaw);
            }
            catch (Exception item)
            {
                exceptions.Add(item);
            }
            if (exceptions.Count != 0)
            {
                throw new AggregateException("Original actor source cleanup remains unconfirmed", exceptions);
            }
        }
    }
}
