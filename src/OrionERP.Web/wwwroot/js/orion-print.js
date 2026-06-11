window.orionPrintReport = (rootId, title, subtitle = "") => {
  const root = document.getElementById(rootId);

  if (!root) {
    console.warn(`No se encontró el contenido para imprimir: ${rootId}`);
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

      .print-header {
        margin-bottom: 12px;
      }

      .print-title {
        font-size: 18px;
        font-weight: 700;
        margin: 0;
      }

      .print-subtitle {
        font-size: 12px;
        color: #555;
        margin: 4px 0 0;
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

      .table-responsive {
        overflow: visible !important;
      }

      .balanza-table td {
        padding-right: .5rem;
      }

      .balanza-summary-grid {
        display: grid;
        grid-template-columns: repeat(4, minmax(0, 1fr));
        gap: 6px;
        margin-bottom: 10px;
      }

      .balanza-summary-card {
        border: 1px solid #d0d0d0;
        padding: 6px;
      }

      .balanza-summary-card span {
        display: block;
        color: #555;
        font-size: 9px;
        font-weight: 700;
        text-transform: uppercase;
      }

      .balanza-summary-card strong {
        display: block;
        margin-top: 2px;
        font-size: 11px;
      }

      .balanza-account-cell {
        display: block;
      }

      .balanza-level-badge {
        font-weight: 700;
        margin-right: 4px;
      }

      .balanza-tree-toggle,
      .balanza-tree-spacer,
      .balanza-open-button,
      .balanza-table__action-col {
        display: none !important;
      }

      .balanza-table td.balanza-indent-1 { padding-left: .25rem; }
      .balanza-table td.balanza-indent-2 { padding-left: 1.25rem; }
      .balanza-table td.balanza-indent-3 { padding-left: 2.25rem; }

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

      .text-end {
        text-align: right;
      }

      @media print {
        .no-print { display: none !important; }
        table { width: 100% !important; }
        th, td { font-size: 11px; }
      }
    </style>
  `;

  const html = `
    <!doctype html>
    <html>
      <head>
        <meta charset="utf-8" />
        <title>${title ?? "Reporte"}</title>
        ${styles}
      </head>
      <body>
        <div class="print-header">
          <p class="print-title">${title ?? ""}</p>
          ${subtitle ? `<p class="print-subtitle">${subtitle}</p>` : ""}
        </div>
        ${root.outerHTML}
      </body>
    </html>
  `;

  iframe.onload = () => {
    try {
      iframe.contentWindow.focus();
      iframe.contentWindow.print();
    } finally {
      setTimeout(() => {
        if (iframe.parentNode) iframe.parentNode.removeChild(iframe);
      }, 200);
    }
  };

  iframe.srcdoc = html;
};

window.orionPrintBalanza = () => {
  const root =
    document.getElementById("balanza-comprobacion-print-root") ||
    document.getElementById("balanza-comprobacion-table");

  if (!root) {
    console.warn("No se encontró el contenido de Balanza para imprimir.");
    return;
  }

  window.orionPrintReport(
    root.id,
    "Balanza de Comprobación"
  );
};
