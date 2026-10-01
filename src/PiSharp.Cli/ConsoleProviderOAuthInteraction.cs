namespace PiSharp.Cli;

/// <summary>Collects provider OAuth choices and prints progress without exposing credential material.</summary>
public sealed class ConsoleProviderOAuthInteraction : IProviderOAuthInteraction
{
    public Task<string> SelectLoginMethodAsync(IReadOnlyList<ProviderOAuthLoginMethod> methods,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Console.Error.WriteLine("Select an OAuth login method:");
        for (var i = 0; i < methods.Count; i++)
            Console.Error.WriteLine($"  {i + 1}. {methods[i].Label}");
        Console.Error.Write($"Login method [1]: ");
        var input = ReadLine();
        if (string.IsNullOrWhiteSpace(input)) return Task.FromResult(methods[0].Id);
        if (int.TryParse(input, out var index) && index >= 1 && index <= methods.Count)
            return Task.FromResult(methods[index - 1].Id);
        var match = methods.FirstOrDefault(method => method.Id.Equals(input, StringComparison.OrdinalIgnoreCase));
        if (match is not null) return Task.FromResult(match.Id);
        throw new ArgumentException($"Unknown OAuth login method '{input}'.");
    }

    public void Notify(ProviderOAuthNotice notice)
    {
        switch (notice.Kind)
        {
            case "auth_url":
                Console.Error.WriteLine($"Authorization URL: {notice.Url}");
                Console.Error.WriteLine(notice.Message);
                break;
            case "device_code":
                Console.Error.WriteLine(notice.Message);
                Console.Error.WriteLine($"Open {notice.Url} and enter {notice.UserCode}.");
                Console.Error.WriteLine($"The code expires in {notice.ExpiresInSeconds ?? 900} seconds.");
                break;
            default:
                Console.Error.WriteLine(notice.Message);
                if (notice.Url is not null) Console.Error.WriteLine(notice.Url);
                break;
        }
    }

    public Task<string> PromptForCodeAsync(string message, string placeholder,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Console.Error.WriteLine(message);
        if (!string.IsNullOrEmpty(placeholder)) Console.Error.WriteLine($"Expected format: {placeholder}");
        Console.Error.Write("Authorization code: ");
        var input = ReadLine(echo: false);
        return Task.FromResult(input ?? throw new EndOfStreamException("No OAuth authorization code was provided."));
    }

    private static string? ReadLine(bool echo = true)
    {
        if (Console.IsInputRedirected) return Console.ReadLine();
        var value = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.Error.WriteLine();
                return value.ToString();
            }
            if (key.Key == ConsoleKey.Escape) throw new OperationCanceledException("OAuth login cancelled.");
            if (key.Key == ConsoleKey.Backspace)
            {
                if (value.Length == 0) continue;
                value.Length--;
                if (echo) Console.Error.Write("\b \b");
                continue;
            }
            if (key.KeyChar == '\0' || char.IsControl(key.KeyChar)) continue;
            value.Append(key.KeyChar);
            if (echo) Console.Error.Write(key.KeyChar);
        }
    }
}
