using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;

namespace SaveEditor.Ui.Tests;

/// <summary>
/// Pins the shipped public surface of <c>SaveEditor.Ui</c>.
/// </summary>
/// <remarks>
/// <para>
/// <c>PLAN.md</c> §3 says the first stable release is 1.0 and that public breaking
/// changes require a major version. That is a promise nobody can keep by reading
/// diffs: a removed overload, a widened parameter, a member quietly made internal
/// are all invisible in review and all break a consumer at compile time.
/// </para>
/// <para>
/// This snapshots the surface to a committed file. Any change — addition or
/// removal — fails until the file is regenerated, which makes the change a
/// deliberate, reviewable act rather than a side effect. Additions are fine at any
/// version; the point is that they are *noticed*.
/// </para>
/// </remarks>
public class PublicApiSurfaceTests
{
    private static string BaselinePath([CallerFilePath] string callerPath = "")
    {
        var directory = Directory.GetParent(callerPath);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PLAN.md")))
        {
            directory = directory.Parent;
        }

        return Path.Combine(
            directory?.FullName ?? throw new InvalidOperationException("Repository root not found."),
            "eng",
            "PublicApi.SaveEditor.Ui.txt");
    }

    [Fact]
    public void The_Public_Surface_Matches_Its_Committed_Baseline()
    {
        var actual = DescribeSurface(typeof(Ui.Shell.EditorShell).Assembly);
        var path = BaselinePath();

        if (Environment.GetEnvironmentVariable("SAVEEDITOR_UPDATE_PUBLIC_API") is "1" or "true")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, actual);
            return;
        }

        Assert.True(
            File.Exists(path),
            $"No committed public-API baseline. Generate it with SAVEEDITOR_UPDATE_PUBLIC_API=1.");

        var expected = File.ReadAllText(path).ReplaceLineEndings("\n");

        if (string.Equals(expected, actual.ReplaceLineEndings("\n"), StringComparison.Ordinal))
        {
            return;
        }

        // Report the actual difference rather than "files differ" — the whole value is
        // in seeing which member moved.
        var expectedLines = expected.Split('\n').ToHashSet(StringComparer.Ordinal);
        var actualLines = actual.ReplaceLineEndings("\n").Split('\n').ToHashSet(StringComparer.Ordinal);

        var removed = expectedLines.Except(actualLines).OrderBy(l => l, StringComparer.Ordinal).ToList();
        var added = actualLines.Except(expectedLines).OrderBy(l => l, StringComparer.Ordinal).ToList();

        var message = new StringBuilder("The public surface changed.\n");

        if (removed.Count > 0)
        {
            message.Append("\nREMOVED — these break existing consumers and need a major version:\n");
            foreach (var line in removed)
            {
                message.Append("  - ").Append(line).Append('\n');
            }
        }

        if (added.Count > 0)
        {
            message.Append("\nADDED — additive, but confirm it is intended surface:\n");
            foreach (var line in added)
            {
                message.Append("  + ").Append(line).Append('\n');
            }
        }

        message.Append("\nIf intended, regenerate with SAVEEDITOR_UPDATE_PUBLIC_API=1 and review the diff.");
        Assert.Fail(message.ToString());
    }

    /// <summary>Renders the public surface deterministically.</summary>
    /// <remarks>
    /// Ordinal-sorted so the output does not depend on reflection order, which is not
    /// guaranteed stable across runtimes and would otherwise produce phantom diffs.
    /// </remarks>
    private static string DescribeSurface(Assembly assembly)
    {
        var lines = new List<string>();

        foreach (var type in assembly.GetExportedTypes())
        {
            lines.Add($"type {type.FullName}");

            foreach (var member in type.GetMembers(
                         BindingFlags.Public | BindingFlags.Instance |
                         BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                // Compiler-generated accessors and record plumbing are noise: they
                // change with syntax rather than with contract.
                if (member is MethodInfo { IsSpecialName: true }
                    or MethodInfo { Name: "<Clone>$" or "PrintMembers" or "Deconstruct" })
                {
                    continue;
                }

                // Qualified with the declaring type: sorting is what makes the file
                // stable, but an unqualified "ctor ()" line tells a reviewer nothing
                // about which type just lost a constructor.
                lines.Add($"{type.FullName} :: {Describe(member)}");
            }
        }

        lines.Sort(StringComparer.Ordinal);
        return string.Join('\n', lines) + '\n';
    }

    private static string Describe(MemberInfo member) => member switch
    {
        PropertyInfo p => $"property {Name(p.PropertyType)} {p.Name}"
                          + (p.CanRead ? " get" : string.Empty)
                          + (p.CanWrite ? " set" : string.Empty),
        FieldInfo f => $"field {Name(f.FieldType)} {f.Name}",
        EventInfo e => $"event {Name(e.EventHandlerType!)} {e.Name}",
        ConstructorInfo c => $"ctor ({Parameters(c.GetParameters())})",
        MethodInfo m => $"method {Name(m.ReturnType)} {m.Name}({Parameters(m.GetParameters())})",
        Type t => $"nested {t.Name}",
        _ => member.ToString() ?? member.Name,
    };

    private static string Parameters(ParameterInfo[] parameters) =>
        string.Join(", ", parameters.Select(p => $"{Name(p.ParameterType)} {p.Name}"));

    private static string Name(Type type)
    {
        if (!type.IsGenericType)
        {
            return type.Name;
        }

        // Not every generic type carries a backtick — nested and constructed types can
        // arrive without one, and assuming otherwise throws rather than degrading.
        var tick = type.Name.IndexOf('`', StringComparison.Ordinal);
        var stem = tick >= 0 ? type.Name[..tick] : type.Name;

        return $"{stem}<{string.Join(", ", type.GetGenericArguments().Select(Name))}>";
    }
}
