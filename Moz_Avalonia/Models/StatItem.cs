using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Moz_Avalonia.Models;

public partial class StatItem : ObservableObject
{
    public StatItem(string name, string value, object? rawValue = null)
    {
        Name = name;
        Value = value;
        AddSample(rawValue);
    }

    public string Name { get; }
    public IReadOnlyList<double> History { get; } = new List<double>();

    [ObservableProperty]
    private int _graphVersion;

    [ObservableProperty]
    private string _value;

    public void Update(string value, object? rawValue)
    {
        Value = value;
        AddSample(rawValue);
    }

    private void AddSample(object? rawValue)
    {
        if (rawValue is null || rawValue is bool || rawValue.GetType().IsEnum) return;
        try
        {
            var number = Convert.ToDouble(rawValue);
            if (!double.IsFinite(number)) return;
            var history = (List<double>)History;
            history.Add(number);
            if (history.Count > 60) history.RemoveAt(0);
            GraphVersion++;
        }
        catch (Exception) when (rawValue is not IConvertible) { }
        catch (FormatException) { }
        catch (InvalidCastException) { }
        catch (OverflowException) { }
    }
}
