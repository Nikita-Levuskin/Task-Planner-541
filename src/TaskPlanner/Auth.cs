using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace TaskPlanner;

public sealed class TokenService(byte[] key, int lifetimeSeconds)
{
    public const string Issuer = "TaskPlanner";
    public const string Audience = "TaskPlanner.Web";
    public TokenValidationParameters Validation => new()
    {
        ValidateIssuer = true, ValidIssuer = Issuer,
        ValidateAudience = true, ValidAudience = Audience,
        ValidateIssuerSigningKey = true, IssuerSigningKey = new SymmetricSecurityKey(key),
        ValidateLifetime = true, ClockSkew = TimeSpan.Zero,
        ValidAlgorithms = [SecurityAlgorithms.HmacSha256]
    };

    public object Issue(User user)
    {
        var expires = DateTime.UtcNow.AddSeconds(lifetimeSeconds);
        var token = new JwtSecurityToken(Issuer, Audience,
            [new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
             new Claim(JwtRegisteredClaimNames.Email, user.Email),
             new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())],
            expires: expires,
            signingCredentials: new SigningCredentials(new SymmetricSecurityKey(key),
                SecurityAlgorithms.HmacSha256));
        return new { accessToken = new JwtSecurityTokenHandler().WriteToken(token),
            expiresAt = expires, user = new { user.Id, user.Email } };
    }

    public static string NewRefresh() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public static void Cookie(HttpContext ctx, string value) => ctx.Response.Cookies.Append(
        "planner_refresh", value, new CookieOptions
        {
            HttpOnly = true, Secure = ctx.Request.IsHttps, SameSite = SameSiteMode.Strict,
            Path = "/api/auth", Expires = DateTimeOffset.UtcNow.AddDays(7), IsEssential = true
        });

    public static bool SameOrigin(HttpContext ctx)
    {
        if (ctx.Request.Headers["X-Planner-Request"] != "1") return false;
        var origin = ctx.Request.Headers.Origin.ToString();
        return origin.Length == 0 || origin == $"{ctx.Request.Scheme}://{ctx.Request.Host}";
    }

    public static int UserId(ClaimsPrincipal user) => int.Parse(user.FindFirstValue("sub")!);
}
