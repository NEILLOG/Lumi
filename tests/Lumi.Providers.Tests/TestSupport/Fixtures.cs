namespace Lumi.Providers.Tests.TestSupport;

internal static class Fixtures
{
    private static string Dir => Path.Combine(AppContext.BaseDirectory, "Fixtures", "anthropic");

    public static byte[] Sse(string name) => File.ReadAllBytes(Path.Combine(Dir, name + ".sse"));

    public static byte[] HttpError(string statusFileName) =>
        File.ReadAllBytes(Path.Combine(Dir, "http-errors", statusFileName + ".json"));
}
