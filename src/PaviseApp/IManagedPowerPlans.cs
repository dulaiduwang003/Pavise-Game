using System;

namespace PaviseApp
{
    internal interface IManagedPowerPlans
    {
        Guid? Current();

        bool Exists(Guid scheme);

        bool IsOwned(Guid scheme);

        bool Duplicate(Guid source, out Guid created);

        bool Activate(Guid scheme);

        bool Delete(Guid scheme);

        bool Rename(Guid scheme, string title, string note);

        string Name(Guid scheme);

        Guid[] ProcessorSettings(Guid scheme, Guid processor);

        uint? Read(Guid scheme, Guid sub, Guid setting, bool ac);

        bool Write(Guid scheme, Guid sub, Guid setting, bool ac, uint value);
    }

    // 守护写回专用：只接受本方持有且当前活动的计划，把被外部改掉的数值写回。
    // 候选计划的调参仍只在未激活时进行，不经过这个入口。返回 Win32 错误码，0 为成功。
    internal interface IManagedPowerActiveRepair
    {
        uint WriteActiveOwned(Guid scheme, Guid sub, Guid setting, bool ac, uint value);
    }
}
