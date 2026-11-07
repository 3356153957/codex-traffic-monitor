namespace CodexTraffic;

public readonly record struct TrafficClassification(TrafficKind Kind, string Source);

/// <summary>
/// Classifies only controller evidence saved with the interval. IP ranges, DNS
/// names, a local proxy port, and the user's current policy cannot prove routing.
/// </summary>
public static class TrafficClassifier
{
    private static readonly HashSet<string> ProxyTypes = new(StringComparer.Ordinal)
    {
        "shadowsocks", "shadowsocksr", "ss", "ssr", "snell", "socks", "socks4", "socks5", "http", "https",
        "vmess", "vless", "trojan", "hysteria", "hysteria2", "hy2", "tuic", "wireguard", "ssh", "anytls",
        "mieru", "trusttunnel", "naive", "naiveproxy", "shadowtls"
    };
    private static readonly HashSet<string> NonProxyNames = new(StringComparer.Ordinal)
    {
        "reject", "rejectdrop", "pass", "passrule", "rematch", "compatible", "dns", "dnsout", "selector", "select", "urltest", "fallback",
        "loadbalance", "relay", "group", "proxygroup", "proxy", "auto", "自动选择", "代理", "选择", "负载均衡", "故障转移"
    };

    public static TrafficClassification Classify(string? proxyType, string? node, string? chain)
    {
        string type = Normalize(proxyType);
        if (type.Length > 0)
        {
            if (IsDirect(type)) return new(TrafficKind.Direct, "代理控制器报告的 Direct 出口类型");
            if (ProxyTypes.Contains(type)) return new(TrafficKind.Proxy, "代理控制器报告的出口类型：" + proxyType!.Trim());
            return new(TrafficKind.Unknown, "出口类型不是已确认的代理或直连类型：" + proxyType!.Trim());
        }
        string leaf = string.IsNullOrWhiteSpace(node) ? GeoIpHints.NodeFromChain(chain ?? "") : node.Trim();
        string normalizedLeaf = Normalize(leaf);
        if (IsDirect(normalizedLeaf)) return new(TrafficKind.Direct, "记录时的 DIRECT / 直连节点报告");
        if (normalizedLeaf.Length == 0 || NonProxyNames.Contains(normalizedLeaf))
            return new(TrafficKind.Unknown, "没有可确认出口类型或代理叶节点的报告");
        // Older app versions saved Mihomo's resolved leaf node but not /proxies
        // type. Keep this evidence visible instead of treating it as typed proof.
        return new(TrafficKind.Proxy, "历史节点 / 链路报告（未记录出口类型）");
    }

    public static void Apply(TargetRow target)
    {
        var classification = Classify(target.ProxyType, target.ProxyNode, target.ProxyChain);
        target.TrafficKind = classification.Kind;
        target.TrafficKindSource = classification.Source;
    }

    public static string Label(TrafficKind kind) => kind switch { TrafficKind.Proxy => "代理", TrafficKind.Direct => "直连", _ => "未确认" };
    private static bool IsDirect(string normalized) => normalized is "direct" or "直连";
    private static string Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? "" : new string(value.Trim().Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
