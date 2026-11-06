using System;
using System.Collections.Generic;
using System.Numerics;
using System.Security.Cryptography;
using PCL.Core.App;
using PCL.Core.IO.Net.SocketForward;
using PCL.Core.Logging;
using PCL.Core.Minecraft.Profile;
using PCL.Core.Utils;
using PCL.Core.Utils.Exts;

namespace PCL.Core.Link.Lobby;

/// <summary>
/// 陶瓦联机大厅信息与房间号编解码。
/// </summary>
public static class LobbyInfoProvider
{
    /// <summary>
    /// 陶瓦联机中大厅创建者使用的虚拟网 IP。
    /// </summary>
    public const string HostIp = "10.144.144.1";

    /// <summary>
    /// EasyTier 网络名前缀，与陶瓦联机保持一致。
    /// </summary>
    public const string NetworkNamePrefix = "terracotta-mc-";

    private const string BaseChars = "0123456789ABCDEFGHJKLMNPQRSTUVWXYZ";
    private const int BaseVal = 34;
    private const int DataLength = 24;
    private const int PortMin = 100;
    private const int PortMax = 65535;

    public static bool IsLobbyAvailable { get; set; } = true;
    public static bool AllowCustomName { get; set; } = true;

    public static BroadcastLocal? McBroadcast { get; internal set; }
    public static TcpForwardWorker? McForward { get; internal set; }

    public class LobbyInfo
    {
        public required string OriginalCode { get; init; }
        public required LobbyType Type { get; init; }
        public required string NetworkName { get; init; }
        public required string NetworkSecret { get; init; }

        /// <summary>
        /// 远程 IP 地址，需要先解析大厅类型再填充
        /// </summary>
        public string? Ip { get; init; }

        /// <summary>
        /// 目标游戏端口
        /// </summary>
        public required int Port { get; init; }
    }

    public enum LobbyType
    {
        Terracotta
    }

    /// <summary>
    /// 目标大厅
    /// </summary>
    public static LobbyInfo? TargetLobby { get; set; }
    public static int JoinerLocalPort { get; set; }

    /// <summary>
    /// 解析陶瓦大厅编号，并返回 LobbyInfo 对象。若解析失败则返回 null。
    /// </summary>
    public static LobbyInfo? ParseCode(string code)
    {
        code = code.Trim().ToUpper();
        if (string.IsNullOrWhiteSpace(code) || !code.IsASCII())
        {
            LogWrapper.Error("Link", "无效的大厅编号: " + code);
            return null;
        }

        var matches = code.RegexSearch(RegexPatterns.TerracottaId);
        if (matches.Count == 0)
        {
            LogWrapper.Error("Link", "大厅编号解析失败，可能是无效的陶瓦大厅编号: " + code);
            return null;
        }

        foreach (var match in matches)
        {
            var codeString = match.Replace("I", "1").Replace("O", "0").Replace("-", "");
            if (codeString.Length != DataLength + 1) continue;

            BigInteger value = 0;
            var checking = 0;
            var valid = true;
            for (var i = 0; i < DataLength; i++)
            {
                var j = BaseChars.IndexOf(codeString[i]);
                if (j < 0)
                {
                    valid = false;
                    break;
                }

                value += BigInteger.Parse(j.ToString()) * BigInteger.Pow(BaseVal, i);
                checking = (j + checking) % BaseVal;
            }

            if (!valid) continue;
            if (checking != BaseChars.IndexOf(codeString[DataLength])) continue;

            var port = (int)(value % 65536);
            if (port < PortMin) continue;

            return new LobbyInfo
            {
                OriginalCode = _Format(codeString),
                NetworkName = codeString[..15].ToLower(),
                NetworkSecret = codeString.Substring(15, 10).ToLower(),
                Port = port,
                Type = LobbyType.Terracotta,
                Ip = HostIp
            };
        }

        LogWrapper.Error("Link", "大厅编号解析失败，可能是无效的陶瓦大厅编号: " + code);
        return null;
    }

    /// <summary>
    /// 依据 Minecraft 局域网端口生成一个新的陶瓦大厅编号。
    /// </summary>
    /// <param name="port">Minecraft 局域网开放端口。</param>
    public static LobbyInfo Generate(int port)
    {
        if (port is < PortMin or > PortMax)
            throw new ArgumentOutOfRangeException(nameof(port), port, $"大厅端口需要在 {PortMin}~{PortMax} 之间");

        // 高位随机生成，低位用于承载端口信息
        var digits = new int[DataLength];
        for (var i = 4; i < DataLength; i++)
            digits[i] = RandomNumberGenerator.GetInt32(BaseVal);

        BigInteger highValue = 0;
        for (var i = 4; i < DataLength; i++)
            highValue += BigInteger.Pow(BaseVal, i) * digits[i];

        // 使最终数值对 65536 取余后正好等于端口
        var target = (int)(((port - highValue) % 65536 + 65536) % 65536);
        var maxLow = 0;
        for (var i = 0; i < 4; i++) maxLow += (BaseVal - 1) * (int)Math.Pow(BaseVal, i);

        var candidates = new List<int>();
        for (var value = target; value <= maxLow; value += 65536) candidates.Add(value);
        var lowValue = candidates[RandomNumberGenerator.GetInt32(candidates.Count)];

        for (var i = 0; i < 4; i++) digits[i] = lowValue / (int)Math.Pow(BaseVal, i) % BaseVal;

        var checking = 0;
        var raw = new char[DataLength + 1];
        for (var i = 0; i < DataLength; i++)
        {
            raw[i] = BaseChars[digits[i]];
            checking = (digits[i] + checking) % BaseVal;
        }

        raw[DataLength] = BaseChars[checking];

        var codeString = new string(raw);

        return new LobbyInfo
        {
            OriginalCode = _Format(codeString),
            NetworkName = codeString[..15].ToLower(),
            NetworkSecret = codeString.Substring(15, 10).ToLower(),
            Port = port,
            Type = LobbyType.Terracotta,
            Ip = HostIp
        };
    }

    /// <summary>
    /// 获取用于联机显示的用户名
    /// </summary>
    public static string? GetUsername() => AllowCustomName && !string.IsNullOrWhiteSpace(Config.Link.Username)
        ? Config.Link.Username
        : ProfileService.Current?.UserName;

    private static string _Format(string codeString) =>
        string.Join("-",
            codeString[..5],
            codeString.Substring(5, 5),
            codeString.Substring(10, 5),
            codeString.Substring(15, 5),
            codeString.Substring(20, 5));
}
