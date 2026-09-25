namespace PiSharp.Cli;

/// <summary>Parses one interactive session path while allowing spaces inside single/double quotes.</summary>
public static class SessionPathArgument
{
    public static string Parse(string argument)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument);
        var value = argument.Trim();
        if (value[0] is not ('\'' or '"')) return value;
        var quote = value[0];
        var closing = value.IndexOf(quote, 1);
        if (closing < 0 || !string.IsNullOrWhiteSpace(value[(closing + 1)..]))
            throw new ArgumentException("Quote a path containing spaces and provide one path only.", nameof(argument));
        var path = value[1..closing];
        if (path.Length == 0) throw new ArgumentException("Session path cannot be empty.", nameof(argument));
        return path;
    }
}
