using System.Net;
using YouTubeVideoPlayer.Tests.TestSupport;
using YouTubeVideoPlayer.Twitch.Auth;

namespace YouTubeVideoPlayer.Tests.Twitch.Auth;

public class TwitchAuthClientTests
{
    private static (TwitchAuthClient Client, FakeHttpHandler Http) Create(HttpStatusCode status, string json)
    {
        var http = new FakeHttpHandler { Respond = _ => (status, json) };
        return (new TwitchAuthClient("my-client", http), http);
    }

    private static Dictionary<string, string> Form(string body) => body.Split('&')
        .Select(pair => pair.Split('='))
        .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1].Replace('+', ' ')));

    #region Device code flow

    [Fact]
    public async Task StartDeviceFlow_PostsClientIdAndScopes_AndParsesTheCode()
    {
        var (client, http) = Create(HttpStatusCode.OK,
            "{\"device_code\":\"dev\",\"user_code\":\"ABCD-1234\",\"verification_uri\":\"https://www.twitch.tv/activate?x\",\"expires_in\":1800,\"interval\":5}");

        var device = await client.StartDeviceFlowAsync(CancellationToken.None);

        Assert.Equal(new DeviceCodeInfo("dev", "ABCD-1234", "https://www.twitch.tv/activate?x", TimeSpan.FromMinutes(30), TimeSpan.FromSeconds(5)), device);
        var request = Assert.Single(http.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/oauth2/device", request.Path);
        Assert.Equal(new Dictionary<string, string> { ["client_id"] = "my-client", ["scopes"] = "chat:read chat:edit" }, Form(request.Body));
    }

    [Fact]
    public async Task StartDeviceFlow_UsesDefaultsForMissingFields()
    {
        var (client, _) = Create(HttpStatusCode.OK, "{\"device_code\":\"dev\",\"interval\":0}");

        var device = await client.StartDeviceFlowAsync(CancellationToken.None);

        Assert.Equal("https://www.twitch.tv/activate", device.VerificationUri);
        Assert.Equal(string.Empty, device.UserCode);
        Assert.Equal(TimeSpan.FromMinutes(30), device.ExpiresIn);
        Assert.Equal(TimeSpan.FromSeconds(1), device.Interval); // never poll faster than once a second
    }

    [Fact]
    public async Task StartDeviceFlow_WithoutDeviceCode_Throws()
    {
        var (client, _) = Create(HttpStatusCode.OK, "{}");

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.StartDeviceFlowAsync(CancellationToken.None));
    }

    [Fact]
    public async Task StartDeviceFlow_WhenTwitchRefuses_ExplainsLikelyCauses()
    {
        var (client, _) = Create(HttpStatusCode.BadRequest, "{\"status\":400,\"message\":\"invalid client\"}");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.StartDeviceFlowAsync(CancellationToken.None));

        Assert.Contains("invalid client", ex.Message);
        Assert.Contains("Public", ex.Message);
    }

    [Theory]
    [InlineData("authorization_pending", DevicePollStatus.Pending)]
    [InlineData("slow_down", DevicePollStatus.SlowDown)]
    [InlineData("access_denied", DevicePollStatus.Denied)]
    [InlineData("invalid device code", DevicePollStatus.Expired)]
    [InlineData("expired_token", DevicePollStatus.Expired)]
    public async Task PollDeviceToken_MapsTwitchErrors(string message, DevicePollStatus expected)
    {
        var (client, _) = Create(HttpStatusCode.BadRequest, $"{{\"status\":400,\"message\":\"{message}\"}}");

        var result = await client.PollDeviceTokenAsync("dev", CancellationToken.None);

        Assert.Equal(expected, result.Status);
        Assert.Null(result.Token);
    }

    [Fact]
    public async Task PollDeviceToken_ReturnsTheTokens_AndSendsTheDeviceGrant()
    {
        var (client, http) = Create(HttpStatusCode.OK,
            "{\"access_token\":\"acc\",\"refresh_token\":\"ref\",\"expires_in\":14400,\"scope\":[\"chat:read\",\"chat:edit\"],\"token_type\":\"bearer\"}");

        var result = await client.PollDeviceTokenAsync("dev", CancellationToken.None);

        Assert.Equal(DevicePollStatus.Success, result.Status);
        Assert.Equal("acc", result.Token!.AccessToken);
        Assert.Equal("ref", result.Token.RefreshToken);
        Assert.Equal(TimeSpan.FromHours(4), result.Token.ExpiresIn);
        Assert.Equal(new[] { "chat:read", "chat:edit" }, result.Token.Scopes);
        var form = Form(http.Requests.Single().Body);
        Assert.Equal("urn:ietf:params:oauth:grant-type:device_code", form["grant_type"]);
        Assert.Equal("dev", form["device_code"]);
        Assert.Equal("my-client", form["client_id"]);
        Assert.Equal("/oauth2/token", http.Requests.Single().Path);
    }

    [Fact]
    public async Task PollDeviceToken_UnknownError_Throws()
    {
        var (client, _) = Create(HttpStatusCode.InternalServerError, "{\"message\":\"server on fire\"}");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.PollDeviceTokenAsync("dev", CancellationToken.None));

        Assert.Contains("server on fire", ex.Message);
    }

    #endregion

    #region Refresh

    [Fact]
    public async Task Refresh_SendsTheRefreshTokenWithoutClientSecret()
    {
        var (client, http) = Create(HttpStatusCode.OK, "{\"access_token\":\"a2\",\"refresh_token\":\"r2\",\"expires_in\":14000}");

        var token = await client.RefreshAsync("r1", CancellationToken.None);

        Assert.Equal(new[] { "a2", "r2" }, new[] { token.AccessToken, token.RefreshToken });
        Assert.Empty(token.Scopes);
        Assert.Equal(new Dictionary<string, string>
        {
            ["client_id"] = "my-client",
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = "r1"
        }, Form(http.Requests.Single().Body));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Refresh_RejectedByTwitch_ThrowsAuthRejected(HttpStatusCode status)
    {
        var (client, _) = Create(status, "{\"status\":400,\"message\":\"Invalid refresh token\"}");

        var ex = await Assert.ThrowsAsync<TwitchAuthRejectedException>(() => client.RefreshAsync("old", CancellationToken.None));

        Assert.Contains("Invalid refresh token", ex.Message);
    }

    [Fact]
    public async Task Refresh_ServerError_IsATransientHttpError()
    {
        var (client, _) = Create(HttpStatusCode.ServiceUnavailable, "maintenance");

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.RefreshAsync("r", CancellationToken.None));

        Assert.Contains("503", ex.Message);
        Assert.Contains("maintenance", ex.Message); // non-JSON bodies are reported as the message
    }

    [Fact]
    public async Task Refresh_WithoutTokensInTheAnswer_Throws()
    {
        var (client, _) = Create(HttpStatusCode.OK, "{\"access_token\":\"a\"}");

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.RefreshAsync("r", CancellationToken.None));
    }

    #endregion

    #region Validate

    [Fact]
    public async Task Validate_SendsTheTokenAsOAuthHeader_AndParsesTheAccount()
    {
        var (client, http) = Create(HttpStatusCode.OK,
            "{\"client_id\":\"my-client\",\"login\":\"lobosbot\",\"user_id\":\"4711\",\"scopes\":[\"chat:read\"],\"expires_in\":3600}");

        var validation = await client.ValidateAsync("acc", CancellationToken.None);

        Assert.Equal("lobosbot", validation!.Login);
        Assert.Equal("4711", validation.UserId);
        Assert.Equal(TimeSpan.FromHours(1), validation.ExpiresIn);
        Assert.Equal(new[] { "chat:read" }, validation.Scopes);
        var request = http.Requests.Single();
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/oauth2/validate", request.Path);
        Assert.Equal("OAuth acc", request.Authorization);
    }

    [Fact]
    public async Task Validate_InvalidToken_ReturnsNull()
    {
        var (client, _) = Create(HttpStatusCode.Unauthorized, "{\"status\":401,\"message\":\"invalid access token\"}");

        Assert.Null(await client.ValidateAsync("acc", CancellationToken.None));
    }

    [Fact]
    public async Task Validate_ServerError_Throws()
    {
        var (client, _) = Create(HttpStatusCode.BadGateway, "");

        await Assert.ThrowsAsync<HttpRequestException>(() => client.ValidateAsync("acc", CancellationToken.None));
    }

    [Fact]
    public async Task Validate_WithMissingFields_UsesEmptyValues()
    {
        var (client, _) = Create(HttpStatusCode.OK, "{}");

        var validation = (await client.ValidateAsync("acc", CancellationToken.None))!;

        Assert.Equal(string.Empty, validation.Login);
        Assert.Equal(string.Empty, validation.UserId);
        Assert.Equal(TimeSpan.Zero, validation.ExpiresIn);
        Assert.Empty(validation.Scopes);
    }

    #endregion

    [Fact]
    public void Dispose_CanBeCalled()
    {
        var (client, _) = Create(HttpStatusCode.OK, "{}");

        client.Dispose();
    }

    [Fact]
    public void DefaultConstructor_CreatesItsOwnHttpClient()
    {
        using var client = new TwitchAuthClient("id");

        Assert.Equal("chat:read chat:edit", TwitchAuthClient.Scopes);
    }
}
