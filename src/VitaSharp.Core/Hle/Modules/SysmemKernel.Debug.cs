using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// The kernel's debug output (SceDebugForDriver / SceDebugForKernel in SceSysmem, FW 3.74, studied in the decompiled
/// module): ksceKernelPrintf and its variants go to the kprintf handler, by default the kernel's own, which formats with
/// __prnt and writes every character into the 4 KB TTY ring (what ksceKernelGetTtyInfo reads) and to the putchar
/// handler a debug module may register; they return the number of characters. The "level" variants print only at or
/// below the kprintf level (level 0 always); the "with info" variants first print the core and the caller's debug
/// info. A failed kernel assertion prints "*** Kernel Assertion failed at &lt;...&gt;" and carries on; a panic prints
/// "*** Kernel Panic at &lt;...&gt;" and stops the system. VitaSharp also copies each line of the TTY to its log.
/// </summary>
public sealed partial class SysmemKernel
{
    const int TtySize = 0x1000;

    /// <summary>The kernel TTY ring and its write position.</summary>
    [SnapshotMissing] // states of builds before it: nothing was printed into it
    readonly byte[] _tty = new byte[TtySize];

    [SnapshotMissing]
    int _ttyWrite;

    /// <summary>The kprintf handler a module registered (0: the kernel's own) and its argument.</summary>
    [SnapshotMissing]
    uint _kprintfHandler, _kprintfArgument;

    /// <summary>The putchar handler a module registered and its argument (none until one registers).</summary>
    [SnapshotMissing]
    uint _putcharHandler, _putcharArgument;

    [SnapshotMissing]
    bool _putcharRegistered;

    /// <summary>The minimum assertion level (0 on a retail unit, set from the DIP switches) and the kprintf level.</summary>
    [SnapshotMissing]
    int _assertLevel, _kprintfLevel;

    /// <summary>Whether a crash makes a dump (on by default).</summary>
    [SnapshotMissing]
    int _crashDump = 1;

    // the line the log copy collects (host side only)
    [SnapshotIgnore]
    readonly System.Text.StringBuilder _ttyLine = new();

    /// <summary>The kernel's own sink: each character into the TTY ring and the log line, and to the putchar
    /// handler (which also sees the begin and end marks).</summary>
    readonly struct TtySink(SysmemKernel owner, GuestThread thread) : IPrntSink
    {
        public void Put(int character)
        {
            owner.PutChar(thread, character);
        }
    }

    void PutChar(GuestThread thread, int character)
    {
        if ((uint)(character - PrntBegin) > 1)
        {
            lock (_tty)
            {
                _tty[_ttyWrite] = (byte)character;
                _ttyWrite = (_ttyWrite + 1) % TtySize;
                if (character == '\n')
                {
                    Log.Info("[kernel tty] " + _ttyLine);
                    _ttyLine.Clear();
                }
                else
                {
                    _ttyLine.Append((char)(byte)character);
                }
            }
        }

        if (_putcharRegistered && _putcharHandler != 0)
        {
            thread.CallGuest(_putcharHandler, _putcharArgument, (uint)character);
        }
    }

    /// <summary>kprintf: the registered handler (handler(arg, fmt, va)), or the kernel's own formatter into the TTY.</summary>
    int Kprintf(uint format, uint va)
    {
        GuestThread thread = GuestThread.Current!;
        if (_kprintfHandler != 0)
        {
            return (int)thread.CallGuest(_kprintfHandler, _kprintfArgument, format, va);
        }

        var sink = new TtySink(this, thread);
        return Prnt(ref sink, format, va);
    }

    /// <summary>Text the kernel itself prints (a header), through the same path as kprintf.</summary>
    int KprintfText(string text)
    {
        uint buffer = KernelAlloc((uint)text.Length + 1, "kernel:kprintf");
        try
        {
            GuestMemory.WriteCString(buffer, text);
            uint format = KernelAlloc(3, "kernel:kprintf");
            try
            {
                GuestMemory.WriteCString(format, "%s");
                uint va = KernelAlloc(4, "kernel:kprintf");
                try
                {
                    GuestMemory.W32(va, buffer);
                    return Kprintf(format, va);
                }
                finally
                {
                    Mem.Free(va);
                }
            }
            finally
            {
                Mem.Free(format);
            }
        }
        finally
        {
            Mem.Free(buffer);
        }
    }

    /// <summary>The variadic words of a call whose fixed arguments end at register <paramref name="fixedArguments"/>:
    /// the firmware pushes the remaining argument registers below the stack pointer, so they continue into the stacked
    /// ones.</summary>
    static unsafe uint PushVariadic(HleContext context, int fixedArguments)
    {
        uint sp = context.Cpu->R[13];
        int pushed = 4 - fixedArguments;
        uint va = sp - (uint)pushed * 4;
        for (int i = 0; i < pushed; i++)
        {
            GuestMemory.W32(va + (uint)i * 4, context.Cpu->R[fixedArguments + i]);
        }

        return va;
    }

    /// <summary>Whether a message of <paramref name="level"/> prints: level 0 always, otherwise at or below the
    /// kprintf level.</summary>
    bool PrintsAt(int level) => level == 0 || level <= _kprintfLevel;

    /// <summary>ksceKernelPrintf(fmt, ...): the number of characters printed.</summary>
    [Hle(Nid = 0x391B74B7, Library = "SceDebugForDriver", Cost = HleCost.UserLibrary)]
    void ksceKernelPrintf(HleContext context)
    {
        context.Return(Kprintf(context.Gpr(0), PushVariadic(context, 1)));
    }

    /// <summary>ksceKernelVprintf(fmt, va_list).</summary>
    [Hle(Nid = 0x411C0733, Library = "SceDebugForDriver", Cost = HleCost.UserLibrary)]
    int ksceKernelVprintf(uint format, uint va) => Kprintf(format, va);

    /// <summary>ksceKernelPrintfLevel(level, fmt, ...): 0 when the level does not print.</summary>
    [Hle(Nid = 0x1A3F2AA4, Library = "SceDebugForDriver", Cost = HleCost.UserLibrary)]
    void ksceKernelPrintfLevel(HleContext context)
    {
        context.Return(PrintsAt((int)context.Gpr(0)) ? Kprintf(context.Gpr(1), PushVariadic(context, 2)) : 0);
    }

    /// <summary>ksceKernelVprintfLevel(level, fmt, va_list).</summary>
    [Hle(Nid = 0x611A158B, Library = "SceDebugForDriver", Cost = HleCost.UserLibrary)]
    int ksceKernelVprintfLevel(int level, uint format, uint va) => PrintsAt(level) ? Kprintf(format, va) : 0;

    /// <summary>
    /// The prefix ksceKernelPrintfWithInfo prints: flag 1 the core ("0:"), flags 2 the info's first word (and its
    /// name, +0xC), flags 6 its first two words (and file:line, +0xC/+0x10), flag 8 its third word (and its name,
    /// +0x14).
    /// </summary>
    static string InfoPrefix(uint flags, uint info)
    {
        var prefix = new System.Text.StringBuilder();
        if ((flags & 1) != 0)
        {
            prefix.Append("0:");
        }

        if (info == 0)
        {
            return prefix.ToString();
        }

        uint first = GuestMemory.R32(info);
        uint second = GuestMemory.R32(info + 4);
        uint third = GuestMemory.R32(info + 8);
        uint name = GuestMemory.R32(info + 0xC);
        uint line = GuestMemory.R32(info + 0x10);
        uint function = GuestMemory.R32(info + 0x14);
        if ((flags & 6) == 2)
        {
            prefix.Append(name == 0 ? $"0x{first:x8}:" : $"0x{first:x8}({GuestMemory.ReadCString(name)}):");
        }
        else if ((flags & 6) == 6)
        {
            prefix.Append(name == 0 ? $"0x{first:x8}{second:x8}:" : $"0x{first:x8}{second:x8}({GuestMemory.ReadCString(name)}:{(int)line}):");
        }

        if ((flags & 8) != 0)
        {
            prefix.Append(function == 0 ? $"0x{third:x8}:" : $"0x{third:x8}({GuestMemory.ReadCString(function)}):");
        }

        return prefix.ToString();
    }

    /// <summary>ksceKernelPrintfWithInfo(flags, info, fmt, ...): the info prefix, then the message.</summary>
    [Hle(Nid = 0x02B04343, Library = "SceDebugForDriver", Cost = HleCost.UserLibrary)]
    void ksceKernelPrintfWithInfo(HleContext context)
    {
        KprintfText(InfoPrefix(context.Gpr(0), context.Gpr(1)));
        context.Return(Kprintf(context.Gpr(2), PushVariadic(context, 3)));
    }

    /// <summary>ksceKernelPrintfLevelWithInfo(level, flags, info, fmt, ...).</summary>
    [Hle(Nid = 0xD9703808, Library = "SceDebugForDriver", Cost = HleCost.UserLibrary)]
    unsafe void ksceKernelPrintfLevelWithInfo(HleContext context)
    {
        if (!PrintsAt((int)context.Gpr(0)))
        {
            context.Return(0);
            return;
        }

        KprintfText(InfoPrefix(context.Gpr(1), context.Gpr(2)));
        context.Return(Kprintf(context.Gpr(3), context.Cpu->R[13]));
    }

    /// <summary>The header of a failed assertion or a panic: the core, the info's words and names, the caller.</summary>
    static string FailureHeader(string what, uint info, uint caller)
    {
        if (info == 0)
        {
            return $"*** {what} at <0:0x0000000000000000:0x00000000:0x{caller:x8}>\n";
        }

        uint first = GuestMemory.R32(info);
        uint second = GuestMemory.R32(info + 4);
        uint third = GuestMemory.R32(info + 8);
        uint file = GuestMemory.R32(info + 0xC);
        if (file == 0)
        {
            return $"*** {what} at <0:0x{first:x8}{second:x8}:0x{third:x8}:0x{caller:x8}>\n";
        }

        uint function = GuestMemory.R32(info + 0x14);
        string functionName = function == 0 ? "(null)" : GuestMemory.ReadCString(function);
        return $"*** {what} at <0:0x{first:x8}{second:x8}({GuestMemory.ReadCString(file)}:{(int)GuestMemory.R32(info + 0x10)}):0x{third:x8}({functionName}):0x{caller:x8}>\n";
    }

    /// <summary>ksceKernelAssert(condition, info): a false condition prints the assertion; the caller goes on.</summary>
    [Hle(Nid = 0x35A35322, Library = "SceDebugForDriver")]
    unsafe void ksceKernelAssert(HleContext context)
    {
        if (context.Gpr(0) == 0)
        {
            KprintfText(FailureHeader("Kernel Assertion failed", context.Gpr(1), context.Cpu->R[14]));
        }
    }

    /// <summary>ksceKernelAssertLevel(level, condition, info): ksceKernelAssert when the level is at or below the
    /// assertion level.</summary>
    [Hle(Nid = 0xFD753E7A, Library = "SceDebugForDriver")]
    unsafe void ksceKernelAssertLevel(HleContext context)
    {
        if ((int)context.Gpr(0) <= _assertLevel && context.Gpr(1) == 0)
        {
            KprintfText(FailureHeader("Kernel Assertion failed", context.Gpr(2), context.Cpu->R[14]));
        }
    }

    /// <summary>ksceKernelPrintfAssertLevel(level, condition, info, fmt, ...): a false condition at a level that
    /// asserts prints the assertion header and the message.</summary>
    [Hle(Nid = 0x821A2D59, Library = "SceDebugForDriver")]
    unsafe void ksceKernelPrintfAssertLevel(HleContext context)
    {
        if (context.Gpr(1) == 0 && (int)context.Gpr(0) <= _assertLevel)
        {
            KprintfText(FailureHeader("Kernel Assertion failed", context.Gpr(2), context.Cpu->R[14]));
            if (context.Gpr(3) != 0)
            {
                Kprintf(context.Gpr(3), context.Cpu->R[13]);
            }
        }
    }

    /// <summary>ksceKernelPrintfPanic(info, fmt, ...): prints the panic header and the message, then the system
    /// stops.</summary>
    [Hle(Nid = 0x00CCE39C, Library = "SceDebugForDriver")]
    unsafe void ksceKernelPrintfPanic(HleContext context)
    {
        uint caller = context.Cpu->R[14];
        KprintfText(FailureHeader("Kernel Panic", context.Gpr(0), caller));
        if (context.Gpr(1) != 0)
        {
            Kprintf(context.Gpr(1), PushVariadic(context, 2));
        }

        Log.Error($"kernel panic (from {K.Symbolize(caller)})");
        throw new GuestFaultException($"kernel panic from {K.Symbolize(caller)}");
    }

    /// <summary>ksceKernelGetAssertLevel(): the minimum assertion level.</summary>
    [Hle(Nid = 0x374B7868, Library = "SceDebugForDriver", ReturnsValue = true)]
    int ksceKernelGetAssertLevel() => _assertLevel;

    /// <summary>ksceKernelSetAssertLevel(level): returns the previous level.</summary>
    [Hle(Nid = 0xA0CE3287, Library = "SceDebugForKernel", ReturnsValue = true)]
    int ksceKernelSetAssertLevel(int level)
    {
        int previous = _assertLevel;
        _assertLevel = level;
        return previous;
    }

    /// <summary>SceDebugForKernel 0xC5889385 (the kprintf level setter): returns the previous level.</summary>
    [Hle(Nid = 0xC5889385, Library = "SceDebugForKernel", ReturnsValue = true)]
    int ksceKernelSetKprintfLevel(int level)
    {
        int previous = _kprintfLevel;
        _kprintfLevel = level;
        return previous;
    }

    /// <summary>ksceKernelEnableCrashDump(enable): returns the previous setting.</summary>
    [Hle(Nid = 0xA465A31A, Library = "SceDebugForKernel", ReturnsValue = true)]
    int ksceKernelEnableCrashDump(int enable)
    {
        int previous = _crashDump;
        _crashDump = enable;
        return previous;
    }

    /// <summary>ksceKernelRegisterKprintfHandler(handler, arg): kprintf goes to handler(arg, fmt, va) from now on.</summary>
    [Hle(Nid = 0x88AD6D0C, Library = "SceDebugForKernel")]
    void ksceKernelRegisterKprintfHandler(uint handler, uint argument)
    {
        _kprintfHandler = handler;
        _kprintfArgument = argument;
    }

    /// <summary>ksceKernelRegisterDebugPutcharHandler(handler, arg): every character of the kernel TTY also goes to
    /// handler(arg, c).</summary>
    [Hle(Nid = 0x22546577, Library = "SceDebugForKernel")]
    void ksceKernelRegisterDebugPutcharHandler(uint handler, uint argument)
    {
        _putcharHandler = handler;
        _putcharArgument = argument;
        _putcharRegistered = true;
    }

    /// <summary>ksceKernelGetDebugPutcharHandler(): the registered handler, 0 before one registers.</summary>
    [Hle(Nid = 0x8D474850, Library = "SceDebugForKernel", ReturnsValue = true)]
    uint ksceKernelGetDebugPutcharHandler() => _putcharRegistered ? _putcharHandler : 0;

    /// <summary>ksceKernelDebugPutchar(c): one character into the TTY (and the putchar handler); returns 1.</summary>
    [Hle(Nid = 0x2AABAEDA, Library = "SceDebugForKernel")]
    int ksceKernelDebugPutchar(int character)
    {
        PutChar(GuestThread.Current!, character);
        return 1;
    }

    /// <summary>ksceKernelGetTtyInfo(buffer, size): the last min(size, 4096) bytes of the TTY ring, oldest first from
    /// the write position; returns how many.</summary>
    [Hle(Nid = 0x62466B0A, Library = "SceDebugForDriver", ReturnsValue = true)]
    uint ksceKernelGetTtyInfo(uint buffer, uint size)
    {
        if (size > TtySize)
        {
            size = TtySize;
        }

        lock (_tty)
        {
            int first = Math.Min((int)size, TtySize - _ttyWrite);
            _tty.AsSpan(_ttyWrite, first).CopyTo(GuestMemory.Span(buffer, first));
            if (first < (int)size)
            {
                _tty.AsSpan(0, (int)size - first).CopyTo(GuestMemory.Span(buffer + (uint)first, (int)size - first));
            }
        }

        return size;
    }
}
