namespace PaviseApp
{
    internal sealed class GpuAdapter
    {
        public string Name;

        public string HardwareId;

        public GpuVendor Vendor;

        public bool Integrated;

        public bool IntegratedKnown;

        public long VideoMemoryBytes;

        public int BusNumber = -1;

        public string KindText
        {
            get
            {
                if (!Integrated)
                {
                    return Lang.T("t.gpuinventory.2");
                }
                return Lang.T("t.gpuinventory.1");
            }
        }
    }
}
