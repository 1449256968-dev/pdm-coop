using System;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

/// <summary>Validate relay endpoints without blocking DNS in the UI.</summary>
public static class CoopEndpoint
{
    public static bool TryParse(string address, string portText, out string host, out ushort port, out string error)
    {
        host = (address ?? "").Trim(); port = 0; error = null;
        if (host.Length == 0) return Fail("请填写服务器域名或 IP。", out error);
        if (host.IndexOf("://", StringComparison.Ordinal) >= 0 || host.IndexOfAny(new[] { '/', '\\', '?', '#', '@' }) >= 0)
            return Fail("请填写域名或 IP，可附带 :端口；不要填写网页链接。", out error);
        if (host.StartsWith("[", StringComparison.Ordinal))
        {
            int close = host.IndexOf(']');
            if (close < 0) return Fail("IPv6 地址的方括号不完整。", out error);
            string suffix = host.Substring(close + 1); host = host.Substring(1, close - 1);
            if (suffix.Length > 0)
            {
                if (!suffix.StartsWith(":", StringComparison.Ordinal)) return Fail("地址格式无效。", out error);
                portText = suffix.Substring(1);
            }
            if (!IPAddress.TryParse(host, out IPAddress ip) || ip.AddressFamily != AddressFamily.InterNetworkV6)
                return Fail("IPv6 地址无效。", out error);
        }
        else if (host.IndexOf(':') == host.LastIndexOf(':') && host.IndexOf(':') >= 0)
        {
            int colon = host.IndexOf(':'); portText = host.Substring(colon + 1); host = host.Substring(0, colon);
        }
        if (!ushort.TryParse((portText ?? "").Trim(), out port) || port == 0)
            return Fail("端口必须为 1–65535；樱花用户请填写远程端口。", out error);
        if (!IPAddress.TryParse(host, out _))
        {
            try { host = new IdnMapping().GetAscii(host); }
            catch (ArgumentException) { return Fail("服务器域名格式无效。", out error); }
            if (Uri.CheckHostName(host) != UriHostNameType.Dns) return Fail("服务器域名或 IP 格式无效。", out error);
        }
        return true;
    }
    private static bool Fail(string message, out string error) { error = message; return false; }
}
