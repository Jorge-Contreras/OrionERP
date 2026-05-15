using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrionERP.Infrastructure.Auth.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(OrionIdentityDbContext))]
    [Migration("20260514120000_AddArrendadorProveedorLink")]
    public partial class AddArrendadorProveedorLink : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ArrendadorProveedorId",
                schema: "auth",
                table: "AspNetUsers",
                type: "int",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUsers_ArrendadorProveedorId",
                schema: "auth",
                table: "AspNetUsers",
                column: "ArrendadorProveedorId",
                unique: true,
                filter: "[ArrendadorProveedorId] IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_AspNetUsers_Proveedores_ArrendadorProveedorId",
                schema: "auth",
                table: "AspNetUsers",
                column: "ArrendadorProveedorId",
                principalSchema: "dbo",
                principalTable: "Proveedores",
                principalColumn: "id",
                onDelete: ReferentialAction.NoAction);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AspNetUsers_Proveedores_ArrendadorProveedorId",
                schema: "auth",
                table: "AspNetUsers");

            migrationBuilder.DropIndex(
                name: "IX_AspNetUsers_ArrendadorProveedorId",
                schema: "auth",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "ArrendadorProveedorId",
                schema: "auth",
                table: "AspNetUsers");
        }
    }
}
