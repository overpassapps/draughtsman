using System.Diagnostics;
using System.Text;

namespace Draughtsman.Server.KeyRing;

/// <summary>A key-encryption key read from its source, with anything worth telling the operator (never the key).</summary>
internal sealed record KeyEncryptionKeyLookup(byte[] Key, bool Created, IReadOnlyList<string> Notes);

/// <summary>Where a key-encryption key (see <see cref="KeyEncryptionKey"/>) comes from: somewhere outside the data folder.</summary>
internal interface IKeyEncryptionKeySource
{
    /// <summary>Names the variable, file or store for the startup line. Never a value.</summary>
    string Description { get; }

    /// <summary>Reads the key. Throws <see cref="KeyEncryptionKeyException"/> with a plain reason when it cannot (absent, unreadable, malformed).</summary>
    KeyEncryptionKeyLookup Load();
}

/// <summary>The key-encryption key in an environment variable, the Docker/Kubernetes-friendly source.</summary>
internal sealed class EnvironmentKeySource(string variable, Func<string, string?> environment) : IKeyEncryptionKeySource
{
    public string Description => $"key-encryption key from the environment variable {variable}";

    public KeyEncryptionKeyLookup Load()
    {
        string? value = environment(variable);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new KeyEncryptionKeyException($"The environment variable {variable} is not set.");
        }

        return new KeyEncryptionKeyLookup(KeyEncryptionKey.Parse(value), false, []);
    }
}

/// <summary>
/// The key-encryption key in a file outside the data folder (a Docker secret, or a root-only file on a host). A file
/// inside the data folder is refused: it would sit right beside the ring it protects, which is the exposure this
/// feature exists to close.
/// </summary>
internal sealed class FileKeySource(string path, string dataFolder) : IKeyEncryptionKeySource
{
    public string Description => $"key-encryption key from the file {path}";

    public KeyEncryptionKeyLookup Load()
    {
        string full = Path.GetFullPath(path);
        if (IsInside(full, dataFolder))
        {
            throw new KeyEncryptionKeyException($"The key file {path} is inside the data folder, next to the key ring it would protect. Move it elsewhere (a Docker secret, or a root-only file).");
        }

        string text;
        try
        {
            text = File.ReadAllText(full);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new KeyEncryptionKeyException($"The key file {path} could not be read ({ex.GetType().Name}).");
        }

        var notes = new List<string>();
        if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(full) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
        {
            notes.Add($"The key file {path} is readable by other users; restrict it to its owner (chmod 600).");
        }

        return new KeyEncryptionKeyLookup(KeyEncryptionKey.Parse(text), false, notes);
    }

    /// <summary>Whether <paramref name="path"/> is the folder or inside it, resolving a symbolic link on the file itself so a link placed outside cannot hide a target inside.</summary>
    internal static bool IsInside(string path, string folder)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;
        StringComparison comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        string? target = null;
        try
        {
            target = new FileInfo(path).ResolveLinkTarget(returnFinalTarget: true)?.FullName;
        }
        catch (IOException)
        {
            // An unresolvable link is read as itself; reading it will fail with its own message.
        }

        return path.StartsWith(root, comparison) || (target is not null && target.StartsWith(root, comparison));
    }
}

/// <summary>
/// The key-encryption key in the macOS Keychain, through the system <c>security</c> tool. A new key is generated on
/// first use and stored under an account id recorded beside the ring (<see cref="KeyRingFiles.IdentityFileName"/>),
/// so the ring is tied to this Mac's Keychain rather than to the folder's path: moved on this Mac it still opens,
/// copied to another machine it does not.
/// <para>
/// The secret reaches <c>security</c> on its standard input (<c>security -i</c>), never on the command line where
/// <c>ps</c> would show it. A Keychain item is readable by any process of the same user that can run <c>security</c>,
/// so this protects the data folder on its own (a copy, a backup, a volume), not a hostile process on the same login.
/// </para>
/// </summary>
internal sealed class MacKeychainKeySource(string account, string service = MacKeychainKeySource.DefaultService, string securityTool = MacKeychainKeySource.SecurityTool, TimeSpan? timeout = null, bool createIfMissing = true) : IKeyEncryptionKeySource
{
    public const string DefaultService = "Draughtsman data key ring";

    internal const string SecurityTool = "/usr/bin/security";

    /// <summary><c>security</c>'s exit status for "the specified item could not be found in the keychain".</summary>
    private const int ItemNotFound = 44;

    /// <summary>How long one <c>security</c> call may take. It has hung for good with no login keychain (a service account, <c>HOME</c> unset or missing), so every wait on it is bounded.</summary>
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);

    public string Description => $"key-encryption key in the macOS Keychain (item \"{service}\", account {account})";

    public KeyEncryptionKeyLookup Load()
    {
        (int exit, string output) = Run(["find-generic-password", "-a", account, "-s", service, "-w"], null);
        if (exit == 0)
        {
            return new KeyEncryptionKeyLookup(KeyEncryptionKey.Parse(output), false, []);
        }

        if (exit != ItemNotFound)
        {
            // Locked keychain, no GUI session, access denied: anything but "not there" is not a reason to mint a new key.
            throw new KeyEncryptionKeyException($"The macOS Keychain could not be read (security exited with status {exit}). Is the login keychain unlocked?");
        }

        if (!createIfMissing)
        {
            // A check must not change the Keychain: an item that is not there is reported, not made.
            throw new KeyEncryptionKeyException("The macOS Keychain has no item for this key ring.");
        }

        string generated = KeyEncryptionKey.Generate();
        // -i reads its commands from stdin, so the new secret is never an argument. -U updates an item that appears between the find and this add.
        string command = $"add-generic-password -a {Quote(account)} -s {Quote(service)} -l {Quote("Draughtsman data key ring")} -w {generated} -U\n";
        (int addExit, _) = Run(["-i"], command);
        (int checkExit, string stored) = Run(["find-generic-password", "-a", account, "-s", service, "-w"], null);
        if (addExit != 0 || checkExit != 0 || stored.Trim() != generated)
        {
            throw new KeyEncryptionKeyException("A key-encryption key could not be stored in the macOS Keychain.");
        }

        return new KeyEncryptionKeyLookup(KeyEncryptionKey.Parse(generated), true, []);
    }

    /// <summary>Removes the item; used by tests and by an operator resetting a machine. True when there was one to remove.</summary>
    public bool Delete()
    {
        (int exit, _) = Run(["delete-generic-password", "-a", account, "-s", service], null);
        return exit == 0;
    }

    private static string Quote(string value)
    {
        return "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    private (int Exit, string Output) Run(string[] arguments, string? standardInput)
    {
        var start = new ProcessStartInfo(securityTool)
        {
            RedirectStandardInput = standardInput is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            UseShellExecute = false,
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        TimeSpan limit = timeout ?? DefaultTimeout;
        using Process process = Process.Start(start) ?? throw new KeyEncryptionKeyException("The macOS security tool could not be started.");
        if (standardInput is not null)
        {
            process.StandardInput.Write(standardInput);
            process.StandardInput.Close();
        }

        // Both pipes are drained on the side, and nothing here blocks on them: a synchronous ReadToEnd would wait for the
        // tool's end of file for ever when it hangs, long before the wait below could give up.
        Task<string> error = process.StandardError.ReadToEndAsync();
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        if (!process.WaitForExit(limit))
        {
            process.Kill(entireProcessTree: true);
            throw new KeyEncryptionKeyException($"The macOS security tool did not answer within {limit.TotalSeconds:0} seconds.");
        }

        // The tool has exited, but a child it left running could still hold the pipe open: bound this wait as well.
        if (!output.Wait(limit))
        {
            process.Kill(entireProcessTree: true);
            throw new KeyEncryptionKeyException($"The macOS security tool did not finish writing its answer within {limit.TotalSeconds:0} seconds.");
        }

        _ = error.Wait(limit);
        return (process.ExitCode, output.Result);
    }
}
