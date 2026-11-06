using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using PCL.Core.App.Configuration;
using PCL.Core.App.Configuration.Storage;
using PCL.Core.App.IoC;
using PCL.Core.IO;
using PCL.Core.Utils;

namespace PCL.Core.App.Essentials;

[LifecycleService(LifecycleState.BeforeLoading, Priority = -2134567890)]
[LifecycleScope("single-instance", "单例", false)]
public sealed partial class SingleInstanceService
{
    private static FileStream? _lockStream;
    private static readonly string _LockFilePath = Path.Combine(Paths.SharedLocalData, "instance.lock");

    /// <summary>
    /// 当前进程是否为额外启动的第二个（及以后的）窗口。第二个窗口不会持有单例锁，也不会接管命令行参数。
    /// </summary>
    public static bool IsSecondaryInstance { get; private set; }

    private static void _TryRpc(string processId, string content)
    {
        var pipeName = $"{RpcService.PipePrefix}@{processId}";
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
        pipe.Connect(1000);
        using var sw = new StreamWriter(pipe, PipeComm.PipeEncoding);
        sw.WriteLine(content);
        sw.Write(PipeComm.PipeEndingChar);
        sw.Flush();
    }

    [LifecycleStart]
    private static void _Start()
    {
        // 使用 --new-window 启动参数时允许额外开启窗口，不再受单例限制
        if (Basics.IsSecondaryWindow)
        {
            IsSecondaryInstance = true;
            Context.Info("检测到多窗口启动参数，本进程将作为第二个窗口运行");
            return;
        }

        try
        {
            var stream = File.Open(_LockFilePath, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
            Context.Debug("未发现重复实例，正在向单例锁写入信息");
            using var sw = new StreamWriter(stream, Encoding.ASCII, 8, true);
            sw.Write(Basics.CurrentProcessId);
            sw.Flush();
            _lockStream = stream;
        }
        catch (Exception)
        {
            // 已有一个实例在运行
            // 若本次为直接启动（无额外命令行参数）且设置中允许启动第二个窗口，
            // 则本进程直接作为新窗口运行，不再转交命令并退出
            if (Basics.CommandLineArguments.Length == 0 && _IsSecondWindowAllowed())
            {
                IsSecondaryInstance = true;
                Context.Info("设置中允许启动第二个窗口，本进程将作为第二个窗口运行");
                return;
            }

            try
            {
                using var stream = File.Open(_LockFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                var pid = reader.ReadToEnd();
                Context.Info($"发现重复实例 {pid}，尝试传递参数并拉起主窗口");
                try
                {
                    _TryRpc(pid, "REQ cli\n" + JsonSerializer.Serialize(StartupService.UnhandledCommands, JsonCompat.SerializerOptions));
                    _TryRpc(pid, "REQ activate");
                }
                catch (Exception ex) { Context.Warn("RPC 通信失败", ex); }
            }
            catch (Exception ex) { Context.Error("读取单例锁出错", ex); }
            finally { Context.RequestExit(1); }
        }
    }

    /// <summary>
    /// 是否允许在已有实例运行的情况下直接打开新的窗口。<br/>
    /// 单例检查先于配置服务执行，因此这里直接读取本地配置文件；读取失败时使用默认值（允许）。
    /// </summary>
    private static bool _IsSecondWindowAllowed()
    {
        try
        {
            if (!File.Exists(ConfigService.LocalConfigPath)) return true;
            var provider = new YamlFileProvider(ConfigService.LocalConfigPath);
            return !provider.Exists("UiAllowSecondWindow") || provider.Get<bool>("UiAllowSecondWindow");
        }
        catch (Exception ex)
        {
            Context.Warn("读取「允许启动第二个窗口」设置失败，使用默认值", ex);
            return true;
        }
    }

    [LifecycleStop]
    private static void _Stop()
    {
        if (_lockStream is null) return;
        Context.Debug("正在删除单例锁");
        _lockStream.Dispose();
        File.Delete(_LockFilePath);
    }
}
