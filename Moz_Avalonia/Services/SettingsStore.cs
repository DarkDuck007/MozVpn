using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Moz_Avalonia.Models;

namespace Moz_Avalonia.Services;

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path;

    public SettingsStore()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _path = System.IO.Path.Combine(root, "MozVpn", "avalonia-settings.json");
    }

    public string Path => _path;

    public async Task<AppSettings> LoadAsync()
    {
        try
        {
            if (!File.Exists(_path))
                return new AppSettings();
            await using var stream = File.OpenRead(_path);
            return await JsonSerializer.DeserializeAsync<AppSettings>(stream, JsonOptions) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public async Task SaveAsync(AppSettings settings)
    {
        var directory = System.IO.Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = _path + ".tmp";
        await using (var stream = File.Create(temporaryPath))
            await JsonSerializer.SerializeAsync(stream, settings, JsonOptions);
        File.Move(temporaryPath, _path, true);
    }
}
