window.orionPrintBalanza = () => {
  const root =
    document.getElementById("balanza-comprobacion-print-root") ||
    document.getElementById("balanza-comprobacion-table");

  if (!root) {
    console.warn("No se encontró el contenido de Balanza para imprimir.");
    return;
  }

  const iframe = document.createElement("iframe");
  iframe.style.position = "fixed";
  iframe.style.right = "0";
  iframe.style.bottom = "0";
  iframe.style.width = "0";
  iframe.style.height = "0";
  iframe.style.border = "0";
  iframe.setAttribute("aria-hidden", "true");

  document.body.appendChild(iframe);

  const styles = `
    <style>
      @page { margin: 12mm; }

      html, body {
        font-family: Arial, sans-serif;
        font-size: 11px;
        color: #111;
      }

      table {
        width: 100%;
        border-collapse: collapse;
      }

      th, td {
        border: 1px solid #d0d0d0;
        padding: 4px 6px;
        vertical-align: top;
      }

      thead { display: table-header-group; }
      tfoot { display: table-footer-group; }

      tr {
        break-inside: avoid;
        page-break-inside: avoid;
      }

      /* Base cell padding */
      .balanza-table td {
        padding-right: .5rem;
      }

      /* Indentación */
      .balanza-table td.balanza-indent-1 { padding-left: .25rem; }
      .balanza-table td.balanza-indent-2 { padding-left: 1.25rem; }
      .balanza-table td.balanza-indent-3 { padding-left: 2.25rem; }

      /* Peso visual por jerarquía */
      .level-1 td {
        font-weight: 700;
        background: #f8f9fa;
      }

      .level-2 td {
        font-weight: 600;
        background: #fcfcfd;
      }

      .level-3 td {
        font-weight: 400;
      }

      .balanza-table th,
      .balanza-table td {
        white-space: nowrap;
      }

      @media print {
        .no-print { display: none !important; }

        .table-responsive {
          overflow: visible !important;
        }

        table {
          width: 100% !important;
        }

        th, td {
          font-size: 11px;
        }
      }
    </style>
  `;

  const html = `
    <!doctype html>
    <html>
      <head>
        <meta charset="utf-8" />
        <title>Balanza de Comprobación</title>
        ${styles}
      </head>
      <body>
        <div id="balanza-print">
          ${root.outerHTML}
        </div>
      </body>
    </html>
  `;

  iframe.onload = () => {
    try {
      iframe.contentWindow.focus();
      iframe.contentWindow.print();
    } finally {
      // Cleanup shortly after opening the print dialog
      setTimeout(() => {
        if (iframe.parentNode) iframe.parentNode.removeChild(iframe);
      }, 200);
    }
  };

  // Use srcdoc for reliable load
  iframe.srcdoc = html;
};
