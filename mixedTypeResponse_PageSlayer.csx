//Power Platform Custom Handler: Page Slayer
//Developed by Dan Romano – dan.romano@swolcat.com
//Description: Makes a one-shot request to a World Bank endpoint using operationId,
//supports dynamic query params like format, page, per_page, mrv, and date. Developed for the World Bank API.

using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using System.Web;
using Newtonsoft.Json.Linq;

public class Script : ScriptBase
{
    private Microsoft.Extensions.Logging.ILogger _logger;

    //Maps supported operationIds to their World Bank API templates.
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
            //Decode and resolve operationId into template URL
            string operationId = DecodeOperationId(this.Context.OperationId);

            if (!EndpointTemplates.TryGetValue(operationId, out string urlTemplate))
                return CreateErrorResponse(HttpStatusCode.BadRequest, "InvalidOperation", $"Operation '{operationId}' not supported.");

            string finalUrl = BuildFinalUrl(operationId, urlTemplate);
            UriBuilder uriBuilder = new(finalUrl);

            //Parse query string from request
            string fullUrl = this.Context.Request.RequestUri.ToString();
            var query = new Uri(fullUrl).Query;
            var queryParams = ParseQueryParams(query);

            //Collect standard parameters (with fallbacks)
            string format = GetQueryOrHeaderValue(queryParams, "format", "json");
            string perPage = GetQueryOrHeaderValue(queryParams, "per_page", "50");
            string page = GetQueryOrHeaderValue(queryParams, "page", "1");
            string mrv = GetQueryOrHeaderValue(queryParams, "mrv", null);
            string date = GetQueryOrHeaderValue(queryParams, "date", null);

            //Construct query string
            var queryParts = new List<string>
            {
                $"format={format}",
                $"per_page={perPage}",
                $"page={page}"
            };
            if (!string.IsNullOrEmpty(mrv)) queryParts.Add($"mrv={mrv}");
            if (!string.IsNullOrEmpty(date)) queryParts.Add($"date={date}");

            uriBuilder.Query = string.Join("&", queryParts);
            Uri fullRequestUri = uriBuilder.Uri;

            _logger?.LogInformation($"Requesting URL: {fullRequestUri}");

            //Make request to World Bank API
            var reqMessage = new HttpRequestMessage(HttpMethod.Get, fullRequestUri);
            var response = await this.Context.SendAsync(reqMessage, this.CancellationToken);
            string content = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
                return CreateErrorResponse(response.StatusCode, "APIError", $"Call failed: {content}");

            //Parse and separate metadata and results
            JArray outerArray = JArray.Parse(content);
            JObject metadata = outerArray.Count > 0 ? outerArray[0].ToObject<JObject>() : new JObject();
            JArray results = outerArray.Count > 1 ? (JArray)outerArray[1] : new JArray();

            //Return clean object with both parts
            var finalResponse = new JObject
            {
                ["metadata"] = metadata,
                ["results"] = results
            };

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(finalResponse.ToString(), System.Text.Encoding.UTF8, "application/json")
            };
        }
        catch (Exception ex)
        {
            return CreateErrorResponse(HttpStatusCode.InternalServerError, "UnhandledException", ex.Message);
        }
    }

    //Decode Base64 operationId if possible
    private string DecodeOperationId(string operationId)
    {
        try
        {
            byte[] data = Convert.FromBase64String(operationId);
            string decoded = System.Text.Encoding.UTF8.GetString(data);
            return EndpointTemplates.ContainsKey(decoded) ? decoded : operationId;
        }
        catch
        {
            return operationId;
        }
    }

    //Pull value from request headers, with fallback
    private string GetHeaderValue(string key, string defaultValue)
    {
        if (this.Context.Request.Headers.TryGetValues(key, out var values))
            return values.FirstOrDefault() ?? defaultValue;
        return defaultValue;
    }

    //Prefer query param; fallback to header
    private string GetQueryOrHeaderValue(Dictionary<string, string> queryParams, string key, string defaultValue)
    {
        return queryParams.TryGetValue(key, out string val) && !string.IsNullOrWhiteSpace(val)
            ? val
            : GetHeaderValue(key, defaultValue);
    }

    //Parse raw query string into key-value dictionary
    private Dictionary<string, string> ParseQueryParams(string query)
    {
        var result = new Dictionary<string, string>();
        if (string.IsNullOrWhiteSpace(query)) return result;

        string[] pairs = query.TrimStart('?').Split('&');
        foreach (var pair in pairs)
        {
            var parts = pair.Split('=');
            if (parts.Length == 2)
            {
                string key = Uri.UnescapeDataString(parts[0]);
                string value = Uri.UnescapeDataString(parts[1]);
                result[key] = value;
            }
        }
        return result;
    }

    //Replace placeholders in the endpoint with values from headers
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
                    .Replace("{indicatorCode}", GetHeaderValue("indicatorCode", "SP.DYN.LE00.IN"));
                break;
            default:
                break;
        }
        return path;
    }

    //Helper to format error responses as JSON
    private HttpResponseMessage CreateErrorResponse(HttpStatusCode code, string err, string msg)
    {
        var obj = new JObject
        {
            ["error"] = new JObject
            {
                ["code"] = err,
                ["message"] = msg
            }
        };
        return new HttpResponseMessage(code)
        {
            Content = new StringContent(obj.ToString(), System.Text.Encoding.UTF8, "application/json")
        };
    }
}
