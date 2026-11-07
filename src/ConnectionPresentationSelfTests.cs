namespace CodexTraffic;

public static class ConnectionPresentationSelfTests
{
    public static List<string> Run()
    {
        var checks = new List<string>();
        void Check(bool value, string name) { if (!value) throw new InvalidOperationException(name); checks.Add(name); }
        using var geo = new GeoIpHints();
        var proxyDomain = new TargetRow { Address = "127.0.0.1", Port = 7897, Hostname = "example.test", ProxyDestination = "example.test:443", ProxyNode = "日本 01" };
        geo.Enrich(proxyDomain);
        Check(proxyDomain.Region.Length == 0 && ConnectionPresentation.RegionLabel(proxyDomain) == "目标 IP 未提供" && proxyDomain.RegionSource.Contains("未返回"), "Domain-only proxy reports never infer target geography from a node name or loopback next hop");
        Check(ConnectionPresentation.RegionLabel(new TargetRow { Address = "127.0.0.1" }) == "本机地址" && ConnectionPresentation.RegionLabel(new TargetRow { Address = "198.18.1.9" }) == "虚拟地址", "Local and fake-IP destinations explain why public geography is inapplicable");
        Check(ConnectionPresentation.NodeLabel(new TargetRow { Address = "8.8.8.8" }) == "未记录节点" && ConnectionPresentation.NodeDetails(new TargetRow()).Contains("不代表已确认直连"), "A missing proxy report never claims a confirmed direct connection");
        var chain = new TargetRow { ProxyChain = "AI → 洛杉矶 → Reality" };
        Check(ConnectionPresentation.NodeLabel(chain) == "Reality" && ConnectionPresentation.NodeContext(chain) == "策略组：洛杉矶", "The reported leaf node and its parent policy group stay distinct and visible");
        Check(ConnectionPresentation.NodeLabel(new TargetRow { ProxyNode = "DIRECT" }) == "直连" && ConnectionPresentation.RegionLabel(new TargetRow { Region = "网络：GOOGLE" }) == "网络：GOOGLE", "Explicit direct reports and CDN network labels retain their original evidence");
        return checks;
    }
}
