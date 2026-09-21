using static Exergism.DeploymentAttestation.Agent.AgentConstants;

namespace Exergism.DeploymentAttestation.Agent;

internal static class PublishedWorktreePermissions
{
    internal static readonly UnixFileMode DIRECTORY_MODE =
        UnixFileMode.UserRead |
        UnixFileMode.UserWrite |
        UnixFileMode.UserExecute |
        UnixFileMode.GroupRead |
        UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead |
        UnixFileMode.OtherExecute;

    internal static readonly UnixFileMode REGULAR_FILE_MODE =
        UnixFileMode.UserRead |
        UnixFileMode.UserWrite |
        UnixFileMode.GroupRead |
        UnixFileMode.OtherRead;

    internal static readonly UnixFileMode EXECUTABLE_FILE_MODE =
        DIRECTORY_MODE;

    internal static UnixFileMode ExpectedFileMode(string gitMode)
        => gitMode switch
        {
            GIT_MODE_FILE => REGULAR_FILE_MODE,
            GIT_MODE_EXECUTABLE => EXECUTABLE_FILE_MODE,
            _ => throw new AgentException($"Unsupported published Git mode: {gitMode}")
        };

    internal static void ApplyFileMode(
        string path,
        string relativePath,
        string gitMode)
    {
        var expected = ExpectedFileMode(gitMode);
        File.SetUnixFileMode(path, expected);
        EnsureFileMode(relativePath, gitMode, File.GetUnixFileMode(path));
    }

    internal static void ApplyDirectoryMode(string path)
    {
        File.SetUnixFileMode(path, DIRECTORY_MODE);
        EnsureDirectoryMode(path, File.GetUnixFileMode(path));
    }

    internal static void EnsureFileMode(
        string relativePath,
        string gitMode,
        UnixFileMode actual)
    {
        var expected = ExpectedFileMode(gitMode);
        if (actual != expected)
            throw new AgentException(
                $"Published file mode mismatch: {relativePath}; actual={Octal(actual)} expected={Octal(expected)}");
    }

    internal static void EnsureDirectoryMode(
        string path,
        UnixFileMode actual)
    {
        if (actual != DIRECTORY_MODE)
            throw new AgentException(
                $"Published directory mode mismatch: {path}; actual={Octal(actual)} expected={Octal(DIRECTORY_MODE)}");
    }

    private static string Octal(UnixFileMode mode)
        => Convert.ToString((int)mode, 8);
}
