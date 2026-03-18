namespace Dx.Core;

public sealed class DxLock : IDisposable
{
    private readonly FileStream _stream;

    private DxLock(FileStream stream) => _stream = stream;

    public static DxLock Acquire(string root)
    {
        var path = Path.Combine(root, ".dx", "lock");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        try
        {
            var stream = new FileStream(
                path,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
            return new DxLock(stream);
        }
        catch (IOException)
        {
            throw new DxException(DxError.PendingTransactionOnOtherSession,
                "Session is locked by another process.");
        }
    }

    public void Dispose()
    {
        _stream.Dispose();
    }
}
