using System.Diagnostics;

namespace ScreenCastReceiver.Helpers;

/// <summary>
/// 防火墙辅助类：
/// - 生成可复制的 netsh 防火墙命令文本（非管理员模式展示）
/// - 一键提权执行（需要管理员权限）
/// </summary>
public static class FirewallHelper
{
    /// <summary>为可执行程序放行入站 TCP/UDP 的 netsh 命令（防火墙区域提示用）。</summary>
    public static string BuildNetshCommand(string exePath, int[] tcpPorts, int[] udpPorts)
    {
        var ruleName = "ScreenCastReceiver";
        var lines = new List<string>
        {
            $"netsh advfirewall firewall delete rule name=\"{ruleName}\"",
            $"netsh advfirewall firewall add rule name=\"{ruleName}\" program=\"{exePath}\" dir=in action=allow protocol=TCP enable=yes"
        };
        if (udpPorts.Length > 0)
        {
            lines.Add($"netsh advfirewall firewall add rule name=\"{ruleName}-UDP\" program=\"{exePath}\" dir=in action=allow protocol=UDP enable=yes");
        }
        return string.Join(Environment.NewLine + "  &&  ", lines);
    }

    /// <summary>一键添加防火墙规则（提权运行 netsh）。返回是否成功与输出信息。</summary>
    public static (bool Success, string Output) TryAddRule(string exePath, int[] tcpPorts, int[] udpPorts)
    {
        var command = BuildNetshCommand(exePath, tcpPorts, udpPorts);
        try
        {
            // 逐条执行，避免一次 && 长命令在某些系统上解析失败
            var commands = command.Split("  &&  ", StringSplitOptions.RemoveEmptyEntries);
            var allOk = true;
            var output = "";
            foreach (var c in commands)
            {
                var psi = new ProcessStartInfo("netsh")
                {
                    Arguments = c.Substring("netsh ".Length),
                    Verb = "runas",          // 提升为管理员（触发 UAC）
                    UseShellExecute = true,
                    CreateNoWindow = true
                };
                var proc = Process.Start(psi);
                if (proc == null) { allOk = false; continue; }
                proc.WaitForExit(30000);
                if (proc.ExitCode != 0) allOk = false;
            }
            output = allOk ? "防火墙规则添加成功" : "部分规则添加失败，请检查输出";
            return (allOk, output);
        }
        catch (Exception ex)
        {
            // 用户取消 UAC 或非管理员
            return (false, $"添加失败（可能未授权管理员）: {ex.Message}");
        }
    }
}
