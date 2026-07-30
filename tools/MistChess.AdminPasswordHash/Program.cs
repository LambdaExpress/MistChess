using Microsoft.AspNetCore.Identity;

if (Console.IsInputRedirected)
{
    Console.Error.WriteLine("Interactive input is required so the password is not exposed through command arguments or a pipe.");
    return 1;
}

Console.TreatControlCAsInput = true;
Console.Error.Write("Administrator password: ");
var password = ReadSecret();
if (password.Length == 0)
{
    Console.Error.WriteLine("The password cannot be empty.");
    return 1;
}

Console.Error.Write("Confirm password: ");
var confirmation = ReadSecret();
if (!string.Equals(password, confirmation, StringComparison.Ordinal))
{
    Console.Error.WriteLine("The passwords do not match.");
    return 1;
}

var hasher = new PasswordHasher<object>();
Console.WriteLine(hasher.HashPassword(new object(), password));
return 0;

static string ReadSecret()
{
    var characters = new List<char>();
    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter)
        {
            Console.Error.WriteLine();
            return new string([.. characters]);
        }

        if (key.Key == ConsoleKey.Backspace)
        {
            if (characters.Count > 0)
            {
                characters.RemoveAt(characters.Count - 1);
            }
            continue;
        }

        if (key.Key == ConsoleKey.C && key.Modifiers.HasFlag(ConsoleModifiers.Control))
        {
            Console.Error.WriteLine();
            Environment.Exit(130);
        }

        if (!char.IsControl(key.KeyChar))
        {
            characters.Add(key.KeyChar);
        }
    }
}
