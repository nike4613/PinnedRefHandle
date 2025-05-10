using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using PinnedRefHandle.IlHelpers;

namespace PinnedRefHandle;

public static unsafe class PinManager
{
    private static ThreadListData? lazyThread;

    public static void AddPin(void* addr)
    {
        var t = lazyThread;
        if (t is null)
        {
            _ = Interlocked.CompareExchange(ref lazyThread, new(), null);
            t = lazyThread;
            t.StartThread();
        }

        t.AddPin(addr);
    }


    private sealed class AddPinnedRecord(nint Ptr, ManualResetEventSlim CompletionFinished)
    {
        public readonly nint Ptr = Ptr;
        public int Index;
        public ManualResetEventSlim CompletionFinished = CompletionFinished;
    }
    private sealed record RemovePinnedRecord(int Index);

    private sealed class ThreadListData
    {
        public readonly ConcurrentBag<AddPinnedRecord> AddChannel = new();
        public readonly ConcurrentBag<RemovePinnedRecord> RemoveChannel = new();
        public readonly ManualResetEventSlim EventsAvailable = new();

        public ManualResetEventSlim? PendingResetEvent;
        public Thread? Thread;

        public int Capacity;
        public int Count;

        public bool AddingFrame;

        private static void ThreadStart(object? obj)
        {
            var data = (ThreadListData)obj!;

            var header = new ListHeader()
            {
                ControlFunc = &StorageControl,
                Data = &data,
            };

            data.AddingFrame = true;
            Helpers.Alloc4(&header);
        }

        public void StartThread()
        {
            lock (this)
            {
                if (Thread is not null) return;

                Thread = new Thread(ThreadStart)
                {
                    IsBackground = true,
                };
                Thread.Start(this);
                return;
            }
        }

        public void AddPin(void* ptr)
        {
            // TODO: pool ManualResetEvent?
            using var mre = new ManualResetEventSlim();
            AddChannel.Add(new((nint)ptr, mre));
            EventsAvailable.Set();
            mre.Wait();
        }
    }

    private static ref sbyte StorageControl(
        ListHeader* header,
        int frameSize,
        ref ControlStatus status,
        ref sbyte inValue
        )
    {
        var data = *(ThreadListData*)header->Data;

        if (data.AddingFrame)
        {
            // we just added this frame, add it to capa
            data.Capacity += frameSize;
            data.AddingFrame = false;
        }

        do
        {
            // if last iter we recorded an event, we've now done what we need, so signal it
            if (data.PendingResetEvent is { } pending)
            {
                pending.Set();
                data.PendingResetEvent = null;
            }

            var capacityInOldFrames = data.Capacity - frameSize;
            var countInThisFrame = data.Count - capacityInOldFrames;

            // first, try to process additions
            while (data.AddChannel.TryTake(out var addRecord))
            {
                if (countInThisFrame >= frameSize)
                {
                    // a new frame needs to be allocated

                    // make sure we add the record back to the bag
                    data.AddChannel.Add(addRecord);
                    data.AddingFrame = true;
                    status = ControlStatus.Alloc4;
                    return ref Unsafe.NullRef<sbyte>();
                }

                // otherwise, we have space, and allocate it
                addRecord.Index = capacityInOldFrames + countInThisFrame;
                data.Count++;
                Debug.Assert(data.Count <= data.Capacity);

                // we need to wait to wake the caller until we've stored the pinned ref, next iter
                data.PendingResetEvent = addRecord.CompletionFinished;

                // then do a write
                status = ControlStatus.FlagWrite | (ControlStatus)countInThisFrame;
                return ref Unsafe.AsRef<sbyte>((void*)addRecord.Ptr);
            }

            // then, removals
            // TODO:

            // then, wait
            data.EventsAvailable.Wait();
            data.EventsAvailable.Reset();
        }
        while (true);
    }


}
