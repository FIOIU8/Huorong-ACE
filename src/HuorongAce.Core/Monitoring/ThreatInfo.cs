namespace HuorongAce.Core.Monitoring;

/// <summary>
/// A threat Huorong processed, surfaced to the UI.
/// </summary>
/// <param name="Name">Virus name reported by Huorong, e.g. <c>Trojan/MEMZ.n</c>.</param>
/// <param name="Detail">Human readable detail, normally the quarantined file path.</param>
/// <param name="Timestamp">When Huorong recorded it.</param>
/// <remarks>
/// C# counterpart of the Go <c>Info</c> struct. It is an immutable record, so
/// the value handed to the UI can never be mutated underneath it — the Go
/// version passed a mutable struct by value and relied on callers copying it.
/// </remarks>
public sealed record ThreatInfo(string Name, string Detail, DateTimeOffset Timestamp)
{
    public static ThreatInfo Simulated(string name) =>
        new(name, "（模拟测试触发）本程序为娱乐用途，并非真实反作弊系统。", DateTimeOffset.Now);
}
