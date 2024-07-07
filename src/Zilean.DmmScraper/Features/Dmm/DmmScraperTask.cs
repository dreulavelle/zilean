namespace Zilean.DmmScraper.Features.Dmm;

public class DmmScraperTask
{
    public static async Task<int> Execute(ZileanConfiguration configuration, ILoggerFactory loggerFactory, CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger<DmmScraperTask>();

        try
        {
            var httpClient = CreateHttpClient();

            var dmmState = new DmmSyncState(loggerFactory.CreateLogger<DmmSyncState>());
            var dmmFileDownloader = new DmmFileDownloader(httpClient, loggerFactory.CreateLogger<DmmFileDownloader>());
            var elasticClient = new ElasticSearchClient(configuration, loggerFactory.CreateLogger<ElasticSearchClient>());
            var rtnService = await CreateRtnService(loggerFactory, cancellationToken);

            await dmmState.SetRunning(cancellationToken);

            var tempDirectory = await dmmFileDownloader.DownloadFileToTempPath(cancellationToken);
            //var tempDirectory = Path.Combine(Path.GetTempPath(), "DMMHashlists");

            var files = Directory.GetFiles(tempDirectory, "*.html", SearchOption.AllDirectories)
                .Where(f => !dmmState.ParsedPages.ContainsKey(Path.GetFileName(f)))
                .ToArray();

            logger.LogInformation("Found {Count} files to parse", files.Length);

            var processor = new DmmPageProcessor(dmmState, loggerFactory.CreateLogger<DmmPageProcessor>(), cancellationToken);

            var torrents = new List<ExtractedDmmEntry>();

            foreach (var file in files)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    logger.LogInformation("Cancellation requested, stopping processing");
                    break;
                }

                var fileName = Path.GetFileName(file);

                var sanitizedTorrents = await processor.ProcessPageAsync(file, fileName);

                if (sanitizedTorrents.Count == 0)
                {
                    continue;
                }

                torrents.AddRange(sanitizedTorrents);

                logger.LogInformation("Total torrents from file {FileName}: {Count}", fileName, sanitizedTorrents.Count);

                dmmState.ParsedPages.TryAdd(fileName, sanitizedTorrents.Count);
                dmmState.IncrementProcessedFilesCount();
            }

            if (torrents.Count != 0)
            {
                var distinctTorrents = torrents.DistinctBy(x => x.InfoHash).ToList();

                ParseTorrentTitles(rtnService, distinctTorrents);

                var indexResult =
                    await elasticClient.IndexManyBatchedAsync(distinctTorrents, ElasticSearchClient.DmmIndex, cancellationToken);

                if (indexResult.Errors)
                {
                    logger.LogInformation("Failed to index {Count} torrents", distinctTorrents.Count);
                    return 1;
                }

                logger.LogInformation("Indexed {Count} torrents", distinctTorrents.Count);
            }

            await dmmState.SetFinished(cancellationToken, processor);

            return 0;
        }
        catch (TaskCanceledException)
        {
            return 0;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error occurred during DMM Scraper Task");
            return 1;
        }
    }

    private static void ParseTorrentTitles(RankTorrentNameService rtnService, List<ExtractedDmmEntry> sanitizedTorrents)
    {
        var torrentsToParse = sanitizedTorrents.ToDictionary(x => x.InfoHash!, x => x.Filename);

        var parsedResponses = rtnService.BatchParse([.. torrentsToParse.Values], trashGarbage: false);

        var successfulResponses = parsedResponses
            .Where(response => response is { Success: true })
            .GroupBy(response => response.Response.RawTitle!)
            .ToDictionary(group => group.Key, group => group.First());

        foreach (var torrent in sanitizedTorrents)
        {
            if (successfulResponses.TryGetValue(torrent.Filename, out var response))
            {
                torrent.RtnResponse = response.Response;
            }
        }
    }

    private static async Task<RankTorrentNameService> CreateRtnService(ILoggerFactory loggerFactory, CancellationToken cancellationToken)
    {
        var pythonEngineService = new PythonEngineService(loggerFactory.CreateLogger<PythonEngineService>());
        await pythonEngineService.InitializePythonEngine(cancellationToken);
        return new RankTorrentNameService(pythonEngineService);
    }

    private static HttpClient CreateHttpClient()
    {
        var httpClient = new HttpClient
        {
            BaseAddress = new Uri("https://github.com/debridmediamanager/hashlists/zipball/main/"),
            Timeout = TimeSpan.FromMinutes(10),
        };

        httpClient.DefaultRequestHeaders.Add("Accept-Encoding", "gzip");
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("curl/7.54");
        return httpClient;
    }
}
