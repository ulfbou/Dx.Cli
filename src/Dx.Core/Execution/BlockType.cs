namespace Dx.Core.Execution;

/// <summary>Identifies the concrete DX block category an executor handles.</summary>
internal enum BlockType
{
    /// <summary>
    /// The "file" block type represents a simple file read or write operation. It is the most basic and widely used block type, 
    /// and is intended for straightforward file manipulations that do not require complex logic or multiple steps. Examples 
    /// include reading the contents of a file, writing data to a file, or appending data to an existing file. The "file" block 
    /// type is designed to be efficient and easy to use for common file operations, making it a fundamental building block for 
    /// many DX workflows.
    /// </summary>
    File,

    /// <summary>
    /// The "patch" block type represents a more complex operation that involves applying a set of changes (a "patch") to a 
    /// target file or set of files.
    /// </summary>
    Patch,

    /// <summary>
    /// The "fs" block type represents filesystem operations such as moving, deleting, encoding, checking out, or restoring 
    /// files or directories.
    /// </summary>
    Fs,

    /// <summary>
    /// The "request" block type represents an execution request that may involve invoking external tools, making API calls, 
    /// or performing other operations that do not fit into the "file", "patch", or "fs" categories. This block type is intended 
    /// for more flexible and extensible operations that may require custom handling by executors, and it allows for a wide range 
    /// of potential use cases beyond basic file manipulations.
    /// </summary>
    Request,
}
