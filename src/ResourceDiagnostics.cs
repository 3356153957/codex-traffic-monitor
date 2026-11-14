using System.Diagnostics;

namespace CodexTraffic;

public sealed record ResourceSample(DateTime TimestampUtc, long WorkingSetBytes, long PrivateBytes,
    double? CpuPercent, long? ManagedHeapBytes, long? AllocatedBytes, int Handles, int Threads)
{
    public double IntervalSeconds { get; init; }
}

internal sealed class ResourceSampler
{
    private readonly int _pid;
    private long _previousTimestamp;
    private TimeSpan _previousCpu;

    internal ResourceSampler(int? pid = null) => _pid = pid ?? Environment.ProcessId;

    internal ResourceSample Read()
    {
        using var process = Process.GetProcessById(_pid);
        long timestamp = Stopwatch.GetTimestamp();
        var cpu = process.TotalProcessorTime;
        double elapsed = _previousTimestamp == 0 ? 0 : (timestamp - _previousTimestamp) / (double)Stopwatch.Frequency;
        double? percent = _previousTimestamp == 0 ? null : CalculateCpuPercent(
            (cpu - _previousCpu).TotalSeconds, elapsed, Environment.ProcessorCount);
        _previousTimestamp = timestamp;
        _previousCpu = cpu;
        bool self = _pid == Environment.ProcessId;
        return new ResourceSample(DateTime.UtcNow, process.WorkingSet64, process.PrivateMemorySize64, percent,
            self ? GC.GetTotalMemory(false) : null, self ? GC.GetTotalAllocatedBytes(false) : null,
            process.HandleCount, process.Threads.Count) { IntervalSeconds = elapsed };
    }

    internal static double CalculateCpuPercent(double cpuSeconds, double wallSeconds, int processors) =>
        wallSeconds > 0 && processors > 0 ? Math.Clamp(cpuSeconds / wallSeconds / processors * 100, 0, 100) : 0;
}

public sealed class ResourceReport
{
    public string DiagnosticsVersion { get; set; } = UpdateChecker.CurrentVersion.ToString();
    public int ProcessId { get; set; }
    public int LogicalProcessors { get; set; } = Environment.ProcessorCount;
    public string CpuBasis { get; set; } = "Percent of total logical-processor capacity; first sample has no CPU interval.";
    public string MemoryBasis { get; set; } = "WorkingSetBytes is resident memory; PrivateBytes is committed private memory. Managed fields are available only for self.";
    public double DurationSeconds { get; set; }
    public double AverageCpuPercent { get; set; }
    public long PeakWorkingSetBytes { get; set; }
    public long PeakPrivateBytes { get; set; }
    public long? AllocatedBytesDuringSample { get; set; }
    public bool? CaptureRunning { get; set; }
    public long? EventsLost { get; set; }
    public List<ResourceSample> Samples { get; set; } = new();
}

public static class ResourceDiagnostics
{
    // Sampling never starts/stops capture, contacts the network or loads flow rows.
    public static async Task<ResourceReport> MeasureAsync(int pid, TimeSpan duration, CancellationToken cancellationToken = default)
    {
        if (duration < TimeSpan.FromSeconds(1) || duration > TimeSpan.FromMinutes(2))
            throw new ArgumentOutOfRangeException(nameof(duration));
        var sampler = new ResourceSampler(pid);
        var report = new ResourceReport { ProcessId = pid };
        var timer = Stopwatch.StartNew();
        report.Samples.Add(sampler.Read());
        while (timer.Elapsed < duration)
        {
            var remaining = duration - timer.Elapsed;
            if (remaining <= TimeSpan.Zero) break;
            await Task.Delay(remaining < TimeSpan.FromSeconds(1) ? remaining : TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            report.Samples.Add(sampler.Read());
        }
        report.DurationSeconds = timer.Elapsed.TotalSeconds;
        // Weight intervals: the last one may be shorter than a second.
        double weightedCpu = 0, seconds = 0;
        for (int i = 1; i < report.Samples.Count; i++)
        {
            double elapsed = report.Samples[i].IntervalSeconds;
            if (elapsed <= 0) continue;
            weightedCpu += report.Samples[i].CpuPercent.GetValueOrDefault() * elapsed;
            seconds += elapsed;
        }
        report.AverageCpuPercent = seconds > 0 ? weightedCpu / seconds : 0;
        report.PeakWorkingSetBytes = report.Samples.Max(s => s.WorkingSetBytes);
        report.PeakPrivateBytes = report.Samples.Max(s => s.PrivateBytes);
        report.AllocatedBytesDuringSample = report.Samples[^1].AllocatedBytes - report.Samples[0].AllocatedBytes;
        return report;
    }
}
