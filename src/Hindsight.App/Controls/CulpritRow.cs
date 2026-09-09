namespace Hindsight.App.Controls;

public sealed record CulpritRow(
    string Process, string Cpu, string Gpu, string FileIo, string Network, string DiskPhysical,
    string Memory, string Private, string Handles, string Threads,
    string ParentChain, string Note, string CommandLine, string Description,
    int IdentityId);
