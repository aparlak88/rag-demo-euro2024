namespace Euro2024Rag.Tests;

/// <summary>Absolute paths to the dataset files the Web project copies into the test output directory.</summary>
public static class DataPaths
{
    public static string Csv { get; } = Path.Combine(AppContext.BaseDirectory, "Data", "Euro_2024_Matches.csv");

    public static string Context { get; } = Path.Combine(AppContext.BaseDirectory, "Data", "tournament-context.json");
}
