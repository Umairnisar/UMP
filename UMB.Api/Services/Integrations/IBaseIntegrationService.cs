using System.Collections.Generic;
using System.Threading.Tasks;
using UMB.Model.Models;

namespace UMB.Api.Services.Integrations
{
    public interface IBaseIntegrationService
    {
        Task<List<PlatformAccount>> GetUserPlatformsAsync(int userId, string platformType = null, string accountIdentifier = null);
        Task<List<WhatsAppConnection>> GetUserWhatsAppAccountsAsync(int userId, string phoneNumber = null);
        Task<bool> RemovePlatformIntegration(int userId, string platformType, string accountIdentifier);
        Task<bool> IsPlatformConnectedAsync(int userId, string platformType, string accountIdentifier);
    }
}