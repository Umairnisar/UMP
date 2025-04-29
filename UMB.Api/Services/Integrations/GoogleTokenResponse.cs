namespace UMB.Api.Services.Integrations
{
    public class GoogleTokenResponse
    {
        public string access_token { get; set; }
        public string refresh_token { get; set; }
        public int expires_in { get; set; }
        // add more fields if needed
    }

}
