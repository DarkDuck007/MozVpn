using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Moz_Avalonia.Models;

namespace Moz_Avalonia.Services;

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly System.Threading.SemaphoreSlim FileLock = new(1, 1);
    private readonly string _path;

    public SettingsStore()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _path = System.IO.Path.Combine(root, "MozVpn", "avalonia-settings.json");
    }

    public string Path => _path;

    public async Task<AppSettings> LoadAsync()
    {
        await FileLock.WaitAsync();
        try
        {
            for (int i = 0; i < 5; i++)
            {
                try
                {
                    if (!File.Exists(_path))
                        return new AppSettings();
                    await using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    return await JsonSerializer.DeserializeAsync<AppSettings>(stream, JsonOptions) ?? new AppSettings();
                }
                catch (IOException)
                {
                    if (i == 4) throw;
                    await Task.Delay(100);
                }
            }
            return new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task SaveAsync(AppSettings settings)
    {
        await FileLock.WaitAsync();
        try
        {
            var directory = System.IO.Path.GetDirectoryName(_path)!;
            Directory.CreateDirectory(directory);
            var temporaryPath = _path + ".tmp";

            for (int i = 0; i < 5; i++)
            {
                try
                {
                    await using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
                        await JsonSerializer.SerializeAsync(stream, settings, JsonOptions);
                    File.Move(temporaryPath, _path, true);
                    break;
                }
                catch (IOException)
                {
                    if (i == 4) throw;
                    await Task.Delay(100);
                }
            }
        }
        finally
        {
            FileLock.Release();
        }
    }
}
