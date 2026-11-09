namespace PaviseApp
{
    internal interface IMaintenanceControl
    {
        bool TryRead(out MaintenanceValue value);

        bool TryWrite(int value, MaintenanceValue expected);

        bool TryDelete(MaintenanceValue expected);

        MaintenanceRuntimeSnapshot QueryRuntime();
    }
}
