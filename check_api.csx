
using System;
using System.Reflection;
using Avalonia.Input;

class Program {
    static void Main() {
        var t = typeof(DataTransfer);
        Console.WriteLine("=== DataTransfer ===");
        foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            Console.WriteLine($"  {m.Name}({string.Join(", ", Array.ConvertAll(m.GetParameters(), p => p.ParameterType.Name + " " + p.Name))})");
        foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            Console.WriteLine($"  Prop: {p.Name} : {p.PropertyType.Name}");
        
        Console.WriteLine("\n=== IClipboard ===");
        var ic = typeof(Avalonia.Input.Platform.IClipboard);
        foreach (var m in ic.GetMethods())
            Console.WriteLine($"  {m.Name}({string.Join(", ", Array.ConvertAll(m.GetParameters(), p => p.ParameterType.Name + " " + p.Name))})");
        
        Console.WriteLine("\n=== DataFormat ===");
        var df = typeof(DataFormat);
        foreach (var f in df.GetFields(BindingFlags.Public | BindingFlags.Static))
            Console.WriteLine($"  {f.Name} = {f.GetValue(null)}");
    }
}
