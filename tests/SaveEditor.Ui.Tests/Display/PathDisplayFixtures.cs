using SaveEditor.Ui.Display;

namespace SaveEditor.Ui.Tests.Display;

/// <summary>
/// Shared vocabulary for the A13 tests.
/// </summary>
/// <remarks>
/// Every character is written as a numeric cast rather than an escape sequence so that
/// these files stay pure ASCII. A test file about invisible reordering characters is the
/// last place an invisible reordering character should be able to hide.
/// </remarks>
internal static class PathDisplayFixtures
{
    /// <summary>U+202E RIGHT-TO-LEFT OVERRIDE. The filename-spoofing character.</summary>
    internal const char RightToLeftOverride = (char)0x202E;

    /// <summary>U+202B RIGHT-TO-LEFT EMBEDDING.</summary>
    internal const char RightToLeftEmbedding = (char)0x202B;

    /// <summary>U+200F RIGHT-TO-LEFT MARK.</summary>
    internal const char RightToLeftMark = (char)0x200F;

    /// <summary>U+2066 LEFT-TO-RIGHT ISOLATE.</summary>
    internal const char LeftToRightIsolate = (char)0x2066;

    /// <summary>U+2068 FIRST STRONG ISOLATE. The formatter's own opening wrapper.</summary>
    internal const char FirstStrongIsolate = (char)0x2068;

    /// <summary>U+2069 POP DIRECTIONAL ISOLATE. The formatter's own closing wrapper.</summary>
    internal const char PopDirectionalIsolate = (char)0x2069;

    /// <summary>U+FFFD REPLACEMENT CHARACTER. What a neutralized character becomes.</summary>
    internal const char Replacement = (char)0xFFFD;

    /// <summary>U+2026 HORIZONTAL ELLIPSIS. What an elided middle component becomes.</summary>
    internal const char Ellipsis = (char)0x2026;

    /// <summary>U+200D ZERO WIDTH JOINER. Deliberately not neutralized.</summary>
    internal const char ZeroWidthJoiner = (char)0x200D;

    /// <summary>A formatter that splits and renders Windows-style, on any platform.</summary>
    internal static PathDisplayFormatter Windows { get; } =
        new(new PathDisplayOptions { SeparatorStyle = PathSeparatorStyle.Windows });

    /// <summary>A formatter that splits and renders POSIX-style, on any platform.</summary>
    internal static PathDisplayFormatter Posix { get; } =
        new(new PathDisplayOptions { SeparatorStyle = PathSeparatorStyle.Posix });

    /// <summary>
    /// Strips the outer isolate pair so an assertion can name the visible text directly.
    /// </summary>
    /// <remarks>
    /// A test-only convenience. Nothing in the framework unwraps a label, and the
    /// unwrapped value is still display text, never a path.
    /// </remarks>
    internal static string Inner(string isolated) =>
        isolated.Length >= 2 &&
        isolated[0] == FirstStrongIsolate &&
        isolated[^1] == PopDirectionalIsolate
            ? isolated[1..^1]
            : isolated;

    /// <summary>Builds a Hebrew word from code points, without embedding one in the source.</summary>
    internal static string Hebrew() => new([(char)0x05E9, (char)0x05DE, (char)0x05D9, (char)0x05E8, (char)0x05D4]);
}
