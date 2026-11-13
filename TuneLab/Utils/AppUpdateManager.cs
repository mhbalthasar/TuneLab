using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TuneLab.Foundation;

namespace TuneLab.Utils;

public class UpdateInfo
{
    public Version? version;
    // 面向用户的下载页（用浏览器打开），语义自 1.x 起未变：可能是 release 页面而非安装包。
    public string? url;
    // 安装器直链，供自动更新直接下载。服务端按平台匹配得到才给；给不出即该平台没有可自更新的包。
    public string? installerUrl;
    public string? description;
    public DateTime publishedAt;
}

internal static class AppUpdateManager
{
    private static readonly string storageFile = Path.Combine(PathManager.ConfigsFolder, "UpdateIgnoreVersion.txt");

    // 更新检查的服务端地址。默认正式地址；设环境变量 TUNELAB_API_BASE 可指向本地/预发布做测试。
    private static string ApiBase =>
        Environment.GetEnvironmentVariable("TUNELAB_API_BASE") is { Length: > 0 } b ? b : "https://api.tunelab.app";

    public static async Task<UpdateInfo?> CheckForUpdate(bool ignoreVersion = true)
    {
        var queryParams = new Dictionary<string, object>
            {
                { "platform", PlatformHelper.GetPlatform() }
            };

        var response = await new HttpClient(ApiBase).GetAsync("/api/app/get-update", queryParams);

        if (!response.IsSuccessful)
        {
            throw new Exception(response.ErrorMessage);
        }

        var data = Newtonsoft.Json.JsonConvert.DeserializeObject<UpdateInfo>(response.Content);

        if (data == null)
        {
            throw new Exception("CheckUpdateFailed");
        }

        // 服务端未给版本号（字段缺失/解析失败）视为无更新，避免后续以 null 版本比较/落盘触发 NRE。
        if (data.version == null || data.version <= AppInfo.Version)
        {
            return null;
        }

        // 读忽略版本（文件不存在即未忽略过任何版本，无需预先创建）。
        if (ignoreVersion && File.Exists(storageFile))
        {
            try
            {
                var ignored = File.ReadAllText(storageFile);
                if (Version.TryParse(ignored, out var ignoredVersion) && ignoredVersion == data.version)
                    return null;
            }
            catch (Exception ex)
            {
                Log.Warning($"Failed to read update ignore file: {ex.Message}");
            }
        }

        return data;
    }

    public static void SaveIgnoreVersion(Version version)
    {
        try
        {
            Directory.CreateDirectory(PathManager.ConfigsFolder);
            File.WriteAllText(storageFile, version.ToString());
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to save update ignore version: {ex.Message}");
        }
    }

    /// <summary>
    /// 下载整包安装器到临时目录，按 Content-Length 回报进度（0–1）。返回下载到的文件路径。
    /// 内容不是 Windows 可执行文件时抛 <see cref="InvalidDataException"/>，由调用方降级为「浏览器打开下载页」。
    /// </summary>
    public static async Task<string> DownloadInstallerAsync(string url, IProgress<double>? progress, CancellationToken ct)
    {
        var dir = Path.Combine(Path.GetTempPath(), "TuneLab.Update");
        Directory.CreateDirectory(dir);
        var destPath = Path.Combine(dir, GetInstallerFileName(url));

        using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        using var resp = await http.GetAsync(url, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        // 网页当安装器下载是真实发生过的事故（链接指向 release 页面、CDN/代理错误页都是 200 + HTML）。
        // 先按 Content-Type 挡一道，下载完再验一次文件头，两道都不信任服务端给的链接。
        var mediaType = resp.Content.Headers.ContentType?.MediaType;
        if (mediaType != null && mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Expected an installer but the server returned '{mediaType}': {url}");

        long? total = resp.Content.Headers.ContentLength;
        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        await using (var dst = File.Create(destPath))
        {
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                if (total is > 0)
                    progress?.Report((double)done / total.Value);
            }
        }

        if (!IsWindowsExecutable(destPath))
        {
            // 留着只会在退出时被 shell 按扩展名打开（曾表现为「选择一个应用以打开此 .1 文件」），直接删掉。
            try { File.Delete(destPath); } catch (Exception ex) { Log.Warning($"Failed to delete invalid installer: {ex.Message}"); }
            throw new InvalidDataException($"Downloaded file is not a Windows executable: {url}");
        }

        return destPath;
    }

    /// <summary>
    /// 拉起下载好的安装器进入静默更新模式：覆盖当前安装目录并重启 TuneLab。
    /// 调用方随后应退出本进程以释放文件锁（安装器会等锁释放）。
    /// 返回是否已拉起：文件在下载后被换掉/损坏时返回 false，调用方应改为让用户手动下载。
    /// </summary>
    public static bool LaunchInstallerUpdate(string installerPath)
    {
        // 下载时已验过一次；这里再验，防的是落盘之后文件被替换或损坏——
        // 绝不把不是程序的东西交给 shell 执行。
        if (!IsWindowsExecutable(installerPath))
        {
            Log.Error($"Refused to launch a non-executable installer: {installerPath}");
            return false;
        }

        // [EP-PACKAGE] Windows 安装器改为 Inno Setup：用它的静默升级参数覆盖到原安装目录并重启。
        // 自研安装器（TuneLab-Setup 的 classic 产物）保留，但不再作为自动更新的目标。
        var installDir = AppDomain.CurrentDomain.BaseDirectory
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        ProcessHelper.CreateProcess(installerPath, ["/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/DIR=" + installDir]);
        return true;
    }

    /// <summary>
    /// 文件头是否为 PE 的 "MZ" 魔数。用于拒绝把网页/错误页当安装器执行。
    /// </summary>
    static bool IsWindowsExecutable(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return stream.ReadByte() == 'M' && stream.ReadByte() == 'Z';
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to inspect installer file: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 取安装器落盘用的文件名。只认 URL 末段本身就是 .exe 的情况，
    /// 否则用固定名兜底——URL 末段当文件名会带出无扩展名/怪扩展名（如 "v2.0.1" → 扩展名 ".1"）。
    /// </summary>
    static string GetInstallerFileName(string url)
    {
        const string fallback = "TuneLab-Setup.exe";
        try
        {
            var name = Path.GetFileName(new Uri(url).AbsolutePath);
            return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : fallback;
        }
        catch
        {
            return fallback;
        }
    }
}
