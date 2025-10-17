using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Text;
using System.Text.Json;
using System.Net.Http;
using System.Net.Http.Headers;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using OfficeOpenXml;

namespace OrionERP.Web.Features.Cfdi.DeclaracionPrevia.Pages
{
  public partial class DeclaracionPrevia : ComponentBase
  {
    // Keep this too if you use it elsewhere:
    // Data models corresponding to stored procedure outputs:
    [Inject] private NavigationManager Nav { get; set; } = default!;
  }
}
