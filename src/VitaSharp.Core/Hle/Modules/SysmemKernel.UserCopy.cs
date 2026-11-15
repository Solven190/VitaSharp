using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// The rest of SceSysmemForDriver's user-memory accessors, as the FW 3.74 SceSysmem has them: each runs the copy with
/// the user permission checks of the unprivileged loads and stores (LDRT/STRT, the domains set to client), so a range
/// the caller's process may not touch ends the copy with the fault error; the *Proc variants first switch to the
/// address space of process <c>pid</c>.
/// </summary>
public sealed partial class SysmemKernel
{
    /// <summary>ksceKernelCopyFromToUser(dst, src, len): a copy from user memory to user memory; 0.</summary>
    [Hle(Nid = 0x1BD44DD5, Library = "SceSysmemForDriver")]
    int ksceKernelCopyFromToUser(uint dst, uint src, uint len)
    {
        if (!IsUserAccessible(src, len) || !IsUserAccessible(dst, len))
        {
            return KernelSpace.UserCopyFault;
        }

        GuestMemory.Copy(dst, src, len);
        return 0;
    }

    /// <summary>ksceKernelStrnlenFromUser(s, max): the length of the user string, at most max.</summary>
    [Hle(Nid = 0xB429D419, Library = "SceSysmemForDriver")]
    int ksceKernelStrnlenFromUser(uint s, uint max)
    {
        uint length = 0;
        while (length < max)
        {
            if (!IsUserAccessible(s + length, 1))
            {
                return KernelSpace.UserCopyFault;
            }

            if (GuestMemory.Read<byte>(s + length) == 0)
            {
                break;
            }

            length++;
        }

        return (int)length;
    }

    /// <summary>ksceKernelStrncpyToUser(dst, src, len): the kernel string into user memory up to its NUL or len bytes,
    /// the rest of the len bytes zeroed; returns the length copied without the NUL.</summary>
    [Hle(Nid = 0x80BD6FEB, Library = "SceSysmemForDriver")]
    int ksceKernelStrncpyToUser(uint dst, uint src, uint len)
    {
        if (!KernelSpace.IsMappedRange(src, Math.Min(len, 1u)))
        {
            return KernelSpace.UserCopyFault;
        }

        uint n = 0;
        while (n < len)
        {
            if (!IsUserAccessible(dst + n, 1))
            {
                return KernelSpace.UserCopyFault;
            }

            byte c = GuestMemory.Read<byte>(src + n);
            GuestMemory.Write(dst + n, c);
            if (c == 0)
            {
                if (!IsUserAccessible(dst + n, len - n))
                {
                    return KernelSpace.UserCopyFault;
                }

                GuestMemory.Fill(dst + n, 0, len - n);
                return (int)n;
            }

            n++;
        }

        return (int)len;
    }

    /// <summary>ksceKernelStrncpyFromUserProc(pid, dst, src, len): ksceKernelStrncpyFromUser in process
    /// <paramref name="pid"/>'s address space.</summary>
    [Hle(Nid = 0x75AAF178, Library = "SceSysmemForDriver")]
    int ksceKernelStrncpyFromUserProc(int pid, uint dst, uint src, uint len)
    {
        if (ProcessOf(pid) is not { } process)
        {
            return SceError.INVALID_PID;
        }

        if (!KernelSpace.IsMappedRange(dst, len))
        {
            return KernelSpace.UserCopyFault;
        }

        byte[] text = new byte[len];
        int copied = InProcess(process, () =>
        {
            uint n = 0;
            while (n < len)
            {
                if (!IsUserAccessible(src + n, 1))
                {
                    return KernelSpace.UserCopyFault;
                }

                byte c = GuestMemory.Read<byte>(src + n);
                text[n] = c;
                if (c == 0)
                {
                    return (int)n;
                }

                n++;
            }

            return (int)len;
        });
        if (copied < 0)
        {
            return copied;
        }

        text.CopyTo(GuestMemory.Span(dst, (int)len));
        return copied;
    }

    /// <summary>ksceKernelCopyToUserProc(pid, dst, src, len): ksceKernelCopyToUser in process
    /// <paramref name="pid"/>'s address space.</summary>
    [Hle(Nid = 0x6B825479, Library = "SceSysmemForDriver")]
    int ksceKernelCopyToUserProc(int pid, uint dst, uint src, uint len)
    {
        if (ProcessOf(pid) is not { } process)
        {
            return SceError.INVALID_PID;
        }

        if (!KernelSpace.IsMappedRange(src, len))
        {
            return KernelSpace.UserCopyFault;
        }

        byte[] data = GuestMemory.ReadSpan(src, (int)len).ToArray();
        bool done = InProcess(process, () =>
        {
            if (!IsUserAccessible(dst, len))
            {
                return false;
            }

            data.CopyTo(GuestMemory.Span(dst, (int)len));
            return true;
        });
        return done ? 0 : KernelSpace.UserCopyFault;
    }

    /// <summary>ksceKernelCountFillValueFromUserProc(pid, ptr, value, len): how many bytes from ptr, a word at a time,
    /// are words equal to <paramref name="value"/> (stops at the first other word).</summary>
    [Hle(Nid = 0x8334454F, Library = "SceSysmemForDriver")]
    int ksceKernelCountFillValueFromUserProc(int pid, uint ptr, uint value, uint len)
    {
        if (ProcessOf(pid) is not { } process)
        {
            return SceError.INVALID_PID;
        }

        return InProcess(process, () =>
        {
            uint counted = 0;
            while (counted < len)
            {
                if (!IsUserAccessible(ptr + counted, 4))
                {
                    return KernelSpace.UserCopyFault;
                }

                if (GuestMemory.R32(ptr + counted) != value)
                {
                    break;
                }

                counted += 4;
            }

            return (int)counted;
        });
    }
}
