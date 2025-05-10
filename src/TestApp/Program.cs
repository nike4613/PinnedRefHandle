// See https://aka.ms/new-console-template for more information
using System.Runtime.CompilerServices;
using PinnedRefHandle;

var id = new StrongBox<int>(1);
unsafe
{
    fixed (int* pid = &id.Value)
    {
        PinManager.AddPin(pid);
    }
}

