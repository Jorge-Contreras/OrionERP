using Microsoft.AspNetCore.Identity;

namespace OrionERP.Infrastructure.Auth
{
    public class ApplicationUser : IdentityUser
    {
        public int? EmployeeId { get; set; }
    }
}
