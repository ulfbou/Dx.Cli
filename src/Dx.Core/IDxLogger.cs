namespace Dx.Core;

public interface IDxLogger
{
    void Info(string message);
    void Warn(string message);
    void Error(string message);
    void Debug(string message);
}

public sealed class NullDxLogger : IDxLogger
{
    public static readonly NullDxLogger Instance = new();
    public void Info(string _) { }
    public void Warn(string _) { }
    public void Error(string _) { }
    public void Debug(string _) { }
}

public sealed class ConsoleDxLogger(bool verbose = false) : IDxLogger
{
    public void Info(string message) => Console.Error.WriteLine($"  {message}");
    public void Warn(string message) => Console.Error.WriteLine($"  warn: {message}");
    public void Error(string message) => Console.Error.WriteLine($"  error: {message}");
    public void Debug(string message) { if (verbose) Console.Error.WriteLine($"  debug: {message}"); }
}
