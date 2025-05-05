using Microsoft.EntityFrameworkCore;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UMB.Model.Models;

namespace UMB.Api.Services.Integrations
{
    public class BaseIntegrationService : IBaseIntegrationService
    {
        private readonly AppDbContext _appDbContext;

        public BaseIntegrationService(AppDbContext appDbContext)
        {
            _appDbContext = appDbContext;
        }

        public async Task<List<PlatformAccount>> GetUserPlatformsAsync(int userId, string platformType = null, string accountIdentifier = null)
        {
            var query = _appDbContext.PlatformAccounts
                .Where(pa => pa.UserId == userId);

            if (!string.IsNullOrEmpty(platformType))
            {
                query = query.Where(pa => pa.PlatformType == platformType);
            }

            if (!string.IsNullOrEmpty(accountIdentifier))
            {
                query = query.Where(pa => pa.AccountIdentifier == accountIdentifier);
            }

            return await query.ToListAsync();
        }

        public async Task<List<WhatsAppConnection>> GetUserWhatsAppAccountsAsync(int userId, string phoneNumber = null)
        {
            var query = _appDbContext.WhatsAppConnections
                .Where(wc => wc.UserId == userId && wc.IsConnected);

            if (!string.IsNullOrEmpty(phoneNumber))
            {
                query = query.Where(wc => wc.PhoneNumber == phoneNumber);
            }

            return await query.ToListAsync();
        }

        public async Task<bool> RemovePlatformIntegration(int userId, string platformType, string accountIdentifier)
        {
            if (platformType.Equals("WhatsApp", StringComparison.OrdinalIgnoreCase))
            {
                var connection = await _appDbContext.WhatsAppConnections
                    .FirstOrDefaultAsync(wc => wc.UserId == userId && wc.PhoneNumber == accountIdentifier && wc.IsConnected);

                if (connection == null)
                {
                    return false;
                }

                connection.IsConnected = false;
                connection.UpdatedAt = DateTime.UtcNow;
                await _appDbContext.SaveChangesAsync();
                return true;
            }

            var account = await _appDbContext.PlatformAccounts
                .FirstOrDefaultAsync(pa => pa.UserId == userId && pa.PlatformType == platformType && pa.AccountIdentifier == accountIdentifier);

            if (account == null)
            {
                return false;
            }

            _appDbContext.PlatformAccounts.Remove(account);
            await _appDbContext.SaveChangesAsync();
            return true;
        }

        public async Task<bool> IsPlatformConnectedAsync(int userId, string platformType, string accountIdentifier)
        {
            if (platformType.Equals("WhatsApp", StringComparison.OrdinalIgnoreCase))
            {
                return await _appDbContext.WhatsAppConnections
                    .AnyAsync(wc => wc.UserId == userId && wc.PhoneNumber == accountIdentifier && wc.IsConnected);
            }

            return await _appDbContext.PlatformAccounts
                .AnyAsync(pa => pa.UserId == userId && pa.PlatformType == platformType && pa.AccountIdentifier == accountIdentifier);
        }
    }
}