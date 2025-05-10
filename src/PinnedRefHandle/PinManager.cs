namespace PinnedRefHandle;

public static unsafe class PinManager
{
    private static ThreadListData? lazyThread;

    public static object AddPin(void* addr)
        => ThreadListData.InterlockedInitialize(ref lazyThread).AddPin(addr);

}
