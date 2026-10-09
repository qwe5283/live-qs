using System.Diagnostics;
using CommunityToolkit.Mvvm.Input;
using LiveQs.Windows.Core.Sync;
using LiveQs.Windows.Core.Update;

namespace LiveQs.Windows.ViewModels;

public sealed partial class SettingsViewModel
{
    private void OnSyncStatusChanged(object? sender, SyncStatus status) =>
        System.Windows.Application.Current.Dispatcher.Invoke(() => UpdateSyncText(status));

    private void OnUpdateStatusChanged(object? sender, UpdateStatus status) =>
        System.Windows.Application.Current.Dispatcher.Invoke(() => UpdateUpdateText(status));

    private void UpdateUpdateText(UpdateStatus status)
    {
        UpdateStatusText = !status.Enabled
            ? "更新检查已关闭"
            : status.State switch
            {
                UpdateCheckState.Idle => "尚未检查",
                UpdateCheckState.UpToDate => $"已是最新 · 当前 v{_currentVersion}",
                UpdateCheckState.Available => $"发现新版本 v{status.AvailableVersion}（当前 v{_currentVersion}），安装包已校验",
                UpdateCheckState.Incompatible => $"发现新版本 v{status.AvailableVersion}，但当前 v{_currentVersion} 低于其最低兼容版本，请先手动升级",
                _ => $"检查失败（{status.LastErrorCode}）",
            };
        var details = new List<string>();
        if (status.LastCheckedAt is { } checkedAt)
        {
            details.Add($"上次检查：{checkedAt.ToLocalTime():yyyy-MM-dd HH:mm}");
        }

        if (status.VerifiedPackagePath is { } packagePath)
        {
            details.Add($"安装包：{packagePath}");
        }

        if (!string.IsNullOrWhiteSpace(status.LastErrorMessage))
        {
            details.Add(status.LastErrorMessage);
        }

        UpdateDetailText = string.Join(Environment.NewLine, details);
    }

    [RelayCommand]
    private void OpenUpdateFolder()
    {
        try
        {
            var directory = Path.Combine(_paths.DataDirectory, "updates");
            Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{directory}\"") { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _dialogs.ShowError("无法打开目录", exception);
        }
    }

    private void UpdateSyncText(SyncStatus status)
    {
        SyncStatusText = !status.Enabled
            ? $"本地模式 · {status.PendingCount} 条待同步"
            : status.IsRunning
                ? $"正在同步 · {status.PendingCount} 条待处理"
                : string.IsNullOrWhiteSpace(status.LastError)
                    ? $"云端已连接 · {status.PendingCount} 条待处理"
                    : $"云端暂不可用 · {status.PendingCount} 条待处理";
    }

}
