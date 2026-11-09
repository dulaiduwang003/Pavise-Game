namespace PaviseApp
{
    internal enum HardwareSchedulingProbeReason
    {
        None,
        InvalidRendererLuid,
        NativeForbiddenInTestBuild,
        ArchitectureUnavailable,
        OsVersionUnavailable,
        OsVersionOutsideContract,
        EntryPointsUnavailable,
        OpenFailed,
        InvalidAdapterHandle,
        RendererLuidChanged,
        QueryFailed,
        QueryIdentityChanged,
        FeatureUnknown,
        UnsupportedFeatureVersion,
        UnsupportedResultBits,
        InconsistentFeatureResult,
        CloseFailed,
        BridgeDisposeFailed,
        NativeFailure
    }
}
