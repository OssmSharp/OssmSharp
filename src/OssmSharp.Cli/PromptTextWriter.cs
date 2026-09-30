using System.Text;

namespace OssmSharp.Cli;

internal sealed class PromptTextWriter(IPromptConsole console, TextWriter target) : TextWriter
{
    private readonly Lock sync = new();
    private readonly StringBuilder pending = new();

    public override Encoding Encoding => target.Encoding;

    public override void Write(char value)
    {
        lock (sync)
        {
            if (value == '\n')
            {
                console.WriteLine(target, pending.ToString().TrimEnd('\r'));
                pending.Clear();
            }
            else
            {
                pending.Append(value);
            }
        }
    }

    public override void Write(string? value)
    {
        lock (sync)
        {
            foreach (var character in value ?? string.Empty)
            {
                Write(character);
            }
        }
    }

    public override void WriteLine(string? value)
    {
        lock (sync)
        {
            Write(value);
            Write('\n');
        }
    }

    public override Task WriteAsync(string? value)
    {
        Write(value);
        return Task.CompletedTask;
    }

    public override Task WriteLineAsync(string? value)
    {
        WriteLine(value);
        return Task.CompletedTask;
    }

    public override void Flush()
    {
        lock (sync)
        {
            if (pending.Length > 0)
            {
                Write('\n');
            }
        }
    }
}
