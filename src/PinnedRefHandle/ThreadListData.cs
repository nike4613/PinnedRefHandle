using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using PinnedRefHandle.IlHelpers;

namespace PinnedRefHandle;

internal sealed unsafe class ThreadListData
{
    private sealed class PinHandle(nint Ptr, ThreadListData OwningList, int Index)
    {
        public readonly nint Ptr = Ptr;
        public ThreadListData OwningList = OwningList;
        public int Index = Index;
    }

    private sealed class AddPinnedRecord(nint Ptr, ManualResetEventSlim CompletionFinished)
    {
        public readonly nint Ptr = Ptr;
        public ManualResetEventSlim CompletionFinished = CompletionFinished;
        public PinHandle? OutHandle;
    }

    private readonly record struct TakeOwnRecord(ThreadListData From, PinHandle Handle, ManualResetEventSlim MRE);
    private readonly record struct RemoveRecord(ThreadListData From, PinHandle Handle, ManualResetEventSlim? MRE);

    private readonly ConcurrentBag<AddPinnedRecord> AddChannel = new();
    private readonly ConcurrentBag<RemoveRecord> RemoveChannel = new();
    private readonly ConcurrentBag<TakeOwnRecord> TakeOwnChannel = new();
    private readonly ManualResetEventSlim EventsAvailable = new();
    private readonly List<PinHandle?> Owned = new();

    private ManualResetEventSlim? PendingResetEvent;
    private Thread? Thread;

    private int Capacity;
    private int Count;
    private int ThisFrameEmptySlots;
    public bool HasSpace => Count < Capacity;

    private bool AddingFrame;
    private bool IsRemoving;

    private ThreadListData? AuxList;
    private ThreadListData? ContinueList;

    private RemoveRecord RemoveTarget;

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

    public static ThreadListData InterlockedInitialize(ref ThreadListData? tld)
    {
        var tl = tld;
        if (tl is null)
        {
            _ = Interlocked.CompareExchange(ref tld, new(), null);
            tl = tld;
            tl.StartThread();
        }
        return tl;
    }

    private ThreadListData GetAuxList()
    {
        var tl = AuxList;
        if (tl is null)
        {
            _ = Interlocked.CompareExchange(ref AuxList, new(), null);
            tl = AuxList;
            tl.AuxList = this; // aux lists are mutual
            tl.StartThread();
        }
        return tl;
    }

    private ThreadListData GetContinueList()
    {
        var tl = ContinueList;
        if (tl is null)
        {
            _ = Interlocked.CompareExchange(ref ContinueList, new(), null);
            tl = ContinueList;
            tl.AuxList = GetAuxList(); // the continue list should have the same aux list as ourselves
            tl.StartThread();
        }
        return tl;
    }

    private void StartThread()
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

    private static ref sbyte StorageControl(
        ListHeader* header,
        int frameSize,
        ref ControlStatus status
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

        RemovalCheck:
            if (data.IsRemoving)
            {
                // We're in the process of removing something. Continue the process of migrating this frame, if necessary.
                if (data.RemoveTarget.Handle.Index < capacityInOldFrames)
                {
                    // the target is in a deeper stack frame, need to continue
                    for (var i = data.Count - 1; i >= capacityInOldFrames; i--)
                    {
                        var handle = data.Owned[i];
                        if (handle is not null)
                        {
                            // we found a handle to remove; first initiate the transfer
                            data.GetAuxList().TakeOwn(data, handle);
                            // now that the aux list has taken ownership of the handle, clear our bookkeeping for it and clear the slot
                            data.Owned.RemoveAt(i);
                            data.Count--;
                            status = ControlStatus.FlagWrite | (ControlStatus)(i - capacityInOldFrames);
                            return ref Unsafe.NullRef<sbyte>();
                        }
                        else
                        {
                            // there's no handle here, remove the list entry if present
                            if (data.Owned.Count > i)
                            {
                                data.Owned.RemoveAt(i);
                            }
                        }
                    }

                    // if we've fallen out here, we've cleared this frame out and should deallocate it
                    data.Capacity -= frameSize;
                    status = ControlStatus.FreeFrame;
                    return ref Unsafe.NullRef<sbyte>();
                }
                else
                {
                    // the target is in this stack frame; we're done with the removal process; fall out
                }
            }

            // first, try to process additions
            while (data.AddChannel.TryTake(out var addRecord))
            {
                if (data.ThisFrameEmptySlots > 0)
                {
                    // the current frame has empty slots; find the slot and use it
                    var index = 0;
                    while (index < countInThisFrame && data.Owned[capacityInOldFrames + index] != null)
                        index++;

                    if (index < countInThisFrame)
                    {
                        // we were able to find the empty slot; use it
                        data.ThisFrameEmptySlots--;
                        addRecord.OutHandle = new(addRecord.Ptr, data, capacityInOldFrames + index);
                        data.PendingResetEvent = addRecord.CompletionFinished;
                        status = ControlStatus.FlagWrite | (ControlStatus)countInThisFrame;
                        return ref Unsafe.AsRef<sbyte>((void*)addRecord.Ptr);
                    }

                    // we weren't able to actually find it; fall out
                    Debug.Fail("Could not find empty frame slot when there should be one");
                }

                if (countInThisFrame >= frameSize)
                {
                    // a new frame needs to be allocated

                    if (!RuntimeHelpers.TryEnsureSufficientExecutionStack())
                    {
                        // there's not enough stack! push to another thread

                        // prefer the AuxList if it has spare space available
                        if (data.AuxList is { HasSpace: true } list)
                        {
                            list.AddChannel.Add(addRecord);
                            list.EventsAvailable.Set();
                            continue;
                        }

                        // otherwise, push to the continuation list
                        var list2 = data.GetContinueList();
                        list2.AddChannel.Add(addRecord);
                        list2.EventsAvailable.Set();
                        continue;
                    }

                    // make sure we add the record back to the bag
                    data.AddChannel.Add(addRecord);
                    data.AddingFrame = true;
                    data.ThisFrameEmptySlots = 0;
                    status = ControlStatus.Alloc4;
                    return ref Unsafe.NullRef<sbyte>();
                }

                // otherwise, we have space, and allocate it
                addRecord.OutHandle = new(addRecord.Ptr, data, capacityInOldFrames + countInThisFrame);
                data.Owned.Add(addRecord.OutHandle); // save the handle; we may need it again
                data.Count++;
                Debug.Assert(data.Count <= data.Capacity);
                Debug.Assert(data.Count == data.Owned.Count);

                // we need to wait to wake the caller until we've stored the pinned ref, next iter
                data.PendingResetEvent = addRecord.CompletionFinished;

                // then do a write
                status = ControlStatus.FlagWrite | (ControlStatus)countInThisFrame;
                return ref Unsafe.AsRef<sbyte>((void*)addRecord.Ptr);
            }

            // then, handle taking ownership
            while (data.TakeOwnChannel.TryTake(out var takeOwn))
            {
                if (countInThisFrame >= frameSize)
                {
                    // to take ownership, we need to allocate frames

                    if (!RuntimeHelpers.TryEnsureSufficientExecutionStack())
                    {
                        // there's not enough stack! push to another thread

                        // do NOT push to the aux list, as this probably came from there. We need to instead push off to the continuation.
                        var list2 = data.GetContinueList();
                        list2.TakeOwnChannel.Add(takeOwn);
                        list2.EventsAvailable.Set();
                        continue;
                    }

                    data.TakeOwnChannel.Add(takeOwn); // add the item back if we need to retry
                    data.AddingFrame = true;
                    status = ControlStatus.Alloc4;
                    return ref Unsafe.NullRef<sbyte>();
                }

                // we have space, try to take ownership
                lock (takeOwn.Handle)
                {
                    if (takeOwn.Handle.OwningList != takeOwn.From)
                    {
                        // someone else got to this first, don't mess up their bookkeeping
                        continue;
                    }

                    // slot the handle in place
                    takeOwn.Handle.OwningList = data;
                    takeOwn.Handle.Index = capacityInOldFrames + countInThisFrame;
                    data.Owned.Add(takeOwn.Handle);
                    data.Count++;
                    Debug.Assert(data.Count <= data.Capacity);
                    Debug.Assert(data.Count == data.Owned.Count);

                    // record the pending reset event
                    data.PendingResetEvent = takeOwn.MRE;

                    // then finally, perform the write
                    status = ControlStatus.FlagWrite | (ControlStatus)countInThisFrame;
                    return ref Unsafe.AsRef<sbyte>((void*)takeOwn.Handle.Ptr);
                }
            }

            // then, removal (which is BY FAR the most complicated operation)
            RemoveRecord removal = default;
            while (data.IsRemoving || data.RemoveChannel.TryTake(out removal))
            {
                if (data.IsRemoving)
                {
                    removal = data.RemoveTarget;
                    data.IsRemoving = false;
                }

                // a handle in this queue was necessarily AT ONE POINT in our list, but may not be anymore
                // filter those that have already had their ownership transfered (or rather, re-queue them)
                lock (removal.Handle)
                {
                    if (removal.Handle.OwningList != data)
                    {
                        RemovePin(removal.Handle, wait: removal.MRE);
                        continue;
                    }

                    // otherwise, this is STILL owned by this list, and so we necessarily have exclusive control and don't need the lock anymore
                }

                if (removal.Handle.Index >= data.Count)
                {
                    // already removed? invalid? whatever
                    continue;
                }

                if (removal.Handle.Index == data.Count - 1)
                {
                    // this is the LAST handle added; removing is simple
                    data.Count--;
                    data.Owned.RemoveAt(removal.Handle.Index);
                    removal.Handle.Index = int.MaxValue; // prevent double-free
                    data.PendingResetEvent = removal.MRE;
                    // write out null
                    status = ControlStatus.FlagWrite | (ControlStatus)(countInThisFrame - 1);
                    return ref Unsafe.NullRef<sbyte>();
                }

                if (removal.Handle.Index >= capacityInOldFrames)
                {
                    // the handle is NOT the last handle added, but IS in this frame. This also keeps things relatively simple; we can null out the slot
                    // and increment ThisFrameEmptySlots
                    var removeIndex = removal.Handle.Index - capacityInOldFrames;
                    data.ThisFrameEmptySlots++;
                    data.Owned[removal.Handle.Index] = null;
                    removal.Handle.Index = int.MaxValue;
                    data.PendingResetEvent = removal.MRE;

                    status = ControlStatus.FlagWrite | (ControlStatus)removeIndex;
                    return ref Unsafe.NullRef<sbyte>();
                }

                // the handle is not even in this frame... Here's where we need to do some extra shenanigans.
                // We will:
                //  1. Record the item to be removed.
                //  2. Loop back around to the front, to process the bulk of the removal
                //  3. In that front, we will move handles from this list to our aux list until we find the frame with the item to remove
                //  4. At that frame, we'll perform the above normal logic to complete the removal.
                data.IsRemoving = true;
                data.RemoveTarget = removal;
                goto RemovalCheck;
            }

            // then, wait
            data.EventsAvailable.Wait();
            data.EventsAvailable.Reset();
        }
        while (true);
    }

    public object AddPin(void* ptr)
    {
        if (AuxList is { } aux && aux.Count < Count)
        {
            // the aux list has been allocated, and has fewer items; shell off to it for balance
            return aux.AddPin(ptr);
        }

        // TODO: pool ManualResetEvent? AddPinnedRecord?
        using var mre = new ManualResetEventSlim();
        var rec = new AddPinnedRecord((nint)ptr, mre);
        AddChannel.Add(rec);
        EventsAvailable.Set();
        mre.Wait();
        return rec.OutHandle!;
    }

    // note: only takes ownership if the handle's owner is still the listed one when we get to it
    private void TakeOwn(ThreadListData owner, PinHandle handle)
    {
        // TODO: pool ManualResetEvent?
        using var mre = new ManualResetEventSlim();
        TakeOwnChannel.Add(new(owner, handle, mre));
        EventsAvailable.Set();
        mre.Wait();
    }

    public static void RemovePin(object handleObj, ManualResetEventSlim? wait)
    {
        var handle = (PinHandle)handleObj;

        // lock so that ownership transfer doesn't happen while we're working
        var data = handle.OwningList;
        data.RemoveChannel.Add(new(data, handle, wait));
        data.EventsAvailable.Set();
    }

    public static void* GetHandleValue(object handleObj)
        => (void*)((PinHandle)handleObj).Ptr;
}
