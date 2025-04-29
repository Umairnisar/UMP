
using Microsoft.EntityFrameworkCore;
using UMB.Model.Models;

namespace UMB.Api.Services.Integrations
{
    public class BaseIntegrationService
    {
        private readonly AppDbContext _appDbContext;
        public BaseIntegrationService(AppDbContext appDbContext)
        {
            _appDbContext = appDbContext;
        }
        public async Task<List<PlatformAccount>> GetUserPlatformsAsync(int userId)
        {
            return await _appDbContext.PlatformAccounts
                .Where(pa => pa.UserId == userId)
                .ToListAsync();
        }

        public async Task<bool> RemovePlatformIntegration(int userId, string platform)
        {
            var account = await _appDbContext.PlatformAccounts
                .FirstOrDefaultAsync(pa => pa.UserId == userId && pa.PlatformType == platform);

            if (account == null)
            {
                return false;
            }

            _appDbContext.PlatformAccounts.Remove(account);
            await _appDbContext.SaveChangesAsync();

            return true;
        }
    }
}
