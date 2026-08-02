using OsuMapManager.Tester;
using OsuMapManager.Tester.Tests;

// Set this to your osu! lazer install path to skip path prompts in all tests.
// Leave empty to be prompted each time.
TesterConfig.OsuInstallPath = @"F:\osu!\osu-lazer";

while (true)
{
    Console.Clear();
    Console.WriteLine("============================================");
    Console.WriteLine("  OsuMapManager.Tester");
    Console.WriteLine("============================================");
    Console.WriteLine();
    Console.WriteLine("  1. Parse SQLite database (show tables & columns)");
    Console.WriteLine("  2. Download beatmap set by Online ID");
    Console.WriteLine("  3. Test: Query client.realm with filter (debug)");
    Console.WriteLine("  4. Test: Query SQLite DB with filter (debug)");
    Console.WriteLine("  5. Test: Create a collection in osu! lazer");
    Console.WriteLine("  0. Exit");
    Console.WriteLine();
    Console.Write("Select option: ");

    var key = Console.ReadKey().KeyChar;
    Console.WriteLine();
    Console.WriteLine();

    switch (key)
    {
        case '1': await TestParseDatabase.RunAsync(); break;
        case '2': await TestDownloadBeatmap.RunAsync(); break;
        case '3': await TestQueryRealm.RunAsync(); break;
        case '4': await TestQueryDb.RunAsync(); break;
        case '5': await TestCreateCollection.RunAsync(); break;
        case '0': return 0;
        default: Console.WriteLine("Invalid option."); Console.ReadKey(); break;
    }
}
