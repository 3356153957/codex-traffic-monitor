using System.Globalization;
using System.IO;
using System.Net;
using MaxMind.Db;

namespace CodexTraffic;

public readonly record struct GeoHint(string Region, string Source);

// Country data is read from the user's existing local database. No lookup API,
// downloads, subscription files, or assumptions based on a proxy node's name.
public sealed class GeoIpHints : IDisposable
{
    private readonly object _gate = new();
    private readonly Reader? _reader;
    private readonly Dictionary<string, GeoHint> _cache = new();
    public bool IsAvailable => _reader != null;

    public GeoIpHints()
    {
        foreach (var path in DatabasePaths())
        {
            try
            {
                if (!File.Exists(path)) continue;
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                _reader = new Reader(stream);
                break;
            }
            catch { /* Optional, missing or incompatible local database. */ }
        }
    }

    private static IEnumerable<string> DatabasePaths()
    {
        yield return Path.Combine(StartupManager.DataDirectory, "geoip", "Country.mmdb");
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        yield return Path.Combine(appData, "io.github.clash-verge-rev.clash-verge-rev", "Country.mmdb");
        yield return Path.Combine(appData, "io.github.clash-verge-rev.clash-verge", "Country.mmdb");
    }

    public GeoHint Lookup(string address)
    {
        if (!IPAddress.TryParse(address, out var ip) || AddressClassifier.Classify(ip) != "公网地址") return default;
        lock (_gate)
        {
            if (_cache.TryGetValue(address, out var previous)) return previous;
            GeoHint result = new("", _reader == null ? "本机未找到可用的 GeoIP 国家库" : "本机 GeoIP 库没有该公网 IP 的地区记录");
            try
            {
                var row = _reader?.Find<Dictionary<string, object>>(ip);
                if (row != null)
                {
                    var country = Dictionary(row, "country") ?? Dictionary(row, "registered_country");
                    if (country != null && country.TryGetValue("iso_code", out var isoObject) && isoObject is string iso)
                    {
                        string name = CountryName(iso);
                        var names = Dictionary(country, "names");
                        if (names != null && names.TryGetValue("zh-CN", out var localized) && localized is string chinese) name = chinese + " · " + iso.ToUpperInvariant();
                        result = name.Length > 0 ? new(name, "本机 GeoIP 国家库（IP 归属参考）")
                            : new("网络：" + iso, "本机 GeoIP 库的网络标签，非地理位置");
                    }
                }
            }
            catch { }
            if (_cache.Count >= 8192) _cache.Clear();
            _cache[address] = result;
            return result;
        }
    }

    private static Dictionary<string, object>? Dictionary(Dictionary<string, object> source, string key)
        => source.TryGetValue(key, out var value) ? value as Dictionary<string, object> : null;

    public static string CountryName(string code)
    {
        code = code.Trim().ToUpperInvariant();
        if (code.Length != 2 || !code.All(char.IsAsciiLetter)) return "";
        string name = code switch
        {
            "US" => "美国", "CN" => "中国", "HK" => "中国香港", "TW" => "中国台湾", "MO" => "中国澳门",
            "JP" => "日本", "SG" => "新加坡", "GB" => "英国", "DE" => "德国", "FR" => "法国",
            "NL" => "荷兰", "CA" => "加拿大", "AU" => "澳大利亚", "KR" => "韩国", "IN" => "印度",
            _ => ""
        };
        if (name.Length == 0) try { name = new RegionInfo(code).EnglishName; } catch { return code; }
        return name + " · " + code;
    }

    public static string NodeFromChain(string chain) => string.IsNullOrWhiteSpace(chain) ? "" : chain.Split(" → ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "";

    public void Enrich(TargetRow target)
    {
        if (target.ProxyNode.Length == 0) target.ProxyNode = NodeFromChain(target.ProxyChain);
        if (target.Region.Length > 0) return;
        string address = target.Address;
        if (target.ProxyDestination.Length > 0)
        {
            if (IPEndPoint.TryParse(target.ProxyDestination, out var destination)) address = destination.Address.ToString();
            else
            {
                target.RegionSource = "代理只提供目标域名，未返回真实目标 IP；无法查询目标地区，节点名称不代表目标服务器所在地。";
                return;
            }
        }
        var geo = Lookup(address);
        target.Region = geo.Region ?? ""; target.RegionSource = geo.Source ?? "";
        if (target.RegionSource.Length == 0) target.RegionSource = ConnectionPresentation.RegionDetails(target);
    }

    public void Dispose() { lock (_gate) _reader?.Dispose(); }
}
