using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace OrionERP.Infrastructure.Auth
{
    public class OrionIdentityDbContext
        : IdentityDbContext<ApplicationUser, IdentityRole, string>
    {
        public OrionIdentityDbContext(DbContextOptions<OrionIdentityDbContext> options)
            : base(options)
        {
        }

        protected override void OnModelCreating(ModelBuilder b)
        {
            base.OnModelCreating(b);
            b.HasDefaultSchema("auth");
        }
    }
}
