using System;

namespace OrionERP.Web.Features.Cfdi.DeclaracionPrevia.Pages
{
  public partial class DeclaracionPrevia
  {
    private void ResetPagination()
    {
      emitidasCurrentPage = 1;
      emitidasPageCount = emitidas != null ? Math.Max(1, (int)Math.Ceiling(emitidas.Count / (double)pageSize)) : 1;
      recibidasCurrentPage = 1;
      recibidasPageCount = recibidas != null ? Math.Max(1, (int)Math.Ceiling(recibidas.Count / (double)pageSize)) : 1;
    }

    private void NextEmitidasPage()
    {
      if (emitidasCurrentPage < emitidasPageCount)
      {
        emitidasCurrentPage++;
      }
    }

    private void PreviousEmitidasPage()
    {
      if (emitidasCurrentPage > 1)
      {
        emitidasCurrentPage--;
      }
    }

    private void NextRecibidasPage()
    {
      if (recibidasCurrentPage < recibidasPageCount)
      {
        recibidasCurrentPage++;
      }
    }

    private void PreviousRecibidasPage()
    {
      if (recibidasCurrentPage > 1)
      {
        recibidasCurrentPage--;
      }
    }
  }
}
