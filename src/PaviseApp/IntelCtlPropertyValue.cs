using System.Runtime.InteropServices;

namespace PaviseApp
{
    [StructLayout(LayoutKind.Explicit, Size = 8)]
    internal struct IntelCtlPropertyValue
    {
        [FieldOffset(0)]
        public uint EnumValue;
    }
}
