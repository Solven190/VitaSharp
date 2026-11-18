using System.Security.Cryptography;
using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

// SceSblSsMgrForDriver (os0:kd/ss_mgr.skprx 3.74, read in the module, study only), the DMAC5 entries left: AES-CMAC
// with a key in memory loaded under a key id, the hash command every one-call hash goes through (the base command in
// the low bits: 0x03 SHA-1, 0x0B SHA-224, 0x13 SHA-256, 0x23/0x33 HMAC-SHA1/256 and 0x3B AES-CMAC, these three with a
// key the engine already holds), and NID_1B14658D, the cipher of ScePfsMgr with a key slot.
public sealed partial class SystemServicesVsh
{
    /// <summary>The DMAC5 hash commands with a key in memory or none.</summary>
    const uint Dmac5CommandSha1 = 0x03, Dmac5CommandSha256 = 0x13;

    /// <summary>ksceSblDmac5AesCmacNP (0x83B058F5)(src, dst 0x10, length, key, keyBits, ctx 0x10, keyId, mask, flags):
    /// the AES-CMAC of the input with the key as given (key id 0); the other key ids are derived by the secure
    /// processor (0x80020004), and the chaining forms carry the engine's state (0x80020004).</summary>
    [Hle(Nid = 0x83B058F5, Library = "SceSblSsMgrForDriver")]
    int ksceSblDmac5AesCmacNP(uint src, uint dst, uint length, uint key, uint keyBits, uint context, uint keyId, int mask,
        uint flags)
    {
        int keyBytes = keyBits switch
        {
            0x80 => 16,
            0xC0 => 24,
            0x100 or 0x200 => 32,
            _ => 0,
        };
        if (keyBytes == 0 || key == 0)
        {
            return DMAC5_ERR_INVALID_ARGUMENT;
        }

        if (keyId != 0 || (flags & Dmac5HashChainFlags) != 0)
        {
            return KERNEL_ERR_UNSUP;
        }

        Span<byte> mac = stackalloc byte[16];
        AesCmac(GuestMemory.ReadSpan(key, keyBytes), GuestMemory.ReadSpan(src, (int)length), mac);
        if (dst != 0)
        {
            mac.CopyTo(GuestMemory.Span(dst, 16));
        }

        return 0;
    }

    /// <summary>ksceSblSsMgrExecuteDmac5HashCommand (0x9641374E)(src, dst, size, ctx, mask, command, flags): runs a
    /// hash command over the input: SHA-1 (3) and SHA-256 (0x13) give their digest; the keyed commands use a key the
    /// engine holds, SHA-224 and the chaining forms the engine's own state: 0x80020004.</summary>
    [Hle(Nid = 0x9641374E, Library = "SceSblSsMgrForDriver")]
    int ksceSblSsMgrExecuteDmac5HashCommand(uint src, uint dst, uint size, uint context, int mask, uint command,
        uint flags)
    {
        if ((flags & Dmac5HashChainFlags) != 0)
        {
            return KERNEL_ERR_UNSUP;
        }

        ReadOnlySpan<byte> input = GuestMemory.ReadSpan(src, (int)size);
        byte[] digest;
        if (command == Dmac5CommandSha1)
        {
            digest = SHA1.HashData(input);
        }
        else if (command == Dmac5CommandSha256)
        {
            digest = SHA256.HashData(input);
        }
        else
        {
            return KERNEL_ERR_UNSUP;
        }

        if (dst != 0)
        {
            digest.CopyTo(GuestMemory.Span(dst, digest.Length));
        }

        return 0;
    }

    /// <summary>NID_1B14658D (SceSblSsMgrForDriver)(src, dst, size, iv, keyBits, slot, mask, command, flags): a DMAC5
    /// cipher with a key the secure processor put in a key slot. The key size is checked first (64, 128, 192, 256 or
    /// 512 bits, else 0x800F1516); the key itself is not on the emulated unit: 0x80020004.</summary>
    [Hle("NID_1B14658D", Nid = 0x1B14658D, Library = "SceSblSsMgrForDriver", Default = true)]
    int Dmac5CipherWithKeyslot(uint src, uint dst, uint size, uint iv, uint keyBits)
    {
        if (keyBits is not (0x40 or 0x80 or 0xC0 or 0x100 or 0x200))
        {
            return DMAC5_ERR_INVALID_ARGUMENT;
        }

        return KERNEL_ERR_UNSUP;
    }

    /// <summary>AES-CMAC (NIST SP 800-38B) of <paramref name="message"/> under <paramref name="key"/>.</summary>
    static void AesCmac(ReadOnlySpan<byte> key, ReadOnlySpan<byte> message, Span<byte> mac)
    {
        using Aes aes = Aes.Create();
        aes.Key = key.ToArray();
        Span<byte> subkey1 = stackalloc byte[16];
        Span<byte> subkey2 = stackalloc byte[16];
        Span<byte> zero = stackalloc byte[16];
        zero.Clear();
        aes.EncryptEcb(zero, subkey1, PaddingMode.None);
        CmacDouble(subkey1, subkey1);
        CmacDouble(subkey1, subkey2);

        int blocks = Math.Max(1, (message.Length + 15) / 16);
        bool lastComplete = message.Length != 0 && message.Length % 16 == 0;
        Span<byte> state = stackalloc byte[16];
        Span<byte> block = stackalloc byte[16];
        state.Clear();
        for (int index = 0; index < blocks; index++)
        {
            int offset = index * 16;
            int count = Math.Min(16, message.Length - offset);
            block.Clear();
            message.Slice(offset, count).CopyTo(block);
            if (index == blocks - 1)
            {
                if (lastComplete)
                {
                    CmacXor(block, subkey1);
                }
                else
                {
                    block[count] = 0x80;
                    CmacXor(block, subkey2);
                }
            }

            CmacXor(block, state);
            aes.EncryptEcb(block, state, PaddingMode.None);
        }

        state.CopyTo(mac);
    }

    /// <summary>The doubling in GF(2^128) of the CMAC subkeys.</summary>
    static void CmacDouble(ReadOnlySpan<byte> input, Span<byte> output)
    {
        bool carry = (input[0] & 0x80) != 0;
        for (int i = 0; i < 15; i++)
        {
            output[i] = (byte)((input[i] << 1) | (input[i + 1] >> 7));
        }

        output[15] = (byte)(input[15] << 1);
        if (carry)
        {
            output[15] ^= 0x87;
        }
    }

    static void CmacXor(Span<byte> target, ReadOnlySpan<byte> other)
    {
        for (int i = 0; i < 16; i++)
        {
            target[i] ^= other[i];
        }
    }
}
