namespace Lumi.Providers.Tests.TestSupport;

internal static class Fixtures
{
    private static string Dir => Path.Combine(AppContext.BaseDirectory, "Fixtures", "anthropic");

    public static byte[] Sse(string name) => File.ReadAllBytes(Path.Combine(Dir, name + ".sse"));

    public static byte[] AnthropicFile(string fileName) => File.ReadAllBytes(Path.Combine(Dir, fileName));

    public static byte[] OpenAISse(string name) => File.ReadAllBytes(Path.Combine(OpenAIDir, name + ".sse"));

    public static byte[] OpenAIFile(string fileName) => File.ReadAllBytes(Path.Combine(OpenAIDir, fileName));

    public static byte[] OpenAIHttpError(string statusFileName) =>
        File.ReadAllBytes(Path.Combine(OpenAIDir, "http-errors", statusFileName + ".json"));

    private static string OpenAIDir => Path.Combine(AppContext.BaseDirectory, "Fixtures", "openai");

    public static byte[] HttpError(string statusFileName) =>
        File.ReadAllBytes(Path.Combine(Dir, "http-errors", statusFileName + ".json"));
}
