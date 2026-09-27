namespace HuorongAce.Core.Diagnostics;

/// <summary>
/// Minimal logging abstraction.
/// </summary>
/// <remarks>
/// The Go version used the standard <c>log</c> package writing to stderr, which
/// is invisible for a <c>-H windowsgui</c> subsystem executable. C# keeps the
/// same "fire and forget" spirit but routes messages through an interface so a
/// GUI host can surface them (debug output, file, or a diagnostics window)
/// without the core taking a dependency on a logging framework.
/// </remarks>
public interface IAppLog
{
    void Info(string message);

    void Error(string message, Exception? exception = null);
}

/// <summary>Sinks messages to <see cref="System.Diagnostics.Debug"/> and the trace listeners.</summary>
public sealed class DebugAppLog : IAppLog
{
    public void Info(string message) => System.Diagnostics.Debug.WriteLine($"[info] {message}");

    public void Error(string message, Exception? exception = null) =>
        System.Diagnostics.Debug.WriteLine($"[error] {message}" + (exception is null ? "" : $" {exception}"));
}

/// <summary>Discards everything. Used by tests and as a safe default.</summary>
public sealed class NullAppLog : IAppLog
{
    public void Info(string message)
    {
    }

    public void Error(string message, Exception? exception = null)
    {
    }
}
