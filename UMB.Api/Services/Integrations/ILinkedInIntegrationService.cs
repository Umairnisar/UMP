using System.Collections.Generic;
using System.Threading.Tasks;
using UMB.Model.Models;

namespace UMB.Api.Services.Integrations
{
    public interface ILinkedInIntegrationService
    {
        string GetAuthorizationUrl(int userId, string accountIdentifier);
        Task ExchangeCodeForTokenAsync(int userId, string code, string accountIdentifier);
        Task<List<MessageMetadata>> FetchMessagesAsync(int userId, string accountIdentifier = null);
        Task<string> SendMessageAsync(int userId, string recipientUrn, string message, string accountIdentifier);
    }
}