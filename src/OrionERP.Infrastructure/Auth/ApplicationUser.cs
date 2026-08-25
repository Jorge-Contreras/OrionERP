using Microsoft.AspNetCore.Identity;

namespace OrionERP.Infrastructure.Auth
{
    public class ApplicationUser : IdentityUser
    {
        /// <summary>
        /// Legacy single-employment link retained during the company-membership
        /// rollback window. New authorization code must use UserCompany.EmployeeId.
        /// </summary>
        public int? EmployeeId { get; set; }
        public int? ArrendadorProveedorId { get; set; }

        public ICollection<UserCompany> Companies { get; set; } = new List<UserCompany>();
    }
}
