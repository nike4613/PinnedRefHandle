#include "version.h"

// control status
.class public auto ansi sealed PinnedRefHandle.IlHelpers.ControlStatus
    extends [CORE_ASSEMBLY]System.Enum
{
    .field public specialname rtspecialname int32 value__

    .field public static literal valuetype PinnedRefHandle.IlHelpers.ControlStatus FreeFrame = int32(0)
    .field public static literal valuetype PinnedRefHandle.IlHelpers.ControlStatus Alloc1 = int32(1)
    .field public static literal valuetype PinnedRefHandle.IlHelpers.ControlStatus Alloc2 = int32(2)
    .field public static literal valuetype PinnedRefHandle.IlHelpers.ControlStatus Alloc4 = int32(3)
    .field public static literal valuetype PinnedRefHandle.IlHelpers.ControlStatus Alloc8 = int32(4)

    .field public static literal valuetype PinnedRefHandle.IlHelpers.ControlStatus FlagRead = int32(0x40000000)
    .field public static literal valuetype PinnedRefHandle.IlHelpers.ControlStatus FlagWrite = int32(0x60000000)
}

// list header
.class public sequential ansi sealed beforefieldinit PinnedRefHandle.IlHelpers.ListHeader
    extends [CORE_ASSEMBLY]System.ValueType
{
    .field public method int8& *(
        valuetype PinnedRefHandle.IlHelpers.ListHeader*, // header
        int32, // slot count
        valuetype PinnedRefHandle.IlHelpers.ControlStatus&, // out status
        int8& // in ref
        ) ControlFunc

    .field public void* Data
}
