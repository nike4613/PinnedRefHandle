using System.Runtime.CompilerServices;

namespace PinnedRefHandle;

#pragma warning disable CS8500 // This takes the address of, gets the size of, or declares a pointer to a managed type
public struct PinnedHandle<T>() : IDisposable, IEquatable<PinnedHandle<T>>
{
    private object? handle;

    public unsafe PinnedHandle(ref T target) : this()
    {
        fixed (T* ptr = &target)
        {
            handle = PinManager.AddPin(ptr);
        }
    }

    public unsafe T* Pointer => handle is { } obj ? (T*)ThreadListData.GetHandleValue(obj) : null;
    public readonly unsafe ref T Target => ref handle is { } obj ? ref *(T*)ThreadListData.GetHandleValue(obj) : ref Unsafe.NullRef<T>();

    public unsafe void SetTarget(ref T target)
    {
        fixed (T* ptr = &target)
        {
            var old = Interlocked.Exchange(ref handle, PinManager.AddPin(ptr));
            if (old is not null)
            {
                PinManager.RemovePin(old);
            }
        }
    }

    public void Dispose()
    {
        var old = Interlocked.Exchange(ref handle, null);
        if (old is not null)
        {
            PinManager.RemovePin(old);
        }
    }

    public override bool Equals(object? obj) => obj is PinnedHandle<T> prh && prh.handle == handle;
    public bool Equals(PinnedHandle<T> other) => other.handle == handle;
    public override int GetHashCode() => handle?.GetHashCode() ?? 0;
    public static bool operator ==(PinnedHandle<T> left, PinnedHandle<T> right) => left.Equals(right);
    public static bool operator !=(PinnedHandle<T> left, PinnedHandle<T> right) => !(left == right);

}
