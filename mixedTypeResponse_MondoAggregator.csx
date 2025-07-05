using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

//MONDO AGGREGATOR
//This script serves as a universal data aggregator for the World Bank API.
//It supports flexible path and query parameters, handles paging automatically,
//and returns a single unified JSON response with metadata and results.
//Developed by Dan Romano – dan.romano@swolcat.com - @krautrocker
//Foundation developed by Troy Taylor - https://github.com/troystaylor/Connector-Code/blob/main/ArrayResponseMixedTypes.csx - @troystaylor

public class Script : ScriptBase
{
    private Microsoft.Extensions.Logging.ILogger _logger;

    //Maps operationId values to World Bank API URL templates.
    //These templates contain placeholders that get substituted at runtime.
    private static readonly Dictionary<string, string> EndpointTemplates = new()
    {
        { "GetCountriesByIncomeLevel", "https://api.worldbank.org/v2/incomeLevel/{incomeLevel}/country" },
        { "GetCountriesByRegion", "https://api.worldbank.org/v2/region/{regionCode}/country" },
        { "SearchCountries", "https://api.worldbank.org/v2/country" },
        { "GetDebtToGDP", "https://api.worldbank.org/v2/country/{countryCode}/indicator/GC.DOD.TOTL.GD.ZS" },
        { "GetEducationSpending", "https://api.worldbank.org/v2/country/{countryCode}/indicator/SE.XPD.TOTL.GD.ZS" },
        { "GetGDPForRegion", "https://api.worldbank.org/v2/country/{regionCode}/indicator/NY.GDP.MKTP.CD" },
        { "GetIndicatorMetadata", "https://api.worldbank.org/v2/indicator/{indicatorCode}" },
        { "GetInflation", "https://api.worldbank.org/v2/country/{countryCode}/indicator/FP.CPI.TOTL.ZG" },
        { "GetLifeExpectancyForIncomeLevel", "https://api.worldbank.org/v2/country/{incomeLevelCode}/indicator/SP.DYN.LE00.IN" },
        { "GetLiteracyRate", "https://api.worldbank.org/v2/country/{countryCode}/indicator/SE.ADT.LITR.ZS" },
        { "GetPrimarySchoolEnrollment", "https://api.worldbank.org/v2/country/{countryCode}/indicator/SE.PRM.NENR" },
        { "QueryIndicatorData", "https://api.worldbank.org/v2/country/{countryCode}/indicator/{indicatorCode}" },
        { "CompareExportsOverTime", "https://api.worldbank.org/v2/country/all/indicator/NE.EXP.GNFS.CD" },
        { "GetAllIncomeLevels", "https://api.worldbank.org/v2/incomeLevel" },
        { "GetAllIndicators", "https://api.worldbank.org/v2/indicator" },
        { "GetAllLendingTypes", "https://api.worldbank.org/v2/lendingType" },
        { "GetAllRegions", "https://api.worldbank.org/v2/region" },
        { "GetAllSources", "https://api.worldbank.org/v2/source" },
        { "GetAllTopics", "https://api.worldbank.org/v2/topic" },
        { "GetCountryMetadata", "https://api.worldbank.org/v2/country" }
    };

    public override async Task<HttpResponseMessage> ExecuteAsync()
    {
        _logger = this.Context.Logger;

        try
        {
            //Decode operationId (may be base64) and resolve template
            string operationId = DecodeOperationId(this.Context.OperationId);
            if (!EndpointTemplates.TryGetValue(operationId, out string urlTemplate))
                return CreateErrorResponse(HttpStatusCode.BadRequest, "InvalidOperation", $"Operation '{operationId}' not supported.");

            //Substitute path parameters in the URL (e.g., {countryCode})
            string baseUrl = BuildFinalUrl(operationId, urlTemplate);

            //Parse query string parameters (e.g., mrv, date, per_page)
            string rawUrl = this.Context.Request.RequestUri.ToString();
            string rawQuery = new Uri(rawUrl).Query;
            var queryParams = ParseQueryParams(rawQuery);

            string format = GetQueryOrHeaderValue(queryParams, "format", "json");
            string perPage = GetQueryOrHeaderValue(queryParams, "per_page", "50");
            string mrv = GetQueryOrHeaderValue(queryParams, "mrv", null);
            string date = GetQueryOrHeaderValue(queryParams, "date", null);
            string pageParam = GetQueryOrHeaderValue(queryParams, "page", null);

            //If page is provided, fetch only that page; otherwise, fetch all
            bool fetchSinglePage = !string.IsNullOrEmpty(pageParam);
            int page = fetchSinglePage ? int.Parse(pageParam) : 1;

            var allResults = new JArray(); // Stores combined results
            JObject metadata = null;       // Metadata only from first page
            int totalPages = 1;

            //Loop until all pages are retrieved (or just one if specified)
            do
            {
                var queryParts = new List<string>
                {
                    $"format={format}",
                    $"per_page={perPage}",
                    $"page={page}"
                };

                if (!string.IsNullOrEmpty(mrv)) queryParts.Add($"mrv={mrv}");
                if (!string.IsNullOrEmpty(date)) queryParts.Add($"date={date}");

                UriBuilder uriBuilder = new(baseUrl)
                {
                    Query = string.Join("&", queryParts)
                };

                Uri finalUri = uriBuilder.Uri;
                _logger?.LogInformation($"Requesting page {page}: {finalUri}");

                var req = new HttpRequestMessage(HttpMethod.Get, finalUri);
                var response = await this.Context.SendAsync(req, this.CancellationToken);
                string content = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                    return CreateErrorResponse(response.StatusCode, "APIError", $"Call failed: {content}");

                JArray outerArray = JArray.Parse(content);
                if (outerArray.Count < 2 || outerArray[1].Type != JTokenType.Array)
                    return CreateErrorResponse(HttpStatusCode.BadGateway, "UnexpectedStructure", "API response did not include expected metadata and data array.");

                metadata ??= outerArray[0].ToObject<JObject>();

                foreach (var item in (JArray)outerArray[1])
                    allResults.Add(item);

                if (fetchSinglePage) break;

                totalPages = int.Parse(metadata["pages"]?.ToString() ?? "1");
                page++;
            }
            while (page <= totalPages);

            //Construct final JSON response with metadata and all results
            var finalResponse = new JObject
            {
                ["metadata"] = metadata,
                ["results"] = allResults
            };

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(finalResponse.ToString(), System.Text.Encoding.UTF8, "application/json")
            };
        }
        catch (Exception ex)
        {
            _logger?.LogError($"Unhandled exception: {ex.Message}");
            return CreateErrorResponse(HttpStatusCode.InternalServerError, "UnhandledException", ex.Message);
        }
    }

    //Substitutes placeholders in endpoint URL with actual values from headers
    private string BuildFinalUrl(string operationId, string template)
    {
        string path = template;

        switch (operationId)
        {
            case "GetCountriesByIncomeLevel":
                path = template.Replace("{incomeLevel}", GetHeaderValue("incomeLevel", "LIC")); break;

            case "GetCountriesByRegion":
                path = template.Replace("{regionCode}", GetHeaderValue("regionCode", "MEA")); break;

            case "GetDebtToGDP":
            case "GetEducationSpending":
            case "GetInflation":
            case "GetLiteracyRate":
            case "GetPrimarySchoolEnrollment":
                path = template.Replace("{countryCode}", GetHeaderValue("countryCode", "USA")); break;

            case "GetGDPForRegion":
                path = template.Replace("{regionCode}", GetHeaderValue("regionCode", "MEA")); break;

            case "GetIndicatorMetadata":
                path = template.Replace("{indicatorCode}", GetHeaderValue("indicatorCode", "NY.GDP.MKTP.CD")); break;

            case "GetLifeExpectancyForIncomeLevel":
                path = template.Replace("{incomeLevelCode}", GetHeaderValue("incomeLevelCode", "LIC")); break;

            case "QueryIndicatorData":
                path = template
                    .Replace("{countryCode}", GetHeaderValue("countryCode", "USA"))
                    .Replace("{indicatorCode}", GetHeaderValue("indicatorCode", "SP.DYN.LE00.IN")); break;
        }

        return path;
    }

    //Parses raw query string into key-value pairs
    private Dictionary<string, string> ParseQueryParams(string query)
    {
        var result = new Dictionary<string, string>();
        if (string.IsNullOrWhiteSpace(query)) return result;

        foreach (var pair in query.TrimStart('?').Split('&'))
        {
            var parts = pair.Split('=');
            if (parts.Length == 2)
                result[Uri.UnescapeDataString(parts[0])] = Uri.UnescapeDataString(parts[1]);
        }

        return result;
    }

    //Reads a value from headers, falling back to default if not found
    private string GetHeaderValue(string key, string fallback)
    {
        if (this.Context.Request.Headers.TryGetValues(key, out var values))
            return values.FirstOrDefault() ?? fallback;
        return fallback;
    }

    //Resolves a parameter using query string first, then header, then fallback
    private string GetQueryOrHeaderValue(Dictionary<string, string> query, string key, string fallback)
    {
        return query.TryGetValue(key, out string val) && !string.IsNullOrWhiteSpace(val)
            ? val
            : GetHeaderValue(key, fallback);
    }

    //Decodes base64-encoded operationId, or returns original if not encoded
    private string DecodeOperationId(string operationId)
    {
        try
        {
            var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(operationId));
            return EndpointTemplates.ContainsKey(decoded) ? decoded : operationId;
        }
        catch { return operationId; }
    }

    //Constructs a standard error response with consistent JSON structure
    private HttpResponseMessage CreateErrorResponse(HttpStatusCode code, string error, string message)
    {
        var obj = new JObject
        {
            ["error"] = new JObject
            {
                ["code"] = error,
                ["message"] = message
            }
        };

        return new HttpResponseMessage(code)
        {
            Content = new StringContent(obj.ToString(), System.Text.Encoding.UTF8, "application/json")
        };
    }
}
