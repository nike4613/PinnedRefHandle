// See https://aka.ms/new-console-template for more information
using System.Runtime.CompilerServices;
using PinnedRefHandle;

var id = new StrongBox<int>(1);
unsafe
{
    object pin1, pin2, pin3, pin4, pin5;

    fixed (int* pid = &id.Value)
    {
        pin1 = PinManager.AddPin(pid);
        pin2 = PinManager.AddPin(pid);
        pin3 = PinManager.AddPin(pid);
        pin4 = PinManager.AddPin(pid);
        pin5 = PinManager.AddPin(pid);
    }

    PinManager.RemovePin(pin1);
    PinManager.RemovePin(pin2);
    PinManager.RemovePin(pin3);
    PinManager.RemovePin(pin4);
    PinManager.RemovePin(pin5);
}

