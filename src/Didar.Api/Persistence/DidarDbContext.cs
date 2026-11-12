using Microsoft.EntityFrameworkCore;

namespace Didar.Api.Persistence;

public sealed class DidarDbContext(DbContextOptions<DidarDbContext> options) : DbContext(options)
{
    public DbSet<Request> Requests => Set<Request>();
    public DbSet<Specialty> Specialties => Set<Specialty>();
    public DbSet<Expert> Experts => Set<Expert>();
    public DbSet<ExpertSpecialty> ExpertSpecialties => Set<ExpertSpecialty>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasCollation("C", "C", "libc", deterministic: true);
        modelBuilder.Entity<Specialty>(entity =>
        {
            entity.ToTable("specialties");
            entity.HasKey(x => x.SpecialtyId);
            entity.Property(x => x.SpecialtyId).HasColumnName("specialty_id");
        });
        modelBuilder.Entity<Expert>(entity =>
        {
            entity.ToTable("experts", table =>
            {
                table.HasCheckConstraint("ck_experts_status", "status IN ('Active', 'PendingDeactivation', 'Inactive')");
                table.HasCheckConstraint("ck_experts_capacity", "capacity >= 0");
            });
            entity.HasKey(x => x.ExpertId);
            entity.Property(x => x.ExpertId).HasColumnName("expert_id");
            entity.Property(x => x.Status).HasColumnName("status").IsRequired();
            entity.Property(x => x.Capacity).HasColumnName("capacity").IsRequired();
            entity.Property(x => x.LastAssignedAt).HasColumnName("last_assigned_at").HasColumnType("timestamp with time zone");
        });
        modelBuilder.Entity<ExpertSpecialty>(entity =>
        {
            entity.ToTable("expert_specialties", table =>
            {
                table.HasCheckConstraint("ck_expert_specialties_status", "status IN ('Active', 'PendingDeactivation', 'Inactive')");
            });
            entity.HasKey(x => new { x.ExpertId, x.SpecialtyId });
            entity.Property(x => x.ExpertId).HasColumnName("expert_id");
            entity.Property(x => x.SpecialtyId).HasColumnName("specialty_id");
            entity.Property(x => x.Status).HasColumnName("status").IsRequired();
            entity.HasIndex(x => x.SpecialtyId);
            entity.HasOne<Expert>().WithMany().HasForeignKey(x => x.ExpertId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Specialty>().WithMany().HasForeignKey(x => x.SpecialtyId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<Request>(entity =>
        {
            entity.ToTable("requests", table =>
            {
                table.HasCheckConstraint("ck_requests_request_key_length", "octet_length(request_key) BETWEEN 1 AND 1024");
                table.HasCheckConstraint("ck_requests_priority", "priority IN ('Normal', 'Urgent')");
                table.HasCheckConstraint("ck_requests_status", "status IN ('Pending', 'Assigned', 'Closed', 'Cancelled')");
                table.HasCheckConstraint("ck_requests_pending_expert", "status <> 'Pending' OR expert_id IS NULL");
                table.HasCheckConstraint("ck_requests_assigned_expert", "status <> 'Assigned' OR expert_id IS NOT NULL");
            });
            entity.HasKey(x => x.RequestId);
            entity.Property(x => x.RequestId).HasColumnName("request_id");
            entity.Property(x => x.RequestKey).HasColumnName("request_key").HasColumnType("text COLLATE \"C\"").IsRequired();
            entity.HasIndex(x => x.RequestKey).IsUnique();
            entity.Property(x => x.SpecialtyId).HasColumnName("specialty_id").IsRequired();
            entity.HasOne<Specialty>().WithMany().HasForeignKey(x => x.SpecialtyId).OnDelete(DeleteBehavior.Restrict);
            entity.Property(x => x.Priority).HasColumnName("priority").IsRequired();
            entity.Property(x => x.Status).HasColumnName("status").IsRequired();
            entity.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone").IsRequired();
            entity.Property(x => x.ExpertId).HasColumnName("expert_id");
            entity.HasOne<Expert>().WithMany().HasForeignKey(x => x.ExpertId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => new { x.SpecialtyId, x.CreatedAt, x.RequestId });
            entity.HasIndex(x => x.ExpertId);
        });
    }
}
