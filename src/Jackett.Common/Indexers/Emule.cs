using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web;
using AngleSharp.Html.Parser;
using AngleSharp.Io;
using Jackett.Common.Models;
using Jackett.Common.Models.IndexerConfig;
using Jackett.Common.Services.Interfaces;
using Jackett.Common.Utils;
using Jackett.Common.Utils.Clients;
using Microsoft.AspNetCore.Http;
using Newtonsoft.Json.Linq;
using NLog;
using Polly.Caching;
using static System.Net.WebRequestMethods;
using static Jackett.Common.Models.IndexerConfig.ConfigurationData;
using WebClient = Jackett.Common.Utils.Clients.WebClient;

namespace Jackett.Common.Indexers
{
    [ExcludeFromCodeCoverage]
    public class Emule : IndexerBase
    {
        public override string Id => "Emule";
        public override string Name => "Emule";
        public override string Description => "Emule conection with emules go to github_URL to get more details";
        // in the event the redirect is inactive https://t.me/s/dontorrent should have the latest working domain
        public override string SiteLink { get; protected set; } = "http://192.168.1.85:3000/";
        public override string[] AlternativeSiteLinks => new[]
        {
            "http://localhost:3000/",
        };
        public override string[] LegacySiteLinks => new[]
        {
            "http://localhost:3000/"
        };
        public override string Language => "es-ES";
        public override string Type => "public";

        protected virtual int KeyLength => 32;
        public string[] SpanishReleaseGroup => new[]
        {
            "nocturnia",
            "exploradoresp2p",
            "xusman",
            "geot",
            "hispashare",
            "cartmangold",
            "grupots",
            "eth@n",
            "grupos hds",
            "grupo hds",
            "grupohds",
            "sharerip",
            "bryan_122",
            "yamil"
        };
        public override TorznabCapabilities TorznabCaps => SetCapabilities();

        private static class EmuleCatType
        {
            public static string Pelicula => "pelicula";
            public static string PeliculaHD => "peliculaHD";
            public static string Serie => "serie";
            public static string SerieHD => "serieHD";
            public static string Documental => "documental";
            public static string Musica => "musica";
            public static string Variado => "variado";
            public static string Juego => "juego";
        }

        private const string NewTorrentsUrl = "ultimos";
        private const string SearchUrl = "emule/search";

        private static Dictionary<string, string> CategoriesMap => new Dictionary<string, string>
            {
                { "/pelicula/", EmuleCatType.Pelicula },
                { "/peliculaHD/", EmuleCatType.PeliculaHD },
                { "/serie/", EmuleCatType.Serie },
                { "/serieHD/", EmuleCatType.SerieHD },
                { "/documental", EmuleCatType.Documental },
                { "/musica/", EmuleCatType.Musica },
                { "/variado/", EmuleCatType.Variado },
                { "/juego/", EmuleCatType.Juego } //games, it can be pc or console
            };

        private new ConfigurationDataAPIKey configData
        {
            get => (ConfigurationDataAPIKey)base.configData;
            set => base.configData = value;
        }

        public Emule(IIndexerConfigurationService configService, WebClient w, Logger l, IProtectionService ps,
            ICacheService cs)
            : base(configService: configService,
                   client: w,
                   logger: l,
                   p: ps,
                   cacheService: cs,
                   configData: new ConfigurationDataAPIKey())
        {
            // avoid CLoudflare too many requests limiter
            webclient.requestDelay = 5;
            configData.AddDynamic("keyInfo", new DisplayInfoConfigurationItem(String.Empty, "Add the same key that in Emulex"));

            /*
            var matchWords = new BoolConfigurationItem("Match words in title") { Value = true };
            configData.AddDynamic("MatchWords", matchWords);
            */
        }

        private TorznabCapabilities SetCapabilities()
        {
            var caps = new TorznabCapabilities
            {
                TvSearchParams = new List<TvSearchParam>
                {
                    TvSearchParam.Q, TvSearchParam.Season, TvSearchParam.Ep
                },
                MovieSearchParams = new List<MovieSearchParam>
                {
                    MovieSearchParam.Q
                },
                MusicSearchParams = new List<MusicSearchParam>
                {
                    MusicSearchParam.Q,
                }
            };

            caps.Categories.AddCategoryMapping(EmuleCatType.Pelicula, TorznabCatType.Movies, "Pelicula");
            caps.Categories.AddCategoryMapping(EmuleCatType.PeliculaHD, TorznabCatType.MoviesUHD, "Peliculas 4K");
            caps.Categories.AddCategoryMapping(EmuleCatType.Serie, TorznabCatType.TVSD, "Serie");
            caps.Categories.AddCategoryMapping(EmuleCatType.SerieHD, TorznabCatType.TVHD, "Serie HD");
            caps.Categories.AddCategoryMapping(EmuleCatType.Musica, TorznabCatType.Audio, "Música");

            return caps;
        }

        public override async Task<IndexerConfigurationStatus> ApplyConfiguration(JToken configJson)
        {
            LoadValuesFromJson(configJson);

            IsConfigured = false;
            var apiKey = configData.Key;
            if (apiKey.Value.Length != KeyLength)
                throw new Exception($"Invalid API Key configured: expected length: {KeyLength}, got {apiKey.Value.Length}");

            try
            {
                var results = await PerformQuery(new TorznabQuery());
                if (!results.Any())
                    throw new Exception("Testing returned no results!");
                IsConfigured = true;
                SaveConfig();
            }
            catch (Exception e)
            {
                throw new ExceptionWithConfigData(e.Message, configData);
            }

            return IndexerConfigurationStatus.Completed;
        }

        protected override async Task<IEnumerable<ReleaseInfo>> PerformQuery(TorznabQuery query)
        {

            // we remove parts from the original query
            query = ParseQuery(query);


            //var releases = await PerformQuerySearch(query, matchWords);

            var releases = string.IsNullOrEmpty(query.SearchTerm) ?
            await checkStatus(query) :
            await PerformQuerySearch(query);


            return releases;
        }

        public override async Task<byte[]> Download(Uri link)
        {
            var downloadUrl = link.ToString();
            byte[] bytes = Encoding.UTF8.GetBytes(downloadUrl);
            return bytes;
        }

        private async Task<List<ReleaseInfo>> checkStatus(TorznabQuery query)
        {

            var releases = new List<ReleaseInfo>();

            var url = "emulex/status/";
            url = new Uri(new Uri(this.SiteLink), url).ToString();

            var result = await RequestWithCookiesAsync(
                url,
                headers: new Dictionary<string, string>
                {
                    {"X-API-KEY", configData.Key.Value}
                }
            );
            logger.Debug("Logout result: " + result.ContentString);


            if (result.Status != HttpStatusCode.OK && (result.Status == HttpStatusCode.ServiceUnavailable || result.Status == HttpStatusCode.Forbidden))
                return releases;



            var release = GenerateRelease("TODO ", "link", "https://ed2k.shortypower.org/?hash=31C0CADFEF07C84E9CF23E26C0BBA159", "Pelicula", DateTime.Now, 40000);
            releases.Add(release);
            releases.Add(release);
            releases.Add(release);
            releases.Add(release);
            releases.Add(release);
            releases.Add(release);
            releases.Add(release);
            releases.Add(release);
            releases.Add(release);


            var release2 = GenerateRelease("TODO2 ", "link", "https://ed2k.shortypower.org/?hash=31C0CADFEF07C84E9CF23E26C0BBA158", "Serie", DateTime.Now, 40000);
            releases.Add(release2);
            releases.Add(release2);
            releases.Add(release2);
            releases.Add(release2);
            releases.Add(release2);
            releases.Add(release2);
            releases.Add(release2);
            releases.Add(release);
            releases.Add(release2);


            return releases;
        }

        private async Task<List<ReleaseInfo>> PerformQuerySearch(TorznabQuery query)
        {
            var releases = new List<ReleaseInfo>();
            // search only the longest word, we filter the results later
            //var searchTerm = GetLongestWord(query.SearchTerm);
            var url = "emulex/search/";// Uri.EscapeDataString(query.SearchTerm +" "+query.Year + " mkv");
            url = new Uri(new Uri(this.SiteLink), url).ToString();
            //var result = await RequestWithCookiesAsync(url, referer: url);



            //Convertimos S01E01 en 0x01


            var searchPattern = query.SearchTerm;
            // Expresión regular para encontrar el patrón S01E01
            string patronEpisodio = @"S(\d{2})E(\d{2})";

            // Buscar el patrón en el texto original
            Match match = Regex.Match(query.SearchTerm, patronEpisodio);

            if (match.Success)
            {
                // Obtener los números de temporada y episodio
                int temporada = int.Parse(match.Groups[1].Value);
                int episodio = int.Parse(match.Groups[2].Value);

                // Formatear los números en el formato deseado (1x01)
                string nuevoFormatoEpisodio = episodio < 10 ? $"0{episodio}" : episodio.ToString();

                string nuevoFormato = $"{temporada}x{nuevoFormatoEpisodio}";
                // Reemplazar el patrón original con el nuevo formato
                searchPattern = Regex.Replace(query.SearchTerm, patronEpisodio, nuevoFormato);

                Console.WriteLine(searchPattern);
            }
            else
            {
                Console.WriteLine("No se encontró el patrón en el texto original.");
            }

            Encoding iso = Encoding.GetEncoding("ISO-8859-1");
            Encoding utf8 = Encoding.UTF8;
            byte[] utfBytes = utf8.GetBytes(searchPattern);
            byte[] isoBytes = Encoding.Convert(utf8, iso, utfBytes);
            string msg = iso.GetString(isoBytes);

            string keyword = msg + " " + query.Year;
            var data = new Dictionary<string, string>
            {
                { "keyword", keyword },
                { "priority", "5" }
            };

            var result = await RequestWithCookiesAsync(
                url,
                headers: new Dictionary<string, string>
                {
                    {"X-API-KEY", configData.Key.Value}
                },
                method: RequestType.POST,
                data: data);
            logger.Debug("Logout result: " + result.ContentString);


            if (result.Status != HttpStatusCode.Created)
                throw new ExceptionWithConfigData(result.ContentString, configData);

            try
            {
                var searchResultParser = new HtmlParser();
                var doc = searchResultParser.ParseDocument(result.ContentString);

                JArray jsonArray = null;
                try
                {
                    jsonArray = JArray.Parse(result.ContentString);
                    //json = JObject.Parse(result.ContentString);
                }
                catch (Exception ex)
                {
                    throw new Exception("Error while parsing json: " + result.ContentString, ex);
                }

                try
                {
                    foreach (JObject item in jsonArray)
                    {

                        //Clean title from dub/subs anomalies

                        string title = item.GetValue("_fileName").ToString();
                        title = Regex.Replace(title, @"\b(Spanish)sub\b", "$1.sub");
                        title = Regex.Replace(title, @"\b(English)sub\b", "$1.sub");

                        title = Regex.Replace(title, @"\b(m)?1080p?\b", "1080p");

                        title = Regex.Replace(title, @"\b(espa[ñn]ol|castellano)|\b ESP \b", "Spanish", RegexOptions.IgnoreCase);
                        title = Regex.Replace(title, @"\b(esp|spa)\b", "Spanish", RegexOptions.IgnoreCase);

                        //title = Regex.Replace(title, @"\((\d{4})\.\w+\.?\w*\)", "($1)");
                       
                        title = Regex.Replace(title, @" \((\d{ 4})[^\)] *\)", "($1)");

                        //title = Regex.Replace(title, @"\b(Englishsub|)\b", "English.sub", RegexOptions.IgnoreCase);

                        foreach (string releaseGroup in this.SpanishReleaseGroup)
                        {
                            /*
                            if ((title.ToLower()).Contains(releaseGroup))
                            {

                                string patron = @"^(.*?\(\d{4}\))";
                                string regex1x02 = @"\d{1}[xX]\d{2}";

                                title = Regex.Replace(title, patron, "$1 " + "Spanish", RegexOptions.IgnoreCase);
                                break;
                            }
                            */

                            if ((title.ToLower()).Contains(releaseGroup))
                            {
                                string patron = @"^(.*?\(\d{4}\))";
                                string regex1x02 = @"\d{1}[xX]\d{2}";

                                // Intenta con la primera expresión regular
                                Match match2 = Regex.Match(title, patron, RegexOptions.IgnoreCase);
                                if (match2.Success)
                                {
                                    // Si hay una coincidencia, reemplaza y termina el ciclo
                                    title = Regex.Replace(title, patron, "$& Spanish ");
                                    break;
                                }
                                else
                                {
                                    // Si no hay coincidencia con la primera expresión regular, intenta con la segunda
                                    match2 = Regex.Match(title, regex1x02);
                                    if (match2.Success)
                                    {
                                        //title = Regex.Replace(title, regex1x02, "$1 Spanish");
                                        //title = Regex.Replace(title, match2.ToString(), m => m.Groups[1].Value + " Spanish");

                                        //string patronEpisodio = @"\b\d{1}[xX]\d{2}\b";

                                        // Reemplazar el formato del episodio con "1x03 Spanish"
                                        title = Regex.Replace(title, regex1x02, "$& Spanish");

                                        break;

                                    }
                                }
                            }
                        }
                        var release = new ReleaseInfo
                        {

                            Title = title,
                            Details = new Uri("https://ed2k.shortypower.org/?hash=" + item.GetValue("_hash")),
                            //Link = new Uri("https://ed2k.shortypower.org/?hash=" + item.GetValue("_hash"))
                        };
                        release.Guid = new Uri("https://ed2k.shortypower.org/?hash=" + item.GetValue("_ed2kLinks"));
                        release.Imdb = ParseUtil.GetImdbId((string)"_test");
                        var freeleech = (bool)true;
                        if (freeleech)
                            release.DownloadVolumeFactor = 0;
                        else
                            release.DownloadVolumeFactor = 1;
                        release.UploadVolumeFactor = 1;


                        //Get category


                        var type = GetCategory(title);
                        release.Category = MapTrackerCatToNewznab(type);


                        string size = (string)item.GetValue("_size");
                        long sizeNumber = Int64.Parse(size);




                            /*
                            if (size.Contains("mb"))
                            {
                                size = size.Replace("mb", "");
                                sizeNumber = (long)Convert.ToDecimal(size);
                                sizeNumber *= (long)Math.Pow(1024, 2);
                            }
                            else if (size.Contains("gb"))
                            {
                                size = size.Replace("gb", "");
                                sizeNumber = (long)Convert.ToDecimal(size);
                                sizeNumber *= (long)Math.Pow(1024, 3);

                            }
                            */
                            release.Files = 1;
                        release.Size = sizeNumber;
                        release.Seeders = (int?)item.GetValue("_seed");
                        release.Peers = (int?)item.GetValue("_peer");
                        release.PublishDate = DateTimeUtil.FromUnknown((string)"1/12/1999");
                        var link = new Uri("https://ed2k.shortypower.org/?hash=" + item.GetValue("_hash"));
                        //release.MagnetUri = new Uri((string)item.GetValue("urled2k"));

                        UriBuilder builder = new UriBuilder();

                        // Asignar el esquema y el host
                        builder.Scheme = "http";
                        builder.Host = "192.168.1.85";
                        builder.Port = 3000;
                        builder.Path = "emulex/ed2k";

                        // Asignar la cadena de consulta con el enlace ed2k sin codificar
                        //string queryString = "name="+(string)item.GetValue("_urled2k");
                        //builder.Query = queryString;

                        builder.Query = "magnet:?xt=urn:btih:"+ item.GetValue("_hash");


                        // Construir el URI
                        Uri uri = builder.Uri;


                        //string querystring = (string)item.GetValue("urled2k");
                       //builder.Query = queryString;

                        // Crear un URI con un esquema válido y almacenar el enlace ed2k como parte del contenido
                        //Uri uri = new Uri("http://192.168.1.85:3000/ed2k/" + (string)item.GetValue("urled2k"));

                        Console.WriteLine("Uri Fragment: " + uri.Fragment);


                        release.MagnetUri  = new System.Uri("magnet:?xt=urn:btih:" + item.GetValue("_hash")+ "99999999" + "&dn=EMULE");
                        release.Link = release.MagnetUri;
                        releases.Add(release);

                    }
                }
                catch (Exception ex)
                {
                    OnParseError(result.ContentString, ex);
                }
            }
            catch (Exception ex)
            {
                OnParseError(result.ContentString, ex);
            }



            return releases;
        }

        /*
       private async Task ParseRelease(ICollection<ReleaseInfo> releases, string link, string title, string category, string quality, TorznabQuery query, bool matchWords)
       {
           // Remove trailing dot if there's one.
           title = title.Trim();
           if (title.EndsWith("."))
               title = title.Remove(title.Length - 1).Trim();

           //There's no public publishDate
           //var publishDate = TryToParseDate(publishStr, DateTime.Now);

           // return results only for requested categories
           if (query.Categories.Any() && !query.Categories.Contains(MapTrackerCatToNewznab(category).First()))
               return;

           // match the words in the query with the titles
           if (matchWords && !CheckTitleMatchWords(query.SearchTerm, title))
               return;

           switch (category)
           {
               case "pelicula":
               case "pelicula4k":
                   await //ParseMovieRelease(releases, link, query, title, quality);
                   break;
                //case "serie":
                //case "seriehd":
                //await ParseSeriesRelease(releases, link, query, title, quality);
                //break;
                //case "musica":
                //await ParseMusicRelease(releases, link, query, title);
                // break;
                default:
                   break;
           }
       }
        */
        /*
private async Task ParseSeriesRelease(ICollection<ReleaseInfo> releases, string link, TorznabQuery query, string title, string quality)
{
   var result = await RequestWithCookiesAsync(link);
   if (result.Status != HttpStatusCode.OK)
       throw new ExceptionWithConfigData(result.ContentString, configData);

   var searchResultParser = new HtmlParser();
   var doc = searchResultParser.ParseDocument(result.ContentString);

   var data = doc.QuerySelector("div.descargar > div.card > div.card-body");

   //var _title = data.QuerySelector("h2.descargarTitulo").TextContent;

   //var data2 = data.QuerySelectorAll("div.d-inline-block > p");

   //var quality = data2[0].TextContent; //"Formato: {0}" -- needs trimming
   //var episodes = data2[1].TextContent; //"Episodios: {0}" -- needs trimming, contains number of episodes available

   var data3 = data.QuerySelectorAll("div.d-inline-block > table.table > tbody > tr");

   foreach (var row in data3)
   {
       var episodeData = row.QuerySelectorAll("td");

       var episodeTitle = episodeData[0].TextContent; //it may contain two episodes divided by '&', eg '1x01 & 1x02'
       var downloadLink = "https:" + episodeData[1].QuerySelector("a").GetAttribute("href"); // URL like "//cdn.pizza/"
       var episodePublishStr = episodeData[2].TextContent;
       var episodePublish = TryToParseDate(episodePublishStr, DateTime.Now);

       // Convert the title to Scene format
       episodeTitle = ParseSeriesTitle(title, episodeTitle, query);

       // if the original query was in scene format, we filter the results to match episode
       // query.Episode != null means scene title
       if (query.Episode != null && !episodeTitle.Contains(query.GetEpisodeSearchString()))
           continue;

       // guess size
       var size = 536870912L; // 512 MB
       if (episodeTitle.ToLower().Contains("720p"))
           size = 1073741824L; // 1 GB
       if (episodeTitle.ToLower().Contains("1080p"))
           size = 4294967296L; // 4 GB

       size *= GetEpisodeCountFromTitle(episodeTitle);

       var release = GenerateRelease(episodeTitle, link, downloadLink, GetCategory(title, link), episodePublish, size);
       releases.Add(release);
   }
}


        private async Task ParseMovieRelease(ICollection<ReleaseInfo> releases, string link, TorznabQuery query, string title, string quality)
       {
           title = title.Trim();

           var result = await RequestWithCookiesAsync(link);
           if (result.Status != HttpStatusCode.OK)
               throw new ExceptionWithConfigData(result.ContentString, configData);

           var searchResultParser = new HtmlParser();
           var doc = searchResultParser.ParseDocument(result.ContentString);

           // parse tags in title, we need to put the year after the real title (before the tags)
           // Harry Potter And The Deathly Hallows: Part 1 [subs. Integrados]
           var tags = "";
           var queryMatches = Regex.Matches(title, @"[\[\(]([^\]\)]+)[\]\)]", RegexOptions.IgnoreCase);
           foreach (Match m in queryMatches)
           {
               var tag = m.Groups[1].Value.Trim().ToUpper();
               if (tag.Equals("4K")) // Fix 4K quality. Eg Harry Potter Y La Orden Del Fénix [4k]
                   quality = "(UHD 4K 2160p)";
               else if (tag.Equals("FULLBLURAY")) // Fix 4K quality. Eg Harry Potter Y El Cáliz De Fuego (fullbluray)
                   quality = "(COMPLETE BLURAY)";
               else // Add the tag to the title
                   tags += " " + tag;
               title = title.Replace(m.Groups[0].Value, "");
           }
           title = title.Trim();

           // clean quality
           if (quality != null)
           {
               var queryMatch = Regex.Match(quality, @"[\[\(]([^\]\)]+)[\]\)]", RegexOptions.IgnoreCase);
               if (queryMatch.Success)
                   quality = queryMatch.Groups[1].Value;
               quality = quality.Trim().Replace("-", " ");
               quality = Regex.Replace(quality, "HDRip", "BDRip", RegexOptions.IgnoreCase); // fix for Radarr
           }

           // add the year
           title = query.Year != null ? title + " " + query.Year : title;

           // add the tags
           title += tags;

           // add spanish
           title += " SPANISH";

           // add quality
           if (quality != null)
               title += " " + quality;

           var info = doc.QuerySelectorAll("div.descargar > div.card > div.card-body").First();
           var moreinfo = info.QuerySelectorAll("div.text-center > div.d-inline-block");

           // guess size
           long size;
           if (moreinfo.Length == 2)
               size = ParseUtil.GetBytes(moreinfo[1].QuerySelector("p").TextContent);
           else if (title.ToLower().Contains("4k"))
               size = 53687091200L; // 50 GB
           else if (title.ToLower().Contains("1080p"))
               size = 4294967296L; // 4 GB
           else if (title.ToLower().Contains("720p"))
               size = 1073741824L; // 1 GB
           else
               size = 536870912L; // 512 MB

           var release = GenerateRelease(title, link, link, GetCategory(title, link), DateTime.Now, size);
           releases.Add(release);
       }
        */
        private ReleaseInfo GenerateRelease(string title, string link, string downloadLink, string cat,
                                            DateTime publishDate, long size)
        {
            var dl = new Uri(downloadLink);
            var _link = new Uri(downloadLink);
            var release = new ReleaseInfo
            {
                Title = title,
                Details = _link,
                Link = dl,
                Guid = dl,
                Category = MapTrackerCatToNewznab(cat),
                PublishDate = publishDate,
                Size = size,
                Seeders = 1,
                Peers = 2,
                DownloadVolumeFactor = 0,
                UploadVolumeFactor = 1
            };
            return release;
        }

        private static bool CheckTitleMatchWords(string queryStr, string title)
        {
            // this code split the words, remove words with 2 letters or less, remove accents and lowercase
            var queryMatches = Regex.Matches(queryStr, @"\b[\w']*\b");
            var queryWords = from m in queryMatches.Cast<Match>()
                             where !string.IsNullOrEmpty(m.Value) && m.Value.Length > 2
                             select Encoding.UTF8.GetString(Encoding.GetEncoding("ISO-8859-8").GetBytes(m.Value.ToLower()));

            var titleMatches = Regex.Matches(title, @"\b[\w']*\b");
            var titleWords = from m in titleMatches.Cast<Match>()
                             where !string.IsNullOrEmpty(m.Value) && m.Value.Length > 2
                             select Encoding.UTF8.GetString(Encoding.GetEncoding("ISO-8859-8").GetBytes(m.Value.ToLower()));
            titleWords = titleWords.ToArray();

            return queryWords.All(word => titleWords.Contains(word));
        }

        private static TorznabQuery ParseQuery(TorznabQuery query)
        {
            // Eg. Marco.Polo.2014.S02E08

            // the season/episode part is already parsed by Jackett
            // query.SanitizedSearchTerm = Marco.Polo.2014.
            // query.Season = 2
            // query.Episode = 8
            var searchTerm = query.SanitizedSearchTerm;

            // replace punctuation symbols with spaces
            // searchTerm = Marco Polo 2014
            searchTerm = Regex.Replace(searchTerm, @"[-._\(\)@/\\\[\]\+\%]", " ");
            searchTerm = Regex.Replace(searchTerm, @"\s+", " ");
            searchTerm = searchTerm.Trim();

            // we parse the year and remove it from search
            // searchTerm = Marco Polo
            // query.Year = 2014
            var r = new Regex("([ ]+([0-9]{4}))$", RegexOptions.IgnoreCase);
            var m = r.Match(searchTerm);
            if (m.Success)
            {
                query.Year = int.Parse(m.Groups[2].Value);
                searchTerm = searchTerm.Replace(m.Groups[1].Value, "");
            }

            // remove some words
            searchTerm = Regex.Replace(searchTerm, @"\b(espa[ñn]ol|spanish|castellano|spa)\b", "", RegexOptions.IgnoreCase);

            query.SearchTerm = searchTerm;
            return query;
        }

        private static string ParseSeriesTitle(string title, string episodeTitle, TorznabQuery query)
        {
            // parse title
            // title = The Mandalorian - 1ª Temporada
            // title = The Mandalorian - 1ª Temporada [720p]
            // title = Grace and Frankie - 5ª Temporada [720p]: 5x08 al 5x13.
            var newTitle = title.Split(new[] { " - " }, StringSplitOptions.RemoveEmptyEntries)[0].Trim();
            // newTitle = The Mandalorian

            // parse episode title
            var newEpisodeTitle = episodeTitle.Trim();
            // episodeTitle = 5x08 al 5x13.
            // episodeTitle = 2x01 - 2x02 - 2x03.
            var matches = Regex.Matches(newEpisodeTitle, "([0-9]+)x([0-9]+)", RegexOptions.IgnoreCase);
            if (matches.Count > 1)
            {
                newEpisodeTitle = "";
                foreach (Match m in matches)
                    if (newEpisodeTitle.Equals(""))
                        newEpisodeTitle += "S" + m.Groups[1].Value.PadLeft(2, '0')
                                               + "E" + m.Groups[2].Value.PadLeft(2, '0');
                    else
                        newEpisodeTitle += "-E" + m.Groups[2].Value.PadLeft(2, '0');
                // newEpisodeTitle = S05E08-E13
                // newEpisodeTitle = S02E01-E02-E03
            }
            else
            {
                // episodeTitle = 1x04 - 05.
                var m = Regex.Match(newEpisodeTitle, "^([0-9]+)x([0-9]+)[^0-9]+([0-9]+)[.]?$", RegexOptions.IgnoreCase);
                if (m.Success)
                    newEpisodeTitle = "S" + m.Groups[1].Value.PadLeft(2, '0')
                                          + "E" + m.Groups[2].Value.PadLeft(2, '0') + "-"
                                          + "E" + m.Groups[3].Value.PadLeft(2, '0');
                // newEpisodeTitle = S01E04-E05
                else
                {
                    // episodeTitle = 1x02
                    // episodeTitle = 1x02 -
                    // episodeTitle = 1x08 -​ CONTRASEÑA: WWW.​PCTNEW ORG bebe
                    m = Regex.Match(newEpisodeTitle, "^([0-9]+)x([0-9]+)(.*)$", RegexOptions.IgnoreCase);
                    if (m.Success)
                    {
                        newEpisodeTitle = "S" + m.Groups[1].Value.PadLeft(2, '0')
                                              + "E" + m.Groups[2].Value.PadLeft(2, '0');
                        // newEpisodeTitle = S01E02
                        if (!m.Groups[3].Value.Equals(""))
                            newEpisodeTitle += " " + m.Groups[3].Value.Replace(" -", "").Trim();
                        // newEpisodeTitle = S01E08 CONTRASEÑA: WWW.​PCTNEW ORG bebe
                    }
                }
            }

            // if the original query was in scene format, we have to put the year back
            // query.Episode != null means scene title
            var year = query.Episode != null && query.Year != null ? " " + query.Year : "";
            newTitle += year + " " + newEpisodeTitle;

            newTitle += " SPANISH";

            // multilanguage
            if (title.ToLower().Contains("ES-EN"))
                newTitle += " ENGLISH";

            //quality
            if (title.ToLower().Contains("720p"))
                newTitle += " 720p";
            else if (title.ToLower().Contains("1080p"))
                newTitle += " 1080p";
            else
                newTitle += " SDTV";

            if (title.ToLower().Contains("HDTV"))
                newTitle += " HDTV";

            if (title.ToLower().Contains("x265"))
                newTitle += " x265";
            else
                newTitle += " x264";

            // return The Mandalorian S01E04 SPANISH 720p HDTV x264
            return newTitle;
        }





        private static string GetCategory(string fileName) {

            string[] extensions =  {
                "3g2",
                "3gp",
                "aaf",
                "asf",
                "avchd",
                "avi",
                "drc",
                "flv",
                "m2v",
                "m3u8",
                "m4p",
                "m4v",
                "mkv",
                "mng",
                "mov",
                "mp2",
                "mp4",
                "mpe",
                "mpeg",
                "mpg",
                "mpv",
                "mxf",
                "nsv",
                "ogg",
                "ogv",
                "qt",
                "rm",
                "rmvb",
                "roq",
                "svi",
                "vob",
                "webm",
                "wmv",
                "yuv"
             };

            var cat = EmuleCatType.Variado ;
            //Video check
            foreach (string extension in extensions)
            {
                if (fileName.Contains(extension))
                {
                    //Check if is a series
                    string regex1x02 = @"\d{1}[xX]\d{2}";
                    string regexS01E01 = @"S\d{2}E\d{2}";
                    if (Regex.IsMatch(fileName, regex1x02) || Regex.IsMatch(fileName, regexS01E01))
                    {
                        cat = EmuleCatType.Serie;
                        if (fileName.Contains("720p") || fileName.Contains("1080p"))
                        {
                            cat = EmuleCatType.SerieHD;
                        }
                    }
                    else
                    {
                        cat = EmuleCatType.Pelicula;
                        if (fileName.Contains("720p") || fileName.Contains("1080p"))
                        {
                            cat = EmuleCatType.PeliculaHD;
                        }

                    }
                    break;
                }
            }
            return cat;
        }
    }
}
