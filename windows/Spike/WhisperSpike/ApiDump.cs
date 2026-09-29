using System.Reflection;

namespace WhisperSpike;

/// <summary>Prints the public surface of Whisper.net.dll by reflection (step 5 of the W1 spike).</summary>
internal static class ApiDump
{
    public static void Write(TextWriter w)
    {
        var asm = typeof(Whisper.net.WhisperFactory).Assembly;
        w.WriteLine($"# {asm.GetName().Name} {asm.GetName().Version} ({asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion})");
        foreach (var t in asm.GetExportedTypes().OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            string kind = t.IsEnum ? "enum" : t.IsValueType ? "struct" : t.IsInterface ? "interface" : "class";
            w.WriteLine($"{kind} {t.FullName}");
            if (t.IsEnum)
            {
                w.WriteLine("    " + string.Join(", ", Enum.GetNames(t)));
                continue;
            }
            foreach (var m in t.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (m is MethodInfo { IsSpecialName: true })
                {
                    continue;
                }
                string s = m switch
                {
                    MethodInfo mi => $"{(mi.IsStatic ? "static " : "")}{mi.ReturnType} {mi.Name}({Params(mi)})",
                    PropertyInfo pi => $"prop {pi.PropertyType} {pi.Name} {{{(pi.CanRead ? " get;" : "")}{(pi.SetMethod?.IsPublic == true ? " set;" : "")} }}",
                    FieldInfo fi => $"field {fi.FieldType} {fi.Name}",
                    ConstructorInfo ci => $"ctor({Params(ci)})",
                    _ => $"{m.MemberType} {m.Name}",
                };
                w.WriteLine("    " + s);
            }
        }
    }

    private static string Params(MethodBase m) =>
        string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType} {p.Name}"));
}
