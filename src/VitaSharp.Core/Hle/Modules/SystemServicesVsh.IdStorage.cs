using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

// SceIdStorageForDriver (bootimage SceIdStorage 3.74, read in the module, study only): the unit's ID storage, leaves
// of 0x200 bytes in the flash, with ids below 0xFFF0. The storage of the emulated unit is formatted, writable, clean
// and its factory leaves hold no data (they read as zeros, as the bridge's reads do). Every change (format,
// unformat, restart, create/delete/write/update a leaf, atomic leaves) is for product (factory) mode only: a retail
// unit refuses it with 0x80230000 before anything else.
public sealed partial class SystemServicesVsh
{
    /// <summary>SceIdStorage: a change outside product mode, a NULL buffer or a range outside the leaf.</summary>
    const int IDSTORAGE_ERR_INVALID = unchecked((int)0x80230000);

    /// <summary>SceIdStorage: a leaf id of 0xFFF0 or more.</summary>
    const int IDSTORAGE_ERR_LEAF_ID = unchecked((int)0x80230001);

    /// <summary>The size of a leaf and the first id that is not a leaf.</summary>
    const uint IdStorageLeafSize = 0x200, IdStorageMaxLeaf = 0xFFF0;

    /// <summary>ksceIdStorageGetLeafSize (0xEB830733)(): 0x200.</summary>
    [Hle(Nid = 0xEB830733, Library = "SceIdStorageForDriver")]
    int ksceIdStorageGetLeafSize()
    {
        return (int)IdStorageLeafSize;
    }

    /// <summary>ksceIdStorageIsFormatted (0xFEFA40C2)(): 1.</summary>
    [Hle(Nid = 0xFEFA40C2, Library = "SceIdStorageForDriver")]
    int ksceIdStorageIsFormatted()
    {
        return 1;
    }

    /// <summary>ksceIdStorageIsReadOnly (0x2D633688)(): 0.</summary>
    [Hle(Nid = 0x2D633688, Library = "SceIdStorageForDriver")]
    int ksceIdStorageIsReadOnly()
    {
        return 0;
    }

    /// <summary>ksceIdStorageIsDirty (0xB9069BAD)(): no leaf waits to be written: 0.</summary>
    [Hle(Nid = 0xB9069BAD, Library = "SceIdStorageForDriver")]
    int ksceIdStorageIsDirty()
    {
        return 0;
    }

    /// <summary>ksceIdStorageGetFreeLeaves (0x37833CB8)(): the free leaves the bridge reports (0).</summary>
    [Hle(Nid = 0x37833CB8, Library = "SceIdStorageForDriver")]
    int ksceIdStorageGetFreeLeaves()
    {
        return 0;
    }

    /// <summary>ksceIdStorageEnumId (0x31E08AFB)(...): the module takes its lock and answers 0.</summary>
    [Hle(Nid = 0x31E08AFB, Library = "SceIdStorageForDriver")]
    int ksceIdStorageEnumId()
    {
        return 0;
    }

    /// <summary>ksceIdStorageFlush (0x3AD32523)(): writes the dirty leaves back: none.</summary>
    [Hle(Nid = 0x3AD32523, Library = "SceIdStorageForDriver")]
    int ksceIdStorageFlush()
    {
        return 0;
    }

    /// <summary>ksceIdStorageReadLeaf (0xEB00C509)(int leaf, void *buffer 0x200): a whole leaf (zeros).</summary>
    [Hle(Nid = 0xEB00C509, Library = "SceIdStorageForDriver")]
    int ksceIdStorageReadLeaf(uint leaf, uint buffer)
    {
        if (leaf >= IdStorageMaxLeaf)
        {
            return IDSTORAGE_ERR_LEAF_ID;
        }

        if (buffer == 0)
        {
            return IDSTORAGE_ERR_INVALID;
        }

        GuestMemory.Fill(buffer, 0, IdStorageLeafSize);
        return 0;
    }

    /// <summary>ksceIdStorageLookup (0x6FE062D1)(int leaf, int offset, void *buffer, SceSize size): part of a leaf
    /// (the range must lie inside the 0x200 bytes).</summary>
    [Hle(Nid = 0x6FE062D1, Library = "SceIdStorageForDriver")]
    int ksceIdStorageLookup(uint leaf, uint offset, uint buffer, uint size)
    {
        if (leaf >= IdStorageMaxLeaf)
        {
            return IDSTORAGE_ERR_LEAF_ID;
        }

        if (offset >= IdStorageLeafSize || size > IdStorageLeafSize || offset + size > IdStorageLeafSize)
        {
            return IDSTORAGE_ERR_INVALID;
        }

        if (buffer != 0 && size != 0)
        {
            GuestMemory.Fill(buffer, 0, size);
        }

        return 0;
    }

    /// <summary>The changes, refused outside product mode (0x80230000): Format (0x958089DB), Unformat (0xF4BCB3EE),
    /// Restart (0x60B647A5), CreateLeaf (0x08A471A6), CreateAtomicLeaves (0x99ACCB71), DeleteLeaf (0x2C97AB36),
    /// WriteLeaf (0x1FA4D135), Update (0x683AAC10).</summary>
    [Hle("ksceIdStorageFormat", Nid = 0x958089DB, Library = "SceIdStorageForDriver")]
    [Hle("ksceIdStorageUnformat", Nid = 0xF4BCB3EE, Library = "SceIdStorageForDriver")]
    [Hle("ksceIdStorageRestart", Nid = 0x60B647A5, Library = "SceIdStorageForDriver")]
    [Hle("ksceIdStorageCreateLeaf", Nid = 0x08A471A6, Library = "SceIdStorageForDriver")]
    [Hle("ksceIdStorageCreateAtomicLeaves", Nid = 0x99ACCB71, Library = "SceIdStorageForDriver")]
    [Hle("ksceIdStorageDeleteLeaf", Nid = 0x2C97AB36, Library = "SceIdStorageForDriver")]
    [Hle("ksceIdStorageWriteLeaf", Nid = 0x1FA4D135, Library = "SceIdStorageForDriver")]
    [Hle("ksceIdStorageUpdate", Nid = 0x683AAC10, Library = "SceIdStorageForDriver")]
    int IdStorageChange()
    {
        return IDSTORAGE_ERR_INVALID;
    }
}
