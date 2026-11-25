namespace PaviseApp
{
    internal sealed class GpuAdapter
    {
        public string Name;

        internal string DriverName;

        public string HardwareId;

        // 设备实例路径 对应 Enum 下的设备键
        public string InstanceId;

        public GpuVendor Vendor;

        public bool Integrated;

        public bool IntegratedKnown;

        public long VideoMemoryBytes;

        public int BusNumber = -1;

        internal GpuAdapter Copy()
        {
            return (GpuAdapter)MemberwiseClone();
        }

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
