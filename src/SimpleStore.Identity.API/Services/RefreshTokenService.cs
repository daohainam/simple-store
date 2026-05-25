using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SimpleStore.Identity.API.Data;
using SimpleStore.Identity.API.Models;

namespace SimpleStore.Identity.API.Services;

public class RefreshTokenService : IRefreshTokenService
{
    private readonly IdentityDbContext _db;
    private readonly JwtOptions _options;

    public RefreshTokenService(IdentityDbContext db, IOptions<JwtOptions> options)
    {
        _db = db;
        _options = options.Value;
    }

    public async Task<(string RawToken, DateTime ExpiresAt)> IssueAsync(string userId, CancellationToken cancellationToken = default)
    {
        var raw = GenerateRaw();
        var hash = Hash(raw);
        var expiresAt = DateTime.UtcNow.AddDays(_options.RefreshTokenDays);

        _db.RefreshTokens.Add(new RefreshToken
        {
            UserId = userId,
            TokenHash = hash,
            ExpiresAt = expiresAt
        });
        await _db.SaveChangesAsync(cancellationToken);

        return (raw, expiresAt);
    }

    public async Task<RefreshToken?> ValidateAsync(string rawToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rawToken)) return null;
        var hash = Hash(rawToken);
        var entity = await _db.RefreshTokens.SingleOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);
        return entity is { IsActive: true } ? entity : null;
    }

    public async Task<(string RawToken, DateTime ExpiresAt)?> RotateAsync(string rawToken, CancellationToken cancellationToken = default)
    {
        var existing = await ValidateAsync(rawToken, cancellationToken);
        if (existing is null) return null;

        var (newRaw, newExp) = (GenerateRaw(), DateTime.UtcNow.AddDays(_options.RefreshTokenDays));
        var newHash = Hash(newRaw);

        existing.RevokedAt = DateTime.UtcNow;
        existing.ReplacedByTokenHash = newHash;

        _db.RefreshTokens.Add(new RefreshToken
        {
            UserId = existing.UserId,
            TokenHash = newHash,
            ExpiresAt = newExp
        });
        await _db.SaveChangesAsync(cancellationToken);

        return (newRaw, newExp);
    }

    public async Task RevokeAsync(string rawToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rawToken)) return;
        var hash = Hash(rawToken);
        var entity = await _db.RefreshTokens.SingleOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);
        if (entity is null || entity.RevokedAt is not null) return;
        entity.RevokedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
    }

    private static string GenerateRaw()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static string Hash(string raw)
    {
        var bytes = Encoding.UTF8.GetBytes(raw);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }
}
