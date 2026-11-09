using System;

namespace PaviseApp
{
    internal struct StandbyPerformanceInformation
    {
        public uint Size;

        public UIntPtr CommitTotal;

        public UIntPtr CommitLimit;

        public UIntPtr CommitPeak;

        public UIntPtr PhysicalTotal;

        public UIntPtr PhysicalAvailable;

        public UIntPtr SystemCache;

        public UIntPtr KernelTotal;

        public UIntPtr KernelPaged;

        public UIntPtr KernelNonpaged;

        public UIntPtr PageSize;

        public uint HandleCount;

        public uint ProcessCount;

        public uint ThreadCount;
    }
}
