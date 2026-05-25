namespace Dx.Core.Execution;

/// <summary>Identifies the concrete DX block category an executor handles.</summary>
internal enum BlockType
{
    File,
    Patch,
    Fs,
    Request,
}
