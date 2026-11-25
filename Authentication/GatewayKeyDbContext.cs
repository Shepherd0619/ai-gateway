using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace AiGateway.Authentication;

internal sealed class GatewayApiKey
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    [System.Text.Json.Serialization.JsonIgnore]
    public byte[] TokenHash { get; set; } = [];
    public DateTime CreatedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
    public string? LastUsedIp { get; set; }
}

internal sealed class GatewayKeyDbContext(DbContextOptions<GatewayKeyDbContext> options) : DbContext(options)
{
    public DbSet<GatewayApiKey> Keys => Set<GatewayApiKey>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        var utc = new ValueConverter<DateTime, DateTime>(v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
        var entity = builder.Entity<GatewayApiKey>();
        entity.ToTable("gateway_api_keys");
        entity.HasKey(k => k.Id);
        entity.Property(k => k.Id).HasColumnName("id");
        entity.Property(k => k.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
        entity.Property(k => k.TokenHash).HasColumnName("token_hash").HasMaxLength(32).IsRequired();
        entity.Property(k => k.CreatedAt).HasColumnName("created_at").HasConversion(utc);
        entity.Property(k => k.ExpiresAt).HasColumnName("expires_at").HasConversion(utc);
        entity.Property(k => k.RevokedAt).HasColumnName("revoked_at").HasConversion(utc);
        entity.Property(k => k.LastUsedAt).HasColumnName("last_used_at").HasConversion(utc);
        entity.Property(k => k.LastUsedIp).HasColumnName("last_used_ip").HasMaxLength(45);
        entity.HasIndex(k => new { k.CreatedAt, k.Id });
    }
}
