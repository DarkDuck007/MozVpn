using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using MozUtil.NatUtils;

namespace Moz_Avalonia.Services;

public static class ResourceCatalog
{
    public static IReadOnlyList<string> Load(string suffix)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var name = assembly.GetManifestResourceNames().Single(x => x.EndsWith(suffix));
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd()
            .Split('\n')
            .Select(x => x.Trim())
            .Where(x => x.Length > 0 && !x.StartsWith('#') && !x.StartsWith("//", StringComparison.Ordinal))
            .Distinct()
            .ToArray();
    }

    public static IReadOnlyList<MozServerDescriptor> LoadServers()
    {
        return Load("ServerList.txt")
            .Select(MZRoamProtocol.ParseServerLine)
            .Where(x => x is not null)
            .Select(x => x!)
            .ToArray();
    }
}
