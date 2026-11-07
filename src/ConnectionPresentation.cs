using System.Net;

namespace CodexTraffic;

// A missing proxy report does not prove a direct route. Node labels and destination
// geography describe different evidence and must never be inferred from each other.
public static class ConnectionPresentation
{
    public static string RegionLabel(TargetRow row)
    {
        if (!string.IsNullOrWhiteSpace(row.Region)) return row.Region;
        if (HasDomainOnlyDestination(row)) return "目标 IP 未提供";
        string route = DestinationRoute(row);
        if (route.Contains("回环", StringComparison.Ordinal)) return "本机地址";
        if (route.Contains("虚拟", StringComparison.Ordinal)) return "虚拟地址";
        if (route.Contains("私有", StringComparison.Ordinal)) return "局域网地址";
        if (route.Contains("保留", StringComparison.Ordinal)) return "保留地址";
        if (row.RegionSource.Contains("未找到", StringComparison.Ordinal)) return "地区库未就绪";
        return "暂无地区数据";
    }

    public static string RegionDetails(TargetRow row)
    {
        if (!string.IsNullOrWhiteSpace(row.RegionSource)) return row.RegionSource;
        if (HasDomainOnlyDestination(row)) return "代理提供了目标域名，但没有返回真实目标 IP，无法按 IP 查询目标地区；代理节点名称不代表目标服务器所在地。";
        string route = DestinationRoute(row);
        if (route.Contains("回环", StringComparison.Ordinal)) return "这是本机回环地址，不适用公网地区；未关联到代理报告时无法知道外部目标地区。";
        if (route.Contains("虚拟", StringComparison.Ordinal)) return "这是代理虚拟 IP，不是真实服务器地址；尚无可用于查询的目标公网 IP。";
        if (route.Contains("私有", StringComparison.Ordinal) || route.Contains("保留", StringComparison.Ordinal)) return "该地址不适用公网地区查询。";
        return "已有目标地址，但没有可用的地区查询结果。地区库只提供 IP 归属参考。";
    }

    public static string NodeLabel(TargetRow row)
    {
        string node = string.IsNullOrWhiteSpace(row.ProxyNode) ? GeoIpHints.NodeFromChain(row.ProxyChain) : row.ProxyNode;
        if (!string.IsNullOrWhiteSpace(node)) return node.Equals("DIRECT", StringComparison.OrdinalIgnoreCase) ? "直连" : node;
        if (HasProxyReport(row)) return "代理未返回节点";
        if (DestinationRoute(row).Contains("回环", StringComparison.Ordinal)) return "本机连接 · 未关联";
        return "未记录节点";
    }

    public static string NodeDetails(TargetRow row)
    {
        if (!string.IsNullOrWhiteSpace(row.ProxyChain)) return "代理报告链：" + row.ProxyChain;
        if (!string.IsNullOrWhiteSpace(row.ProxyNode)) return "代理报告的节点：" + row.ProxyNode;
        if (HasProxyReport(row)) return "已取得代理目标报告，但报告没有提供节点链。";
        return "记录时没有匹配的代理节点报告。可能是本机服务、未经过可读取的代理，或连接在代理轮询前已经结束；这不代表已确认直连。历史缺失不能按当前节点补回。";
    }

    public static string NodeContext(TargetRow row)
    {
        var chain = row.ProxyChain.Split(" → ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return chain.Length > 1 ? "策略组：" + chain[^2] : "";
    }

    private static bool HasProxyReport(TargetRow row) => row.ProxyDestination.Length > 0 || row.DomainSource.Contains("代理", StringComparison.Ordinal);
    private static bool HasDomainOnlyDestination(TargetRow row) => row.ProxyDestination.Length > 0 && !IPEndPoint.TryParse(row.ProxyDestination, out _);
    private static string DestinationRoute(TargetRow row)
    {
        string address = IPEndPoint.TryParse(row.ProxyDestination, out var endpoint) ? endpoint.Address.ToString() : row.Address;
        return IPAddress.TryParse(address, out var ip) ? AddressClassifier.Classify(ip) : "";
    }
}
