using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using GoldbergGUI.Core.Models;
using GoldbergGUI.Core.Utils;
using Microsoft.Extensions.Logging;
using NinjaNye.SearchExtensions;
using SQLite;
using SteamStorefrontAPI;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace GoldbergGUI.Core.Services
{
    // gets info from steam api
    public interface ISteamService
    {
        public Task Initialize(ILogger<ISteamService> log);
        public Task<IEnumerable<SteamApp>> GetListOfAppsByName(string name);
        public Task<SteamApp> GetAppByName(string name);
        public Task<SteamApp> GetAppById(int appid);
        public Task<List<Achievement>> GetListOfAchievements(SteamApp steamApp);
        public Task<List<DlcApp>> GetListOfDlc(SteamApp steamApp, bool useSteamDb);
    }

    class SteamCache
    {
        public string SteamUri { get; }
        public Type ApiVersion { get; }
        public string SteamAppType { get; }

        public SteamCache(string uri, Type apiVersion, string steamAppType)
        {
            SteamUri = uri;
            ApiVersion = apiVersion;
            SteamAppType = steamAppType;
        }
    }

    // ReSharper disable once UnusedType.Global
    // ReSharper disable once ClassNeverInstantiated.Global
    public class SteamService : ISteamService
    {
        // ReSharper disable StringLiteralTypo
        private readonly Dictionary<string, SteamCache> _caches =
            new Dictionary<string, SteamCache>
            {
                {
                    AppTypeGame,
                    new SteamCache(
                        "https://api.steampowered.com/IStoreService/GetAppList/v1/" +
                        "?max_results=50000" +
                        "&include_games=1" +
                        "&key=" + Secrets.SteamWebApiKey(),
                        typeof(SteamAppsV1),
                        AppTypeGame
                    )
                },
                {
                    AppTypeDlc,
                    new SteamCache(
                        "https://api.steampowered.com/IStoreService/GetAppList/v1/" +
                        "?max_results=50000" +
                        "&include_games=0" +
                        "&include_dlc=1" +
                        "&key=" + Secrets.SteamWebApiKey(),
                        typeof(SteamAppsV1),
                        AppTypeDlc
                    )
                }
            };

        private static readonly Secrets Secrets = new Secrets();

        private const string UserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
            "Chrome/87.0.4280.88 Safari/537.36";
        private const string AppTypeGame = "game";
        private const string AppTypeDlc = "dlc";
        private const string Database = "steamapps.cache";
        private const string GameSchemaUrl = "https://api.steampowered.com/ISteamUserStats/GetSchemaForGame/v2/";

        private ILogger<ISteamService> _log;

        private SQLiteAsyncConnection _db;

        public async Task Initialize(ILogger<ISteamService> log)
        {
            static SteamApps DeserializeSteamApps(Type type, string cacheString)
            {
                return type == typeof(SteamAppsV2)
                    ? (SteamApps)JsonSerializer.Deserialize<SteamAppsV2>(cacheString)
                    : JsonSerializer.Deserialize<SteamAppsV1>(cacheString);
            }

            _log = log;
            _db = new SQLiteAsyncConnection(Database);
            //_db.CreateTable<SteamApp>();
            await _db.CreateTableAsync<SteamApp>()
                //.ContinueWith(x => _log.LogDebug("Table success!"))
                .ConfigureAwait(false);

            var countAsync = await _db.Table<SteamApp>().CountAsync().ConfigureAwait(false);
            if (DateTime.Now.Subtract(File.GetLastWriteTimeUtc(Database)).TotalDays >= 1 || countAsync == 0)
            {
                foreach (var (appType, steamCache) in _caches)
                {
                    _log.LogInformation($"Updating cache ({appType})...");
                    bool haveMoreResults;
                    long lastAppId = 0;
                    var client = new HttpClient();
                    var cacheRaw = new HashSet<SteamApp>();
                    do
                    {
                        var response = lastAppId > 0
                            ? await client.GetAsync($"{steamCache.SteamUri}&last_appid={lastAppId}")
                                .ConfigureAwait(false)
                            : await client.GetAsync(steamCache.SteamUri).ConfigureAwait(false);
                        var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        var steamApps = DeserializeSteamApps(steamCache.ApiVersion, responseBody);
                        foreach (var appListApp in steamApps.AppList.Apps) cacheRaw.Add(appListApp);
                        haveMoreResults = steamApps.AppList.HaveMoreResults;
                        lastAppId = steamApps.AppList.LastAppid;
                    } while (haveMoreResults);

                    var cache = new HashSet<SteamApp>();
                    foreach (var steamApp in cacheRaw)
                    {
                        steamApp.AppType = steamCache.SteamAppType;
                        steamApp.ComparableName = PrepareStringToCompare(steamApp.Name);
                        cache.Add(steamApp);
                    }

                    await _db.InsertAllAsync(cache, "OR IGNORE").ConfigureAwait(false);
                }
            }
        }

        public async Task<IEnumerable<SteamApp>> GetListOfAppsByName(string name)
        {
            var query = await _db.Table<SteamApp>()
                .Where(x => x.AppType == AppTypeGame).ToListAsync().ConfigureAwait(false);
            var listOfAppsByName = query.Search(x => x.Name)
                .SetCulture(StringComparison.OrdinalIgnoreCase)
                .ContainingAll(name.Split(' '));
            return listOfAppsByName;
        }

        public async Task<SteamApp> GetAppByName(string name)
        {
            _log.LogInformation($"Trying to get app {name}");
            var comparableName = PrepareStringToCompare(name);
            var app = await _db.Table<SteamApp>()
                .FirstOrDefaultAsync(x => x.AppType == AppTypeGame && x.ComparableName.Equals(comparableName))
                .ConfigureAwait(false);
            if (app != null) _log.LogInformation($"Successfully got app {app}");
            return app;
        }

        public async Task<SteamApp> GetAppById(int appid)
        {
            _log.LogInformation($"Trying to get app with ID {appid}");
            var app = await _db.Table<SteamApp>().Where(x => x.AppType == AppTypeGame)
                .FirstOrDefaultAsync(x => x.AppId.Equals(appid)).ConfigureAwait(false);
            if (app != null) _log.LogInformation($"Successfully got app {app}");
            return app;
        }

        public async Task<List<Achievement>> GetListOfAchievements(SteamApp steamApp)
        {
            var achievementList = new List<Achievement>();
            if (steamApp == null)
            {
                return achievementList;
            }

            _log.LogInformation($"Getting achievements for App {steamApp}");

            var client = new HttpClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            var apiUrl = $"{GameSchemaUrl}?key={Secrets.SteamWebApiKey()}&appid={steamApp.AppId}&l=en";

            var response = await client.GetAsync(apiUrl);
            var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            var jsonResponse = JsonDocument.Parse(responseBody);
            var achievementData = jsonResponse.RootElement.GetProperty("game")
                .GetProperty("availableGameStats")
                .GetProperty("achievements");

            achievementList = JsonSerializer.Deserialize<List<Achievement>>(achievementData.GetRawText());
            return achievementList;
        }

        public async Task<List<DlcApp>> GetListOfDlc(SteamApp steamApp, bool useSteamDb)
        {
            var dlcList = new List<DlcApp>();
            if (steamApp != null)
            {
                _log.LogInformation($"Get DLC for App {steamApp}");
                var task = AppDetails.GetAsync(steamApp.AppId);
                var steamAppDetails = await task.ConfigureAwait(true);
                if (steamAppDetails.Type == AppTypeGame)
                {
                    steamAppDetails.DLC.ForEach(async x =>
                    {
                        var result = await _db.Table<SteamApp>().Where(z => z.AppType == AppTypeDlc)
                                         .FirstOrDefaultAsync(y => y.AppId.Equals(x)).ConfigureAwait(true)
                                     ?? new SteamApp() { AppId = x, Name = $"Unknown DLC {x}", ComparableName = $"unknownDlc{x}", AppType = AppTypeDlc };
                        dlcList.Add(new DlcApp(result));
                        _log.LogDebug($"{result.AppId}={result.Name}");
                    });

                    _log.LogInformation("Got DLC successfully...");

                    // Get DLC from SteamDB
                    // Get Cloudflare cookie (not implemented)
                    // Scrape and parse HTML page
                    // Add missing to DLC list

                    // Return current list if we don't intend to use SteamDB
                    if (!useSteamDb) return dlcList;

                    try
                    {
                        var steamDbUri = new Uri($"https://steamdb.info/app/{steamApp.AppId}/dlc/");

                        var client = new HttpClient();
                        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);

                        _log.LogInformation($"Get SteamDB App {steamApp}");
                        var httpCall = client.GetAsync(steamDbUri);
                        var response = await httpCall.ConfigureAwait(false);
                        _log.LogDebug(httpCall.Status.ToString());
                        _log.LogDebug(response.EnsureSuccessStatusCode().ToString());

                        var readAsStringAsync = response.Content.ReadAsStringAsync();
                        var responseBody = await readAsStringAsync.ConfigureAwait(false);
                        _log.LogDebug(readAsStringAsync.Status.ToString());

                        var parser = new HtmlParser();
                        var doc = parser.ParseDocument(responseBody);

                        var query1 = doc.QuerySelector("#dlc");
                        if (query1 != null)
                        {
                            _log.LogInformation("Got list of DLC from SteamDB.");
                            var query2 = query1.QuerySelectorAll(".app");
                            foreach (var element in query2)
                            {
                                var dlcId = element.GetAttribute("data-appid");
                                var query3 = element.QuerySelectorAll("td");
                                var dlcName = query3 != null
                                    ? query3[1].Text().Replace("\n", "").Trim()
                                    : $"Unknown DLC {dlcId}";
                                var dlcApp = new DlcApp { AppId = Convert.ToInt32(dlcId), Name = dlcName };
                                var i = dlcList.FindIndex(x => x.AppId.Equals(dlcApp.AppId));
                                if (i > -1)
                                {
                                    if (dlcList[i].Name.Contains("Unknown DLC")) dlcList[i] = dlcApp;
                                }
                                else
                                {
                                    dlcList.Add(dlcApp);
                                }
                            }

                            dlcList.ForEach(x => _log.LogDebug($"{x.AppId}={x.Name}"));
                            _log.LogInformation("Got DLC from SteamDB successfully...");
                        }
                        else
                        {
                            _log.LogError("Could not get DLC from SteamDB!");
                        }
                    }
                    catch (Exception e)
                    {
                        _log.LogError("Could not get DLC from SteamDB! Skipping...");
                        _log.LogError(e.ToString());
                    }
                }
                else
                {
                    _log.LogError("Could not get DLC: Steam App is not of type \"game\"");
                }
            }
            else
            {
                _log.LogError("Could not get DLC: Invalid Steam App");
            }

            return dlcList;
        }

        private static string PrepareStringToCompare(string name)
        {
            return Regex.Replace(name, Misc.AlphaNumOnlyRegex, "").ToLower();
        }
    }
}