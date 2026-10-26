namespace EvolverCollect.Server.Auth;

using System.Security.Claims;
using EvolverCollect.Storage;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

/// <summary>控制台认证：账号校验 + Cookie 签发 + 首登强制改密。</summary>
public sealed class AuthService(ConfigRepository repo)
{
    public sealed record LoginResult(bool Success, string? Error = null, ClaimsPrincipal? Principal = null)
    {
        public bool MustChangePassword { get; set; }
    }

    public async Task<LoginResult> LoginAsync(string? userName, string? password)
    {
        if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrWhiteSpace(password))
            return new LoginResult(false, "用户名或密码不能为空");

        var user = await repo.GetUserAsync(userName);
        if (user is null || !PasswordHasher.Verify(password, user.PasswordHash))
            return new LoginResult(false, "用户名或密码错误");

        return new LoginResult(true, null, BuildPrincipal(user)) { MustChangePassword = user.MustChangePassword };
    }

    public static ClaimsPrincipal BuildPrincipal(UserEntity user)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, user.UserName),
            new(ClaimTypes.Role, user.Role),
        };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        return new ClaimsPrincipal(identity);
    }

    public async Task<bool> ChangePasswordAsync(string userName, string oldPassword, string newPassword)
    {
        var user = await repo.GetUserAsync(userName);
        if (user is null || !PasswordHasher.Verify(oldPassword, user.PasswordHash)) return false;
        if (string.IsNullOrWhiteSpace(newPassword) || newPassword.Length < 6) return false;
        return await repo.ChangePasswordAsync(user.Id, PasswordHasher.Hash(newPassword));
    }

    public static AuthenticationProperties CookieProperties() => new()
    {
        IsPersistent = true,
        ExpiresUtc = DateTimeOffset.UtcNow.AddDays(7),
    };
}
