using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web;

using Garmin.Connect.Auth;
using Garmin.Connect.Converters;
using Garmin.Connect.Exceptions;
using Garmin.Connect.Models;

namespace Garmin.Connect;

public class GarminConnectContext
{
    private readonly HttpClient _httpClient;
    private readonly IAuthParameters _authParameters;

    private const int Attempts = 3;
    private const int DelayAfterFailAuth = 300;
    private readonly Regex _csrfRegex = new Regex(@"name=""_csrf""\s+value=""(\w+)""", RegexOptions.Compiled);
    private readonly Regex _responseUrlRegex = new Regex(@"""(https:[^""]+?ticket=[^""]+)""", RegexOptions.Compiled);
    private DateTime _tokenExpire = DateTime.Now;
    private string _tokenCached;

    public GarminConnectContext(HttpClient httpClient, IAuthParameters authParameters)
    {
        _httpClient = httpClient;
        _authParameters = authParameters;
    }

    public async Task ReLoginIfExpired(bool force = false)
    {
        if (force || _authParameters.NeedReLogin)
        {
            _tokenExpire = DateTime.Now;

            var (cookies, preferences, profile) = await Login();

            _authParameters.Cookies = cookies;
            Preferences = preferences;
            Profile = profile;
        }
    }

    public GarminSocialProfile Profile { get; private set; }

    public GarminUserPreferences Preferences { get; private set; }

    public async Task<T> GetAndDeserialize<T>(string url)
    {
        var response = await MakeHttpGet(url);
        var json = await response.Content.ReadAsByteArrayAsync();

        // Console.WriteLine($"{url}\n{json}\n\n\n");
        // return default;

        return GarminSerializer.To<T>(json);
    }

    public Task<HttpResponseMessage> MakeHttpGet(string url) =>
        MakeHttpRequest(url, HttpMethod.Get);

    public Task<HttpResponseMessage> MakeHttpPut<TBody>(string url, TBody body) =>
        MakeHttpRequest(url, HttpMethod.Put, JsonContent.Create(body));

    private async Task<HttpResponseMessage> MakeHttpRequest(string url, HttpMethod method, HttpContent content = null)
    {
        var force = false;
        Exception exception = null;

        for (var i = 0; i < Attempts; i++)
        {
            try
            {
                await ReLoginIfExpired(force);

                var bearerToken = await GetBearerToken();

                var httpRequestMessage = new HttpRequestMessage(method, $"{_authParameters.BaseUrl}{url}");
                httpRequestMessage.Headers.Add("cookie", _authParameters.Cookies);
                httpRequestMessage.Headers.Add("authorization", $"Bearer {bearerToken}");
                httpRequestMessage.Headers.Add("di-backend", "connectapi.garmin.com");
                httpRequestMessage.Content = content;

                var response = await _httpClient.SendAsync(httpRequestMessage);

                RaiseForStatus(response);

                return response;
            }
            catch (GarminConnectRequestException ex)
            {
                exception = ex;
                if (ex.Status == HttpStatusCode.Forbidden)
                {
                    await Task.Delay(DelayAfterFailAuth);
                    force = true;
                    continue;
                }

                Debug.WriteLine(ex.Message);
                throw;
            }
        }

        throw new GarminConnectAuthenticationException($"Authentication fail after {Attempts} attempts", exception);
    }

    private async Task<(string authUrl, string cookies)> GetAuthCookies()
    {
        var queryParams = _authParameters.GetQueryParameters();
        var formParams = _authParameters.GetFormParameters();
        var headers = _authParameters.GetHeaders();

        var queryString = HttpUtility.ParseQueryString("");
        foreach (var (key, value) in queryParams)
        {
            queryString.Add(key, value);
        }

        var signinUrl = $"{_authParameters.SigninUrl}?{queryString}";

        var httpRequestMessage = new HttpRequestMessage(HttpMethod.Get, signinUrl);
        foreach (var (key, value) in headers)
        {
            httpRequestMessage.Headers.Add(key, value);
        }

        var responseMessage = await _httpClient.SendAsync(httpRequestMessage);
        RaiseForStatus(responseMessage);

        var htmlAuth = await responseMessage.Content.ReadAsStringAsync();
        var csrf = _csrfRegex.Match(htmlAuth).Groups[1].Value;

        httpRequestMessage = new HttpRequestMessage(HttpMethod.Post, signinUrl);
        foreach (var (key, value) in headers)
        {
            httpRequestMessage.Headers.Add(key, value);
        }

        httpRequestMessage.Content = new FormUrlEncodedContent(new Dictionary<string, string>(formParams) { {"_csrf",csrf} });
        var response = await _httpClient.SendAsync(httpRequestMessage);
        RaiseForStatus(response);

        var html = await response.Content.ReadAsStringAsync();

        var responseUrlMatch = _responseUrlRegex.Match(html);
        if (!responseUrlMatch.Success)
        {
            throw new GarminConnectAuthenticationException();
        }

        var responseUrl = responseUrlMatch.Groups[1].Value.Replace("\\", string.Empty);

        var cookies = response.Headers.SingleOrDefault(header => header.Key == "Set-Cookie").Value;
        var sb = new StringBuilder();
        foreach (var cookie in cookies)
        {
            sb.Append($"{cookie};");
        }

        return (responseUrl, sb.ToString());
    }

    private async Task<string> GetBearerToken()
    {
        if (DateTime.Now < _tokenExpire)
        {
            return _tokenCached;
        }

        var httpRequestMessage = new HttpRequestMessage(HttpMethod.Post, _authParameters.ExchangeUrl);
        httpRequestMessage.Headers.Add("cookie", _authParameters.Cookies);

        var response = await _httpClient.SendAsync(httpRequestMessage);

        RaiseForStatus(response);

        var bearerToken = GarminSerializer.To<BearerToken>(await response.Content.ReadAsByteArrayAsync());

        _tokenCached = bearerToken.AccessToken;
        _tokenExpire = DateTime.Now.AddSeconds(bearerToken.ExpireIn - 60);

        return _tokenCached;
    }
    
    private async Task<(string cookies, GarminUserPreferences preferences, GarminSocialProfile profile)> Login()
    {
        var (authUrl, cookies) = await GetAuthCookies();

        var httpRequestMessage = new HttpRequestMessage(HttpMethod.Get, authUrl);

        httpRequestMessage.Headers.Add("Cookie", cookies);
        var response = await _httpClient.SendAsync(httpRequestMessage);

        RaiseForStatus(response);

        var html = await response.Content.ReadAsStringAsync();

        var userPreferences = ParseJson<GarminUserPreferences>(html, "VIEWER_USERPREFERENCES");
        var socialProfile = ParseJson<GarminSocialProfile>(html, "VIEWER_SOCIAL_PROFILE");

        return (cookies, userPreferences, socialProfile);
    }

    private static void RaiseForStatus(HttpResponseMessage response)
    {
        switch (response.StatusCode)
        {
            case HttpStatusCode.TooManyRequests:
                throw new GarminConnectTooManyRequestsException();
            case HttpStatusCode.NoContent:
            case HttpStatusCode.OK:
                return;
            default:
            {
                var message = $"{response.RequestMessage?.Method.Method}: {response.RequestMessage?.RequestUri}";
                throw new GarminConnectRequestException(message, response.StatusCode);
            }
        }
    }

    private static TModel ParseJson<TModel>(string html, string key)
    {
        var dataRegex = new Regex($@"window\.{key} = (.*);", RegexOptions.Compiled);
        var dataMatch = dataRegex.Match(html);

        if (dataMatch.Success)
        {
            var json = dataMatch.Groups[1].Value.Replace("\\\"", "\"");
            var model = JsonSerializer.Deserialize<TModel>(json);
            if (model != null)
            {
                return model;
            }
        }

        throw new GarminConnectUnexpectedException(key);
    }
}