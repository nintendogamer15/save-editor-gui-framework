using System.Text.Json;

namespace SaveEditor.Ui.Settings;

/// <summary>
/// A structural pre-pass over the settings bytes, run before any object is constructed.
/// </summary>
/// <remarks>
/// <para>
/// The deserializer is already closed and already bounded, so this pass is not what
/// makes the read safe on its own. It exists because the deserializer's own limits are
/// coarse: <c>MaxDepth</c> is enforced, but there is no maximum string length and no
/// maximum array length, and a file claiming a hundred thousand recents would otherwise
/// be materialized in full and only then truncated to ten. Truncating after allocation
/// is not a bound.
/// </para>
/// <para>
/// It also produces a specific reason. "The file was malformed" and "the file carried a
/// type discriminator" call for the same fail-soft handling but are not the same event,
/// and only one of them means somebody wrote that file on purpose.
/// </para>
/// </remarks>
internal static class SettingsJsonScanner
{
    /// <summary>Serializer metadata names, refused wherever they appear.</summary>
    private static readonly string[] MetadataPropertyNames = ["$type", "$id", "$ref", "$values"];

    /// <summary>Screens the bytes.</summary>
    /// <param name="utf8">The raw file contents.</param>
    /// <param name="limits">Structural limits to enforce.</param>
    /// <param name="detail">Operator-facing detail when a limit is exceeded.</param>
    /// <returns><see cref="SettingsRejection.None"/> when the bytes are structurally acceptable.</returns>
    internal static SettingsRejection Scan(
        ReadOnlySpan<byte> utf8,
        SettingsStructuralLimits limits,
        out string detail)
    {
        detail = string.Empty;

        var readerOptions = new JsonReaderOptions
        {
            // Headroom over the limit under test, so the scanner reaches its own
            // depth check and names the violation instead of the reader throwing a
            // generic parse error the caller would have to guess the meaning of. The
            // reader's own MaxDepth stays as the backstop, and the real limit is
            // enforced again on the reader the deserializer runs against.
            MaxDepth = limits.MaxDepth + 8,
            CommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false,
        };

        var reader = new Utf8JsonReader(utf8, readerOptions);

        // One entry per open container: -1 for an object, otherwise the number of
        // elements seen so far in an array.
        var containers = new List<int>(limits.MaxDepth + 1);

        try
        {
            while (reader.Read())
            {
                if (reader.CurrentDepth > limits.MaxDepth)
                {
                    detail = $"The document nests deeper than {limits.MaxDepth} levels.";
                    return SettingsRejection.DepthExceeded;
                }

                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject:
                        if (!CountElement(containers, limits, out detail))
                        {
                            return SettingsRejection.ArrayTooLong;
                        }

                        containers.Add(-1);
                        break;

                    case JsonTokenType.StartArray:
                        if (!CountElement(containers, limits, out detail))
                        {
                            return SettingsRejection.ArrayTooLong;
                        }

                        containers.Add(0);
                        break;

                    case JsonTokenType.EndObject:
                    case JsonTokenType.EndArray:
                        if (containers.Count > 0)
                        {
                            containers.RemoveAt(containers.Count - 1);
                        }

                        break;

                    case JsonTokenType.PropertyName:
                        {
                            var rejection = ScreenName(ref reader, limits, out detail);
                            if (rejection != SettingsRejection.None)
                            {
                                return rejection;
                            }

                            break;
                        }

                    case JsonTokenType.String:
                        if (RawLength(ref reader) > limits.MaxStringLength)
                        {
                            detail = $"A string value is longer than {limits.MaxStringLength} bytes.";
                            return SettingsRejection.StringTooLong;
                        }

                        if (!CountElement(containers, limits, out detail))
                        {
                            return SettingsRejection.ArrayTooLong;
                        }

                        break;

                    default:
                        if (!CountElement(containers, limits, out detail))
                        {
                            return SettingsRejection.ArrayTooLong;
                        }

                        break;
                }
            }
        }
        catch (JsonException ex)
        {
            detail = $"The document is not well-formed JSON: {ex.Message}";
            return SettingsRejection.MalformedJson;
        }

        return SettingsRejection.None;
    }

    private static SettingsRejection ScreenName(
        ref Utf8JsonReader reader,
        SettingsStructuralLimits limits,
        out string detail)
    {
        detail = string.Empty;

        if (RawLength(ref reader) > limits.MaxStringLength)
        {
            detail = $"A property name is longer than {limits.MaxStringLength} bytes.";
            return SettingsRejection.StringTooLong;
        }

        foreach (var metadata in MetadataPropertyNames)
        {
            if (reader.ValueTextEquals(metadata))
            {
                detail = $"The document carries the serializer metadata property '{metadata}'.";
                return SettingsRejection.TypeDiscriminator;
            }
        }

        // Escaped forms such as "$type" decode through GetString, so the prefix
        // check has to run on the decoded name rather than on the raw bytes. No
        // legitimate settings property begins with a dollar sign.
        var name = reader.GetString();
        if (name is not null && name.StartsWith('$'))
        {
            detail = $"The document carries a serializer metadata property '{name}'.";
            return SettingsRejection.TypeDiscriminator;
        }

        return SettingsRejection.None;
    }

    /// <summary>
    /// Raw encoded length of the current string token, in bytes.
    /// </summary>
    /// <remarks>
    /// Escapes only ever shrink on decode, so the raw length is an upper bound on the
    /// decoded length and refusing on it refuses no earlier than necessary — while
    /// avoiding decoding a hostile string in order to measure it.
    /// </remarks>
    private static long RawLength(ref Utf8JsonReader reader) =>
        reader.HasValueSequence ? reader.ValueSequence.Length : reader.ValueSpan.Length;

    private static bool CountElement(List<int> containers, SettingsStructuralLimits limits, out string detail)
    {
        detail = string.Empty;

        if (containers.Count == 0)
        {
            return true;
        }

        var top = containers[^1];
        if (top < 0)
        {
            return true;
        }

        top++;
        if (top > limits.MaxArrayElements)
        {
            detail = $"An array claims more than {limits.MaxArrayElements} elements.";
            return false;
        }

        containers[^1] = top;
        return true;
    }
}

/// <summary>Structural limits applied to the settings file before it is deserialized.</summary>
/// <param name="MaxDepth">Deepest permitted nesting.</param>
/// <param name="MaxStringLength">Longest permitted string or property name, in raw bytes.</param>
/// <param name="MaxArrayElements">Largest permitted array.</param>
internal readonly record struct SettingsStructuralLimits(
    int MaxDepth,
    int MaxStringLength,
    int MaxArrayElements);
