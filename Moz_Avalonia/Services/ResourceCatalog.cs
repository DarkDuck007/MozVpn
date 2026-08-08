using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace Moz_Avalonia.Services;

public static class ResourceCatalog
{
    public static IReadOnlyList<string> Load(string suffix)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var name = assembly.GetManifestResourceNames().Single(x => x.EndsWith(suffix));
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0).Distinct().ToArray();
    }
}
