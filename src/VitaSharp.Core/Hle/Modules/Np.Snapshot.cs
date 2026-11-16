using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

[GenerateSnapshot, SnapshotIgnoreFields("_calloutThreads", "CalloutSink", "_xmlReaders", "XmlSink")]
public sealed partial class NpManager
{
    [GenerateSnapshot]
    sealed partial class AuthReq;
}

[GenerateSnapshot]
public sealed partial class NpSocial
{
}
