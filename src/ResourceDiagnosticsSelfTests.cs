namespace CodexTraffic;

internal static class ResourceDiagnosticsSelfTests
{
    internal static IEnumerable<string> Run()
    {
        if (ResourceSampler.CalculateCpuPercent(2, 1, 4) != 50 ||
            ResourceSampler.CalculateCpuPercent(-1, 1, 4) != 0 ||
            ResourceSampler.CalculateCpuPercent(9, 1, 4) != 100)
            throw new InvalidOperationException("CPU normalization or bounds failed.");
        yield return "Resource CPU uses all logical processors and clamps invalid intervals";
        var sampler = new ResourceSampler();
        var first = sampler.Read();
        var second = sampler.Read();
        if (first.CpuPercent != null || second.CpuPercent is not (>= 0 and <= 100) ||
            second.WorkingSetBytes <= 0 || second.PrivateBytes <= 0 || second.ManagedHeapBytes <= 0)
            throw new InvalidOperationException("Live resource sample failed.");
        yield return "Live self resource samples expose memory without forcing a collection";
    }
}
