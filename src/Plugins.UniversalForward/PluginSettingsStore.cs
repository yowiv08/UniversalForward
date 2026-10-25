using System.Text.Json;
using System.Text.Json.Serialization;

namespace Plugins.UniversalForward;

internal sealed record ExecutionSettings([property: JsonNumberHandling(JsonNumberHandling.Strict)] int DefaultHostAttempts)
{
    internal void Validate()
    {
        if (DefaultHostAttempts is < 1 or > 35)
            throw new FormatException("宿主默认尝试次数必须为 1–35（含首次）。");
    }
}

internal sealed record AppearanceSettings(string Mode)
{
    internal void Validate()
    {
        if (Mode is not ("host" or "light" or "dark")) throw new FormatException("外观模式必须为 host、light 或 dark。");
    }
}

/// <summary>设置位于工作目录的数据区，独立于插件更新时替换的程序集目录。</summary>
internal sealed class PluginSettingsStore(string directory)
{
    internal SettingsFile<ExecutionSettings> Execution { get; } = new(
        Path.Combine(directory, "execution-settings.json"), new(1), value => value.Validate());
    internal SettingsFile<AppearanceSettings> Appearance { get; } = new(
        Path.Combine(directory, "appearance-settings.json"), new("host"), value => value.Validate());
}

internal sealed class SettingsFile<T> where T : class
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _path;
    private readonly Action<T> _validate;
    private T _value;
    internal T Value => Volatile.Read(ref _value);
    internal string? LoadError { get; private set; }

    internal SettingsFile(string path, T fallback, Action<T> validate)
    {
        _path = path; _validate = validate; _value = fallback;
        try
        {
            var value = JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json)
                ?? throw new FormatException("设置内容不能为空。");
            validate(value);
            _value = value;
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or FormatException)
        { LoadError = "已保存的插件设置读取失败，当前使用默认值；请重新保存设置。"; }
    }

    // 调用者串行化写入；先完成磁盘替换，再发布新的内存快照。
    internal async Task SaveAsync(T value, CancellationToken cancellationToken)
    {
        _validate(value);
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(file, value, Json, cancellationToken);
                await file.FlushAsync(cancellationToken);
                file.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, _path, overwrite: true);
            Volatile.Write(ref _value, value);
            LoadError = null;
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }
}
