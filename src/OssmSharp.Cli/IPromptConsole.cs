namespace OssmSharp.Cli;

/// <summary>
/// Renders the interactive prompt and keeps partially typed input intact while results are written.
/// </summary>
internal interface IPromptConsole
{
    /// <summary>
    /// Gets the result writer; each complete line redraws partially typed input.
    /// </summary>
    TextWriter Output { get; }

    /// <summary>
    /// Gets the diagnostic writer; each complete line redraws partially typed input.
    /// </summary>
    TextWriter Error { get; }

    /// <summary>
    /// Gets the success colour for <see cref="Output"/>, or an empty string when colour is disabled.
    /// </summary>
    string Success { get; }

    /// <summary>
    /// Gets the warning colour for <see cref="Output"/>, or an empty string when colour is disabled.
    /// </summary>
    string Warning { get; }

    /// <summary>
    /// Gets the failure colour for <see cref="Error"/>, or an empty string when colour is disabled.
    /// </summary>
    string Failure { get; }

    /// <summary>
    /// Gets the colour reset for <see cref="Output"/>, or an empty string when colour is disabled.
    /// </summary>
    string OutputReset { get; }

    /// <summary>
    /// Gets the colour reset for <see cref="Error"/>, or an empty string when colour is disabled.
    /// </summary>
    string ErrorReset { get; }

    /// <summary>
    /// Starts a line of input and shows the prompt.
    /// </summary>
    void BeginRead();

    /// <summary>
    /// Adds a typed character to the current input.
    /// </summary>
    /// <param name="character">The typed character.</param>
    void Append(char character);

    /// <summary>
    /// Removes the last typed character, if any.
    /// </summary>
    void Backspace();

    /// <summary>
    /// Ends the current line of input.
    /// </summary>
    void EndRead();

    /// <summary>
    /// Writes a complete line to <paramref name="target"/>, then redraws the prompt and partially typed input.
    /// </summary>
    /// <param name="target">The underlying writer.</param>
    /// <param name="value">The line to write.</param>
    void WriteLine(TextWriter target, string value);
}
