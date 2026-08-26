namespace SaveEditor.Ui.Interaction;

/// <summary>
/// Text that originated outside the framework — typically produced by a codec
/// from the contents of a save file — and must be sanitized before display.
/// </summary>
/// <remarks>
/// <para>
/// Validation messages and unknown-data warnings are derived from attacker-
/// controlled bytes and are rendered inside dialogs whose accept action can
/// overwrite a real save. A crafted file can otherwise emit text that imitates
/// framework chrome ("Integrity verified. Safe to continue.") or uses bidi
/// overrides to reverse a displayed path, talking the user past a destructive
/// outcome.
/// </para>
/// <para>
/// This type exists so that untrusted text is distinguishable in the type system
/// rather than by convention. The framework's own title, framing sentence, and
/// accept label are plain strings and are never sourced from a codec.
/// </para>
/// </remarks>
/// <param name="Value">The raw, unsanitized text as supplied.</param>
public readonly record struct UntrustedText(string Value)
{
    /// <summary>Returns the raw value. Sanitization happens at render time, not here.</summary>
    public override string ToString() => Value;
}
