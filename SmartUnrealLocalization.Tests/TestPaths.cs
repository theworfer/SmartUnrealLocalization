using System.Runtime.CompilerServices;

namespace SmartUnrealLocalization.Tests;

/// <summary>
/// Locating the repository from a test.
///
/// <para>Several guards read the app's SOURCE rather than call into it, because
/// the view model needs Avalonia to instantiate. They used to find the repo by
/// walking up from <c>AppContext.BaseDirectory</c>, which ties them to the
/// default <c>bin/Debug/net8.0</c> layout: build to any other output path — as
/// you must when the app is running and holding a lock on its own DLL — and
/// every one of those guards fails for a reason that has nothing to do with the
/// code under test.</para>
///
/// <para><see cref="CallerFilePathAttribute"/> is baked in at compile time, so
/// it points at the source tree whatever the output path is.</para>
/// </summary>
internal static class TestPaths
{
    private static string? _root;

    public static string RepoRoot()
    {
        if (_root is not null) return _root;

        var dir = new DirectoryInfo(Path.GetDirectoryName(ThisFile())!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SmartUnrealLocalization.csproj")))
            dir = dir.Parent;
        if (dir is null)
            throw new DirectoryNotFoundException(
                "SmartUnrealLocalization.csproj not found above " + ThisFile());
        return _root = dir.FullName;
    }

    public static string In(params string[] parts) => Path.Combine(new[] { RepoRoot() }.Concat(parts).ToArray());

    private static string ThisFile([CallerFilePath] string path = "") => path;
}
