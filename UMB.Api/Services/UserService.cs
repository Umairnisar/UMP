using UMB.Model;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;
using UMB.Model.Models;

namespace UMB.Api.Services
{
    public interface IUserService
    {
        Task<User> RegisterAsync(string email, string password, string fullName);
        Task<User> ValidateUserAsync(string email, string password);
        // Additional methods (e.g., get user by id, etc.)
    }

    public class UserService : IUserService
    {
        private readonly AppDbContext _dbContext;

        public UserService(AppDbContext dbContext)
        {
            _dbContext = dbContext;
        }

        public async Task<User> RegisterAsync(string email, string password, string username)
        {
            var existingUser = await _dbContext.Users.FirstOrDefaultAsync(u => u.Email == email);
            if (existingUser != null) return null;

            var user = new User
            {
                Email = email,
                PasswordHash = HashPassword(password),
                UserName = username,
                CreatedAt = DateTime.UtcNow
            };

            _dbContext.Users.Add(user);
            await _dbContext.SaveChangesAsync();
            return user;
        }

        public async Task<User> ValidateUserAsync(string email, string password)
        {
            var user = await _dbContext.Users.FirstOrDefaultAsync(u => u.Email == email);
            if (user == null) return null;

            var hash = HashPassword(password);
            if (user.PasswordHash == hash)
            {
                return user;
            }
            return null;
        }

        private string HashPassword(string password)
        {
            using var sha256 = SHA256.Create();
            var hashedBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(password));
            return BitConverter.ToString(hashedBytes).Replace("-", "").ToLower();
        }
    }
}
