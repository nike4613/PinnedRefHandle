#include "version.h"

.class public auto ansi abstract sealed PinnedRefHandle.IlHelpers.Helpers
    extends [CORE_ASSEMBLY]System.Object
{

#define FUNC_ALLOC1 "PinnedRefHandle.IlHelpers.Helpers::Alloc1"
#define FUNC_ALLOC2 "PinnedRefHandle.IlHelpers.Helpers::Alloc2"
#define FUNC_ALLOC4 "PinnedRefHandle.IlHelpers.Helpers::Alloc4"
//#define FUNC_ALLOC8 "PinnedRefHandle.IlHelpers.Helpers::Alloc8"

#define FUNC_DEF_NAME "Alloc1"
#include "FuncDef.ilinc"
#undef FUNC_DEF_NAME

#define FUNC_GE_2
#define FUNC_DEF_NAME "Alloc2"
#include "FuncDef.ilinc"
#undef FUNC_DEF_NAME

#define FUNC_GE_4
#define FUNC_DEF_NAME "Alloc4"
#include "FuncDef.ilinc"
#undef FUNC_DEF_NAME

//#define FUNC_GE_8
//#define FUNC_DEF_NAME "Alloc8"
//#include "FuncDef.ilinc"
//#undef FUNC_DEF_NAME

}