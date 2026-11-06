using System.Text.Json.Serialization;
using LockPeek.Core;
namespace LockPeek.App;
internal sealed record DiagnosticsReport(string[] Paths, int Unsupported, ScanResult Scan, int Pid, long WorkingSet, long WindowHandle);
[JsonSerializable(typeof(DiagnosticsReport))]
internal partial class DiagnosticsJsonContext : JsonSerializerContext;
