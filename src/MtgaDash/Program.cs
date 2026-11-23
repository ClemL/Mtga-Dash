using MtgaDash;
using MtgaDash.Cards;
using MtgaDash.Data;

// "MtgaDash.exe --download-card-images": fetch the missing card images and exit, without starting the
// dashboard. Safe while the dashboard runs: it only reads the database and writes image files.
if (args.Contains("--download-card-images", StringComparer.OrdinalIgnoreCase))
{
    var tool = AppHost.Build(args.Where(a => !a.Equals("--download-card-images", StringComparison.OrdinalIgnoreCase)).ToArray());
    var images = tool.Services.GetRequiredService<CardImageStore>();
    var wanted = tool.Services.GetRequiredService<Queries>().ImageWanted();
    Console.WriteLine($"{wanted.Count} rares and mythics held at 3+ copies; images go to {images.Folder}");
    if (wanted.Count == 0) Console.WriteLine("Nothing to do. Card names need CardDbPath, and cards need saved decks.");
    var report = await images.DownloadMissingAsync(wanted, Console.WriteLine, CancellationToken.None);
    return report.Failed > 0 ? 1 : 0;
}

AppHost.Build(args).Run();
return 0;
