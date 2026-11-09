using Microsoft.Win32;

namespace PaviseApp
{
    internal sealed class MaintenanceValue
    {
        internal bool Exists;

        internal RegistryValueKind Kind;

        internal object Value;

        internal bool IsDisabled
        {
            get
            {
                if (Exists && Kind == RegistryValueKind.DWord && Value is int)
                {
                    return (int)Value == 1;
                }
                return false;
            }
        }

        internal bool SameAs(MaintenanceValue other)
        {
            if (other != null && Exists == other.Exists)
            {
                if (Exists)
                {
                    if (Kind == other.Kind)
                    {
                        return object.Equals(Value, other.Value);
                    }
                    return false;
                }
                return true;
            }
            return false;
        }
    }
}
