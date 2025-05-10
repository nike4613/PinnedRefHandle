namespace PinnedRefHandle;

public static unsafe class PinManager
{
    private static ThreadListData? lazyThread;

    public static object AddPin(void* addr)
        => ThreadListData.InterlockedInitialize(ref lazyThread).AddPin(addr);

    public static void RemovePin(object pin)
    {
        ArgumentNullException.ThrowIfNull(pin);

        using var mre = new ManualResetEventSlim();
        ThreadListData.RemovePin(pin, mre);
        mre.Wait();
    }
}
